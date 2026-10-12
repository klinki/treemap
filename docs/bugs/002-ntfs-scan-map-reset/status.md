# Bug status

## Current state

`awaiting-user-confirmation` for MFT extension-record support. Attempt 004's uncapped scan and
percentage repair remains confirmed.

## Active attempt

[Fix attempt 005](fix-attempt-005.md).

## Last updated

2026-10-12.

## Confirmation date

2026-10-12. The earlier 2026-10-11 confirmation applied to attempt 002.

## Resolution summary

The original process exit was a memory-guard termination. A diagnostic reproduction
confirmed a preview `OutOfMemoryException` while growing the snapshot indexes. Scan
finalization restores the prior empty map, and the desktop leaves the failure
unobserved. Attempt 001 reduced allocations and exposed the failure, but the visible
C: retest still failed during tree construction. Attempt 002 adds a memory budget
and an explicit incomplete result. Its visible C: retest preserved 76,715 discovered
entries at 151.90 MiB peak process memory, with no scan fault or guard termination.
The scan is partial, and the immediate NTFS record rejection remains unresolved.

The user confirmed on 2026-10-11 that the reset and crash are gone in the updated
window. This closes the reported map-reset and process-exit bug. Complete C:
inventory and the NTFS record rejection remain separate limitations.

After the follow-up failures, attempt 004 removed memory caps at the user's
request and added the percentage label. Visible verification retained the full
accessible C: map at detail 1 and reached 100%. The user accepted this repair on
2026-10-12 with "Good, commit your changes." MFT fallback and protected-path
warnings remain documented limitations; the former 2 GB constraint is not met
by this uncapped run and was superseded by the user's instruction.

## Attempt history

- Attempt 001 planned after the C: reproduction narrowed the reset to scan
  finalization following stalled previews. Implementation will reduce construction
  memory while preserving accepted-data and identity contracts.
- Attempt 001 passed its automated checks but failed the visible C: reproduction.
  [Attempt 002](fix-attempt-002.md) will bound discovery before construction fails.
- Attempt 002 passed the visible C: reproduction and affected automated checks.
  Awaiting user confirmation; the bug remains open.
- Attempt 002 confirmed by the user on 2026-10-11: "Yes, the reset and crash are gone".
- Attempt 003 opened after the user reported inconsistent detail and failure to
  complete a C: scan. The earlier confirmation applied to the reset and crash only.

- Attempt 004 accepted by the user on 2026-10-12 after the uncapped visible
  verification and regular-app retest request. Implementation commit: 2a58ded.

## State change log

- 2026-10-11: Opened from user report of a mid-scan map reset and later exit.
- 2026-10-11: Confirmed the bounded-process memory termination and began code review.
- 2026-10-11: Reviewed Muse's findings. Backend-decline trigger remains unconfirmed;
  delegated a diagnostic harness to capture transitions and memory before a fix.
- 2026-10-11: C: diagnostic reproduced a reset after a portable scan; NTFS declined
  before discovery. Planned attempt 001 and added explicit runner-fault logging.
- 2026-10-11: Second reproduction captured the preview allocation exception and
  stale desktop status. Reviewed Muse's implementation and delegated corrections
  to identity transactions, live-status tests, and memory-failure reporting.
- 2026-10-11: Attempt 001 passed 522 tests with six skips. Visible C: reproduction
  still restored an empty map after a preview allocation failure. Opened attempt 002.
- 2026-10-11: Attempt 002 kept the C: map visible and returned a memory-limited
  partial result under unchanged caps. Core 170, scanner 283, and desktop 81 tests
  passed; six scanner checks were skipped by platform/environment.
- 2026-10-11: Awaiting the user's retest before marking the reported bug fixed.
- 2026-10-11: User confirmed that the reset and crash are gone. Marked fixed;
  memory-limited partial scans and the NTFS fallback remain documented limitations.
- 2026-10-11: Reopened for the same scan flow. The user's screenshot shows
  descendants at detail 1 and a memory-limited stop after 48,590 entries. Started
  attempt 003 to enforce consistent depth and allow full enumeration within caps.

- 2026-10-12: User accepted attempt 004 and requested the changes be committed.
  Marked fixed. Implementation was already committed as 2a58ded; this update
  records the confirmation without changing code or verification results.

## Notes

Passing local verification will leave this bug awaiting user confirmation.

User confirmation has now been received for the reported reset and crash.

- 2026-10-11: User authorized a 2 GB total app budget for C: and asked to defer the
  disk-storage rewrite. Preserved its draft on experiment/disk-backed-scanner at
  19da19a and returned to main. Continuing attempt 003 with managed storage.

- 2026-10-12: Managed-storage tests passed (590 tests, six environment skips).
  Elevated C: retained 1.63 million entries with frame depth 1 and no reset or
  crash, but stopped at the memory estimate. Peak job memory was 1,435.11 MiB.
  The full-scan requirement remains open; another memory-review round is active.

- 2026-10-12: Round 7 published and displayed all 3,542,813 accessible C: entries
  under the 2 GB constraint. Peak job memory was 1,447.96 MiB (1.52 GB), and every
  frame remained at detail 1 without temporary expansion. No resource limit,
  allocation fault, or map reset occurred. Protected paths remain unreadable and
  are reported honestly. All 610 affected tests passed, with six skips. Attempt
  003 is awaiting user confirmation; the earlier confirmation covered attempt
  002 only.

- 2026-10-12: The user rejected attempt 003 confirmation: "The scan still stops
  or resets", then clarified "Map reset to unscanned". The successful round 7
  evidence came from a minimized diagnostic. The actual visible app remains
  running but has no exception logging, so its precise failure is not yet known.
  Opened attempt 004 to reproduce with visible rendering under the same 2 GB cap
  and fix the evidenced failure. The disk experiment remains deferred.

- 2026-10-12: User confirmed the exact failure is out of memory and requested
  no memory limits for now, superseding the 2 GB cap. Delegated removal of
  launch-time kernel/GC memory limits, default scanner estimated limits, and
  addition of a whole-number percentage beside the progress bar. Process
  deadlines remain; explicit library memory budgets remain opt-in.

- 2026-10-12: Attempt 004 passed 407 affected tests with six skips. Its elevated,
  maximized, visible C: scan published and displayed all 3,543,334 accessible
  entries at detail 1 and reached 100% without allocation failure or reset.
  Scan time was 156.6 seconds and peak process/job memory was 2.35 GB. No memory
  caps were applied under the user's updated instruction. MFT fallback and
  protected-path warnings remain. Awaiting a regular-app retest confirmation.

- 2026-10-12: User explicitly confirmed "Yes, map and percentage work", further
  confirming attempt 004. Separately, they requested support for the MFT
  attribute-list limitation. Reopened this scan-flow record for attempt 005;
  the uncapped scan repair remains accepted and memory caps remain disabled.

- 2026-10-12: Attempt 005 passed 383 scanner checks with six platform skips;
  host checks passed 86/86. Elevated visible C: retained 3,544,475 discovered
  entries on NTFS MFT without fallback, reached 100%, and kept detail 1.
  Scan time was 180.5 seconds and peak process/job memory was about 2.17 GB;
  no memory limits were imposed. One nameless MFT record caused an explicit
  incomplete warning, with two unreadable-entry diagnostics. No memory stop,
  reset, or crash occurred. Native extension support awaits user confirmation;
  the confirmed attempt 004 repair remains accepted.
