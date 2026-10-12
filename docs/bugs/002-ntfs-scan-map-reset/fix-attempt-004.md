# Fix attempt 004

## Attempt status

Awaiting user confirmation after visible verification on 2026-10-12.

## Goal

Publish and retain the full accessible C: map at default detail 1 with visible
rendering and no imposed memory limit.

Updated on 2026-10-12: the user explicitly requested no memory limits for now.
That supersedes the 2 GB constraint for this attempt. The goal is a retained full
accessible C: map with visible rendering and no imposed memory cap.

## Relation to previous attempts

Attempt 003 reduced storage and lookup allocation, passed 610 affected tests,
and completed a minimized diagnostic run. Commit 77ef2db is on main. The actual
visible app still reset to unscanned space according to the user. That retest
failed; no fix is confirmed. Preserve the previous evidence and disk experiment.

## Proposed change

First compare the production host and diagnostic setup, then instrument a visible
reproduction with production scan defaults and exception stacks. Delegate this
investigation to Muse Spark Contributor. Choose a production fix only after the
failure phase and allocation path are known.

The user supplied screenshots and confirmed the exact failure status: "Scan
failed: out of memory. Try a smaller folder." The reset followed an allocation
failure. The next change disables launch-time kernel/GC caps explicitly and
removes the scanner's automatic estimated limit by default. Explicit library
memory budgets remain available. The user also requested a whole-number
percentage beside the progress bar; a second delegated task implements that.

## Risks

Changing logging, timing, or rendering can alter allocation pressure. Avoid
retaining snapshots for evidence. Keep the 1,907 MiB process/job caps, managed
heap limit, and deadline unchanged. Do not mask final allocation failures or
accept a resource-limited partial scan as success.

The earlier unchanged-cap plan above is superseded by the user's no-memory-limit
instruction. Keep process ownership and deadlines, but disable memory flags,
memory kill thresholds, and inherited .NET heap-limit variables for new runs.

## Files and components

The ignored diagnostic harness, WinForms scan and rendering lifecycle, scanner
finalization, immutable snapshots, and the existing bug workspace.

Additional changes cover ScanOptions, ScanMemoryBudget, ScanShell percentage
layout/lifecycle, and the runner's explicit DisableMemoryLimits and Visible switches.

## Verification plan

Root alone runs serial builds, tests, and elevated visible C: scans with deadlines
and no memory caps, as requested by the user.
Verify final publication and displayed counts, depth 1, resource JSON, and
exception absence. Add focused regressions for the evidenced production repair,
then run affected suites. Request a fresh user confirmation after local success.

## Implementation summary

The scanner uses no estimated memory limit by default. An explicit
MemoryBudgetBytes is still honored exactly. The runner's DisableMemoryLimits
switch omits kernel memory flags and kill thresholds, clears four inherited .NET
heap-limit variables, and restores the parent environment afterward. Visible
shows the child window; deadlines and process cleanup remain active.

The desktop displays an integer percentage beside the progress bar, an ellipsis
when no estimate exists, 100% after an accepted finish, and 0% after failure or
cancellation. A table row sizes to the bar and label, including large fonts and
narrow windows. Existing generation checks prevent stale progress from changing
a completed scan's percentage.

The diagnostic now accepts visible/maximized flags and uses the production
ScanShell defaults. Its one-second timer no longer performs the harness-only
whole-tree file-count walk. It logs exception stacks, final publication, visible
depth, percentage, managed memory, private memory, and working set without
retaining snapshot history.

## Test results

Verification milestones follow.

The scanner suite passed 321 tests with six skips after removing the default
estimate limit. Explicit memory-budget tests still pass. A small launcher probe
confirmed all four inherited DOTNET/COMPlus heap-limit variables were absent in
the child and restored in the parent. Its result reports MemoryLimitsEnabled
false. A separate deadline probe terminated after 2.09 seconds with timeout
exit 124, confirming that removing memory limits retains process cleanup.

The first desktop run passed 82 tests and failed four. Two were percentage-label
clipping, one was an ambiguous initial-versus-unknown progress assertion, and one
was an existing animation-state-dependent coalescing check. Round 2 fixed the
test gate and removed a single-use helper, but autosizing a docked Panel still
collapsed its row to four pixels. Desktop then passed 84 and failed only the
two clipping tests; the unrelated coalescing check passed unchanged. Round 3
uses content-based table layout for the progress row. A visible C: run remains
pending until the corrected UI is verified.

Round 3's table layout passed all 86 desktop tests, including percentage state,
default/narrow/large-font bounds, and stale-tick handling. Together with the
scanner suite this is 407 passes and six skips. The unchanged burst coalescing
test also passed. The supervisor removed duplicate implementation comments.
The diagnostic built successfully, then began an elevated, visible, maximized
C: run with production defaults and no GC heap cap. Early frames report integer
percentages and depth 1. Final publication is still being checked.

## Outcome and remaining gaps

The user confirmed that the previous visible reset followed an out-of-memory
failure. Its precise allocation stack remains unavailable. The new visible scan
without memory caps published its final map successfully; user confirmation in
the regular app is pending. MFT extension metadata and protected paths still
cause honest fallback/access warnings.

## Next step

Open the regular app without memory caps and request user confirmation.

## Visible full C: verification — 2026-10-12

An elevated, visible, maximized diagnostic using production ScanShell defaults
completed the portable walk and published 3,543,334 entries: 2,927,637 files and
615,697 folders. Its final snapshot and displayed map both contained 3,543,337
nodes including root/free/unaccounted blocks. Every recorded frame had detail 1,
no temporary expansion, and maximum depth 1. The percentage label reached 100%.
No scan fault, allocation exception, preview fault, resource stop, or reset was
recorded.

Scan duration was 156.6 seconds. The process completed in 160.13 seconds with
exit 0. Peak process/job committed memory was 2,245.18 MiB, or 2,354,245,632 bytes
(2.35 GB). No kernel memory flags or GC heap cap were applied, as requested by
the user. The result JSON explicitly reports MemoryLimitsEnabled false. This
does not satisfy the old 2 GB constraint; that constraint was superseded before
this run.

NTFS declined split attribute metadata before discovery and used portable
fallback. IsComplete remained false solely because of protected-path errors,
including System Volume Information, rather than resource truncation. The final
unaccounted residual was 26,751,734,012 bytes. Evidence is in ignored
attempt004-visible-full-c.log and its process JSON. Scanner 321 and desktop 86
passed, with six scanner skips. The regular app retest remains pending.
