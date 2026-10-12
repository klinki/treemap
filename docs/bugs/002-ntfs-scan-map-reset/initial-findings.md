# Initial findings

## Confirmed facts

- The run's bounded-process report records `StopReason: memory_limit`, exit code
  124, elapsed time 77.9925223 seconds, and peak process/job memory 485.65234375 MiB.
- The application stdout/stderr log is empty.
- The memory guard stopped the process. There is no evidence yet of a separate
  unhandled application exception.
- The scanner resets collected entries and republishes its initial space basis
  when an NTFS backend declines after collecting entries.
- Intermediate previews rebuild a complete immutable tree from captured entries.
- The default entry budget is 500,000 entries. It is a count limit, not a heap cap.

## Likely cause

A late backend decline is one possible explanation for the lost tiles. The exact
trigger is unconfirmed. Snapshot rebuilds and retained scan structures are memory
suspects requiring investigation.

## Unknowns

The selected drive, fallback reason, entry count at reset, and allocation breakdown
were not recorded by the interactive launch.

## Reproduction status

User reproduced the visible reset and exit. Supervisor has confirmed the memory
termination from the recorded launch report, but has not reproduced the reset.

## Evidence gathered

- `artifacts/verification/scan-exploration/ntfs-validation-20261011-213806.log.json`
- `FileSystemScanner.Scan` and its `PreviewLoop` fallback lifecycle.
- `NtfsMftScanBackend`, `NtfsDirectoryMap`, and `ScanOptions`.

## 2026-10-11 investigation update

Muse's read-only review confirmed that a second-pass entry-budget stop succeeds
with an incomplete result; it does not trigger a backend fallback. The NTFS
directory pass produces no discovered file previews. A reset after discovered
tiles therefore requires evidence of a later backend decline or a different
publication failure.

The review found simultaneous collector paths, captured entry arrays, mutable
tree nodes, immutable tree nodes, snapshot indexes, identity paths, and the NTFS
directory map during preview construction. This is a concrete memory multiplier,
but the allocation breakdown and entry count in the reported run are unknown.

Supervisor rejected retaining abandoned NTFS data as if it were valid, and rejected
dropping identity reservations needed for stable preview/final node IDs. Merely
slowing previews would also leave the final full-tree build unbounded by bytes.

Next investigation step is an ignored diagnostic desktop harness that logs backend
transitions, snapshot summaries, status, and memory under the existing hard caps.
It will not retain snapshots or log individual file paths.

User confirmed the affected drive was `C:\`. The diagnostic harness compiled
under the existing caps. Supervisor corrected an unnecessary, invalid physical-size
wrapper and ensured fast scan completion and unhandled UI exceptions are logged.
The elevated reproduction began at 2026-10-11 21:47:35 local time with the original
500,000-entry budget, 512 MiB process cap, 1 GiB job cap, and 120-second deadline.

## 2026-10-11 C: reproduction evidence

The first diagnostic run completed in 22.44 seconds, peak process memory
401.56 MiB. NTFS declined immediately at zero collected entries with an update
sequence validation failure. The portable backend produced progressively larger
snapshots up to 225,769 nodes, then stopped publishing while enumeration continued
to 499,603 retained entries. When enumeration ended, publication restored the
empty prior snapshot. Desktop status remained `Scanning C:\` with `Backend:
choosing...` even though `IsScanning` became false.

This contradicts the initial hypothesis of a late NTFS fallback causing the reset
in this reproduction. The evidence instead points to a preview fault rethrown
after enumeration and an unobserved failure in the desktop scan task. A second
diagnostic captures the runner exception explicitly. The managed heap was close
to its 384 MiB limit before previews stopped.

Evidence: `artifacts/verification/ntfs-map-reset/diagnostic-before.log` and its
bounded-process JSON report. A generated configuration type conflict produced one
warning in the ignored diagnostic project; production projects built successfully.

The second diagnostic confirmed `OutOfMemoryException` from dictionary resizing
inside `TreemapSnapshot.Index`, reached through `SnapshotPublisher.Publish` on the
preview timer. `PreviewLoop.ThrowIfFaulted` rethrew that exception after enumeration,
then the scanner restored the empty prior map. The host excludes this exception
from its failure handler and discards the task returned by `RunScanAsync`, leaving
no error status. The original process later exceeded the external memory guard.

This is now the confirmed reproduction mechanism, rather than a late NTFS
fallback. Second diagnostic completed in 22.39 seconds at peak process memory
403.48 MiB. Its runner-fault log records the complete exception stack.

Evidence: `artifacts/verification/ntfs-map-reset/diagnostic-before-2.log` and its
bounded-process JSON report. Snapshot indexes currently start at capacity 256 and
grow repeatedly, temporarily retaining old and new backing arrays. A second Muse
implementation task will pre-size these indexes from a nonallocating node count
and cover the allocation reduction, independently of scanner tree/identity edits.

## Attempt 001 retest

The allocation tests passed after pre-sizing indexes and removing empty per-file
collections. The visible C: reproduction nevertheless captured another preview
`OutOfMemoryException`, now in `ScanTreeBuilder.Materialise`. Its last useful
snapshot held 299,438 nodes. Enumeration continued to 499,603 entries before the
scanner restored the prior empty map. The desktop reported the memory error.

The process closed normally in 21.38 seconds at peak process memory of 403.80 MiB;
normal diagnostic exit is not a successful scan. Optimization alone is insufficient
under this managed heap limit. Attempt 002 will stop discovery before construction
exhausts its allowance, returning an explicitly incomplete map through normal
publication rather than accepting an allocation exception as success.

Evidence: `artifacts/verification/ntfs-map-reset/diagnostic-after.log` and its JSON
report. This visible run includes native rendering costs, unlike the minimized
investigation runs.

## Attempt 003 visible failure — 2026-10-12

Round 7's minimized diagnostic published the full accessible C: inventory below
2 GB, but the user reported that the actual visible app resets to unscanned space.
The app is still running, so the observed reset is not itself evidence of a hard
guard termination. Its redirected console log is empty. The host catches scan
exceptions and the scanner restores the prior source on failure; the exact
exception and phase must be captured in a visible reproduction. No larger cap
or disk-backed rewrite is authorized for this repair.

Subsequently the user explicitly requested no memory limits for now, superseding
the previous 2 GB cap. The user screenshot and status text confirm an allocation
failure. The NTFS fallback is separate: this reader declines ATTRIBUTE_LIST
records because it does not follow metadata in extension records. That is a
reader limitation, not evidence of volume corruption. The disk-backed draft
remains deferred.

## MFT extension support investigation — 2026-10-12

The parser records base ordinals/sequences and HasAttributeList. MftRecordStream
visits live records using FSCTL_GET_NTFS_FILE_RECORD. The first DirectoryVisitor
deliberately rejects every attribute list and ignores extension records; entry
enumeration likewise cannot merge extension metadata. This reproduces immediately
on C: before native discovery and causes the confirmed portable fallback.

FSCTL_GET_NTFS_FILE_RECORD may return a lower live ordinal than requested, so
resolving a specific reference must validate the returned ordinal and sequence.
Nonresident size fields describe the full stream only at lowest VCN zero.
Attribute-list entries identify records and attribute instances; NTFS metadata
can place the list itself outside the base record. The implementation must
handle these cases without dropping links or double-counting continuation sizes.

Primary references: Microsoft's ATTRIBUTE_LIST_ENTRY, ATTRIBUTE_RECORD_HEADER,
and FSCTL_GET_NTFS_FILE_RECORD documentation. Implementation has been delegated
to Muse Spark Contributor; root will review and run all verification.
