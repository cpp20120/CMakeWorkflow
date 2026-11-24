using System.Diagnostics;
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

    internal static string Location(string store, ManagedToolRelease release)
    {
        string group = release.Id switch
        {
            "sysroot" => "sdks",
            "vcpkg-registry" => "vcpkg",
            _ => "toolchains"
        };
        return Path.Combine(store, group, release.Id, release.Version, release.Platform, release.Sha256);
    }

    internal static string ToolPath(string store, ManagedToolRelease release, string tool) =>
        tool is "sysroot" or "vcpkg-registry" ? Location(store, release) : Executable(Location(store, release), tool);

    private static ComponentArchive Format(ManagedToolRelease release) =>
        ManagedComponentCatalog.Find(release.Id, release.Version)?.Format ?? release.Id switch
        {
            "llvm" or "sysroot" => ComponentArchive.TarXz,
            "vcpkg" => ComponentArchive.RawExecutable,
            _ => release.TarRoot is null ? ComponentArchive.ZipExecutable : ComponentArchive.TarGzip
        };

    private static bool UsesXz(ManagedToolRelease release) => Format(release) == ComponentArchive.TarXz;

    internal static async Task<bool> IsInstalledAsync(string store, ManagedToolRelease release, CancellationToken ct)
    {
        string location = Location(store, release);
        CheckDirectoryPath(location);
        if (Directory.Exists(location))
        {
            await VerifyTreeAsync(location, release, ct);
            if (release.Id == "vcpkg-registry")
            {
                await ManagedVcpkgCheckout.VerifyAsync(location, release.Version, ct);
            }

            return true;
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
            await VerifyTreeAsync(files, release, ct);
            await ProbeAsync(release, files, runtime, ct);
            if (release.Id == "llvm")
            {
                ManagedLinuxSdk.RequireHostAbi();
                ComponentRequirement? requirement = ManagedComponentCatalog.Find(release.Id, release.Version)?
                    .Requires.SingleOrDefault(item => item.Id == "sysroot");
                ManagedToolRelease sdk = requirement is null ? ToolCatalog.SysrootRelease :
                    ManagedComponentCatalog.Find(requirement.Id, requirement.Version)?.Release ??
                    throw new InvalidDataException("The compiler's required sysroot is unavailable in the catalog.");
                if (!await IsInstalledAsync(store, sdk, ct))
                {
                    throw new InvalidDataException("The pinned Linux SDK must be installed before LLVM. Apply linux-clang.");
                }

                await ManagedLinuxSdk.CompileAndLinkAsync(files, Location(store, sdk), staging, ct);
            }

            await VerifyTreeAsync(files, release, ct);

            ct.ThrowIfCancellationRequested();
            Directory.Move(files, destination);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    internal static async Task<FileStream> LockAsync(string path, CancellationToken ct)
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

    internal static void EnsureDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Managed tool installation requires Linux.");
        }

        CheckDirectoryPath(path);
        _ = Directory.CreateDirectory(path, PrivateDirectory);
        CheckDirectoryPath(path);
    }

    internal static void CheckDirectoryPath(string path)
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

    internal static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException($"Managed tool path contains a symbolic link: {entry.FullName}");
        }
    }

    private static async Task ExtractAsync(ManagedToolRelease release, string archive, string root, CancellationToken ct)
    {
        await using FileStream input = File.OpenRead(archive);
        ComponentArchive format = Format(release);
        if (format == ComponentArchive.RawExecutable)
        {
            await WriteFileAsync(root, "bin/vcpkg", input, input.Length, true, ct);
            return;
        }

        if (format == ComponentArchive.ZipExecutable)
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

        if (format == ComponentArchive.TarXz)
        {
            string expanded = archive + ".tar";
            await ExpandXzAsync(archive, expanded, ct);
            await using FileStream tar = File.OpenRead(expanded);
            await ExtractTarAsync(tar, release, root, ct);
            return;
        }

        await using GZipStream gzip = new(input, CompressionMode.Decompress);
        await ExtractTarAsync(gzip, release, root, ct);
    }

    private static async Task ExpandXzAsync(string archive, string expanded, CancellationToken ct)
    {
        const string xz = "/usr/bin/xz";
        if (!File.Exists(xz))
        {
            throw new InvalidDataException("Managed LLVM and sysroot installation require /usr/bin/xz.");
        }

        ProcessStartInfo start = new(xz)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add("--decompress");
        start.ArgumentList.Add("--stdout");
        start.ArgumentList.Add("--memlimit-decompress=384MiB");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(archive);
        using Process process = Process.Start(start) ?? throw new IOException("Unable to start xz.");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
            await using FileStream output = new(expanded, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _ = await CopyBoundedAsync(process.StandardOutput.BaseStream, output, MaxExpandedBytes, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("xz decompression failed: " + await error);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out expanding the managed xz archive.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task ExtractTarAsync(Stream input, ManagedToolRelease release, string root, CancellationToken ct)
    {
        using TarReader tar = new(input, leaveOpen: true);
        string prefix = release.TarRoot ?? "";
        bool allowLinks = UsesXz(release);
        long size = 0;
        HashSet<string> entries = new(StringComparer.Ordinal);
        List<(string Name, string Target)> links = [];
        List<(string Name, string Target)> hardLinks = [];
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (release.Id == "vcpkg-registry" && entries.Count == 0 &&
                entry is PaxGlobalExtendedAttributesTarEntry global &&
                global.GlobalExtendedAttributes.Count == 1 &&
                global.GlobalExtendedAttributes.TryGetValue("comment", out string? commit) && commit == release.Version)
            {
                continue;
            }

            string name = EntryName(entry.Name);
            if (entry.EntryType == TarEntryType.Directory && name == prefix)
            {
                continue;
            }

            ValidateRelativePath(name, allowLinks);
            if (entries.Count >= MaxEntries || entry.Length > MaxExpandedBytes - size || !entries.Add(name))
            {
                throw new InvalidDataException("Tar release exceeds extraction bounds or contains duplicate paths.");
            }

            size += entry.Length;
            if (prefix.Length != 0 && !name.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Tar entry is outside the release root.");
            }

            string relative = prefix.Length == 0 ? name : name[(prefix.Length + 1)..];
            if (entry.EntryType == TarEntryType.Directory)
            {
                _ = Directory.CreateDirectory(Path.Combine(root, relative));
            }
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                await WriteFileAsync(root, relative, entry.DataStream ?? Stream.Null, entry.Length,
                    (entry.Mode & UnixFileMode.UserExecute) != 0, ct);
            }
            else if (allowLinks && entry.EntryType == TarEntryType.SymbolicLink)
            {
                links.Add((relative, NormalizeLink(root, relative, entry.LinkName)));
            }
            else if (allowLinks && entry.EntryType == TarEntryType.HardLink)
            {
                string target = EntryName(entry.LinkName);
                ValidateRelativePath(target, allowLinks);
                hardLinks.Add((relative, target));
            }
            else
            {
                throw new InvalidDataException($"Unexpected tar entry type: {entry.EntryType}.");
            }
        }

        // Files and directories are created before links. A link cannot redirect extraction.
        foreach ((string relative, string target) in hardLinks)
        {
            FileInfo source = new(Path.Combine(root, target));
            if (!source.Exists || source.Length > MaxExpandedBytes - size)
            {
                throw new InvalidDataException($"Invalid or oversized hard link target: {target}.");
            }

            size += source.Length;
            await using FileStream contents = source.OpenRead();
            await WriteFileAsync(root, relative, contents, source.Length, IsExecutable(source.FullName), ct);
        }

        if (release.Id == "llvm")
        {
            // llvm-ar dispatches ranlib mode by argv[0]. Chromium does not ship this alias.
            links.Add(("bin/llvm-ranlib", "llvm-ar"));
        }

        HashSet<string> linkPaths = new(links.Select(link => link.Name), StringComparer.Ordinal);
        foreach ((string relative, string target) in links)
        {
            ct.ThrowIfCancellationRequested();
            for (string? parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent);
                 parent = Path.GetDirectoryName(parent))
            {
                if (linkPaths.Contains(parent))
                {
                    throw new InvalidDataException($"Cannot create an archive link below another link: {relative}.");
                }
            }

            string path = Path.Combine(root, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _ = File.CreateSymbolicLink(path, target);
        }
    }

    private static string EntryName(string name) =>
        name.StartsWith("./", StringComparison.Ordinal) ? name[2..].TrimEnd('/') : name.TrimEnd('/');

    private static string NormalizeLink(string root, string relative, string target)
    {
        if (target.Length == 0 || Path.IsPathRooted(target) || target.Any(char.IsControl) ||
            target.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsafe archive link: {relative} -> {target}");
        }

        string parent = Path.GetDirectoryName(Path.Combine(root, relative))!;
        string destination = Path.GetFullPath(target, parent);
        return IsDescendant(root, destination)
            ? Path.GetRelativePath(parent, destination)
            : throw new InvalidDataException($"Archive link escapes staging: {relative} -> {target}");
    }

    private static bool IsExecutable(string file) => !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;

    private static void ValidateRelativePath(string name, bool allowLinuxBackslash)
    {
        if (name.Length > 4096 || (!allowLinuxBackslash && name.Contains('\\', StringComparison.Ordinal)) || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl) ||
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
        if (await CopyBoundedAsync(source, output, size, ct) != size)
        {
            throw new InvalidDataException("Truncated archive entry.");
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            (executable ? UnixFileMode.UserExecute : UnixFileMode.None));
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream output, long size, CancellationToken ct)
    {
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

        return total;
    }

    private static async Task VerifyTreeAsync(string root, ManagedToolRelease release, CancellationToken ct)
    {
        string actual = await TreeHashAsync(root, UsesXz(release), release.Id == "vcpkg-registry", ct);
        if (actual != release.TreeSha256)
        {
            throw new InvalidDataException($"Managed {release.Id} tree failed SHA-256 verification: {root}. " +
                                           "Remove any damaged published installation while no workflows use it.");
        }
    }

    // The checkout Git directory is verified separately against the exact commit.
    // This digest covers the entire installed release, including CMake modules and executable bits.
    // It is pinned in the catalog independently of the archive digest.
    internal static async Task<string> TreeHashAsync(string root, bool allowLinks, bool excludeGit, CancellationToken ct)
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
                if (!allowLinks)
                {
                    RejectLink(entry);
                }

                if (entries.Count >= MaxEntries)
                {
                    throw new InvalidDataException("Managed release has too many entries.");
                }

                string relative = Path.GetRelativePath(root, entry.FullName);
                if (excludeGit && relative == ".git")
                {
                    continue;
                }

                ValidateRelativePath(relative, allowLinks);
                entries.Add(relative, entry);
                if (entry is DirectoryInfo child && entry.LinkTarget is null)
                {
                    pending.Push(child);
                }
            }
        }

        long total = 0;
        foreach ((string relative, FileSystemInfo entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (allowLinks && entry.LinkTarget is { } target)
            {
                if (NormalizeLink(root, relative, target) != target)
                {
                    throw new InvalidDataException($"Managed release contains a noncanonical link: {relative}.");
                }

                hash.AppendData(Encoding.UTF8.GetBytes($"l:{relative}:{target}\n"));
            }
            else if (entry is FileInfo file)
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
        if (release.Id == "sysroot")
        {
            ManagedLinuxSdk.CheckLayout(root);
            return;
        }

        if (release.Id == "vcpkg-registry")
        {
            await ManagedVcpkgCheckout.PrepareAsync(root, release.Version, ct);
            return;
        }

        if (release.Id == "vcpkg")
        {
            ToolCommandResult vcpkg = await RunProbeAsync(runtime, Executable(root, "vcpkg"), ["version"], ct);
            if (vcpkg.ExitCode != 0 || !vcpkg.Output.Contains("version " + release.Version + "-", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Managed vcpkg did not report pinned version {release.Version}.");
            }

            return;
        }

        string[] tools = release.Id switch
        {
            "llvm" => ["clang", "clang++", "ld.lld"],
            "cmake" => ["cmake", "ctest", "cpack"],
            _ => ["ninja"]
        };
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

        if (release.Id == "llvm")
        {
            ToolCommandResult target = await RunProbeAsync(runtime, Executable(root, "clang++"), ["-dumpmachine"], ct);
            if (target.ExitCode != 0 || target.Output.Trim() != ManagedLinuxSdk.TargetTriple)
            {
                throw new InvalidDataException("Managed Clang did not report the pinned Linux target triple.");
            }

            return;
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
