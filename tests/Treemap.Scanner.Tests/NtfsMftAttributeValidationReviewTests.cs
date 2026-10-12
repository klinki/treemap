using System.Buffers.Binary;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

public class NtfsMftAttributeValidationReviewTests
{
    private const ulong Base = 44;
    private const ulong Fragment = 100;
    private const ushort BaseSequence = 1;
    private const ushort FragmentSequence = 9;
    private const uint FileName = 0x30;
    private const uint Data = 0x80;

    [Fact]
    public void AnUnnamedListEntryCanHaveZeroNameOffset()
    {
        byte[] entry = Entry();
        entry[7] = 0;

        Assert.True(MftAttributeList.TryParseEntries(entry, Base, [], out string? failure), failure);
    }

    [Theory]
    [InlineData((ushort)10, (ushort)0)]
    [InlineData(FragmentSequence, (ushort)7)]
    public void EveryReferenceToASharedFragmentMustValidate(ushort secondSequence, ushort secondInstance)
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .AddAttributeListEntry(FileName, Fragment, secondSequence, secondInstance)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "listed.txt", parentSequence: 1).Build());

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void AReferenceToTheBaseMustMatchAnActualAttribute()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddAttributeListEntry(FileName, Base, BaseSequence, 7).Build());
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void AVcnZeroReferenceCannotMatchAContinuation()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0, lowestVcn: 0).Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddDataNonResident(dataSize: 4096, allocatedSize: 4096, lowestVcn: 1).Build());

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void UnlistedFragmentNamesAreNotMerged()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddAttributeListEntry(FileName, Base, BaseSequence, 0)
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0).Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddDataResident(new byte[4])
            .WithAttributeInstance(1).AddFileName(5, "unlisted.txt", parentSequence: 1).Build());
        MftRecordInfo merged = new();

        Assert.True(MftLogicalFileResolver.TryResolve(record, source, default, merged, out string? failure), failure);
        Assert.DoesNotContain(merged.Names, name => name.Name == "unlisted.txt");
        Assert.Equal(4, merged.LogicalSize);
    }

    [Fact]
    public void GappedListSegmentsAreNotConcatenatedAsContiguousData()
    {
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 0, HasFileSize = true, FileSize = 8192,
            MappingPairs = [0x11, 1, 10, 0],
        });
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 2, HighestVcn = 2,
            MappingPairs = [0x11, 1, 20, 0],
        });
        byte[] cluster = new byte[4096];
        for (int offset = 0; offset < cluster.Length; offset += 32)
        {
            Entry().CopyTo(cluster, offset);
        }
        using FixtureRecordSource source = Source();
        source.AddVolumeData(10 * 4096, cluster);
        source.AddVolumeData(20 * 4096, cluster);

        Assert.False(MftLogicalFileResolver.TryReadListValue(record, source, default, [], out _));
    }

    [Fact]
    public void OverlappingListSegmentsDecline()
    {
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 1, HasFileSize = true, FileSize = 64,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(10, 2)]),
        });
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 1, HighestVcn = 1,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(20, 1)]),
        });
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryReadListValue(record, source, default, [], out _));
    }

    [Fact]
    public void DuplicateListSegmentsDecline()
    {
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 0, HasFileSize = true, FileSize = 64,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(10, 1)]),
        });
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 0,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(20, 1)]),
        });
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryReadListValue(record, source, default, [], out _));
    }

    [Fact]
    public void ListSegmentHighestMustMatchItsRuns()
    {
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 5, HasFileSize = true, FileSize = 64,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(10, 1)]),
        });
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryReadListValue(record, source, default, [], out _));
    }

    [Fact]
    public void ListRunsPastTheVolumeDecline()
    {
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 0, HasFileSize = true, FileSize = 32,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(500, 1)]),
        });
        using FixtureRecordSource source = new(new MftGeometry(512, 4096, 1024, 512, TotalClusters: 100, NumberSectors: 800));

        Assert.False(MftLogicalFileResolver.TryReadListValue(record, source, default, [], out _));
    }

    [Fact]
    public void UnlistedStdFlagsAreNotMerged()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddStandardInformation(FileAttributes.Normal)
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddStandardInformation(FileAttributes.Hidden)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .Build());
        MftRecordInfo merged = new();

        Assert.True(MftLogicalFileResolver.TryResolve(record, source, default, merged, out string? failure), failure);
        Assert.False((merged.Attributes & FileAttributes.Hidden) != 0);
        Assert.Equal(64, merged.LogicalSize);
    }

    [Fact]
    public void UnlistedDataSizesAreNotMerged()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddStandardInformation(FileAttributes.Normal)
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "listed.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 9999, allocatedSize: 9999)
            .Build());
        MftRecordInfo merged = new();

        Assert.True(MftLogicalFileResolver.TryResolve(record, source, default, merged, out string? failure), failure);
        Assert.Contains(merged.Names, name => name.Name == "listed.txt");
        Assert.False(merged.HasUnnamedData);
    }

    [Fact]
    public void ConflictingUnnamedVcnZeroSizesDecline()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 100, allocatedSize: 100)
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0, lowestVcn: 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddDataNonResident(dataSize: 9999, allocatedSize: 9999, lowestVcn: 0)
            .Build());

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void SameReferenceTwiceIsIdempotent()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0)
            .AddAttributeListEntry(Data, Fragment, FragmentSequence, 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .WithAttributeInstance(0).AddFileName(5, "dup.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .Build());
        MftRecordInfo merged = new();

        Assert.True(MftLogicalFileResolver.TryResolve(record, source, default, merged, out string? failure), failure);
        Assert.Single(merged.Names, name => name.Name == "dup.txt");
        Assert.Equal(64, merged.LogicalSize);
    }

    [Fact]
    public void OutOfRangeReferencesDeclineBeforeIo()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddAttributeListEntry(FileName, 99999, 1, 0)
            .Build());
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
        Assert.Empty(source.Requested);
    }

    [Fact]
    public void OwnerChecksApplyToAlreadyCachedFragments()
    {
        // Two entries share fragment 100; the second entry's sequence is checked even
        // though the record is already cached from the first.
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .AddAttributeListEntry(Data, Fragment, 7, 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "listed.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .Build());

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void FragmentStandardTimeTakesPrecedenceOverFileNameFallback()
    {
        DateTimeOffset stdTime = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        MftRecordInfo baseWithFallback = new();
        Assert.True(MftRecordParser.TryParse(Base, new MftRecordBuilder()
            .AddFileName(5, "timed.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 128, allocatedSize: 128)
            .Build(), baseWithFallback, out string? baseFailure), baseFailure);

        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddStandardInformation(FileAttributes.Normal, stdTime)
            .Build());

        // Base carries no STD; give it a list entry so the fragment STD is selected.
        MftRecordInfo listed = new();
        Assert.True(MftRecordParser.TryParse(Base, new MftRecordBuilder()
            .AddFileName(5, "timed.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 128, allocatedSize: 128)
            .AddAttributeListEntry(0x10, Fragment, FragmentSequence, 0)
            .Build(), listed, out string? listedFailure), listedFailure);

        MftRecordInfo merged = new();
        Assert.True(MftLogicalFileResolver.TryResolve(listed, source, default, merged, out string? failure), failure);
        Assert.Equal(stdTime, merged.LastWriteTime);
    }

    [Fact]
    public void GeometrySupportsTwoMegabyteClusters()
    {
        byte[] volume = MftGeometryFixtures.Volume(bytesPerCluster: 2 * 1024 * 1024, bytesPerSector: 4096);
        Assert.True(MftGeometry.TryParse(volume, out MftGeometry geometry, out string? failure), failure);
        Assert.Equal(2u * 1024u * 1024u, geometry.BytesPerCluster);
    }

    [Fact]
    public void CancellationDuringListVolumeReadsThrows()
    {
        byte[] listValue = MftRecordBuilder.EncodeListEntries([(Data, Fragment, FragmentSequence, (ushort)0, string.Empty, 0UL)]);
        MftRecordInfo record = new();
        record.Reset(Base);
        record.AttributeListSegments.Add(new()
        {
            LowestVcn = 0, HighestVcn = 0, HasFileSize = true, FileSize = (ulong)listValue.Length,
            MappingPairs = MftRecordBuilder.EncodeMappingPairs([(10, 1)]),
        });
        byte[] cluster = new byte[4096];
        listValue.CopyTo(cluster, 0);
        using FixtureRecordSource source = Source();
        source.AddVolumeData(10 * 4096, cluster);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MftLogicalFileResolver.TryReadListValue(record, source, cts.Token, [], out _));
    }

    [Fact]
    public void GenuineSplitListAcrossTwoRecordsDeclinesAsImpossible()
    {
        // ATTRIBUTE_LIST never leaves its base record: when non-resident its mapping
        // array fits the base MFT record. A prefix entry naming a continuation record
        // holding the rest of the list is not a shape real volumes produce, so the
        // base-mapped read declines instead of bootstrapping a hierarchy.
        const uint clusterSize = 4096;
        const ulong continuation = 102;
        const ushort continuationSequence = 4;
        const long listLcn0 = 10;
        const long listLcn1 = 20;

        // Build a list value larger than one cluster so the prefix ends within an
        // entry: 100 unnamed 32-byte entries plus 30 named 40-byte entries.
        List<(uint type, ulong refOrdinal, ushort refSequence, ushort attributeId, string attributeName, ulong lowestVcn)> all = [];
        for (int i = 0; i < 100; i++)
        {
            all.Add((Data, Fragment, FragmentSequence, (ushort)0, string.Empty, 0UL));
        }

        for (int i = 0; i < 30; i++)
        {
            all.Add((FileName, Fragment, FragmentSequence, (ushort)0, "stream-" + i.ToString("D3"), 0UL));
        }

        // The continuation entry itself lives in the first cluster so the prefix
        // bootstraps the second segment.
        all.Insert(10, (0x20u, continuation, continuationSequence, (ushort)0, string.Empty, 1UL));
        byte[] fullValue = MftRecordBuilder.EncodeListEntries(all);
        Assert.True(fullValue.Length > clusterSize);

        using FixtureRecordSource source = Source();
        source.Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "split-list.txt", parentSequence: 1)
            .AddDataNonResident(dataSize: 64, allocatedSize: 64)
            .Build());

        // Reordered extension ordinals: continuation ordinal is below the fragment
        // to prove VCN order, not ordinal order, drives the read.
        source.Add(continuation, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(continuationSequence)
            .AddAttributeListNonResidentSegment(1, 1, null, (listLcn1, 1))
            .Build());

        MftRecordInfo baseRecord = Parse(new MftRecordBuilder()
            .AddAttributeListNonResidentSegment(0, 0, fullValue.Length, (listLcn0, 1))
            .Build());

        byte[] cluster0 = new byte[clusterSize];
        byte[] cluster1 = new byte[clusterSize];
        Array.Copy(fullValue, 0, cluster0, 0, (int)clusterSize);
        Array.Copy(fullValue, clusterSize, cluster1, 0, fullValue.Length - (int)clusterSize);
        source.AddVolumeData(listLcn0 * clusterSize, cluster0);
        source.AddVolumeData(listLcn1 * clusterSize, cluster1);

        List<MftAttributeListEntry> entries = [];
        Assert.False(MftLogicalFileResolver.TryReadListValue(baseRecord, source, default, entries, out _));
    }

    [Fact]
    public void AListAcrossNoncontiguousRunsWithACrossingEntryResolves()
    {
        // The legitimate multi-cluster shape: one base-mapped value across two
        // noncontiguous runs, with an entry straddling the cluster boundary.
        const uint clusterSize = 4096;
        List<(uint type, ulong refOrdinal, ushort refSequence, ushort attributeId, string attributeName, ulong lowestVcn)> all = [];
        for (int i = 0; i < 127; i++)
        {
            all.Add((Data, Fragment, FragmentSequence, (ushort)0, string.Empty, 0UL));
        }

        all.Add((FileName, Fragment, FragmentSequence, (ushort)0, "crossing-name", 0UL));
        for (int i = 0; i < 20; i++)
        {
            all.Add((Data, Fragment, FragmentSequence, (ushort)0, string.Empty, 0UL));
        }

        byte[] fullValue = MftRecordBuilder.EncodeListEntries(all);
        Assert.True(fullValue.Length > clusterSize);

        using FixtureRecordSource source = Source();
        MftRecordInfo baseRecord = Parse(new MftRecordBuilder()
            .AddAttributeListNonResidentSegment(0, 1, fullValue.Length, (10, 1), (20, 1))
            .Build());

        byte[] cluster0 = new byte[clusterSize];
        byte[] cluster1 = new byte[clusterSize];
        Array.Copy(fullValue, 0, cluster0, 0, (int)clusterSize);
        Array.Copy(fullValue, clusterSize, cluster1, 0, fullValue.Length - (int)clusterSize);
        source.AddVolumeData(10 * clusterSize, cluster0);
        source.AddVolumeData(20 * clusterSize, cluster1);

        List<MftAttributeListEntry> entries = [];
        Assert.True(MftLogicalFileResolver.TryReadListValue(baseRecord, source, default, entries, out string? failure), failure);
        Assert.Equal(all.Count, entries.Count);
        Assert.Equal("crossing-name", entries[127].AttributeName);
    }

    [Fact]
    public void AFileNameInstanceSelectsItsOwnLink()
    {
        // Fixture attributes default to instance zero, which is unrealistic for an
        // extension holding two FILE_NAME attributes. The listed instance selects
        // exactly its own link; the other link never merges.
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 1)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "unselected.txt", parentSequence: 1)
            .WithAttributeInstance(1).AddFileName(5, "selected.txt", parentSequence: 1)
            .Build());
        MftRecordInfo merged = new();

        Assert.True(MftLogicalFileResolver.TryResolve(record, source, default, merged, out string? failure), failure);
        Assert.Contains(merged.Names, name => name.Name == "selected.txt");
        Assert.DoesNotContain(merged.Names, name => name.Name == "unselected.txt");
    }

    [Fact]
    public void DuplicateFullIdentitiesDecline()
    {
        // Two FILE_NAME contributions sharing type, name and instance cannot both be
        // the listed attribute: merging both would fold several links as one.
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "base.txt", parentSequence: 1)
            .AddAttributeListEntry(FileName, Fragment, FragmentSequence, 0)
            .Build());
        using FixtureRecordSource source = Source().Add(Fragment, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(FragmentSequence)
            .AddFileName(5, "first.txt", parentSequence: 1)
            .AddFileName(5, "second.txt", parentSequence: 1)
            .Build());

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }

    [Fact]
    public void InconsistentSectorAndClusterCountsDecline()
    {
        byte[] volume = MftGeometryFixtures.Volume(
            bytesPerSector: 512,
            bytesPerCluster: 4096,
            numberSectors: 800,
            totalClusters: 50);
        Assert.False(MftGeometry.TryParse(volume, out _, out _));
    }

    [Fact]
    public void ALegitimateSectorTailIsAllowed()
    {
        byte[] volume = MftGeometryFixtures.Volume(
            bytesPerSector: 512,
            bytesPerCluster: 4096,
            numberSectors: 805,
            totalClusters: 100);
        Assert.True(MftGeometry.TryParse(volume, out _, out string? failure), failure);
    }

    [Fact]
    public void SplitListWithMissingContinuationDeclines()
    {
        const uint clusterSize = 4096;
        byte[] fullValue = MftRecordBuilder.EncodeListEntries([
            (Data, Fragment, FragmentSequence, (ushort)0, string.Empty, 0UL),
            (0x20u, 102UL, (ushort)4, (ushort)0, string.Empty, 1UL),
        ]);
        // Pad the stated length past the first extent so a continuation is required,
        // but serve no continuation record.
        long stated = clusterSize + 32;
        using FixtureRecordSource source = Source();
        MftRecordInfo baseRecord = Parse(new MftRecordBuilder()
            .AddAttributeListNonResidentSegment(0, 0, stated, (10, 1))
            .Build());
        byte[] cluster = new byte[clusterSize];
        Array.Copy(fullValue, cluster, Math.Min(fullValue.Length, cluster.Length));
        source.AddVolumeData(10 * clusterSize, cluster);

        Assert.False(MftLogicalFileResolver.TryReadListValue(baseRecord, source, default, [], out _));
    }

    [Fact]
    public void SplitListWithCyclicContinuationDeclines()
    {
        const uint clusterSize = 4096;
        byte[] fullValue = MftRecordBuilder.EncodeListEntries([
            (0x20u, 102UL, (ushort)4, (ushort)0, string.Empty, 1UL),
        ]);
        long stated = clusterSize + 32;
        using FixtureRecordSource source = Source();
        source.Add(102, new MftRecordBuilder()
            .AsFragmentOf(Base, BaseSequence).Sequence(4)
            .AddAttributeListEntry(0x20u, Base, BaseSequence, 0, lowestVcn: 0)
            .AddAttributeListNonResidentSegment(1, 1, null, (20, 1))
            .Build());
        MftRecordInfo baseRecord = Parse(new MftRecordBuilder()
            .AddAttributeListNonResidentSegment(0, 0, stated, (10, 1))
            .Build());
        byte[] cluster0 = new byte[clusterSize];
        byte[] cluster1 = new byte[clusterSize];
        Array.Copy(fullValue, cluster0, fullValue.Length);
        source.AddVolumeData(10 * clusterSize, cluster0);
        source.AddVolumeData(20 * clusterSize, cluster1);

        Assert.False(MftLogicalFileResolver.TryReadListValue(baseRecord, source, default, [], out _));
    }

    [Fact]
    public void DuplicateBaseIdentitiesCannotMatchASelfReference()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddFileName(5, "first.txt", parentSequence: 1)
            .AddFileName(5, "second.txt", parentSequence: 1)
            .AddAttributeListEntry(FileName, Base, BaseSequence, 0)
            .Build());
        using FixtureRecordSource source = Source();

        Assert.False(MftLogicalFileResolver.TryResolve(record, source, default, new(), out _));
    }
    private static FixtureRecordSource Source() => new(new MftGeometry(512, 4096, 1024, 512));

    private static MftRecordInfo Parse(byte[] bytes)
    {
        MftRecordInfo record = new();
        Assert.True(MftRecordParser.TryParse(Base, bytes, record, out string? failure), failure);
        return record;
    }

    private static byte[] Entry()
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, FileName);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 32);
        bytes[7] = 26;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), Base | ((ulong)BaseSequence << 48));
        return bytes;
    }
}
