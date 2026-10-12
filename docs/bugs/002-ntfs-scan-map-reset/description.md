# NTFS scan loses discovered tiles and exits

## Status

Open for attempt 005, adding MFT extension-record support. Attempt 004's
uncapped scan and percentage display remain confirmed.
Reopened on 2026-10-11 after follow-up reports in the same scan flow.

## Reported symptoms

The elevated desktop scan progressively revealed entries. Around halfway through,
the discovered tiles disappeared and the map showed one large unscanned space
tile. The process later exited.

## Expected behavior

Discovered entries remain visible as scanning progresses. A backend fallback,
scan limit, or failure has an explicit status. A bounded scan does not exhaust its
process memory budget during normal progress publication.

## Reproduction details

- Build at commit `7d32327`, starting at detail level 1.
- Elevated visible launch at 2026-10-11 21:38:06 local time.
- Intermediate snapshots, free space, and unaccounted space enabled.
- Hard limits of 512 MiB per process and 1 GiB per job, with a 90% memory guard.
- User confirmed the selected drive was `C:\`. Status text after the reset is unknown.

## Affected area

NTFS enumeration, fallback lifecycle, intermediate snapshot construction, and
desktop scan status.

## Constraints

Delegate implementation to Muse Spark. The supervisor reviews, runs all processes
under hard limits, and commits locally. Preserve safe fallback and cancellation
behavior. Do not raise memory limits as a substitute for investigating memory use.

## Open questions

- What triggered the map reset?
- Which retained structures or allocation peaks drove the memory limit?
- Can the scan report resource limits and preserve useful progress safely?

## Local verification update

Attempt 002 reproduced a successful partial C: result under the original caps.
Discovered tiles remained visible, the final map held 76,715 scanned entries, and
the status named the memory limit. Peak process memory was 151.90 MiB. The scan
remains incomplete, and C: still uses portable fallback after an NTFS record
rejection. User confirmation of the reset/crash repair is pending.

## User confirmation

On 2026-10-11, the user retested the updated window and confirmed: "Yes, the reset
and crash are gone". The reported bug is fixed. The scan still returns an explicit
partial result at its memory budget, and the NTFS record rejection still causes
portable fallback. The pending confirmation in the earlier update is resolved.

## Follow-up report

The user supplied a screenshot of C: at basic detail 1. Windows contains an
Installer header and many file tiles, although detail 1 should show only immediate
children of the displayed root. The scan stops after 48,590 entries under the memory
estimate, leaving 673.7 GB unaccounted. The status also shows portable fallback
following an MFT update-sequence rejection. A full scan is required; stopping early
is insufficient even though it avoids the previously reported reset and crash.

On 2026-10-12, attempt 003 published all 3,542,813 accessible C: entries with
maximum frame depth 1, no reset or allocation failure, and 1.52 GB peak job memory.
Protected paths still produce an honest incomplete result. The full accessible
walk is no longer truncated by a resource budget. Visible user confirmation is
pending; the disk-backed rewrite remains on a separate experimental branch.

The user then reported that the updated visible C: app reset to an unscanned map.
Attempt 003 is not accepted. Its minimized diagnostic success does not establish
success with visible rendering; attempt 004 investigates that difference under
the unchanged 2 GB app constraint.

The user then confirmed the status "Scan failed: out of memory. Try a smaller
folder." They requested no memory limits for now and a whole-number percentage
beside the progress bar. The no-limit instruction supersedes the earlier 2 GB
constraint for the next verification run.

Attempt 004 completed a visible, maximized C: scan without memory caps. The final
map contained all 3,543,334 accessible entries, remained at detail 1, and reached
100% without resetting. Peak process/job memory was 2.35 GB. The new regular-app
user retest remains pending.

## Attempt 004 confirmation

On 2026-10-12, the user accepted the updated repair with "Good, commit your
changes." The implementation is committed as 2a58ded. This resolves the pending
confirmation above. MFT fallback and protected-path warnings remain documented.

## MFT support request — 2026-10-12

The user requested "Fix that, I want it to be supported" for metadata split
across MFT records. A valid attribute list must not itself force portable
fallback. Names, hard links, directory relationships, and unnamed-stream sizes
must include the referenced extension records. Invalid or stale metadata must
still produce honest diagnostics or fallback. The latest instruction remains
no imposed memory limits, and the disk-backed experiment remains deferred.
