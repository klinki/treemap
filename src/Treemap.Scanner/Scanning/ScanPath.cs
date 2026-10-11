namespace Treemap.Scanning;

/// <summary>Path handling shared by both backends and the tree builder.</summary>
/// <remarks>
/// Only the platform's own separators are structural: a backslash is a legal character in
/// a Unix file name, and splitting on it would tear <c>a\b.txt</c> into two nodes. Name
/// comparison follows the platform's case rules too, because folding case on a Unix
/// filesystem would collapse two distinct files into one node - and hand both the same id.
/// </remarks>
internal static class ScanPath
{
    /// <summary>Directory separators that split a relative path into segments.</summary>
    public static char[] Separators { get; } = Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
        ? [Path.DirectorySeparatorChar]
        : [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Comparer for sibling names and for the scanner's path-to-id map.</summary>
    public static StringComparer NameComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>The matching <see cref="StringComparison"/> for path prefix tests.</summary>
    public static StringComparison NameComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// The one spelling of a scan root the rest of the scanner works with: absolute, and
    /// without a trailing separator unless the path <i>is</i> the volume root.
    /// <c>Path.TrimEndingDirectorySeparator</c> keeps <c>C:\</c> and <c>/</c> whole.
    /// </summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Joins segments below a parent directory. <paramref name="parent"/> may be empty.</summary>
    public static string Join(string parent, string name)
        => parent.Length == 0 ? name : string.Concat(parent, Path.DirectorySeparatorChar, name);
}