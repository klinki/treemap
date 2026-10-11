namespace Treemap.Scanning.Windows;

/// <summary>
/// Windows path shaping for the MFT backend.
/// </summary>
/// <remarks>
/// Hand-rolled rather than delegated to <see cref="Path"/>, so that which segments of a path
/// sit below a volume root is answered the same way on every host and testable without a disk.
/// </remarks>
internal static class NtfsPath
{
    /// <summary>
    /// Splits a Windows path into its volume root and the segments below it.
    /// </summary>
    /// <remarks>
    /// Only a drive-letter volume qualifies. A UNC share names a file server, not a
    /// volume, and there is no single MFT behind it to enumerate. A device path
    /// (<c>\\.\C:</c>) is how the backend opens the volume, never how a caller names a
    /// directory to scan.
    /// </remarks>
    public static bool TrySplit(string path, out string volumeRoot, out string[] segments)
    {
        volumeRoot = string.Empty;
        segments = [];

        if (string.IsNullOrEmpty(path)
            || path.Length < 3
            || path[1] != ':'
            || !IsDriveLetter(path[0])
            || (path[2] != '\\' && path[2] != '/'))
        {
            // Anything else is not an absolute drive-rooted path: a drive-relative
            // "C:" or "C:sub", a UNC share, a device or extended path, or a name the
            // host shaped some other way. All decline with empty outs.
            return false;
        }

        volumeRoot = path[..2] + "\\";

        List<string> parts = [];
        int start = 2;

        while (start <= path.Length)
        {
            int back = path.IndexOf('\\', start);
            int slash = path.IndexOf('/', start);
            int separator = back < 0 ? slash : slash < 0 ? back : Math.Min(back, slash);
            int end = separator < 0 ? path.Length : separator;
            string segment = path[start..end];

            if (segment.Length > 0 && segment != ".")
            {
                if (segment == "..")
                {
                    if (parts.Count == 0)
                    {
                        // Climbing above the volume root. The caller asked for something
                        // that does not exist, and guessing is how a scan ends up reading a
                        // directory it was not pointed at.
                        volumeRoot = string.Empty;
                        segments = [];
                        return false;
                    }

                    parts.RemoveAt(parts.Count - 1);
                }
                else
                {
                    parts.Add(segment);
                }
            }

            if (separator < 0)
            {
                break;
            }

            start = separator + 1;
        }

        segments = [.. parts];
        return true;
    }

    private static bool IsDriveLetter(char character)
        => (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z');
}