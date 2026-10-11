namespace Treemap.Scanning;

/// <summary>
/// Point-in-time volume accounting for one scan: the size of the volume and what is
/// still free on it. Read once per scan and cached, so every preview and the final
/// tree measure against the same basis.
/// </summary>
/// <param name="TotalSize">Total bytes on the volume, never negative.</param>
/// <param name="FreeSpaceBytes">Free bytes on the volume, never negative.</param>
internal sealed record VolumeSpaceInfo(long TotalSize, long FreeSpaceBytes);

/// <summary>
/// The unaccounted-space residual: used volume bytes minus what the scanned files
/// represent, never negative and never wrapping.
/// </summary>
internal static class VolumeSpace
{
    /// <summary>
    /// Used bytes minus represented file bytes, clamped at zero. Duplicated hard links
    /// count once per link in <paramref name="representedBytes"/>, so the represented
    /// side can legitimately exceed the used side - that clamps to nothing missing,
    /// not to a negative hole. All inputs are read defensively: a platform that
    /// reports nonsense still yields a nonnegative answer.
    /// </summary>
    public static long UnknownResidual(long totalSize, long freeSpace, long representedBytes)
    {
        long total = totalSize > 0 ? totalSize : 0;
        long free = freeSpace > 0 ? freeSpace : 0;
        long represented = representedBytes > 0 ? representedBytes : 0;

        long used = total <= free ? 0 : total - free;
        return represented >= used ? 0 : used - represented;
    }
}
