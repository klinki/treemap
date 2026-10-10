# Fix attempt 001

Status: in-progress. Goal: repair the supervisor review findings, add meaningful regressions, verify under enforced resource limits, then commit locally as authorized.

This is the first implementation attempt after the source review. Space-bunny owns source and test edits in bounded batches. The supervisor owns documentation, review, verification execution, corrections, and the commit.

Planned sequence:

1. Freeze child collections, aggregate sizes once, preserve unchanged nodes, and serialize publication order. Add focused model and publisher regressions.
1. Correct clock arithmetic, completion repaint, visible-frame hit testing, hierarchy ordering, and explicit hover ancestry. Move reusable ordering work out of frame evaluation where practical.
1. Notify the host after publication, coalesce UI relayout, remove resize relayout and idle ticks, make synthetic IDs local, and provide process-bounded verification tooling and complete solution membership.
1. Review all diffs, run the complete Release test suite and build under hard limits, obtain a narrow benchmark measurement if its setup is sound, update the audit record, and stage only reviewed task files for a Conventional Commit.

Risks include transition endpoint rendering, topology changes during tweening, UI thread dispatch and disposal races, and benchmark job configuration. New dependencies and broad API changes should be avoided. Regression coverage should exercise observable failures with small inputs and deterministic clocks where possible.

Verification will use kernel process and job commit limits with termination, build servers disabled, and explicit deadlines. Existing tests, new focused regressions, whole-project compilation, and targeted host behavior checks are required. Test durations must not be presented as benchmark measurements.

Implementation summary: pending delegated model/publication batch.

Test results: baseline green; no post-change checks yet.

Outcome: pending. Remaining gaps: remaining implementation batches and supervisor verification.

## Snapshot review

Space-bunny completed the model batch without executing processes. The supervisor verified the private child-copy wrappers, single-pass aggregation and locked publication store. Review caught three invalid implicit assignments from IReadOnlyList to IList in the new tests; these now obtain the interface through Assert.IsAssignableFrom. Host publication/lifecycle repairs are the next delegated batch. Verification remains pending.

## Host and tooling review

The supervisor accepted the kernel runner copy and verified its smoke command, timeout termination and environment restoration. The new Windows test project restored within the caps and is included in the solution. Host review corrected a protected handle-method call, excluded zero-alpha departed cells from visible-state assertions, and replaced a timing-dependent disposal race with a captured-notification barrier. The subscriber-progress test now blocks only the first notification and expects two published versions. Benchmark CLI review corrected nullable environment handling and strips --full from list invocations. Full compilation and post-change tests remain pending.

## Verification result and correction round

The first complete Release run compiled and executed all four test projects: 144/146 passed, with 160.4 MiB peak committed job memory. Two new Core regressions failed. One exposed a negative-duration constructor bypassing the setter clamp; the other searched for a point not covered by the root, which cannot exist. A fresh delegated space-bunny correction round fixed the constructor and replaced the impossible search with explicit overlap probes. Supervisor review also put invisible cells after the visible file in the opacity fixture so it detects a missing alpha cutoff.

Final verification on 2026-10-11: Core 101/101, WinForms 10/10, generator/perf 19/19, Skia 17/17. Total 147/147 passed, zero skipped, 8.20 seconds for the bounded command, 157.8 MiB peak job memory. All nine projects built in Release with zero warnings/errors in 3.10 seconds, 120.6 MiB peak job memory. Runner smoke and timeout checks passed; caller environment was restored. The unchanged native helper had already passed a hard memory-termination test during baseline review.

BenchmarkDotNet listing works for bare --list and --full --list tree. One actual Layout10K ShortRun job completed, with three measured workload iterations and child exit code zero. Mean 1.177 ms; standard deviation 0.0277 ms; 1.91 MB allocated per operation. The 99.9% confidence interval is 0.671–1.682 ms, so this first measurement should not be treated as a precise comparison baseline. The command took 16.22 seconds and the whole job peaked at 280.1 MiB. No second default job appeared. This case generates 10,000 files plus folders/free space; the remaining benchmark cases were not measured.

Evidence is in ignored artifacts/verification: tests-final.log and FinalTestResults/*.trx, build-final.log, benchmark.log, sibling resource JSON files, BenchmarkDotNet/results, and runner validation logs. Hidden controls exercised real message dispatch and Skia/GDI painting; no visible user walkthrough is claimed.

The reviewed defects are locally verified. Remaining work is the filesystem scanner feature, continuity for interrupted tweens, and arbitrary retained ancestry reversals choosing the target hierarchy. These are recorded in README. The user authorized the local commit after supervisor acceptance; no push is planned. Bug-record closure awaits user confirmation and is separate from the authorized commit.
