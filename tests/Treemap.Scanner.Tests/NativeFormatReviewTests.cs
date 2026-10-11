using System.Buffers.Binary;
using System.Text;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

public class NativeFormatReviewTests
{
    [Fact]
    public void DocumentedGeometryAllowsAnExtendedBufferAndSmallRecordsOnLargeSectors()
    {
        byte[] bytes = new byte[128];
        Write32(bytes, 40, 4096);
        Write32(bytes, 48, 1024);
        Write64(bytes, 56, 1024 * 100);
        Write32(bytes, 96, 32);
        Write16(bytes, 100, 3);
        Write16(bytes, 102, 1);

        Assert.True(MftGeometry.TryParse(bytes, out MftGeometry geometry, out string? failure), failure);
        Assert.Equal(4096u, geometry.BytesPerSector);
        Assert.Equal(1024u, geometry.RecordSize);
        Assert.Equal(100, geometry.RecordCount);
    }

    [Fact]
    public void AResidentDataValueUsesItsExactLengthAndFileNameUsesItsByteLength()
    {
        byte[] name = new byte[66 + 2 * 9];
        Write64(name, 0, (7UL << 48) | 5);
        Write64(name, 40, 999_999);
        Write64(name, 48, 888_888);
        Write32(name, 56, 0x0002_0000);
        name[64] = 9;
        name[65] = 1;
        Encoding.Unicode.GetBytes("small.bin").CopyTo(name, 66);
        byte[] bytes = Record(Resident(0x30, name), Resident(0x80, [1, 2, 3]));
        MftRecordInfo info = new();

        Assert.True(MftRecordParser.TryParse(20, bytes, info, out string? failure), failure);
        Assert.Equal("small.bin", Assert.Single(info.Names).Name);
        Assert.Equal(7, info.Names[0].ParentSequence);
        Assert.Equal(5UL, info.Names[0].ParentOrdinal);
        Assert.Equal(0x0002_0000u, (uint)info.Attributes);
        Assert.Equal(3, info.LogicalSize);
        Assert.Equal(0, info.AllocatedSize);
    }

    [Fact]
    public void UpdateSequenceChecksSectorTailsAndRestoresArbitrarySavedWords()
    {
        byte[] bytes = Record(Resident(0x80, []));
        Write16(bytes, 50, 0);
        Write16(bytes, 52, 0x1234);
        byte[] damaged = (byte[])bytes.Clone();
        Write16(damaged, 510, 0x9999);

        Assert.True(MftRecordParser.TryParse(20, bytes, new(), out string? failure), failure);
        Assert.False(MftRecordParser.TryParse(20, damaged, new(), out _));
    }

    [Fact]
    public void NamedDataAndIndexAllocationCannotSupplyTheUnnamedFileSize()
    {
        byte[] namedData = NonResident(0x80, 9000, 8192, "ads");
        byte[] index = NonResident(0xA0, 7000, 4096);
        byte[] data = NonResident(0x80, 123, 4096);
        MftRecordInfo info = new();

        Assert.True(MftRecordParser.TryParse(20, Record(namedData, index, data), info, out string? failure), failure);
        Assert.Equal(123, info.LogicalSize);
        Assert.Equal(4096, info.AllocatedSize);
    }

    [Fact]
    public void AShortDosAliasDoesNotCreateAnotherHardLinkNode()
    {
        MftName[] names =
        [
            new(5, 7, "LONGFI~1.BIN", MftNameNamespace.Dos),
            new(5, 7, "Long file.bin", MftNameNamespace.Win32),
            new(8, 2, "other.bin", MftNameNamespace.Win32AndDos),
        ];

        List<MftName> visible = MftRecordParser.VisibleNames(names);
        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, name => name.NameNamespace == MftNameNamespace.Dos);
    }

    [Fact]
    public void SparseDataUsesTheExtendedAllocationField()
    {
        byte[] data = new byte[80];
        Write32(data, 0, 0x80);
        Write32(data, 4, 80);
        data[8] = 1;
        Write16(data, 12, 0x8000);
        Write16(data, 32, 72);
        Write64(data, 40, 100_000);
        Write64(data, 48, 99_000);
        Write64(data, 56, 99_000);
        Write64(data, 64, 4096);
        MftRecordInfo info = new();

        Assert.True(MftRecordParser.TryParse(20, Record(data), info, out string? failure), failure);
        Assert.Equal(99_000, info.LogicalSize);
        Assert.Equal(4096, info.AllocatedSize);
    }

    [Fact]
    public void SparseDataCannotTreatRunBytesAsAnExtendedAllocationField()
    {
        byte[] data = NonResident(0x80, 1000, 4096);
        Write16(data, 12, 0x8000);
        Assert.False(MftRecordParser.TryParse(20, Record(data), new(), out _));
    }

    [Fact]
    public void ADeclaredFileNameCannotRunOutsideItsValue()
    {
        byte[] name = new byte[68];
        Write64(name, 0, (7UL << 48) | 5);
        name[64] = 255;
        name[65] = 1;
        Assert.False(MftRecordParser.TryParse(20, Record(Resident(0x30, name)), new(), out _));
    }

    [Fact]
    public void ARecordWithoutAnAttributeTerminatorIsRejected()
    {
        byte[] bytes = Record(Resident(0x80, [1, 2, 3]));
        Write32(bytes, 24, 88);
        Assert.False(MftRecordParser.TryParse(20, bytes, new(), out _));
    }

    private static byte[] Record(params byte[][] attributes)
    {
        byte[] bytes = new byte[1024];
        Write32(bytes, 0, 0x454C4946);
        Write16(bytes, 4, 48);
        Write16(bytes, 6, 3);
        Write16(bytes, 16, 2);
        Write16(bytes, 20, 56);
        Write16(bytes, 22, 1);
        Write32(bytes, 28, 1024);
        int offset = 56;
        foreach (byte[] attribute in attributes)
        {
            attribute.CopyTo(bytes, offset);
            offset += attribute.Length;
        }
        Write32(bytes, offset, uint.MaxValue);
        Write32(bytes, 24, (uint)(offset + 8));
        Write16(bytes, 48, 0xABCD);
        Write16(bytes, 50, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));
        Write16(bytes, 52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1022)));
        Write16(bytes, 510, 0xABCD);
        Write16(bytes, 1022, 0xABCD);
        return bytes;
    }

    private static byte[] Resident(uint type, byte[] value)
    {
        byte[] bytes = new byte[(24 + value.Length + 7) & ~7];
        Write32(bytes, 0, type);
        Write32(bytes, 4, (uint)bytes.Length);
        Write32(bytes, 16, (uint)value.Length);
        Write16(bytes, 20, 24);
        value.CopyTo(bytes, 24);
        return bytes;
    }

    private static byte[] NonResident(uint type, ulong logical, ulong allocated, string? name = null)
    {
        byte[] encodedName = name is null ? [] : Encoding.Unicode.GetBytes(name);
        int mappingOffset = (64 + encodedName.Length + 7) & ~7;
        byte[] bytes = new byte[mappingOffset + 8];
        Write32(bytes, 0, type);
        Write32(bytes, 4, (uint)bytes.Length);
        bytes[8] = 1;
        bytes[9] = (byte)(encodedName.Length / 2);
        if (name is not null) Write16(bytes, 10, 64);
        Write16(bytes, 32, (ushort)mappingOffset);
        Write64(bytes, 40, allocated);
        Write64(bytes, 48, logical);
        Write64(bytes, 56, logical);
        encodedName.CopyTo(bytes, 64);
        return bytes;
    }

    private static void Write16(byte[] bytes, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void Write32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void Write64(byte[] bytes, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}
