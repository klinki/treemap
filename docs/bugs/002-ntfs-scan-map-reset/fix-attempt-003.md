# Fix attempt 003

## Attempt status

Investigation and implementation in progress.

## Goal

Show exactly one descendant generation at basic detail 1 and complete full-volume
enumeration within the existing process and managed-heap caps. Preserve useful
previews, filtering, navigation, cancellation, stable identity, and honest failures.

## Relation to previous attempts

Attempt 002 prevents the reset and crash by stopping early. The user confirmed those
symptoms were gone, then reported that the limited scan still does not meet the
full-scan requirement. Its conservative accounting charges every possible parent
for every entry, even when siblings share those parents. A separate default
500,000-entry cap would also truncate larger volumes.

## Investigation

The screenshot has Less detail disabled but shows Windows, Installer, and files
within Installer. The host constructs layout options with depth 1; the real
layout and animation pipeline need inspection. NTFS rejects the first returned
record before discovery. The source uses FSCTL_GET_NTFS_FILE_RECORD, so the parser
must distinguish raw on-disk fixup bytes from the kernel-returned representation.

## Proposed change

Repair depth handling with regressions through the real layout path. Correct NTFS
record representation handling without weakening raw-record corruption checks.
Replace retention and repeated reconstruction of every full file path with compact
or disk-backed scan storage so enumeration can finish under the unchanged caps.
Keep explicit caller limits and truthful inaccessible-entry diagnostics.

## Risks

Disk-backed storage needs immutable preview versions, bounded caches, stable IDs,
and file cleanup after readers release a snapshot. Changes must preserve existing
model contracts and avoid silently discarding entries. Kernel-returned records must
have an explicit source representation; mixed or malformed raw records must fail.

## Verification plan

Run serial bounded tests for affected projects, including detail changes, snapshot
publication, large scans, filtering, fallback, identity, and cancellation. Capture
the live C: result under 512 MiB per process, 1 GiB per job, and 384 MiB managed heap.
Require enumeration to finish rather than stop at the estimate or default entry
cap. Document any genuinely unreadable entries separately from resource truncation.

## Implementation and outcome

Pending. Muse Spark 1.3 Free failed before edits with a rate-limit response. The user
selected non-free Muse Spark 1.3 Contributor for implementation; the supervisor
owns reviews, process execution, verification, and the local commit.

### NTFS source verification

The read-only elevated probe captured three high-ordinal records and the volume
root on C:. In every record, the two unit tails matched the saved words in the
update sequence array and did not match its tag. Record 4049797 had tag 0x0011 and
tails 0x0004 and 0x0000. This confirms that the native source returns fixup-applied
bytes and that applying raw-record validation again was a false rejection.

Muse added an explicit source representation and retained strict raw-record
checks. The supervisor's bounded focused parser suite passed all 67 tests. Evidence
is in the ignored verification artifacts named attempt003-record-probe and
attempt003-ntfs-parser-tests. The diagnostic build had zero warnings and errors.

### Fresh-scan clarification

The user confirmed Windows was already expanded immediately after scanning, before
clicking in the map. The root-click temporary-expansion defect found by the delegate
is a separate confirmed defect; it does not establish the cause of this fresh-scan
report. A further review and live frame-depth diagnostics are required.

### Remaining native-backend limitation

A bounded read-only probe passed the corrected representation check and reached an
attribute-list record after 64 records. The current native backend deliberately
falls back when attributes span extension records. The representation repair alone
therefore does not make this C: volume eligible for a complete native scan. The
portable fallback must finish enumeration under the same memory limits.

### Supervisor review checkpoints

The existing core suite passed 173 tests under the unchanged process caps. Two new
projection contract checks both reproduced draft defects: a five-node filtered tree
reports eight nodes, and mutating the caller's overlay list changes a published lazy
root. Further scanner checks cover metadata immutability across preview versions and
cache isolation between old and new views. These are review failures, not accepted
behavior; the storage implementation remains uncommitted.

The parser-only repair is committed locally as 51bd1f7. Its focused 67-test run and
live record probes are independent of the unfinished storage rewrite.

### Revised user budget and deferred experiment

