using System.Runtime.ExceptionServices;

namespace Treemap.Scanning;

/// <summary>
/// The universal scanner: streams <see cref="DirectoryInfo"/> enumerations across a fixed
/// pool of workers. The NTFS backend obeys the same rules, so a fallback does not change
/// what the user sees.
/// </summary>
/// <remarks>
/// Directories are streamed one at a time, links are recorded but never followed,
/// hidden and system entries are included unless a caller asks otherwise, and empty
/// directories and zero-byte files all survive into the tree. The scan root is the one
/// thing that must work: if it is missing, unreadable or a link, the scan throws,
/// because there is no such thing as a treemap of nothing.
/// </remarks>
internal sealed class PortableScanBackend : IScanBackend
{
    // The enumerator's own default skips hidden and system entries, which is exactly the
    // kind of silent omission this scanner refuses. There is no options-only overload of
    // EnumerateFileSystemInfos, so every call passes a "*" pattern beside it.
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    public ScanBackend Id => ScanBackend.Portable;

    public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
    {
        // Nothing can fall back from here, so this backend never declines.
        failureReason = null;

        VerifyRoot(request.RootPath);

        DirectoryWorkQueue queue = new();
        queue.Enqueue(new DirectoryInfo(ScanPath.Normalize(request.RootPath)), 0);

        Task[] workers = new Task[request.Options.MaxParallelism];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(
                () => RunWorker(queue, request, collector),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        // Every worker is joined before anything is rethrown, so a failure in one cannot
        // leave the others walking a tree nobody will read. The first failure is the one
        // the caller sees, and stopping the queue ends the rest of the walk.
        ExceptionDispatchInfo? failure = null;
        foreach (Task worker in workers)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                queue.Stop();
            }
        }

