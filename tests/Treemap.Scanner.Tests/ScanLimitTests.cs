using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The bounds. A scan that stops early must say so; a scan that hides why is worse than
/// one that returns less.
/// </summary>
public class ScanLimitTests
{
    [Fact]
    public async Task EntryBudgetStopsTheWalkAndMarksTheResultIncomplete()
    {
        using TempTree tree = new(nameof(EntryBudgetStopsTheWalkAndMarksTheResultIncomplete));

        for (int i = 0; i < 50; i++)
        {
            tree.MakeFile($"f{i:D3}.bin", 1);
        }

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose() with
        {
            MaxEntries = 10,
        });

        Assert.False(result.IsComplete);
        Assert.NotNull(result.IncompleteReason);
        Assert.Contains("budget", result.IncompleteReason!, StringComparison.OrdinalIgnoreCase);

        // The budget counts directories and diagnostics too, so the total is bounded by
        // it rather than merely close to it.
        Assert.True(result.Root.Children.Count <= 10, $"kept {result.Root.Children.Count} entries against a budget of 10");
    }

    /// <summary>
    /// Diagnostics are charged against the same budget, so producing a flood of them
    /// cannot crowd out the tree - and cannot dodge being counted either.
    /// </summary>
    [RequiresDirectoryLinks]
    public async Task DiagnosticFloodIsBoundedAndStillCounts()
    {
        using TempTree tree = new(nameof(DiagnosticFloodIsBoundedAndStillCounts));
        tree.MakeDirectory("target");

        // Each link produces one warning without being followed, so they can be generated
        // in bulk without needing permissions to make real failures happen.
        int made = 0;
        for (int i = 0; i < 30; i++)
        {
            if (!DirectoryLinkProbe.TryCreate(
                    Path.Combine(tree.Root, "target", $"link{i:D2}"),
                    Path.Combine(tree.Root, "target")))
            {
                break;
            }

            made++;
        }

        if (made < 10)
        {
            // The link probe passed but the fixture could not produce a flood; nothing
            // here can be asserted about how the scanner handles one.
            return;
        }

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose() with
        {
            MaxDiagnostics = 5,
            MaxEntries = 1000,
        });

        Assert.True(result.Diagnostics.Count <= 5, "the retained diagnostic list exceeded its bound");
        Assert.True(result.SuppressedDiagnostics > 0, "suppressed diagnostics were not counted");
        Assert.True(result.IsComplete, "a deliberate policy warning should not read as a failure");
    }

    [Fact]
    public async Task DepthLimitStopsDescendingAndMarksTheResultIncomplete()
    {
        using TempTree tree = new(nameof(DepthLimitStopsDescendingAndMarksTheResultIncomplete));
        tree.MakeFile(Path.Combine("a", "b", "c", "d", "too-deep.txt"), 1);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose() with
        {
            MaxDepth = 2,
        });

        Assert.False(result.IsComplete);
        Assert.Contains("depth", result.IncompleteReason!, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(TreeAssert.Find(result.Root, "a", "b"));
        Assert.Null(TreeAssert.Find(result.Root, "a", "b", "c", "d", "too-deep.txt"));
    }

    [Fact]
    public async Task TrailingSeparatorsOnTheRootDoNotChangeTheResult()
    {
        using TempTree tree = new(nameof(TrailingSeparatorsOnTheRootDoNotChangeTheResult));
        tree.MakeFile(Path.Combine("sub", "a.txt"), 5);

        ScanResult clean = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());
        ScanResult padded = await new FileSystemScanner().ScanAsync(tree.Root + Path.DirectorySeparatorChar, options: TestOptions.Verbose());

        Assert.Equal(Path.TrimEndingDirectorySeparator(tree.Root), padded.RootPath);
        Assert.Equal(clean.RootPath, padded.RootPath);
        Assert.Equal(clean.Root.Name, padded.Root.Name);
        Assert.NotEqual(string.Empty, padded.Root.Name);
        Assert.Equal(clean.TotalLogicalSize, padded.TotalLogicalSize);
        Assert.Equal(
            clean.Root.Children.Select(c => c.Name).ToArray(),
            padded.Root.Children.Select(c => c.Name).ToArray());
    }
}

/// <summary>Option validation happens before any filesystem work.</summary>
public class ScanOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(ScanOptions.MaxParallelismHardLimit + 1)]
    [InlineData(-1)]
    public void ParallelismOutsideTheRangeIsRejected(int parallelism)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScanOptions { MaxParallelism = parallelism }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ScanOptions.MaxDepthHardLimit + 1)]
    public void DepthOutsideTheRangeIsRejected(int depth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions { MaxDepth = depth }.Validate());
    }

    [Fact]
    public void DepthAtTheHardLimitIsAccepted()
    {
        ScanOptions options = new() { MaxDepth = ScanOptions.MaxDepthHardLimit };
        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void ZeroEntryBudgetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions { MaxEntries = 0 }.Validate());
    }

    [Fact]
    public void NegativeIntervalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScanOptions { ProgressInterval = TimeSpan.FromSeconds(-1) }.Validate());
    }

    [Fact]
    public void DefaultsAreInsideTheirOwnRanges()
    {
        ScanOptions defaults = ScanOptions.Default;
        Assert.Same(defaults, defaults.Validate());
        Assert.Equal(FileAttributes.None, defaults.SkipAttributes);
        Assert.Equal(ScanBackendPreference.Auto, defaults.Backend);
    }

    /// <summary>
    /// An undefined preference would read as "not Portable" and quietly pick a backend the
    /// caller never asked for, so it is rejected like any other out-of-range value.
    /// </summary>
    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void UndefinedBackendPreferenceIsRejected(int value)
    {
        ScanOptions options = new() { Backend = (ScanBackendPreference)value };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public async Task AnUndefinedBackendFailsBeforeTouchingTheFilesystem()
    {
        using TempTree tree = new(nameof(AnUndefinedBackendFailsBeforeTouchingTheFilesystem));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new FileSystemScanner().ScanAsync(
                tree.Root,
                options: new ScanOptions { Backend = (ScanBackendPreference)99 }));
    }

    [Fact]
    public async Task InvalidOptionsFailBeforeTouchingTheFilesystem()
    {
        using TempTree tree = new(nameof(InvalidOptionsFailBeforeTouchingTheFilesystem));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new FileSystemScanner().ScanAsync(tree.Root, options: new ScanOptions { MaxDepth = 0 }));
    }

    /// <summary>
    /// Allocation is a separate question from length. Turning it off must leave the
    /// logical sizes intact rather than zeroing the tree.
    /// </summary>
    [Fact]
    public async Task WithoutAllocationTheLogicalSizesStillDescribeTheTree()
    {
        using TempTree tree = new(nameof(WithoutAllocationTheLogicalSizesStillDescribeTheTree));
        tree.MakeFile("a.bin", 4096);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose() with
        {
            QueryAllocatedSize = false,
        });

        Assert.Equal(4096, result.TotalLogicalSize);
        Assert.Equal(0, result.TotalPhysicalSize);

        // LayoutSize is what the renderer measures with, and it already falls back to the
        // logical size when allocation is unknown - so the node still has area.
        TreemapNode node = TreeAssert.Find(result.Root, "a.bin")!;
        Assert.Equal(4096, node.LayoutSize);
    }
}