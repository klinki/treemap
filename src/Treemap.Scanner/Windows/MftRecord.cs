using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Treemap.Scanning.Windows;

/// <summary>Which namespace an NTFS name belongs to. More than one may name the same link.</summary>
internal enum MftNameNamespace : byte
{
    /// <summary>The NTFS name as stored: case preserved, no Win32 mapping applied.</summary>
    Posix = 0,

    /// <summary>The name a Win32 caller sees.</summary>
    Win32 = 1,

    /// <summary>The 8.3 alias NTFS keeps for old programs.</summary>
    Dos = 2,

    /// <summary>One name serving both Win32 and DOS callers.</summary>
    Win32AndDos = 3,
}

/// <summary>One <c>FILE_NAME</c> link: the directory it hangs in, and the name it carries there.</summary>
/// <param name="ParentOrdinal">MFT record number of the containing directory.</param>
/// <param name="ParentSequence">
/// The generation of that directory the record was written against. A mismatch means the
/// record number has since been reused, so the link is stale rather than merely old.
/// </param>
/// <param name="Name">The entry's name, which is always a single path segment.</param>
/// <param name="NameNamespace">Which namespace the name is presented in.</param>
internal readonly record struct MftName(
    ulong ParentOrdinal,
    ushort ParentSequence,
    string Name,
    MftNameNamespace NameNamespace);

/// <summary>The attribute types this backend reads. Everything else is skipped unread.</summary>
internal enum MftAttributeType : uint
{
    StandardInformation = 0x10,
    AttributeList = 0x20,
    FileName = 0x30,
    Data = 0x80,
    IndexRoot = 0x90,
    IndexAllocation = 0xA0,
    ReparsePoint = 0xC0,
}

/// <summary>
/// How an MFT record's bytes are represented before parsing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Raw"/> is the record as stored on disk: the last two bytes of every
/// 512-byte unit carry the update sequence tag, and the <c>USA</c> save area holds
/// what each unit really ended with. The parser must check the tag and write the
/// saved words back.
/// </para>
/// <para>
/// <see cref="FixupApplied"/> is the record as <c>FSCTL_GET_NTFS_FILE_RECORD</c>
/// returns it: the filesystem has already validated the tag while reading the
/// sectors and restored the saved words over the tails, so the tails hold ordinary
/// content that only coincidentally equals the tag. Checking the tails against the
/// tag again rejects nearly every healthy record (the first record walked, every
/// run), and writing the save area back a second time corrupts the restored tails.
/// </para>
/// </remarks>
internal enum MftRecordRepresentation : byte
{
    /// <summary>On-disk bytes; tails carry the update sequence tag.</summary>
    Raw = 0,

    /// <summary>Kernel-decoded bytes; tails already hold the restored content.</summary>
    FixupApplied = 1,
}

/// <summary>One STANDARD_INFORMATION contribution, tied to its attribute identity.</summary>
/// <param name="Identity">Which attribute stated it.</param>
/// <param name="AttributeBits">DWORD of file attribute bits.</param>
/// <param name="LastWriteTime">Modification time, when the value states one.</param>
internal readonly record struct MftStdContribution(
    MftAttributeIdentity Identity,
    uint AttributeBits,
    DateTimeOffset? LastWriteTime);

/// <summary>One FILE_NAME contribution, tied to its attribute identity.</summary>
/// <param name="Identity">Which attribute stated it.</param>
/// <param name="Name">The link this attribute carries.</param>
/// <param name="AttributeBits">DOS attribute bits from the FILE_NAME value.</param>
/// <param name="LastWriteTime">Modification time from the FILE_NAME value, as a fallback.</param>
internal readonly record struct MftFileNameContribution(
    MftAttributeIdentity Identity,
    MftName Name,
    uint AttributeBits,
    DateTimeOffset? LastWriteTime);

/// <summary>One DATA contribution, tied to its attribute identity.</summary>
/// <param name="Identity">Which attribute stated it.</param>
/// <param name="IsResident">True when the content lives inside the record.</param>
/// <param name="IsUnnamed">True for the unnamed stream that states the file length.</param>
/// <param name="LogicalSize">File length in bytes.</param>
/// <param name="AllocatedSize">Allocated bytes.</param>
internal readonly record struct MftDataContribution(
    MftAttributeIdentity Identity,
    bool IsResident,
    bool IsUnnamed,
    long LogicalSize,
    long AllocatedSize);

/// <summary>Everything one MFT record contributed. Reused for every record in a walk.</summary>
internal sealed class MftRecordInfo
{
    /// <summary>MFT record number this came from. The record itself never states it.</summary>
    public ulong Ordinal { get; private set; }