        failure?.Throw();
        return true;
    }

    private static void RunWorker(DirectoryWorkQueue queue, ScanRequest request, ScanCollector collector)
    {
        while (queue.TryTake(collector, request.CancellationToken, out DirectoryWorkQueue.Work work))
        {
            try
            {
                ScanDirectory(queue, work, request, collector);
            }
            catch
            {
                // End the walk here rather than letting it run on until the parent has
                // joined every worker: work whose result nobody will read is wasted, and
                // cancellation should reach the other workers immediately.
                queue.Stop();
                throw;
            }
            finally
            {
                queue.Complete();
            }
        }
    }

    private static void ScanDirectory(
        DirectoryWorkQueue queue,
        DirectoryWorkQueue.Work work,
        ScanRequest request,
        ScanCollector collector)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        ScanOptions options = request.Options;
        string prefix = RelativePrefix(work.Directory.FullName, request.RootPath);

        // Opening an enumeration is the expensive part, and the budget may have been spent
        // by another worker since this directory was queued.
        if (ShouldStop(queue, collector))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using IEnumerator<FileSystemInfo> entries = Enumerate(work.Directory, collector, out bool failed);
        if (failed)
        {
            return;
        }

        while (true)
        {
            // Checked per entry, not only per directory: entries that are skipped or fail
            // an attribute read cost no budget of their own, so a directory full of them
            // would otherwise keep walking long after the budget was gone.
            if (ShouldStop(queue, collector))
            {
                return;
            }

            bool moved;
            try
            {
                moved = entries.MoveNext();
            }
            catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
            {
                // The subtree is simply absent. One locked directory must not end a scan
                // of the whole tree, so this is a diagnostic and the result is partial.
                collector.AddDiagnostic(work.Directory.FullName, ScanFailure.Describe(exception));
                return;
            }

            if (!moved)
            {
                return;
            }

            FileSystemInfo entry = entries.Current;
            cancellationToken.ThrowIfCancellationRequested();
            collector.Report(ScanPhase.Enumerating, entry.FullName);

            // A progress callback is caller code: it may cancel, and an exception thrown
            // by one must not be mistaken for an unreadable directory.
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = entry.Attributes;
            }
            catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
            {
                collector.AddDiagnostic(entry.FullName, ScanFailure.Describe(exception));
                continue;
            }

            if ((attributes & options.SkipAttributes) != 0)
            {
                continue;
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isLink = (attributes & FileAttributes.ReparsePoint) != 0;

            long logical = 0;
            long physical = 0;
            DateTimeOffset? lastWrite = null;

            if (isLink)
            {
                // Following a link means reading - and allocating - for something that is
                // not part of this tree. The entry keeps its name and reports zero, which
                // is what a link is.
                collector.AddDiagnostic(
                    entry.FullName,
                    "Link recorded but not followed; the entry contributes no bytes.",
                    ScanDiagnosticSeverity.Warning);
            }
            else if (!isDirectory &&
                     !TryReadFile((FileInfo)entry, options, collector, out logical, out physical, out lastWrite))
            {
                continue;
            }

            if (!collector.TryAdd(ScanPath.Join(prefix, entry.Name),
                    isDirectory ? NodeKind.Folder : NodeKind.File,
                    logical,
                    physical,
                    lastWrite))
            {
                // The budget is spent. Stop the walk rather than draining the remaining
                // directories to find out nothing fits.
                queue.Stop();
                return;
            }

            if (!isDirectory || isLink)
            {
                continue;
            }

            if (work.Depth + 1 > options.MaxDepth)
            {
                collector.MarkIncomplete($"Stopped descending at depth {work.Depth}, the configured maximum.");
                continue;
            }

            queue.Enqueue((DirectoryInfo)entry, work.Depth + 1);
        }
    }

    /// <summary>
    /// Ends the walk when the shared budget is gone, so every worker stops for the same
    /// reason at the same places instead of only where an entry happens to cost something.
    /// </summary>
    private static bool ShouldStop(DirectoryWorkQueue queue, ScanCollector collector)
    {
        if (!collector.ShouldStop())
        {
            return false;
        }

        queue.Stop();
        return true;
    }

    /// <summary>
    /// Opens the enumeration, reporting an unreadable directory instead of throwing. The
    /// enumerator is returned rather than enumerated here, so a failure to open stays
    /// distinguishable from a failure part way through a working stream.
    /// </summary>
    private static IEnumerator<FileSystemInfo> Enumerate(
        DirectoryInfo directory,
        ScanCollector collector,
        out bool failed)
    {
        try
        {
            IEnumerator<FileSystemInfo> enumerator =
                directory.EnumerateFileSystemInfos("*", Enumeration).GetEnumerator();

            failed = false;
            return enumerator;
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            collector.AddDiagnostic(directory.FullName, ScanFailure.Describe(exception));
            failed = true;
            return EmptyEnumerator();
        }
    }

    private static IEnumerator<FileSystemInfo> EmptyEnumerator()
    {
        yield break;
    }

    private static bool TryReadFile(
        FileInfo file,
        ScanOptions options,
        ScanCollector collector,
        out long logical,
        out long physical,
        out DateTimeOffset? lastWrite)
    {
        logical = 0;
        physical = 0;
        lastWrite = null;

        try
        {
            logical = file.Length < 0 ? 0 : file.Length;
            lastWrite = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc));

            if (options.QueryAllocatedSize)
            {
                physical = AllocatedSize.Read(file.FullName, out bool available);
                if (!available)
                {
                    collector.NoteAllocationUnavailable(
                        "Allocated size is unavailable on this platform, so physical sizes report 0 and the layout falls back to the logical size.");
                }
            }

            return true;
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            collector.AddDiagnostic(file.FullName, ScanFailure.Describe(exception));
            return false;
        }
    }

    /// <summary>
    /// Fails loudly when the root itself is unusable. Everything below the root is a
    /// diagnostic instead, because a scan that cannot read one branch can still say a
    /// great deal about the rest.
    /// </summary>
    private static void VerifyRoot(string rootPath)
    {
        string full = ScanPath.Normalize(rootPath);
        FileAttributes attributes;

        try
        {
            // DirectoryInfo.Attributes returns -1 for a missing path instead of
            // throwing, which then misreads as a link. File.GetAttributes throws
            // the missing/access error instead.
            attributes = File.GetAttributes(full);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            throw new DirectoryNotFoundException($"The scan root '{rootPath}' does not exist.", exception);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"The scan root '{rootPath}' is not a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Descending through a link at the root is the one place the walk could leave
            // the tree the caller chose, or revisit itself. Refuse rather than guess.
            throw new IOException($"The scan root '{rootPath}' is a link. Scan the directory it points to instead.");
        }

        // Opening the enumeration is the real readability test; Attributes only stats.
        // Whatever this throws - UnauthorizedAccessException above all - is the caller's
        // problem, not something to hide in a diagnostics list.
        using IEnumerator<FileSystemInfo> probe = new DirectoryInfo(full).EnumerateFileSystemInfos("*", Enumeration).GetEnumerator();
        probe.MoveNext();
    }

    /// <summary>
    /// The path of a directory below the root, or an empty string for the root itself.
    /// <see cref="ScanPath.Join"/> owns the separator.
    /// </summary>
    private static string RelativePrefix(string directoryPath, string rootPath)
    {
        string relative = Path.GetRelativePath(ScanPath.Normalize(rootPath), ScanPath.Normalize(directoryPath));
        return relative.Length == 0 || relative == "." ? string.Empty : relative;
    }
}