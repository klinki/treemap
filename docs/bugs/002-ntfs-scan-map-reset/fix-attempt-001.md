# Fix attempt 001

## Attempt status

Failed local reproduction. Allocation reductions and truthful status remain useful,
but attempt 002 is needed to bound scan construction.

## Goal

Keep large scan construction within the current bounded run's memory allowance,
prevent a preview allocation failure from silently clearing the map, and report
backend transitions while scanning.

## Relation to previous attempts

First fix attempt. Two investigation rounds preceded it: read-only code review and
a diagnostic harness. The original user report remains open.

## Proposed change

- Remove empty per-file child collections from tree construction and reduce
  redundant path retention while preserving stable identities across previews,
  final results, cancellation, fallback, and rescans.
- Pre-size snapshot indexes from the tree's node count to avoid dictionary-growth
  allocation peaks. Keep immutable public model and lookup behavior unchanged.
- Use measured evidence to address allocation failure before changing publication
  policy. Keep abandoned backend data out of accepted results.
- Surface the active backend and fallback reason during scanning.
- Report managed allocation failures in the desktop status and return controls
  to their idle state, instead of leaving a discarded scan task unobserved.
- Add focused regressions for identity and large-tree memory behavior. Add a
  graceful resource-limit outcome if optimization alone cannot support the current
  entry budget safely.

## Risks

Identity changes can accidentally reuse an ID across different roots or lose IDs
after cancellation. Memory estimates can miss long paths or implied directories.
Throttling previews alone does not solve the final build's allocation peak.

## Files and components

Scanner tree assembly, identity transactions, preview lifecycle, scan progress,
desktop status, and their tests. Diagnostic artifacts remain ignored.

## Verification plan

Supervisor runs serial bounded Release checks for affected projects, then repeats
the C: diagnostic under the same 512 MiB process and 1 GiB job caps. Record actual
memory, exceptions, publication sequence, and whether the entry limit was reached.
The user retests the visible application before bug-record closure.

## Implementation summary

Pre-sized snapshot indexes; made child collections lazy; shared relative-path ID
keys within one root-scoped identity transaction; exposed active backend and
fallback information on progress; added a visible desktop allocation-failure status.

## Test results

Core: 170 passed. Scanner: 271 passed, six skipped. Desktop: 81 passed. The allocation
regressions rejected old dictionary-growth and eager-child-storage controls.

## Outcome

Automated checks passed, but the visible C: reproduction still failed. Superseded
by attempt 002; the user report remains open.

### Visible C: result

The affected suites passed 522 tests with six skips. The visible diagnostic still
captured an `OutOfMemoryException` in `ScanTreeBuilder.Materialise` on a preview.
The latest displayed map held 299,438 nodes. Enumeration reached 499,603 retained
entries, then finalization restored the empty prior map. The desktop correctly
reported the memory failure. The bounded process completed in 21.38 seconds with
peak process memory of 403.80 MiB and peak job memory of 405.00 MiB. Exit code zero
only means the diagnostic window closed normally; it does not mean the scan succeeded.

Evidence is in `artifacts/verification/ntfs-map-reset/diagnostic-after.log` and its
JSON sibling. This attempt did not fix the reported map reset. Its improvements
remain on disk for [attempt 002](fix-attempt-002.md).

## Remaining gaps

The first diagnostic run showed an immediate NTFS parser decline, then a portable
scan. At 499,603 retained entries, publication restored the empty prior snapshot
without a scan-failure status. A second run captures the discarded scan task's
exception explicitly.

The second run confirmed an `OutOfMemoryException` while a preview grows snapshot
index dictionaries. Scanner finalization rethrows the captured preview fault and
restores the empty prior map. The discarded desktop task leaves the error unobserved.

## Supervisor review

The first implementation reduced per-file collections and absolute-path copies.
Review found two independent pending identity maps that could both commit after
switching build modes. The correction uses one transaction and retains the prior
committed root on abort. Review also rejected private-reflection tests and required
a gated test that observes fallback status before the final result arrives.

The snapshot-index allocation regression failed against the old 256-entry initial
capacity, measuring 2,324,048 bytes against its 1,500,000-byte limit. The fixed Core
suite passed 170 tests. Scanner and desktop verification remain pending review.