The user corrected the SpaceSniffer comparison and authorized 2 GB of total app
memory for their C: scan. This replaces the earlier 512 MiB constraint for this
case. Verification will cap process and job at 1,907 MiB, rounded below two billion
bytes, with the existing 90 percent hard-kill tripwire and managed-heap allowance.
Smaller test processes retain their original default limits.

At the user's request, the unfinished disk-storage rewrite is preserved locally
on branch experiment/disk-backed-scanner, commit 19da19a. It includes its draft
code, tests, and documented unresolved review defects. The supervisor stopped that
delegate and returned to main; the experiment is not part of the current repair.
The NTFS parser commit and the independent depth changes remain on main.

The current implementation will keep managed storage, reduce redundant full-path
retention and full-tree copying where needed, correct memory estimates, and verify
complete C: enumeration against the revised total process budget. Non-free Muse
Spark Contributor owns implementation, while the supervisor owns execution and
review. Earlier disk-storage plans and failures above are preserved as history.

### Depth verification on main

The supervisor strengthened the live-preview regression with gates between worker
publications, so each preview reaches the actual rendered frame before the next
one arrives. Core passed 173 tests and desktop passed 86 tests, including the
fresh zero-click preview and stale-click checks. The desktop run peaked at
111.54 MiB per process and 205.86 MiB for its job, using the original test limits.
The detail repair was committed separately after those checks. These automated
results do not establish the cause of the user's persistent fresh-scan screenshot;
actual C: frame-depth verification and the user's retest remain required.

### Managed-storage review round 1

Muse replaced production full-path retention with a managed directory tree and
shared immutable file nodes, and combined snapshot node/parent lookups. The first
bounded scanner check failed compilation on an unresolved XML documentation
reference in ManagedScanTree. The supervisor also found lost folder timestamps
and no tests for the new production path. A second Contributor round must correct
these and cover preview immutability, identity transactions, fallback, metadata,
and memory accounting. No complete C: scan or memory claim is accepted yet.

Preview construction still visits file references and rebuilds the snapshot index.
Sharing file objects removes repeated object creation but does not make preview
cost depend on folders alone. The automatic budget is half the remaining managed
heap, capped at 1.2 GB; with the bounded runner's heap limit it is approximately
750 MB. Actual peak process and job memory, including the real desktop shell,
will decide whether the revised implementation meets the user's 2 GB constraint.

### Managed-storage review round 2

On 2026-10-12, the bounded scanner suite compiled and ran 311 tests: 299 passed,
six failed, and six were skipped. Peak process memory was 132.95 MiB and job memory
213.79 MiB; the job completed normally. The failures expose a parent-traversal
error: explicit folder entries create a same-named child beneath their own folder.
This loses the intended timestamp, makes empty folders nonempty, and prevents a
folder entry from replacing a colliding file. Parent traversal must exclude the
leaf, and budget accounting must not charge the same new folder twice. A third
Contributor round is correcting these with exact-shape regressions. Full-drive
verification remains pending.

### Independent model and renderer checks

After the round 2 storage edits, Core passed all 173 tests and Skia passed all 25
under the original small-process caps. These checks cover the combined snapshot
lookup and renderer contracts. Scanner corrections and desktop integration checks
remain pending; these passes do not establish a complete full-drive result.

### Elevated C: measurement after round 3

The affected suites passed: Core 173, Skia 25, scanner 306 (six environment skips),
and desktop 86. The supervisor repaired a test-name typo and strengthened the
identity-pruning check to compare the original accepted child's actual ID with
its restored ID, rather than an ordering assertion that could pass with reuse.

A fresh elevated C: scan used the real ScanShell and both real backends, under the
1,907 MiB process/job caps and 600-second deadline. NTFS correctly declined
fragmented attributes; portable enumeration ran 99.5 seconds and discovered
1,272,675 files and 357,527 folders. It stopped at the approximately 750 MB
estimated allowance, leaving 249.12 GB unaccounted. The process exited normally,
with no reset or crash, but this is still a resource-truncated result, not a full
scan. Peak kernel process memory was 1,433.91 MiB and job memory 1,435.11 MiB.
After final publication, managed memory fell to 469.4 MB on collection.

