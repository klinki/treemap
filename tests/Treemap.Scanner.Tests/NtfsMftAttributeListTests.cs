using System.Buffers.Binary;
using Treemap;
using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Files whose metadata is split across MFT records: the base names an attribute list,
/// and fragments carry the names, sizes, links and directory metadata the base lacks.
/// </summary>
/// <remarks>
/// Every case here drives the production backend through a fixture volume: the real
/// downward walk, the real parser, the real targeted fragment reads and the real
/// directory map. Only the volume handle and the ordinary file query are substituted.
/// Record ordinals are 16 or above throughout (0 to 15 are reserved and stepped over;
/// record 5 is the volume root). A fragment's FILE_NAME states its parent's sequence
/// explicitly, because the builder otherwise defaults it to the fragment's own.
/// </remarks>
public class NtfsMftExtensionResolutionTests
{
    private const ulong Root = 5;
    private const ulong Subdirectory = 16;
    private const ulong InnerDirectory = 17;
    private const ushort RootSequence = 5;
    private const ushort RecordSequence = 1;
    private const string ScanRootPath = @"C:\sub";

    private const uint TypeStandardInformation = 0x10;
    private const uint TypeAttributeList = 0x20;
    private const uint TypeFileName = 0x30;
    private const uint TypeData = 0x80;

