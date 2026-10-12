using System.Buffers.Binary;
using System.Text;

namespace Treemap.Scanning.Windows;

/// <summary>
/// One <c>ATTRIBUTE_LIST_ENTRY</c>: where one attribute segment lives.
/// </summary>
/// <remarks>
/// Layout per Microsoft's documentation, all little-endian:
/// <code>
/// 0x00  UINT32  AttributeType
/// 0x04  UINT16  RecordLength
/// 0x06  UINT8   NameLength (characters)
/// 0x07  UINT8   NameOffset
/// 0x08  UINT64  LowestVcn
/// 0x10  UINT64  FileReference (low 48 bits ordinal, high 16 bits sequence)
/// 0x18  UINT16  AttributeId (the attribute header's instance at 0x0E)
/// + name UTF-16LE at NameOffset, NameLength characters
/// </code>
/// The documentation calls the 0x18 field reserved; on real NTFS volumes it carries
/// the attribute identity that matches the attribute header's instance.
/// </remarks>
internal readonly record struct MftAttributeListEntry(
    uint AttributeType,
    string AttributeName,
    ulong LowestVcn,
    ulong ReferencedOrdinal,
    ushort ReferencedSequence,
    ushort AttributeId);

/// <summary>Attribute identity for list-entry validation; resident attributes have VCN zero.</summary>
/// <param name="AttributeType">Numeric attribute type (for example 0x30 for FILE_NAME).</param>
/// <param name="AttributeName">Stream name; empty for the unnamed stream.</param>
/// <param name="AttributeId">Attribute header instance that the list entry must name.</param>
/// <param name="IsNonResident">True for a non-resident segment carrying VCNs.</param>
/// <param name="LowestVcn">Lowest VCN of the segment; zero for resident attributes.</param>
internal readonly record struct MftAttributeIdentity(
    uint AttributeType,
    string AttributeName,
    ushort AttributeId,
    bool IsNonResident,
    ulong LowestVcn);

/// <summary>One non-resident <c>ATTRIBUTE_LIST</c> segment: runs holding part of the list value.</summary>
internal sealed class MftAttributeListSegment
{
    public ulong LowestVcn { get; set; }

    public ulong HighestVcn { get; set; }

    /// <summary>Total list bytes. Only the segment at VCN zero states the real length.</summary>
    public ulong FileSize { get; set; }

    public bool HasFileSize { get; set; }

    public byte[] MappingPairs { get; set; } = [];
}

/// <summary>
/// Parses <c>ATTRIBUTE_LIST</c> values. Pure: no I/O, no state.
/// </summary>
internal static class MftAttributeList
{
    /// <summary>One entry without a name: type, length, name fields, VCN, reference, id.</summary>
    public const int MinimumEntryLength = 0x1A;

    /// <summary>Entries are quadword-aligned; lengths step in 8-byte units like attributes.</summary>
    private const int EntryAlignment = 8;

    private const ulong ReferenceOrdinalMask = 0x0000_FFFF_FFFF_FFFFUL;

    /// <summary>Parses every entry in a resident list value, or returns why it cannot be trusted.</summary>
    public static bool TryParseEntries(
        ReadOnlySpan<byte> value,
        ulong ordinal,
        List<MftAttributeListEntry> entries,
        out string? failure)
    {
        failure = null;

        if (value.Length == 0)
        {
            failure = $"MFT record {ordinal} has an empty ATTRIBUTE_LIST value, which names no segment.";
            return false;
        }

        int offset = 0;

        while (offset < value.Length)
        {
            if (!TryParseEntry(value[offset..], ordinal, out MftAttributeListEntry entry, out int length, out failure))
            {
                return false;
            }

            entries.Add(entry);
            offset += length;
        }

        failure = null;
        return true;
    }

