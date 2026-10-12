using System.Buffers.Binary;
using System.Text;
using Treemap.Scanning.Windows;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Builds MFT records field by field, at the offsets the documented record and attribute
/// layouts put them at.
/// </summary>
/// <remarks>
/// Offsets here are written as literals on purpose. A builder that derived them from the
/// parser's own constants would agree with the parser no matter how wrong the parser was,
/// which is exactly the failure this fixture exists to prevent. Every offset below is a
/// literal, so a change to the parser's constants shows up here as a failing test.
/// </remarks>
internal sealed class MftRecordBuilder
{
    // Record header.
    private const int UsaOffsetField = 0x04;
    private const int UsaCountField = 0x06;
    private const int SequenceNumberField = 0x10;
    private const int FirstAttributeField = 0x14;
    private const int FlagsField = 0x16;
    private const int BytesInUseField = 0x18;
    private const int BytesAllocatedField = 0x1C;
    private const int BaseRecordField = 0x20;

    // Resident attribute header, 24 bytes.
    private const int ResidentHeader = 0x18;

    // Non-resident attribute header: 64 bytes, or 72 when it states a total allocation.
    private const int NonResidentHeader = 0x40;
    private const int ExtendedHeader = 0x48;

    /// <summary>Bytes of run list that close a non-resident attribute: the end-of-runs pair.</summary>
    private const int MappingTerminatorLength = 8;

    private readonly byte[] _buffer;
    private readonly int _usaOffset = 0x30;
    private readonly int _usaCount;
    private readonly int _firstAttribute;
    private readonly Dictionary<int, ushort> _saved = [];
    private int _attributeOffset;

    private bool _inUse = true;
    private bool _isDirectory;
    private ushort _sequence = 1;
    private ulong _baseOrdinal;
    private ushort _baseSequence;
    private ushort _usaTag = 1;
    private int _corruptTail = -1;
    private int? _bytesInUseOverride;
    private int? _bytesAllocatedOverride;
    private int? _usaCountOverride;
    private bool _signature = true;
    private bool _endMarker = true;
    private byte? _nameNamespaceOverride;
    private byte? _nameLengthOverride;
    private ushort _nextInstance;
    private readonly List<(uint type, ulong refOrdinal, ushort refSequence, ushort attributeId, string attributeName, ulong lowestVcn)> _listEntries = [];
    private byte[]? _listRaw;
    private (long fileSize, (long lcn, ulong clusters)[] runs)? _listNonResident;
    private readonly List<(ulong lowestVcn, ulong highestVcn, long? fileSize, (long lcn, ulong clusters)[] runs)> _listNonResidentSegments = [];

    /// <summary>Writes a name entry into a namespace that does not exist.</summary>
    public MftRecordBuilder NameNamespace(byte value)
    {
        _nameNamespaceOverride = value;
        return this;
    }

    /// <summary>States a name length that does not match the name.</summary>
    public MftRecordBuilder NameLengthInCharacters(byte value)
    {
        _nameLengthOverride = value;
        return this;
    }

    public MftRecordBuilder(int recordSize = 1024)
    {
        _buffer = new byte[recordSize];
        _usaCount = (recordSize / MftRecordParser.UpdateSequenceStride) + 1;

        // Fixed at construction. The record header points here for the life of the record,
        // not at wherever the attributes happen to end up.
        _firstAttribute = (_usaOffset + (_usaCount * 2) + 7) & ~7;
        _attributeOffset = _firstAttribute;
    }

    public MftRecordBuilder InUse(bool value = true)
    {
        _inUse = value;
        return this;
    }

    public MftRecordBuilder AsDirectory(bool value = true)
    {
        _isDirectory = value;
        return this;
    }

    public MftRecordBuilder Sequence(ushort value)
    {
        _sequence = value;
        return this;
    }

    public MftRecordBuilder AsFragmentOf(ulong baseOrdinal, ushort baseSequence)
    {
        _baseOrdinal = baseOrdinal;
        _baseSequence = baseSequence;
        return this;
    }

    public MftRecordBuilder UpdateSequenceTag(ushort value)
    {
        _usaTag = value;
        return this;
    }

