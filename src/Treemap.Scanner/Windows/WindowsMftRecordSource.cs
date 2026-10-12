using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Treemap.Scanning.Windows;

/// <summary>
/// Reads a live NTFS volume's MFT through a read-only volume handle.
/// </summary>
/// <remarks>
/// Opened for <c>GENERIC_READ</c> and shared for read, write and delete, so a scan never
/// takes a volume offline. An unprivileged process generally cannot get that handle on a
/// system volume at all; access denied is a decline, not a problem to solve around. Nothing
/// here escalates and nothing here writes.
/// </remarks>
internal sealed class WindowsMftRecordSource : IMftRecordSource, IMftVolumeReader
{
    /// <summary>
    /// The whole input is one file reference number. A sequence number of zero says the
    /// caller does not know the record's generation, which is the point: an ordinal says
    /// nothing about which record still occupies it.
    /// </summary>
    private const int InputBufferLength = 8;

    private const int OutputFileReferenceOffset = 0x00;
    private const int OutputLengthOffset = 0x08;

    /// <summary>Room for the 96-byte basic structure plus its extended block.</summary>
    private const int GeometryBufferLength = 512;

    private readonly SafeFileHandle _volume;
    private readonly byte[] _input = new byte[InputBufferLength];
    private byte[] _output;
    private bool _disposed;

    private WindowsMftRecordSource(SafeFileHandle volume, MftGeometry geometry)
    {
        _volume = volume;
        _output = new byte[geometry.BufferLength];
        Geometry = geometry;
    }

    public MftGeometry Geometry { get; }

    /// <summary>
    /// Kernel-decoded: <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns each record after the
    /// filesystem has checked the update sequence tag and restored the saved words over
    /// the unit tails, so the parser must not check the tag or write the save area back
    /// a second time.
    /// </summary>
    public MftRecordRepresentation Representation => MftRecordRepresentation.FixupApplied;

    /// <summary>
    /// Opens <paramref name="volumeRoot"/> and reads its geometry. Returns false with a
    /// reason for anything that stops the volume being read.
    /// </summary>
    public static bool TryOpen(string volumeRoot, out WindowsMftRecordSource? source, out string? failure)
        => TryOpen(volumeRoot, out source, out failure, out _);

    /// <summary>
    /// Opens <paramref name="volumeRoot"/> and reads its geometry, also reporting the
    /// numeric Win32 error when the volume handle or geometry query fails. A zero
    /// <paramref name="win32Error"/> means success or a non-Win32 failure (unsupported
    /// geometry, unusable input); the numeric code is what callers match on, never the
    /// localized message text.
    /// </summary>
    public static bool TryOpen(string volumeRoot, out WindowsMftRecordSource? source, out string? failure, out int win32Error)
    {
        source = null;
        failure = null;
        win32Error = 0;

        SafeFileHandle? handle;

        try
        {
            handle = NativeMethods.CreateFileW(
                DevicePath(volumeRoot),
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                0,
                NativeMethods.OpenExisting,
                flagsAndAttributes: 0,
                templateFile: 0);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            failure = $"The volume '{volumeRoot}' could not be opened: {ScanFailure.Describe(exception)}";
            return false;
        }

        if (handle.IsInvalid)
        {
            // Read before anything else can overwrite the thread's error, including the
            // dispose that follows.
            int error = Marshal.GetLastWin32Error();
            win32Error = error;
            handle.Dispose();
            failure = $"The volume '{volumeRoot}' could not be opened: {NativeMethods.DescribeError(error)}";
            return false;
        }

        try
        {
            byte[] buffer = new byte[GeometryBufferLength];

            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlGetNtfsVolumeData,
                    [],
                    0,
                    buffer,
                    buffer.Length,
                    out uint returned,
                    0))
            {
                int error = Marshal.GetLastWin32Error();
                win32Error = error;
                failure = $"The volume '{volumeRoot}' did not answer an NTFS geometry query: {NativeMethods.DescribeError(error)}";
                return false;
            }

            if (returned > buffer.Length)
            {
                failure = $"The volume '{volumeRoot}' reported {returned} geometry bytes, more than the {buffer.Length}-byte buffer.";
                return false;
            }

            if (!MftGeometry.TryParse(buffer.AsSpan(0, (int)returned), out MftGeometry geometry, out string? geometryFailure))
            {
                failure = $"The volume '{volumeRoot}' reported geometry this backend cannot use: {geometryFailure}";
                return false;
            }

