using Treemap;
using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// A fact that only means anything on Windows, because the code under it needs the scanner
/// to accept a real <c>C:\</c> root. Reported as skipped elsewhere rather than quietly
/// passing.
/// </summary>
internal sealed class NtfsWindowsOnlyFactAttribute : FactAttribute
{
    public NtfsWindowsOnlyFactAttribute() => Skip = OperatingSystem.IsWindows() ? null : "Windows only.";
}

/// <summary>
/// The MFT backend driven through its own seams: a record source the test fills in, and a
/// metadata query it can answer itself.
/// </summary>
/// <remarks>
/// These are not tests of a mock. The volume open, the downward walk, the directory map, the
/// subtree walk and the parser are all the production ones; only the handle and the ordinary
/// file query are substituted, because those are the two things a test cannot have.
///
/// Record ordinals are 16 or above throughout, because 0 to 15 are reserved by NTFS and the
/// backend steps over them unread. Record 5 is the volume root.
/// </remarks>
public class NtfsMftScanBackendTests
{
    private const ulong Root = 5;
    private const ulong Subdirectory = 16;
    private const ulong InnerDirectory = 17;
    private const ushort RootSequence = 5;
    private const ushort RecordSequence = 1;
    private const string VolumeRootPath = @"C:\";
    private const string ScanRootPath = @"C:\sub";

    [Fact]
    public void EmitsOnlyTheEntriesInsideTheRequestedSubtree()
    {
        FixtureRecordSource source = Volume();
        source.Add(60, File(60, Root, "on-the-root.txt", 5_000, 5_120, RootSequence));

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        // Relative to the scan root, so the root itself is not an entry.
        Assert.Equal(["a.txt", "b.bin", "inner", Nested("inner", "inner.txt")], Paths(collector));
        Assert.Equal(3, collector.FileCount);
        Assert.Equal(1, collector.DirectoryCount);
    }

    [Fact]
    public void ScansTheWholeVolumeWhenTheRootIsTheVolumeRoot()
    {
        ScanCollector collector = Collector();

        Assert.True(Backend(Volume()).TryRun(Request(VolumeRootPath), collector, out string? failure), failure);

        Assert.Contains(Nested("sub", "a.txt"), Paths(collector));
        Assert.Contains("on-the-root.txt", Paths(collector));
    }

