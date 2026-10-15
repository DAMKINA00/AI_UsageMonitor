using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.Core;

/// <summary>
/// 조회용으로 띄운 프로세스를 Job 에 넣어, Job 을 닫으면 그 프로세스가 만든 자식까지 함께 끝나게 한다.
/// (부모가 먼저 끝나 프로세스 트리로 찾을 수 없게 된 자식이 남는 것을 막는다.)
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobObjectLimitKillOnJobClose = 0x2000;

    IntPtr _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
                throw new Win32Exception(error);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Logged = new();

    /// <summary>
    /// 프로세스를 Job 에 넣는다. 실패하면 false (그때는 프로세스 트리 종료에만 의존한다). 실패는 label 별로 처음 한 번만 로그에 남긴다.
    /// 시작 직후에 넣으므로, 시작과 할당 사이에 그 프로세스가 띄운 자식은 Job 밖에 남을 수 있다.
    /// </summary>
    public bool TryAdd(Process process, string label)
    {
        string? failure;
        try
        {
            if (_handle == IntPtr.Zero) failure = "job handle is closed";
            else if (AssignProcessToJobObject(_handle, process.Handle)) return true;
            else failure = $"AssignProcessToJobObject error {Marshal.GetLastWin32Error()}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { failure = ex.Message; }
        if (Logged.TryAdd(label, 0)) Log.Write($"{label}: could not add process to kill-on-close job ({failure}); relying on process tree kill");
        return false;
    }

    /// <summary>Job 을 만들지 못했을 때 label 별로 처음 한 번만 로그에 남긴다.</summary>
    public static void ReportUnavailable(string label)
    {
        if (Logged.TryAdd(label + ":create", 0)) Log.Write($"{label}: kill-on-close job is unavailable; relying on process tree kill");
    }

    /// <summary>닫으면 Job 안에 남은 프로세스가 모두 종료된다.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Job 을 만들 수 없는 환경이면 null.</summary>
    public static KillOnCloseJob? TryCreate()
    {
        try { return new KillOnCloseJob(); }
        catch (Win32Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