            source = new WindowsMftRecordSource(handle, geometry);
            return true;
        }
        finally
        {
            // Every path out of here either transferred the handle or did not.
            if (source is null)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary><c>C:\</c> becomes <c>\\.\C:</c>.</summary>
    private static string DevicePath(string volumeRoot) => @"\\.\" + volumeRoot.TrimEnd('\\');

    public bool TryReadRecord(
        ulong ordinal,
        byte[] destination,
        out ulong actualOrdinal,
        out int payloadLength,
        out string? failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        actualOrdinal = 0;
        payloadLength = 0;
        failureReason = null;

        if (destination.Length < Geometry.BufferLength)
        {
            failureReason = $"The record buffer is {destination.Length} bytes, too small for a {Geometry.RecordSize}-byte record.";
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(_input, ordinal & (ulong)MftGeometry.OrdinalMask);

        if (!NativeMethods.DeviceIoControl(
                _volume,
                NativeMethods.FsctlGetNtfsFileRecord,
                _input,
                _input.Length,
                _output,
                _output.Length,
                out uint returned,
                0))
        {
            int error = Marshal.GetLastWin32Error();
            failureReason = $"MFT record {ordinal} could not be read: {NativeMethods.DescribeError(error)}";
            return false;
        }

        if (returned < MftGeometry.OutputHeaderLength || returned > _output.Length)
        {
            failureReason = $"MFT record {ordinal} returned {returned} bytes of output, outside its {_output.Length}-byte buffer.";
            return false;
        }

        ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(_output.AsSpan(OutputFileReferenceOffset));
        uint reportedLength = BinaryPrimitives.ReadUInt32LittleEndian(_output.AsSpan(OutputLengthOffset));

        if (reportedLength != Geometry.RecordSize || reportedLength > returned - MftGeometry.OutputHeaderLength)
        {
            failureReason = $"MFT record {ordinal} returned {reportedLength} bytes of record, not the {Geometry.RecordSize} bytes every record holds.";
            return false;
        }

        actualOrdinal = reference & (ulong)MftGeometry.OrdinalMask;
        payloadLength = (int)reportedLength;

        // Copied out so the reusable field never escapes and the walk owns the bytes it
        // parses.
        _output.AsSpan(MftGeometry.OutputHeaderLength, payloadLength)
            .CopyTo(destination.AsSpan(MftGeometry.OutputHeaderLength));

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _volume.Dispose();
    }

    /// <summary>
    /// Reads raw volume bytes for non-resident attribute-list values. Volume handles are
    /// non-cached even without <c>FILE_FLAG_NO_BUFFERING</c>, so every read uses a
    /// sector-aligned unmanaged buffer with a bounded reusable chunk: offsets, counts and
    /// addresses are sector-sized, bounds are checked before I/O, and nothing writes,
    /// locks or unmounts. Read-only.
    /// </summary>
    public bool TryReadVolume(long volumeByteOffset, byte[] buffer, int offset, int count, out string? failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        failureReason = null;

        long endOffset;
        try
        {
            checked
            {
                endOffset = volumeByteOffset + count;
            }
        }
        catch (OverflowException)
        {
            failureReason = $"The volume read at byte {volumeByteOffset} for {count} bytes overflows.";
            return false;
        }

        long endBuffer;
        try
        {
            checked
            {
                endBuffer = (long)offset + count;
            }
        }
        catch (OverflowException)
        {
            failureReason = $"The volume read at byte {volumeByteOffset} for {count} bytes overflows its buffer.";
            return false;
        }

        if (volumeByteOffset < 0 || offset < 0 || count < 0 || endBuffer > buffer.Length)
        {
            failureReason = $"The volume read at byte {volumeByteOffset} for {count} bytes does not fit its buffer.";
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        long sectorSize = Geometry.BytesPerSector;
        if (sectorSize > 0)
        {
            if (volumeByteOffset % sectorSize != 0 || count % sectorSize != 0)
            {
                failureReason = $"The volume read at byte {volumeByteOffset} for {count} bytes is not sector-aligned to {sectorSize} bytes.";
                return false;
            }
        }

        long totalBytes = Geometry.TotalVolumeBytes;
        if (totalBytes > 0 && endOffset > totalBytes)
        {
            failureReason = $"The volume read at byte {volumeByteOffset} for {count} bytes runs past the {totalBytes}-byte volume.";
            return false;
        }

        // Bounded chunks reused through one aligned temporary buffer rather than two
        // whole-value copies. One megabyte stays sector-aligned for every supported
        // sector size (512..64 KiB) and keeps a single outstanding read small.
        const int MaxChunk = 1024 * 1024;
        long alignedChunk = sectorSize > 0 ? (MaxChunk / sectorSize) * sectorSize : MaxChunk;
        if (alignedChunk <= 0)
        {
            alignedChunk = sectorSize > 0 ? sectorSize : MaxChunk;
        }

        int chunkSize = (int)Math.Min(alignedChunk, count);
        nint raw = nint.Zero;
        nint aligned = nint.Zero;
        try
        {
            // Over-allocate so the aligned address inside still holds a full chunk.
            long reserve = (long)chunkSize + sectorSize;
            raw = Marshal.AllocHGlobal((nint)reserve);
            long address = raw.ToInt64();
            long misalignment = sectorSize > 0 ? address % sectorSize : 0;
            long adjust = misalignment == 0 ? 0 : sectorSize - misalignment;
            aligned = nint.CreateChecked(address + adjust);

            int done = 0;
            while (done < count)
            {
                int want = Math.Min(chunkSize, count - done);
                long at = volumeByteOffset + done;

                if (!NativeMethods.SetFilePointerEx(_volume, at, out _, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    failureReason = $"The volume could not seek to byte {at}: {NativeMethods.DescribeError(error)}";
                    return false;
                }

                if (!NativeMethods.ReadFileAligned(_volume, aligned, (uint)want, out uint read, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    failureReason = $"The volume could not be read at byte {at}: {NativeMethods.DescribeError(error)}";
                    return false;
                }

                if (read != want)
                {
                    failureReason = read == 0
                        ? $"The volume ended reading at byte {at}, short of {count} bytes."
                        : $"The volume read {read} bytes at byte {at}, short of the {want} bytes asked for and sector alignment.";
                    return false;
                }

                Marshal.Copy(aligned, buffer, offset + done, (int)read);
                done += (int)read;
            }
        }
        finally
        {
            if (raw != nint.Zero)
            {
                Marshal.FreeHGlobal(raw);
            }
        }

        return true;
    }
}