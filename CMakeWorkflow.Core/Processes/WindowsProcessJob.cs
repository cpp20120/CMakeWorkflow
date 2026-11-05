using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Owns the Windows descendant process group until all members are terminated.
// Applied immediately after Process.Start. The initial start/assignment window
// is not perfectly atomic; stronger guarantees require CREATE_SUSPENDED.
internal sealed class WindowsProcessJob : IDisposable
{
    private IntPtr handle;

    private WindowsProcessJob(IntPtr handle)
    {
        this.handle = handle;
    }

    public static WindowsProcessJob Attach(Process process)
    {
        nint handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        WindowsProcessJob job = new(handle);
        try
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                // Process.Start resumes the child before we can assign a Job Object.
                // A very short-lived child may exit in that window; Windows then
                // returns ERROR_ACCESS_DENIED although its exit code remains valid.
                // Never accept an assignment failure for a still-running process:
                // without the Job Object its descendants could escape cleanup.
                int error = Marshal.GetLastPInvokeError();
                if (error != 5 || !process.HasExited) // ERROR_ACCESS_DENIED
                {
                    throw new Win32Exception(error,
                        $"AssignProcessToJobObject failed (Win32={error}, exited={process.HasExited}).");
                }
                // Empty Job Object; the direct child has already terminated.
                // The caller must still drain redirected streams and inspect ExitCode.
                // This does not close the pre-assignment descendant race; atomic
                // supervision would require CREATE_SUSPENDED at process creation.
            }
            return job;
        }
        catch (Exception attachError)
        {
            // Fail closed: a process we cannot supervise must not keep running.
            List<Exception> failures = [attachError];
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception killError)
            {
                failures.Add(killError);
            }
#pragma warning restore CA1031


            try { job.Dispose(); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception disposeError) { failures.Add(disposeError); }
#pragma warning restore CA1031


            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(attachError).Throw();
            }

            throw new AggregateException("Process supervision attachment and cleanup failed.", failures);
        }
    }

    public async Task TerminateAndWaitAsync(CancellationToken ct)
    {
        if (!TerminateJobObject(handle, 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateJobObject failed.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (!QueryInformationJobObject(handle, 1, out BasicAccounting info,
                        (uint)Marshal.SizeOf<BasicAccounting>(), out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        $"QueryInformationJobObject failed (class=1, size={Marshal.SizeOf<BasicAccounting>()}).");
                }

                if (info.ActiveProcesses == 0)
                {
                    return;
                }

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
        nint previous = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (previous != IntPtr.Zero)
        {
            _ = CloseHandle(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out BasicAccounting information, uint length, out uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr handle);
}
