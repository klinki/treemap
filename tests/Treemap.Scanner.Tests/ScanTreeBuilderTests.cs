using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The shared tree builder: the stage both backends share, so it is tested directly
/// rather than only through a scan.
/// </summary>
public class ScanTreeBuilderTests
{
    [Fact]
    public void FoldersAggregateTheirChildren()
    {
        TreemapNode root = Build(
            new ScanEntry("a", NodeKind.Folder, 0, 0, null),
            new ScanEntry(Path.Combine("a", "one"), NodeKind.File, 100, 100, null),
            new ScanEntry(Path.Combine("a", "two"), NodeKind.File, 200, 300, null));

        TreemapNode folder = root.Children.Single();
        Assert.Equal(NodeKind.Folder, folder.Kind);
        Assert.Equal(300, folder.LogicalSize);
        Assert.Equal(400, folder.PhysicalSize);
        Assert.Equal(300, root.LogicalSize);
    }

    [Fact]
    public void EntriesMayArriveInAnyOrder()
    {
        TreemapNode root = Build(
            new ScanEntry(Path.Combine("a", "b", "leaf.txt"), NodeKind.File, 5, 5, null),
            new ScanEntry(Path.Combine("a", "b"), NodeKind.Folder, 0, 0, null),
            new ScanEntry("a", NodeKind.Folder, 0, 0, null));

        TreemapNode leaf = TreeAssert.Find(root, "a", "b", "leaf.txt")!;
        Assert.Equal(5, leaf.LogicalSize);
        Assert.Equal(NodeKind.Folder, TreeAssert.Find(root, "a", "b")!.Kind);
    }

    [Fact]
    public void ChildrenAreSortedByName()
    {
        TreemapNode root = Build(
            new ScanEntry("zebra", NodeKind.File, 1, 1, null),
            new ScanEntry("apple", NodeKind.File, 1, 1, null),
            new ScanEntry("mango", NodeKind.File, 1, 1, null));

        Assert.Equal(new[] { "apple", "mango", "zebra" }, root.Children.Select(c => c.Name));
    }

    /// <summary>
    /// A volume root is a Drive. Flattening it to a Folder because it has children would
    /// lose the distinction the renderer draws.
    /// </summary>
    [Fact]
    public void AVolumeRootKeepsItsDriveKind()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        ScanIdMap ids = new();
        ids.BeginBuild();
        TreemapNode root = ScanTreeBuilder.Build(
            volumeRoot,
            [new ScanEntry("windows", NodeKind.Folder, 0, 0, null)],
            ids,
            CancellationToken.None);

        Assert.Equal(NodeKind.Drive, root.Kind);
        Assert.NotEqual(string.Empty, root.Name);
    }

    [Fact]
    public void VolumeRootNameDropsTheTrailingSeparatorButNotABareOne()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        string expected = volumeRoot.Length > 1
            ? volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : volumeRoot;

        Assert.Equal(expected, ScanTreeBuilder.RootName(volumeRoot));
    }

    [Fact]
    public void AFolderRootIsNamedAfterTheFolder()
    {
        Assert.Equal("treemap", ScanTreeBuilder.RootName(Path.Combine(Path.GetTempPath(), "treemap")));
        Assert.False(ScanTreeBuilder.IsVolumeRoot(Path.Combine(Path.GetTempPath(), "treemap")));
    }

    /// <summary>
    /// A trailing separator makes no difference to which directory this is, so it must
    /// not produce an empty name or a root path that disagrees with it.
    /// </summary>
    [Fact]
    public void ATrailingSeparatorDoesNotEmptyTheRootName()
    {
        string folder = Path.Combine(Path.GetTempPath(), "treemap-root-name");
        string padded = folder + Path.DirectorySeparatorChar;

        Assert.Equal("treemap-root-name", ScanTreeBuilder.RootName(padded));
        Assert.Equal(Path.TrimEndingDirectorySeparator(folder), ScanTreeBuilder.FullPath(padded));
        Assert.Equal(ScanTreeBuilder.RootName(folder), ScanTreeBuilder.RootName(padded));
        Assert.False(ScanTreeBuilder.IsVolumeRoot(padded));
    }

    [WindowsOnlyFact]
    public void RootNameKeepsABareSeparatorIntact()
    {
        // "C:\" trimmed of its only character would leave nothing to display.
        Assert.Equal("C:", ScanTreeBuilder.RootName("C:\\"));
    }

    /// <summary>
    /// A backslash is a legal character in a Unix file name. Splitting on it there would
    /// tear one file into a folder and a file.
    /// </summary>
    [UnixOnlyFact]
    public void BackslashInAUnixFileNameIsNotASeparator()
    {
        TreemapNode root = Build(new ScanEntry("weird\\name.txt", NodeKind.File, 7, 7, null));

        TreemapNode file = Assert.Single(root.Children);
        Assert.Equal("weird\\name.txt", file.Name);
        Assert.Equal(NodeKind.File, file.Kind);
    }

    [Fact]
    public void IdenticalPathsCollapseToOneNode()
    {
        TreemapNode root = Build(
            new ScanEntry(Path.Combine("a", "same.txt"), NodeKind.File, 10, 10, null),
            new ScanEntry(Path.Combine("a", "same.txt"), NodeKind.File, 10, 10, null));

        Assert.Equal(10, TreeAssert.Find(root, "a", "same.txt")!.LogicalSize);
        Assert.Single(root.Children);
    }

    /// <summary>
    /// Two files can each be a legal size and still add up to more than a long holds, which
    /// is what a volume-sized scan reports through <see cref="ScanResult.TotalLogicalSize"/>.
    /// A folder total that wrapped negative would contradict it and render as a tree with
    /// no area at all, so the total has to saturate instead. Covered at both levels: the
    /// folder that holds the pair and the root above it.
    /// </summary>
    [Fact]
    public void ATotalLargerThanALongSaturatesInsteadOfWrapping()
    {
        TreemapNode root = Build(
            new ScanEntry(Path.Combine("nested", "huge.bin"), NodeKind.File, long.MaxValue - 1, long.MaxValue - 1, null),
            new ScanEntry(Path.Combine("nested", "rest.bin"), NodeKind.File, 10, 10, null));

        TreemapNode folder = Assert.Single(root.Children);
        Assert.Equal(NodeKind.Folder, folder.Kind);

        foreach (TreemapNode node in new[] { root, folder })
        {
            Assert.True(node.LogicalSize >= 0, "a folder total went negative");
            Assert.Equal(long.MaxValue, node.LogicalSize);
            Assert.Equal(long.MaxValue, node.PhysicalSize);
        }
    }

    private static TreemapNode Build(params ScanEntry[] entries)
    {
        ScanIdMap ids = new();
        ids.BeginBuild();
        return ScanTreeBuilder.Build(Path.Combine(Path.GetTempPath(), "builder"), entries, ids, CancellationToken.None);
    }
}