Every recorded UI sample had detail 1, no temporary expansion, and frame depth 1.
This validates the fresh-click-free detail behavior for this instrumented C: run.
The fourth Contributor round will revise the conservative allowance from these
measurements and reduce final construction overlap before another bounded C: run.
Evidence: ignored attempt003-managed-full-c.log and its job result JSON.

### Managed-storage review round 4

The scanner now reserves three-quarters of the remaining managed heap, still capped
at 1.2 GB and respecting smaller explicit limits. Once enumeration has joined and
previews have drained, it captures file references and releases the mutable working
tree before constructing final folders and the snapshot index. Prior published
snapshots, counters, and pending identities remain intact. The scanner suite passed
307 tests with six environment skips; desktop passed 86. Core 173 and Skia 25 remain
valid because their code has not changed. The supervisor removed speculative peak
multipliers from production comments; only actual process measurements will support
a completion claim. A second elevated C: run is in progress under the same caps.

### Round 4 C: outcome and independent inventory count

The second bounded elevated scan retained 2,528,619 entries (2,034,831 files and
493,788 folders) in 145.7 seconds, then stopped at the revised memory estimate.
It did not crash or reset. Peak process memory was 1,445.90 MiB and job memory
1,447.09 MiB; every UI sample remained at detail 1 without temporary expansion.
The full-scan requirement is still unmet.

To stop guessing the remaining inventory, the supervisor ran a separate streaming
count with hidden/system entries included and reparse points recorded but not
followed. It retained no model or file-path list. C: had 3,542,603 candidate entries:
2,927,443 files, 615,160 folders, and 27,842 links. Twenty-five directory/attribute
errors occurred and no depth limit was reached. The count finished in 50.5 seconds
at 56.07 MiB peak process memory and 57.27 MiB for its job. This is an upper bound
on readable scanner entries because it does not perform file-size metadata probes.

Applying current basename charges to that inventory requires 1,522,034,360 estimated
bytes before diagnostics, exceeding both the 1.2 GB cap and the roughly 1.124 GB
allowance. The fifth Contributor round will correct accounting for shared storage
and reduce preview overlap against this measured inventory. Evidence remains in
ignored attempt003-managed-full-c-r4 and attempt003-inventory-count logs and JSON.

### Round 5 reached enumeration end but failed finalization

Scanner tests passed 314 with six environment skips after the supervisor removed a
formula-only test and tightened the NTFS fixture's partial-result budget to 3,500
bytes. Accounting now uses 256 bytes per file and 512 per folder plus basename
bytes, with 85 percent of remaining heap capped at 1.3 GB. Large previews use a
2-second floor from one million entries and 3 seconds from two million.

C: enumeration reached all 3,542,696 entries (2,927,494 files, 615,202 folders) in
161.1 seconds, matching the independent candidate count within live changes.
There was no memory-estimate stop. However, optional preview index construction
threw OutOfMemoryException before its snapshot became visible. The loop recorded
that as a fatal scan error; finalization rethrew it before releasing the working
tree and restored the initial empty map. This run failed the required behavior
and is not accepted despite normal process exit and no job-limit termination.
Peak process memory was 1,439.20 MiB and job memory 1,440.39 MiB.

The sixth Contributor round must preserve the last visible map when optional
pre-publication preview allocation fails, continue enumeration, and reach the
final build after releasing working storage. Subscriber exceptions after visible
publication must still propagate and restore the prior accepted map. Final
allocation failures must remain honest. Deterministic small tests will cover the
distinction, cancellation, and final data preservation.

### Managed-storage review round 6

Optional preview allocation failures before visible publication now pause previews
and preserve the last visible map while enumeration continues. A subscriber's
OutOfMemoryException after publication still fails the scan, restores its prior
accepted source, and aborts identities. Final construction directly consumes the
working tree and releases dictionaries as each immutable folder is built, avoiding
the full preview-copy graph at finalization. Final allocation failures still
propagate honestly.

