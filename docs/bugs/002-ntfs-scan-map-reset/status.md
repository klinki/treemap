# Bug status

## Current state

`awaiting-user-confirmation`.

## Active attempt

[Fix attempt 002](fix-attempt-002.md).

## Last updated

2026-10-11.

## Confirmation date

Pending user retest.

## Resolution summary

The original process exit was a memory-guard termination. A diagnostic reproduction
confirmed a preview `OutOfMemoryException` while growing the snapshot indexes. Scan
finalization restores the prior empty map, and the desktop leaves the failure
unobserved. Attempt 001 reduced allocations and exposed the failure, but the visible
C: retest still failed during tree construction. Attempt 002 adds a memory budget
and an explicit incomplete result. Its visible C: retest preserved 76,715 discovered
entries at 151.90 MiB peak process memory, with no scan fault or guard termination.
The scan is partial, and the immediate NTFS record rejection remains unresolved.

## Attempt history

- Attempt 001 planned after the C: reproduction narrowed the reset to scan
  finalization following stalled previews. Implementation will reduce construction
  memory while preserving accepted-data and identity contracts.
- Attempt 001 passed its automated checks but failed the visible C: reproduction.
  [Attempt 002](fix-attempt-002.md) will bound discovery before construction fails.
- Attempt 002 passed the visible C: reproduction and affected automated checks.
  Awaiting user confirmation; the bug remains open.

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

## Notes

Passing local verification will leave this bug awaiting user confirmation.