/// <summary>
/// The id map, tested directly: its guarantee is narrow and worth pinning down.
/// </summary>
public class ScanIdMapTests
{
    [Fact]
    public void ReusesIdsAcrossCommittedBuilds()
    {
        ScanIdMap map = new();

        map.BeginBuild();
        int alpha = map.Reserve("/tree/alpha");
        int beta = map.Reserve("/tree/beta");
        map.Commit();

        map.BeginBuild();
        Assert.Equal(alpha, map.Reserve("/tree/alpha"));
        Assert.Equal(beta, map.Reserve("/tree/beta"));
    }

    [Fact]
    public void NewPathsGetFreshIdsStartingAboveTheRoot()
    {
        ScanIdMap map = new();
        map.BeginBuild();

        Assert.Equal(2, map.Reserve("/tree/alpha"));
        Assert.Equal(3, map.Reserve("/tree/beta"));
        map.Commit();
    }

    [Fact]
    public void PathsMissingFromTheNextTreeAreForgotten()
    {
        ScanIdMap map = new();

        map.BeginBuild();
        int kept = map.Reserve("/tree/kept");
        map.Reserve("/tree/gone");
        map.Commit();

        map.BeginBuild();
        int reused = map.Reserve("/tree/kept");
        map.Commit();

        // "gone" is no longer in the map, so it is not holding an id hostage.
        map.BeginBuild();
        Assert.Equal(kept, reused);
        Assert.True(map.Reserve("/tree/brand-new") > reused, "a new path was handed a stale id");
    }

    /// <summary>
    /// A build that never produced a result must not become the baseline, or a cancelled
    /// scan would rewrite the identity of a tree the user is still looking at.
    /// </summary>
    [Fact]
    public void AbortedBuildsDoNotReplaceTheCommittedMap()
    {
        ScanIdMap map = new();

        map.BeginBuild();
        int original = map.Reserve("/tree/alpha");
        map.Commit();

        map.BeginBuild();
        map.Reserve("/tree/half-read");
        map.Abort();

        map.BeginBuild();
        Assert.Equal(original, map.Reserve("/tree/alpha"));
    }

    [Fact]
    public void ReservingOutsideABuildIsAProgrammerError()
    {
        ScanIdMap map = new();
        Assert.Throws<InvalidOperationException>(() => map.Reserve("/tree/alpha"));
    }

    [Fact]
    public void CaseMatchesOnlyWhereThePlatformSaysSo()
    {
        ScanIdMap map = new();
        map.BeginBuild();
        int first = map.Reserve("/tree/MixedCase");

        int second = OperatingSystem.IsWindows()
            ? map.Reserve("/tree/mixedcase")
            : map.Reserve("/tree/MixedCase");

        Assert.Equal(first, second);
    }
}

/// <summary>Volume discovery, which is what a picker is built on.</summary>
public class VolumeDiscoveryTests
{
    [Fact]
    public void OnlyReadyVolumesAreReturned()
    {
        IReadOnlyList<ScannableVolume> volumes = FileSystemScanner.GetReadyVolumes();

        Assert.NotNull(volumes);
        Assert.All(volumes, v =>
        {
            Assert.False(string.IsNullOrWhiteSpace(v.RootDirectory));
            Assert.True(v.TotalSize >= 0);
            Assert.True(v.AvailableFreeSpace >= 0);
            Assert.True(v.AvailableFreeSpace <= v.TotalSize || v.TotalSize == 0);
        });
    }

    [Fact]
    public async Task AsyncDiscoveryMatchesTheSynchronousOne()
    {
        IReadOnlyList<ScannableVolume> direct = FileSystemScanner.GetReadyVolumes();
        IReadOnlyList<ScannableVolume> async = await FileSystemScanner.GetReadyVolumesAsync();

        Assert.Equal(direct.Select(v => v.RootDirectory), async.Select(v => v.RootDirectory));
    }
}
