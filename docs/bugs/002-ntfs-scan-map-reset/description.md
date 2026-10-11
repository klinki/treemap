# NTFS scan loses discovered tiles and exits

## Status

Awaiting user confirmation after attempt 002.

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
