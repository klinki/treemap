using System.Buffers.Binary;
using System.Text;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// A whole MFT record assembled from literal byte offsets, with no help from the fixture
/// builder or from the parser's own constants.
/// </summary>
/// <remarks>
/// The builder in <see cref="MftRecordFixtures"/> writes the same offsets, so it would agree
/// with the parser however wrong the parser was. This record is written by hand instead: if
/// an offset in the parser is off by anything, only this test notices.
/// </remarks>
public class NtfsMftRawLayoutTests
{
    private const int RecordSize = 1024;
    private const int AttributeArea = 0x38;

    [Fact]
    public void ARecordWrittenByHandFromDocumentedOffsetsIsReadBackCorrectly()
    {
        DateTimeOffset lastWrite = new(2021, 6, 5, 8, 30, 0, TimeSpan.Zero);
        byte[] record = WriteRecord("quarterly.bin", lastWrite, FileAttributes.Archive, 4_096, 8_192);

        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(77, record, info, out string? failure), failure);

        MftName name = Assert.Single(info.Names);
        Assert.Equal("quarterly.bin", name.Name);
        Assert.Equal(11UL, name.ParentOrdinal);
        Assert.Equal(9, name.ParentSequence);

        Assert.Equal(4_096, info.LogicalSize);
        Assert.Equal(8_192, info.AllocatedSize);
        Assert.Equal(lastWrite, info.LastWriteTime!.Value);
        Assert.True((info.Attributes & FileAttributes.Archive) != 0);
        Assert.True(info.InUse);
        Assert.False(info.IsDirectory);
        Assert.False(info.IsExtension);
        Assert.Equal(3, info.SequenceNumber);
    }

    [Fact]
    public void ADosNameEntryInTheSameRecordSuppliesItsAttributes()
    {
        byte[] record = WriteRecord("hidden.bin", null, FileAttributes.Normal, 0, 0, FileAttributes.Hidden);

        MftRecordInfo info = new();
        Assert.True(MftRecordParser.TryParse(77, record, info, out string? failure), failure);

        Assert.True((info.Attributes & FileAttributes.Hidden) != 0);
    }

    /// <summary>
    /// Writes the record header, then STANDARD_INFORMATION, FILE_NAME and a non-resident
    /// DATA, every field at its documented offset.
    /// </summary>
    private static byte[] WriteRecord(
        string name,
        DateTimeOffset? lastWrite,
        FileAttributes attributes,
        long fileSize,
        long allocatedSize,
        FileAttributes dosAttributes = FileAttributes.Normal)
    {
        byte[] record = new byte[RecordSize];

        // Record header.
        "FILE"u8.CopyTo(record);
        WriteUShort(record, 0x04, 0x30);            // update sequence array offset
        WriteUShort(record, 0x06, 3);               // tag plus one word per 512-byte unit
        WriteULong(record, 0x08, 0);                // $logFileSequenceNumber
        WriteUShort(record, 0x10, 3);               // sequence number
        WriteUShort(record, 0x12, 1);               // hard link count
        WriteUShort(record, 0x14, AttributeArea);   // first attribute
        WriteUShort(record, 0x16, 0x0001);          // in use, not a directory
        WriteUInt(record, 0x1C, RecordSize);       // bytes allocated
        WriteULong(record, 0x20, 0);                // base file record: not a fragment
        WriteUShort(record, 0x28, 1);               // next attribute id

        int offset = AttributeArea;

        // STANDARD_INFORMATION, resident, unnamed. The value lives at the header
        // start plus the resident value offset (0x18); the next attribute starts
        // at the header start plus the returned length.
        int siStart = offset;
        int siLength = WriteResidentHeader(
            record,
            siStart,
            type: 0x10,
            valueLength: 0x30,
            nameOffset: 0);

        WriteULong(record, siStart + 0x18 + 0x08, ToFileTime(lastWrite));
        WriteUInt(record, siStart + 0x18 + 0x20, (uint)attributes);
        offset = siStart + siLength;

        // FILE_NAME, resident, unnamed.
        byte[] text = Encoding.Unicode.GetBytes(name);
        int fileNameStart = offset;
        int fileNameLength = WriteResidentHeader(
            record,
            fileNameStart,
            type: 0x30,
            valueLength: 66 + text.Length,
            nameOffset: 0);

        WriteULong(record, fileNameStart + 0x18 + 0x00, (11 & 0x0000_FFFF_FFFF_FFFFUL) | (9UL << 48));
        WriteUInt(record, fileNameStart + 0x18 + 56, (uint)dosAttributes);
        record[fileNameStart + 0x18 + 64] = (byte)(text.Length / 2);
        record[fileNameStart + 0x18 + 65] = 3;                    // Win32 and DOS namespace
        text.CopyTo(record, fileNameStart + 0x18 + 66);
        offset = fileNameStart + fileNameLength;

        // DATA, non-resident, unnamed, first fragment. A 72-byte header puts the
        // total-allocation field at 0x40 inside the header and the runs at 0x48, and the run
        // list ends with its own eight-byte terminator.
        int dataLength = 0x48 + 8;
        WriteUInt(record, offset + 0x00, 0x80);
        WriteUInt(record, offset + 0x04, (uint)dataLength);
        record[offset + 0x08] = 1;                  // non-resident
        WriteUShort(record, offset + 0x0A, 0);      // name offset: unnamed
        WriteUShort(record, offset + 0x0C, 0);      // flags: neither compressed nor sparse
        WriteULong(record, offset + 0x10, 0);       // lowest virtual cluster number
        WriteUShort(record, offset + 0x20, 0x48);   // mapping pairs offset
        WriteULong(record, offset + 0x28, (ulong)allocatedSize);
        WriteULong(record, offset + 0x30, (ulong)fileSize);
        WriteULong(record, offset + 0x38, (ulong)fileSize);
        WriteULong(record, offset + 0x40, (ulong)allocatedSize);   // total allocated
        offset += dataLength;

        WriteUInt(record, 0x18, (uint)(offset + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0xFFFF_FFFF);

        // The update sequence: a tag, and what each 512-byte unit really ended with.
        WriteUShort(record, 0x30, 0xABCD);
        WriteUShort(record, 0x32, 0x0000);
        WriteUShort(record, 0x34, 0x1234);
        WriteUShort(record, 510, 0xABCD);
        WriteUShort(record, 1022, 0xABCD);

        return record;
    }

    private static int WriteResidentHeader(byte[] record, int offset, uint type, int valueLength, ushort nameOffset)
    {
        int length = (0x18 + valueLength + 7) & ~7;

        WriteUInt(record, offset + 0x00, type);
        WriteUInt(record, offset + 0x04, (uint)length);
        record[offset + 0x08] = 0;                  // resident
        record[offset + 0x09] = 0;                  // name length, in characters
        WriteUShort(record, offset + 0x0A, nameOffset);
        WriteUShort(record, offset + 0x0C, 0);      // flags
        WriteUShort(record, offset + 0x0E, 0);      // attribute id
        WriteUInt(record, offset + 0x10, (uint)valueLength);
        WriteUShort(record, offset + 0x14, 0x18);   // value offset

        return length;
    }

    private static ulong ToFileTime(DateTimeOffset? value)
        => value is null ? 0 : (ulong)value.Value.UtcDateTime.ToFileTimeUtc();

    private static void WriteUShort(byte[] buffer, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteUInt(byte[] buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteULong(byte[] buffer, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);
}