The scanner suite passed 317 tests with six environment skips, including three
small deterministic tests for allocation-only recovery, subscriber failure, and
cancellation after a paused preview. The supervisor moved version capture under
the preview gate, covered initial basis construction with the same recovery rule,
and replaced the obsolete release helper's test with actual final-freeze checks.
The focused review suite passed 61 tests. Desktop and elevated C: verification
are next; no full-map success is claimed yet.

### Round 6 final lookup allocation failure

Desktop passed 86 tests and the focused review checks passed 61 after scanner's
317 passes and six skips. The elevated C: run completed enumeration of 3,542,527
entries and reached final construction. Preview allocation recovery no longer
blocked that path. Final snapshot lookup construction nevertheless threw
OutOfMemoryException before publication and restored the empty prior map. This
run also failed the required behavior. Peak process memory was 1,445.09 MiB and
job memory 1,446.28 MiB; neither hard cap nor deadline killed the process.

The seventh Contributor round targets the large contiguous node lookup allocation.
A compact paged lookup for densely assigned IDs can reduce per-node overhead and
avoid a single array for millions of records, while keeping a sparse fallback for
arbitrary IDs and preserving the public snapshot contracts. The proposed savings
remain unverified until another complete final publication succeeds. Evidence:
ignored attempt003-managed-full-c-r6.log and job result JSON.

### Managed-storage review round 7

Contributor replaced the large contiguous snapshot lookup with 4,096-ID pages.
Node references and parent IDs occupy separate arrays below the large-object
heap threshold. Sparse IDs retain a dictionary fallback. The supervisor raised
the paging threshold to half-page average occupancy so sparse IDs do not waste
memory, fixed parent lookup for negative IDs, and strengthened the negative,
duplicate-ID, and snapshot-independence fixtures to exercise actual paging.

Core passed 182 tests, including nine paged lookup cases. The scanner passed 317
tests with six skips, desktop passed 86, and Skia passed 25. All runs completed
within their existing process caps. An elevated C: run now checks final publication
under the unchanged 1,907 MiB process and job caps. No full-map success is claimed
until that run finishes and its scan result, displayed tree, and guard JSON agree.

### Round 7 elevated C: verification — 2026-10-12

The elevated run finished the portable walk and published its final map. It
recorded 3,542,813 entries: 2,927,511 files and 615,302 folders. The final snapshot
contained 3,542,816 nodes including the root, free space, and unaccounted space.
The desktop displayed that same final count. Every recorded frame remained at
detail 1 with no temporary expansion and maximum frame depth 1. There was no
scan fault, allocation exception, resource-budget stop, or map reset.

Scan time was 179.1 seconds; the entire diagnostic process completed in 184.33
seconds with exit 0. Peak process memory was 1,446.76 MiB and peak job memory
1,447.96 MiB (1,518,292,992 bytes), below the user's 2 GB total-app constraint.
The unchanged hard process/job caps were 1,907 MiB, with a 90-percent kill
tripwire, managed heap limit 1,499,725,824 bytes, and a 600-second deadline.

NTFS declined an entry with attributes split across fragment records before
discovery; the portable fallback enumerated the full accessible inventory.
`IsComplete` honestly remained false because protected paths such as System
Volume Information could not be read. No count, memory, or deadline limit
truncated enumeration. The final unaccounted residual was 27,414,287,367 bytes;
it can include protected data, metadata, and allocation differences.

Evidence is in ignored attempt003-managed-full-c-r7.log and its guard JSON.
Core 182, scanner 317, desktop 86, and Skia 25 passed (610 total, six scanner
skips). The disk-backed draft remains separate at experiment/disk-backed-scanner
commit 19da19a. Attempt 003 is awaiting the user's visible-window confirmation.

### User retest rejected — 2026-10-12

After commit 77ef2db, the actual visible TreemapHost app auto-scanned C: under the
same 1,907 MiB process/job caps. The user reported "The scan still stops or resets"
and clarified "Map reset to unscanned". Its console log is empty, and the app
remains running at approximately 1.44 GB private memory. This is a failed visible
confirmation. The successful minimized round 7 diagnostic remains evidence for
that harness only. Attempt 004 will capture the visible failure before proposing
a further production change.
