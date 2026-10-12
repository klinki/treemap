# Fix attempt 006

## Attempt status

Confirmed by the user on 2026-10-12; implementation commit a2cd59d.
Attempt 005's native extension support is committed as 6784813 and has now been
confirmed by the user. Its successful native C evidence is preserved.

## User request and findings

The user asked whether the file size is known so parsing duration can be estimated
and a percentage shown. Windows returns MftValidDataLength and record size before
the walk. MftGeometry retains the resulting RecordCount. The descending walk has
a known table span and current ordinal; vacant slots skipped by the kernel still
advance its position.

The current percentage uses discovered file allocation divided by used disk
space. The directory-index pass discovers no file bytes, so it reports zero for
most of the native scan. Native C attempt 005 confirmed that visible behavior.
The backend walks the table twice: first for directories, then to emit entries.

## Proposed change

Use the known MFT span for native progress, with distinct directory-index and
entry-reading phases. Progress advances across both passes, holds below 100 until
the accepted result, and preserves the existing portable discovery estimate.
Show current/total MFT size and an approximate remaining time for the current
pass after observing its speed. Label the estimate as such: table position is a
known denominator, but caching, gaps, extension reads, and tree work change rate.
Finalization and fallback must clear stale per-pass time estimates.

## Implementation and review

Muse Spark Contributor implements scanner/host progress contracts and small
regressions. Root reviews, runs serial checks without memory limits, and commits
locally after verification. Keep native extension resolution, memory behavior,
publication/identity lifecycle, and the shelved disk experiment unchanged.

## Verification plan

Small deterministic tests cover advancement before any file is emitted, gaps in
ordinals, both pass boundaries, partial/failing/cancelled walks, progress throttling,
phase/estimate clearing, portable fallback, final 100%, and UI percent/phase text.
Avoid timing-dependent sleeps for rate math. Run affected Scanner and Host suites;
then check the elevated visible native C scan for advancing indexing percentage,
MFT size, labelled current-pass estimate, final retained map, and no fallback.

## Primary reference

[NTFS_VOLUME_DATA_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer)
documents the table byte length and record size.

## Test results

Scanner 405 checks and host 91 checks passed; six scanner checks were skipped.
The elevated visible native C verification passed. Detailed evidence follows.

## Contributor round 1

The provider ended the first task with "Output token rate limit exceeded. Please
retry after a brief wait." No source or test edits were made; only root's planned
bug documentation is dirty. Implementation and verification remain pending.
Retry the same paid Muse Spark Contributor model with the original task brief.

## Contributor round 2 review

The implementation adds indexing progress, two-pass percentages, current/total
MFT size, and approximate time remaining in the current pass. Root corrected a
duplicate fixture constructor before compilation. The focused scanner run then
passed 16 and failed two checks: a cursor expectation was one record too large,
and a throwing callback fired at BeginMftPass outside its assertion. UI checks
passed 5/5.

Root added regressions for cancellation from the terminal pass callback and ETA
at TimeSpan's representable boundary. The latter passed; terminal cancellation
failed because TryWalk returned without checking the token after its forced
completion tick. The expanded focused suite passed 17 and failed three of 20.
Another Contributor round will fix callback cancellation, fixture assertions,
redundant test wrappers, and stale progress-contract documentation. No live scan
or commit has been performed for attempt 006.

Logs: artifacts/verification/ntfs-map-reset/attempt006-mft-progress-r2-compiled.log,
attempt006-mft-supervisor-review.log, and attempt006-mft-display-r2.log.
All process verification explicitly disabled memory limits.

## Contributor round 3 review

Terminal progress callbacks now check cancellation before returning success.
Native phase startup also checks cancellation. Fixture expectations and throw
arrangement were corrected; redundant UI test wrappers and a second progress
state lock were removed. Percentage documentation includes native folder scans.
The first-pass test now requires advancement before the final boundary tick.

The complete scanner suite passed 405 checks with six platform skips, including
22 native progress regressions. Run time was 66 seconds for tests, 73.46 seconds
for the whole process. Peak process/job memory was 152.97/235.16 MiB. No memory
limits were imposed. Log: artifacts/verification/ntfs-map-reset/attempt006-scanner-r3.log.
Host checks and live C verification remain pending.

## Final verification

The complete host suite passed 91/91 in 18 seconds. Diagnostic build succeeded.
Logs: attempt006-host-r3.log and attempt006-diagnostic-build-r3.log under
artifacts/verification/ntfs-map-reset. Together with Scanner 405 passed/six skipped,
all 496 affected checks passed. No Core/Skia code changed and no benchmark was run.

The elevated maximized C: window used production scan defaults, no heap/kernel
memory cap, and a 600-second deadline. Windows reported an MFT span of
4,147,904,512 bytes (about 4.15 GB). Indexing displayed 1% at 1.6 seconds, 3% at
2.6 seconds with "about 26s left in this pass", then 6% at 3.6 seconds. No files
had been emitted yet. The second pass began at 50% about 23.4 seconds after launch;
its own size counter and time estimate restarted. The entire percentage series
was monotonic and no active tick showed 100%. Both passes displayed estimates.

Native MFT finished without portable fallback and retained 3,544,924 discovered
entries (2,928,657 files, 616,267 directories). Scan time was 54.0 seconds on this
rerun. The final percentage was 100%; final progress details were cleared; every
recorded frame stayed at detail 1. No exception, reset, crash, or memory stop
occurred. One nameless MFT record still caused an explicit incomplete result,
with two unreadable-entry diagnostics, as documented for attempt 005.

The process exited normally after 59.24 seconds, peaking at 2,053.05078125 MiB
process/job memory (about 2.15 GB). Evidence: attempt006-visible-full-c-r3.log and
its .log.json result. Scan durations vary between runs; ETA reports observed rate
for the current pass, not a guaranteed finish time or a performance comparison.

The user separately confirmed attempt 005 during this verification with "Yes,
native scanning worked". Attempt 006 remains awaiting confirmation of the new
indexing percentage, size, and time estimate after the local commit.

## User confirmation

On 2026-10-12, the user answered the progress confirmation question with
"Yes, progress works". This confirms advancing indexing percentages, MFT size,
and the approximate current-pass time estimate. Attempt 006 is fixed at
implementation commit a2cd59d. The earlier pending state above is historical.
The existing nameless-record warning remains outside this progress repair.
