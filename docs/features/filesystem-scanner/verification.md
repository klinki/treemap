# Filesystem scanner review and verification

Reviewed on 2026-10-11. Space-bunny implemented the initial scanner and host; Muse Spark handled the remaining corrections after the user changed the delegate. The supervisor reviewed the changes and ran verification.

## Final checks

All execution used `tools/Invoke-Bounded.ps1`: 512 MiB per process, 1 GiB per job, a 120-second deadline, and termination on memory limits. Build servers, shared compilation, and parallel MSBuild were disabled. These are kernel-enforced caps, supplemented by the managed heap limit.

| Check | Result | Log under `artifacts/verification/scanner/` |
| --- | --- | --- |
| Release build of all 11 solution projects | 0 warnings, 0 errors; 77.7 MiB peak job memory | `solution-build-final.log` |
| Full solution tests | 340 passed, 5 skipped, 0 failed; 169.6 MiB peak job memory | `solution-tests-final.log` |
| Independent small real-directory scan | Passed, including actual Windows access-denied fallback | `smoke-final.log` |
| Minimum-size hidden WinForms rendering | Reviewed toolbar, wrapped status, and treemap bounds | `scan-shell.png` |

Each execution log has a sibling `.json` file containing exit status, elapsed time, and peak memory. Test results are in `FinalTestResults/*.trx`. Artifacts are git-ignored.

| Test project | Passed | Skipped |
| --- | ---: | ---: |
| Treemap.Core.Tests | 101 | 0 |
| Treemap.Host.WinForms.Tests | 19 | 0 |
| Treemap.Perf.Tests | 19 | 0 |
| Treemap.Scanner.Tests | 184 | 5 |
| Treemap.Skia.Tests | 17 | 0 |

The scanner skips are explicit: one Unix backslash-filename case on Windows, and four cases requiring symbolic-link creation that this Windows account cannot perform. Native reparse-point behavior remains covered by record fixtures; the skipped real-filesystem cases have not been validated here.

## Independent checks

The supervisor's real-directory fixture contained five visible file names, including a Windows hard link, two directories including an empty directory, and 56 logical bytes. The portable scanner returned those counts and sizes. Snapshot publication ran on a worker thread. Adding a file and rescanning with a trailing separator preserved IDs for retained paths. A one-entry budget produced an explicitly incomplete result; cancellation from the Publishing progress callback preserved the previous snapshot.

The same small directory scanned in Auto mode tried a read-only C: volume handle, received Win32 error 5 (access denied), and returned the same tree through portable enumeration. A separate read-only volume-open probe also received access denied. Neither check scanned a whole live volume or requested elevation.

Nine supervisor-authored native format tests use literal byte offsets independent of the delegate's fixture builder. They cover extended geometry and 4 KiB sectors, exact resident data lengths, update sequence validation, named streams, hard-link names and DOS aliases, sparse allocation, malformed filename bounds, and missing attribute terminators. The broader native suite covers downward ordinal progression, vacant records, parent generations, subtree selection, fallback, cancellation, and limits.

## Corrections from review

- Corrected native record and geometry bounds, unnamed DATA size extraction, sparse/compressed allocation headers, parent sequence validation, and unsupported fragment handling.
- Kept scans bounded by workers, entries, diagnostics, and depth; checked cancellation before publication and fallback. Saturated size totals to avoid signed overflow.
- Corrected the Windows missing-root check: `DirectoryInfo.Attributes` could return `-1`, which was mistaken for a reparse point. Root validation now uses the throwing `File.GetAttributes` API.
- Corrected faulty test records and expectations rather than weakening valid parser checks. Windows native paths now split both separator spellings and reject drive-relative paths.
- Corrected hidden UI verification. It now creates descendant handles on the owning STA thread and retains a scoped WinForms synchronization context across temporary message pumps. Cross-thread control checks remain enabled. The earlier toolbar-layout diagnosis was disproved and its production change was removed.

The hidden-pump behavior was checked against the official [WinForms message-loop source](https://source.dot.net/System.Windows.Forms/System/Windows/Forms/Application.ThreadContext.cs.html) and [synchronization context source](https://source.dot.net/System.Windows.Forms/System/Windows/Forms/WindowsFormsSynchronizationContext.cs.html). The failure reproduced in isolation; it was not treated as a timing flake or resolved by increasing deadlines.

## Validation limits

- The live NTFS MFT scan path is **not verified against an accessible real volume**. Its parsing and traversal passed fixtures; this session verified actual access-denied fallback. No NTFS scanning performance claim is made.
- No Linux or macOS runtime was available. The scanner targets portable .NET 10 and guards Windows native calls, but cross-platform runtime verification remains outstanding.
- NTFS records whose attributes span extension records currently force portable fallback. Unsupported or untrusted native metadata also falls back; partial native results are discarded.
- File sizes count the unnamed stream. Visible hard links each contribute their full size, so totals can count shared storage repeatedly. Physical sizes are unavailable on other platforms, and zero allocation uses logical size for treemap layout, including resident and fully sparse files.
- Volume discovery can be slow for removable or network drives. It runs asynchronously in the host; the first verification run spent 66 seconds in a real ready-volume query. Filesystem cancellation is cooperative between operations and cannot interrupt a blocking operating-system call.
- Scans observe a changing filesystem over time. They are not atomic disk snapshots. Free space is added only for complete volume-root scans.

The earlier `Layout10K` benchmark remains historical renderer/layout evidence. No benchmark was rerun for this scanner change.
