using Treemap.Scanning.Windows;

namespace Treemap.Scanning;

/// <summary>
/// Answers one file's sizes the ordinary way, for entries whose MFT record does not carry
/// them. Returns false when the filesystem could not be asked either.
/// </summary>
internal delegate bool NtfsSizeQuery(
    string path,
    out long logicalSize,
    out long allocatedSize,
    out DateTimeOffset? lastWriteTime);

/// <summary>
/// Scans NTFS by reading its Master File Table rather than by enumerating directories.
/// </summary>
/// <remarks>
/// <para>
/// A different view of the same tree, not a better one. A volume's Master File Table can be
/// read instead of enumerated, but only by a process allowed to open the volume that way.
/// One that is not declines: it returns false with a reason, the caller discards what was
/// collected and runs the portable backend. It never falls back on its own and never asks for
/// privileges it does not have.
/// </para>
/// <para>
/// Two passes over the table. The first keeps directories, because the tree lives in their
/// parent references and nothing can be placed until they are known; the second streams every
/// record and keeps the entries whose parent fell inside the range the caller asked for.
/// </para>
/// <para>
/// Anything that would make the tree confidently wrong declines the backend rather than
/// producing a partial one: a record that will not validate, a volume whose geometry is not
/// understood, an entry whose split attributes cannot be followed across fragment records. Anything narrower
/// - a link that was not followed, a file whose size came from an ordinary query - is a
/// diagnostic on an otherwise good result.
/// </para>
/// </remarks>
internal sealed class NtfsMftScanBackend : IScanBackend
{
    private readonly Func<string, IMftRecordSource?> _openVolume;
    private readonly NtfsSizeQuery? _queryOverride;

    /// <summary>Creates the production backend.</summary>
    public NtfsMftScanBackend()
        : this(null, null)
    {
    }

    /// <summary>
    /// Test seam: substitutes the volume source and the metadata query. A
    /// <see langword="null"/> for either means the real one.
    /// </summary>
    internal NtfsMftScanBackend(Func<string, IMftRecordSource?>? openVolume, NtfsSizeQuery? queryMetadata)
    {
        _openVolume = openVolume ?? OpenVolume;
        _queryOverride = queryMetadata;
    }

    public ScanBackend Id => ScanBackend.NtfsMft;

    public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        if (!NtfsPath.TrySplit(request.RootPath, out string volumeRoot, out string[] segments))
        {
            failureReason = $"'{request.RootPath}' does not name a local drive volume, and the MFT backend reads one.";
            return false;
        }

        IMftRecordSource? source;

        try
        {
            source = _openVolume(volumeRoot);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            failureReason = ScanFailure.Describe(exception);
            return false;
        }

        if (source is null)
        {
            failureReason = $"The volume '{volumeRoot}' could not be read.";
            return false;
        }

        using (source)
        {
            NtfsDirectoryMap map = new(request.Options.MaxEntries, collector);
            DirectoryVisitor directories = new(map, source, cancellationToken);

            if (!MftRecordStream.TryWalk(source, cancellationToken, collector, request.RootPath, directories, out failureReason))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (directories.MapFull)
            {
                failureReason = map.MemoryLimitReached
                    ? "The NTFS directory map exceeded the scan memory budget."
                    : $"The volume holds more directories than the {request.Options.MaxEntries}-entry budget allows.";
                return false;
            }

            if (!map.TryResolveRoot(segments, out ulong rootOrdinal, out failureReason))
            {
                failureReason = $"The scan root could not be resolved from the volume's MFT: {failureReason}";
                return false;
            }

            NtfsDirectoryMap.Exclusions exclusions = map.MarkSubtree(rootOrdinal, request.Options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (map.MemoryLimitReached)
            {
                failureReason = "The NTFS directory map exceeded the scan memory budget.";
                return false;
            }

            ReportExclusions(request, exclusions, collector);

            cancellationToken.ThrowIfCancellationRequested();

            // The default query is built here rather than stored, so it can see this scan's options and
            // skip the allocated-size call entirely when the caller turned it off.
            NtfsSizeQuery query = _queryOverride
                ?? ((string path, out long logical, out long allocated, out DateTimeOffset? lastWrite)
                    => QueryMetadata(path, request.Options, out logical, out allocated, out lastWrite));

            EntryVisitor visitor = new(query, map, collector, request, source);

            if (!MftRecordStream.TryWalk(source, cancellationToken, collector, request.RootPath, visitor, out failureReason))
            {
                return false;
            }

            visitor.ReportCounters(request.RootPath, collector);
            failureReason = null;
            return true;
        }
    }

