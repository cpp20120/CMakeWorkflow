using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed class ManagedToolStore(VerifiedCMakeInstaller installer)
{
    internal const string Platform = "linux-x64";
    private const long MaxExpandedBytes = 768L * 1024 * 1024;
    private const int MaxEntries = 32_768;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static bool Supports(ToolPlatform platform, Architecture architecture) =>
        platform == ToolPlatform.Linux && architecture == Architecture.X64;

    internal static string GetRoot()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string? configured = System.Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        if (!string.IsNullOrEmpty(configured) && !Path.IsPathFullyQualified(configured))
        {
            throw new ArgumentException("CMAKEWORKFLOW_TOOL_STORE must be an absolute path.");
        }

        string root = Path.GetFullPath(string.IsNullOrEmpty(configured)
            ? Path.Combine(home, ".local", "share", "CMakeWorkflow")
            : configured);
        return !root.Any(char.IsControl) && !root.Contains(Path.PathSeparator, StringComparison.Ordinal) &&
               !IsDescendant("/usr", root) && !IsDescendant("/opt", root) && !IsDescendant("/etc", root) &&
               ((!string.IsNullOrEmpty(home) && IsDescendant(home, root)) || IsDescendant("/tmp", root))
            ? root
            : throw new InvalidOperationException("Managed tool store must be below the user home directory or /tmp.");
    }

    private static bool IsDescendant(string parent, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + "/", StringComparison.Ordinal);

    internal static string Executable(string root, string tool) => Path.Combine(root, "bin", tool);

    internal static string Location(string store, ManagedToolRelease release) =>
        Path.Combine(store, "toolchains", release.Id, release.Version, release.Platform, release.Sha256);

    internal static async Task<bool> IsInstalledAsync(string store, ManagedToolRelease release, CancellationToken ct)
    {
        string location = Location(store, release);
        CheckDirectoryPath(location);
        if (Directory.Exists(location))
        {
            string actual = await TreeHashAsync(location, ct);
            return actual == release.TreeSha256
                ? true
                : throw new InvalidDataException($"Installed {release.Id} tree failed SHA-256 verification: {location}. " +
                                                 "Remove the damaged installation while no workflows are using it and apply a new plan.");
        }

        return false;
    }

    internal async Task InstallAsync(string store, ManagedToolRelease release, IToolingRuntime runtime, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool installation requires Linux.");
        }

        string destination = Location(store, release);
        string parent = Path.GetDirectoryName(destination)!;
        string locks = Path.Combine(store, "locks");
        EnsureDirectory(locks);
        string lockPath = Path.Combine(locks, release.Sha256 + ".lock");
        RejectLink(new FileInfo(lockPath));
        await using FileStream lease = await LockAsync(lockPath, ct);
        if (await IsInstalledAsync(store, release, ct))
        {
            return;
        }

        EnsureDirectory(parent);
        string stagingRoot = Path.Combine(store, "staging");
        EnsureDirectory(stagingRoot);
        // Only the holder of this digest's stable lock may clean or use its staging area.
        string staging = Path.Combine(stagingRoot, release.Sha256);
        CheckDirectoryPath(staging);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        EnsureDirectory(staging);
        try
        {
            string archive = Path.Combine(staging, "download");
            await installer.DownloadVerifiedAsync(new Uri(release.Source), release.Sha256, archive, ct);
            string files = Path.Combine(staging, "files");
            EnsureDirectory(files);
            await ExtractAsync(release, archive, files, ct);
            if (await TreeHashAsync(files, ct) != release.TreeSha256)
            {
                throw new InvalidDataException($"Extracted {release.Id} tree failed SHA-256 verification.");
            }

            await ProbeAsync(release, files, runtime, ct);
            if (await TreeHashAsync(files, ct) != release.TreeSha256)
            {
                throw new InvalidDataException($"{release.Id} changed its installation during the probe.");
            }

            ct.ThrowIfCancellationRequested();
            Directory.Move(files, destination);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 11)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for the managed tool lock: {path}", error);
                }
            }
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool installation requires Linux.");
        }

        CheckDirectoryPath(path);
        _ = Directory.CreateDirectory(path, PrivateDirectory);
        CheckDirectoryPath(path);
    }

    private static void CheckDirectoryPath(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool verification requires Linux.");
        }

        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            RejectLink(directory);
            if (!directory.Exists)
            {
                continue;
            }

            UnixFileMode mode = File.GetUnixFileMode(directory.FullName);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 &&
                (directory.FullName != "/tmp" || (mode & UnixFileMode.StickyBit) == 0))
            {
                throw new IOException($"Managed tool store path is writable by other users: {directory.FullName}");
            }
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException($"Managed tool path contains a symbolic link: {entry.FullName}");
        }
    }

    private static async Task ExtractAsync(ManagedToolRelease release, string archive, string root, CancellationToken ct)
    {
        await using FileStream input = File.OpenRead(archive);
        if (release.TarRoot is null)
        {
            using ZipArchive zip = new(input, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "ninja" ||
                ((zip.Entries[0].ExternalAttributes >> 16) & 0xf000) != 0x8000)
            {
                throw new InvalidDataException("Expected a single regular ninja file in the ZIP release.");
            }

            ZipArchiveEntry entry = zip.Entries[0];
            await using Stream source = await entry.OpenAsync(ct);
            await WriteFileAsync(root, "bin/ninja", source, entry.Length, true, ct);
            return;
        }

        await using GZipStream gzip = new(input, CompressionMode.Decompress);
        await using TarReader tar = new(gzip);
        int count = 0;
        long size = 0;
        HashSet<string> entries = new(StringComparer.Ordinal);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            string name = entry.EntryType == TarEntryType.Directory ? entry.Name.TrimEnd('/') : entry.Name;
            ValidateRelativePath(name);
            if (++count > MaxEntries || entry.Length > MaxExpandedBytes - size || !entries.Add(name))
            {
                throw new InvalidDataException("Tar release exceeds extraction bounds or contains duplicate paths.");
            }

            size += entry.Length;
            if (entry.EntryType == TarEntryType.Directory && name == release.TarRoot)
            {
                continue;
            }

            if (!name.StartsWith(release.TarRoot + "/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Tar entry is outside the release root.");
            }

            string relative = name[(release.TarRoot.Length + 1)..];
            if (entry.EntryType == TarEntryType.Directory)
            {
                _ = Directory.CreateDirectory(Path.Combine(root, relative));
            }
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                await WriteFileAsync(root, relative, entry.DataStream ?? Stream.Null, entry.Length,
                    (entry.Mode & UnixFileMode.UserExecute) != 0, ct);
            }
            else
            {
                throw new InvalidDataException($"Unexpected tar entry type: {entry.EntryType}.");
            }
        }
    }

    private static void ValidateRelativePath(string name)
    {
        if (name.Length > 4096 || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl) ||
            name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe archive path: {name}");
        }
    }

    private static async Task WriteFileAsync(string root, string relative, Stream source, long size, bool executable,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool installation requires Linux.");
        }

        if (size is < 0 or > MaxExpandedBytes)
        {
            throw new InvalidDataException("Archive entry exceeds extraction bounds.");
        }

        string path = Path.Combine(root, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > size)
            {
                throw new InvalidDataException("Archive entry exceeds its declared size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total != size)
        {
            throw new InvalidDataException("Truncated archive entry.");
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            (executable ? UnixFileMode.UserExecute : UnixFileMode.None));
    }

    // This digest covers the entire installed release, including CMake modules and executable bits.
    // It is pinned in the catalog independently of the archive digest.
    private static async Task<string> TreeHashAsync(string root, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool verification requires Linux.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SortedDictionary<string, FileSystemInfo> entries = new(StringComparer.Ordinal);
        Stack<DirectoryInfo> pending = new();
        pending.Push(new(root));
        while (pending.TryPop(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                RejectLink(entry);
                if (entries.Count >= MaxEntries)
                {
                    throw new InvalidDataException("Managed release has too many entries.");
                }

                string relative = Path.GetRelativePath(root, entry.FullName);
                ValidateRelativePath(relative);
                entries.Add(relative, entry);
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }

        long total = 0;
        foreach ((string relative, FileSystemInfo entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry is FileInfo file)
            {
                total += file.Length;
                if (total > MaxExpandedBytes)
                {
                    throw new InvalidDataException("Managed release exceeds expanded size limit.");
                }

                UnixFileMode mode = File.GetUnixFileMode(file.FullName);
                bool execute = (mode & UnixFileMode.UserExecute) != 0;
                if ((mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup |
                             UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    throw new InvalidDataException("Managed release contains a set-ID or externally writable file.");
                }

                hash.AppendData(Encoding.UTF8.GetBytes((execute ? "f:1:" : "f:0:") + relative + "\n"));
                await using FileStream input = file.OpenRead();
                hash.AppendData(await SHA256.HashDataAsync(input, ct));
            }
            else
            {
                hash.AppendData(Encoding.UTF8.GetBytes("d:" + relative + "\n"));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task ProbeAsync(ManagedToolRelease release, string root, IToolingRuntime runtime, CancellationToken ct)
    {
        string[] tools = release.Id == "cmake" ? ["cmake", "ctest", "cpack"] : ["ninja"];
        foreach (string tool in tools)
        {
            string executable = Executable(root, tool);
            if (!File.Exists(executable))
            {
                throw new InvalidDataException($"Managed release lacks bin/{tool}.");
            }

            ToolCommandResult version = await RunProbeAsync(runtime, executable, ["--version"], ct);
            if (version.ExitCode != 0 || ToolingService.ExtractVersion(version.Output)?.ToString() != release.Version)
            {
                throw new InvalidDataException($"Managed {tool} did not report pinned version {release.Version}.");
            }
        }

        string[] probe = release.Id == "cmake" ? ["-E", "capabilities"] : ["-t", "list"];
        ToolCommandResult result = await RunProbeAsync(runtime, Executable(root, release.Id), probe, ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            throw new InvalidDataException($"Managed {release.Id} failed its functional probe.");
        }
    }
    private static Task<ToolCommandResult> RunProbeAsync(IToolingRuntime runtime, string executable,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(15);
        return runtime is SystemToolingRuntime system
            ? system.RunAsync(executable, args, timeout, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "C",
                ["PATH"] = Path.GetDirectoryName(executable)!
            }, ct)
            : runtime.RunAsync(executable, args, timeout, ct);
    }
}
