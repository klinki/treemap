using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// A real directory tree under the temp directory, removed when the test finishes. Only the
/// directory this test created is ever deleted - nothing walks up looking for something to
/// tidy.
/// </summary>
internal sealed class TempTree : IDisposable
{
    public TempTree(string name)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "treemap-scanner-tests",
            name + "-" + Guid.NewGuid().ToString("N")[..10]);

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Creates a directory below the root and returns its full path.</summary>
    public string MakeDirectory(params string[] segments)
    {
        string path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a file of <paramref name="length"/> bytes and returns its full path.</summary>
    public string MakeFile(string relativePath, int length = 0)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    /// <summary>
    /// Creates a file with explicit attributes. Windows-only: elsewhere the setter rejects
    /// anything but read-only, so a fixture needing this is Windows-only too.
    /// </summary>
    public string MakeFileWithAttributes(string relativePath, FileAttributes attributes, int length = 0)
    {
        string path = MakeFile(relativePath, length);
        File.SetAttributes(path, attributes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            // One recursive delete of the directory this fixture created, and nothing
            // else. Enumerating first to clear attributes would walk the tree, and some
            // of these fixtures contain a link pointing at an ancestor - a traversal
            // would follow it out of the fixture and into real directories.
            // Directory.Delete does not recurse through a reparse point.
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            // A failed fixture delete must not fail the test that used it.
        }
    }
}

/// <summary>
/// Creates a directory link, reporting rather than throwing when the platform or account
/// will not allow one.
/// </summary>
internal static class TestLinks
{
    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return false;
        }
    }
}

/// <summary>
/// Answers once per test run whether this account can create directory links. Windows needs
/// a privilege an unelevated run does not have, and the fixtures that need one cannot
/// report a failure usefully without it.
/// </summary>
/// <remarks>
/// The probe creates its own directory, tries one link whose target is inside that same
/// directory, and deletes the lot, including on failure. It cannot reach anything outside
/// itself, and the answer is cached so the cost is paid once per run.
/// </remarks>
internal static class DirectoryLinkProbe
{
    public static bool IsSupported { get; } = Probe();

    public static bool TryCreate(string linkPath, string targetPath)
        => IsSupported && TestLinks.TryCreateDirectoryLink(linkPath, targetPath);

    private static bool Probe()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "treemap-scanner-tests",
            "link-probe-" + Guid.NewGuid().ToString("N")[..10]);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "target"));
            return TestLinks.TryCreateDirectoryLink(Path.Combine(root, "link"), Path.Combine(root, "target"));
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
            {
            }
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips on platforms or accounts that cannot create
/// directory links, using the probe above rather than a package the project does not have.
/// </summary>
public sealed class RequiresDirectoryLinksAttribute : FactAttribute
{
    public RequiresDirectoryLinksAttribute()
    {
        if (!DirectoryLinkProbe.IsSupported)
        {
            Skip = "This platform or account cannot create directory links.";
        }
    }
}

/// <summary>A fact that only makes sense on Windows.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only behaviour.";
        }
    }
}

/// <summary>A theory that only makes sense on Windows.</summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only behaviour.";
        }
    }
}

/// <summary>A fact that only makes sense where a backslash is an ordinary file-name character.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "A backslash is a separator on Windows.";
        }
    }
}

/// <summary>Finds a node by path in a built tree.</summary>
internal static class TreeAssert
{
    public static TreemapNode? Find(TreemapNode root, params string[] segments)
    {
        TreemapNode node = root;

        foreach (string segment in segments)
        {
            TreemapNode? next = null;
            foreach (TreemapNode child in node.Children)
            {
                if (string.Equals(child.Name, segment, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    public static IEnumerable<TreemapNode> Flatten(TreemapNode root)
    {
        yield return root;

        foreach (TreemapNode child in root.Children)
        {
            foreach (TreemapNode descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}