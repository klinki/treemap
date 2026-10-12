# Treemap

Disk-usage treemap with squarified layout, SkiaSharp rendering, hit-testing,
animation, and immutable snapshot publication. `Treemap.Scanner` scans real
directories through portable .NET enumeration or a read-only Windows NTFS MFT
backend. The desktop host uses WinForms; the scanner library targets portable .NET 10.

This project is heavily inspired by [SpaceSniffer](https://www.uderzo.it/main_products/space_sniffer/).

## Prerequisites

- Windows — the bounded runner enforces caps through kernel32 job objects.
- PowerShell 5.1 or PowerShell 7+.
- .NET 10 SDK.

## Bounded execution

`tools/Invoke-Bounded.ps1` runs one process under hard caps: **512 MiB per process,
1 GiB per job, 120 s wall clock**. The process is assigned to the job while still
suspended, so its earliest descendants inherit the caps — for a benchmark run that
includes the build and every BenchmarkDotNet child process.

The whole job is killed on a job memory event, at 90% of either cap, or at the
deadline. It fails closed: an unresolvable program or a failed job, completion port, or
log file throws rather than falling back to an unbounded run.

For the current desktop scan investigation, memory limits are disabled explicitly
with `-DisableMemoryLimits`. This removes kernel memory caps and inherited .NET
heap caps while keeping the deadline, process cleanup, and peak measurements.
`-Visible` shows the app window. The wrapper's default capped mode is unchanged.

## Restore

Run once, and again after any package change. Bounded like everything else:

```powershell
& ./tools/Invoke-Bounded.ps1 -Application dotnet -Arguments 'restore Treemap.slnx --disable-parallel -m:1'
```

## Build and test the solution

`--no-restore` is safe because the restore above went through the same wrapper.
`-m:1` serializes the MSBuild node graph; `--disable-build-servers`,
`-p:UseSharedCompilation=false`, and `-p:BuildInParallel=false` stop the compiler
server and parallel project builds from multiplying process count and memory.

```powershell
& ./tools/Invoke-Bounded.ps1 -Application dotnet -LogPath artifacts/verification/build.log -Arguments 'build Treemap.slnx -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false'

& ./tools/Invoke-Bounded.ps1 -Application dotnet -LogPath artifacts/verification/tests.log -Arguments 'test Treemap.slnx -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false'
```

## Scan a directory

Start the desktop host, then enter a path, browse for a folder, or select a ready
volume and click Scan. Cancel stops the current scan and keeps the previous tree.
The status reports the backend, fallback reason, progress, and incomplete results.
Launching without a path does not scan automatically.

```powershell
& ./tools/Invoke-Bounded.ps1 -Application 'src/Treemap.Host.WinForms/bin/Release/net10.0-windows/TreemapHost.exe' -DisableMemoryLimits -Visible -TimeoutSeconds 600 -Arguments '--no-elevation'
```

Append a quoted directory path to that argument string to scan it after the window
opens. The wrapper closes the entire application at its deadline or memory limit;
choose a suitable deadline for an interactive session. `--no-elevation` disables the
automatic UAC restart below, which every bounded session should pass: a
broker-created elevated process does not inherit the wrapper's job limits.

The scanner is also usable without the desktop host:

```csharp
using Treemap.Scanning;

var scanner = new FileSystemScanner();
var result = await scanner.ScanAsync(
    rootPath,
    publisher: snapshotPublisher,
    progress: progress,
    cancellationToken: cancellationToken);
```

Reuse the scanner to preserve node IDs for paths present in consecutive scans.
Scans on one instance run serially. Enumeration, tree assembly, and the optional
`SnapshotPublisher.Publish` run on background threads. Progress uses the supplied
`IProgress<ScanProgress>`; construct `Progress<T>` on the UI thread to marshal it.
Library calls publish once after enumeration by default, including explicitly
incomplete results. Set `ScanOptions.PublishIntermediateSnapshots = true` to receive
immutable previews while scanning. `SnapshotInterval` defaults to 500 ms, with a
10 ms scheduling floor. Preview construction is serialized and skips unchanged
entry sets. The desktop enables previews by default.

For whole-volume scans with capacity available, the first preview contains free
space and the full unaccounted used space before enumeration begins. Discovered
files replace that residual in later previews. The desktop shows a whole-number
percentage beside the progress bar. Native scans use known MFT traversal across
two passes: folder indexing contributes 0–50%, and entry reading contributes
50–99%. During each pass, the status shows the current and total MFT table size
and an approximate time remaining for that pass. Vacant slots count toward table
position; extra fragment reads and changing throughput affect the time estimate.
The scan reaches 100% when its result is accepted.

Portable whole-volume scans estimate discovered storage as a fraction of used
volume bytes, capped at 99% until completion. Portable folder scans and unavailable
capacity use indeterminate progress with an ellipsis instead of a percentage.

With previews enabled, cancellation or failure restores the previous tree and its
source path after draining any preview in progress. Abandoning an NTFS attempt
discards its previews before portable enumeration starts. Without previews,
cancellation before publication preserves the previous snapshot. A throwing final
publication subscriber still leaves the published tree visible in this legacy mode.

`FileSystemScanner.GetReadyVolumesAsync` lists ready volumes. `ScanOptions.Backend`
can force portable enumeration. Auto tries NTFS on Windows and falls back to
directory enumeration when a volume cannot be opened, its metadata is unsupported,
or a record cannot be trusted. The scanner library reports access denied without
requesting elevation. UNC paths and other filesystems use the portable backend.

The desktop host restarts itself automatically when a selected NTFS scan needs raw
volume access the current process lacks: it probes read-only access first, then
relaunches its own executable with `runas` and resumes the absolute chosen root in
the elevated window. Declining UAC or a failed launch keeps the original window and
runs the regular scan with an explanation. Pass `--no-elevation` to disable the
automatic restart; an elevated child started this way does not inherit the bounded
runner's limits.

The default limits are two directory workers, directory depth 256, and 200 retained
diagnostics. There is no default entry-count or memory limit. Callers can set
`MaxEntries` or `MemoryBudgetBytes` explicitly. A requested memory budget estimates
file nodes, basenames, folders, diagnostics, backend workspace, and snapshot
construction. Discovery stops before accepting an entry that exceeds it and
publishes an explicitly incomplete map, including volume-space blocks. Hidden
and system entries are included unless
`SkipAttributes` requests otherwise. Links retain zero-byte placeholders and are
never followed; a link selected as the root is rejected.

The in-memory tree stores basenames and shares immutable file nodes across previews.
Finalization releases working dictionaries as it freezes folders. Snapshot lookups
use compact pages for dense IDs and a dictionary for sparse IDs. Large scans slow
previews to reduce allocation pressure. If an intermediate preview runs out of
memory before publication, previews pause and enumeration continues toward the
final map; final allocation failures still fail the scan.

The MFT reader follows resident and nonresident `$ATTRIBUTE_LIST` values to
resolve metadata in extension records. It validates references and merges only
the named attributes; continuation data never adds to the file's size. Unsupported
or inconsistent metadata falls back to portable scanning with a reason. See
[Microsoft's MFT description](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table).

Logical sizes count the unnamed file stream. Every visible hard-link name receives
its own node and full size, so totals can count shared clusters repeatedly. The
native backend reports extra hard-link entries; portable enumeration cannot identify
them. Windows supplies allocated sizes when available; other platforms report zero
with a warning. Treemap layout falls back to logical size when allocation is zero,
including resident or fully sparse data. Whole-volume scans can include free and
unaccounted space even when the result is incomplete. Folder scans include neither.
The scanner reads total volume capacity and total free space once per scan. The
unaccounted block is the nonnegative difference between used volume bytes and the
sum of each file's layout weight. It can include unscanned files, inaccessible
entries, filesystem metadata, and allocation differences. It does not measure
access-denied files alone. Duplicate hard links can reduce this residual to zero.
`TotalLogicalSize` and `TotalPhysicalSize` remain file totals; `FreeSpaceBytes` and
`UnknownSpaceBytes` report the separate synthetic blocks. Live scans are observations
over time, not atomic filesystem snapshots.

See the [scanner design](docs/features/filesystem-scanner/implementation-plan.md)
and [review evidence](docs/features/filesystem-scanner/verification.md).

## Explore the map

The desktop reserves folder headers above their children and shows names and sizes
inside visible cells. Hover highlights the node and its ancestor headers. The
tooltip shows the absolute path and logical or allocated size from the snapshot.

- Single-click a folder to show two extra levels inside its current rectangle.
  The displayed root stays in place. Selecting another node returns the previous
  subtree to the global detail level.
- Double-click a folder to focus its subtree. Back and Forward restore previous
  focus and camera positions. Up moves to the actual parent; Home returns to the
  scan root and fits the camera.
- Backspace or Left goes back, Right goes forward, Up or right-click goes to the
  parent, and Home or middle-click returns home. The wheel zooms around the cursor;
  dragging pans.
- More detail and Less detail change visible descendant depth from 1 to 64 without
  rescanning or changing zoom. The desktop starts at level 1, showing only the
  immediate folders and files; level 2 shows their children, and so on. Each More
  step reveals exactly one further generation, and Less reverses it, bounded at
  level 1.
- Free space and Unaccounted space independently show green and gray blocks.
  Both start visible. Toggling them reflows the map while preserving scan data and
  totals.
- Apply a filename filter such as `*.mp4` with Apply or Enter. `*` and `?` match
  filenames without regard to case. Matching files retain their ancestor folders,
  whose displayed logical and allocated totals are recalculated. Clear or a blank
  filter restores the original tree. Active filters omit volume-space blocks;
  clearing restores the user's space visibility choices. Filtering never rescans.

Navigation history resets when a new source is scanned. Same-source previews keep
the current focus where it still exists, and active filters apply to each preview.
The map follows the viewport's proportions to fill wide and portrait windows.
Resizing refits the camera immediately and debounces the geometry rebuild until
the size settles. The frame timer stops when animation settles.

See the [reference interaction plan](docs/features/reference-interactions/implementation-plan.md)
and [verification evidence](docs/features/reference-interactions/verification.md).
The [scan exploration plan](docs/features/scan-exploration/implementation-plan.md)
and [review evidence](docs/features/scan-exploration/verification.md) cover the
progress, filtering, and click behavior updates.

## One real benchmark

`Layout10K` lays out 10,000 generated files plus their folders and free space. `ShortRun` is the default job.
The build and the benchmark's own child processes stay inside the same kernel caps.

```powershell
& ./tools/Invoke-Bounded.ps1 -Application dotnet -TimeoutSeconds 180 -LogPath artifacts/verification/benchmark.log -Arguments 'tests/Treemap.Performance/bin/Release/net10.0/Treemap.Performance.dll --filter *LayoutBenchmarks.Layout10K'
```

Other entry points: `--list`, `--job dry` for a one-iteration smoke test, and `--full`
for BenchmarkDotNet's default thorough job. Tree size is capped at 100,000 files
(`PerfConfig.MaxFileCount`).

## Evidence

Every bounded run writes a console log and a sibling `.json` file recording exit code,
stop reason, elapsed seconds, and peak process and job memory:

```
artifacts/verification/bounded-run.log
artifacts/verification/bounded-run.log.json
```

`artifacts/` is git-ignored. In capped mode the wrapper sets `DOTNET_GCHeapHardLimit`
for the launched process. That managed heap limit supplements the kernel caps.
`-DisableMemoryLimits` clears .NET heap-limit variables and omits kernel memory
caps; the result JSON records `MemoryLimitsEnabled: false`.

## Verification (2026-10-11)

All 548 runnable tests passed in Release, including 78 hidden WinForms tests.
Five scanner tests were explicitly skipped because of platform or symbolic-link
permissions. All 11 projects built with zero warnings and errors. The final full
test run completed in 29.0 seconds and its process tree peaked at 149.5 MiB committed
memory. Builds, tests, and rendering fixtures used the bounded runner. Eleven hidden
window fixtures covered initial and discovered space, full-width and portrait
layouts, temporary detail, folder focus, filename filtering, no matches, and narrow
toolbar and filter wrapping. See the
[scan exploration evidence](docs/features/scan-exploration/verification.md).

Earlier real-directory checks verified publication, stable IDs, limits, cancellation,
and Windows access-denied fallback. See the
[scanner review evidence](docs/features/filesystem-scanner/verification.md) for details.

The native MFT path passed record fixtures and an elevated visible C: scan; see
[attempt 005](docs/bugs/002-ntfs-scan-map-reset/fix-attempt-005.md).
Linux/macOS runtime checks remain unverified.

Before the scanner change, one BenchmarkDotNet 0.15.8 ShortRun measurement completed
for `Layout10K`: mean
**1.177 ms**, standard deviation **0.0277 ms**, **1.91 MB allocated per operation**,
three measured iterations. The 99.9% confidence interval is wide (0.671–1.682 ms);
this is an initial measurement under the stated caps. The complete benchmark process
tree peaked at 280.1 MiB. Other benchmark cases remain unmeasured. No benchmark was
rerun for the scanner, reference interaction, or scan exploration changes.

Logs are in `artifacts/verification/`; workload results are in
`BenchmarkDotNet/results/` below that directory. Runner JSON verifies resource use;
BenchmarkDotNet workload samples establish that the benchmark actually ran.

Live previews received during an active tween snap to the newest layout. Other
interrupted transitions can still jump from the previous target. When retained nodes
reverse ancestor relationships, the prepared draw order follows the target hierarchy.

## Large-scan verification (2026-10-12)

All 610 affected Core, scanner, desktop, and Skia tests passed; six scanner tests
were skipped. An elevated minimized diagnostic enumerated 3,542,813 C: entries
and published the final map in 179.1 seconds. Peak job memory was 1.52 GB under
the 2 GB app budget.
Every recorded frame remained at default detail 1. NTFS declined fragmented
attributes, so the portable fallback completed the accessible inventory; protected
paths remain reported as unreadable. See
[attempt 003](docs/bugs/002-ntfs-scan-map-reset/fix-attempt-003.md) for limits and evidence.

The subsequent visible app retest failed with an out-of-memory error. The user
then requested no memory limits. After that change, a visible, maximized C: scan
published all 3,543,334 accessible entries in 156.6 seconds, reached 100%, and
retained detail 1 without a reset. Peak process/job memory was 2.35 GB. Scanner
321 and desktop 86 tests passed, with six skips. See
[attempt 004](docs/bugs/002-ntfs-scan-map-reset/fix-attempt-004.md) for the updated
constraints and evidence. The user accepted the repair on 2026-10-12.

With extension-record support, an elevated visible C: scan stayed on NTFS MFT
without fallback, retained 3,544,475 discovered entries, reached 100%, and kept
detail 1. Scan time was 180.5 seconds; peak process/job memory was about 2.17 GB,
with memory limits disabled. One MFT record without a visible name still produced
an explicit incomplete result; no memory stop, reset, or crash occurred. Scanner
383 tests and host 86 tests passed, with six scanner platform skips. See
[attempt 005](docs/bugs/002-ntfs-scan-map-reset/fix-attempt-005.md) for verification
and remaining warnings. The user confirmed native scanning on 2026-10-12.

Native traversal progress passed 496 scanner/host checks (six scanner skips).
A visible C: run showed advancing indexing percentages before any files were
emitted, a known 4.15 GB MFT table, and approximate remaining time in both passes.
It retained the native final map at 100% and detail 1. The existing nameless-record
warning remained. See [attempt 006](docs/bugs/002-ntfs-scan-map-reset/fix-attempt-006.md)
for live evidence. The user confirmed this progress change on 2026-10-12.
