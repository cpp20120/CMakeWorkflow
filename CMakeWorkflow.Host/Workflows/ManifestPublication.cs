using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CMakeWorkflow.Host.Protocol;
using Microsoft.Win32.SafeHandles;

namespace CMakeWorkflow.Host.Workflows;

internal static partial class OperationArtifactFile
{
    // Stage at the canonical workspace root. The only remotely selected directory is
    // opened component-by-component before a create-only publication in that directory.
    internal static async Task PublishManifestAsync(string workspace, string directory, string fileName,
        ReadOnlyMemory<byte> bytes, string digest, CancellationToken ct)
    {
        string destination = Path.Combine(directory, fileName);
        if (await HasVerifiedManifestAsync(workspace, destination, bytes.Length, digest, ct))
        {
            return;
        }

        string temporaryName = ".cmwf-manifest-" + Guid.NewGuid().ToString("N") + ".tmp";
        string temporary = Path.Combine(workspace, temporaryName);
        string[] components = directory == workspace ? [] :
            Path.GetRelativePath(workspace, directory).Split(Path.DirectorySeparatorChar);
        bool created = false;
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();

            if (OperatingSystem.IsWindows())
            {
                PublishWindows(workspace, components, temporary, destination);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                PublishUnix(workspace, components, temporaryName, fileName);
            }
            else
            {
                throw new PlatformNotSupportedException("Secure manifest publication is unsupported on this OS.");
            }
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }

        if (!await HasVerifiedManifestAsync(workspace, destination, bytes.Length, digest, ct))
        {
            throw new IOException("Published manifest is no longer available.");
        }
    }

    private static async Task<bool> HasVerifiedManifestAsync(string workspace, string path, int length, string digest,
        CancellationToken ct)
    {
        await using FileStream? existing = OpenCatalogFile(workspace, path, optional: true);
        if (existing is null)
        {
            return false;
        }
        if (existing.Length != length)
        {
            throw new ApplicationRefusal("manifest_changed", "The content-addressed manifest path contains different bytes.");
        }
        byte[] data = new byte[length];
        await existing.ReadExactlyAsync(data, ct);
        return existing.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(data)) != digest
            ? throw new ApplicationRefusal("manifest_changed", "The content-addressed manifest path contains different bytes.")
            : true;
    }

    private static void PublishUnix(string workspace, string[] components, string temporary, string destination)
    {
        int flags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 : 0x100000 | 0x100 | 0x1000000;
        int rootFd = UnixOpen(workspace, flags);
        if (rootFd == -1)
        {
            throw UnixOpenError(Marshal.GetLastPInvokeError());
        }
        using SafeFileHandle root = new(new IntPtr(rootFd), ownsHandle: true);
        SafeFileHandle? child = null;
        try
        {
            foreach (string component in components)
            {
                int fd = UnixOpenAt((child ?? root).DangerousGetHandle().ToInt32(), component, flags);
                if (fd == -1)
                {
                    throw UnixOpenError(Marshal.GetLastPInvokeError());
                }
                SafeFileHandle next = new(new IntPtr(fd), ownsHandle: true);
                child?.Dispose();
                child = next;
            }

            // linkat publishes the flushed file atomically and refuses an existing name.
            int error = LinkAt(root, temporary, child ?? root, destination, 0);
            const int alreadyExists = 17;
            if (error is not (0 or alreadyExists))
            {
                throw UnixOpenError(error);
            }
        }
        finally
        {
            child?.Dispose();
        }
    }

    private static void PublishWindows(string workspace, string[] components, string temporary, string destination)
    {
        // Keep every directory open without FILE_SHARE_DELETE while resolving the
        // final pathname for the create-only move.
        const uint directoryAccess = 0x80 | 0x20 | 0x00100000;
        const uint shareReadWrite = 1 | 2;
        const uint openExisting = 3;
        const uint openReparseDirectory = 0x00200000 | 0x02000000;
        using SafeFileHandle root = CreateFileW(workspace, directoryAccess, shareReadWrite, IntPtr.Zero,
            openExisting, openReparseDirectory, IntPtr.Zero);
        if (root.IsInvalid)
        {
            throw new IOException("Could not anchor the manifest workspace.");
        }

        List<SafeFileHandle> opened = [];
        try
        {
            RequireRealDirectory(root);
            SafeFileHandle parent = root;
            foreach (string component in components)
            {
                parent = OpenWindowsChild(parent, component, directory: true, shareReadWrite);
                opened.Add(parent);
                RequireRealDirectory(parent);
            }
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Validate the winner through a no-follow handle.
            }
        }
        finally
        {
            foreach (SafeFileHandle handle in opened)
            {
                handle.Dispose();
            }
        }
    }

    private static int LinkAt(SafeFileHandle sourceDirectory, string source, SafeFileHandle destinationDirectory,
        string destination, int flags)
    {
        nint sourceName = Marshal.StringToCoTaskMemUTF8(source);
        nint destinationName = 0;
        try
        {
            destinationName = Marshal.StringToCoTaskMemUTF8(destination);
            int status = LinkAtNative(sourceDirectory, sourceName, destinationDirectory, destinationName, flags);
            return status == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeCoTaskMem(destinationName);
            Marshal.FreeCoTaskMem(sourceName);
        }
    }

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LinkAtNative(SafeFileHandle sourceDirectory, nint source,
        SafeFileHandle destinationDirectory, nint destination, int flags);
}
