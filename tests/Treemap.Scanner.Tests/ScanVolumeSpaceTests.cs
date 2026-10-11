using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Volume accounting: the free-space block and the nonnegative unaccounted-space
/// residual, measured against per-file layout weights rather than folder totals.
/// </summary>
public class ScanVolumeSpaceTests
{
    private static string VolumeRoot => OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static FileSystemScanner VolumeScanner(ScriptedBackend backend, long total, long free) => new(
        backend,
        null,
        _ => new VolumeSpaceInfo(total, free));

    private static ScanOptions VolumeOptions() => TestOptions.Verbose(ScanBackendPreference.Portable);

    [Theory]
    [InlineData(10_000, 3_000, 3_500, 3_500)]
    [InlineData(10_000, 3_000, 7_000, 0)]
    [InlineData(10_000, 3_000, 99_999, 0)]
    [InlineData(10_000, 10_000, 0, 0)]
    [InlineData(10_000, 99_999, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-5, -5, -5, 0)]
    public void ResidualIsUsedMinusRepresentedClampedAtZero(long total, long free, long represented, long expected)
        => Assert.Equal(expected, VolumeSpace.UnknownResidual(total, free, represented));

    [Fact]
    public void ResidualNeverWrapsAtTheBounds()
    {
        Assert.Equal(0, VolumeSpace.UnknownResidual(long.MaxValue, 0, long.MaxValue));
        Assert.Equal(long.MaxValue, VolumeSpace.UnknownResidual(long.MaxValue, 0, 0));
    }

