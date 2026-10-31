using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CMakeWorkflow;

// Owns the Windows descendant process group until all members are terminated.
// Applied immediately after Process.Start. The initial start/assignment window
// is not perfectly atomic; stronger guarantees require CREATE_SUSPENDED.
internal sealed class WindowsProcessJob : IDisposable
{
    private IntPtr handle;

    private WindowsProcessJob(IntPtr handle) => this.handle = handle;

    public static WindowsProcessJob Attach(Process process)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        var job = new WindowsProcessJob(handle);
        try
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
            return job;
        }
        catch
        {
            // Fail closed: a process we cannot supervise must not keep running.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            job.Dispose();
            throw;
        }
    }

    public async Task TerminateAndWaitAsync(CancellationToken ct)
    {
        if (!TerminateJobObject(handle, 1))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateJobObject failed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (!QueryInformationJobObject(handle, 1, out var info,
                    (uint)Marshal.SizeOf<BasicAccounting>(), out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryInformationJobObject failed.");
                if (info.ActiveProcesses == 0) return;
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Windows job descendants remained alive after termination.");
        }
    }

    public void Dispose()
    {
        var previous = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (previous != IntPtr.Zero) CloseHandle(previous);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out BasicAccounting information, uint length, out uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
