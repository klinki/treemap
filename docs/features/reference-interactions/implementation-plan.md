# Reference interactions

Implement the behavior approved from the SpaceSniffer recording. The recording shows scanning through 00:48, drill-down and navigation through 01:19, then detail controls and toggles for free and inaccessible space.

## Scope

1. Publish immutable scan previews while entries arrive. Make previews opt-in for library callers and enable them in the desktop host. Throttle and serialize preview construction, retain only the latest snapshot, and keep node identity stable through the final result. Restore the prior map on cancellation or failure. An abandoned NTFS backend must not contaminate the portable fallback.
1. Add volume accounting for free space and space not represented by scanned files. Use a nonnegative residual against volume usage and describe it as unaccounted space, since metadata, inaccessible entries, and allocation differences can contribute. Do not invent sizes for unreadable folders or attach volume space to directory scans. Keep file totals separate from synthetic space blocks.
1. Reserve folder header strips above descendants. Show folder names and sizes in headers and leaf names and sizes in their cells. Show labels on synthetic space blocks. Highlight the hovered node and its ancestor chain, and add a tooltip with the path and size.
1. Add visible Back, Forward, Up, and Home controls. Back and Forward restore navigation history and camera state. Up resolves the actual parent from the whole snapshot. New navigation invalidates forward history, and snapshot changes remove stale entries safely.
1. Add bounded More detail and Less detail controls that change visible descendant depth without rescanning or changing camera zoom.
1. Add independent Free space and Unaccounted space display toggles. Filtering changes layout proportions without changing the snapshot or scan totals. Use green for free space and gray for unaccounted space. Start with these optional blocks hidden, as in the recording.

## Constraints

Keep the existing portable scanner, NTFS fallback, elevation workflow, entry limits, idle timer behavior, and resize handling. Prefer direct, readable changes. Do not add packages or copy reference implementation code. Preserve unrelated changes and never push.

Muse Spark implements the changes and meaningful regression tests. The supervisor reviews them, performs all execution under the existing hard process and job limits, and creates the local Conventional Commit. Delegated agents must not run shell commands, tests, builds, scans, processes, Git mutations, or further delegation.

## Acceptance and verification

- A controlled scan produces a preview before completion, then a correct final snapshot with stable identities. Cancellation and failure restore the prior map, and fallback discards abandoned data.
- Volume accounting handles incomplete scans, mixed allocation availability, overcounted hard links, unavailable volume metadata, and arithmetic bounds without negative or fabricated residuals.
- Headers remain outside child bounds. Rendered labels, hover ancestry, and special-space colors are checked with small deterministic fixtures.
- Navigation history, actual parent lookup, detail limits, and space filtering have meaningful core or host tests. Display controls do not invoke scanning.
- Run a Release build and affected tests with the bounded runner. Inspect a rendered fixture and record results and any unverified live behavior before committing.