    [Fact]
    public void ANameHeldOnlyByAFragmentIsEmittedWithItsSize()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "split.txt", parentSequence: RecordSequence)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry entry = Assert.Single(collector.Entries, e => e.RelativePath == "split.txt");
        Assert.Equal(4_096, entry.LogicalSize);
        Assert.Equal(8_192, entry.PhysicalSize);
    }

    [Fact]
    public void HardLinksSplitAcrossRecordsBecomeOneNodePerName()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "first.bin", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(InnerDirectory, "second.bin", parentSequence: RecordSequence)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Contains(collector.Entries, e => e.RelativePath == "first.bin" && e.LogicalSize == 4_096);
        Assert.Contains(collector.Entries, e => e.RelativePath == Path.Combine("inner", "second.bin") && e.LogicalSize == 4_096);
        Assert.Equal(1, collector.HardLinkedFileCount);
    }

    [Fact]
    public void OnlyTheVcnZeroDataSegmentStatesTheSize()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "big.bin", parentSequence: RecordSequence)
            .AddAttributeListEntry(TypeData, refOrdinal: 100, refSequence: 9, attributeId: 0, lowestVcn: 0)
            .AddAttributeListEntry(TypeData, refOrdinal: 101, refSequence: 4, attributeId: 0, lowestVcn: 8)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddDataNonResident(dataSize: 5_000, allocatedSize: 8_192, lowestVcn: 0)
            .Build());
        source.Add(101, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(4)
            .AddDataNonResident(dataSize: 99_999, allocatedSize: 99_999, lowestVcn: 8)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        // The continuation neither overwrites the VCN-zero size nor adds to it.
        ScanEntry entry = Assert.Single(collector.Entries, e => e.RelativePath == "big.bin");
        Assert.Equal(5_000, entry.LogicalSize);
        Assert.Equal(8_192, entry.PhysicalSize);
    }

    [Fact]
    public void TimestampsFoldFromAFragment()
    {
        DateTimeOffset lastWrite = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "timed.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 128, allocatedSize: 128)
            .AddAttributeListEntry(TypeStandardInformation, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddStandardInformation(FileAttributes.Normal, lastWrite)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry entry = Assert.Single(collector.Entries, e => e.RelativePath == "timed.txt");
        Assert.Equal(lastWrite, entry.LastWriteTime);
    }

    [Fact]
    public void AttributesFoldFromAFragmentForSkipping()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "hidden.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeStandardInformation, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddStandardInformation(FileAttributes.Hidden)
            .Build());

        ScanOptions hidden = ScanOptions.Default with { SkipAttributes = FileAttributes.Hidden };
        ScanCollector collector = Collector(hidden);

        Assert.True(Backend(source).TryRun(Request(ScanRootPath, hidden), collector, out string? failure), failure);

        Assert.DoesNotContain(collector.Entries, e => e.RelativePath.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryNamedOnlyByAFragmentIsPlaced()
    {
        FixtureRecordSource source = Volume();
        source.Add(18, new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(18, RecordSequence)
            .Sequence(9)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Subdirectory, "extdir", parentSequence: RecordSequence)
            .Build());
        source.Add(45, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(18, "inside.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 128, allocatedSize: 128)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Contains(collector.Entries, e => e.RelativePath == "extdir" && e.Kind == NodeKind.Folder);
        Assert.Contains(collector.Entries, e => e.RelativePath == Path.Combine("extdir", "inside.txt"));
    }

    [Fact]
    public void AFragmentOwnedByAnotherFileDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "owned.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(45, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "owned.txt", parentSequence: RecordSequence)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStaleFragmentSequenceDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "stale.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(3)
            .AddFileName(Subdirectory, "stale.txt", parentSequence: RecordSequence)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANearestLowerRecordIsNotTheLinkedMetadata()
    {
        // Nothing lives at 9999, so the source answers with the highest record below it.
        // That record is not the fragment, and must not be mistaken for it.
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "lower.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeData, refOrdinal: 9999, refSequence: 1, attributeId: 0)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADeletedFragmentDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "gone.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .InUse(false)
            .AddFileName(Subdirectory, "gone.txt", parentSequence: RecordSequence)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATargetThatIsNotAFragmentDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "plain.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeData, refOrdinal: 43, refSequence: RecordSequence, attributeId: 0)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAttributeIdentityMismatchDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "identity.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 7)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "identity.txt", parentSequence: RecordSequence)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateReferencesAndABackReferenceResolveOnce()
    {
        // The base list names the fragment twice (exact duplicates are idempotent)
        // and names its own DATA segment once (a base self-reference). The fragment
        // itself holds no list: lists always live in the base record.
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 256, allocatedSize: 256)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .AddAttributeListEntry(TypeData, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .AddAttributeListEntry(TypeData, refOrdinal: 44, refSequence: RecordSequence, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "cycle.bin", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 256, allocatedSize: 256)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        // One logical file: the shared fragment is read once and never emitted alone.
        // The volume's own three files and inner folder are untouched.
        Assert.Single(collector.Entries, e => e.RelativePath == "cycle.bin");
        Assert.Equal(5, collector.Entries.Count);
    }

    [Fact]
    public void AMalformedListValueDeclines()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "bad.bin", parentSequence: RecordSequence)
            .AddAttributeListRaw([0x20, 0x00, 0x00, 0x00, 0x05, 0x00])
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void AFragmentBelowItsBaseResolvesRegardlessOfWalkOrder()
    {
        FixtureRecordSource source = Volume();
        source.Add(100, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 1_024, allocatedSize: 1_024)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 40, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(40, new MftRecordBuilder()
            .AsFragmentOf(100, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "low.bin", parentSequence: RecordSequence)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Single(collector.Entries, e => e.RelativePath == "low.bin");
        Assert.Equal(5, collector.Entries.Count);
    }

    [Fact]
    public void AListSplitAcrossTwoFragmentsDeclinesAsImpossible()
    {
        // ATTRIBUTE_LIST never leaves its base record: its mapping array fits the
        // base MFT record. A base list naming ATTRIBUTE_LIST in a fragment, and a
        // fragment holding its own list, is not a shape real volumes produce.
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 1_000, allocatedSize: 1_024)
            .AddAttributeListEntry(TypeAttributeList, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 101, refSequence: 4, attributeId: 0)
            .Build());
        source.Add(101, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(4)
            .AddFileName(Subdirectory, "deep.bin", parentSequence: RecordSequence)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("ATTRIBUTE_LIST", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMultipleOrdinaryFragmentsResolveTogether()
    {
        // A real split: one fragment holds a second hard-link name, another holds
        // the VCN-zero DATA size, and a third holds a continuation whose undefined
        // sizes never overwrite it. Named streams never contribute.
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "first.bin", parentSequence: RecordSequence)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 1)
            .AddAttributeListEntry(TypeData, refOrdinal: 101, refSequence: 4, attributeId: 0, lowestVcn: 0)
            .AddAttributeListEntry(TypeData, refOrdinal: 102, refSequence: 5, attributeId: 0, lowestVcn: 8)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .WithAttributeInstance(1)
            .AddFileName(InnerDirectory, "second.bin", parentSequence: RecordSequence)
            .Build());
        source.Add(101, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(4)
            .AddDataNonResident(dataSize: 5_000, allocatedSize: 8_192, lowestVcn: 0)
            .Build());
        source.Add(102, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(5)
            .AddDataNonResident(dataSize: 9_000, allocatedSize: 9_000, lowestVcn: 8)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Contains(collector.Entries, e => e.RelativePath == "first.bin" && e.LogicalSize == 5_000);
        Assert.Contains(collector.Entries, e => e.RelativePath == Path.Combine("inner", "second.bin") && e.LogicalSize == 5_000);
        Assert.Equal(1, collector.HardLinkedFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AContinuationWithUndefinedSizesIsIgnored(bool fixupApplied)
    {
        // Continuation DATA sizes are undefined (often all-ones on disk). The VCN-zero
        // size states the file length; the continuation validates by identity only.
        FixtureRecordSource source = new(new MftGeometry(4096, 4096, 1024, 4096))
        {
            Representation = fixupApplied ? MftRecordRepresentation.FixupApplied : MftRecordRepresentation.Raw,
        };
        AddVolumeRoots(source);

        MftRecordBuilder baseBuilder = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "grown.bin", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 5_000, allocatedSize: 8_192, lowestVcn: 0)
            .AddAttributeListEntry(TypeData, refOrdinal: 101, refSequence: 4, attributeId: 0, lowestVcn: 8);
        MftRecordBuilder fragmentBuilder = new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(4)
            .AddOversizedNonResidentData(ulong.MaxValue, lowestVcn: 8);

        source.Add(44, fixupApplied ? baseBuilder.BuildFixupApplied() : baseBuilder.Build());
        source.Add(101, fixupApplied ? fragmentBuilder.BuildFixupApplied() : fragmentBuilder.Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry entry = Assert.Single(collector.Entries, e => e.RelativePath == "grown.bin");
        Assert.Equal(5_000, entry.LogicalSize);
        Assert.Equal(8_192, entry.PhysicalSize);
    }

    [Fact]
    public void ANonResidentListResolvesThroughVolumeClusters()
    {
        const long listLcn = 10;
        const uint clusterSize = 4096;
        byte[] listValue = MftRecordBuilder.EncodeListEntries(
            [(TypeData, 100UL, (ushort)9, (ushort)0, string.Empty, 0UL)]);

        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "nc.bin", parentSequence: RecordSequence)
            .AddAttributeListNonResident(listValue.Length, (listLcn, 1))
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddDataNonResident(dataSize: 7_000, allocatedSize: 8_192)
            .Build());

        byte[] cluster = new byte[clusterSize];
        listValue.CopyTo(cluster, 0);
        source.AddVolumeData(listLcn * clusterSize, cluster);

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        ScanEntry entry = Assert.Single(collector.Entries, e => e.RelativePath == "nc.bin");
        Assert.Equal(7_000, entry.LogicalSize);
        Assert.Equal(8_192, entry.PhysicalSize);
    }

    [Fact]
    public void ANonResidentListWithoutVolumeBytesDeclines()
    {
        byte[] listValue = MftRecordBuilder.EncodeListEntries(
            [(TypeData, 100UL, (ushort)9, (ushort)0, string.Empty, 0UL)]);

        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "nc-missing.bin", parentSequence: RecordSequence)
            .AddAttributeListNonResident(listValue.Length, (10, 1))
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddDataNonResident(dataSize: 7_000, allocatedSize: 8_192)
            .Build());

        Assert.False(Backend(source).TryRun(Request(ScanRootPath), Collector(), out string? failure));
        Assert.Contains("fragment", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancellationDuringFragmentFollowThrowsRatherThanFallingBack()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "cancelled.txt", parentSequence: RecordSequence)
            .Build());

        MftRecordInfo baseRecord = new();
        Assert.True(MftRecordParser.TryParse(44, source.Entries.Single(e => e.Key == 44).Value, baseRecord, out string? parseFailure), parseFailure);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MftLogicalFileResolver.TryResolve(baseRecord, source, cts.Token, new MftRecordInfo(), out _));
    }

    [Fact]
    public void AFixupAppliedVolumeResolvesFragments()
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 4096, 1024, 4096))
        {
            Representation = MftRecordRepresentation.FixupApplied,
        };
        AddVolumeRoots(source);
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .BuildFixupApplied());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .AddFileName(Subdirectory, "fixup.bin", parentSequence: RecordSequence)
            .BuildFixupApplied());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Single(collector.Entries, e => e.RelativePath == "fixup.bin");
        Assert.Equal(5, collector.Entries.Count);
    }

    [Fact]
    public void AResolvableSplitOutsideTheScanRootSucceeds()
    {
        FixtureRecordSource source = Volume();
        source.Add(60, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddDataNonResident(dataSize: 5_000, allocatedSize: 5_120)
            .AddAttributeListEntry(TypeFileName, refOrdinal: 100, refSequence: 9, attributeId: 0)
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(60, RecordSequence)
            .Sequence(9)
            .AddFileName(Root, "on-the-root.txt", parentSequence: RootSequence)
            .Build());

        ScanCollector collector = Collector();

        Assert.True(Backend(source).TryRun(Request(ScanRootPath), collector, out string? failure), failure);

        Assert.Equal(["a.txt", "b.bin", "inner", Path.Combine("inner", "inner.txt")], Paths(collector));
    }

    [Fact]
    public void ANamedStreamOnlyFragmentReportsThroughTheOrdinaryQuery()
    {
        FixtureRecordSource source = Volume();
        source.Add(44, new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "ads.bin", parentSequence: RecordSequence)
            .AddAttributeListEntry(TypeData, refOrdinal: 100, refSequence: 9, attributeId: 5, attributeName: "payload")
            .Build());
        source.Add(100, new MftRecordBuilder()
            .AsFragmentOf(44, RecordSequence)
            .Sequence(9)
            .WithAttributeInstance(5)
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

        Assert.Contains(collector.Entries, e => e.RelativePath == "ads.bin");
        Assert.Contains(collector.Diagnostics, d => d.Message.Contains("no readable unnamed DATA", StringComparison.Ordinal));
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

    private static List<string> Paths(ScanCollector collector)
        => [.. collector.Entries.Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)];

    /// <summary>The root, one folder, a folder inside it, and a few files.</summary>
    private static FixtureRecordSource Volume()
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 4096, 1024, 4096));
        AddVolumeRoots(source);
        return source;
    }

    private static void AddVolumeRoots(FixtureRecordSource source)
    {
        source.Add(Root, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RootSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Root, ".", MftNameNamespace.Posix)
            .Build());
        source.Add(Subdirectory, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Root, "sub", parentSequence: RootSequence)
            .Build());
        source.Add(InnerDirectory, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Subdirectory, "inner", parentSequence: RecordSequence)
            .Build());
        source.Add(41, new MftRecordBuilder()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(InnerDirectory, "inner.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 128, allocatedSize: 128)
            .Build());
        source.Add(42, new MftRecordBuilder()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "a.txt", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 1_000, allocatedSize: 1_024)
            .Build());
        source.Add(43, new MftRecordBuilder()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(Subdirectory, "b.bin", parentSequence: RecordSequence)
            .AddDataNonResident(dataSize: 2_000, allocatedSize: 4_096)
            .Build());
    }
}