    /// <summary>Parses one entry at the start of <paramref name="span"/>.</summary>
    public static bool TryParseEntry(
        ReadOnlySpan<byte> span,
        ulong ordinal,
        out MftAttributeListEntry entry,
        out int length,
        out string? failure)
    {
        entry = default;
        length = 0;
        failure = null;

        if (span.Length < MinimumEntryLength)
        {
            failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry of {span.Length} bytes, shorter than its {MinimumEntryLength}-byte header.";
            return false;
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(span);
        int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        int nameLength = span[6];
        int nameOffset = span[7];

        if (recordLength < MinimumEntryLength || recordLength > span.Length)
        {
            failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry declaring a length of {recordLength} bytes, outside the {span.Length} bytes left.";
            return false;
        }

        if ((recordLength & (EntryAlignment - 1)) != 0)
        {
            failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry declaring a length of {recordLength} bytes, which is not quadword-aligned.";
            return false;
        }

        // The name offset is unused when the entry carries no name: zero is the
        // ordinary value there, so it is only bounds-checked when there is a name.
        // When there is one it is UTF-16 that has to sit inside the entry, past the
        // fixed header, on a two-byte boundary.
        if (nameLength > 0)
        {
            if (nameOffset < MinimumEntryLength || nameOffset > recordLength)
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry naming itself outside its own length.";
                return false;
            }

            if ((nameOffset & 1) != 0)
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry whose name is not UTF-16 aligned.";
                return false;
            }

            if ((nameLength * 2) > recordLength - nameOffset)
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry whose name does not fit its length.";
                return false;
            }
        }

        ulong lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
        ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(span[24..]);

        string name = string.Empty;
        if (nameLength > 0)
        {
            try
            {
                name = Encoding.Unicode.GetString(span.Slice(nameOffset, nameLength * 2));
            }
            catch (ArgumentException)
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry whose name is not readable UTF-16.";
                return false;
            }

            if (name.Contains('\0'))
            {
                failure = $"MFT record {ordinal} has an ATTRIBUTE_LIST entry whose name carries an embedded NUL.";
                return false;
            }
        }

        entry = new MftAttributeListEntry(
            type,
            name,
            lowestVcn,
            reference & ReferenceOrdinalMask,
            (ushort)(reference >> 48),
            attributeId);
        length = recordLength;
        return true;
    }
}

/// <summary>Decodes NTFS mapping pairs (data runs) into cluster extents. Pure.</summary>
internal static class MftDataRuns
{
    /// <summary>One extent: <paramref name="Clusters"/> clusters at <paramref name="Lcn"/>. An LCN of -1 is a sparse hole reading as zeros.</summary>
    internal readonly record struct Run(long Lcn, ulong Clusters);

    /// <summary>
    /// Decodes <paramref name="pairs"/> into <paramref name="runs"/>, or returns why not.
    /// Offsets are signed deltas from the previous data run, skipping sparse holes.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> pairs,
        ulong ordinal,
        List<Run> runs,
        out string? failure)
    {
        failure = null;
        int offset = 0;
        long lastDataLcn = 0;

        // Seed from any data runs already collected, so appending stays relative.
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].Lcn >= 0)
            {
                lastDataLcn = runs[i].Lcn;
                break;
            }
        }

        while (true)
        {
            if (offset >= pairs.Length)
            {
                failure = $"MFT record {ordinal} has a run list that never terminates.";
                return false;
            }

            byte header = pairs[offset++];
            if (header == 0)
            {
                failure = null;
                return true;
            }

            int lengthSize = header & 0x0F;
            int offsetSize = (header >> 4) & 0x0F;

            if (lengthSize < 1 || lengthSize > 8 || offsetSize > 8)
            {
                failure = $"MFT record {ordinal} has a run with an unusable length of {lengthSize} and offset of {offsetSize} bytes.";
                return false;
            }

            if (offset + lengthSize + offsetSize > pairs.Length)
            {
                failure = $"MFT record {ordinal} has a run falling outside its mapping pairs.";
                return false;
            }

            ulong clusters = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                clusters |= (ulong)pairs[offset + i] << (8 * i);
            }

            offset += lengthSize;

            if (clusters == 0)
            {
                failure = $"MFT record {ordinal} has a run covering no clusters.";
                return false;
            }

            if (offsetSize == 0)
            {
                runs.Add(new Run(-1, clusters));
                continue;
            }

            long delta = 0;
            for (int i = 0; i < offsetSize; i++)
            {
                delta |= (long)pairs[offset + i] << (8 * i);
            }

            // Sign-extend the delta: offsets are signed little-endian.
            int shift = (8 - offsetSize) * 8;
            delta = (delta << shift) >> shift;
            offset += offsetSize;

            long lcn;
            try
            {
                checked
                {
                    lcn = lastDataLcn + delta;
                }
            }
            catch (OverflowException)
            {
                failure = $"MFT record {ordinal} has a run whose cluster offset overflows.";
                return false;
            }

            if (lcn < 0)
            {
                failure = $"MFT record {ordinal} has a run pointing before the start of the volume.";
                return false;
            }

            runs.Add(new Run(lcn, clusters));
            lastDataLcn = lcn;
        }
    }
}