    /// <summary>The word the <paramref name="index"/>-th 512-byte unit really ended with.</summary>
    public MftRecordBuilder SavedWord(int index, ushort value)
    {
        _saved[index] = value;
        return this;
    }

    /// <summary>Overwrites a sector tail with something the tag does not match: a torn record.</summary>
    public MftRecordBuilder CorruptTailAt(int index)
    {
        _corruptTail = index;
        return this;
    }

    public MftRecordBuilder WithoutSignature()
    {
        _signature = false;
        return this;
    }

    /// <summary>Leaves off the end-of-attributes marker, so the attribute list never closes.</summary>
    public MftRecordBuilder WithoutEndMarker()
    {
        _endMarker = false;
        return this;
    }

    public MftRecordBuilder BytesInUse(int value)
    {
        _bytesInUseOverride = value;
        return this;
    }

    public MftRecordBuilder BytesAllocated(int value)
    {
        _bytesAllocatedOverride = value;
        return this;
    }

    public MftRecordBuilder UpdateSequenceCount(int value)
    {
        _usaCountOverride = value;
        return this;
    }

    public MftRecordBuilder AddStandardInformation(FileAttributes attributes, DateTimeOffset? lastWrite = null)
    {
        byte[] value = new byte[0x30];

        // The four times here are full 64-bit FILETIMEs.
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), ToWindowsTime(lastWrite));
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), (uint)attributes);

        return AddResidentAttribute((uint)MftAttributeType.StandardInformation, value);
    }

    public MftRecordBuilder AddFileName(
        ulong parentOrdinal,
        string name,
        MftNameNamespace nameNamespace = MftNameNamespace.Win32AndDos,
        ushort? parentSequence = null,
        FileAttributes dosAttributes = FileAttributes.Normal)
    {
        byte[] text = Encoding.Unicode.GetBytes(name);
        byte[] value = new byte[66 + text.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(
            value.AsSpan(0),
            (parentOrdinal & 0x0000_FFFF_FFFF_FFFFUL) | ((ulong)(parentSequence ?? _sequence) << 48));

        // A deliberately wrong cached size pair. Reading a file's size from here is the
        // shortcut that makes an MFT scan cheap and wrong.
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x28), 0xFFFF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(56), (uint)dosAttributes);
        value[64] = _nameLengthOverride ?? (byte)(text.Length / 2);
        value[65] = _nameNamespaceOverride ?? (byte)nameNamespace;
        text.CopyTo(value, 66);

        return AddResidentAttribute((uint)MftAttributeType.FileName, value);
    }

    public MftRecordBuilder AddDataResident(byte[] content)
        => AddResidentAttribute((uint)MftAttributeType.Data, content);

    /// <summary>A named stream: real bytes that belong to no directory entry.</summary>
    public MftRecordBuilder AddNamedStream(string streamName, byte[] content)
    {
        byte[] name = Encoding.Unicode.GetBytes(streamName);
        byte[] value = new byte[content.Length];
        content.CopyTo(value, 0);

        return AddResidentAttribute((uint)MftAttributeType.Data, value, name);
    }

    /// <summary>
    /// A non-resident DATA attribute. An ordinary one has a 64-byte header; a compressed or
    /// sparse one is 72 bytes, so its total-allocation slot at 0x40 sits inside the header
    /// and the runs start after it. Either way the run list follows the header and ends with
    /// its own eight-byte terminator.
    /// </summary>
    public MftRecordBuilder AddDataNonResident(
        long dataSize,
        long allocatedSize,
        ulong lowestVcn = 0,
        long totalAllocated = 0,
        bool compressed = false,
        bool sparse = false,
        ushort? mappingPairsOffset = null,
        int? headerLength = null)
    {
        bool statesTotal = compressed || sparse;
        int header = headerLength ?? (statesTotal ? ExtendedHeader : NonResidentHeader);

        byte[] value = new byte[0x48];
        value[8] = 1;

        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x20), mappingPairsOffset ?? (ushort)header);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40), (ulong)allocatedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(48), (ulong)dataSize);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(56), (ulong)dataSize);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(64), (ulong)totalAllocated);

        ushort flags = 0;
        if (compressed)
        {
            flags |= MftRecordParser.AttributeFlagCompressed;
        }

        if (sparse)
        {
            flags |= MftRecordParser.AttributeFlagSparse;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(12), flags);

        // Truncating the header is how a test states an attribute with no total-allocation
        // field, and the runs move with it.
        byte[] truncated = new byte[header + MappingTerminatorLength];
        value.AsSpan(0, header).CopyTo(truncated);
        return AddNonResidentAttribute((uint)MftAttributeType.Data, truncated);
    }

    /// <summary>A non-resident DATA attribute stating a size past long.MaxValue.</summary>
    public MftRecordBuilder AddOversizedNonResidentData(ulong fileSize, bool compressed = false, ulong lowestVcn = 0)
    {
        int header = compressed ? ExtendedHeader : NonResidentHeader;
        byte[] value = new byte[header + MappingTerminatorLength];

        value[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x20), (ushort)header);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(16), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(40), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(48), fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(12), compressed ? MftRecordParser.AttributeFlagCompressed : (ushort)0);

        if (compressed)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(64), ulong.MaxValue);
        }

        return AddNonResidentAttribute((uint)MftAttributeType.Data, value);
    }

    /// <summary>
    /// Marks the record as split but names a fragment that is not on the volume: a valid
    /// list entry pointing at record 9999, which the volume answers by returning a lower
    /// live record. Resolution must decline rather than attach that record's metadata.
    /// </summary>
    public MftRecordBuilder AddAttributeList()
        => AddAttributeListEntry((uint)MftAttributeType.Data, refOrdinal: 9999, refSequence: 1, attributeId: 0);

    /// <summary>
    /// Appends one entry to this record's resident attribute list: a valid entry naming
    /// the attribute segment at (<paramref name="refOrdinal"/>, <paramref name="refSequence"/>).
    /// Every attribute this builder writes carries instance zero unless
    /// <see cref="WithAttributeInstance"/> says otherwise, so entries use zero by default.
    /// </summary>
    public MftRecordBuilder AddAttributeListEntry(
        uint type,
        ulong refOrdinal,
        ushort refSequence,
        ushort attributeId,
        string attributeName = "",
        ulong lowestVcn = 0)
    {
        _listEntries.Add((type, refOrdinal, refSequence, attributeId, attributeName, lowestVcn));
        return this;
    }

    /// <summary>A resident list with these exact value bytes: malformed when they are not valid entries.</summary>
    public MftRecordBuilder AddAttributeListRaw(byte[] value)
    {
        _listRaw = value;
        return this;
    }

    /// <summary>
    /// A non-resident attribute list: its value lives in volume clusters. Each run is
    /// (<paramref name="runs"/> element): an LCN and a cluster count, or LCN -1 for a
    /// sparse hole. Tests serve the bytes through <see cref="FixtureRecordSource.AddVolumeData"/>.
    /// </summary>
    public MftRecordBuilder AddAttributeListNonResident(long fileSize, params (long lcn, ulong clusters)[] runs)
    {
        _listNonResident = (fileSize, runs);
        return this;
    }

    /// <summary>
    /// A non-resident list segment covering <paramref name="lowestVcn"/>..<paramref name="highestVcn"/>.
    /// Only the VCN-zero segment states <paramref name="fileSize"/>; continuations pass null.
    /// Each run is an LCN and a cluster count, or LCN -1 for a sparse hole.
    /// </summary>
    public MftRecordBuilder AddAttributeListNonResidentSegment(
        ulong lowestVcn,
        ulong highestVcn,
        long? fileSize,
        params (long lcn, ulong clusters)[] runs)
    {
        _listNonResidentSegments.Add((lowestVcn, highestVcn, fileSize, runs));
        return this;
    }

    /// <summary>The attribute instance the next attribute carries. Fixture attributes default to zero.</summary>
    public MftRecordBuilder WithAttributeInstance(ushort value)
    {
        _nextInstance = value;
        return this;
    }

    /// <summary>Writes an attribute header declaring a length its body does not have.</summary>
    public MftRecordBuilder AddMalformedAttribute(uint type, uint declaredLength, int actualLength)
    {
        byte[] body = new byte[Math.Max(actualLength, ResidentHeader)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, type);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), declaredLength);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x10), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x14), ResidentHeader);

        _buffer.AsSpan(_attributeOffset, actualLength).Clear();
        body.AsSpan(0, actualLength).CopyTo(_buffer.AsSpan(_attributeOffset));
        _attributeOffset += actualLength;
        return this;
    }

    /// <summary>Produces the record as the filesystem would store it.</summary>
    public byte[] Build()
    {
        FlushAttributeList();

        int end = (_attributeOffset + 7) & ~7;

        if (_endMarker)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(end), 0xFFFF_FFFF);
        }

        if (_signature)
        {
            "FILE"u8.CopyTo(_buffer);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(UsaOffsetField), (ushort)_usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(UsaCountField), (ushort)(_usaCountOverride ?? _usaCount));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(SequenceNumberField), _sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(FirstAttributeField), (ushort)_firstAttribute);

        ushort flags = 0;
        if (_inUse)
        {
            flags |= 0x0001;
        }

        if (_isDirectory)
        {
            flags |= 0x0002;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(FlagsField), flags);

        BinaryPrimitives.WriteUInt32LittleEndian(
            _buffer.AsSpan(BytesInUseField),
            (uint)(_bytesInUseOverride ?? (end + 4)));

        BinaryPrimitives.WriteUInt32LittleEndian(
            _buffer.AsSpan(BytesAllocatedField),
            (uint)(_bytesAllocatedOverride ?? _buffer.Length));

        BinaryPrimitives.WriteUInt64LittleEndian(
            _buffer.AsSpan(BaseRecordField),
            (_baseOrdinal & 0x0000_FFFF_FFFF_FFFFUL) | ((ulong)_baseSequence << 48));

        WriteUpdateSequence();
        return _buffer;
    }

    /// <summary>
    /// Emits the pending attribute list, if any, as one attribute just before the
    /// end-of-attributes marker. Resident entries are quadword-aligned, as on-disk lists are.
    /// </summary>
    private void FlushAttributeList()
    {
        if (_listRaw is not null)
        {
            AddResidentAttribute((uint)MftAttributeType.AttributeList, _listRaw);
            _listRaw = null;
            return;
        }

        if (_listEntries.Count > 0)
        {
            byte[] value = EncodeListEntries(_listEntries);
            _listEntries.Clear();
            AddResidentAttribute((uint)MftAttributeType.AttributeList, value);
            return;
        }

        if (_listNonResident is { } nonResident)
        {
            byte[] pairs = EncodeMappingPairs(nonResident.runs);
            int header = 0x40;
            byte[] attribute = new byte[header + pairs.Length];
            attribute[8] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0A), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0E), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x10), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x28), (ulong)nonResident.fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x30), (ulong)nonResident.fileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x38), (ulong)nonResident.fileSize);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), (ushort)header);
            pairs.CopyTo(attribute, header);

            _listNonResident = null;
            AddNonResidentAttribute((uint)MftAttributeType.AttributeList, attribute);
        }

        foreach ((ulong lowestVcn, ulong highestVcn, long? fileSize, (long lcn, ulong clusters)[] runs) in _listNonResidentSegments)
        {
            byte[] pairs = EncodeMappingPairs(runs);
            int header = 0x40;
            byte[] attribute = new byte[header + pairs.Length];
            attribute[8] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0A), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0E), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x10), lowestVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x18), highestVcn);
            if (fileSize is { } size)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x28), (ulong)size);
                BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x30), (ulong)size);
                BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(0x38), (ulong)size);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), (ushort)header);
            pairs.CopyTo(attribute, header);

            AddNonResidentAttribute((uint)MftAttributeType.AttributeList, attribute);
        }

        _listNonResidentSegments.Clear();
    }

    /// <summary>Encodes list entries exactly as the builder writes them into a record.</summary>
    internal static byte[] EncodeListEntries(
        IEnumerable<(uint type, ulong refOrdinal, ushort refSequence, ushort attributeId, string attributeName, ulong lowestVcn)> entries)
    {
        List<byte> value = [];
        foreach ((uint type, ulong refOrdinal, ushort refSequence, ushort attributeId, string attributeName, ulong lowestVcn) in entries)
        {
            byte[] name = Encoding.Unicode.GetBytes(attributeName);
            int recordLength = (26 + name.Length + 7) & ~7;
            byte[] entry = new byte[recordLength];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, type);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)recordLength);
            entry[6] = (byte)attributeName.Length;
            entry[7] = 26;
            BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), lowestVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(
                entry.AsSpan(16),
                (refOrdinal & 0x0000_FFFF_FFFF_FFFFUL) | ((ulong)refSequence << 48));
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(24), attributeId);
            name.CopyTo(entry, 26);
            value.AddRange(entry);
        }

        return [.. value];
    }

    /// <summary>Encodes runs as mapping pairs: minimal length bytes plus minimal signed offset deltas.</summary>
    internal static byte[] EncodeMappingPairs((long lcn, ulong clusters)[] runs)
    {
        List<byte> pairs = [];
        long previous = 0;

        foreach ((long lcn, ulong clusters) in runs)
        {
            if (lcn < 0)
            {
                pairs.Add((byte)LengthSize(clusters));
                pairs.AddRange(LittleEndian(clusters, LengthSize(clusters)));
                continue;
            }

            long delta = lcn - previous;
            previous = lcn;
            int offsetSize = SignedSize(delta);
            int lengthSize = LengthSize(clusters);
            pairs.Add((byte)((offsetSize << 4) | lengthSize));
            pairs.AddRange(LittleEndian(clusters, lengthSize));
            pairs.AddRange(LittleEndian((ulong)delta & (offsetSize == 8 ? ulong.MaxValue : (1UL << (8 * offsetSize)) - 1), offsetSize));
        }

        pairs.Add(0);
        return [.. pairs];
    }

    private static int LengthSize(ulong value)
    {
        for (int size = 1; size <= 8; size++)
        {
            if (value < (size == 8 ? ulong.MaxValue : 1UL << (8 * size)))
            {
                return size;
            }
        }

        return 8;
    }

    private static int SignedSize(long value)
    {
        for (int size = 1; size <= 8; size++)
        {
            long min = size == 8 ? long.MinValue : -(1L << ((8 * size) - 1));
            long max = size == 8 ? long.MaxValue : (1L << ((8 * size) - 1)) - 1;
            if (value >= min && value <= max)
            {
                return size;
            }
        }

        return 8;
    }

    private static byte[] LittleEndian(ulong value, int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[i] = (byte)(value >> (8 * i));
        }

        return bytes;
    }
    /// <summary>
    /// Produces the record as <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns it: the on-disk
    /// bytes with the update sequence already applied, i.e. the saved words written back
    /// over the unit tails. A healthy record in this form carries ordinary content in its
    /// tails, not the tag, so parsing it as <see cref="MftRecordRepresentation.Raw"/>
    /// reports it torn.
    /// </summary>
    public byte[] BuildFixupApplied()
    {
        byte[] raw = Build();
        ApplyFixup(raw);
        return raw;
    }

    /// <summary>
    /// Simulates the kernel's fixup: copies each saved word over its unit tail, as the
    /// filesystem does before handing the record to the caller.
    /// </summary>
    public static void ApplyFixup(byte[] record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(UsaOffsetField));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(UsaCountField));

        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * MftRecordParser.UpdateSequenceStride) - 2;
            if (tail + 2 > record.Length || usaOffset + (i * 2) + 2 > record.Length)
            {
                break;
            }

            ushort saved = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + (i * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), saved);
        }
    }

    /// <summary>
    /// The array holds a tag followed by what each 512-byte unit really ended with. The word
    /// that is really there is the one that goes into the save area; the tag then goes over
    /// it. That is the order NTFS writes them in, so a record this builder produces is
    /// byte-for-byte what the filesystem would leave on disk.
    /// </summary>
    private void WriteUpdateSequence()
    {
        int count = _usaCountOverride ?? _usaCount;

        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_usaOffset), _usaTag);

        for (int i = 1; i < count; i++)
        {
            int tail = (i * MftRecordParser.UpdateSequenceStride) - 2;
            if (tail + 2 > _buffer.Length)
            {
                continue;
            }

            ushort prior = _saved.TryGetValue(i - 1, out ushort chosen)
                ? chosen
                : BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(tail));

            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_usaOffset + (i * 2)), prior);

            ushort live = _corruptTail == i - 1 ? (ushort)(_usaTag ^ 0xFFFF) : _usaTag;
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(tail), live);
        }
    }

    private MftRecordBuilder AddResidentAttribute(uint type, byte[] value, byte[]? name = null)
    {
        // A named attribute states where its name is, and the value starts on the next
        // 8-byte boundary after it.
        int nameOffset = name is null ? 0 : ResidentHeader;
        int valueOffset = name is null ? ResidentHeader : AlignUp(ResidentHeader + name.Length);
        int length = AlignUp(valueOffset + value.Length);

        byte[] body = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, type);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)length);
        body[8] = 0;

        if (name is not null)
        {
            body[9] = (byte)(name.Length / 2);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x0A), (ushort)nameOffset);
            name.CopyTo(body, nameOffset);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x0E), _nextInstance);
        _nextInstance = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(20), (ushort)valueOffset);
        value.CopyTo(body, valueOffset);

        Append(body);
        return this;
    }

    private MftRecordBuilder AddNonResidentAttribute(uint type, byte[] value)
    {
        int length = AlignUp(value.Length);
        byte[] body = new byte[length];
        value.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body, type);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0x0E), _nextInstance);
        _nextInstance = 0;

        Append(body);
        return this;
    }

    private void Append(byte[] body)
    {
        body.CopyTo(_buffer, _attributeOffset);
        _attributeOffset += body.Length;
    }

    private static ulong ToWindowsTime(DateTimeOffset? value)
        => value is null ? 0 : (ulong)value.Value.UtcDateTime.ToFileTimeUtc();

    private static int AlignUp(int value) => (value + 7) & ~7;
}

