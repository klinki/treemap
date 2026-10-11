namespace Treemap.Scanning.Windows;

/// <summary>
/// A read-only stream of MFT records from one volume. Separating this from the backend is
/// what lets the fixture tests drive the real walk, the real descending-ordinal arithmetic
/// and the real validation without a volume handle.
/// </summary>
internal interface IMftRecordSource : IDisposable
{
    /// <summary>Geometry this source's records conform to.</summary>
    MftGeometry Geometry { get; }

    /// <summary>
    /// How this source's records are represented: on-disk bytes still carrying the update
    /// sequence tag in every unit's tail (<see cref="MftRecordRepresentation.Raw"/>), or
    /// kernel-decoded bytes whose tails the filesystem already restored
    /// (<see cref="MftRecordRepresentation.FixupApplied"/>).
    /// </summary>
    MftRecordRepresentation Representation { get; }

    /// <summary>
    /// Finds the record in use with the highest ordinal at or below
    /// <paramref name="ordinal"/>, copies it into <paramref name="destination"/> from
    /// <see cref="MftGeometry.OutputHeaderLength"/>, and returns its ordinal and length.
    /// </summary>
    /// <remarks>
    /// An <paramref name="actualOrdinal"/> of zero means the search ran off the end of the
    /// table: that is the end of the volume's records, not an error. False means the source
    /// could not answer at all.
    /// </remarks>
    bool TryReadRecord(
        ulong ordinal,
        byte[] destination,
        out ulong actualOrdinal,
        out int payloadLength,
        out string? failureReason);
}

/// <summary>Receives each usable record in a walk.</summary>
internal interface IMftRecordVisitor
{
    /// <summary>False stops the walk. With <see cref="FailureReason"/> set, that is a decline.</summary>
    bool Visit(MftRecordInfo record);

    /// <summary>
    /// Why the walk must stop, or <see langword="null"/> when stopping is not a failure.
    /// </summary>
    string? FailureReason { get; }
}

/// <summary>
/// Walks a volume's MFT records from the top down.
/// </summary>
/// <remarks>
/// <para>
/// The downward walk is the only way to enumerate. A record number says nothing about whether
/// that record is in use, so asking for <c>n</c> returns the highest live record at or below
/// it - vacant records are skipped by the kernel, not by this loop. The next request is
/// <c>actual - 1</c>, so progress is guaranteed even when a whole run of records is vacant, and
/// ordinal zero ends the walk.
/// </para>
/// <para>
/// A record that does not validate stops the walk: the caller is better served by the portable
/// retry than by a tree that is confidently wrong.
/// </para>
/// </remarks>
internal static class MftRecordStream
{
    /// <summary>How often the walk offers a progress tick; the collector throttles the rest.</summary>
    private const int ReportInterval = 256;

    /// <summary>
    /// Visits every record the source can produce at or below the last ordinal in the
    /// table. Returns false with a reason for a source failure or a record that will not
    /// validate.
    /// </summary>
    public static bool TryWalk(
        IMftRecordSource source,
        CancellationToken cancellationToken,
        ScanCollector collector,
        string currentPath,
        IMftRecordVisitor visitor,
        out string? failureReason)
    {
        failureReason = null;

        MftGeometry geometry = source.Geometry;
        byte[] buffer = new byte[geometry.BufferLength];
        MftRecordInfo record = new();

        ulong requested = (ulong)geometry.RecordCount - 1;
        long iteration = 0;

        while (requested > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((iteration++ % ReportInterval) == 0)
            {
                // A large table spends a long time before it reaches the first record the
                // caller asked for, so the phase is reported from inside the walk rather
                // than only at its end.
                collector.Report(ScanPhase.Enumerating, currentPath);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!source.TryReadRecord(requested, buffer, out ulong actual, out int payloadLength, out failureReason))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (actual == 0)
            {
                return true;
            }

            if (actual > requested)
            {
                failureReason = $"The volume returned MFT record {actual} while record {requested} was requested, which would not make progress.";
                return false;
            }

            if (payloadLength != (int)geometry.RecordSize)
            {
                // Every record on a volume is the same fixed size, and the returned length is
                // how the volume says so. A shorter answer is not a small record: it is a
                // volume that is not describing itself the way it must.
                failureReason = $"The volume returned {payloadLength} bytes for MFT record {actual}, not the {geometry.RecordSize} bytes every record holds.";
                return false;
            }

            requested = actual - 1;

            // Records 0 to 15 are NTFS's own metadata. Except for record 5 they name no
            // directory entry and are never emitted, and they are stepped over before
            // anything tries to read file attributes out of them. Record 5 is the volume
            // root: a directory like any other, and the anchor of the whole hierarchy.
            if (actual < MftRecordParser.ReservedRecordCount && actual != MftRecordParser.VolumeRootRecord)
            {
                continue;
            }

            ReadOnlySpan<byte> payload = buffer.AsSpan(MftGeometry.OutputHeaderLength, payloadLength);

            if (!MftRecordParser.TryParse(actual, payload, record, out string? parseFailure, source.Representation))
            {
                failureReason = parseFailure;
                return false;
            }

            if (record.InUse && !visitor.Visit(record))
            {
                failureReason = visitor.FailureReason;
                return failureReason is null;
            }
        }

        return true;
    }
}