/// <summary>The attribute-list value and data-run codecs, without any volume.</summary>
public class NtfsMftAttributeListParserTests
{
    private const ulong Ordinal = 42;

    [Fact]
    public void ResidentEntriesRoundTrip()
    {
        byte[] value = MftRecordBuilder.EncodeListEntries(
            [
                (0x30u, 100UL, (ushort)9, (ushort)0, string.Empty, 0UL),
                (0x80u, 101UL, (ushort)4, (ushort)2, "payload", 8UL),
            ]);

        List<MftAttributeListEntry> entries = [];
        Assert.True(MftAttributeList.TryParseEntries(value, Ordinal, entries, out string? failure), failure);

        Assert.Equal(2, entries.Count);
        Assert.Equal(0x30u, entries[0].AttributeType);
        Assert.Equal(100UL, entries[0].ReferencedOrdinal);
        Assert.Equal(9, entries[0].ReferencedSequence);
        Assert.Equal(0, entries[0].AttributeId);
        Assert.Equal(101UL, entries[1].ReferencedOrdinal);
        Assert.Equal("payload", entries[1].AttributeName);
        Assert.Equal(8UL, entries[1].LowestVcn);
        Assert.Equal(2, entries[1].AttributeId);
    }

    [Fact]
    public void ATruncatedEntryRejectsTheValue()
    {
        List<MftAttributeListEntry> entries = [];
        Assert.False(MftAttributeList.TryParseEntries([0x20, 0x00, 0x00, 0x00, 0x05, 0x00], Ordinal, entries, out string? failure));
        Assert.Contains("shorter than", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEntryLengthPastTheValueRejectsIt()
    {
        byte[] value = MftRecordBuilder.EncodeListEntries([(0x30u, 100UL, (ushort)9, (ushort)0, string.Empty, 0UL)]);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(4), 0x100);

        List<MftAttributeListEntry> entries = [];
        Assert.False(MftAttributeList.TryParseEntries(value, Ordinal, entries, out string? failure));
        Assert.Contains("outside", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyListValueRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddAttributeListRaw([])
            .Build();

        MftRecordInfo info = new();
        Assert.False(MftRecordParser.TryParse(Ordinal, record, info, out string? failure));
        Assert.Contains("empty", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttributeIdentitiesAreRecordedForValidation()
    {
        byte[] record = new MftRecordBuilder()
            .WithAttributeInstance(7)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(5, "identity.bin")
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .Build();

        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(Ordinal, record, info, out string? failure), failure);

        Assert.Contains(info.AttributeIdentities, i => i.AttributeType == 0x10 && i.AttributeId == 7 && !i.IsNonResident);
        Assert.Contains(info.AttributeIdentities, i => i.AttributeType == 0x30 && i.AttributeId == 0 && !i.IsNonResident);
        Assert.Contains(info.AttributeIdentities, i => i.AttributeType == 0x80 && i.AttributeId == 0 && i.IsNonResident);
    }

    [Fact]
    public void DataRunsDecodeWithDeltasAndHoles()
    {
        byte[] pairs = MftRecordBuilder.EncodeMappingPairs([(100, 4), (-1, 3), (120, 2)]);

        List<MftDataRuns.Run> runs = [];
        Assert.True(MftDataRuns.TryDecode(pairs, Ordinal, runs, out string? failure), failure);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new MftDataRuns.Run(100, 4), runs[0]);
        Assert.Equal(new MftDataRuns.Run(-1, 3), runs[1]);
        Assert.Equal(new MftDataRuns.Run(120, 2), runs[2]);
    }

    [Fact]
    public void AnUnterminatedRunListIsRejected()
    {
        List<MftDataRuns.Run> runs = [];
        Assert.False(MftDataRuns.TryDecode([0x11, 0x04, 0x05], Ordinal, runs, out string? failure));
        Assert.Contains("never terminates", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARunCoveringNoClustersIsRejected()
    {
        byte[] pairs = MftRecordBuilder.EncodeMappingPairs([(100, 0)]);

        List<MftDataRuns.Run> runs = [];
        Assert.False(MftDataRuns.TryDecode(pairs, Ordinal, runs, out string? failure));
        Assert.Contains("no clusters", failure!, StringComparison.OrdinalIgnoreCase);
    }
}
