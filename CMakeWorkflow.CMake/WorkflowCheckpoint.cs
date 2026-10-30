using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CMakeWorkflow;

// run.json and its audit are a single commit. Callers must hold the run lease.
internal static class WorkflowCheckpoint
{
    public static async Task SaveAsync(WorkflowReport report, string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "run.json");
        var temporary = path + ".tmp";
        var faultNode = report.Nodes.FirstOrDefault(n => n.Status == "running")?.Id
            ?? report.Reconciliations.LastOrDefault()?.NodeId;
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, report, WorkflowJson.Default.WorkflowReport, ct);
            ct.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
            if (OperatingSystem.IsMacOS()) FullSync(stream);
        }
        await RecoveryFaults.HitAsync("checkpoint-flushed", faultNode, ct);
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            // Same directory/volume; never fall back to copy+delete.
            if (!MoveFileExW(WindowsPath(temporary), WindowsPath(path), 0x1 | 0x8))
                throw NativeError("Checkpoint replacement"); // REPLACE_EXISTING | WRITE_THROUGH
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.Move(temporary, path, overwrite: true);
            await RecoveryFaults.HitAsync("checkpoint-replaced", faultNode, ct);
            await RecoveryFaults.HitAsync("checkpoint-directory-sync", faultNode, ct);
            // Include parents: a newly created run directory must also survive.
            for (string? parent = Path.GetFullPath(directory); parent is not null; parent = Path.GetDirectoryName(parent))
                SyncDirectory(parent);
            if (OperatingSystem.IsMacOS())
            {
                using var committed = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                FullSync(committed);
            }
        }
        else throw new PlatformNotSupportedException("Durable workflow checkpoints require Linux, macOS or Windows.");
    }

    private static string WindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static IOException NativeError(string operation)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException(operation + " failed; checkpoint durability is unconfirmed: " + error.Message, error);
    }

    private static void SyncDirectory(string directory)
    {
        // O_RDONLY | O_DIRECTORY | O_CLOEXEC; never leak directory descriptors to workers.
        var fd = Open(directory, OperatingSystem.IsMacOS() ? 0x100000 | 0x1000000 : 0x10000 | 0x80000);
        if (fd < 0) throw NativeError("Open checkpoint directory");
        try
        {
            int result;
            do { result = Fsync(fd); } while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR
            if (result != 0) throw NativeError("Sync checkpoint directory");
        }
        finally { Close(fd); }
    }

    private static void FullSync(FileStream stream)
    {
        int result;
        do { result = Fcntl(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 51); } // F_FULLFSYNC
        while (result != 0 && Marshal.GetLastPInvokeError() == 4);
        if (result != 0) throw NativeError("Full sync checkpoint");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int fd, int command);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string target, uint flags);
}
