# Treemap review defects

Status: in-progress. The user authorized space-bunny to implement the reviewed fixes, the supervisor to review and verify them, and a local commit after verification.

Published treemap snapshots can change through aliased child collections, concurrent publishers can replace newer versions with older ones, and size aggregation repeats recursive work exponentially. Animation progress truncates, completion misses repaint, hit testing uses target geometry, and opacity sorting can paint parents over children. The host does not automatically react to publication and performs unnecessary layout and timer work. Verification omits perf projects and a background-task timeout does not stop runaway generation.

Expected behavior: immutable coherent snapshots, ordered publication, linear aggregation with distinct logical and physical totals, smooth animation with final repaint, interaction matching the painted frame, correct hierarchy draw order, UI updates after publication, and bounded reproducible verification.

Affected areas are the core model, publisher, animation/controller, Skia rendering, WinForms host, synthetic generator, solution, and verification tooling. The filesystem scanner remains subsequent feature work after these repaired contracts. No pushes or destructive operations are authorized. Preserve unrelated changes and use memory-capped process execution for every build, test, and benchmark run.

The previous review's small reproductions and source audit establish the defects. Its logs remain in ignored artifacts/supervisor. BenchmarkDotNet has no valid measurement at intake. Interactive host behavior needs appropriate local validation in addition to core and renderer tests.
