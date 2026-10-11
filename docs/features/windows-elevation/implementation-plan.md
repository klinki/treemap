# Windows scan elevation

The desktop app will restart with administrator privileges when its selected NTFS scan needs raw volume access. The user chose restarting the app instead of an elevated helper. The scanner library will continue to fall back without prompting.

## Implementation

1. Add a small typed NTFS access check that reuses the read-only volume opener. Detect access denial by its Win32 code, never by localized message text. Skip unsupported paths and non-NTFS volumes, and dispose the probe handle without enumerating the MFT.
1. Before a desktop scan, check access on a worker thread. Request UAC only when NTFS access is denied and the current process is not elevated. Skip the check for a forced portable scan.
1. Relaunch the installed app executable directly with `runas`. Pass the absolute selected path as a separate argument and mark the retry to prevent restart loops. Start scanning only after the elevated window is shown.
1. Close the original window only after the launch succeeds. If UAC is declined or launching fails, retain the window and run the regular scanner with an explanation. Cancellation or closing during the access check must prevent a late launch.
1. Provide `--no-elevation` for sessions that must remain within an external process job. Use it in the documented bounded host command because a UAC restart can leave the original job.
1. Keep process launching and access checking injectable. Verify the branch decisions, argument handling, scan lifecycle, and existing scanner behavior without invoking UAC in automated tests.

## Verification and delivery

The supervisor will independently review the implementation, run proportionate Release checks under the existing hard process and job memory limits, record the results, and commit the reviewed changes locally. Automated launch tests will use stand-ins: a broker-created elevated process cannot be assumed to inherit the test runner's job limits. Live UAC consent and elevated MFT access will be reported separately if they have not been exercised.
