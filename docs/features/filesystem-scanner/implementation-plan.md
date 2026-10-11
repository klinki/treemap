# Filesystem scanner implementation plan

Build a portable .NET scanner that produces immutable treemap snapshots on a worker thread, plus a Windows NTFS MFT reader that automatically falls back to ordinary directory enumeration. Implementation is authorized by the user's scanner request; the supervisor reviews, verifies under hard resource caps, and creates a local Conventional Commit.

## Design

- Add a separate portable scanner project referencing Treemap.Core. Keep Windows native interop isolated and guarded by the operating system.
- Expose volume discovery and cancellable asynchronous scanning of a selected directory or volume. Return the selected backend, fallback reason, counts, bounded diagnostics, and completeness status.
- Stream ordinary directory enumeration with a small fixed number of workers. Do not follow directory reparse points or symbolic links. Report inaccessible or disappearing entries rather than aborting unrelated subtrees.
- Read NTFS MFT records through read-only volume handles and FSCTL_GET_NTFS_FILE_RECORD. Follow the returned ordinal downward so vacant records cannot cause a loop. Validate native output and record bounds, update sequence fixups, parent references, and supported attributes.
- Derive file sizes from the unnamed DATA attribute rather than stale FILE_NAME cached sizes. Resolve hard-link names explicitly. Unsupported metadata must produce a documented fallback or ordinary metadata query, never invented sizes.
- Bound entry count, directory depth, and retained diagnostics. Cancellation must stop work without triggering fallback or publishing a successful result. Publish complete immutable trees from background work; progress reports need not rebuild the entire tree.
- Add a WinForms scan shell with directory or volume selection, progress, cancellation, and an explicit backend/fallback status. Start scanning only after a user selects a root or supplies an argument.

## Delivery

1. Delegate scanner implementation with disjoint ownership where useful. Space-bunny handled the initial implementation; Muse Spark took over the remaining corrections at the user's request.
1. Independently review platform behavior, tree identity, error handling, native parsing, cancellation, fallback, and host lifecycle.
1. Run fixture-driven scanner tests and all existing regression tests using the bounded runner. Exercise a small real directory scan and the available Windows backend without scanning an entire live volume during verification.
1. Update README usage and verification evidence. Record any native or cross-platform validation limits honestly.
1. Stage only reviewed files, create a Conventional Commit, and never push.

## Reference

FileMover is at C:/ai-workspace/FileMover. It is read-only inspiration, not a dependency or a source to copy wholesale. Its MFT approach uses read-only volume queries and downward record enumeration. Its FILE_NAME size shortcut should not be carried over.

Microsoft documents downward enumeration in [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record), geometry in [NTFS_VOLUME_DATA_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer), and the supported native record layout in [ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header).