    private static void ReportExclusions(
        ScanRequest request,
        NtfsDirectoryMap.Exclusions exclusions,
        ScanCollector collector)
    {
        string root = request.RootPath;

        if (exclusions.LinksNotFollowed > 0)
        {
            collector.AddDiagnostic(root, $"{exclusions.LinksNotFollowed} link(s) were not followed, and neither were their contents.", ScanDiagnosticSeverity.Warning);
        }

        if (exclusions.StaleDirectories > 0)
        {
            collector.AddDiagnostic(root, $"{exclusions.StaleDirectories} director(ies) referred to a reused parent record and were left out with their contents.");
        }

        if (exclusions.BeyondDepth > 0)
        {
            // The boundary directories themselves are still recorded as empty placeholders,
            // exactly as the portable backend records them. What is missing is everything
            // behind them.
            collector.MarkIncomplete($"Stopped descending beyond depth {request.Options.MaxDepth}, the configured maximum.");
        }
    }

    /// <summary>Pass one: keep the directories, and nothing else.</summary>
    private sealed class DirectoryVisitor(NtfsDirectoryMap map, IMftRecordSource source, CancellationToken cancellationToken) : IMftRecordVisitor
    {
        /// <summary>True when the walk stopped because the directory budget ran out.</summary>
        public bool MapFull { get; private set; }

        public string? FailureReason { get; private set; }

        public bool Visit(MftRecordInfo record)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A fragment belongs to the record that names it. It often carries no name of its
            // own, and treating it as a directory would lose a whole folder to a guess.
            // Fragments are merged into their base below; they are never placed on their own.
            if (record.IsExtension)
            {
                return true;
            }

            // Directory pass keeps directories and nothing else. The directory bit lives
            // in the base header, never in a fragment, so a non-directory needs no
            // resolution here: the entry pass resolves full files and hard links (even
            // outside the root, because an unresolvable split anywhere declines).
            if (!record.IsDirectory)
            {
                return true;
            }

            MftRecordInfo logical = record;

            // A logical file whose attributes are split across fragment records is resolved
            // from its list before anything is placed. A link held only by a fragment can
            // sit anywhere, including inside the range the caller asked for, so an entry
            // that cannot be resolved is a decline wherever it lives, rather than a tree
            // that is quietly missing links.
            if (record.HasAttributeList)
            {
                MftRecordInfo merged = new();
                if (!MftLogicalFileResolver.TryResolve(record, source, cancellationToken, merged, out string? resolveFailure))
                {
                    FailureReason = resolveFailure;
                    return false;
                }

                logical = merged;
            }

            if (!logical.IsDirectory)
            {
                return true;
            }

            List<MftName> names = MftRecordParser.VisibleNames(logical.Names);
            if (names.Count != 1)
            {
                FailureReason = names.Count == 0
                    ? "A directory on this volume stated no name and cannot be placed."
                    : "A directory on this volume carries several names and cannot be placed.";
                return false;
            }

            if (map.TryAdd(logical, names[0]))
            {
                return true;
            }

