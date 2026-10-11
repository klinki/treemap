using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Backend selection and fallback: which backend ran, what was thrown away, and what the
/// result says about it.
/// </summary>
public class BackendSelectionTests
{
    [Fact]
    public async Task ExplicitPortableRequestRunsThePortableBackendAndClaimsNoFallback()
    {
        using TempTree tree = new(nameof(ExplicitPortableRequestRunsThePortableBackendAndClaimsNoFallback));
        tree.MakeFile("a.txt", 10);

        ScriptedBackend fast = new(ScanBackend.NtfsMft) { Succeeds = true };

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: fast)
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Portable));

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Null(result.FallbackFrom);
        Assert.Null(result.FallbackReason);
        Assert.Equal(0, fast.Runs);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task FastBackendSuccessIsUsedDirectly()
    {
        using TempTree tree = new(nameof(FastBackendSuccessIsUsedDirectly));
        tree.MakeFile("ignored-by-the-script.txt", 999);

        ScriptedBackend fast = new(ScanBackend.NtfsMft);
        fast.Entries.Add(new ScanEntry("from-mft.txt", NodeKind.File, 42, 42, null));

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: fast)
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Auto));

        Assert.Equal(ScanBackend.NtfsMft, result.Backend);
        Assert.Null(result.FallbackFrom);
        Assert.Equal(1, fast.Runs);
        Assert.Equal(42, result.TotalLogicalSize);
        Assert.NotNull(TreeAssert.Find(result.Root, "from-mft.txt"));

        // The portable walk never ran, so the file it could have found is absent.
        Assert.Null(TreeAssert.Find(result.Root, "ignored-by-the-script.txt"));
    }

    /// <summary>
    /// A backend that half-reads a volume and then decides it cannot be trusted must not
    /// leave its partial data behind for the retry to build on.
    /// </summary>
    [Fact]
    public async Task DecliningFastBackendIsDiscardedAndTheReasonIsKept()
    {
        using TempTree tree = new(nameof(DecliningFastBackendIsDiscardedAndTheReasonIsKept));
        tree.MakeFile("real.txt", 77);

        ScriptedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "Win32 5: Access is denied.",
        };

        fast.Entries.Add(new ScanEntry("partial-from-mft.txt", NodeKind.File, 5000, 5000, null));

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: fast)
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Auto));

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);
        Assert.Equal("Win32 5: Access is denied.", result.FallbackReason);

        Assert.Null(TreeAssert.Find(result.Root, "partial-from-mft.txt"));
        Assert.NotNull(TreeAssert.Find(result.Root, "real.txt"));
        Assert.Equal(77, result.TotalLogicalSize);
    }

    /// <summary>
    /// Cancellation is not a failure. A fast backend that cancels and then declines must
    /// reach the caller as a cancellation: without that, "declined" and "cancelled" look
    /// the same and the scan quietly restarts on another backend.
    /// </summary>
    [Fact]
    public async Task CancellationFromTheFastBackendDoesNotFallBack()
    {
        using TempTree tree = new(nameof(CancellationFromTheFastBackendDoesNotFallBack));
        tree.MakeFile("a.txt", 1);

        using CancellationTokenSource cts = new();
        ScriptedBackend portable = new(ScanBackend.Portable);

        ScriptedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            OnRun = cts.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FileSystemScanner(portableOverride: portable, ntfsOverride: fast)
                .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Auto), cancellationToken: cts.Token));

        Assert.Equal(1, fast.Runs);
        Assert.Equal(0, portable.Runs);
    }

    /// <summary>
    /// An exception out of a backend is a bug, not a fact about the filesystem. Letting
    /// it surface is the only way anyone hears about it.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedBackendExceptionIsNotSwallowed()
    {
        using TempTree tree = new(nameof(AnUnexpectedBackendExceptionIsNotSwallowed));
        tree.MakeFile("a.txt", 1);

        ScriptedBackend fast = new(ScanBackend.NtfsMft)
        {
            Throw = new InvalidOperationException("the backend is broken"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FileSystemScanner(portableOverride: null, ntfsOverride: fast)
                .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Auto)));
    }

    /// <summary>
    /// Every visible name of a hard-linked file becomes a node with its own size, in both
    /// backends, so falling back does not change the shape of the tree. The price is that
    /// the totals charge a shared file once per link, which is stated rather than hidden.
    /// </summary>
    [Fact]
    public async Task HardLinkedNamesAreNotDeduplicated()
    {
        using TempTree tree = new(nameof(HardLinkedNamesAreNotDeduplicated));
        tree.MakeFile("a.txt", 1);

        ScriptedBackend fast = new(ScanBackend.NtfsMft) { MarkExtraNamesAsHardLinks = true };
        fast.Entries.Add(new ScanEntry(Path.Combine("one", "shared.bin"), NodeKind.File, 500, 500, null));
        fast.Entries.Add(new ScanEntry(Path.Combine("two", "shared.bin"), NodeKind.File, 500, 500, null));

        ScanResult result = await new FileSystemScanner(portableOverride: null, ntfsOverride: fast)
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.NtfsMft));

        Assert.Equal(ScanBackend.NtfsMft, result.Backend);
        Assert.Equal(2, result.FileCount);

        // Two names of one file: the first is the file, the second is the extra link.
        Assert.Equal(1, result.HardLinkedFileCount);
        Assert.Equal(1000, result.TotalLogicalSize);
        Assert.NotNull(TreeAssert.Find(result.Root, "one", "shared.bin"));
        Assert.NotNull(TreeAssert.Find(result.Root, "two", "shared.bin"));
    }

    [Fact]
    public async Task RejectsNothingAndSerialisesConcurrentScans()
    {
        using TempTree tree = new(nameof(RejectsNothingAndSerialisesConcurrentScans));
        tree.MakeFile("a.txt", 1);

        FileSystemScanner scanner = new();

        // Two scans at once must both succeed and serialise, not race over node identity.
        Task<ScanResult> first = scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        Task<ScanResult> second = scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());

        ScanResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(1, r.FileCount));
    }
}