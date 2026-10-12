using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Treemap.Scanning.Windows;

/// <summary>
/// The Win32 entry points the Windows backend needs, and nothing else.
/// </summary>
/// <remarks>
/// Plain <c>DllImport</c> rather than source-generated interop: the source generator
/// emits unsafe marshalling, and the scanner project does not enable unsafe code.
/// <c>DeviceIoControl</c> uses buffered I/O so managed arrays are fine there; raw
/// volume reads use sector-aligned unmanaged buffers (see
/// <see cref="WindowsMftRecordSource"/>), because volume handles are non-cached even
/// without <c>FILE_FLAG_NO_BUFFERING</c> and all non-cached I/O requirements apply
/// (sector-sized offsets, counts and addresses).
/// </remarks>
internal static class NativeMethods
{
    public const uint GenericRead = 0x8000_0000;
    public const uint FileShareRead = 0x0000_0001;
    public const uint FileShareWrite = 0x0000_0002;
    public const uint FileShareDelete = 0x0000_0004;
    public const uint OpenExisting = 3;

    /// <summary>Asks for the volume's geometry. See <see cref="MftGeometry"/>.</summary>
    public const uint FsctlGetNtfsVolumeData = 0x0009_0064;

    /// <summary>
    /// Asks for one MFT record. The search runs downward from the requested ordinal, which
    /// is how a volume's records are enumerated when nothing is known about which are in use.
    /// </summary>
    public const uint FsctlGetNtfsFileRecord = 0x0009_0068;

    /// <summary>
    /// Opens a file, directory or volume read-only. Returns an invalid
    /// <see cref="SafeFileHandle"/> on failure; read the error with
    /// <see cref="Marshal.GetLastWin32Error"/> straight after the call.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    /// A file's allocated size, split across two words.
    /// <see cref="AllocatedSize"/> depends on this exact shape.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    /// <summary>Moves the volume file pointer. Reads below use <c>FileBegin</c>.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(
        SafeFileHandle file,
        long distanceToMove,
        out long newFilePointer,
        uint moveMethod);

    /// <summary>
    /// Reads bytes at the current volume pointer into a sector-aligned unmanaged buffer.
    /// The caller owns the lifetime: the pointer must stay valid for the call and be
    /// freed afterwards. Read-only; nothing here writes, locks or unmounts.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFileAligned(
        SafeFileHandle file,
        nint buffer,
        uint bytesToRead,
        out uint bytesRead,
        nint overlapped);

    /// <summary>Formats a Win32 error for a fallback reason.</summary>
    public static string DescribeError(int error) => $"Win32 {error}: {new System.ComponentModel.Win32Exception(error).Message}";
}