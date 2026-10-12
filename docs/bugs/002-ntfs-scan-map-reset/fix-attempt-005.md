# Fix attempt 005

## Attempt status

Confirmed by the user on 2026-10-12; implementation commit 6784813.

## Goal

Keep valid metadata split across MFT records on the NTFS backend, including the
user's C: volume. Preserve complete names, hard links, folder relationships,
timestamps, attributes, and unnamed-stream sizes.

## Relation to previous attempts

Attempt 004 is confirmed by the user. Its memory-limit removal and percentage
display remain unchanged. This attempt addresses the previously documented MFT
fallback limitation at the user's explicit request. Keep the disk experiment
separate and preserve all prior verification history.

## Proposed change

Resolve extension metadata into logical base-file records before directory and
entry visitors consume it. Validate record ordinals, sequences, ownership,
attribute identities, and continuation semantics. Support resident and
nonresident attribute lists. Prefer bounded temporary state for one logical file
over retaining the whole MFT as a second object graph. Choose the smallest clean
design that preserves correctness on real NTFS metadata.

## Risks

The volume can change during scanning. Reused record numbers must not attach
stale metadata to another file. Continuations must not duplicate sizes or names.
Malformed references and cycles must terminate with an honest fallback; they
must never silently create a complete-looking partial inventory.

If nonresident list values require direct volume reads, Windows treats volume
handles as noncached even without an explicit flag. Offsets, lengths, and buffer
alignment must follow the sector requirements; volume bounds and attribute value
lengths must be checked before reading. All reads remain read-only.

## Files and components

The Windows MFT parser, record source/stream, backend, native read helpers, and
their scanner fixtures and regression tests. Root owns docs and process launches;
Muse Spark Contributor owns implementation. Core, host, and memory-cap behavior
remain outside the requested change.

## Verification plan

Root runs serial tests and elevated visible C: scans with deadlines and no memory
caps. Small fixtures cover extension names and data, hard links across scan roots,
directory metadata, resident/nonresident lists, out-of-order records, stale or
missing references, cycles, malformed data, cancellation, and raw versus
kernel-decoded records. Run the complete scanner suite after focused checks.
Verify that real C: stays on the NTFS backend and publishes a retained final map
at detail 1, without the split-record fallback. Compare inventory and totals
against ordinary scans with existing hard-link/reparse policy accounted for.

## Primary references