    public bool InUse { get; set; }

    public bool IsDirectory { get; set; }

    /// <summary>Generation of this record, used to tell a live reference from a stale one.</summary>
    public ushort SequenceNumber { get; set; }

    /// <summary>True when this record holds a fragment of another record's attributes.</summary>
    public bool IsExtension { get; set; }

    public ulong BaseOrdinal { get; set; }

    public ushort BaseSequence { get; set; }

    /// <summary>True when this record's attributes do not all fit inside it.</summary>
    public bool HasAttributeList { get; set; }

    /// <summary>Entries of resident <c>ATTRIBUTE_LIST</c> values seen in this record.</summary>
    public List<MftAttributeListEntry> AttributeListEntries { get; } = [];

    /// <summary>Non-resident <c>ATTRIBUTE_LIST</c> segments seen in this record.</summary>
    public List<MftAttributeListSegment> AttributeListSegments { get; } = [];

    /// <summary>Every attribute identity seen in this record, for list-entry validation.</summary>
    public List<MftAttributeIdentity> AttributeIdentities { get; } = [];

    /// <summary>Per-identity STANDARD_INFORMATION contributions, for selective merges.</summary>
    public List<MftStdContribution> StdContributions { get; } = [];

    /// <summary>Per-identity FILE_NAME contributions, for selective merges.</summary>
    public List<MftFileNameContribution> FileNameContributions { get; } = [];

    /// <summary>Per-identity DATA contributions, for selective merges.</summary>
    public List<MftDataContribution> DataContributions { get; } = [];

    /// <summary>File attributes, folded from every field that states them.</summary>
    public FileAttributes Attributes { get; set; }

    public DateTimeOffset? LastWriteTime { get; set; }

    /// <summary>True when <see cref="LastWriteTime"/> came from STANDARD_INFORMATION rather than a FILE_NAME fallback.</summary>
    public bool LastWriteFromStandardInformation { get; set; }

    /// <summary>True when an unnamed <c>DATA</c> attribute supplied a size this backend trusts.</summary>
    public bool HasUnnamedData { get; set; }

    /// <summary>True when that content lives inside the record rather than in allocated runs.</summary>
    public bool DataIsResident { get; set; }

    public long LogicalSize { get; set; }

    public long AllocatedSize { get; set; }

    /// <summary>Every link this record carries, DOS aliases and duplicates included.</summary>
    public List<MftName> Names { get; } = [];

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public void Reset(ulong ordinal)
    {
        Ordinal = ordinal;
        InUse = false;
        IsDirectory = false;
        SequenceNumber = 0;
        IsExtension = false;
        BaseOrdinal = 0;
        BaseSequence = 0;
        HasAttributeList = false;
        AttributeListEntries.Clear();
        AttributeListSegments.Clear();
        AttributeIdentities.Clear();
        StdContributions.Clear();
        FileNameContributions.Clear();
        DataContributions.Clear();
        Attributes = 0;
        LastWriteTime = null;
        LastWriteFromStandardInformation = false;
        HasUnnamedData = false;
        DataIsResident = false;
        LogicalSize = 0;
        AllocatedSize = 0;
        Names.Clear();
    }

    /// <summary>Folds a DWORD of file attribute bits in, from whichever field stated them.</summary>
    public void OrAttributes(uint bits) => Attributes = (FileAttributes)((uint)Attributes | bits);
}

/// <summary>
/// Turns the bytes of one MFT record into an <see cref="MftRecordInfo"/>.
/// </summary>
/// <remarks>
/// Pure: no I/O, no handles, no state. A record that does not describe itself consistently
/// is rejected outright, because a table that will not describe itself cannot become a
/// tree that is merely incomplete - it becomes a tree that is confidently wrong.
/// </remarks>
internal static class MftRecordParser
{
    /// <summary><c>FILE</c> in the order the record stores it.</summary>
    public const uint Signature = 0x454C_4946;

    /// <summary>Fixed header through the base file reference.</summary>
    public const int MinimumRecordLength = 0x2A;

    /// <summary>
    /// Update sequence entries step in 512-byte units whatever the volume's sector size is.
    /// A 4 KiB-sector volume still stores MFT fixups every 512 bytes, and reading them at
    /// the sector size puts every fixup in the wrong place - then declares the intact
    /// record torn.
    /// </summary>
    public const int UpdateSequenceStride = 512;

    /// <summary>Records 0 to 15 are reserved by NTFS for its own metadata.</summary>
    public const int ReservedRecordCount = 16;

