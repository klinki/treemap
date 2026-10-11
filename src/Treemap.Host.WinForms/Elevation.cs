using System.Diagnostics;
using System.Security.Principal;
using Treemap.Scanning;
using Treemap.Scanning.Windows;

namespace Treemap.Host.WinForms;

/// <summary>Probes raw NTFS access for one root. Production reuses the scanner probe.</summary>
internal delegate NtfsAccessResult ScanAccessProbe(
    string rootPath,
    ScanOptions options,
    CancellationToken cancellationToken);

/// <summary>Answers whether the current process is elevated.</summary>
internal delegate bool ElevationCheck();

/// <summary>
/// Starts the trusted executable elevated. Returns true only when the new process
/// started; false means the starter saw a null process. Throws on launch failure.
/// </summary>
internal delegate bool ElevationStarter(string exePath, string rootAbsolutePath, string retryFlag);

/// <summary>Resolves the trusted executable to relaunch elevated, or null.</summary>
internal delegate string? TrustedExeResolver();

/// <summary>
/// Elevation through a UAC restart of the app itself. The scanner library never
/// requests privileges; this host directly relaunches its own executable via
/// ShellExecute with <c>runas</c> when raw NTFS access is denied. No command shell,
/// no script, no string-built command line: the root and the retry flag travel as
/// separate arguments.
/// </summary>
internal static class Elevation
{
    /// <summary>
    /// One-shot marker telling the new process it is the elevated retry, so it never
    /// relaunches in a loop. It is not proof of elevation: anyone can pass it.
    /// </summary>
    internal const string ElevatedRetryFlag = "--elevated-retry";

    /// <summary>Opt-out for bounded sessions that must remain within an external process job.</summary>
    internal const string NoElevationFlag = "--no-elevation";

    /// <summary>Win32 ERROR_CANCELLED: the UAC prompt was declined.</summary>
    internal const int UacDeclined = 1223;

    /// <summary>File name that is ever launched elevated.</summary>
    internal const string HostExeName = "TreemapHost.exe";

    /// <summary>Splits launch arguments into an optional root plus two flags, in any order.</summary>
    internal static (string? root, bool isRetry, bool noElevation) ParseLaunchArgs(string[] args)
    {
        string? root = null;
        bool isRetry = false;
        bool noElevation = false;

        foreach (string arg in args)
        {
            if (string.Equals(arg, ElevatedRetryFlag, StringComparison.OrdinalIgnoreCase))
            {
                isRetry = true;
            }
            else if (string.Equals(arg, NoElevationFlag, StringComparison.OrdinalIgnoreCase))
            {
                noElevation = true;
            }
            else if (root is null && !string.IsNullOrWhiteSpace(arg))
            {
                root = arg;
            }
        }

        return (root, isRetry, noElevation);
    }

    /// <summary>True when the current process runs as an administrator.</summary>
    internal static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Resolves the trusted executable with the production filesystem.</summary>
    internal static string? ResolveTrustedExePath()
        => ResolveTrustedExePath(Environment.ProcessPath, AppContext.BaseDirectory, File.Exists);

    /// <summary>
    /// Test seam: resolves the executable from supplied paths so no test touches the
    /// real process or the disk. Only <see cref="HostExeName"/> is ever returned, so a
    /// framework-dependent <c>dotnet TreemapHost.dll</c> launch resolves the companion
    /// exe in the app directory, and neither dotnet nor a test host is ever launched.
    /// </summary>
    internal static string? ResolveTrustedExePath(
        string? processPath,
        string? baseDirectory,
        Func<string?, bool> fileExists)
    {
        try
        {
            if (IsHostExe(processPath) && processPath is not null && fileExists(processPath))
            {
                return processPath;
            }

            if (!string.IsNullOrEmpty(baseDirectory))
            {
                string companion = Path.Combine(baseDirectory, HostExeName);
                if (fileExists(companion))
                {
                    return companion;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> elevated with the absolute root and the retry
    /// flag as separate arguments. The launch goes directly to the trusted exe via
    /// ShellExecute; there is no command shell, script, or string-built command line,
    /// so spaces, Unicode and trailing backslashes survive untouched.
    /// </summary>
    /// <param name="processStarter">
    /// Test seam for the final <see cref="Process.Start(ProcessStartInfo)"/> call, so tests
    /// can inspect the start info without launching anything. Production passes null.
    /// </param>
    internal static bool StartElevated(
        string exePath,
        string rootAbsolutePath,
        string retryFlag,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
        };
        psi.ArgumentList.Add(rootAbsolutePath);
        psi.ArgumentList.Add(retryFlag);

        Process? process = processStarter is null ? Process.Start(psi) : processStarter(psi);
        if (process is null)
        {
            return false;
        }

        process.Dispose();
        return true;
    }

    private static bool IsHostExe(string? path)
        => string.Equals(Path.GetFileName(path), HostExeName, StringComparison.OrdinalIgnoreCase);
}
