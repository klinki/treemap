using System.Runtime.InteropServices;

namespace Treemap.Scanning;

/// <summary>
/// Reads a file's allocated ("on disk") size. A platform that will not answer reports 0
/// with <c>available == false</c>, which <see cref="TreemapNode.LayoutSize"/> already reads
/// as "fall back to the logical size", so a scan without allocation still renders at true
/// scale.
/// </summary>
/// <remarks>
/// The only thing this needs from the Windows interop layer is
/// <c>NativeMethods.GetCompressedFileSize(string path, out uint high)</c> returning
/// <c>uint</c>, with <c>SetLastError = true</c> so the 0xFFFFFFFF failure value can be told
/// apart from a genuine low word. That is why this file is not inside the Windows folder.
/// </remarks>
internal static class AllocatedSize
{
    public static long Read(string path) => Read(path, out _);

    /// <summary>
    /// The allocated size, and whether the platform actually answered. A file that really
    /// occupies nothing and a file the platform declined to describe both report 0, and a
    /// caller that reports sizes should not have to guess which one it got.
    /// </summary>
    public static long Read(string path, out bool available)
    {
        if (!OperatingSystem.IsWindows())
        {
            available = false;
            return 0;
        }

        uint low = Windows.NativeMethods.GetCompressedFileSize(Extended(path), out uint high);

        // Read straight after the call: the value is per-thread, and only the
        // error code tells a real 0xFFFFFFFF low word from a failure returning one.
        int error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0)
        {
            available = false;
            return 0;
        }

        available = true;
        long combined = ((long)high << 32) | low;
        return combined < 0 ? long.MaxValue : combined;
    }

    /// <summary>
    /// Native paths stop at MAX_PATH unless they carry the extended-length prefix, and
    /// this API does not add one for us the way the managed file wrappers do.
    /// </summary>
    private static string Extended(string path)
    {
        if (path.Length < 250 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}