            MapFull = true;
            return false;
        }
    }

    /// <summary>Pass two: keep the entries inside the range the caller asked for.</summary>
    private sealed class EntryVisitor : IMftRecordVisitor
    {
        private readonly NtfsSizeQuery _queryMetadata;
        private readonly NtfsDirectoryMap _map;
        private readonly ScanCollector _collector;
        private readonly ScanRequest _request;

        private long _linksNotFollowed;
        private long _metadataFailures;
        private long _unnamedRecords;
        private long _unresolvedParents;
        private long _sizesUnavailable;
        private long _skippedByAttributes;

        public EntryVisitor(
            NtfsSizeQuery queryMetadata,
            NtfsDirectoryMap map,
            ScanCollector collector,
            ScanRequest request,
            IMftRecordSource source)
        {
            _queryMetadata = queryMetadata;
            _map = map;
            _collector = collector;
            _request = request;
            _source = source;
        }

        private readonly IMftRecordSource _source;

        public string? FailureReason { get; private set; }

        public bool Visit(MftRecordInfo record)
        {
            CancellationToken cancellationToken = _request.CancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            // Record 5 is the volume root: the node the caller asked to scan, which the
            // tree builder creates. A fragment belongs to the record that names it, and is
            // merged into its base below; it is never emitted on its own.
            if (record.Ordinal == MftRecordParser.VolumeRootRecord || record.IsExtension)
            {
                return true;
            }

            MftRecordInfo logical = record;

            // A split entry is resolved before anything is reported, so a name or size
            // held only by a fragment is not silently dropped. One that cannot be
            // resolved declines the backend wherever it lives.
            if (record.HasAttributeList)
            {
                MftRecordInfo merged = new();
                if (!MftLogicalFileResolver.TryResolve(record, _source, cancellationToken, merged, out string? resolveFailure))
                {
                    FailureReason = resolveFailure;
                    return false;
                }

                logical = merged;
            }

            // An entry the caller asked to skip is skipped whether it is a file or a folder,
            // and a skipped folder takes its contents with it because the map never descended
            // into it. The scan root is not filtered here: the portable backend reports a
            // root whatever its attributes, and so does this.
            if ((logical.Attributes & _request.Options.SkipAttributes) != 0)
            {
                _skippedByAttributes++;
                return true;
            }

            List<MftName> names = MftRecordParser.VisibleNames(logical.Names);
            List<string> relativePaths = [];

            foreach (MftName name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MftParentStatus status = _map.ResolveParent(name, logical.Ordinal, out bool inSubtree, out int depth, out string prefix);

                if (status != MftParentStatus.Resolved)
                {
                    // An unresolvable parent means the entry cannot be placed, and a reused
                    // record number means the link is stale. Neither is repaired by guessing.
                    _unresolvedParents++;
                    continue;
                }

                // A directory at the depth limit is still recorded as an empty placeholder,
                // as it is by the portable backend; only what is behind it is lost.
                if (!inSubtree || depth > _request.Options.MaxDepth)
                {
                    continue;
                }

                relativePaths.Add(ScanPath.Join(prefix, name.Name));
            }

            if (relativePaths.Count == 0)
            {
                if (names.Count == 0)
                {
                    _unnamedRecords++;
                }

                return true;
            }

            for (int i = 0; i < relativePaths.Count; i++)
            {
                if (!TryEmit(logical, relativePaths[i], isPrimaryName: i == 0, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryEmit(
            MftRecordInfo record,
            string relativePath,
            bool isPrimaryName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isDirectory = record.IsDirectory;

            // A directory's own bytes are metadata the treemap sums from its children, and it
            // carries no time here - exactly what the portable backend reports, so falling
            // back does not change what the tree looks like.
            long logical = 0;
            long allocated = 0;
            DateTimeOffset? lastWrite = isDirectory ? null : record.LastWriteTime;

            if (!isDirectory && record.IsReparsePoint)
            {
                // Reading a link would read - and charge - for a file that is not part of
                // this tree. It keeps its name and reports nothing, which is what a link is.
                _linksNotFollowed++;
            }
            else if (!isDirectory && record.HasUnnamedData)
            {
                logical = record.LogicalSize;
                allocated = _request.Options.QueryAllocatedSize ? record.AllocatedSize : 0;
            }
            else if (!isDirectory && !TryReadSizes(relativePath, out logical, out allocated, out lastWrite))
            {
                // Nothing in the record states this file's size: a named-stream-only file,
                // or content in records this backend will not follow. The filesystem is asked
                // directly; if it cannot answer either, the entry is reported rather than
                // given a size of zero.
                _sizesUnavailable++;
            }

            if (_collector.TryAdd(
                    relativePath,
                    isDirectory ? NodeKind.Folder : NodeKind.File,
                    logical,
                    allocated,
                    lastWrite,
                    isExtraHardLinkEntry: !isPrimaryName))
            {
                return true;
            }

            // The shared budget is spent. Stop the walk rather than draining the rest of the
            // table to find out that nothing else fits either.
            _collector.ShouldStop();
            return false;
        }

        private bool TryReadSizes(
            string relativePath,
            out long logical,
            out long allocated,
            out DateTimeOffset? lastWrite)
        {
            try
            {
                return _queryMetadata(
                    Path.Combine(_request.RootPath, relativePath),
                    out logical,
                    out allocated,
                    out lastWrite);
            }
            catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
            {
                logical = 0;
                allocated = 0;
                lastWrite = null;
                _metadataFailures++;
                return false;
            }
        }

        /// <summary>
        /// One diagnostic per kind of thing left out, not one per entry: a volume with a
        /// hundred thousand links must not spend a hundred thousand units of the caller's
        /// budget describing them.
        /// </summary>
        public void ReportCounters(string rootPath, ScanCollector collector)
        {
            if (_linksNotFollowed > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_linksNotFollowed} link(s) were recorded but not followed; those entries contribute no bytes.", ScanDiagnosticSeverity.Warning);
            }

            if (_metadataFailures > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_metadataFailures} entr(ies) could not be read at all.");
            }

            if (_unnamedRecords > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_unnamedRecords} MFT record(s) named no entry and were skipped.");
            }

            if (_unresolvedParents > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_unresolvedParents} entr(ies) pointed at a reused or missing parent record and were skipped.");
            }

            if (_skippedByAttributes > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_skippedByAttributes} entr(ies) matched the skipped attributes and were left out with their contents.", ScanDiagnosticSeverity.Warning);
            }

            if (_sizesUnavailable > 0)
            {
                collector.AddDiagnostic(rootPath, $"{_sizesUnavailable} file(s) had no readable unnamed DATA attribute and no reachable size; they report zero.");
            }
        }
    }

    private static IMftRecordSource? OpenVolume(string volumeRoot)
    {
        if (!WindowsMftRecordSource.TryOpen(volumeRoot, out WindowsMftRecordSource? source, out string? failure))
        {
            // Carried as an exception only so the reason survives the seam's signature; it is
            // caught immediately above and turned back into a decline.
            throw new IOException(failure);
        }

        return source;
    }

    /// <summary>
    /// The ordinary answer to a file's sizes: stat the file. Used only for entries the MFT
    /// could not answer, so it costs one open per such entry and none for the rest.
    /// </summary>
    private static bool QueryMetadata(
        string path,
        ScanOptions options,
        out long logicalSize,
        out long allocatedSize,
        out DateTimeOffset? lastWriteTime)
    {
        try
        {
            FileInfo file = new(path);
            logicalSize = file.Length < 0 ? 0 : file.Length;

            // Asked for it, asked for it: a scan that turned the option off must not pay for
            // a size it will discard.
            allocatedSize = options.QueryAllocatedSize ? AllocatedSize.Read(path) : 0;
            lastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc));
            return true;
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            logicalSize = 0;
            allocatedSize = 0;
            lastWriteTime = null;
            return false;
        }
    }
}