    [Fact]
    public async Task VolumeRootGetsFreeAndUnknownBlocks_WithMixedAllocationFallback()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("known.bin", NodeKind.File, 1_000, 2_000, null));
        backend.Entries.Add(new ScanEntry("sparse.bin", NodeKind.File, 1_500, 0, null));

        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        // Used is 7000; represented is 2000 + 1500 (logical fallback where
        // allocation is unknown), so 3500 is unaccounted for.
        Assert.Equal(3_000, result.FreeSpaceBytes);
        Assert.Equal(3_500, result.UnknownSpaceBytes);

        TreemapNode? free = TreeAssert.Find(result.Root, "Free space");
        Assert.NotNull(free);
        Assert.Equal(NodeKind.FreeSpace, free.Kind);
        Assert.Equal(3_000, free.PhysicalSize);

        TreemapNode? unknown = TreeAssert.Find(result.Root, "Unaccounted space");
        Assert.NotNull(unknown);
        Assert.Equal(NodeKind.UnknownSpace, unknown.Kind);
        Assert.Equal(3_500, unknown.PhysicalSize);

        Assert.Equal(NodeKind.Drive, result.Root.Kind);
    }

    [Fact]
    public async Task FileTotalsExcludeSpaceBlocks()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 2_000, null));

        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        Assert.Equal(1_000, result.TotalLogicalSize);
        Assert.Equal(2_000, result.TotalPhysicalSize);
        Assert.Equal(2_000 + 3_000 + 5_000, result.Root.PhysicalSize);
    }

    [Fact]
    public async Task SpaceBlocksAreIndependentlyOptional()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        ScanResult noUnknown = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions() with { IncludeUnknownSpace = false });
        Assert.Equal(3_000, noUnknown.FreeSpaceBytes);
        Assert.Equal(0, noUnknown.UnknownSpaceBytes);
        Assert.Null(TreeAssert.Find(noUnknown.Root, "Unaccounted space"));

        ScanResult noFree = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions() with { IncludeFreeSpace = false });
        Assert.Equal(0, noFree.FreeSpaceBytes);
        Assert.True(noFree.UnknownSpaceBytes > 0);
        Assert.Null(TreeAssert.Find(noFree.Root, "Free space"));
    }

    [Fact]
    public async Task UnavailableMetadataAddsNoBlocks()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        ScanResult result = await new FileSystemScanner(backend, null, _ => null)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        Assert.Equal(0, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);
        Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
        Assert.Contains(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task FolderScanNeverGetsSpaceBlocks()
    {
        using TempTree tree = new(nameof(FolderScanNeverGetsSpaceBlocks));
        tree.MakeFile("a.txt", 100);

        ScanResult result = await new FileSystemScanner(null, null, _ => new VolumeSpaceInfo(10_000, 3_000))
            .ScanAsync(tree.Root, options: VolumeOptions());

        Assert.Equal(0, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);
        Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
    }

    [Fact]
    public async Task IncompleteVolumeScanStillGetsSpaceBlocks()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        for (int i = 0; i < 5; i++)
        {
            backend.Entries.Add(new ScanEntry($"f{i}.bin", NodeKind.File, 1_000, 1_000, null));
        }

        ScanResult result = await VolumeScanner(backend, total: 100_000, free: 10_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions() with { MaxEntries = 2 });

        Assert.False(result.IsComplete);
        Assert.True(result.FreeSpaceBytes > 0);
        Assert.True(result.UnknownSpaceBytes > 0);
        Assert.NotNull(TreeAssert.Find(result.Root, "Free space"));
        Assert.NotNull(TreeAssert.Find(result.Root, "Unaccounted space"));
    }

    [Fact]
    public async Task OvercountedHardLinksClampTheResidual()
    {
        ScriptedBackend backend = new(ScanBackend.Portable) { MarkExtraNamesAsHardLinks = true };
        backend.Entries.Add(new ScanEntry(Path.Combine("one", "shared.bin"), NodeKind.File, 5_000, 5_000, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("two", "shared.bin"), NodeKind.File, 5_000, 5_000, null));

        // Used is 7000 but the two links represent 10000, so nothing is missing.
        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        Assert.Equal(3_000, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);
        Assert.Null(TreeAssert.Find(result.Root, "Unaccounted space"));
        Assert.NotNull(TreeAssert.Find(result.Root, "Free space"));
    }

    [Fact]
    public async Task SaturatingTotalsCannotProduceANegativeResidual()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("huge-a.bin", NodeKind.File, long.MaxValue, long.MaxValue, null));
        backend.Entries.Add(new ScanEntry("huge-b.bin", NodeKind.File, long.MaxValue, long.MaxValue, null));

        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: VolumeOptions() with { MaxEntries = long.MaxValue });

        Assert.Equal(long.MaxValue, result.TotalPhysicalSize);
        Assert.Equal(0, result.UnknownSpaceBytes);
    }

    [Fact]
    public async Task FreeBeyondTotalIsClampedToTheVolume()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 99_999)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        Assert.Equal(10_000, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);

        TreemapNode? free = TreeAssert.Find(result.Root, "Free space");
        Assert.NotNull(free);
        Assert.Equal(10_000, free.PhysicalSize);
        Assert.Null(TreeAssert.Find(result.Root, "Unaccounted space"));
    }

    [Fact]
    public async Task InvalidVolumeMetadataAddsNoBlocks()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        ScanResult result = await VolumeScanner(backend, total: -5, free: -5)
            .ScanAsync(VolumeRoot, options: VolumeOptions());

        Assert.Equal(0, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);
        Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
    }

    [Fact]
    public async Task FolderScanNeverCallsTheVolumeProvider()
    {
        using TempTree tree = new(nameof(FolderScanNeverCallsTheVolumeProvider));
        tree.MakeFile("a.txt", 100);

        int providerCalls = 0;
        ScanOptions options = new()
        {
            Backend = ScanBackendPreference.Portable,
            ProgressInterval = TimeSpan.Zero,
            PublishIntermediateSnapshots = true,
            SnapshotInterval = TimeSpan.FromMilliseconds(20),
        };

        ScanResult result = await new FileSystemScanner(
                null,
                null,
                _ => { Interlocked.Increment(ref providerCalls); return new VolumeSpaceInfo(10_000, 3_000); })
            .ScanAsync(tree.Root, TestPublisher.Empty(), options);

        Assert.Equal(0, providerCalls);
        Assert.Equal(0, result.FreeSpaceBytes);
        Assert.Equal(0, result.UnknownSpaceBytes);
        Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
    }

    [Fact]
    public void UnknownSpaceDefaultsOn()
    {
        Assert.True(ScanOptions.Default.IncludeUnknownSpace);
        Assert.True(ScanOptions.Default.IncludeFreeSpace);
    }
}