    /// <summary>The record every NTFS volume root hangs from.</summary>
    public const ulong VolumeRootRecord = 5;

    internal const ushort AttributeFlagCompressed = 0x0001;
    internal const ushort AttributeFlagEncrypted = 0x4000;
    internal const ushort AttributeFlagSparse = 0x8000;

    // Record header.
    private const int UpdateSequenceOffsetField = 0x04;
    private const int UpdateSequenceCountField = 0x06;
    private const int SequenceNumberField = 0x10;
    private const int FirstAttributeField = 0x14;
    private const int FlagsField = 0x16;
    private const int BytesInUseField = 0x18;
    private const int BytesAllocatedField = 0x1C;
    private const int BaseRecordField = 0x20;

    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    // Resident attribute header, 24 bytes.
    private const int ResidentHeaderLength = 0x18;
    private const int AttributeNameLengthField = 0x09;
    private const int AttributeNameOffsetField = 0x0A;
    private const int AttributeFlagsField = 0x0C;
    private const int AttributeInstanceField = 0x0E;
    private const int ResidentValueLengthField = 0x10;
    private const int ResidentValueOffsetField = 0x14;

    // Non-resident attribute header, 64 bytes, or 72 when it states a total allocation.
    private const int NonResidentHeaderLength = 0x40;
    private const int MappingPairsOffsetField = 0x20;
    private const int DataAllocatedLengthField = 0x28;
    private const int DataFileSizeField = 0x30;
    private const int DataTotalAllocatedField = 0x40;
    private const int DataTotalAllocatedHeaderLength = 0x48;
    private const int LowestVcnField = 0x10;

    // STANDARD_INFORMATION value.
    private const int StandardInformationLength = 0x24;
    private const int StandardInformationModifiedField = 0x08;
    private const int StandardInformationAttributesField = 0x20;

    // FILE_NAME value: through the one-byte name length and namespace, then the name.
    private const int FileNameLength = 66;
    private const int FileNameParentField = 0x00;
    private const int FileNameModifiedField = 0x10;
    private const int FileNameAttributesField = 56;
    private const int FileNameNameLengthField = 64;
    private const int FileNameNamespaceField = 65;
    private const int FileNameTextOffset = 66;

    private const uint EndOfAttributes = 0xFFFF_FFFF;
    private const ulong ReferenceOrdinalMask = 0x0000_FFFF_FFFF_FFFFUL;

    /// <summary>Parses one on-disk (<see cref="MftRecordRepresentation.Raw"/>) record, or returns the reason it cannot be trusted.</summary>
    public static bool TryParse(ulong ordinal, ReadOnlySpan<byte> record, MftRecordInfo info, out string? failure)
        => TryParse(ordinal, record, info, out failure, MftRecordRepresentation.Raw);

