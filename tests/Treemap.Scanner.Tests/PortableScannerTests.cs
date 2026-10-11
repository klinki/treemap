using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The universal scanner against real directories: what it finds, what it refuses to
/// follow, and what it throws.
/// </summary>
public class PortableScannerTests
{
    [Fact]
    public async Task ScansDirectoriesFilesAndTotals()
    {
        using TempTree tree = new(nameof(ScansDirectoriesFilesAndTotals));
        tree.MakeFile("root.txt", 100);
        tree.MakeDirectory("alpha");
        tree.MakeFile(Path.Combine("alpha", "one.bin"), 200);
        tree.MakeFile(Path.Combine("alpha", "two.bin"), 300);
        tree.MakeDirectory("alpha", "nested");
        tree.MakeFile(Path.Combine("alpha", "nested", "deep.txt"), 400);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.True(result.IsComplete, result.IncompleteReason);
        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Equal(2, result.DirectoryCount);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(1000, result.TotalLogicalSize);

        Assert.NotNull(TreeAssert.Find(result.Root, "alpha", "nested", "deep.txt"));
        Assert.Equal(900, TreeAssert.Find(result.Root, "alpha")!.LogicalSize);
    }

    /// <summary>
    /// A zero-byte file and an empty directory are real things on disk. Dropping them
    /// would make the treemap quietly lie about what is there.
    /// </summary>
    [Fact]
    public async Task KeepsEmptyDirectoriesAndZeroByteFiles()
    {
        using TempTree tree = new(nameof(KeepsEmptyDirectoriesAndZeroByteFiles));
        tree.MakeFile("empty.txt");
        tree.MakeDirectory("nothing-here");

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        TreemapNode? empty = TreeAssert.Find(result.Root, "empty.txt");
        Assert.NotNull(empty);
        Assert.Equal(0, empty.LogicalSize);

        TreemapNode? folder = TreeAssert.Find(result.Root, "nothing-here");
        Assert.NotNull(folder);
        Assert.Equal(NodeKind.Folder, folder.Kind);
        Assert.Empty(folder.Children);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.DirectoryCount);
    }

    /// <summary>
    /// Relative paths must come out as single segments. A doubled separator or a stray
    /// prefix would show up as a blank node name in the renderer.
    /// </summary>
    [Fact]
    public async Task EveryNodeNameIsASingleSegment()
    {
        using TempTree tree = new(nameof(EveryNodeNameIsASingleSegment));
        tree.MakeFile(Path.Combine("a", "b", "c", "file.txt"), 1);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        foreach (TreemapNode node in TreeAssert.Flatten(result.Root))
        {
            Assert.NotEqual(string.Empty, node.Name);
            Assert.DoesNotContain('\\', node.Name);
            Assert.DoesNotContain('/', node.Name);
        }
    }

    /// <summary>
    /// Hidden and system entries are scanned like everything else, unless a caller asks
    /// otherwise. Only Windows has those attributes to ask about: on Unix the setter
    /// either refuses them or ignores them, so there is nothing here to assert.
    /// </summary>
    [WindowsOnlyFact]
    public async Task HiddenAndSystemEntriesAreIncludedByDefault()
    {
        using TempTree tree = new(nameof(HiddenAndSystemEntriesAreIncludedByDefault));
        tree.MakeFileWithAttributes("plain.txt", FileAttributes.Normal, 10);
        tree.MakeFileWithAttributes("secret.txt", FileAttributes.Hidden, 20);
        tree.MakeFileWithAttributes("system.txt", FileAttributes.System, 30);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.Equal(3, result.FileCount);
        Assert.Equal(60, result.TotalLogicalSize);
    }

    /// <summary>Skipping is opt-in, and then it is honoured exactly.</summary>
    [WindowsOnlyFact]
    public async Task SkipAttributesIsHonouredWhenAskedFor()
    {
        using TempTree tree = new(nameof(SkipAttributesIsHonouredWhenAskedFor));
        tree.MakeFileWithAttributes("plain.txt", FileAttributes.Normal, 10);
        tree.MakeFileWithAttributes("secret.txt", FileAttributes.Hidden, 20);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose() with
        {
            SkipAttributes = FileAttributes.Hidden,
        });

        Assert.Equal(1, result.FileCount);
        Assert.Equal(10, result.TotalLogicalSize);
    }

    [RequiresDirectoryLinks]
    public async Task LinksAreRecordedButNeverFollowed()
    {
        using TempTree tree = new(nameof(LinksAreRecordedButNeverFollowed));
        tree.MakeDirectory("real");
        tree.MakeFile(Path.Combine("real", "payload.bin"), 5000);

        // The probe above said this should work; if the fixture still refuses, the
        // environment disagrees with itself and there is nothing to assert.
        if (!DirectoryLinkProbe.TryCreate(Path.Combine(tree.Root, "shortcut"), Path.Combine(tree.Root, "real")))
        {
            return;
        }

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        TreemapNode? link = TreeAssert.Find(result.Root, "shortcut");
        Assert.NotNull(link);
        Assert.Equal(NodeKind.Folder, link.Kind);
        Assert.Empty(link.Children);

        // The link contributes nothing of its own; only what it points at is counted.
        Assert.Equal(1, result.FileCount);
        Assert.Equal(5000, result.TotalLogicalSize);

        Assert.Contains(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Warning && d.Path.Contains("shortcut"));
    }

    /// <summary>
    /// A link pointing back up the tree would otherwise make the walk unbounded. The
    /// scan has to terminate, and it has to terminate because it refused the link rather
    /// than because it hit a limit.
    /// </summary>
    [RequiresDirectoryLinks]
    public async Task LinkCycleDoesNotLoop()
    {
        using TempTree tree = new(nameof(LinkCycleDoesNotLoop));
        tree.MakeDirectory("a", "b");

        if (!DirectoryLinkProbe.TryCreate(Path.Combine(tree.Root, "a", "b", "up"), tree.Root))
        {
            return;
        }

        Task<ScanResult> scan = new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());
        Task first = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(scan, first);

        ScanResult result = await scan;
        Assert.True(result.IsComplete, result.IncompleteReason);
    }

    [Fact]
    public async Task MissingRootThrowsDirectoryNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), "treemap-scanner-tests", "not-here-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new FileSystemScanner().ScanAsync(missing, options: TestOptions.Verbose()));
    }

    /// <summary>A file is not a tree. That is an error, not an empty result.</summary>
    [Fact]
    public async Task FileRootThrows()
    {
        using TempTree tree = new(nameof(FileRootThrows));
        string file = tree.MakeFile("a.txt", 1);

        await Assert.ThrowsAsync<IOException>(
            () => new FileSystemScanner().ScanAsync(file, options: TestOptions.Verbose()));
    }

    /// <summary>
    /// The root is the one place following a link could leave the tree the caller chose,
    /// or revisit itself, so it is refused rather than resolved.
    /// </summary>
    [RequiresDirectoryLinks]
    public async Task LinkRootIsRefused()
    {
        using TempTree tree = new(nameof(LinkRootIsRefused));
        tree.MakeDirectory("target");
        tree.MakeFile(Path.Combine("target", "a.txt"), 1);

        string link = Path.Combine(tree.Root, "link");
        if (!DirectoryLinkProbe.TryCreate(link, Path.Combine(tree.Root, "target")))
        {
            return;
        }

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => new FileSystemScanner().ScanAsync(link, options: TestOptions.Verbose()));

        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlankRootIsRejectedBeforeAnyWork()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new FileSystemScanner().ScanAsync("   "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new FileSystemScanner().ScanAsync(null!));
    }

    /// <summary>
    /// A directory full of files is the shape that makes a naive sibling lookup quadratic.
    /// It also has to come back in a stable order regardless of how the workers
    /// interleaved.
    /// </summary>
    [Fact]
    public async Task LargeFlatDirectoryIsSortedAndComplete()
    {
        using TempTree tree = new(nameof(LargeFlatDirectoryIsSortedAndComplete));

        const int Count = 400;
        for (int i = 0; i < Count; i++)
        {
            tree.MakeFile($"f{i:D4}.bin", i % 7);
        }

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.True(result.IsComplete, result.IncompleteReason);
        Assert.Equal(Count, result.FileCount);

        string[] names = [.. result.Root.Children.Select(c => c.Name)];
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
    }
}