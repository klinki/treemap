using Treemap;

namespace Treemap.Performance;

/// <summary>
/// Builds synthetic-but-realistic filesystem trees for benchmarking.
/// </summary>
/// <remarks>
/// <para>
/// Benchmarks are only meaningful against data that resembles the real workload.
/// A balanced tree or uniform sizes would make every node the same size and hide
/// exactly the costs that matter: culling, label fitting, and sorting.
/// </para>
///
/// <para>
/// The shape below imitates a real Windows user profile: a heavy
/// <c>Users</c> branch, deep directory nesting, a long tail of small files, a few
/// very large files, and a chunk of free space — all of which the renderer and
/// layout algorithm treat differently.
/// </para>
/// </remarks>
public static class SyntheticTree
{
    /// <summary>
    /// Id source scoped to a single <see cref="Build"/> call.
    /// </summary>
    /// <remarks>
    /// Ids used to come from a static counter reset at the top of <c>Build</c>. Two
    /// concurrent builds reset and advanced each other's counter, which produced
    /// duplicate ids inside one tree and made the same seed produce different trees.
    /// </remarks>
    private sealed class Ids
    {
        private int _next;

        public int Next() => _next++;
    }

    /// <summary>Deterministic PRNG so benchmark runs are comparable across machines.</summary>
    private sealed class Rng(int seed)
    {
        private uint _state = (uint)seed | 1u;

        public uint Next()
        {
            // xorshift32: fast, deterministic, good enough for shape generation.
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        public int Next(int minInclusive, int maxExclusive)
            => minInclusive + (int)(Next() % (uint)(maxExclusive - minInclusive));

        public double NextDouble() => (Next() % 100_000) / 100_000.0;
    }

    public static TreemapNode Build(int targetFileCount, int seed = 12345)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetFileCount, 1);

        Ids ids = new();
        Rng rng = new(seed);

        List<TreemapNode> roots = [];
        int budget = targetFileCount;

        // Free space is always present on a real volume and is a large flat node,
        // which exercises the space-node colour path.
        roots.Add(TreemapNode.Create(
            ids.Next(), "Unallocated", NodeKind.FreeSpace, 95_000_000_000L, 95_000_000_000L));

        while (budget > 0)
        {
            roots.Add(BuildTopLevel(rng, ids, ref budget));
        }

        return TreemapNode.Create(ids.Next(), "Local Disk (C:)", NodeKind.Drive, 0, 0, null, 0, roots);
    }

    private static TreemapNode BuildTopLevel(Rng rng, Ids ids, ref int budget)
    {
        string[] names =
        [
            "Windows", "Program Files", "Program Files (x86)", "Users",
            "ProgramData", "Apps", "Games", "temp",
        ];

        string name = names[rng.Next(0, names.Length)];

        int share = Math.Max(1, budget / Math.Max(1, rng.Next(2, 6)));
        share = Math.Min(share, budget);

        TreemapNode node = name == "Users"
            ? BuildUsersProfile(rng, ids, share, ref budget)
            : BuildFolder(rng, ids, name, share, ref budget, maxDepth: 3);

        return node;
    }

    /// <summary>Users is the dominant branch on a real machine, and nests deepest.</summary>
    private static TreemapNode BuildUsersProfile(Rng rng, Ids ids, int share, ref int budget)
    {
        List<TreemapNode> profiles = [];

        string[] users = ["david", "Public", "Default", "AllUsers"];
        int perUser = Math.Max(1, share / users.Length);

        foreach (string user in users)
        {
            int userBudget = Math.Min(perUser, budget);
            if (userBudget <= 0)
            {
                break;
            }

            profiles.Add(BuildFolder(rng, ids, user, userBudget, ref budget, maxDepth: 4, userProfile: true));
        }

        return TreemapNode.Create(ids.Next(), "Users", NodeKind.Folder, 0, 0, null, 0, profiles);
    }

    private static TreemapNode BuildFolder(
        Rng rng, Ids ids, string name, int fileCount, ref int budget, int maxDepth, bool userProfile = false)
    {
        List<TreemapNode> children = [];

        // Direct files.
        // The floor of 1 is load-bearing, not cosmetic. A percentage split alone can
        // yield 0 files for a small fileCount, and since childShare is floored at 1
        // the recursion would then create subfolders forever while decrementing
        // nothing -- an unbounded loop that grows the node list until RAM is gone.
        // Emitting at least one file per folder guarantees budget strictly decreases
        // on every call, so generation always terminates.
        int direct = Math.Max(1, (int)(fileCount * (0.15 + (rng.NextDouble() * 0.35))));
        for (int i = 0; i < direct && budget > 0; i++)
        {
            children.Add(MakeFile(rng, ids));
            budget--;
        }

        if (budget > 0 && maxDepth > 0)
        {
            int subFolderCount = rng.Next(0, userProfile ? 8 : 5);

            for (int i = 0; i < subFolderCount && budget > 0; i++)
            {
                // Skewed split: a few large folders plus a long tail of tiny ones.
                int childShare = rng.NextDouble() < 0.25
                    ? Math.Max(1, fileCount / 3)
                    : Math.Max(1, fileCount / (subFolderCount + 2));

                childShare = Math.Min(childShare, budget);
                if (childShare <= 0)
                {
                    break;
                }

                children.Add(BuildFolder(
                    rng, ids, SubFolderName(rng), childShare, ref budget, maxDepth - 1, userProfile));
            }
        }

        return TreemapNode.Create(ids.Next(), name, NodeKind.Folder, 0, 0, null, 0, children);
    }

    private static TreemapNode MakeFile(Rng rng, Ids ids)
    {
        // Log-ish distribution: mostly small files with rare multi-hundred-MB outliers.
        double roll = rng.NextDouble();
        long size = roll switch
        {
            < 0.70 => rng.Next(1_000, 200_000),
            < 0.92 => rng.Next(200_000, 8_000_000),
            < 0.99 => rng.Next(8_000_000, 90_000_000),
            _ => rng.Next(90_000_000, 900_000_000),
        };

        return TreemapNode.Create(
            ids.Next(),
            FileName(rng),
            NodeKind.File,
            size,
            size,
            DateTimeOffset.UnixEpoch.AddSeconds(rng.Next(0, 1_700_000_000)),
            rng.Next(0, 10));
    }

    private static readonly string[] Extensions =
        [".dll", ".exe", ".cs", ".json", ".png", ".log", ".tmp", ".dat", ".txt", ".pak", ".bin"];

    private static readonly string[] BaseNames =
        ["data", "core", "main", "util", "index", "config", "assets", "engine", "runtime", "cache"];

    private static string FileName(Rng rng)
        => BaseNames[rng.Next(0, BaseNames.Length)]
           + Extensions[rng.Next(0, Extensions.Length)];

    private static readonly string[] SubFolderNames =
        ["src", "lib", "bin", "obj", "temp", "docs", "media", "backup", "logs", "build"];

    private static string SubFolderName(Rng rng)
        => SubFolderNames[rng.Next(0, SubFolderNames.Length)];
}
