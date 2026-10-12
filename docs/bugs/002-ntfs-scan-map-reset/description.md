# NTFS scan loses discovered tiles and exits

## Status

Awaiting user confirmation of attempt 003 after local verification on 2026-10-12.
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
