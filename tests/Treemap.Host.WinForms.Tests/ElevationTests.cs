using System.ComponentModel;
using System.Diagnostics;
using Treemap;
using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Elevation through a UAC restart: when raw NTFS access is denied the shell
/// relaunches its trusted executable elevated and the original closes without
/// scanning; otherwise it scans normally. All launches use stand-ins, so no test
/// touches UAC or a real volume. The production <c>StartElevated</c> start info is
/// verified through an injected process starter that never launches anything.
/// </summary>
public class ElevationTests
{
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    private static readonly ScanOptions PortableOnly = ScanOptions.Default with
    {
        Backend = ScanBackendPreference.Portable,
        ProgressInterval = TimeSpan.Zero,
    };

    private static readonly ScanOptions AutoVerbose = ScanOptions.Default with
    {
        Backend = ScanBackendPreference.Auto,
        ProgressInterval = TimeSpan.Zero,
    };

    // ---- launch parsing and trusted resolution (no window needed) ----

    [Fact]
    public void LaunchArgsParseRootAndFlagsInAnyOrder()
    {
        Assert.Equal((@"C:\data", false, false), Elevation.ParseLaunchArgs([@"C:\data"]));
        Assert.Equal((@"C:\data", true, false), Elevation.ParseLaunchArgs([@"C:\data", "--elevated-retry"]));
        Assert.Equal((@"C:\data", true, false), Elevation.ParseLaunchArgs(["--elevated-retry", @"C:\data"]));
        Assert.Equal((@"C:\data", false, true), Elevation.ParseLaunchArgs([@"C:\data", "--no-elevation"]));
        Assert.Equal((@"C:\data", true, true), Elevation.ParseLaunchArgs(["--no-elevation", @"C:\data", "--elevated-retry"]));
        Assert.Equal((null, false, false), Elevation.ParseLaunchArgs([]));
        Assert.Equal((null, true, false), Elevation.ParseLaunchArgs(["--elevated-retry"]));
    }

    [Fact]
    public void TrustedResolutionOnlyEverReturnsTheHostExe()
    {
        // Apphost running as the exe itself.
        Assert.Equal(
            @"C:\app\TreemapHost.exe",
            Elevation.ResolveTrustedExePath(
                @"C:\app\TreemapHost.exe",
                @"C:\app",
                _ => true));

        // Framework-dependent `dotnet TreemapHost.dll`: the companion exe in the app
        // directory is used.
        Assert.Equal(
            @"C:\app\TreemapHost.exe",
            Elevation.ResolveTrustedExePath(
                @"C:\dotnet\dotnet.exe",
                @"C:\app",
                path => string.Equals(path, @"C:\app\TreemapHost.exe", StringComparison.OrdinalIgnoreCase)));

        // Single-file publish: the running exe itself.
        Assert.Equal(
            @"C:\app\TreemapHost.exe",
            Elevation.ResolveTrustedExePath(
                @"C:\app\TreemapHost.exe",
                null,
                _ => true));

        // A test host is never launched.
        Assert.Null(Elevation.ResolveTrustedExePath(
            @"C:\tests\testhost.exe",
            @"C:\tests",
            path => !string.Equals(Path.GetFileName(path), "TreemapHost.exe", StringComparison.OrdinalIgnoreCase)));

        // Nothing trusted on disk.
        Assert.Null(Elevation.ResolveTrustedExePath(
            @"C:\dotnet\dotnet.exe",
            @"C:\app",
            _ => false));
    }

    // ---- production start info (no process is ever launched) ----

