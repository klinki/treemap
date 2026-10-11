# Scan exploration verification

The [implementation plan](implementation-plan.md) records the approved progress,
space accounting, viewport, click, and filtering changes. Muse Spark implemented
the Scanner, Core, and desktop changes. The supervisor reviewed the source and
performed verification under the repository's hard process limits before committing.

## Reviewed behavior

- A whole-volume scan publishes its initial free and unaccounted blocks before
  backend enumeration. One cached capacity reading supplies that initial preview,
  later previews, progress, and the final tree. Discoveries reduce the residual.
  Library callers still receive final-only publication unless previews are enabled.
- Initial previews share the identity transaction and rollback path with later
  previews. Cancellation and subscriber failure restore the previous tree and
  source. A portable retry discards the abandoned backend's entries and identities
  before republishing the initial volume basis.
- The progress bar sits below the toolbar and above the filter row. Volume progress
  estimates discovered layout weight divided by used bytes, capped at 99 until
  completion. Folders and unavailable capacity use marquee progress. Accepted
  incomplete results reach 100 while preserving their warning and residual;
  cancellation and failure reset to zero. Stale callbacks cannot alter a later scan.
- The map uses the viewport aspect ratio with a fitted camera. Resize refits the
  camera and back buffer immediately and coalesces geometry work with a one-shot
  100 ms timer. A relayout that consumes the resize stops that timer.
- Deferred single clicks expand the selected folder by two extra levels inside the
  current map. Selecting a different node restores the former branch's global
  detail. Double clicks navigate to the first press's stable node ID, including
  when a same-source preview changes geometry between presses. Dragging, held
  presses, stale sources, changed filters, and removed nodes do not apply a stale
  click. Effective layout depth is bounded at 64, including oversized library options.
- Filename filters use the framework's case-insensitive basename `*` and `?`
  matcher. Matching files retain their ancestors and IDs; folder logical and
  allocated totals are recalculated with saturating addition. Published scan data
  remains intact. Active filters omit synthetic space blocks, and clearing restores
  the source tree and space visibility choices. Projection and summary caches
  invalidate on a new preview or filter. Inactive summaries avoid a full-tree count.

## Review corrections

Review corrected branch depth bounds, source and selection validity, Home clearing
temporary detail, and the framework glob matcher. Pointer review covered WinForms
double-click event ordering and stable-ID latching across moving previews. Tooltip
caches now account for filter changes that alter folder sizes without publication.

The first Core run exposed three test fixtures clicking folder centers covered by
children; corrected fixtures use visible leaves or folder headers. The first Host
build exposed obsolete Skia text calls, replaced with the renderer's installed
`SKFont` API. Two desktop tests started before their initial resize debounce settled;
their setup now waits for both resize and animation settlement without weakening
the interaction assertions.

The supervisor also identified a click/resize race: cancelling the resize timer
when a click requires no relayout leaves stale aspect geometry. The pending resize
must continue unless the click actually rebuilds the map. A regression checks the
settled map geometry after a deferred leaf click and resize.

The first rendering check exposed a zero-byte root label underneath the no-match
message and different size formatting in the filter summary and map. The empty
state now renders exclusively, and the filter summary uses the map's shared
size formatter. The supervisor rechecked both affected views after correction.

## Rendering checks

A deterministic fixture covers initial and discovered space, a wide map, default
and temporary detail, folder focus, a matching `*.mp4` filter, no matches, clearing
at narrow width, a portrait view, and narrow matching and empty filters. The filtered
fixture contains three files with 300,000,000 logical and allocated bytes.

Windows remain hidden. Native toolbar panels are detached temporarily for
`DrawToBitmap` and composited at their original positions, then restored. The
fixture never starts a scan, enumerates volumes, or requests elevation. Its bar
value is set directly to illustrate the placement of estimated progress; scan
lifecycle and progress transitions are covered by the desktop tests.

Generated images and fixture source remain in the ignored local
[scan exploration artifacts](../../../artifacts/verification/scan-exploration/).
The supervisor inspected all eleven views: the map fills the available width and
portrait viewport, controls and summaries wrap at 720 pixels, temporary detail adds
descendants in place, and no-match text appears alone on the empty background.

## Final checks

Every build, test, and renderer process used
[Invoke-Bounded.ps1](../../../tools/Invoke-Bounded.ps1) with 512 MiB per process,
1 GiB per job, and a 120-second deadline. Build servers, shared compilation, and
parallel project builds were disabled. No cap or deadline was reached. One fixture
build inadvertently overlapped the final test run; each retained its own hard caps.
The renderer ran after the tests finished.

- All 11 solution projects built in Release with zero warnings and errors. The
  final build took 4.4 seconds with 113.9 MiB peak process memory and 196.0 MiB
  peak job commitment.
- The final full Release suite passed 548 tests, failed none, and skipped five.
  Core passed 166, Host 78, Perf 19, Scanner 260 with five skips, and Skia 25.
  The run took 29.0 seconds with 70.3 MiB peak process memory and 149.5 MiB peak
  job commitment. Scanner skips retain their existing platform or symbolic-link
  permission conditions.
- The final fixture produced eleven images in 2.0 seconds with 38.9 MiB peak
  process memory and 40.1 MiB peak job commitment. The `*.mp4` projection reported
  exactly three files and 300,000,000 logical and allocated bytes; the summary and
  map both format that total as 286.1 MB using the shared binary size formatter.
- `git diff --check` passed, and local Markdown links resolve. Generated projects,
  images, console logs, resource records, and TRX results remain ignored.

Final local evidence:
[build log](../../../artifacts/verification/scan-exploration/release-build-final.log),
[build resource record](../../../artifacts/verification/scan-exploration/release-build-final.log.json),
[test log](../../../artifacts/verification/scan-exploration/release-tests-final.log),
[test resource record](../../../artifacts/verification/scan-exploration/release-tests-final.log.json),
[test results](../../../artifacts/verification/scan-exploration/release-test-results-final/),
[render log](../../../artifacts/verification/scan-exploration/render-run-final.log),
and [render resource record](../../../artifacts/verification/scan-exploration/render-run-final.log.json).

## Validation limits

- Preview and progress tests use small gated backends. They establish publication,
  accounting, identity, and rollback behavior; they do not measure volume-scale
  throughput or frame smoothness.
- Hidden rendering checks validate layout and text placement. They do not exercise
  every gesture in a visible window or the native tooltip popup.
- A live scan through an accessible NTFS volume, actual UAC consent, and Linux/macOS
  runtime checks remain unverified. Earlier real-directory fallback evidence is in
  the [scanner review](../filesystem-scanner/verification.md).
- Progress is a storage coverage estimate, not a remaining-time estimate.
  Unaccounted space can include unscanned files, inaccessible entries, metadata,
  and allocation differences. Visible hard links can overcount storage.
- No BenchmarkDotNet measurement was run for this change. Earlier layout numbers do
  not measure the new filtering, resize, or preview paths.