    /// <summary>Parses one record in the stated representation, or returns the reason it cannot be trusted.</summary>
    public static bool TryParse(ulong ordinal, ReadOnlySpan<byte> record, MftRecordInfo info, out string? failure, MftRecordRepresentation representation)
    {
        info.Reset(ordinal);
        failure = null;

        if (record.Length < MinimumRecordLength)
        {
            failure = $"MFT record {ordinal} is {record.Length} bytes, shorter than its {MinimumRecordLength}-byte header.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Signature)
        {
            failure = $"MFT record {ordinal} does not start with the FILE signature.";
            return false;
        }

        uint bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(record[BytesInUseField..]);
        uint bytesAllocated = BinaryPrimitives.ReadUInt32LittleEndian(record[BytesAllocatedField..]);

        if (bytesInUse < MinimumRecordLength || bytesInUse > (uint)record.Length)
        {
            failure = $"MFT record {ordinal} claims {bytesInUse} bytes in use, outside the {record.Length}-byte record.";
            return false;
        }

        if (bytesAllocated < bytesInUse || bytesAllocated > (uint)record.Length)
        {
            failure = $"MFT record {ordinal} claims {bytesAllocated} allocated bytes, outside the {record.Length}-byte record.";
            return false;
        }

        int firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record[FirstAttributeField..]);
        if (firstAttribute < MinimumRecordLength || firstAttribute > (int)bytesInUse)
        {
            failure = $"MFT record {ordinal} points its attributes at byte {firstAttribute}, outside the {bytesInUse} bytes it holds.";
            return false;
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(record.Length);
        try
        {
            Span<byte> copy = scratch.AsSpan(0, record.Length);
            record.CopyTo(copy);

            if (!TryPrepareUpdateSequence(copy, (int)bytesAllocated, firstAttribute, representation, out string? repairFailure))
            {
                failure = $"MFT record {ordinal} {repairFailure}";
                return false;
            }

            info.InUse = (BinaryPrimitives.ReadUInt16LittleEndian(copy[FlagsField..]) & FlagInUse) != 0;
            info.IsDirectory = (BinaryPrimitives.ReadUInt16LittleEndian(copy[FlagsField..]) & FlagDirectory) != 0;
            info.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(copy[SequenceNumberField..]);

            ulong baseReference = BinaryPrimitives.ReadUInt64LittleEndian(copy[BaseRecordField..]);
            info.BaseOrdinal = baseReference & ReferenceOrdinalMask;
            info.BaseSequence = (ushort)(baseReference >> 48);
            info.IsExtension = info.BaseOrdinal != 0;

            ReadAttributes(copy[..(int)bytesInUse], firstAttribute, ordinal, info, out failure);
            return failure is null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Prepares the record's tails for reading: repairs them for <see cref="MftRecordRepresentation.Raw"/>,
    /// or only validates the array shape for <see cref="MftRecordRepresentation.FixupApplied"/>
    /// (whose tails the kernel already restored).
    /// </summary>
    internal static bool TryPrepareUpdateSequence(
        Span<byte> record,
        int bytesAllocated,
        int firstAttribute,
        MftRecordRepresentation representation,
        out string? failure)
        => representation == MftRecordRepresentation.FixupApplied
            ? TryValidateUpdateSequenceArray(record, bytesAllocated, firstAttribute, out failure)
            : TryRepairUpdateSequence(record, bytesAllocated, firstAttribute, out failure);

    /// <summary>
    /// Checks the update sequence array shape of a kernel-decoded record without touching
    /// its tails. The tails hold restored content, not the tag, so comparing them to the
    /// tag would reject healthy records and writing the save area back would corrupt them.
    /// </summary>
    internal static bool TryValidateUpdateSequenceArray(
        Span<byte> record,
        int bytesAllocated,
        int firstAttribute,
        out string? failure)
        => TryReadUpdateSequenceHeader(record, bytesAllocated, firstAttribute, out _, out _, out failure);

    /// <summary>
    /// Puts back the two bytes at the end of every 512-byte unit, and proves the record was
    /// written whole.
    /// </summary>
    /// <remarks>
    /// The array holds a tag followed by what each unit really ended with. The live bytes at the
    /// end of each unit are what to check against the tag, and the saved words are what to write
    /// back; comparing the save area with itself would accept any torn record.
    /// Only for <see cref="MftRecordRepresentation.Raw"/> bytes. Kernel-decoded records must
    /// go through <see cref="TryValidateUpdateSequenceArray"/> instead.
    /// </remarks>
    internal static bool TryRepairUpdateSequence(
        Span<byte> record,
        int bytesAllocated,
        int firstAttribute,
        out string? failure)
    {
        if (!TryReadUpdateSequenceHeader(record, bytesAllocated, firstAttribute, out int usaOffset, out int usaCount, out failure))
        {
            return false;
        }

        int tag = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);

        // Every unit is checked before any is written back, so a torn record cannot be
        // half-repaired into looking whole.
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * UpdateSequenceStride) - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[tail..]) != tag)
            {
                failure = "is torn: a 512-byte unit does not carry its update sequence tag.";
                return false;
            }
        }

        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * UpdateSequenceStride) - 2;
            ushort saved = BinaryPrimitives.ReadUInt16LittleEndian(record[(usaOffset + (i * 2))..]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[tail..], saved);
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Checks the record length, the entry count and that the array fits inside the first
    /// unit ahead of the attributes. Shared by both representations: a malformed array
    /// rejects the record however its tails arrived.
    /// </summary>
    private static bool TryReadUpdateSequenceHeader(
        Span<byte> record,
        int bytesAllocated,
        int firstAttribute,
        out int usaOffset,
        out int usaCount,
        out string? failure)
    {
        usaOffset = 0;
        usaCount = 0;

        if (record.Length % UpdateSequenceStride != 0 || bytesAllocated % UpdateSequenceStride != 0)
        {
            failure = $"is not a whole number of {UpdateSequenceStride}-byte units.";
            return false;
        }

        usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[UpdateSequenceOffsetField..]);
        usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[UpdateSequenceCountField..]);
        int sectors = bytesAllocated / UpdateSequenceStride;

        if (usaCount != sectors + 1)
        {
            failure = $"carries {usaCount} update sequence entries where its {bytesAllocated} allocated bytes need {sectors + 1}.";
            return false;
        }

        // The array has to fit inside the first unit, ahead of the attributes: a tag array that
        // overruns the header is describing a record that is not there.
        if (usaOffset < MinimumRecordLength ||
            (usaCount * 2) > UpdateSequenceStride ||
            usaOffset + (usaCount * 2) > UpdateSequenceStride ||
            usaOffset + (usaCount * 2) > firstAttribute)
        {
            failure = "has an update sequence array that does not fit inside its header.";
            return false;
        }

        failure = null;
        return true;
    }

    private static void ReadAttributes(
        ReadOnlySpan<byte> record,
        int firstAttribute,
        ulong ordinal,
        MftRecordInfo info,
        out string? failure)
    {
        int offset = firstAttribute;
        bool terminated = false;

        while (offset + 4 <= record.Length)
        {
            // The terminator is four bytes and the bytes in use can end right after it, so
            // it is looked for before the eight bytes an attribute header needs.
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]) == EndOfAttributes)
            {
                terminated = true;
                break;
            }

            if (offset + 8 > record.Length)
            {
                break;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);

            // Attributes are 8-byte aligned and carry a full header. Both are checked before
            // the record is indexed any further, so a bogus length cannot walk the loop off
            // the end or spin it forever.
            if (length < ResidentHeaderLength || (length & 7) != 0)
            {
                failure = $"MFT record {ordinal} has an attribute at byte {offset} declaring an invalid length of {length}.";
                return;
            }

            if (length > (uint)(record.Length - offset))
            {
                failure = $"MFT record {ordinal} has an attribute at byte {offset} running past the end of the record.";
                return;
            }

            ReadAttribute(record.Slice(offset, (int)length), ordinal, info, out failure);
            if (failure is not null)
            {
                return;
            }

            offset += (int)length;
        }

        // Without the terminator there is no way to tell a complete attribute list from a
        // truncated one, and reading on would run into whatever the record happens to hold.
        failure = terminated
            ? null
            : $"MFT record {ordinal} has no end-of-attributes marker, so its attribute list does not end where it says.";
    }

    private static void ReadAttribute(ReadOnlySpan<byte> attribute, ulong ordinal, MftRecordInfo info, out string? failure)
    {
        failure = null;

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
        bool nonResident = attribute[8] != 0;
        int nameBytes = attribute[AttributeNameLengthField] * 2;
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[AttributeNameOffsetField..]);
        ushort instance = BinaryPrimitives.ReadUInt16LittleEndian(attribute[AttributeInstanceField..]);
        int headerLength = nonResident ? NonResidentHeaderLength : ResidentHeaderLength;

        if (attribute.Length < headerLength)
        {
            failure = $"MFT record {ordinal} has a {(nonResident ? "non-resident" : "resident")} attribute stub of {attribute.Length} bytes.";
            return;
        }

        // An unnamed attribute states no name offset at all, and zero is the ordinary value,
        // so the name is only bounds-checked when there is one. When there is one it is UTF-16
        // that has to sit inside the attribute, past the header.
        string attributeName = string.Empty;
        if (nameBytes > 0)
        {
            if (nameOffset < headerLength ||
                nameOffset > attribute.Length ||
                nameBytes > attribute.Length - nameOffset)
            {
                failure = $"MFT record {ordinal} has an attribute naming itself outside its own length.";
                return;
            }

            try
            {
                attributeName = Encoding.Unicode.GetString(attribute.Slice(nameOffset, nameBytes));
            }
            catch (ArgumentException)
            {
                failure = $"MFT record {ordinal} has an attribute whose name is not readable UTF-16.";
                return;
            }
        }
        else
        {
            nameOffset = 0;
        }

        if (nonResident)
        {
            // A named non-resident attribute keeps its name between the header and the runs.
            int mappingPairs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[MappingPairsOffsetField..]);

            if (mappingPairs < NonResidentHeaderLength || mappingPairs >= attribute.Length)
            {
                failure = $"MFT record {ordinal} has a non-resident attribute whose runs fall outside it.";
                return;
            }

            if (nameBytes > 0 && nameOffset + nameBytes > mappingPairs)
            {
                failure = $"MFT record {ordinal} has a non-resident attribute whose name overlaps its runs.";
                return;
            }

            if (type == (uint)MftAttributeType.AttributeList)
            {
                // A list may live in its own runs. Its references are not readable here,
                // but its existence means this record's attributes are not all present.
                info.HasAttributeList = true;
                info.AttributeIdentities.Add(new MftAttributeIdentity(
                    type, attributeName, instance, true,
                    BinaryPrimitives.ReadUInt64LittleEndian(attribute[LowestVcnField..])));

                if (nameBytes == 0)
                {
                    CaptureListSegment(attribute, ordinal, info, out failure);
                }

                return;
            }

            if (type == (uint)MftAttributeType.Data && nameBytes == 0)
            {
                MftAttributeIdentity dataIdentity = new(
                    type, attributeName, instance, true,
                    BinaryPrimitives.ReadUInt64LittleEndian(attribute[LowestVcnField..]));
                info.AttributeIdentities.Add(dataIdentity);
                ReadNonResidentData(attribute, ordinal, info, dataIdentity, out failure);
                return;
            }

            info.AttributeIdentities.Add(new MftAttributeIdentity(
                type, attributeName, instance, true,
                BinaryPrimitives.ReadUInt64LittleEndian(attribute[LowestVcnField..])));

            return;
        }

        uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[ResidentValueLengthField..]);
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[ResidentValueOffsetField..]);

        // The value is exactly what it says it is. Anything past it is padding, and reporting
        // padding as content would report every resident file as larger than it is.
        if (valueOffset < headerLength ||
            valueOffset > attribute.Length ||
            valueLength > (uint)(attribute.Length - valueOffset))
        {
            failure = $"MFT record {ordinal} has an attribute placing its value outside its own length.";
            return;
        }

        // A resident attribute carries its name ahead of its value, never inside it.
        if (nameBytes > 0 && valueOffset < nameOffset + nameBytes)
        {
            failure = $"MFT record {ordinal} has an attribute whose value overlaps its name.";
            return;
        }

        ReadOnlySpan<byte> value = attribute.Slice(valueOffset, (int)valueLength);

        MftAttributeIdentity identity = new(
            type, attributeName, instance, false, 0);
        info.AttributeIdentities.Add(identity);

        switch ((MftAttributeType)type)
        {
            case MftAttributeType.StandardInformation when nameBytes == 0:
                if (value.Length < StandardInformationLength)
                {
                    failure = $"MFT record {ordinal} has a truncated STANDARD_INFORMATION attribute.";
                    return;
                }

                // All four times in both of these attributes are full 64-bit FILETIMEs.
                uint stdBits = BinaryPrimitives.ReadUInt32LittleEndian(value[StandardInformationAttributesField..]);
                DateTimeOffset? stdTime = FromWindowsTime(
                    BinaryPrimitives.ReadUInt64LittleEndian(value[StandardInformationModifiedField..]));
                info.OrAttributes(stdBits);
                info.StdContributions.Add(new MftStdContribution(identity, stdBits, stdTime));
                if (stdTime is not null)
                {
                    info.LastWriteTime = stdTime;
                    info.LastWriteFromStandardInformation = true;
                }

                break;

            case MftAttributeType.FileName when nameBytes == 0:
                if (value.Length < FileNameLength)
                {
                    failure = $"MFT record {ordinal} has a truncated FILE_NAME attribute.";
                    return;
                }

                if (!TryReadFileName(value, ordinal, info, identity, out failure))
                {
                    return;
                }

                break;

            case MftAttributeType.Data:
                // Only the unnamed stream is the file's length. A named stream belongs to no
                // directory entry the user sees, and folding it in would overstate the file.
                bool isUnnamed = nameBytes == 0;
                info.DataContributions.Add(new MftDataContribution(
                    identity, true, isUnnamed, value.Length, 0));
                if (isUnnamed && !info.HasUnnamedData)
                {
                    info.HasUnnamedData = true;
                    info.DataIsResident = true;
                    info.LogicalSize = value.Length;

                    // Resident content lives inside the MFT record, in bytes already spent.
                    info.AllocatedSize = 0;
                }

                break;

            case MftAttributeType.AttributeList when nameBytes == 0:
                info.HasAttributeList = true;
                if (!MftAttributeList.TryParseEntries(value, ordinal, info.AttributeListEntries, out failure))
                {
                    return;
                }

                break;

            case MftAttributeType.AttributeList:
                // A named attribute list is not a layout this backend models. Flag it so
                // the resolver declines honestly rather than half-reporting the file.
                info.HasAttributeList = true;
                break;

            default:
                // A type this backend does not model, or a layout it did not expect. NTFS
                // reserves plenty of both, and neither is a reason to reject the record.
                break;
        }
    }

    /// <summary>
    /// Keeps one non-resident list segment's runs so the resolver can read its value from
    /// the volume. Only the segment at VCN zero states the total list length.
    /// </summary>
    private static void CaptureListSegment(
        ReadOnlySpan<byte> attribute,
        ulong ordinal,
        MftRecordInfo info,
        out string? failure)
    {
        failure = null;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[AttributeFlagsField..]);
        if ((flags & (AttributeFlagCompressed | AttributeFlagEncrypted)) != 0)
        {
            failure = $"MFT record {ordinal} has a compressed or encrypted ATTRIBUTE_LIST that this backend will not decode.";
            return;
        }

        int mappingPairs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[MappingPairsOffsetField..]);
        ulong lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute[LowestVcnField..]);
        ulong highestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute[(LowestVcnField + 8)..]);

        if (highestVcn < lowestVcn)
        {
            failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST segment whose highest VCN {highestVcn} is below its lowest {lowestVcn}.";
            return;
        }

        // Bounds were checked by the caller; the slice below only copies the runs.
        MftAttributeListSegment segment = new()
        {
            LowestVcn = lowestVcn,
            HighestVcn = highestVcn,
            MappingPairs = attribute[mappingPairs..].ToArray(),
        };

        if (lowestVcn == 0)
        {
            if (attribute.Length < DataFileSizeField + 8)
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST attribute with no list length.";
                return;
            }

            segment.FileSize = BinaryPrimitives.ReadUInt64LittleEndian(attribute[DataFileSizeField..]);
            segment.HasFileSize = true;
        }

        info.AttributeListSegments.Add(segment);
    }

    /// <summary>
    /// Reads one link. Every reason to reject it is a reason to reject the record: NTFS cannot
    /// store a separator in a name, so a name that carries one is a damaged record rather
    /// than an entry to quietly leave out.
    /// </summary>
    private static bool TryReadFileName(
        ReadOnlySpan<byte> value,
        ulong ordinal,
        MftRecordInfo info,
        MftAttributeIdentity identity,
        out string? failure)
    {
        // The DOS attribute bits live here rather than in STANDARD_INFORMATION on every
        // filesystem, and SkipAttributes is asked for in those terms, so both sources fold
        // into one answer.
        uint fileBits = BinaryPrimitives.ReadUInt32LittleEndian(value[FileNameAttributesField..]);
        info.OrAttributes(fileBits);

        byte nameNamespace = value[FileNameNamespaceField];
        if (nameNamespace > (byte)MftNameNamespace.Win32AndDos)
        {
            failure = $"MFT record {ordinal} has a FILE_NAME entry in namespace {nameNamespace}, which does not exist.";
            return false;
        }

        int nameBytes = value[FileNameNameLengthField] * 2;
        if (nameBytes <= 0 || nameBytes > value.Length - FileNameTextOffset)
        {
            failure = $"MFT record {ordinal} has a FILE_NAME entry whose name does not fit its attribute.";
            return false;
        }

        string name = Encoding.Unicode.GetString(value.Slice(FileNameTextOffset, nameBytes));
        if (!IsUsableName(name, ordinal))
        {
            failure = $"MFT record {ordinal} has a FILE_NAME entry naming '{name}', which cannot be one path segment.";
            return false;
        }

        ulong parentReference = BinaryPrimitives.ReadUInt64LittleEndian(value[FileNameParentField..]);
        MftName link = new(
            parentReference & ReferenceOrdinalMask,
            (ushort)(parentReference >> 48),
            name,
            (MftNameNamespace)nameNamespace);
        info.Names.Add(link);

        DateTimeOffset? fileTime = value.Length >= FileNameModifiedField + 8
            ? FromWindowsTime(BinaryPrimitives.ReadUInt64LittleEndian(value[FileNameModifiedField..]))
            : null;
        info.FileNameContributions.Add(new MftFileNameContribution(identity, link, fileBits, fileTime));

        // STANDARD_INFORMATION owns the timestamp when it states one; the FILE_NAME
        // time is only a fallback for records whose standard information is elsewhere.
        if (fileTime is not null && info.LastWriteTime is null)
        {
            info.LastWriteTime = fileTime;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Rejects names that would turn one record into more than one path segment: a separator, an
    /// embedded NUL, or the two relative names. The volume root's own POSIX name is the single
    /// exception - it is literally <c>"."</c>, pointing at itself.
    /// </summary>
    private static bool IsUsableName(string name, ulong ordinal)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (name is "." or "..")
        {
            return ordinal == VolumeRootRecord;
        }

        foreach (char character in name)
        {
            if (character is '\\' or '/' or '\0')
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadNonResidentData(
        ReadOnlySpan<byte> attribute,
        ulong ordinal,
        MftRecordInfo info,
        MftAttributeIdentity identity,
        out string? failure)
    {
        failure = null;

        int mappingPairs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[MappingPairsOffsetField..]);
        if (mappingPairs < NonResidentHeaderLength || mappingPairs >= attribute.Length)
        {
            failure = $"MFT record {ordinal} has a DATA attribute whose runs fall outside it.";
            return;
        }

        // Length fields are only defined on the VCN-zero segment. A continuation
        // carries whatever bytes its header slot holds (often all-ones), so the
        // identity is recorded first and sizes are never inspected for one.
        ulong lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute[LowestVcnField..]);
        if (lowestVcn != 0)
        {
            return;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[AttributeFlagsField..]);

        // A compressed or sparse run occupies only part of the cluster range it covers, so
        // the run's own allocation is not the file's. Without the longer header there is no
        // honest answer, and using the run's figure anyway would understate the file.
        bool total = (flags & (AttributeFlagCompressed | AttributeFlagSparse)) != 0;
        if (total && (attribute.Length < DataTotalAllocatedHeaderLength || mappingPairs < DataTotalAllocatedHeaderLength))
        {
            failure = $"MFT record {ordinal} has a compressed or sparse DATA attribute with no total-allocation field.";
            return;
        }

        ulong allocated = BinaryPrimitives.ReadUInt64LittleEndian(attribute[DataAllocatedLengthField..]);
        if (total)
        {
            allocated = BinaryPrimitives.ReadUInt64LittleEndian(attribute[DataTotalAllocatedField..]);
        }

        if (!TrySize(BinaryPrimitives.ReadUInt64LittleEndian(attribute[DataFileSizeField..]), out long logical, out string? sizeFailure) ||
            !TrySize(allocated, out long allocatedSize, out sizeFailure))
        {
            failure = $"MFT record {ordinal} has a DATA attribute that {sizeFailure}";
            return;
        }

        info.DataContributions.Add(new MftDataContribution(identity, false, true, logical, allocatedSize));

        // Only the segment starting at virtual cluster zero carries the file's real
        // length, and it is kept from the first one seen. A named stream never
        // reaches here (the caller only forwards the unnamed stream).
        if (info.HasUnnamedData)
        {
            return;
        }

        info.HasUnnamedData = true;
        info.DataIsResident = false;
        info.LogicalSize = logical;
        info.AllocatedSize = allocatedSize;
    }

    /// <summary>
    /// The links a user can see: DOS aliases dropped wherever the same directory also carries
    /// a Win32 name, and every remaining distinct name kept.
    /// </summary>
    /// <remarks>
    /// One file under three names is three nodes, each carrying the whole size, because that is
    /// what the portable backend does with the same directory entries. The totals count a shared
    /// file once per link, which <see cref="ScanResult.HardLinkedFileCount"/> states rather than
    /// hides.
    /// </remarks>
    public static List<MftName> VisibleNames(IReadOnlyList<MftName> names)
    {
        HashSet<ulong> directoriesWithWin32Names = [];

        foreach (MftName name in names)
        {
            if (name.NameNamespace is MftNameNamespace.Win32 or MftNameNamespace.Win32AndDos)
            {
                directoriesWithWin32Names.Add(name.ParentOrdinal);
            }
        }

        List<MftName> visible = new(names.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (MftName name in names)
        {
            // An 8.3 alias is not an extra file. Where the directory it lives in already
            // shows a Win32 name, the alias is a second label for the same entry.
            if (name.NameNamespace == MftNameNamespace.Dos && directoriesWithWin32Names.Contains(name.ParentOrdinal))
            {
                continue;
            }

            if (seen.Add(string.Concat(name.ParentOrdinal.ToString(), "\0", name.Name)))
            {
                visible.Add(name);
            }
        }

        return visible;
    }

    private static DateTimeOffset? FromWindowsTime(ulong value)
    {
        // Zero and all ones are NTFS's "never set" markers, and both would otherwise read
        // as 1601 or as a date past the end of the representable range.
        if (value is 0 or ulong.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime((long)value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TrySize(ulong value, out long size, out string? failure)
    {
        // A size past long.MaxValue cannot be reported without inventing a different number,
        // and the allocated total feeds a running sum, so it is declined rather than clamped.
        if (value > long.MaxValue)
        {
            size = 0;
            failure = "states a size this scanner cannot represent.";
            return false;
        }

        size = (long)value;
        failure = null;
        return true;
    }
}
