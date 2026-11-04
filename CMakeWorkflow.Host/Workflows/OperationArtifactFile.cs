using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CMakeWorkflow.Host;

/// <summary>
/// Opens a report-bound artifact relative to an already opened workspace directory.
/// Every descendant is resolved under a held directory handle, without following
/// symlinks/junctions. A pathname check followed by File.Open would be racy.
/// </summary>
internal static class OperationArtifactFile
{
    internal static FileStream OpenRead(string workspace, string path)
    {
        var root = Path.GetFullPath(workspace);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ApplicationRefusal("path_not_allowed", "Artifact is outside the workspace.");

        var components = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(p => p is "." or ".." || p.Length is 0 or > 255))
            throw new ApplicationRefusal("path_not_allowed", "Invalid artifact path.");

        SafeFileHandle handle = OperatingSystem.IsWindows()
            ? OpenWindows(root, components)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? OpenUnix(root, components)
                : throw new PlatformNotSupportedException("Secure artifact reads are unsupported on this OS.");
        try
        {
            var attrs = File.GetAttributes(handle);
            if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new ApplicationRefusal("path_not_allowed", "Artifact is not a regular, unlinked file.");
            // The descriptor is the authority for all subsequent reads and metadata.
            var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new ApplicationRefusal("path_not_allowed", "Artifact is not a seekable regular file.");
            }
            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenUnix(string root, string[] components)
    {
        // Linux and Darwin have different O_DIRECTORY/O_NOFOLLOW/O_CLOEXEC bits.
        int directoryFlags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 :
            0x100000 | 0x100 | 0x1000000;
        // NONBLOCK prevents a malicious FIFO leaf from stalling the Host in open().
        int fileFlags = OperatingSystem.IsLinux() ? 0x20000 | 0x80000 | 0x800 :
            0x100 | 0x1000000 | 0x4;
        int fd = UnixOpen(root, directoryFlags);
        if (fd == -1) throw UnixOpenError(Marshal.GetLastPInvokeError());
        SafeFileHandle parent = new(new IntPtr(fd), ownsHandle: true);
        try
        {
            foreach (var part in components[..^1])
            {
                fd = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), part, directoryFlags);
                if (fd == -1) throw UnixOpenError(Marshal.GetLastPInvokeError());
                var next = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
                parent.Dispose();
                parent = next;
            }
            fd = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), components[^1], fileFlags);
            if (fd == -1) throw UnixOpenError(Marshal.GetLastPInvokeError());
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }
        finally
        {
            parent.Dispose();
        }
    }

    private static Exception UnixOpenError(int error) => error switch
    {
        2 => new FileNotFoundException("Operation artifact no longer exists."), // ENOENT
        20 => new DirectoryNotFoundException("Artifact parent directory no longer exists."), // ENOTDIR
        40 => new ApplicationRefusal("path_not_allowed", "Symlink in artifact path."), // ELOOP
        _ => new IOException($"Secure artifact open failed (errno={error}).")
    };

    // Anchor the only absolute Win32 open to the workspace. All subsequent names
    // are single components relative to a held directory handle via NtCreateFile.
    private static SafeFileHandle OpenWindows(string root, string[] components)
    {
        const uint readAttributes = 0x80, traverse = 0x20, synchronize = 0x00100000;
        const uint shareAll = 1 | 2 | 4;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        SafeFileHandle parent = CreateFileW(root, readAttributes | traverse | synchronize,
            shareAll, IntPtr.Zero, openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (parent.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            parent.Dispose();
            throw new IOException($"Cannot anchor artifact workspace (win32={error}).", new Win32Exception(error));
        }
        try
        {
            RequireRealDirectory(parent);
            foreach (var part in components[..^1])
            {
                var next = OpenWindowsChild(parent, part, directory: true);
                try { RequireRealDirectory(next); }
                catch { next.Dispose(); throw; }
                parent.Dispose();
                parent = next;
            }
            return OpenWindowsChild(parent, components[^1], directory: false);
        }
        finally
        {
            parent.Dispose();
        }
    }

    private static void RequireRealDirectory(SafeFileHandle handle)
    {
        var attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new ApplicationRefusal("path_not_allowed", "Linked or non-directory artifact ancestor.");
    }

    private static SafeFileHandle OpenWindowsChild(SafeFileHandle parent, string part, bool directory)
    {
        // A relative OBJECT_ATTRIBUTES RootDirectory binds this lookup to the
        // opened ancestor, so swapping the named ancestor cannot redirect it.
        // OBJ_DONT_REPARSE + FILE_OPEN_REPARSE_POINT reject reparse traversal.
        const uint objectCaseInsensitive = 0x40, objectDontReparse = 0x1000;
        // FILE_TRAVERSE is requested only for directory components, not data files.
        var access = 1u | 0x80u | 0x00100000u | (directory ? 0x20u : 0u);
        const uint shareAll = 1 | 2 | 4;
        const uint openExisting = 1; // FILE_OPEN (NT create disposition)
        const uint synchronous = 0x20, openReparsePoint = 0x00200000;
        var data = Marshal.StringToHGlobalUni(part);
        var name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var length = checked((ushort)(part.Length * 2));
            Marshal.StructureToPtr(new UnicodeString { Length = length,
                MaximumLength = checked((ushort)(length + 2)), Buffer = data }, name, fDeleteOld: false);
            var attrs = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(), ObjectName = name,
                Attributes = objectCaseInsensitive | objectDontReparse
            };
            var status = NtCreateFile(out var handle, access, ref attrs, out _, IntPtr.Zero,
                0, shareAll, openExisting, synchronous | openReparsePoint, IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                if (status == unchecked((int)0xC0000034) || status == unchecked((int)0xC000003A))
                    throw new FileNotFoundException("Operation artifact no longer exists.");
                throw new IOException($"Secure artifact open failed (NTSTATUS=0x{status:X8}).");
            }
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("Secure artifact open returned an invalid handle.");
            }
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess,
        ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
        uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        IntPtr eaBuffer, uint eaLength);
}
