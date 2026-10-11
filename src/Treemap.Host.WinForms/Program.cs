namespace Treemap.Host.WinForms;

/// <summary>
/// Entry point: opens the scan window, optionally pointed at a root, and runs a message
/// loop until it closes.
/// </summary>
internal static class Program
{
    /// <param name="args">
    /// An optional root to scan - a folder or a volume such as <c>C:\</c> - plus
    /// <c>--elevated-retry</c> (the one-shot marker of an elevated relaunch) and
    /// <c>--no-elevation</c> (disables the automatic UAC restart), in any order. Without
    /// a root nothing is scanned until the user picks one, so the program never walks a
    /// whole drive on its own.
    /// </param>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        (string? root, bool isRetry, bool noElevation) = Elevation.ParseLaunchArgs(args);

        // The shell owns the publisher, so a scan publishes into the view built to show
        // it, and the argument is not acted on until the window is shown.
        using ScanShell shell = new(startupPath: root, isElevatedRetry: isRetry, disableElevation: noElevation);

        Application.Run(shell);
    }
}