/// <summary>Builds the buffer <c>FSCTL_GET_NTFS_VOLUME_DATA</c> returns.</summary>
internal static class MftGeometryFixtures
{
    private const int BytesPerSectorOffset = 0x28;
    private const int BytesPerClusterOffset = 0x2C;
    private const int RecordSizeOffset = 0x30;
    private const int ValidDataLengthOffset = 0x38;
    private const int ExtendedByteCountOffset = 0x60;
    private const int MajorVersionOffset = 0x64;
    private const int MinorVersionOffset = 0x66;

    /// <summary>
    /// NTFS 3.1, 4 KiB sectors and clusters, 1 KiB MFT records: the ordinary combination,
    /// and the one that shows a parser reading fixups at the sector size would be wrong.
    /// </summary>
    public static byte[] Volume(
        uint recordCount = 4096,
        uint bytesPerSector = 4096,
        uint recordSize = 1024,
        int majorVersion = 3,
        int minorVersion = 1,
        long? validDataLength = null,
        int? returnedLength = null,
        uint? extendedByteCount = null,
        uint bytesPerCluster = 4096,
        long numberSectors = 0,
        long totalClusters = 0)
    {
        byte[] buffer = new byte[512];

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x08), numberSectors);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x10), totalClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(BytesPerSectorOffset), bytesPerSector);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(BytesPerClusterOffset), bytesPerCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(RecordSizeOffset), recordSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(ValidDataLengthOffset), validDataLength ?? ((long)recordCount * recordSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ExtendedByteCountOffset), extendedByteCount ?? 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(MajorVersionOffset), (ushort)majorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(MinorVersionOffset), (ushort)minorVersion);

        return returnedLength is null ? buffer : buffer[..returnedLength.Value];
    }
}