    [Fact]
    public void StartElevatedBuildsDirectRunasInfoWithoutLaunching()
    {
        List<ProcessStartInfo> seen = [];

        bool started = Elevation.StartElevated(
            @"C:\app\TreemapHost.exe",
            @"C:\Temp\Elev été\",
            Elevation.ElevatedRetryFlag,
            psi =>
            {
                seen.Add(psi);
                return new Process();
            });

        Assert.True(started);
        Assert.Single(seen);

        ProcessStartInfo psi = seen[0];
        Assert.Equal(@"C:\app\TreemapHost.exe", psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.Equal("runas", psi.Verb);
        Assert.Equal([@"C:\Temp\Elev été\", Elevation.ElevatedRetryFlag], psi.ArgumentList);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void StartElevatedKeepsAVolumeRootTrailingBackslash()
    {
        List<ProcessStartInfo> seen = [];

        bool started = Elevation.StartElevated(
            @"C:\app\TreemapHost.exe",
            @"C:\",
            Elevation.ElevatedRetryFlag,
            psi =>
            {
                seen.Add(psi);
                return new Process();
            });

        Assert.True(started);
        Assert.Single(seen);
        Assert.Equal(@"C:\", seen[0].ArgumentList[0]);
    }

    [Fact]
    public void StartElevatedReturningNullReportsNotStarted()
    {
        bool started = Elevation.StartElevated(
            @"C:\app\TreemapHost.exe",
            @"C:\data",
            Elevation.ElevatedRetryFlag,
            _ => null);

        Assert.False(started);
    }

    // ---- skipped elevation ----

    [Fact]
    public void NoElevationDisablesProbeAndLaunch() => StaRunner.Run(() =>
    {
        int probes = 0;
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            disableElevation: true,
            accessProbe: (_, _, _) =>
            {
                probes++;
                return Denied();
            },
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, probes);
        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void ForcedPortableNeverProbesOrLaunches() => StaRunner.Run(() =>
    {
        int probes = 0;
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: PortableOnly,
            accessProbe: (_, _, _) =>
            {
                probes++;
                return new NtfsAccessResult(NtfsRawAccess.NotApplicable, string.Empty, 0);
            },
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, probes);
        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void AvailableAccessScansWithoutLaunching() => StaRunner.Run(() =>
    {
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Available(),
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void AlreadyElevatedScansWithoutRelaunching() => StaRunner.Run(() =>
    {
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => true,
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void RetryMarkedShellNeverRelaunches() => StaRunner.Run(() =>
    {
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            isElevatedRetry: true,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            },
            trustedExeResolver: () => @"C:\app\TreemapHost.exe");

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    // ---- successful relaunch ----

    [Fact]
    public void DeniedAccessLaunchesOnceWithAbsoluteRootAndRetryFlagThenClosesWithoutScanning() => StaRunner.Run(() =>
    {
        const string Input = @"C:\Temp\Elev été";
        string expectedRoot = Path.GetFullPath(Input);
        List<(string exe, string root, string flag)> calls = [];
        int runs = 0;

        ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(Input, 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (exe, root, flag) =>
            {
                calls.Add((exe, root, flag));
                return true;
            });

        shell.StartScan(Input);
        StaRunner.PumpUntil(() => calls.Count == 1);

        Assert.Equal(@"C:\app\TreemapHost.exe", calls[0].exe);
        Assert.Equal(expectedRoot, calls[0].root);
        Assert.Equal("--elevated-retry", calls[0].flag);

        // The original closes and never scans: no publication, no runner call.
        StaRunner.PumpUntil(() => shell.IsDisposed || shell.Disposing);
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Single(calls);
        Assert.Equal(0, runs);
        Assert.Equal(0L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void DeniedAccessViaTheRealStarterNormalizesARelativeRootThenCloses() => StaRunner.Run(() =>
    {
        const string Input = @"Elev été";
        string expectedRoot = Path.GetFullPath(Input);
        List<ProcessStartInfo> seen = [];
        int runs = 0;

        ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(Input, 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (exe, root, flag) => Elevation.StartElevated(
                exe,
                root,
                flag,
                psi =>
                {
                    seen.Add(psi);
                    return new Process();
                }));

        shell.StartScan(Input);
        StaRunner.PumpUntil(() => seen.Count == 1);

        Assert.Equal(@"C:\app\TreemapHost.exe", seen[0].FileName);
        Assert.True(seen[0].UseShellExecute);
        Assert.Equal("runas", seen[0].Verb);
        Assert.Equal(expectedRoot, seen[0].ArgumentList[0]);
        Assert.Equal(Elevation.ElevatedRetryFlag, seen[0].ArgumentList[1]);
        Assert.Equal(string.Empty, seen[0].Arguments);

        StaRunner.PumpUntil(() => shell.IsDisposed || shell.Disposing);
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Single(seen);
        Assert.Equal(0, runs);
        Assert.Equal(0L, shell.Publisher.Current.Version);
    });

    // ---- decline and failure fall back ----

    [Fact]
    public void DeclinedElevationScansNormallyWithAnExplanation() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (_, _, _) => throw new Win32Exception(Elevation.UacDeclined));

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.Contains("declined", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.IsDisposed);
    });

    [Fact]
    public void FailedLaunchScansNormallyWithAnExplanation() => StaRunner.Run(() =>
    {
        int launches = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => null,
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                throw new InvalidOperationException("must not be called without an exe");
            });

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.Contains("unavailable", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.IsDisposed);
    });

    [Fact]
    public void RealStarterThrowingFallsBackToANormalScan() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (exe, root, flag) => Elevation.StartElevated(
                exe,
                root,
                flag,
                _ => throw new Win32Exception(2)));

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.Contains("failed", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.IsDisposed);
    });

    [Fact]
    public void RealStarterReturningNullFallsBackToANormalScan() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(
            scanRunner: (_, publisher, _, _, _) =>
            {
                publisher.Publish(Trees.WithMarker(42));
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) => Denied(),
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (exe, root, flag) => Elevation.StartElevated(
                exe,
                root,
                flag,
                _ => null));

        shell.StartScan(@"C:\data");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.Contains("failed to start", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.IsDisposed);
    });

    // ---- cancellation and disposal suppress a late launch ----

    [Fact]
    public void CancellingWhileTheProbeIsPendingSuppressesLaunchAndScan() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        int launches = 0;
        int runs = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, token) =>
            {
                entered.Set();
                Assert.True(token.WaitHandle.WaitOne(WorkerDeadline), "cancel never arrived");
                token.ThrowIfCancellationRequested();
                return Denied();
            },
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        Assert.True(entered.Wait(WorkerDeadline), "the probe never started");

        shell.CancelScan();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(0, runs);
        Assert.Contains("cancelled", shell.StatusText, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void ProbeFailureAfterCancelStillSuppressesLaunchAndScan() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int launches = 0;
        int runs = 0;

        using ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(WorkerDeadline), "the test never released the probe");
                throw new IOException("released after cancel");
            },
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        Assert.True(entered.Wait(WorkerDeadline), "the probe never started");

        shell.CancelScan();
        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(0, runs);
        Assert.Equal(0L, shell.Publisher.Current.Version);
        Assert.Contains("cancelled", shell.StatusText, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void DisposingWhileTheProbeIsPendingSuppressesLaunch() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int launches = 0;
        int runs = 0;

        ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(WorkerDeadline), "the test never released the probe");
                return Denied();
            },
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        Assert.True(entered.Wait(WorkerDeadline), "the probe never started");

        shell.Dispose();
        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(0, runs);
        Assert.Equal(0L, shell.Publisher.Current.Version);
    });

    [Fact]
    public void ProbeFailureAfterDisposeSuppressesLaunch() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int launches = 0;
        int runs = 0;

        ScanShell shell = CreateShell(
            scanRunner: (_, _, _, _, _) =>
            {
                runs++;
                return Task.FromResult(Result(@"C:\data", 42));
            },
            scanOptions: AutoVerbose,
            accessProbe: (_, _, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(WorkerDeadline), "the test never released the probe");
                throw new IOException("released after dispose");
            },
            elevationCheck: () => false,
            trustedExeResolver: () => @"C:\app\TreemapHost.exe",
            elevationLauncher: (_, _, _) =>
            {
                launches++;
                return true;
            });

        shell.StartScan(@"C:\data");
        Assert.True(entered.Wait(WorkerDeadline), "the probe never started");

        shell.Dispose();
        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, launches);
        Assert.Equal(0, runs);
        Assert.Equal(0L, shell.Publisher.Current.Version);
    });

    // ---- helpers ----

    private static NtfsAccessResult Denied() => new(NtfsRawAccess.Denied, @"C:\", 5);

    private static NtfsAccessResult Available() => new(NtfsRawAccess.Available, @"C:\", 0);

    private static ScanResult Result(string rootPath, int markerId) => new()
    {
        RootPath = rootPath,
        Root = Trees.WithMarker(markerId),
        Backend = ScanBackend.Portable,
        IsComplete = true,
        FileCount = 1,
        Duration = TimeSpan.FromMilliseconds(10),
    };

    private static ScanShell CreateShell(
        ScanRunner? scanRunner = null,
        ScanOptions? scanOptions = null,
        string? startupPath = null,
        bool isElevatedRetry = false,
        bool disableElevation = false,
        ScanAccessProbe? accessProbe = null,
        ElevationCheck? elevationCheck = null,
        ElevationStarter? elevationLauncher = null,
        TrustedExeResolver? trustedExeResolver = null)
    {
        ScanShell shell = new(
            scanRunner,
            scanOptions,
            startupPath,
            isElevatedRetry,
            disableElevation,
            accessProbe,
            elevationCheck,
            elevationLauncher,
            trustedExeResolver);
        _ = shell.Handle;

        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        return shell;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
