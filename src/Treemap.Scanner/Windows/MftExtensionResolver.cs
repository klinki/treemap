namespace Treemap.Scanning.Windows;

/// <summary>
/// Reads raw volume bytes at a byte offset, for non-resident attribute-list values.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMftRecordSource"/> so record-only sources keep working:
/// a source without volume access simply cannot resolve a non-resident list, which the
/// resolver reports honestly rather than guessing at.
/// </remarks>
internal interface IMftVolumeReader
{
    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="volumeByteOffset"/>.</summary>
    bool TryReadVolume(long volumeByteOffset, byte[] buffer, int offset, int count, out string? failure);
}

/// <summary>
/// Resolves one logical file whose attributes are split across MFT fragment records.
/// </summary>
/// <remarks>
/// <para>
/// Per-logical-file temporary state only: the base copy, cached fragments and validated
/// identities. Nothing is retained across files, so there is no second whole-MFT graph
/// in memory. A base that resolves keeps its ordinal; its fragments are never emitted
/// on their own.
/// </para>
/// <para>
/// The attribute list itself always lives in the base record: resident, or non-resident
/// with its mapping array fitting the base record (LowestVCN zero, runs covering the
/// value). A list entry naming ATTRIBUTE_LIST, or a fragment carrying its own list, is
/// a shape real volumes do not produce, so it declines as malformed rather than
/// following an invented hierarchy. Entries are processed once, in list order.
/// </para>
/// <para>
/// Validation is exact: the volume may answer a lower live ordinal for a requested
/// reference, so the returned ordinal must equal the requested one and the fragment's
/// sequence must equal the referenced one. Each fragment must be in use, must flag
/// itself as an extension, and must name this base's ordinal and sequence as its owner.
/// Each followed entry must match an attribute identity (type, name, instance, VCN) in the
/// fragment it names, including every repeat reference and every self reference. Anything
/// else declines honestly so the portable backend retries, rather than attaching stale
/// metadata to another file.
/// </para>
/// <para>
/// Merges are selective: only the listed attribute's contribution (names, flags, times,
/// sizes) folds in. Unlisted fragment metadata never pollutes the inventory. Sizes follow
/// the existing continuation rule: only the unnamed <c>DATA</c> segment at VCN zero states
/// the file's length. Later continuations never overwrite it, and sizes are never summed.
/// Named streams never contribute. Conflicting duplicate VCN-zero sizes or standard
/// information times decline rather than first-win.
/// </para>
/// </remarks>
internal static class MftLogicalFileResolver
{
    private const uint TypeAttributeList = 0x20;
    private const uint TypeStandardInformation = 0x10;
    private const uint TypeFileName = 0x30;
    private const uint TypeData = 0x80;

