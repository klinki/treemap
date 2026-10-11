# Fix attempt 002

## Attempt status

User confirmed the reset and crash are gone on 2026-10-11. Local verification
passed after attempt 001 failed the visible C: reproduction.

## Goal

Finish a memory-constrained scan with useful discovered data and an explicit
incomplete reason, rather than reaching an allocation failure and restoring an
empty map. Keep the existing external process and job caps.

## Relation to previous attempts

Attempt 001 removed per-file child collections, shared relative paths, pre-sized
snapshot indexes, and added truthful live status. Its affected suites passed
522 tests with six skips. The visible C: run still failed during preview tree
construction, with the latest displayed snapshot containing 299,438 nodes.
The desktop now reported the allocation failure, but that did not repair the reset.

## Proposed change

- Apply a conservative memory budget derived from the available managed heap,
  with an optional caller override. Account for path storage and tree construction
  before accepting more entries, leaving room for the final snapshot and renderer.
- Stop enumeration at the budget, report an incomplete reason, and publish the
  collected entries through the normal final-result path. Preserve free and
  unaccounted space and stable IDs. Do not treat an unexpected allocation failure
  as a successful partial result.
- Add deterministic budget tests using small synthetic inputs, including previews,
  long paths, fallback reset, and cancellation. Keep tests independent of the
  machine's current memory pressure.

## Risks

An entry estimate must include inferred folders, path lengths, retained snapshots,
and temporary construction allocations. An overly loose estimate repeats the bug;
an overly strict one stops discovery earlier than necessary. A rescan can also
retain a previous map while constructing its replacement.

## Verification plan

Review the accounting and incomplete-result semantics. Run serial bounded checks
for affected projects, then repeat the visible C: scan under the same 512 MiB
process, 1 GiB job, 384 MiB managed heap, and 120-second deadline. Require a useful
final map, an explicit incomplete reason, and no scan fault or guard termination.

## Implementation summary

The collector resolves its allowance once from half of the remaining managed heap.
An optional positive `MemoryBudgetBytes` override can reduce it. Each entry costs
an estimated 1,024 bytes plus path storage, with additional node and prefix-string
costs for possible inferred parents. Retained diagnostics and NTFS directory-map
nodes and paths draw from the same allowance. Checks happen under the collector
gate before retaining more data.

The memory limit stops discovery through the existing backend stop contract and
sets an explicit incomplete reason. Final publication retains accepted entries,
free space, and unaccounted space. Failed NTFS attempts reset accounting and release
retained list capacity before retry. NTFS map exhaustion declines safely; exhaustion
during its entry pass returns the accepted partial data. Unexpected allocation
exceptions still fail and restore the prior map.

Muse's subsequent implementation calls could not run because of a rate limit and
an insufficient-funds response from the alternate endpoint. The supervisor made
the attempt 002 changes after recording the plan and those provider failures.

## Test results

Eight focused memory-budget checks passed, including partial results, stable IDs on
rescan, long paths, diagnostics, fallback reset, cancellation, and preview/final
volume-space consistency. Full scanner verification and the visible C: retest are
pending. Added NTFS first-pass and entry-pass budget checks to the full suite.

Final checks passed: Core 170, scanner 283, desktop 81. Six scanner checks were
skipped by platform/environment. All jobs used the existing hard caps and completed
normally. The final scanner job peaked at 176.33 MiB and the desktop job at
192.72 MiB. A focused run passed twelve memory checks, including long absolute scan
roots and both NTFS directory-map and entry-pass exhaustion.

### Visible C: result

The elevated visible retest returned a normal incomplete result with 76,715 retained
entries, 36,973 files, and 39,742 folders. The final snapshot held 76,718 nodes,
including the root, free space, and unaccounted space. After the first discovered
preview, subsequent publications grew from 20,017 to 69,344 to 76,718 nodes; there
was no reset to an empty map and no scan exception.

The bounded run completed in 2.31 seconds with peak process memory of 151.90 MiB and
peak job memory of 153.09 MiB. The result and status explicitly named the memory
budget as the stopping reason. This is a partial scan, not a complete C: inventory.
The same NTFS record rejection occurred before discovery; portable enumeration
produced the accepted result.

Evidence: `artifacts/verification/ntfs-map-reset/diagnostic-after-2.log` and its JSON
report. Caps remained 512 MiB per process, 1 GiB per job, 384 MiB managed heap, and
120 seconds. The native desktop rendered during the scan.

## Outcome and remaining gaps

Local verification passed; user confirmation is pending. The immediate NTFS
update-sequence rejection remains a separate known limitation; C: currently uses
the portable fallback.

The local reset-and-memory-failure reproduction now passes with an explicitly
partial map. Final checks passed; user confirmation remains pending. The memory
allowance is a conservative estimate and can stop before the entry cap.
External hard limits still protect against allocations outside that estimate.

## User confirmation

On 2026-10-11, the user retested the updated window and confirmed: "Yes, the reset
and crash are gone". Attempt 002 resolves the reported bug. Earlier references to
pending confirmation describe the state before this retest. Complete C: inventory
and the NTFS record rejection remain separate limitations.
