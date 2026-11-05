using System.ComponentModel;
using System.Runtime.InteropServices;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Atomically writes already-encoded bytes and durably commits the destination.
// Callers own directory creation, leases, serialization and recovery policy.
internal static class DurableByteWriter
{
    internal static async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes,
        CancellationToken ct, Func<Task>? afterFlushed = null, Func<Task>? afterReplaced = null,
        Func<Task>? beforeDirectorySync = null)
    {
        path = Path.GetFullPath(path);
        string temporary = path + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, ct);
            ct.ThrowIfCancellationRequested();

#pragma warning disable CA1849 // Flush(true) must commit bytes to disk; FlushAsync only flushes buffers and cannot replace this durability boundary.
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849

            if (OperatingSystem.IsMacOS())
            {
                FullSync(stream);
            }
        }

        if (afterFlushed is not null)
        {
            await afterFlushed();
        }

        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(WindowsPath(temporary), WindowsPath(path), 0x1 | 0x8))
            {
                throw NativeError("Checkpoint replacement");
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.Move(temporary, path, overwrite: true);
            if (afterReplaced is not null)
            {
                await afterReplaced();
            }

            if (beforeDirectorySync is not null)
            {
                await beforeDirectorySync();
            }

            for (string? parent = Path.GetDirectoryName(path);
                 parent is not null;
                 parent = Path.GetDirectoryName(parent))
            {
                SyncDirectory(parent);
            }

            if (OperatingSystem.IsMacOS())
            {
                using FileStream committed = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                FullSync(committed);
            }
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Durable workflow checkpoints require Linux, macOS or Windows.");
        }
    }

    private static string WindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static IOException NativeError(string operation)
    {
        Win32Exception error = new(Marshal.GetLastPInvokeError());
        return new IOException(operation + " failed; checkpoint durability is unconfirmed: " + error.Message, error);
    }

    private static void SyncDirectory(string directory)
    {
        // O_RDONLY | O_DIRECTORY | O_CLOEXEC; never leak directory descriptors to workers.
        int fd = Open(directory, OperatingSystem.IsMacOS() ? 0x100000 | 0x1000000 : 0x10000 | 0x80000);
        if (fd < 0)
        {
            throw NativeError("Open checkpoint directory");
        }

        try
        {
            int result;
            do
            {
                result = Fsync(fd);
            } while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR

            if (result != 0)
            {
                throw NativeError("Sync checkpoint directory");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static void FullSync(FileStream stream)
    {
        int result;
        do
        {
            result = Fcntl(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 51);
        } // F_FULLFSYNC
        while (result != 0 && Marshal.GetLastPInvokeError() == 4);

        if (result != 0)
        {
            throw NativeError("Full sync checkpoint");
        }
    }


#pragma warning disable CA2101 // Unix open/openat paths are explicitly marshaled as UTF-8; Unicode marshaling would change the native filesystem ABI.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
#pragma warning restore CA2101


    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fcntl(int fd, int command);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool MoveFileExW(string source, string target, uint flags);
}