    /// <summary>
    /// Merges <paramref name="baseRecord"/> with every fragment its attribute list names
    /// into <paramref name="merged"/>. Records without a list are copied unchanged.
    /// Returns false with a reason when the fragments cannot be followed honestly.
    /// Throws <see cref="OperationCanceledException"/> on cancellation, never a fallback.
    /// </summary>
    public static bool TryResolve(
        MftRecordInfo baseRecord,
        IMftRecordSource source,
        CancellationToken cancellationToken,
        MftRecordInfo merged,
        out string? failure)
    {
        CopyBase(baseRecord, merged);

        if (!baseRecord.HasAttributeList)
        {
            failure = null;
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        ulong baseOrdinal = baseRecord.Ordinal;
        ushort baseSequence = baseRecord.SequenceNumber;

        List<MftAttributeListEntry> initial = new(baseRecord.AttributeListEntries);

        if (baseRecord.AttributeListSegments.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (baseRecord.AttributeListEntries.Count > 0)
            {
                failure = Wrap(baseOrdinal, "it holds both a resident list and list segments outside the record, which this backend will not reconcile.");
                return false;
            }

            if (!TryReadListValue(baseRecord, source, cancellationToken, initial, out string? listFailure))
            {
                failure = Wrap(baseOrdinal, listFailure);
                return false;
            }
        }

        if (initial.Count == 0)
        {
            failure = Wrap(baseOrdinal, "it states split attributes but names no segment to follow.");
            return false;
        }

        foreach (MftAttributeListEntry entry in initial)
        {
            if (entry.AttributeType == TypeAttributeList)
            {
                failure = Wrap(baseOrdinal, "its list refers to ATTRIBUTE_LIST itself, which always lives in the base record and is never followed.");
                return false;
            }
        }

        Dictionary<ulong, MftRecordInfo> fragments = [];
        HashSet<MftAttributeListEntry> validated = [];
        byte[] buffer = new byte[source.Geometry.BufferLength];

        foreach (MftAttributeListEntry entry in initial)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.ReferencedOrdinal == baseOrdinal)
            {
                if (entry.ReferencedSequence != baseSequence)
                {
                    failure = Wrap(baseOrdinal, $"its list refers to itself at sequence {entry.ReferencedSequence}, not {baseSequence}.");
                    return false;
                }

                if (!HasMatchingIdentity(baseRecord, entry))
                {
                    failure = Wrap(baseOrdinal, $"its list refers to itself for attribute 0x{entry.AttributeType:X} with identity {entry.AttributeId}, which this record does not hold.");
                    return false;
                }

                validated.Add(entry);
                continue;
            }

            if (!IsOrdinalInRange(entry.ReferencedOrdinal, source.Geometry))
            {
                failure = Wrap(baseOrdinal, $"its list refers to MFT record {entry.ReferencedOrdinal}, outside the 0..{source.Geometry.RecordCount - 1} records this volume holds.");
                return false;
            }

            if (fragments.TryGetValue(entry.ReferencedOrdinal, out MftRecordInfo? cached))
            {
                if (cached.SequenceNumber != entry.ReferencedSequence)
                {
                    failure = Wrap(baseOrdinal, $"fragment record {entry.ReferencedOrdinal} is at sequence {cached.SequenceNumber}, not the referenced {entry.ReferencedSequence}.");
                    return false;
                }

                if (cached.BaseOrdinal != baseOrdinal || cached.BaseSequence != baseSequence)
                {
                    failure = Wrap(baseOrdinal, $"fragment record {entry.ReferencedOrdinal} extends MFT record {cached.BaseOrdinal}, not {baseOrdinal}.");
                    return false;
                }

                if (!HasMatchingIdentity(cached, entry))
                {
                    failure = Wrap(baseOrdinal, $"fragment record {entry.ReferencedOrdinal} holds no attribute 0x{entry.AttributeType:X} with identity {entry.AttributeId}.");
                    return false;
                }

                if (!validated.Add(entry))
                {
                    continue;
                }

                if (!TryMergeSelected(merged, cached, entry, out string? mergeFailure))
                {
                    failure = Wrap(baseOrdinal, mergeFailure);
                    return false;
                }

                continue;
            }

            MftRecordInfo fragment = new();
            if (!TryReadFragment(source, entry, baseOrdinal, baseSequence, fragment, buffer, cancellationToken, out string? fragmentFailure))
            {
                failure = Wrap(baseOrdinal, fragmentFailure);
                return false;
            }

            if (fragment.HasAttributeList || fragment.AttributeListEntries.Count > 0 || fragment.AttributeListSegments.Count > 0)
            {
                failure = Wrap(baseOrdinal, $"fragment record {fragment.Ordinal} holds its own attribute list, which real volumes keep in the base record alone.");
                return false;
            }

            fragments.Add(entry.ReferencedOrdinal, fragment);
            validated.Add(entry);

            if (!TryMergeSelected(merged, fragment, entry, out string? firstMergeFailure))
            {
                failure = Wrap(baseOrdinal, firstMergeFailure);
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static string Wrap(ulong baseOrdinal, string? detail)
        => $"MFT record {baseOrdinal} has split attributes in fragment records that could not be followed: {detail}";

    private static bool IsOrdinalInRange(ulong ordinal, MftGeometry geometry)
        => ordinal < (ulong)geometry.RecordCount;

    private static void CopyBase(MftRecordInfo baseRecord, MftRecordInfo merged)
    {
        merged.Reset(baseRecord.Ordinal);
        merged.InUse = baseRecord.InUse;
        merged.IsDirectory = baseRecord.IsDirectory;
        merged.SequenceNumber = baseRecord.SequenceNumber;
        merged.OrAttributes((uint)baseRecord.Attributes);
        merged.LastWriteTime = baseRecord.LastWriteTime;
        merged.LastWriteFromStandardInformation = baseRecord.LastWriteFromStandardInformation;
        if (baseRecord.HasUnnamedData)
        {
            merged.HasUnnamedData = true;
            merged.DataIsResident = baseRecord.DataIsResident;
            merged.LogicalSize = baseRecord.LogicalSize;
            merged.AllocatedSize = baseRecord.AllocatedSize;
        }

        foreach (MftName name in baseRecord.Names)
        {
            merged.Names.Add(name);
        }

        foreach (MftStdContribution std in baseRecord.StdContributions)
        {
            merged.StdContributions.Add(std);
        }

        foreach (MftFileNameContribution fileName in baseRecord.FileNameContributions)
        {
            merged.FileNameContributions.Add(fileName);
        }

        foreach (MftDataContribution data in baseRecord.DataContributions)
        {
            merged.DataContributions.Add(data);
        }
    }

    /// <summary>
    /// Merges only the listed attribute's contribution. Unlisted fragment metadata never
    /// pollutes names, flags, times or sizes. A selected identity matching more than one
    /// contribution, conflicting VCN-zero sizes, or conflicting defined standard
    /// information times decline rather than first-win.
    /// </summary>
    internal static bool TryMergeSelected(
        MftRecordInfo merged,
        MftRecordInfo fragment,
        MftAttributeListEntry entry,
        out string? failure)
    {
        failure = null;

        if (entry.AttributeType == TypeStandardInformation && entry.AttributeName.Length == 0)
        {
            List<MftStdContribution> matches = [];
            foreach (MftStdContribution std in fragment.StdContributions)
            {
                if (std.Identity.AttributeType == TypeStandardInformation &&
                    std.Identity.AttributeName.Length == 0 &&
                    std.Identity.AttributeId == entry.AttributeId)
                {
                    matches.Add(std);
                }
            }

            if (matches.Count == 0)
            {
                // Identity validated but no contribution (for example a named STD this
                // backend does not fold): nothing to merge.
                return true;
            }

            if (matches.Count > 1)
            {
                failure = $"fragment record {fragment.Ordinal} holds {matches.Count} STANDARD_INFORMATION attributes with identity {entry.AttributeId}.";
                return false;
            }

            MftStdContribution selected = matches[0];
            merged.OrAttributes(selected.AttributeBits);
            merged.StdContributions.Add(selected);

            if (selected.LastWriteTime is not null)
            {
                if (merged.LastWriteFromStandardInformation &&
                    merged.LastWriteTime is not null &&
                    merged.LastWriteTime != selected.LastWriteTime)
                {
                    failure = $"fragment record {fragment.Ordinal} states a STANDARD_INFORMATION time that conflicts with an earlier one.";
                    return false;
                }

                merged.LastWriteTime = selected.LastWriteTime;
                merged.LastWriteFromStandardInformation = true;
            }

            return true;
        }

        if (entry.AttributeType == TypeFileName && entry.AttributeName.Length == 0)
        {
            List<MftFileNameContribution> matches = [];
            foreach (MftFileNameContribution contribution in fragment.FileNameContributions)
            {
                if (contribution.Identity.AttributeType == TypeFileName &&
                    contribution.Identity.AttributeName.Length == 0 &&
                    contribution.Identity.AttributeId == entry.AttributeId)
                {
                    matches.Add(contribution);
                }
            }

            if (matches.Count == 0)
            {
                failure = $"fragment record {fragment.Ordinal} holds no FILE_NAME with identity {entry.AttributeId} to merge.";
                return false;
            }

            if (matches.Count > 1)
            {
                failure = $"fragment record {fragment.Ordinal} holds {matches.Count} FILE_NAME attributes with identity {entry.AttributeId}.";
                return false;
            }

            MftFileNameContribution selected = matches[0];
            merged.OrAttributes(selected.AttributeBits);

            bool already = false;
            foreach (MftName existing in merged.Names)
            {
                if (existing.Equals(selected.Name))
                {
                    already = true;
                    break;
                }
            }

            if (!already)
            {
                merged.Names.Add(selected.Name);
            }

            merged.FileNameContributions.Add(selected);

            if (selected.LastWriteTime is not null &&
                merged.LastWriteTime is null)
            {
                merged.LastWriteTime = selected.LastWriteTime;
            }

            return true;
        }

        if (entry.AttributeType == TypeData && entry.AttributeName.Length == 0 && entry.LowestVcn == 0)
        {
            List<MftDataContribution> matches = [];
            foreach (MftDataContribution data in fragment.DataContributions)
            {
                if (data.Identity.AttributeType == TypeData &&
                    data.Identity.AttributeName.Length == 0 &&
                    data.Identity.AttributeId == entry.AttributeId &&
                    data.Identity.LowestVcn == 0 &&
                    data.IsUnnamed)
                {
                    matches.Add(data);
                }
            }

            if (matches.Count == 0)
            {
                // Validated identity exists but carries no size this backend folds
                // (for example a continuation already handled as no-merge): nothing to do.
                return true;
            }

            if (matches.Count > 1)
            {
                failure = $"fragment record {fragment.Ordinal} holds {matches.Count} unnamed DATA segments at VCN zero with identity {entry.AttributeId}.";
                return false;
            }

            MftDataContribution value = matches[0];
            if (!merged.HasUnnamedData)
            {
                merged.HasUnnamedData = true;
                merged.DataIsResident = value.IsResident;
                merged.LogicalSize = value.LogicalSize;
                merged.AllocatedSize = value.AllocatedSize;
                merged.DataContributions.Add(value);
                return true;
            }

            if (merged.LogicalSize != value.LogicalSize || merged.AllocatedSize != value.AllocatedSize)
            {
                failure = $"fragment record {fragment.Ordinal} states an unnamed DATA size of {value.LogicalSize} bytes that conflicts with an earlier {merged.LogicalSize} bytes.";
                return false;
            }

            return true;
        }

        // Continuation DATA (VCN != 0), named streams and unmodeled types contribute
        // nothing to the file's sizes, flags, times or names. Validation already proved
        // the listed attribute exists; merging nothing keeps unlisted metadata out.
        return true;
    }

    private static bool TryReadFragment(
        IMftRecordSource source,
        MftAttributeListEntry entry,
        ulong baseOrdinal,
        ushort baseSequence,
        MftRecordInfo fragment,
        byte[] buffer,
        CancellationToken cancellationToken,
        out string? failure)
    {
        failure = null;

        cancellationToken.ThrowIfCancellationRequested();

        if (!source.TryReadRecord(entry.ReferencedOrdinal, buffer, out ulong actual, out int payloadLength, out string? readFailure))
        {
            failure = $"fragment record {entry.ReferencedOrdinal} could not be read: {readFailure}";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (actual == 0)
        {
            failure = $"fragment record {entry.ReferencedOrdinal} is not allocated.";
            return false;
        }

        // The volume answers below the request when the exact record is vacant: that is
        // a different record, not the linked metadata.
        if (actual != entry.ReferencedOrdinal)
        {
            failure = $"the volume returned MFT record {actual} for fragment {entry.ReferencedOrdinal}, which does not identify the linked metadata.";
            return false;
        }

        if (payloadLength != (int)source.Geometry.RecordSize)
        {
            failure = $"fragment record {actual} returned {payloadLength} bytes, not the {source.Geometry.RecordSize} bytes every record holds.";
            return false;
        }

        ReadOnlySpan<byte> payload = buffer.AsSpan(MftGeometry.OutputHeaderLength, payloadLength);

        if (!MftRecordParser.TryParse(actual, payload, fragment, out string? parseFailure, source.Representation))
        {
            failure = $"fragment record {actual} will not validate: {parseFailure}";
            return false;
        }

        if (!fragment.InUse)
        {
            failure = $"fragment record {actual} is not in use.";
            return false;
        }

        if (!fragment.IsExtension)
        {
            failure = $"MFT record {actual} does not extend another record.";
            return false;
        }

        if (fragment.BaseOrdinal != baseOrdinal || fragment.BaseSequence != baseSequence)
        {
            failure = $"fragment record {actual} extends MFT record {fragment.BaseOrdinal}, not {baseOrdinal}.";
            return false;
        }

        if (fragment.SequenceNumber != entry.ReferencedSequence)
        {
            failure = $"fragment record {actual} is at sequence {fragment.SequenceNumber}, not the referenced {entry.ReferencedSequence}.";
            return false;
        }

        if (!HasMatchingIdentity(fragment, entry))
        {
            failure = $"fragment record {actual} holds no attribute 0x{entry.AttributeType:X} with identity {entry.AttributeId}.";
            return false;
        }

        return true;
    }

    private static bool HasMatchingIdentity(MftRecordInfo fragment, MftAttributeListEntry entry)
    {
        int matches = 0;
        foreach (MftAttributeIdentity identity in fragment.AttributeIdentities)
        {
            if (identity.AttributeType != entry.AttributeType || identity.AttributeId != entry.AttributeId)
            {
                continue;
            }

            if (!string.Equals(identity.AttributeName, entry.AttributeName, StringComparison.Ordinal))
            {
                continue;
            }

            // A VCN-zero entry answers only the VCN-zero segment (resident or
            // non-resident at zero). A continuation names its segment exactly, and a
            // resident attribute never answers a non-zero VCN.
            if (identity.LowestVcn != entry.LowestVcn)
            {
                continue;
            }

            if (entry.LowestVcn != 0 && !identity.IsNonResident)
            {
                continue;
            }

            matches++;
        }

        return matches == 1;
    }

    /// <summary>
    /// Reads the one non-resident list value mapped by the base record. The list stream
    /// itself stays base-mapped: exactly one segment at VCN zero whose runs cover the
    /// stated length. Entries may cross cluster or run boundaries; the whole value is
    /// read once in bounded chunks, then parsed strictly.
    /// </summary>
    internal static bool TryReadListValue(
        MftRecordInfo record,
        IMftRecordSource source,
        CancellationToken cancellationToken,
        List<MftAttributeListEntry> entries,
        out string? failure)
    {
        failure = null;
        cancellationToken.ThrowIfCancellationRequested();

        if (record.AttributeListSegments.Count != 1)
        {
            failure = $"MFT record {record.Ordinal} holds {record.AttributeListSegments.Count} list segments outside the record; the list value lives in its base record alone.";
            return false;
        }

        MftAttributeListSegment segment = record.AttributeListSegments[0];

        if (segment.LowestVcn != 0)
        {
            failure = $"MFT record {record.Ordinal} holds its list outside the record but no segment starts it.";
            return false;
        }

        if (!segment.HasFileSize)
        {
            failure = $"MFT record {record.Ordinal} holds its list outside the record but states no list length.";
            return false;
        }

        if (segment.FileSize == 0 || segment.FileSize > long.MaxValue)
        {
            failure = $"MFT record {record.Ordinal} states a list length this scanner cannot represent.";
            return false;
        }

        if (segment.HighestVcn < segment.LowestVcn)
        {
            failure = $"MFT record {record.Ordinal} has a list segment whose highest VCN is below its lowest.";
            return false;
        }

        if (source is not IMftVolumeReader reader)
        {
            failure = $"MFT record {record.Ordinal} holds its list outside the record, which this source cannot read.";
            return false;
        }

        long fileSize = (long)segment.FileSize;
        long volumeBytes = source.Geometry.TotalVolumeBytes;
        if (volumeBytes > 0 && fileSize > volumeBytes)
        {
            failure = $"MFT record {record.Ordinal} states a {fileSize}-byte list larger than the {volumeBytes}-byte volume.";
            return false;
        }

        if (fileSize > int.MaxValue)
        {
            failure = $"MFT record {record.Ordinal} states a {fileSize}-byte list this scanner cannot represent.";
            return false;
        }

        List<MftDataRuns.Run> runs = [];
        if (!MftDataRuns.TryDecode(segment.MappingPairs, record.Ordinal, runs, out string? runsFailure))
        {
            failure = runsFailure;
            return false;
        }

        if (runs.Count == 0)
        {
            failure = $"MFT record {record.Ordinal} has a list segment with no runs.";
            return false;
        }

        ulong clusters = 0;
        try
        {
            checked
            {
                foreach (MftDataRuns.Run run in runs)
                {
                    clusters += run.Clusters;

                    if (run.Lcn >= 0)
                    {
                        long lcnEnd = run.Lcn + (long)run.Clusters;
                        if (source.Geometry.TotalClusters > 0 && lcnEnd > source.Geometry.TotalClusters)
                        {
                            failure = $"MFT record {record.Ordinal} has a list run past the end of the volume.";
                            return false;
                        }

                        long runEndBytes = lcnEnd * (long)source.Geometry.BytesPerCluster;
                        if (volumeBytes > 0 && runEndBytes > volumeBytes)
                        {
                            failure = $"MFT record {record.Ordinal} has a list run past the end of the volume.";
                            return false;
                        }
                    }
                    else
                    {
                        failure = $"MFT record {record.Ordinal} has a sparse or compressed list run this backend will not decode.";
                        return false;
                    }
                }
            }
        }
        catch (OverflowException)
        {
            failure = $"MFT record {record.Ordinal} has a list run this scanner cannot represent.";
            return false;
        }

        ulong span = segment.HighestVcn - segment.LowestVcn + 1;
        if (clusters != span)
        {
            failure = $"MFT record {record.Ordinal} has a list segment covering VCNs {segment.LowestVcn}..{segment.HighestVcn} but runs for {clusters} clusters.";
            return false;
        }

        long coveredBytes;
        try
        {
            checked
            {
                coveredBytes = (long)clusters * source.Geometry.BytesPerCluster;
            }
        }
        catch (OverflowException)
        {
            failure = $"MFT record {record.Ordinal} has list runs this scanner cannot represent.";
            return false;
        }

        if (fileSize > coveredBytes)
        {
            failure = $"MFT record {record.Ordinal} states a {fileSize}-byte list but its runs cover only {coveredBytes} bytes.";
            return false;
        }

        byte[] value = new byte[(int)fileSize];
        long clusterBytes = (long)source.Geometry.BytesPerCluster;
        long sectorBytes = (long)source.Geometry.BytesPerSector;

        // Bounded reads in whole clusters so every volume offset and count stays
        // sector-aligned. One megabyte per read keeps a single outstanding read small.
        long maxChunk = 1024 * 1024;
        if (sectorBytes > 0)
        {
            maxChunk = (maxChunk / sectorBytes) * sectorBytes;
            if (maxChunk <= 0)
            {
                maxChunk = sectorBytes;
            }
        }

        long clustersPerChunk = clusterBytes > 0 ? Math.Max(1, maxChunk / clusterBytes) : 1;
        long filled = 0;

        foreach (MftDataRuns.Run run in runs)
        {
            if (filled >= fileSize)
            {
                break;
            }

            long runBytes = checked((long)run.Clusters * clusterBytes);
            long wantedInRun = Math.Min(runBytes, fileSize - filled);
            long runBase = run.Lcn * clusterBytes;
            long consumed = 0;

            while (consumed < wantedInRun)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long clustersLeft = (runBytes - consumed + clusterBytes - 1) / clusterBytes;
                long sliceClusters = Math.Min(clustersPerChunk, clustersLeft);
                long sliceBytes = sliceClusters * clusterBytes;
                long sliceWanted = Math.Min(sliceBytes, wantedInRun - consumed);

                byte[] chunk = new byte[(int)sliceBytes];
                if (!reader.TryReadVolume(runBase + consumed, chunk, 0, chunk.Length, out string? readFailure))
                {
                    failure = $"the list value of MFT record {record.Ordinal} could not be read: {readFailure}";
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Array.Copy(chunk, 0, value, (int)filled, (int)sliceWanted);
                filled += sliceWanted;
                consumed += sliceWanted;
            }
        }

        if (filled != fileSize)
        {
            failure = $"MFT record {record.Ordinal} has list runs shorter than its {fileSize}-byte list.";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!MftAttributeList.TryParseEntries(value, record.Ordinal, entries, out string? strictFailure))
        {
            failure = strictFailure;
            return false;
        }

        failure = null;
        return true;
    }
}
