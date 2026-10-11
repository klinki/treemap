using System.Buffers.Binary;

namespace Treemap.Scanning.Windows;

/// <summary>
/// What one NTFS volume says about its own geometry, read from the buffer
/// <c>FSCTL_GET_NTFS_VOLUME_DATA</c> returns.
/// </summary>
/// <remarks>
/// The documented <c>NTFS_VOLUME_DATA_BUFFER</c> is 96 bytes, and its extended block starts
/// at 0x60:
/// <code>
/// 0x28  UINT32          BytesPerSector
/// 0x2C  UINT32          BytesPerCluster
/// 0x30  UINT32          BytesPerFileRecordSegment
/// 0x34  UINT32          ClustersPerFileRecordSegment
/// 0x38  INT64           MftValidDataLength
/// 0x40  INT64           MftStartLcn
/// 0x60  UINT32          ByteCount, extended bytes used in the output buffer
/// 0x64  UINT16          MajorVersion
/// 0x66  UINT16          MinorVersion
/// </code>
/// Reading the version therefore needs 104 bytes of answer.
/// </remarks>
internal readonly record struct MftGeometry(uint BytesPerSector, uint RecordSize, long RecordCount)
{
    /// <summary>
    /// Header in front of the record <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns: the file
    /// reference number of the record found, its length, and then the record.
    /// </summary>
    public const int OutputHeaderLength = 12;

    /// <summary>Padding past the record, beyond the documented output size.</summary>
    public const int OutputSlack = 16;

    /// <summary>Bytes the record output buffer must hold for one full record.</summary>
    public int BufferLength => OutputHeaderLength + OutputSlack + (int)RecordSize;

    /// <summary>Bytes an answer must contain before its version can be read.</summary>
    public const int MinimumBufferLength = 0x68;

    /// <summary>Where the extended block starts, and how much of it holds the version.</summary>
    private const int ExtendedOffset = 0x60;
    private const int ExtendedVersionLength = 8;

    private const int BytesPerSectorOffset = 0x28;
    private const int RecordSizeOffset = 0x30;
    private const int ValidDataLengthOffset = 0x38;
    private const int MajorVersionOffset = 0x64;
    private const int MinorVersionOffset = 0x66;

    /// <summary>
    /// Reads and checks the geometry. A volume that answers with something this backend
    /// cannot reason about is declined rather than guessed at.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out MftGeometry geometry, out string? failure)
    {
        geometry = default;

        if (buffer.Length < MinimumBufferLength)
        {
            failure = $"The volume returned {buffer.Length} bytes of geometry; {MinimumBufferLength} are needed to read its version.";
            return false;
        }

        uint extendedLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[ExtendedOffset..]);

        // ByteCount is the number of output-buffer bytes the extended block used, and the
        // answer returned is all this backend has to work from. A block shorter than the
        // version carries no version; a block longer than the answer describes a volume that
        // did not return what it claims.
        uint extendedRoom = (uint)(buffer.Length - ExtendedOffset);

        if (extendedLength < ExtendedVersionLength || extendedLength > extendedRoom)
        {
            failure = $"The volume reported {extendedLength} bytes of extended geometry, which does not fit the {buffer.Length} bytes it answered with.";
            return false;
        }

        int major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[MajorVersionOffset..]);
        int minor = BinaryPrimitives.ReadUInt16LittleEndian(buffer[MinorVersionOffset..]);

        // NTFS 3.0 and 3.1 are the layouts this backend has been reasoned about. Anything
        // else is declined rather than read hopefully.
        if (major != 3 || minor > 1)
        {
            failure = $"The volume reports NTFS version {major}.{minor}; this backend trusts 3.0 and 3.1 only.";
            return false;
        }

        uint sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[BytesPerSectorOffset..]);
        if (sectorSize < 512 || sectorSize > 65536 || (sectorSize & (sectorSize - 1)) != 0)
        {
            failure = $"The volume reports a sector size of {sectorSize} bytes, which is not a usable power of two.";
            return false;
        }

        uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[RecordSizeOffset..]);

        // Deliberately not tested against the sector size: a 4 KiB-sector volume with 1 KiB
        // MFT records is ordinary, and the update sequence steps in 512-byte units either way.
        if (recordSize < MftRecordParser.MinimumRecordLength ||
            recordSize > 65536 ||
            recordSize % MftRecordParser.UpdateSequenceStride != 0)
        {
            failure = $"The volume reports an MFT record size of {recordSize} bytes, which is not a whole number of {MftRecordParser.UpdateSequenceStride}-byte units.";
            return false;
        }

        long validLength = BinaryPrimitives.ReadInt64LittleEndian(buffer[ValidDataLengthOffset..]);
        if (validLength <= 0 || validLength % recordSize != 0)
        {
            failure = $"The volume reports {validLength} valid MFT bytes, which is not a whole number of {recordSize}-byte records.";
            return false;
        }

        long recordCount = validLength / recordSize;

        // Ordinals travel back inside a 64-bit file reference number whose low 48 bits are
        // the record number.
        if (recordCount < MftRecordParser.ReservedRecordCount || recordCount > OrdinalMask)
        {
            failure = $"The volume reports {recordCount} MFT records, outside the range this backend can enumerate.";
            return false;
        }

        geometry = new MftGeometry(sectorSize, recordSize, recordCount);
        failure = null;
        return true;
    }

    /// <summary>The low 48 bits of a file reference number, which is the record number.</summary>
    public const long OrdinalMask = 0x0000_FFFF_FFFF_FFFFL;
}