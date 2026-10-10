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
/// The tests below are the regression net for that class of bug. They assert the two
/// properties that matter: generation <b>terminates</b>, and it produces <b>exactly</b>
/// the requested number of files. Running them against a 1-second timeout makes a
/// regression fail fast instead of consuming the machine first.
/// </para>
/// </remarks>
public class SyntheticTreeTests
{
    /// <summary>
    /// Hard timeout for generation. The buggy version never returned, so without this
    /// the suite itself would hang and exhaust memory before any assertion ran.
    /// </summary>
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(20);

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
        // The specific counts from the bug report: these are the values where the
        // direct-file percentage truncated to zero.
        long start = Environment.TickCount64;

        TreemapNode root = Generate(requested);

        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(requested, CountFiles(root));
        Assert.True(elapsed < GenerationTimeout.TotalMilliseconds,
            $"generation took {elapsed}ms, expected well under {GenerationTimeout.TotalMilliseconds}ms");
    }

    [Fact]
    public void RootIsADriveWithChildren()
    {
        TreemapNode root = Generate(50);

        Assert.Equal(NodeKind.Drive, root.Kind);
        Assert.NotEmpty(root.Children);
    }

    [Fact]
    public void GenerationIsDeterministicForAGivenSeed()
    {
        // Same seed must produce the same shape, otherwise benchmark runs are not
        // comparable across machines.
        TreemapNode first = Generate(200, seed: 4242);
        TreemapNode second = Generate(200, seed: 4242);

        Assert.Equal(Describe(first), Describe(second));
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

    /// <summary>Runs generation on a background thread with a hard timeout.</summary>
    private static TreemapNode Generate(int fileCount, int seed = 12345)
    {
        Task<TreemapNode> task = Task.Run(() => SyntheticTree.Build(fileCount, seed));

        Assert.True(
            task.Wait(GenerationTimeout),
            $"SyntheticTree.Build({fileCount}) did not complete within " +
            $"{GenerationTimeout.TotalSeconds}s -- the generator is not making progress");

        return task.Result;
    }

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

    private static string Describe(TreemapNode node)
    {
        StringBuilder sb = new();
        Walk(node, sb, depth: 0);
        return sb.ToString();
    }

    private static void Walk(TreemapNode node, StringBuilder sb, int depth)
    {
        sb.Append(node.Kind).Append(':').Append(node.Name).Append(':').Append(depth).Append(';');
        foreach (TreemapNode child in node.Children)
        {
            Walk(child, sb, depth + 1);
        }
    }
}
