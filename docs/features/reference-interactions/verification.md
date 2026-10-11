# Reference interaction verification

The [implementation plan](implementation-plan.md) covers the approved behavior from
the SpaceSniffer recording. Muse Spark implemented the scanner and presentation
changes in separate tasks. The supervisor reviewed the source, corrected integration
errors, and performed execution under the repository's hard process limits.

## Reviewed behavior

- The desktop enables immutable scan previews; library callers retain final-only
  publication by default. A single timer serializes preview construction, skips
  unchanged entry sets, and delays the next build until the current one finishes.
  Entry and depth limits remain in effect.
- Preview construction uses the same pending identity transaction as the final
  tree. Cancellation and failure drain the preview before aborting that transaction
  and restoring the prior tree and source. Portable fallback discards the abandoned
  collector entries and identity reservations. Subscriber failures become failures
  on the scanning thread, including a subscriber's cancellation exception when the
  scan token remains active.
- Whole-volume previews and results use one cached capacity/free-space reading.
  Free bytes are clamped to capacity. Unaccounted bytes are used volume bytes minus
  the sum of each file's allocated size or logical fallback, clamped at zero.
  Folder scans never request volume accounting. Synthetic blocks do not change file
  counts or file totals.
- Layout reserves headers outside child bounds and collapses folders at the detail
  limit or when there is no usable child area. Text uses device-pixel sizes and clips
  to its mapped region. Hover shades ancestor headers while unrelated leaf interiors
  retain their colors.
- Back and Forward restore focus and camera state. Up uses the snapshot's actual
  parent index. Home refits even when already at the root. Source changes reset
  navigation safely; same-source previews preserve valid focus. Source comparison
  follows Windows case-insensitivity and Unix case-sensitivity.
- Detail controls change descendant depth independently of zoom. Space toggles
  change layout without publishing, rescanning, or removing snapshot nodes. The
  desktop starts at depth 4 with both optional blocks hidden.

## Corrections during verification

The first build exposed an unused variable under warnings-as-errors. The next
exposed a timer callback parameter named `_` that shadowed `out _` discards. Both
were corrected. Review also removed a duplicate identity-reset helper and fixed
null source restoration, preview lock consistency, subscriber fault handling,
fallback serialization, and small-region font sizing.

The first complete test run passed the scanner, Skia, and performance test projects
but exposed five new Core/Host test failures. The cases confused zero-area culling
with folder collapse, replaced a one-entry tooltip cache before testing reuse,
called `PerformClick` on hidden buttons, and asserted disappearance before the
animation settled. These findings were sent back to Muse Spark for correction.

The settled tween buffer retains departing cells at zero opacity, so the toggle
test now checks visible cells after settlement. The complete corrected suite passed
before the final visual check identified one more production issue: the default
drop shadow overlapped and darkened the hovered cell after its fill was brightened.
The corrected renderer clips the shadow outside the bounds and preserves the canvas
state. A regression compares interior RGB values with the shadow enabled and
requires the hovered fill to brighten.

## Rendering checks

A small deterministic fixture rendered the default view, both space blocks,
drill-down, a 720-pixel-wide window, more detail, less detail, and hover. All windows
remained hidden. The fixture temporarily detached the native toolbar panels for
`DrawToBitmap` and composited them at their original positions, because native
children of a hidden form otherwise paint blank. It never started a scan, enumerated
volumes, or requested elevation.

The supervisor inspected the saved images for label placement and clipping,
green/gray space blocks, header separation, detail changes, and toolbar wrapping.
The small window keeps every display control reachable. Headers too small for
readable text are deliberately unlabeled. The fixture also produced the expected
absolute tooltip text from its snapshot.

Images and renderer-run logs are under the ignored local
[reference interaction artifacts](../../../artifacts/verification/reference-interactions/).

## Final bounded checks

Every build, test, and renderer process used
[Invoke-Bounded.ps1](../../../tools/Invoke-Bounded.ps1) with 512 MiB per process,
1 GiB per job, and a 120-second deadline. Build servers, shared compilation, and
parallel project builds were disabled. Jobs ran one at a time; no cap or deadline
was reached.

- All 11 solution projects built in Release with zero warnings and errors. The
  final build completed in 4.0 seconds with a 154.3 MiB peak job commitment.
- The full Release solution test run passed 458 tests, failed none, and skipped five.
  Core passed 130, Host passed 42, Perf passed 19, Scanner passed 242 with five
  skips, and Skia passed 25. The whole run took 26.4 seconds with 66.0 MiB peak
  process memory and 132.7 MiB peak job commitment.
- The final renderer fixture produced all seven images in 0.4 seconds with a
  28.1 MiB peak job commitment. The supervisor rechecked the corrected hover image:
  the hovered fill brightens, with ancestor outlines and header shading intact.
- `git diff --check` passed. Generated projects, images, and logs remain ignored.

Local logs and resource records are
[release-build.log](../../../artifacts/verification/reference-interactions/release-build.log),
[release-build.log.json](../../../artifacts/verification/reference-interactions/release-build.log.json),
[release-tests.log](../../../artifacts/verification/reference-interactions/release-tests.log),
[release-tests.log.json](../../../artifacts/verification/reference-interactions/release-tests.log.json),
and [render-run.log](../../../artifacts/verification/reference-interactions/render-run.log).
Per-project TRX results are in the ignored
[release test results](../../../artifacts/verification/reference-interactions/release-test-results/).

## Validation limits

- Native traversal and preview behavior use record fixtures and scripted fast
  backends. A live scan through an accessible NTFS volume and the actual UAC consent
  flow remain unverified. The earlier session verified real access-denied fallback.
- Linux and macOS runtime checks remain outstanding. The Windows run does not
  validate the Unix-only source comparison case.
- Preview tests use tiny gated backends with bounded waits. They verify ordering
  and identity, not scan throughput or smoothness at volume scale.
- Static rendering checks do not validate a visible tooltip popup or every live
  mouse gesture. The desktop's aspect-preserving camera still leaves margins in
  wide windows; this change does not redesign viewport layout.
- Unaccounted space includes metadata and allocation differences as well as
  unscanned or inaccessible entries. Visible hard links can overcount storage.
  Scanning observes a changing filesystem, not an atomic disk image.
- No BenchmarkDotNet measurement was run for this change. The earlier `Layout10K`
  result remains historical evidence and does not measure the new rendering or
  preview paths.