- [ATTRIBUTE_LIST_ENTRY](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry)
- [ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)
- [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [Volume handle and noncached I/O requirements](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [File buffering](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering)

## Implementation summary

Muse Spark Contributor implemented resident/nonresident list parsing and per-file
extension resolution. Supervisor review corrected format assumptions, continuation
size handling, and duplicate identities. Raw volume reads use aligned unmanaged
buffers; malformed references still produce an explicit portable fallback.

## Test results

Final scanner verification passed 383 checks with six platform skips. The host
suite passed 86 checks before the final parser identity cleanup; the focused
extension suite passed 62 checks after it. Native evidence appears below.

### Contributor round 1 review

The first implementation uses per-file resolution, a resident list parser, data
run decoding, and direct volume reads. Root fixed a range-expression compile
error and an incomplete XML parameter comment before verification. The focused
run then passed 29 and failed eight of 37 checks. Seven new supervisor regression
cases exposed unchecked repeated/base references, VCN-zero identity mismatches,
unlisted names merged from fragments, rejection of a valid unnamed-entry zero
offset, and concatenation across a VCN gap. Contributor's timestamp test also
failed because an extension's standard-information timestamp lost to the base
filename cache.

Static review found that actual volume buffers are not aligned despite the
comments, volume bounds are unavailable, and split nonresident list values in
different records cannot be assembled by reading the entire value first. The
new 64 KB cluster ceiling also rejects larger valid modern NTFS clusters.
Another Contributor round must correct these contracts and preserve the focused
regressions. No native success is claimed and the change is not committed.

## Outcome and next step

Native extension-record support is committed as 6784813 and confirmed by the
user. One unnamed-record warning remains outside this repair. Follow-up parsing
progress is tracked separately in attempt 006.

### Contributor round 2 review

Root removed a redundant timestamp loop that used ReferenceEquals on value types
(CA2013), and corrected two case-sensitive named arguments in the new fixtures.
The focused suite then passed 53 checks in 62 ms. The complete scanner suite
passed 374 checks, with six platform skips, in 22 seconds. Both runs explicitly
disabled memory limits. Logs are under artifacts/verification/ntfs-map-reset:
attempt005-attribute-review-r2-tests-final.log and attempt005-scanner-r2.log.

Primary-source review corrected an assumption in root's round 2 request:
ATTRIBUTE_LIST does not list itself, and its nonresident mapping pairs remain in
the base record. NTFS-3G's layout documentation states these restrictions, and
Linux ntfs3 loads the list from the base's VCN-zero mapping. The recursive list
bootstrap and its synthetic success fixtures are unnecessary for the requested
extension support. This is a correction to root's earlier requirement, rather
than a newly discovered lack of support for valid NTFS files.

References: https://raw.githubusercontent.com/tuxera/ntfs-3g/master/include/ntfs-3g/layout.h
(lines 949-975) and https://raw.githubusercontent.com/torvalds/linux/master/fs/ntfs3/attrlist.c
(ntfs_load_attr_list). These were read for format verification only; no source
implementation was copied.

Static review also found continuation DATA size fields read before checking
LowestVcn, even though those sizes are defined only at VCN zero. Round 3 will
remove unsupported list recursion, ignore undefined continuation sizes, retain
exact reference/identity checks, and make the production reader simpler before
native C verification. No native success or MFT-support commit is claimed yet.

### Contributor round 3 review

The resolver now loads the base-mapped list once and processes each reference
with a per-file fragment cache. Nested list bootstrap was removed. Undefined
continuation DATA sizes are ignored before reading the size fields. Tests cover
raw and kernel-decoded continuations containing all-ones sizes, multiple real
fragments with distinct name instances, and an entry crossing noncontiguous run
boundaries. Duplicate selected identities and malformed list shapes decline.

Root corrected an unsigned/signed multiplication compile error. Focused tests
passed 61/61 in 47 ms; the complete scanner suite passed 382 with six platform
skips in 22 seconds; the host suite passed 86/86 in 12 seconds. The ignored
production-window diagnostic rebuilt successfully. All process runs explicitly
disabled memory limits. Logs: attempt005-attribute-review-r3-compiled.log,
attempt005-scanner-r3.log, attempt005-host-r3.log, and
attempt005-diagnostic-build-r3.log under artifacts/verification/ntfs-map-reset.

An elevated visible C: run was launched with a 600-second deadline and no memory
cap, using production scan defaults. Native success is pending this live check.

### Supervisor final identity review

Root removed duplicate recording of nonresident DATA attribute identities and an
unused selected-standard-information flag. Identity matches now require exactly
one attribute, including base-self references. An additional regression rejects
ambiguous duplicate base names. The focused suite passed 62/62 after cleanup.
The first cleanup verification exposed unchanged duplicate additions because
newline-sensitive replacements had not matched; root corrected the edits before
retesting. No such intermediate binary was used for a native scan.

Windows cancelled the first administrator launch. No native C scan started and
no native success is claimed. Root requested a retry preference before launching
another UAC prompt.

### Native visible C: verification

After the user requested a retry, Windows elevation succeeded. The rebuilt
production-window diagnostic ran with production defaults, no .NET heap cap,
no kernel memory limits, and a 600-second process deadline. NTFS MFT returned
success and never started the portable backend. Its final result had no fallback
reason: 2,928,281 files and 616,194 directories (3,544,475 entries). Scan time was
180.5 seconds. The process ended normally after 185.06 seconds; peak process/job
working set was 2,073.015625 MiB (about 2.17 GB).

The final published/displayed snapshot had 3,544,476 nodes. Every recorded frame
stayed at detail 1 with no temporary expansion. The final percentage was 100%,
and the retained map showed no memory stop, exception, reset, or crash. The initial
snapshot held free and unaccounted space; later snapshots replaced the residual
with discovered data. The directory pass preceded file publication.

This verifies native extension support on this live C: volume. It does not claim
an entirely warning-free inventory: IsComplete was false because one MFT record
had no visible name and was skipped. The desktop reported two unreadable-entry
diagnostics. That honest incomplete status remains; it was not caused by a memory
budget or split-record fallback. No identical-instant comparison with portable C:
was run because the live volume changes; focused backend fixtures verify names,
hard links, timestamps, and sizes. Earlier portable counts are historical evidence.

Evidence: artifacts/verification/ntfs-map-reset/attempt005-visible-full-c-final.log
and its .log.json runner result. Final source checks:
attempt005-scanner-final.log (383 passed, six skipped) and
attempt005-diagnostic-build-final.log (successful rebuild). No benchmark rerun.

### User confirmation

On 2026-10-12, the user answered the native scan confirmation question with
"Yes, native scanning worked". This confirms attempt 005's extension-record
repair. Attempt 006's additional indexing progress and time estimate still need
their own confirmation; memory limits remain disabled.
