# Treemap

Windows disk-usage treemap: squarified layout, SkiaSharp rendering, hit-testing, a
per-frame animation path, and snapshot publication for large trees. Benchmark and
perf-test data is generated synthetically; **the filesystem scanner that would walk a
real volume is a subsequent feature and is not implemented yet.**

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

All 147 tests passed in Release, including ten hidden WinForms lifecycle/painting tests.
All nine projects built with zero warnings and errors. Every process run used the
bounded runner; the final test process tree peaked at 157.8 MiB committed memory.

One BenchmarkDotNet 0.15.8 ShortRun measurement completed for `Layout10K`: mean
**1.177 ms**, standard deviation **0.0277 ms**, **1.91 MB allocated per operation**,
three measured iterations. The 99.9% confidence interval is wide (0.671–1.682 ms);
this is an initial measurement under the stated caps. The complete benchmark process
tree peaked at 280.1 MiB. Other benchmark cases remain unmeasured.

Logs are in `artifacts/verification/`; workload results are in
`BenchmarkDotNet/results/` below that directory. Runner JSON verifies resource use;
BenchmarkDotNet workload samples establish that the benchmark actually ran.

Known limits: publishing again during an active tween restarts from its previous
target and can jump. When retained nodes reverse ancestor relationships, a single
prepared draw order follows the target hierarchy. The filesystem scanner remains
subsequent feature work.
