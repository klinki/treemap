using System.Diagnostics;
using System.Text;
using Treemap;
using Xunit;

namespace Treemap.Performance.Tests;

/// <summary>
/// Guards the synthetic tree generator.
/// </summary>
/// <remarks>
/// <para>
/// This generator once contained an unbounded loop: for small <c>fileCount</c> values
/// the direct-file count truncated to zero, while subfolder shares were floored at one.
/// The recursion therefore produced folders forever without ever decrementing the
/// remaining budget, growing the node list until it exhausted system RAM.
/// </para>
///
/// <para>
/// The tests below are the regression net for that class of bug. They assert that
/// generation <b>terminates</b> and produces <b>exactly</b> the requested number of
/// files, and that concurrent builds stay isolated from one another.
/// </para>
///
/// <para>
/// There is no in-test hard timeout. An earlier version ran generation on a background
/// thread and asserted that <c>Task.Wait</c> returned, which proves nothing: the waiting
/// thread cannot stop the runaway build, and the test host still holds every node the
/// build allocated. Trees here are generated synchronously and kept small (1,000 files
/// is the largest case). Elapsed time is measured with a stopwatch and reported as a
/// signal, not enforced as a timeout. A runaway build is terminated by the external
/// bounded runner, which caps memory and kills the whole job at its deadline.
/// </para>
/// </remarks>
public class SyntheticTreeTests
{
    /// <summary>
    /// Reported budget for the small-tree cases. Not a timeout: nothing enforces it,
    /// it only makes a large regression obvious in the failure message.
    /// </summary>
    private static readonly TimeSpan SmallTreeBudget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(1_000)]
    public void GeneratesExactlyTheRequestedFileCount(int requested)
    {
        TreemapNode root = Generate(requested);

        int files = CountFiles(root);
        Assert.Equal(requested, files);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SmallTreesCompleteQuickly(int requested)
    {
        // The counts from the bug report: these are the values where the direct-file
        // percentage truncated to zero.
        Stopwatch clock = Stopwatch.StartNew();

        TreemapNode root = Generate(requested);

        clock.Stop();

        Assert.Equal(requested, CountFiles(root));
        Assert.True(clock.Elapsed < SmallTreeBudget,
            $"generation took {clock.ElapsedMilliseconds}ms, " +
            $"expected well under {SmallTreeBudget.TotalMilliseconds}ms");
    }

    [Fact]
    public void RootIsADriveWithChildren()
    {
        TreemapNode root = Generate(50);

        Assert.Equal(NodeKind.Drive, root.Kind);
        Assert.NotEmpty(root.Children);
    }

    [Fact]
    public void IdsAreUniqueWithinATree()
    {
        TreemapNode root = Generate(500);

        List<int> ids = Ids(root);

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void GenerationIsDeterministicForAGivenSeed()
    {
        // Same seed must produce the same shape and the same ids, otherwise benchmark
        // runs are not comparable across machines.
        TreemapNode first = Generate(200, seed: 4242);
        TreemapNode second = Generate(200, seed: 4242);

        Assert.Equal(Describe(first), Describe(second));

        Assert.Equal<int>(Ids(first), Ids(second));
    }

    /// <summary>
    /// Ids used to come from a static counter that <c>Build</c> reset on entry. Concurrent
    /// builds reset and advanced each other's counter, so ids repeated inside a single
    /// tree and the same seed no longer produced the same tree.
    /// </summary>
    [Fact]
    public void ConcurrentSameSeedBuildsStayIsolatedAndIdentical()
    {
        const int Builds = 4;
        const int Files = 100;

        TreemapNode[] trees = new TreemapNode[Builds];
        Parallel.For(0, Builds, i => trees[i] = SyntheticTree.Build(Files, seed: 4242));

        string expectedShape = Describe(trees[0]);
        List<int> expectedIds = Ids(trees[0]);

        Assert.Equal(expectedIds.Count, expectedIds.Distinct().Count());
        Assert.Equal(Files, CountFiles(trees[0]));

        for (int i = 1; i < Builds; i++)
        {
            List<int> ids = Ids(trees[i]);

            Assert.Equal(expectedIds.Count, ids.Distinct().Count());
            Assert.Equal<int>(expectedIds, ids);
            Assert.Equal(expectedShape, Describe(trees[i]));
            Assert.Equal(Files, CountFiles(trees[i]));
        }
    }

    [Fact]
    public void DifferentSeedsProduceDifferentShapes()
    {
        TreemapNode first = Generate(500, seed: 1);
        TreemapNode second = Generate(500, seed: 2);

        Assert.NotEqual(Describe(first), Describe(second));
    }

    [Fact]
    public void FileNodesHavePositiveSizes()
    {
        TreemapNode root = Generate(300);

        foreach (TreemapNode file in EnumerateFiles(root))
        {
            Assert.True(file.LayoutSize > 0, "benchmark files must have a positive size");
        }
    }

    [Fact]
    public void EveryTreeIncludesFreeSpace()
    {
        // A real volume always has free space, and it exercises the space-node path.
        TreemapNode root = Generate(100);

        Assert.Contains(root.Children, c => c.Kind == NodeKind.FreeSpace);
    }

    private static TreemapNode Generate(int fileCount, int seed = 12345)
        => SyntheticTree.Build(fileCount, seed);

    private static int CountFiles(TreemapNode root)
    {
        int count = 0;
        foreach (TreemapNode node in Enumerate(root))
        {
            if (node.Kind == NodeKind.File)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<TreemapNode> EnumerateFiles(TreemapNode root)
        => Enumerate(root).Where(n => n.Kind == NodeKind.File);

    /// <summary>Iterative walk: recursion depth is bounded but stack safety is free.</summary>
    private static IEnumerable<TreemapNode> Enumerate(TreemapNode root)
    {
        Stack<TreemapNode> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            TreemapNode node = stack.Pop();
            yield return node;

            foreach (TreemapNode child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static List<int> Ids(TreemapNode root) => [.. Enumerate(root).Select(n => n.Id)];

    private static string Describe(TreemapNode node)
    {
        StringBuilder sb = new();
        Walk(node, sb, depth: 0);
        return sb.ToString();
    }

    private static void Walk(TreemapNode node, StringBuilder sb, int depth)
    {
        sb.Append(node.Id).Append(':')
          .Append(node.Kind).Append(':')
          .Append(node.Name).Append(':')
          .Append(depth).Append(';');

        foreach (TreemapNode child in node.Children)
        {
            Walk(child, sb, depth + 1);
        }
    }
}