    [Fact]
    public void EachPassDescendsThroughVacantRecordsWithoutRepeating()
    {
        FixtureRecordSource source = Volume();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure), failure);

        // The table is walked twice: once for directories, once for entries.
        int pass = 0;

        for (int i = 1; i < source.Requested.Count; i++)
        {
            if (source.Requested[i] >= source.Requested[i - 1])
            {
                pass++;
                continue;
            }

            Assert.True(pass <= 1, $"the walk did not descend at request {i}.");
        }

        Assert.Equal(1, pass);
        Assert.Equal(4095UL, source.Requested[0]);
    }

    [Fact]
    public void ARequestSkipsVacantRecordsAndResumesBelowTheOneItGot()
    {
        FixtureRecordSource source = Volume();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure), failure);

        // The kernel was asked for 4095 and answered 60, so the next request is 59.
        // 60 itself was never requested; vacant records are skipped by the source.
        Assert.Equal(4095UL, source.Requested[0]);
        Assert.Equal(59UL, source.Requested[1]);
    }

    [Fact]
    public void DeclinesWhenTheSourceAnswersAboveTheRequestedOrdinal()
    {
        FixtureRecordSource source = Volume();
        source.ForcedOrdinal = 5_000;

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("would not make progress", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclinesWhenARecordWillNotValidate()
    {
        FixtureRecordSource source = Volume();
        source.Add(42, new MftRecordBuilder()
            .CorruptTailAt(1)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "torn.txt")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("torn", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclinesWhenAnAttributeListNeverEnds()
    {
        FixtureRecordSource source = Volume();
        source.Add(42, new MftRecordBuilder()
            .WithoutEndMarker()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "runaway.txt")
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("end-of-attributes", failure!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records 1 to 15 and 6 to 15 are NTFS's own. They carry layouts this backend does not
    /// model and name no directory entry, so they are stepped over unread - otherwise every
    /// volume on earth would send the scan back to the portable backend.
    /// </summary>
    [Fact]
    public void StepsOverReservedRecordsWithoutReadingThem()
    {
        FixtureRecordSource source = Volume();
        source.Add(7, new byte[1024]);

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);
        Assert.Equal(["a.txt", "b.bin", "inner", Nested("inner", "inner.txt")], Paths(collector));
    }

    [Fact]
    public void CancellationStopsTheWalkAndNeverBecomesAFallback()
    {
        FixtureRecordSource source = Volume();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => Backend(source).TryRun(new ScanRequest(ScanRootPath, ScanOptions.Default, cts.Token), Collector(), out _));
    }

    [Fact]
    public void AFileWithNoUnnamedDataIsAskedForTheOrdinaryWay()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "streamy.dat")
            .AddNamedStream("payload", [1, 2, 3, 4])
            .Build());

        List<string> asked = [];
        ScanCollector collector = Collector();

        Assert.True(
            Backend(source, (string path, out long logical, out long allocated, out DateTimeOffset? lastWrite) =>
            {
                asked.Add(path);
                logical = 777;
                allocated = 4_096;
                lastWrite = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
                return true;
            }).TryRun(Request(ScanRootPath), collector, out string? failure),
            failure);

        // The backend builds the path itself, so it is joined the way this host joins.
        Assert.Equal([Path.Combine(ScanRootPath, "streamy.dat")], asked);

        ScanEntry entry = collector.Entries.Single(e => e.RelativePath.EndsWith("streamy.dat", StringComparison.Ordinal));
        Assert.Equal(777, entry.LogicalSize);
        Assert.Equal(4_096, entry.PhysicalSize);
    }

    [Fact]
    public void AFileTheFilesystemCannotAnswerEitherIsReportedRatherThanSizedZero()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "streamy.dat")
            .AddNamedStream("payload", [1, 2, 3])
            .Build());

        ScanCollector collector = Collector();

        Assert.True(
            Backend(source, static (string path, out long logical, out long allocated, out DateTimeOffset? lastWrite) =>
            {
                logical = 0;
                allocated = 0;
                lastWrite = null;
                return false;
            }).TryRun(Request(ScanRootPath), collector, out string? failure),
            failure);

        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("no readable unnamed DATA", StringComparison.Ordinal));
        Assert.False(collector.IsComplete);
    }

    [Fact]
    public void TurningOffAllocatedSizeReportsTheLogicalSizeAlone()
    {
        ScanOptions options = ScanOptions.Default with { QueryAllocatedSize = false };
        ScanCollector collector = Collector(options);

        Assert.True(Backend(Volume()).TryRun(Request(ScanRootPath, options), collector, out string? failure), failure);

        Assert.All(collector.Entries.Where(e => e.Kind == NodeKind.File), e => Assert.Equal(0, e.PhysicalSize));
        Assert.Contains(collector.Entries, e => e.LogicalSize == 1_000);
    }

    [Fact]
    public void AReparseFileKeepsItsNameAndReportsNoBytes()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.ReparsePoint)
            .AddFileName(Subdirectory, "link.txt")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry link = collector.Entries.Single(e => e.RelativePath.EndsWith("link.txt", StringComparison.Ordinal));
        Assert.Equal(0, link.LogicalSize);
        Assert.Equal(0, link.PhysicalSize);
        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("not followed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The link itself is a node, as it is for the portable backend, but nothing behind it is
    /// read. A link pointing at an ancestor is exactly how a bounded walk becomes unbounded.
    /// </summary>
    [Fact]
    public void AReparseDirectoryIsEmittedButItsContentsAreNot()
    {
        FixtureRecordSource source = Volume();
        source.Add(18, new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory | FileAttributes.ReparsePoint)
            .AddFileName(Subdirectory, "loop")
            .Build());

        source.Add(45, File(45, 18, "inside.txt", 4_096, 4_096));

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Contains(collector.Entries, e => e.RelativePath == "loop" && e.Kind == NodeKind.Folder);
        Assert.DoesNotContain(collector.Entries, e => e.RelativePath.Contains("inside", StringComparison.Ordinal));
        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("not followed", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryHardLinkNameBecomesANodeCarryingTheWholeFile()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "first.bin")
            .AddFileName(Subdirectory, "second.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry[] links =
        [
            .. collector.Entries.Where(e =>
                e.RelativePath.Contains("first", StringComparison.Ordinal) ||
                e.RelativePath.Contains("second", StringComparison.Ordinal))
        ];

        Assert.Equal(2, links.Length);
        Assert.All(links, e =>
        {
            Assert.Equal(4_096, e.LogicalSize);
            Assert.Equal(8_192, e.PhysicalSize);
        });

        Assert.Equal(1, collector.HardLinkedFileCount);
    }

    /// <summary>
    /// An entry whose attributes are split across fragment records cannot be reported from its
    /// own record alone. Half of it is worse than none of it.
    /// </summary>
    [Fact]
    public void AnEntryWithSplitAttributesIsDeclinedRatherThanHalfReported()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "split.bin")
            .AddAttributeList()
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A split entry declines wherever it lives. The fragments are not read, and a link held
    /// only by a fragment can sit inside the requested range with nothing in the base record
    /// to say so, so "it is probably outside" is not good enough.
    /// </summary>
    [Fact]
    public void ASplitRecordOutsideTheScanRootStillDeclinesBecauseItsExtensionsMayNameUs()
    {
        FixtureRecordSource source = Volume();
        source.Add(60, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Root, "elsewhere.bin")
            .AddAttributeList()
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A decline must leave the portable backend to produce the tree. The root here is a
    /// temporary directory rather than a fixture volume, so the reason depends on the host: a
    /// drive-letter path it can resolve on Windows, a path shape it declines on the spot
    /// anywhere else. Both must reach the portable scan.
    /// </summary>
    [Fact]
    public async Task ADeclinedSplitEntryLetsThePortableRetryProduceTheTree()
    {
        using TempTree tree = new(nameof(ADeclinedSplitEntryLetsThePortableRetryProduceTheTree));
        tree.MakeFile("real.txt", 64);

        FixtureRecordSource source = Volume();
        source.Add(42, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "split.bin")
            .AddAttributeList()
            .Build());

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: Backend(source))
            .ScanAsync(tree.Root, options: ScanOptions.Default with { Backend = ScanBackendPreference.Auto });

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);
        Assert.False(string.IsNullOrWhiteSpace(result.FallbackReason));
        Assert.Equal(1, result.FileCount);
    }

    /// <summary>
    /// The directory a link names has been reused by something else. Attaching anyway would
    /// move a file under an unrelated tree; the entry is left out and said so.
    /// </summary>
    [Fact]
    public void AStaleParentSequenceIsNotAttachedToTheReusedRecord()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, File(44, Subdirectory, "orphan.txt", 4_096, 4_096, parentSequence: 99));

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.DoesNotContain(collector.Entries, e => e.RelativePath.Contains("orphan", StringComparison.Ordinal));
        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("reused", StringComparison.Ordinal));
        Assert.False(collector.IsComplete);
    }

    /// <summary>
    /// Record numbers carry no hierarchy. A file may sit in a directory whose record number is
    /// higher than its own, which is what happens when a file is moved into a freshly created
    /// folder.
    /// </summary>
    [Fact]
    public void AFileMayLiveInADirectoryWithAHigherRecordNumber()
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 1024, 4096));
        source.Add(Root, RootRecord());
        source.Add(80, Directory(80, Root, "sub", RootSequence));
        source.Add(40, File(40, 80, "moved.txt", 4_096, 4_096));

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Contains(collector.Entries, e => e.RelativePath == "moved.txt" && e.LogicalSize == 4_096);
    }

    [Fact]
    public void ADirectoryReachedThroughAReusedParentIsLeftOutWithItsContents()
    {
        FixtureRecordSource source = Volume();
        source.Add(18, new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Subdirectory, "stale", parentSequence: 99)
            .Build());

        source.Add(45, File(45, 18, "inside.txt", 4_096, 4_096));

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.DoesNotContain(collector.Entries, e => e.RelativePath.Contains("stale", StringComparison.Ordinal));
        Assert.DoesNotContain(collector.Entries, e => e.RelativePath.Contains("inside", StringComparison.Ordinal));
        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("reused parent", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryThatNamesNothingIsDeclinedRatherThanSilentlyLost()
    {
        FixtureRecordSource source = Volume();
        source.Add(18, new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("no name", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVolumeWithoutItsRootRecordIsDeclined()
    {
        FixtureRecordSource source = new(Volume().Geometry);

        foreach ((ulong ordinal, byte[] record) in Volume().Entries)
        {
            if (ordinal != Root)
            {
                source.Add(ordinal, record);
            }
        }

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("root record", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkippingHiddenEntriesExcludesFilesAndDirectoriesWithTheirContents()
    {
        FixtureRecordSource source = Volume();
        source.Add(17, new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory | FileAttributes.Hidden)
            .AddFileName(Subdirectory, "inner")
            .Build());

        ScanOptions hidden = ScanOptions.Default with { SkipAttributes = FileAttributes.Hidden };
        ScanCollector collector = Collector(hidden);

        Assert.True(Backend(source).TryRun(Request(ScanRootPath, hidden), collector, out string? failure), failure);

        Assert.Equal(["a.txt", "b.bin"], Paths(collector));
    }

    /// <summary>
    /// A directory at the depth limit is still recorded as an empty placeholder, exactly as
    /// the portable backend records it. What is missing is what is behind it, and the result
    /// says so.
    /// </summary>
    [Fact]
    public void TheDepthLimitKeepsTheFrontierPlaceholderAndMarksTheResultIncomplete()
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 1024, 4096));
        source.Add(Root, RootRecord());
        source.Add(Subdirectory, Directory(Subdirectory, Root, "sub", RootSequence));
        source.Add(17, Directory(17, Subdirectory, "level1"));
        source.Add(18, Directory(18, 17, "level2"));
        source.Add(19, File(19, 18, "deep.txt", 4_096, 4_096));

        ScanOptions depth = ScanOptions.Default with { MaxDepth = 1 };
        ScanCollector collector = Collector(depth);

        Assert.True(Backend(source).TryRun(Request(ScanRootPath, depth), collector, out string? failure), failure);

        Assert.Equal(["level1", Nested("level1", "level2")], Paths(collector));
        Assert.False(collector.IsComplete);
        Assert.Contains("depth", collector.IncompleteReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEntryBudgetStopsTheWalkInsteadOfDrainingTheTable()
    {
        FixtureRecordSource source = Volume();
        for (ulong ordinal = 100; ordinal < 112; ordinal++)
        {
            source.Add(ordinal, File(ordinal, Subdirectory, $"bulk{ordinal}.txt", 1_024, 1_024));
        }

        ScanCollector collector = Collector(ScanOptions.Default with { MaxEntries = 6 });

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.True(collector.Entries.Count <= 6);
        Assert.False(collector.IsComplete);
        Assert.Contains("budget", collector.IncompleteReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoreDirectoriesThanTheBudgetIsADeclineNotAPartialMap()
    {
        FixtureRecordSource source = Volume();
        for (ulong ordinal = 100; ordinal < 120; ordinal++)
        {
            source.Add(ordinal, Directory(ordinal, Root, $"folder{ordinal}", RootSequence));
        }

        ScanOptions options = ScanOptions.Default with { MaxEntries = 8 };

        Assert.False(Backend(source).TryRun(Request(ScanRootPath, options), Collector(options), out string? failure));
        Assert.Contains("directories", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclinesWhenTheVolumeCannotBeOpened()
    {
        NtfsMftScanBackend backend = new(_ => null, null);

        Assert.False(backend.TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains(@"C:\", failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinesWhenTheRootDoesNotNameALocalVolume()
    {
        Assert.False(Backend(Volume()).TryRun(Request(@"\\server\share\folder"), Collector(), out string? failure));
        Assert.Contains("local drive volume", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclinesWhenTheRootIsNotADirectoryInTheVolume()
    {
        Assert.False(Backend(Volume()).TryRun(Request(@"C:\nowhere"), Collector(), out string? failure));
        Assert.Contains("could not be resolved", failure!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole point of declining: what the fast backend collected is thrown away and the
    /// portable backend really runs, rather than the caller being handed an empty volume.
    /// </summary>
    [Fact]
    public async Task ADeclineLeavesThePortableBackendToProduceTheResult()
    {
        using TempTree tree = new(nameof(ADeclineLeavesThePortableBackendToProduceTheResult));
        tree.MakeFile("real.txt", 77);

        FixtureRecordSource source = Volume();
        source.ForcedOrdinal = 5_000;

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: Backend(source))
            .ScanAsync(tree.Root, options: ScanOptions.Default with { Backend = ScanBackendPreference.Auto });

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);
        Assert.NotNull(result.FallbackReason);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(77, result.TotalLogicalSize);
    }

    /// <summary>
    /// The fast backend's own result, through the public scanner. Windows-only because the
    /// scanner normalises the root with the host's own path rules, and only a Windows host
    /// keeps <c>C:\</c> a drive root the backend will accept.
    /// </summary>
    [NtfsWindowsOnlyFact]
    public async Task ASuccessfulFastScanIsUsedWithoutThePortableRetry()
    {
        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: Backend(Volume()))
            .ScanAsync(VolumeRootPath, options: ScanOptions.Default with { Backend = ScanBackendPreference.Auto });

        Assert.Equal(ScanBackend.NtfsMft, result.Backend);
        Assert.Null(result.FallbackFrom);
        Assert.True(result.TotalLogicalSize > 0);
    }

    private static NtfsMftScanBackend Backend(FixtureRecordSource source, NtfsSizeQuery? query = null)
        => new(_ => source, query ?? ThrowingQuery());

    private static NtfsSizeQuery ThrowingQuery()
        => static (string path, out long logical, out long allocated, out DateTimeOffset? lastWrite)
            => throw new InvalidOperationException($"The metadata query must not be used here: {path}");

    private static ScanCollector Collector(ScanOptions? options = null)
        => new(options ?? ScanOptions.Default, progress: null);

    private static ScanRequest Request(string rootPath, ScanOptions? options = null)
        => new(rootPath, options ?? ScanOptions.Default, CancellationToken.None);

    /// <summary>Joins path segments the way the host does, which is how entries are built.</summary>
    private static string Nested(string parent, string child) => Path.Combine(parent, child);

    private static List<string> Paths(ScanCollector collector)
        => [.. collector.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)];

    /// <summary>The root, one folder, a folder inside it, and a few files.</summary>
    private static FixtureRecordSource Volume()
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 1024, 4096));

        source.Add(Root, RootRecord());
        source.Add(Subdirectory, Directory(Subdirectory, Root, "sub", RootSequence));
        source.Add(InnerDirectory, Directory(InnerDirectory, Subdirectory, "inner"));
        source.Add(41, File(41, InnerDirectory, "inner.txt", 128, 128));
        source.Add(42, File(42, Subdirectory, "a.txt", 1_000, 1_024));
        source.Add(43, File(43, Subdirectory, "b.bin", 2_000, 4_096));
        source.Add(60, File(60, Root, "on-the-root.txt", 5_000, 5_120, RootSequence));

        return source;
    }

    private static byte[] RootRecord() => new MftRecordBuilder()
        .AsDirectory()
        .Sequence(RootSequence)
        .AddStandardInformation(FileAttributes.Directory)
        .AddFileName(Root, ".", MftNameNamespace.Posix)
        .Build();

    private static byte[] Directory(ulong ordinal, ulong parent, string name, ushort? parentSequence = null)
        => new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(parent, name, parentSequence: parentSequence ?? RecordSequence)
            .Build();

    private static byte[] File(ulong ordinal, ulong parent, string name, long size, long allocated, ushort? parentSequence = null)
        => new MftRecordBuilder()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(parent, name, parentSequence: parentSequence ?? RecordSequence)
            .AddDataNonResident(dataSize: size, allocatedSize: allocated)
            .Build();
}

/// <summary>
/// The volume-root split the MFT backend resolves every scan root through. Host-independent:
/// it shapes the string only, so forward slashes must split before any segment is read.
/// </summary>
public class NtfsPathTests
{
    [Fact]
    public void ForwardSlashesSplitIntoSeparateSegments()
    {
        Assert.True(NtfsPath.TrySplit("C:/sub/inner", out string volumeRoot, out string[] segments));
        Assert.Equal(@"C:\", volumeRoot);
        Assert.Equal(["sub", "inner"], segments);
    }

    [Fact]
    public void MixedAndRepeatedSeparatorsCollapseToOneSegmentEach()
    {
        Assert.True(NtfsPath.TrySplit(@"C:\sub//inner\\\file.txt", out string volumeRoot, out string[] segments));
        Assert.Equal(@"C:\", volumeRoot);
        Assert.Equal(["sub", "inner", "file.txt"], segments);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("C:sub")]
    [InlineData("C:foo/bar")]
    public void DriveRelativePathsDeclineWithEmptyOuts(string path)
    {
        Assert.False(NtfsPath.TrySplit(path, out string volumeRoot, out string[] segments));
        Assert.Equal(string.Empty, volumeRoot);
        Assert.Empty(segments);
    }

    [Theory]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\.\C:")]
    [InlineData(@"\\?\C:\sub")]
    [InlineData("")]
    public void NonDrivePathsDeclineWithEmptyOuts(string path)
    {
        Assert.False(NtfsPath.TrySplit(path, out string volumeRoot, out string[] segments));
        Assert.Equal(string.Empty, volumeRoot);
        Assert.Empty(segments);
    }

    [Fact]
    public void DotSegmentsResolveAndClimbingAboveTheRootDeclines()
    {
        Assert.True(NtfsPath.TrySplit(@"C:\sub\.\inner", out _, out string[] dotted));
        Assert.Equal(["sub", "inner"], dotted);

        Assert.True(NtfsPath.TrySplit(@"C:\sub\..\other", out _, out string[] parented));
        Assert.Equal(["other"], parented);

        Assert.False(NtfsPath.TrySplit(@"C:\..\evil", out string volumeRoot, out string[] segments));
        Assert.Equal(string.Empty, volumeRoot);
        Assert.Empty(segments);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:/")]
    public void AVolumeRootAloneYieldsNoSegments(string path)
    {
        Assert.True(NtfsPath.TrySplit(path, out string volumeRoot, out string[] segments));
        Assert.Equal(@"C:\", volumeRoot);
        Assert.Empty(segments);
    }
}