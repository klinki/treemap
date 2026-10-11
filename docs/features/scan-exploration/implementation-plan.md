# Scan exploration implementation plan

Extend the reference interactions with the user's approved scan and exploration
updates. Muse Spark implements the changes; the supervisor reviews, verifies under
hard process limits, and commits locally without pushing.

## Behavior

- Add a progress bar below the toolbar. Whole-volume scans show the fraction of
  used bytes discovered, capped below completion until the scan finishes. Folder
  scans and unavailable capacity use indeterminate progress. Cancellation, failure,
  fallback, and stale callbacks must leave coherent progress state.
- Publish an initial volume preview before enumeration with the cached free-space
  and unaccounted-space blocks. Later previews replace the residual with discovered
  entries. Start the desktop with both optional blocks visible. Preserve their
  independent toggles and the existing final-only library default.
- Fill the available map viewport using its aspect ratio. Coalesce resize work
  instead of rebuilding the entire tree on every resize event. Keep cursor zoom,
  hit testing, navigation, and idle timer behavior consistent.
- Single-click temporarily expands the clicked folder inside the current map,
  showing two additional descendant levels where geometry permits. The displayed
  root, camera, and history stay in place. Selecting another node clears the
  previous expansion. Double-click navigates to the clicked folder using the
  pointer location even if the first click changed the layout.
- Add a filename filter row with Apply, Enter, and Clear. Support `*` and `?`,
  including `*.mp4`. Recalculate folder logical and allocated sizes from matching
  files, keep their ancestors and stable IDs, and leave the published scan tree
  unchanged. Blank or `*` clears the filter. Filtered views omit volume-space
  blocks; clearing restores the user's space visibility choices. Apply the filter
  to each new preview. Show an explicit empty result and matching totals.

## Implementation and verification

- Delegate separate ownership of Scanner, Core, and WinForms files and tests.
  Agree on minimal progress and filter contracts before edits.
- Review initial-preview identity and rollback, progress accounting, filtering
  cache invalidation, temporary expansion, resize cost, and double-click ordering.
- Build Release and run proportionate tests through the existing bounded runner,
  with 512 MiB per process, 1 GiB per job, and a 120-second deadline. Run jobs
  serially; disable build servers, shared compilation, and parallel builds.
- Inspect deterministic hidden-window renders of wide and narrow layouts,
  initial/progressive space, temporary expansion, filtered totals, and no matches.
- Record evidence and runtime limits, then stage only task files and create a
  Conventional Commit. Preserve unrelated changes and never push.