/// <summary>
/// An <see cref="IMftRecordSource"/> served from a list, with the downward search semantics
/// a real volume has: a request for an ordinal answers with the highest registered ordinal at
/// or below it, and zero once there is none.
/// </summary>
internal sealed class FixtureRecordSource : IMftRecordSource, IMftVolumeReader
{
    private readonly List<KeyValuePair<ulong, byte[]>> _records = [];
    private readonly List<KeyValuePair<long, byte[]>> _volume = [];

    public FixtureRecordSource(MftGeometry geometry) => Geometry = geometry;

    public MftGeometry Geometry { get; }

    /// <summary>
    /// On-disk bytes by default: the builder writes the tag into every unit tail, so the
    /// raw torn checks apply. Tests modelling kernel-decoded output set this to
    /// <see cref="MftRecordRepresentation.FixupApplied"/>.
    /// </summary>
    public MftRecordRepresentation Representation { get; set; } = MftRecordRepresentation.Raw;

    public int Reads { get; private set; }

    /// <summary>Ordinals requested, in order, so a test can assert the walk really descends.</summary>
    public List<ulong> Requested { get; } = [];

    /// <summary>Every registered record, for tests that need to build a variant volume.</summary>
    public IReadOnlyList<KeyValuePair<ulong, byte[]>> Entries => _records;

