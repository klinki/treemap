namespace Treemap.Scanning.Windows;

/// <summary>
/// What a read-only raw-volume probe found. Numeric, not textual: callers match on
/// <c>Win32Error</c>, never on a localized message.
/// </summary>
internal enum NtfsRawAccess
{
    /// <summary>The path cannot use raw MFT access, so elevation would be pointless.</summary>
    NotApplicable,

    /// <summary>The volume opened read-only; no elevation needed.</summary>
    Available,

    /// <summary>The volume refused with Win32 access-denied; elevation may help.</summary>
    Denied,

    /// <summary>The volume could not be probed for another reason; scan normally.</summary>
    Unavailable,
}

/// <summary>The outcome of one probe: what was found, for which volume, with which code.</summary>
internal readonly record struct NtfsAccessResult(
    NtfsRawAccess Access,
    string VolumeRoot,
    int Win32Error);

/// <summary>
/// A tiny read-only check for whether the selected root needs raw NTFS access the
/// current process lacks. The library never requests privileges; it only reports what
/// the probe found so a host can decide.
/// </summary>
/// <remarks>
/// The probe reuses <c>WindowsMftRecordSource.TryOpen</c> and disposes the
/// handle without enumerating the MFT. Only Win32 access-denied counts as denied;
/// every other failure is an ordinary fallback, matched numerically.
/// </remarks>
internal static class NtfsAccessProbe
{
    /// <summary>Win32 ERROR_ACCESS_DENIED.</summary>
    internal const int ErrorAccessDenied = 5;

    /// <summary>Probes <paramref name="rootPath"/> with the production filesystem.</summary>
    internal static NtfsAccessResult Check(
        string? rootPath,
        ScanOptions? options,
        CancellationToken cancellationToken)
        => Check(rootPath, options, cancellationToken, null, null, null, null);

    /// <summary>
    /// Test seam: substitutes the platform, attribute, volume and volume-open answers so
    /// no test opens a real volume. A null delegate means the real one.
    /// </summary>
    internal static NtfsAccessResult Check(
        string? rootPath,
        ScanOptions? options,
        CancellationToken cancellationToken,
        Func<bool>? isWindows,
        Func<string, FileAttributes?>? getAttributes,
        Func<string, (DriveType driveType, string? format)?>? getVolumeInfo,
        Func<string, (bool opened, int win32Error)>? tryOpenVolume)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool windows = isWindows is null ? OperatingSystem.IsWindows() : isWindows();
        if (!windows)
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (options?.Backend == ScanBackendPreference.Portable)
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        string full;
        try
        {
            full = ScanPath.Normalize(rootPath);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (!NtfsPath.TrySplit(full, out string volumeRoot, out _))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        FileAttributes? attributes;
        try
        {
            attributes = getAttributes is null ? File.GetAttributes(full) : getAttributes(full);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (attributes is null
            || (attributes.Value & FileAttributes.Directory) == 0
            || (attributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        (DriveType driveType, string? format)? volume;
        try
        {
            volume = getVolumeInfo is null ? DefaultGetVolumeInfo(volumeRoot) : getVolumeInfo(volumeRoot);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (volume is null)
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (volume.Value.driveType == DriveType.Network)
        {
            // A mapped network letter names a file server, not the local volume behind
            // it; there is no local raw volume to open.
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        if (!string.Equals(volume.Value.format, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool opened;
        int error;
        try
        {
            (opened, error) = tryOpenVolume is null ? DefaultTryOpen(volumeRoot) : tryOpenVolume(volumeRoot);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return new NtfsAccessResult(NtfsRawAccess.Unavailable, volumeRoot, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (opened)
        {
            return new NtfsAccessResult(NtfsRawAccess.Available, volumeRoot, 0);
        }

        return error == ErrorAccessDenied
            ? new NtfsAccessResult(NtfsRawAccess.Denied, volumeRoot, error)
            : new NtfsAccessResult(NtfsRawAccess.Unavailable, volumeRoot, error);
    }

    private static (DriveType driveType, string? format)? DefaultGetVolumeInfo(string volumeRoot)
    {
        DriveInfo drive = new(volumeRoot);
        return (drive.DriveType, drive.DriveFormat);
    }

    private static (bool opened, int win32Error) DefaultTryOpen(string volumeRoot)
    {
        bool ok = WindowsMftRecordSource.TryOpen(volumeRoot, out WindowsMftRecordSource? source, out _, out int error);
        if (ok && source is not null)
        {
            source.Dispose();
            return (true, 0);
        }

        source?.Dispose();
        return (false, error);
    }
}
