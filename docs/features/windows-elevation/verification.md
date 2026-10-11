# Windows scan elevation verification

Reviewed on 2026-10-11. Muse Spark implemented the feature and review corrections. The supervisor independently reviewed the privilege and scan lifecycle behavior, ran verification, and corrected one test analyzer warning.

## Final checks

All execution used the existing bounded runner with hard limits of 512 MiB per process, 1 GiB per job, and 120 seconds. Build servers, shared compilation, and parallel MSBuild were disabled. No UAC prompt or elevated process ran during verification.

The Release build of all 11 projects passed with zero warnings and errors. Its job peaked at 197.4 MiB. The full solution tests passed 376 tests, with five explicit skips and no failures. They completed in 26.1 seconds and peaked at 126.9 MiB per job.

| Test project                | Passed | Skipped |
| --------------------------- | -----: | ------: |
| Treemap.Core.Tests          |    101 |       0 |
| Treemap.Host.WinForms.Tests |     39 |       0 |
| Treemap.Perf.Tests          |     19 |       0 |
| Treemap.Scanner.Tests       |    200 |       5 |
| Treemap.Skia.Tests          |     17 |       0 |

The skips are unchanged from the scanner feature. One requires Unix filename semantics; four require symbolic-link creation unavailable to this Windows account. The new Windows probe fixtures use explicit platform skips on other operating systems, rather than returning an untested pass.

Evidence is in [solution-build-final.log](../../../artifacts/verification/elevation/solution-build-final.log), [solution-tests-final.log](../../../artifacts/verification/elevation/solution-tests-final.log), and [FinalTestResults](../../../artifacts/verification/elevation/FinalTestResults). Every bounded log has a sibling `.json` file recording exit status, elapsed time, and peak memory. Artifacts are git-ignored.

## Independent real-directory check

The supervisor ran the production access probe against a tiny local NTFS directory. It returned `Denied` with Win32 code 5. Missing roots, file roots, UNC paths, and forced portable scans returned `NotApplicable`. A portable scan returned the expected one file and three logical bytes. The bounded smoke passed without MFT enumeration; its log is [probe-smoke.log](../../../artifacts/verification/elevation/probe-smoke.log).

## Review and behavior coverage

- The launcher passes the absolute selected root and the retry marker as separate arguments through [.NET ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0). Tests inspect the actual production launch settings through an injected process starter, including spaces, Unicode, and trailing volume separators.
- The original window closes only after a successful launch. UAC cancellation uses [Win32 error 1223](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-). Decline, launch exceptions, a missing executable, and a null process result retain the window and allow regular scanning with an explanation.
- Cancellation or closing while a probe is pending prevents a late launch or scan. Regression tests cover both a normal probe result and an I/O failure after cancellation or disposal, and wait for the operation to finish before asserting.
- Forced portable scans, available access, already elevated processes, and retry-marked processes do not relaunch. The retry marker prevents loops; it is not evidence of administrator privileges.
- Mapped network drives, UNC paths, non-NTFS filesystems, and invalid roots cannot trigger raw-volume elevation. The probe reuses the read-only native opener, captures the numeric error before cleanup, and disposes any opened handle without traversing the MFT.
- Trusted executable resolution supports the apphost, a companion executable when launched through `dotnet`, and single-file apphost paths. It avoids assembly-location lookup and never launches `dotnet` or a test host with administrator privileges.

The scanner library continues to fall back without requesting privileges. Only the desktop host requests UAC.

## Validation limits

Live UAC consent and elevated MFT traversal have not been verified in this session. Automated launch checks replace only the final process starter. The real-directory check verified the actual access-denied decision and portable scanning.

Elevating the app does not guarantee that every native record is supported. Unsupported metadata still causes portable fallback. Scans remain observations of a live filesystem.

Bounded desktop sessions use `--no-elevation`. A process started by the Windows elevation broker cannot be assumed to inherit the original runner's job limits. Normal desktop launches request elevation automatically when the selected NTFS scan needs it.
