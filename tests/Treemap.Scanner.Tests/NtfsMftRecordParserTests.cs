using System.Buffers.Binary;
using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The MFT parser, driven by records built field by field.
/// </summary>
/// <remarks>
/// Every case here is one the parser has to get right because a live volume will not wait
/// for it to find out: a torn record, an attribute claiming more bytes than its record
/// holds, a name with a separator in it. These tests call the real parser.
/// </remarks>
public class NtfsMftRecordParserTests
{
    private const ulong VolumeRoot = 5;
    private const ulong AFile = 42;

    // ── The update sequence ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The array holds a tag followed by what each 512-byte unit really ended with. The live
    /// bytes at the end of a unit are the tag; the saved words are what goes back. Checking
    /// the save area against the tag instead would accept every torn record and fill the
    /// record with the tag wherever data belonged.
    /// </summary>
    [Fact]
    public void TheSavedWordsAreRestoredOverTheTag()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0x0000)
            .SavedWord(1, 0x1234)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "fixups.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .Build();

        // On disk, each unit's tail carries the tag.
        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));

        Assert.True(
            MftRecordParser.TryRepairUpdateSequence(record, bytesAllocated: 1024, firstAttribute: 0x38, out string? failure),
            failure);

        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
    }

    [Fact]
    public void ASavedWordEqualToTheTagIsNotTorn()
    {
        // Comparing the save area with the tag would reject this healthy record.
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0xABCD)
            .SavedWord(1, 0xABCD)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "healthy.bin")
            .Build();

        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(AFile, record, info, out string? failure), failure);
        Assert.Equal("healthy.bin", Assert.Single(info.Names).Name);
    }

    [Fact]
    public void ALiveUnitThatDoesNotCarryTheTagIsTorn()
    {
        byte[] record = new MftRecordBuilder()
            .CorruptTailAt(1)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "torn.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .Build();

        AssertRejected(record, "torn");
    }

    [Fact]
    public void AUnitThatDoesNotCarryTheTagIsTornEvenWithHealthySaveArea()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0)
            .SavedWord(1, 0)
            .CorruptTailAt(0)
            .AddStandardInformation(FileAttributes.Normal)
            .Build();

        AssertRejected(record, "torn");
    }

    [Fact]
    public void AnUpdateSequenceCountThatDoesNotMatchTheRecordIsRejected()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceCount(2)
            .AddStandardInformation(FileAttributes.Normal)
            .Build();

        AssertRejected(record, "update sequence entries");
    }

    [Fact]
    public void AnUpdateSequenceArrayThatOverrunsTheHeaderIsRejected()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .AddStandardInformation(FileAttributes.Normal)
            .Build();

        // A tag array starting past the attributes is describing a record that is not there.
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04), 0x200);

        AssertRejected(record, "update sequence array");
    }

    /// <summary>
    /// A 4 KiB-sector volume still stores MFT fixups every 512 bytes. Reading them at the
    /// sector size would expect two entries where a 1 KiB record has three.
    /// </summary>
    [Fact]
    public void TheUpdateSequenceStepsInFiveTwelveBytesRegardlessOfSectorSize()
    {
        byte[] record = new MftRecordBuilder(recordSize: 1024)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "any.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .Build();

        Assert.True(MftGeometry.TryParse(MftGeometryFixtures.Volume(bytesPerSector: 4096), out MftGeometry geometry, out _));
        Assert.Equal(4096u, geometry.BytesPerSector);

        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(AFile, record, info, out string? failure), failure);
        Assert.True(info.HasUnnamedData);
    }

    // ── Kernel-decoded (FSCTL_GET_NTFS_FILE_RECORD) representation ────────────────

    /// <summary>
    /// <c>FSCTL_GET_NTFS_FILE_RECORD</c> hands back the record after the filesystem has
    /// restored the saved words over the unit tails. Those tails hold ordinary content,
    /// not the tag, so the raw torn check rejects the healthy record while the
    /// fixup-applied parse accepts it. This is the C:\ first-record failure: the walk's
    /// very first record is healthy, yet raw parsing declines it as torn.
    /// </summary>
    [Fact]
    public void AFixupAppliedRecordWithRestoredTailsParsesWithoutATornFailure()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0x0000)
            .SavedWord(1, 0x1234)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "fixup-applied.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .BuildFixupApplied();

        // Kernel-decoded tails carry the restored words, not the tag.
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));

        MftRecordInfo info = new();
        Assert.True(
            MftRecordParser.TryParse(AFile, record, info, out string? failure, MftRecordRepresentation.FixupApplied),
            failure);
        Assert.Equal("fixup-applied.bin", Assert.Single(info.Names).Name);
        Assert.True(info.HasUnnamedData);
    }

    [Fact]
    public void ARestoredRecordParsedAsRawIsStillTorn()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0x0000)
            .SavedWord(1, 0x1234)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "healthy.bin")
            .BuildFixupApplied();

        // The raw torn check is not weakened: restored tails are not the tag.
        MftRecordInfo info = new();
        Assert.False(MftRecordParser.TryParse(AFile, record, info, out string? failure));
        Assert.Contains("torn", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFixupAppliedRecordLeavesItsRestoredTailsAlone()
    {
        byte[] record = new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0x2222)
            .SavedWord(1, 0x3333)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "tails.bin")
            .BuildFixupApplied();

        MftRecordInfo info = new();
        Assert.True(
            MftRecordParser.TryParse(AFile, record, info, out string? failure, MftRecordRepresentation.FixupApplied),
            failure);

        // No second write-back of the save area: the restored content survives parsing.
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        Assert.Equal(0x3333, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
    }

    [Fact]
    public void AFixupAppliedRecordWithABadUpdateSequenceCountIsRejected()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "bad-count.bin")
            .BuildFixupApplied();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x06), 2);

        MftRecordInfo info = new();
        Assert.False(MftRecordParser.TryParse(AFile, record, info, out string? failure, MftRecordRepresentation.FixupApplied));
        Assert.Contains("update sequence entries", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFixupAppliedRecordWithAnOverrunningArrayIsRejected()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "bad-array.bin")
            .BuildFixupApplied();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04), 0x200);

        MftRecordInfo info = new();
        Assert.False(MftRecordParser.TryParse(AFile, record, info, out string? failure, MftRecordRepresentation.FixupApplied));
        Assert.Contains("update sequence array", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFixupAppliedWalkSourceParsesItsRestoredRecord()
    {
        MftGeometry geometry = new(4096, 1024, 4096);
        FixtureRecordSource source = new(geometry)
        {
            Representation = MftRecordRepresentation.FixupApplied,
        };
        source.Add(42, new MftRecordBuilder()
            .UpdateSequenceTag(0xABCD)
            .SavedWord(0, 0x0000)
            .SavedWord(1, 0x1234)
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "walked.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .BuildFixupApplied());

        ScanCollector collector = new(ScanOptions.Default, progress: null);
        CountingVisitor visitor = new();

        // As Raw this same record would decline as torn on the first visit; as
        // FixupApplied the walk reaches it.
        Assert.True(
            MftRecordStream.TryWalk(source, CancellationToken.None, collector, @"C:\", visitor, out string? failure),
            failure);
        Assert.Equal(1, visitor.Count);
    }

    private sealed class CountingVisitor : IMftRecordVisitor
    {
        public int Count { get; private set; }

        public string? FailureReason => null;

        public bool Visit(MftRecordInfo record)
        {
            Count++;
            return true;
        }
    }

    // ── Sizes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NonResidentDataSupersedesTheCachedSizeInTheNameEntry()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "notes.txt")
            .AddDataNonResident(dataSize: 12_345, allocatedSize: 16_384)
            .Build());

        Assert.True(record.HasUnnamedData);
        Assert.False(record.DataIsResident);
        Assert.Equal(12_345, record.LogicalSize);
        Assert.Equal(16_384, record.AllocatedSize);
    }

    /// <summary>
    /// A resident value is exactly its declared length. Reading to the end of the attribute
    /// instead would report the header and the padding as file content.
    /// </summary>
    [Fact]
    public void ResidentDataReportsItsValueLengthAndNoAllocation()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "tiny.txt")
            .AddDataResident("hello"u8.ToArray())
            .Build());

        Assert.True(record.HasUnnamedData);
        Assert.True(record.DataIsResident);
        Assert.Equal(5, record.LogicalSize);

        // Resident content lives inside the MFT record, in bytes already spent.
        Assert.Equal(0, record.AllocatedSize);
    }

    [Fact]
    public void AZeroLengthResidentFileIsStillAFile()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "empty.txt")
            .AddDataResident([])
            .Build());

        Assert.True(record.HasUnnamedData);
        Assert.Equal(0, record.LogicalSize);
    }

    [Fact]
    public void OnlyTheFragmentStartingAtTheFirstClusterCarriesTheFilesLength()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "fragment.bin")
            .AddDataNonResident(dataSize: 99_999, allocatedSize: 4_096)
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096, lowestVcn: 8)
            .Build());

        Assert.Equal(99_999, record.LogicalSize);
    }

    [Fact]
    public void ACompressedAttributeReportsTheTotalAllocationRatherThanItsRun()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "archive.zip")
            .AddDataNonResident(dataSize: 100_000, allocatedSize: 1_024, totalAllocated: 40_960, compressed: true)
            .Build());

        Assert.Equal(100_000, record.LogicalSize);
        Assert.Equal(40_960, record.AllocatedSize);
    }

    [Fact]
    public void ASparseAttributeReportsTheTotalAllocationRatherThanItsRun()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "sparse.bin")
            .AddDataNonResident(dataSize: 1_073_741_824, allocatedSize: 4_096, totalAllocated: 8_192, sparse: true)
            .Build());

        Assert.Equal(8_192, record.AllocatedSize);
    }

    /// <summary>
    /// The longer header exists for compressed and sparse runs. An attribute that carries it
    /// without either flag still reports the run's own allocation.
    /// </summary>
    [Fact]
    public void APlainAttributeIgnoresTheTotalAllocationSlot()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "plain.bin")
            .AddDataNonResident(dataSize: 8_192, allocatedSize: 8_192, totalAllocated: 999_999, headerLength: 0x48)
            .Build());

        Assert.Equal(8_192, record.AllocatedSize);
    }

    /// <summary>A named stream is real bytes that belong to no directory entry.</summary>
    [Fact]
    public void AFileWithOnlyANamedStreamHasNoSizeToReport()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "streamy.dat")
            .AddNamedStream("payload", [1, 2, 3, 4])
            .Build());

        Assert.False(record.HasUnnamedData);
        Assert.Equal(0, record.LogicalSize);
    }

    [Fact]
    public void AnUnnamedDataAttributeWinsOverALaterNamedOne()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "both.bin")
            .AddNamedStream("payload", new byte[16])
            .AddDataResident([1, 2, 3])
            .Build());

        Assert.Equal(3, record.LogicalSize);
    }

    // ── Names ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARootRecordKeepsItsPosixDotName()
    {
        MftRecordInfo record = Parse(MftRecordParser.VolumeRootRecord, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(5)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(VolumeRoot, ".", MftNameNamespace.Posix)
            .Build());

        MftName name = Assert.Single(record.Names);
        Assert.Equal(".", name.Name);
        Assert.Equal(MftNameNamespace.Posix, name.NameNamespace);
        Assert.Equal(VolumeRoot, name.ParentOrdinal);
    }

    /// <summary>
    /// NTFS cannot store a separator in a name, so one that carries a separator means a
    /// damaged record. Dropping the name quietly would leave the entry unplaceable and say
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData(@"a\b.txt")]
    [InlineData("c/d.txt")]
    [InlineData("..")]
    [InlineData(".")]
    public void ANameThatCannotBeOnePathSegmentRejectsTheRecord(string name)
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, name)
            .Build();

        AssertRejected(record, "cannot be one path segment");
    }

    [Fact]
    public void ANameInANamespaceThatDoesNotExistRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .NameNamespace(9)
            .AddFileName(VolumeRoot, "odd.bin")
            .Build();

        AssertRejected(record, "namespace");
    }

    [Fact]
    public void ANameLongerThanItsAttributeRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .NameLengthInCharacters(200)
            .AddFileName(VolumeRoot, "short.bin")
            .Build();

        AssertRejected(record, "does not fit");
    }

    [Fact]
    public void ADosAliasIsDroppedWhereItsDirectoryAlsoShowsAWin32Name()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "Program Files", MftNameNamespace.Win32AndDos)
            .AddFileName(VolumeRoot, "PROGRA~1", MftNameNamespace.Dos)
            .Build());

        Assert.Equal("Program Files", Assert.Single(MftRecordParser.VisibleNames(record.Names)).Name);
    }

    [Fact]
    public void ADosAliasIsDroppedEvenWhenItsTextDoesNotMatchTheLongName()
    {
        // The alias is a second label for the same entry, not a comparison problem.
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "Quite Long Name.txt", MftNameNamespace.Win32)
            .AddFileName(VolumeRoot, "QUICL01~.TXT", MftNameNamespace.Dos)
            .Build());

        Assert.Equal("Quite Long Name.txt", Assert.Single(MftRecordParser.VisibleNames(record.Names)).Name);
    }

    [Fact]
    public void ADosAliasInAnotherDirectoryIsLeftAlone()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "Program Files", MftNameNamespace.Win32AndDos)
            .AddFileName(11, "PROGRA~1", MftNameNamespace.Dos)
            .Build());

        Assert.Equal(["Program Files", "PROGRA~1"], [.. MftRecordParser.VisibleNames(record.Names).Select(n => n.Name)]);
    }

    [Fact]
    public void ADosAliasSurvivesWhenItsDirectoryHasNoWin32Name()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "PROGRA~1", MftNameNamespace.Dos)
            .Build());

        Assert.Equal("PROGRA~1", Assert.Single(record.Names).Name);
    }

    [Fact]
    public void EveryDistinctHardLinkNameSurvives()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "one.bin")
            .AddFileName(VolumeRoot, "two.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 8_192)
            .Build());

        string[] visible = [.. MftRecordParser.VisibleNames(record.Names).Select(name => name.Name)];

        Assert.Equal(["one.bin", "two.bin"], visible);
    }

    [Fact]
    public void DuplicateNamesForTheSameDirectoryCollapse()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "same.bin", MftNameNamespace.Posix)
            .AddFileName(VolumeRoot, "same.bin", MftNameNamespace.Win32AndDos)
            .Build());

        Assert.Single(MftRecordParser.VisibleNames(record.Names));
    }

    // ── Record shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AFragmentRecordReportsTheBaseItExtends()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AsFragmentOf(AFile, 7)
            .AddStandardInformation(FileAttributes.Normal)
            .Build());

        Assert.True(record.IsExtension);
        Assert.Equal(AFile, record.BaseOrdinal);
        Assert.Equal(7, record.BaseSequence);
    }

    [Fact]
    public void ABaseRecordIsNotMistakenForAFragment()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "base.bin")
            .Build());

        Assert.False(record.IsExtension);
        Assert.Equal(0UL, record.BaseOrdinal);
    }

    [Fact]
    public void AnAttributeListIsRecordedSoTheBackendCanDecline()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "split.bin")
            .AddAttributeList()
            .Build());

        Assert.True(record.HasAttributeList);
    }

    [Fact]
    public void RunsThatFallOutsideTheAttributeRejectTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "odd.bin")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096, mappingPairsOffset: 0x30)
            .Build();

        AssertRejected(record, "runs fall outside");
    }

    /// <summary>
    /// A compressed run's own allocation is not the file's. Without the total-allocation field
    /// there is no honest answer, and using the run's figure anyway would understate the file.
    /// </summary>
    [Fact]
    public void ACompressedAttributeWithNoTotalAllocationFieldRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "archive.zip")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096, compressed: true, headerLength: 0x40)
            .Build();

        AssertRejected(record, "total-allocation");
    }

    [Fact]
    public void ASizePastWhatTheScannerCanReportRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "huge.bin")
            .AddOversizedNonResidentData((ulong)long.MaxValue + 1)
            .Build();

        AssertRejected(record, "cannot represent");
    }

    [Fact]
    public void AnAttributeListThatNeverTerminatesRejectsTheRecord()
    {
        byte[] record = new MftRecordBuilder()
            .WithoutEndMarker()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "runaway.bin")
            .Build();

        AssertRejected(record, "end-of-attributes");
    }

    /// <summary>
    /// A directory on a real volume carries index attributes this backend does not model.
    /// Their absence from the result must not be mistaken for a malformed record, or every
    /// directory on the volume would send the scan back to the portable backend.
    /// </summary>
    [Fact]
    public void ARecordWithOnlyTheAttributesWeModelIsStillAValidDirectory()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AsDirectory()
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(VolumeRoot, "folder")
            .Build());

        Assert.True(record.IsDirectory);
        Assert.Equal("folder", Assert.Single(record.Names).Name);
    }

    [Fact]
    public void ARecordWithoutTheSignatureIsRejected()
        => AssertRejected(new MftRecordBuilder().WithoutSignature().AddStandardInformation(FileAttributes.Normal).Build(), "signature");

    [Theory]
    [InlineData(0)]
    [InlineData(0x29)]
    public void ARecordWithAnUnusableByteCountIsRejected(int bytesInUse)
        => AssertRejected(new MftRecordBuilder().BytesInUse(bytesInUse).AddStandardInformation(FileAttributes.Normal).Build(), "bytes in use");

    [Fact]
    public void ARecordAllocatingMoreThanItHoldsIsRejected()
        => AssertRejected(new MftRecordBuilder().BytesAllocated(8192).AddStandardInformation(FileAttributes.Normal).Build(), "allocated");

    [Fact]
    public void AnAttributeRunningPastTheEndOfTheRecordIsRejected()
        => AssertRejected(
            new MftRecordBuilder().AddMalformedAttribute((uint)MftAttributeType.FileName, declaredLength: 0x400, actualLength: 0x60).Build(),
            "past the end");

    [Theory]
    [InlineData(4u)]
    [InlineData(12u)]
    [InlineData(13u)]
    [InlineData(20u)]
    public void AnAttributeWithAnUnusableLengthIsRejected(uint declaredLength)
        => AssertRejected(
            new MftRecordBuilder().AddMalformedAttribute((uint)MftAttributeType.FileName, declaredLength, actualLength: 0x60).Build(),
            "invalid length");

    // ── Times and attributes ───────────────────────────────────────────────────────────

    [Fact]
    public void TimeAndAttributesComeFromStandardInformation()
    {
        DateTimeOffset lastWrite = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);

        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Hidden | FileAttributes.Archive, lastWrite)
            .AddFileName(VolumeRoot, "hidden.txt")
            .Build());

        Assert.Equal(lastWrite, record.LastWriteTime!.Value);
        Assert.True((record.Attributes & FileAttributes.Hidden) != 0);
        Assert.False(record.IsReparsePoint);
    }

    [Fact]
    public void ADosNameEntryAlsoStatesTheAttributes()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.Normal)
            .AddFileName(VolumeRoot, "system.dll", dosAttributes: FileAttributes.System | FileAttributes.Hidden)
            .Build());

        Assert.True((record.Attributes & FileAttributes.System) != 0);
        Assert.True((record.Attributes & FileAttributes.Hidden) != 0);
    }

    [Fact]
    public void ARenamePointIsRecognisedSoItCanBeLeftAlone()
    {
        MftRecordInfo record = Parse(new MftRecordBuilder()
            .AddStandardInformation(FileAttributes.ReparsePoint)
            .AddFileName(VolumeRoot, "link")
            .AddDataNonResident(dataSize: 4_096, allocatedSize: 4_096)
            .Build());

        Assert.True(record.IsReparsePoint);
    }

    // ── Geometry ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeometryAcceptsFourKiloByteSectorsWithOneKiloByteRecords()
    {
        Assert.True(MftGeometry.TryParse(MftGeometryFixtures.Volume(), out MftGeometry geometry, out string? failure), failure);

        Assert.Equal(4096u, geometry.BytesPerSector);
        Assert.Equal(1024u, geometry.RecordSize);
        Assert.Equal(4096L, geometry.RecordCount);
    }

    /// <summary>
    /// ByteCount is how many bytes of the output buffer the extended block used. A block that
    /// fits the answer is ordinary; one that does not describes a volume that returned less
    /// than it claims, and reading a version out of that is reading past the answer.
    /// </summary>
    [Fact]
    public void AnExtendedBlockThatFitsTheAnswerIsAccepted()
    {
        Assert.True(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(extendedByteCount: 32, returnedLength: 128),
            out MftGeometry geometry,
            out string? failure), failure);

        Assert.Equal(4096L, geometry.RecordCount);
    }

    [Fact]
    public void AnExtendedBlockPastTheEndOfTheAnswerIsDeclined()
    {
        Assert.False(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(extendedByteCount: 32, returnedLength: 104),
            out _,
            out string? failure));

        Assert.Contains("extended geometry", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExtendedBlockLargerThanAnyBufferIsDeclined()
    {
        Assert.False(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(extendedByteCount: uint.MaxValue),
            out _,
            out string? failure));

        Assert.Contains("extended geometry", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExtendedBlockTooShortToHoldAVersionIsDeclined()
    {
        Assert.False(MftGeometry.TryParse(MftGeometryFixtures.Volume(extendedByteCount: 4), out _, out string? failure));
        Assert.Contains("extended geometry", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryCountsRecordsFromTheValidLength()
    {
        Assert.True(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(recordCount: 32, recordSize: 4096),
            out MftGeometry geometry,
            out _));

        Assert.Equal(4096u, geometry.RecordSize);
        Assert.Equal(32L, geometry.RecordCount);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    public void GeometryDeclinesAVersionThisBackendDoesNotTrust(int major, int minor)
    {
        Assert.False(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(majorVersion: major, minorVersion: minor),
            out _,
            out string? failure));

        Assert.Contains("version", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryDeclinesAnAnswerTooShortToHoldAVersion()
    {
        Assert.False(MftGeometry.TryParse(MftGeometryFixtures.Volume(returnedLength: 96), out _, out string? failure));
        Assert.Contains("version", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryDeclinesAValidLengthThatIsNotAWholeRecord()
    {
        Assert.False(MftGeometry.TryParse(
            MftGeometryFixtures.Volume(validDataLength: (1024 * 4096) + 512),
            out _,
            out string? failure));

        Assert.Contains("valid MFT bytes", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryDeclinesARecordSizeThatIsNotAWholeUpdateSequenceUnit()
    {
        Assert.False(MftGeometry.TryParse(MftGeometryFixtures.Volume(recordSize: 1000), out _, out string? failure));
        Assert.Contains("record size", failure!, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRejected(byte[] record, string expected)
    {
        MftRecordInfo info = new();
        Assert.False(MftRecordParser.TryParse(AFile, record, info, out string? failure));
        Assert.Contains(expected, failure!, StringComparison.OrdinalIgnoreCase);
    }

    private static MftRecordInfo Parse(byte[] record) => Parse(AFile, record);

    private static MftRecordInfo Parse(ulong ordinal, byte[] record)
    {
        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(ordinal, record, info, out string? failure), failure);
        return info;
    }
}