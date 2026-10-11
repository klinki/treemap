using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The raw-volume probe: when a root can use MFT access, whether it is denied by its
/// numeric Win32 code, and when elevation would be pointless. No test opens a real
/// volume; the filesystem answers are injected. Tests that shape Windows drive paths
/// are Windows-only: drive-letter normalization follows the host platform.
/// </summary>
public class NtfsAccessProbeTests
{
    private static readonly ScanOptions Auto = ScanOptions.Default with { Backend = ScanBackendPreference.Auto };

    [WindowsOnlyFact]
    public void AccessDeniedByNumericCodeIsDenied()
    {
        NtfsAccessResult result = Probe(@"C:\sub", opened: false, error: 5);

        Assert.Equal(NtfsRawAccess.Denied, result.Access);
        Assert.Equal(@"C:\", result.VolumeRoot);
        Assert.Equal(5, result.Win32Error);
    }

    [WindowsOnlyFact]
    public void AnotherWin32FailureIsUnavailableRatherThanDenied()
    {
        NtfsAccessResult result = Probe(@"C:\sub", opened: false, error: 2);

        Assert.Equal(NtfsRawAccess.Unavailable, result.Access);
        Assert.Equal(2, result.Win32Error);
    }

    [WindowsOnlyFact]
    public void AnOpenedVolumeIsAvailable()
    {
        NtfsAccessResult result = Probe(@"C:\sub", opened: true, error: 0);

        Assert.Equal(NtfsRawAccess.Available, result.Access);
        Assert.Equal(0, result.Win32Error);
    }

    [Theory]
    [InlineData(@"\\server\share\folder")]
    [InlineData("")]
    [InlineData(" ")]
    public void NonDrivePathsAreNotApplicableWithoutTouchingAVolume(string path)
    {
        bool touched = false;

        NtfsAccessResult result = NtfsAccessProbe.Check(
            path,
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => throw new InvalidOperationException("must not stat"),
            getVolumeInfo: _ => throw new InvalidOperationException("must not query the volume"),
            tryOpenVolume: _ =>
            {
                touched = true;
                return (false, 5);
            });

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
        Assert.False(touched);
    }

    [WindowsOnlyFact]
    public void RelativePathsNormalizeToTheLocalDriveAndCanNeedElevation()
    {
        string expectedRoot = Path.GetPathRoot(Path.GetFullPath(@"relative-sub"))!;

        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"relative-sub",
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => FileAttributes.Directory,
            getVolumeInfo: _ => (DriveType.Fixed, "NTFS"),
            tryOpenVolume: root =>
            {
                Assert.Equal(expectedRoot, root);
                return (false, 5);
            });

        Assert.Equal(NtfsRawAccess.Denied, result.Access);
        Assert.Equal(expectedRoot, result.VolumeRoot);
    }

    [WindowsOnlyFact]
    public void NonNtfsVolumesAreNotApplicable()
    {
        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => FileAttributes.Directory,
            getVolumeInfo: _ => (DriveType.Fixed, "FAT32"),
            tryOpenVolume: _ => throw new InvalidOperationException("must not open a non-NTFS volume"));

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
    }

    [WindowsOnlyFact]
    public void MappedNetworkLettersAreNotApplicableWithoutOpening()
    {
        bool opened = false;

        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => FileAttributes.Directory,
            getVolumeInfo: _ => (DriveType.Network, "NTFS"),
            tryOpenVolume: _ =>
            {
                opened = true;
                return (false, 5);
            });

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
        Assert.False(opened);
    }

    [WindowsOnlyTheory]
    [InlineData(null)]
    [InlineData(FileAttributes.Normal)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    public void MissingNonDirectoryOrLinkRootsAreNotApplicable(FileAttributes? attributes)
    {
        bool opened = false;

        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => attributes,
            getVolumeInfo: _ => (DriveType.Fixed, "NTFS"),
            tryOpenVolume: _ =>
            {
                opened = true;
                return (false, 5);
            });

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
        Assert.False(opened);
    }

    [Fact]
    public void PortablePreferenceIsNotApplicable()
    {
        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"C:\sub",
            ScanOptions.Default with { Backend = ScanBackendPreference.Portable },
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => throw new InvalidOperationException("must not stat"),
            getVolumeInfo: _ => throw new InvalidOperationException("must not query the volume"),
            tryOpenVolume: _ => throw new InvalidOperationException("must not open"));

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
    }

    [Fact]
    public void NonWindowsHostsAreNotApplicable()
    {
        NtfsAccessResult result = NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            CancellationToken.None,
            isWindows: () => false,
            getAttributes: _ => throw new InvalidOperationException("must not stat"),
            getVolumeInfo: _ => throw new InvalidOperationException("must not query the volume"),
            tryOpenVolume: _ => throw new InvalidOperationException("must not open"));

        Assert.Equal(NtfsRawAccess.NotApplicable, result.Access);
    }

    [Fact]
    public void CancellationBeforeTheProbeThrows()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            cts.Token,
            isWindows: () => throw new InvalidOperationException("must not reach the platform check"),
            getAttributes: _ => throw new InvalidOperationException("must not stat"),
            getVolumeInfo: _ => throw new InvalidOperationException("must not query the volume"),
            tryOpenVolume: _ => throw new InvalidOperationException("must not open")));
    }

    [WindowsOnlyFact]
    public void CancellationAfterTheProbeThrowsRatherThanReporting()
    {
        using CancellationTokenSource cts = new();

        Assert.ThrowsAny<OperationCanceledException>(() => NtfsAccessProbe.Check(
            @"C:\sub",
            Auto,
            cts.Token,
            isWindows: () => true,
            getAttributes: _ => FileAttributes.Directory,
            getVolumeInfo: _ => (DriveType.Fixed, "NTFS"),
            tryOpenVolume: _ =>
            {
                cts.Cancel();
                return (true, 0);
            }));
    }

    private static NtfsAccessResult Probe(string root, bool opened, int error)
        => NtfsAccessProbe.Check(
            root,
            Auto,
            CancellationToken.None,
            isWindows: () => true,
            getAttributes: _ => FileAttributes.Directory,
            getVolumeInfo: _ => (DriveType.Fixed, "NTFS"),
            tryOpenVolume: _ => (opened, error));
}