    /// <summary>Fails every read once this many have succeeded. Null never fails.</summary>
    public int? FailAfterReads { get; set; }

    /// <summary>Answers with this ordinal instead of the real one, to model a broken source.</summary>
    public ulong? ForcedOrdinal { get; set; }

    public FixtureRecordSource Add(ulong ordinal, byte[] record)
    {
        int index = _records.Count;
        while (index > 0 && _records[index - 1].Key > ordinal)
        {
            index--;
        }

        _records.Insert(index, new KeyValuePair<ulong, byte[]>(ordinal, record));
        return this;
    }

    public bool TryReadRecord(
        ulong ordinal,
        byte[] destination,
        out ulong actualOrdinal,
        out int payloadLength,
        out string? failureReason)
    {
        Reads++;
        Requested.Add(ordinal);
        failureReason = null;

        if (FailAfterReads is { } limit && Reads > limit)
        {
            actualOrdinal = 0;
            payloadLength = 0;
            failureReason = "the fixture source was told to fail.";
            return false;
        }

        int index = _records.Count - 1;
        while (index >= 0 && _records[index].Key > ordinal)
        {
            index--;
        }

        if (index < 0)
        {
            actualOrdinal = 0;
            payloadLength = 0;
            return true;
        }

        byte[] record = _records[index].Value;
        actualOrdinal = ForcedOrdinal ?? _records[index].Key;
        payloadLength = record.Length;
        record.CopyTo(destination, MftGeometry.OutputHeaderLength);
        return true;
    }

