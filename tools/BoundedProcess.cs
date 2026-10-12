using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class BoundedProcess
{
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] struct CompletionPort
    {
        public IntPtr Key, Port;
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref CompletionPort info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int kind, out ExtendedLimit info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existing, IntPtr key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetQueuedCompletionStatus(IntPtr port, out uint message, out IntPtr key, out IntPtr data, uint timeout);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public sealed class Result
    {
        public uint ProcessId, ExitCode;
        public bool MemoryLimitsEnabled;
        public string StopReason;
        public double ElapsedSeconds, PeakProcessMiB, PeakJobMiB;
    }
    static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    static IntPtr CheckHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    public static Result Run(string application, string arguments, string directory, string log, int timeoutSeconds, ulong processMiB, ulong jobMiB, bool disableMemoryLimits = false, bool showWindow = false)
    {
        IntPtr job = IntPtr.Zero, port = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        ProcessInfo process = new ProcessInfo();
        bool assigned = false;
        Result result = new Result { StopReason = "completed", MemoryLimitsEnabled = !disableMemoryLimits };
        try
        {
            job = CheckHandle(CreateJobObject(IntPtr.Zero, null));
            ExtendedLimit limits = new ExtendedLimit();
            // Kernel commit caps, kill on close, and no breakaway permission.
            // Disabling memory limits leaves only kill-on-close, and the
            // 90% tripwire plus limit messages must stay disabled below.
            if (disableMemoryLimits)
            {
                limits.Basic.Flags = 0x2000;
            }
            else
            {
                limits.Basic.Flags = 0x100 | 0x200 | 0x2000;
                limits.ProcessMemory = new UIntPtr(processMiB * 1024 * 1024);
                limits.JobMemory = new UIntPtr(jobMiB * 1024 * 1024);
            }
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()));
            port = CheckHandle(CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 1));
            CompletionPort association = new CompletionPort { Key = new IntPtr(1), Port = port };
            Check(SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<CompletionPort>()));
            SecurityAttributes security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            output = CheckHandle(CreateFile(log, 0x40000000, 1, ref security, 2, 0x80, IntPtr.Zero));
            input = CheckHandle(CreateFile("NUL", 0x80000000, 1, ref security, 3, 0x80, IntPtr.Zero));
            StartupInfo startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x101, ShowWindow = (ushort)(showWindow ? 5 : 0), Input = input, Output = output, Error = output };
            // Hidden default keeps CREATE_NO_WINDOW; visible GUI needs a window.
            uint createFlags = showWindow ? 0x00000004u : 0x08000004u;
            Check(CreateProcess(application, new StringBuilder("\"" + application + "\" " + arguments), IntPtr.Zero, IntPtr.Zero, true, createFlags, IntPtr.Zero, directory, ref startup, out process));
            // Assign while suspended so even its earliest descendants inherit the caps.
            Check(AssignProcessToJobObject(job, process.Process));
            assigned = true;
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            result.ProcessId = process.ProcessId;
            Stopwatch clock = Stopwatch.StartNew();
            while (true)
            {
                uint message;
                IntPtr key, data;
                bool received = GetQueuedCompletionStatus(port, out message, out key, out data, 100);
                Check(QueryInformationJobObject(job, 9, out limits, (uint)Marshal.SizeOf<ExtendedLimit>(), IntPtr.Zero));
                result.PeakProcessMiB = limits.PeakProcessMemory.ToUInt64() / 1048576.0;
                result.PeakJobMiB = limits.PeakJobMemory.ToUInt64() / 1048576.0;
                // Also kill at 90% of caps: completion-port limit messages can be dropped.
                // Disabled caps mean unlimited: skip every memory kill, avoid 0 * 0.9.
                bool memoryExceeded = false;
                if (!disableMemoryLimits)
                {
                    bool limitMessage = received && (message == 9 || message == 10);
                    bool processOver = processMiB > 0 && result.PeakProcessMiB >= processMiB * 0.9;
                    bool jobOver = jobMiB > 0 && result.PeakJobMiB >= jobMiB * 0.9;
                    memoryExceeded = limitMessage || processOver || jobOver;
                }
                if (memoryExceeded)
                    result.StopReason = "memory_limit";
                else if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
                    result.StopReason = "timeout";
                if (result.StopReason != "completed")
                {
                    Check(TerminateJobObject(job, 124));
                    WaitForSingleObject(process.Process, 5000);
                    break;
                }
                uint wait = WaitForSingleObject(process.Process, 0);
                if (wait == 0) break;
                if (wait == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Check(GetExitCodeProcess(process.Process, out result.ExitCode));
            result.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            return result;
        }
        finally
        {
            if (process.Process != IntPtr.Zero && !assigned) TerminateProcess(process.Process, 125);
            // The non-inheritable job handle kills any remaining descendants, even on errors.
            if (job != IntPtr.Zero) CloseHandle(job);
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (input != IntPtr.Zero) CloseHandle(input);
            if (output != IntPtr.Zero) CloseHandle(output);
            if (port != IntPtr.Zero) CloseHandle(port);
        }
    }
}
