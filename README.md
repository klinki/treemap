# Treemap

Disk-usage treemap with squarified layout, SkiaSharp rendering, hit-testing,
animation, and immutable snapshot publication. `Treemap.Scanner` scans real
directories through portable .NET enumeration or a read-only Windows NTFS MFT
backend. The desktop host uses WinForms; the scanner library targets portable .NET 10.

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
& ./tools/Invoke-Bounded.ps1 -Application dotnet -TimeoutSeconds 300 -Arguments 'src/Treemap.Host.WinForms/bin/Release/net10.0-windows/TreemapHost.dll --no-elevation'
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
The scanner publishes once after enumeration, including explicitly incomplete
results. Cancellation before publication throws and preserves the previous snapshot.

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

The default limits are two directory workers, 500,000 entries and diagnostics
combined, directory depth 256, and 200 retained diagnostics. Hitting a limit marks
the result incomplete. Entry limits bound counts, not heap bytes; run large scans
under the hard process limits above. Hidden and system entries are included unless
`SkipAttributes` requests otherwise. Links retain zero-byte placeholders and are
never followed; a link selected as the root is rejected.

Logical sizes count the unnamed file stream. Every visible hard-link name receives
its own node and full size, so totals can count shared clusters repeatedly. The
native backend reports extra hard-link entries; portable enumeration cannot identify
them. Windows supplies allocated sizes when available; other platforms report zero
with a warning. Treemap layout falls back to logical size when allocation is zero,
including resident or fully sparse data. Free space is added only for a complete
volume-root scan. Live scans are observations over time, not atomic filesystem snapshots.

See the [scanner design](docs/features/filesystem-scanner/implementation-plan.md)
and [review evidence](docs/features/filesystem-scanner/verification.md).

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

`artifacts/` is git-ignored. The wrapper also sets `DOTNET_GCHeapHardLimit` for the
launched process; that is a **managed heap** limit which supplements the kernel caps —
it is not a machine-wide cap on its own, and memory pressure that is not in managed
allocations is invisible to it.

## Verification (2026-10-11)

All 340 runnable tests passed in Release, including 19 hidden WinForms tests.
Five scanner tests were explicitly skipped because of platform or symbolic-link
permissions. All 11 projects built with zero warnings and errors. Every process run
used the bounded runner; the final test process tree peaked at 169.6 MiB committed
memory. A small real-directory scan verified publication, stable IDs, limits,
cancellation, and actual Windows access-denied fallback. See the
[scanner review evidence](docs/features/filesystem-scanner/verification.md) for details.

The native MFT path passed record fixtures; a live native scan remains unverified
because Windows denied the volume handle. Linux and macOS runtime checks remain
outstanding.

Before the scanner change, one BenchmarkDotNet 0.15.8 ShortRun measurement completed
for `Layout10K`: mean
**1.177 ms**, standard deviation **0.0277 ms**, **1.91 MB allocated per operation**,
three measured iterations. The 99.9% confidence interval is wide (0.671–1.682 ms);
this is an initial measurement under the stated caps. The complete benchmark process
tree peaked at 280.1 MiB. Other benchmark cases remain unmeasured.

Logs are in `artifacts/verification/`; workload results are in
`BenchmarkDotNet/results/` below that directory. Runner JSON verifies resource use;
BenchmarkDotNet workload samples establish that the benchmark actually ran.

Known limits: publishing again during an active tween restarts from its previous
target and can jump. When retained nodes reverse ancestor relationships, a single
prepared draw order follows the target hierarchy.