    public void Dispose()
    {
    }

    /// <summary>Serves raw volume bytes at <paramref name="byteOffset"/> for non-resident list tests.</summary>
    public FixtureRecordSource AddVolumeData(long byteOffset, byte[] data)
    {
        _volume.Add(new KeyValuePair<long, byte[]>(byteOffset, data));
        return this;
    }

    public bool TryReadVolume(long volumeByteOffset, byte[] buffer, int offset, int count, out string? failure)
    {
        failure = null;

        if (volumeByteOffset < 0 || offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            failure = $"The fixture volume read at byte {volumeByteOffset} for {count} bytes does not fit its buffer.";
            return false;
        }

        int done = 0;
        while (done < count)
        {
            long at = volumeByteOffset + done;
            KeyValuePair<long, byte[]>? hit = null;

            foreach (KeyValuePair<long, byte[]> segment in _volume)
            {
                if (at >= segment.Key && at < segment.Key + segment.Value.Length)
                {
                    hit = segment;
                    break;
                }
            }

            if (hit is null)
            {
                failure = $"The fixture volume holds no bytes at {at}.";
                return false;
            }

            int fromSegment = (int)(at - hit.Value.Key);
            int available = hit.Value.Value.Length - fromSegment;
            int wanted = Math.Min(available, count - done);
            Array.Copy(hit.Value.Value, fromSegment, buffer, offset + done, wanted);
            done += wanted;
        }

        return true;
    }
}