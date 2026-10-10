using System.Globalization;
using Treemap;

namespace Treemap.Host.WinForms;

/// <summary>
/// Entry point for the WinForms shell: builds a demo tree, publishes it as a snapshot and
/// shows it in a <see cref="TreemapForm"/>.
/// </summary>
internal static class Program
{
    private static int _nextId = 1;

    /// <summary>Fixed seed so the demo tree is identical on every run.</summary>
    private static readonly Random Clock = new(20251010);

    /// <summary>Node id to display name, for the renderer's label fitting.</summary>
    private static readonly Dictionary<int, string> Names = [];

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Names.Clear();
        TreemapNode root = BuildDrive();

        // The controller reads publisher.Current for both its initial focus root and every
        // Relayout, so the tree has to be published before the view is constructed.
        SnapshotPublisher publisher = new(root);
        publisher.Publish(root);

        TreemapForm view = new(publisher, ResolveName)
        {
            Dock = DockStyle.Fill,
        };

        using Form shell = new()
        {
            Text = "Treemap",
            ClientSize = new Size(1100, 680),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = SystemColors.Control,
            KeyPreview = true,
        };

        shell.Controls.Add(view);
        Application.Run(shell);
    }

    private static string ResolveName(int id)
        => Names.TryGetValue(id, out string? name) ? name : id.ToString(CultureInfo.InvariantCulture);

    // ---- demo tree ----

    private static TreemapNode File(string name, long size, int colorGroup)
    {
        int id = _nextId++;
        Names[id] = name;

        DateTimeOffset written = DateTimeOffset.Now.AddDays(-Clock.Next(1, 720));
        return TreemapNode.Create(id, name, NodeKind.File, size, size, written, colorGroup);
    }

    private static TreemapNode Folder(string name, params TreemapNode[] children)
    {
        int id = _nextId++;
        Names[id] = name;

        // Sizes are aggregated by Create from the children.
        return TreemapNode.Create(id, name, NodeKind.Folder, 0, 0, null, 0, children);
    }

    private static TreemapNode Space(string name, NodeKind kind, long size)
    {
        int id = _nextId++;
        Names[id] = name;

        return TreemapNode.Create(id, name, kind, size, size, null, 0);
    }

    private static TreemapNode Drive(string name, params TreemapNode[] children)
    {
        int id = _nextId++;
        Names[id] = name;

        return TreemapNode.Create(id, name, NodeKind.Drive, 0, 0, null, 0, children);
    }

    private const long Mb = 1_000_000L;
    private const long Gb = 1_000L * Mb;

    private static TreemapNode BuildDrive()
        => Drive("Local Disk (C:)",
            Folder("Windows",
                Folder("System32",
                    File("ntdll.dll", 2_180 * Mb, 1),
                    File("kernel32.dll", 1_240 * Mb, 1),
                    File("winhttp.dll", 372 * Mb, 1),
                    File("drivers\\etc\\hosts", 1 * Mb, 4)),
                Folder("WinSxS",
                    File("amd64_microsoft-windows-core.system_1", 3_900 * Mb, 1),
                    File("amd64_microsoft-windows-shell_1", 2_150 * Mb, 1)),
                File("explorer.exe", 5_100 * Mb, 1),
                File("notepad.exe", 201 * Mb, 1)),
            Folder("Program Files",
                Folder("Microsoft",
                    Folder("Edge",
                        File("msedge.exe", 6_400 * Mb, 2),
                        File("chrome_elf.dll", 1_100 * Mb, 2)),
                    Folder("Office",
                        Folder("root\\Office16",
                            File("winword.exe", 2_800 * Mb, 0),
                            File("excel.exe", 4_150 * Mb, 0),
                            File("outlook.exe", 1_960 * Mb, 0)))),
                Folder("dotnet",
                    File("dotnet.exe", 780 * Mb, 3),
                    Folder("shared",
                        Folder("Microsoft.NETCore.App",
                            File("System.Private.CoreLib.dll", 12 * Mb, 3),
                            File("System.Runtime.dll", 2 * Mb, 3)),
                        Folder("Microsoft.AspNetCore.App",
                            File("Microsoft.AspNetCore.dll", 4 * Mb, 3)))),
                Folder("Git",
                    File("git.exe", 3_600 * Mb, 3),
                    File("bash.exe", 1_100 * Mb, 3),
                    File("git-lfs.exe", 12 * Mb, 3))),
            Folder("Users\\david",
                Folder("Documents",
                    Folder("Reports",
                        File("q3-forecast.xlsx", 9_800 * Mb, 6),
                        File("q3-forecast.xlsx.bak", 9_100 * Mb, 6),
                        File("summary.docx", 640 * Mb, 0)),
                    Folder("Invoices",
                        File("2026-07.pdf", 240 * Mb, 2),
                        File("2026-08.pdf", 260 * Mb, 2)),
                    File("notes.txt", 42 * Mb, 5),
                    File("todo.md", 18 * Mb, 5)),
                Folder("Projects",
                    Folder("sniffer",
                        Folder("treemap",
                            File("Treemap.Core.dll", 96 * Mb, 3),
                            File("Treemap.Skia.dll", 74 * Mb, 3),
                            File("TreemapHost.exe", 128 * Mb, 3)),
                        Folder("scanner",
                            File("VolumeScanner.cs", 46 * Mb, 5),
                            File("Native\\diskutil.h", 8 * Mb, 5)),
                        File("SpaceSniffer2.exe", 8_691 * Mb, 9),
                        File("README.md", 18 * Mb, 5)),
                    Folder("archive",
                        File("backup-2025.zip", 41 * Gb, 7),
                        File("backup-2024.zip", 38 * Gb, 7))),
                Folder("Pictures",
                    File("wallpaper-4k.png", 5_400 * Mb, 8),
                    Folder("Screenshots",
                        File("2026-08-01.png", 1_900 * Mb, 8),
                        File("2026-08-02.png", 2_100 * Mb, 8))),
                Space("AppData", NodeKind.UnknownSpace, 82 * Gb)),
            Folder("temp",
                File("build.log", 640 * Mb, 6),
                File("cache.tmp", 1_900 * Mb, 6)),
            Space("Unallocated", NodeKind.FreeSpace, 118 * Gb));
}
