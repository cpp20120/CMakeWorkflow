using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed record VcpkgProjectReceipt(VcpkgProjectPlan Plan, string InstalledTreeSha256, string ToolchainSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadTreeSha256 { get; init; }
}

public sealed record ManagedVcpkgInstallation(string ManifestRoot, string InstalledDirectory, string VcpkgToolchain,
    string ChainloadToolchain, string Triplet, string PackageTreeSha256)
{
    public string? DownloadTreeSha256 { get; init; }
}

public static class VcpkgProjects
{
    internal const string Triplet = VcpkgProjectPlanning.Triplet;
    private const string ReceiptName = "receipt.json";

    internal static byte[] Serialize(VcpkgProjectPlan plan) => VcpkgProjectPlanning.Serialize(plan);
    internal static Task<VcpkgProjectPlan> PlanAsync(string project, CancellationToken ct) =>
        VcpkgProjectPlanning.PlanAsync(project, ct);
    internal static Task<VcpkgProjectPlan> PlanSnapshotAsync(string directory, string commit, string snapshotId,
        CancellationToken ct) => VcpkgProjectPlanning.PlanSnapshotAsync(directory, commit, snapshotId, ct);

    internal static Task InstallAsync(VcpkgProjectPlan plan, string digest, CancellationToken ct) =>
        InstallAsync(plan, digest, plan.Directory, ct);

    internal static async Task InstallAsync(VcpkgProjectPlan plan, string digest, string sourceRoot, CancellationToken ct)
    {
        ManagedEnvironmentLocks.RequireDigest(digest);
        string store = ManagedToolStore.GetRoot();
        string paths = Path.Combine(store, "locks");
        ManagedToolStore.EnsureDirectory(paths);
        string lockPath = Path.Combine(paths, "vcpkg-" + digest + ".lock");
        ManagedToolStore.RejectLink(new FileInfo(lockPath));
        await using FileStream lease = await ManagedToolStore.LockAsync(lockPath, ct);
        if (plan != await VcpkgProjectPlanning.ReadProjectAsync(sourceRoot, plan.Commit, plan.SourceSnapshotId, ct) || ManagedEnvironmentLocks.Hash(Serialize(plan)) != digest)
        {
            throw new InvalidDataException("The vcpkg project plan changed after approval.");
        }

        string candidate = InstallationPath(store, digest);
        ManagedToolStore.CheckDirectoryPath(candidate);
        if (File.Exists(Path.Combine(candidate, ReceiptName)))
        {
            _ = await ResolveAsync(digest, plan.SourceSnapshotId, sourceRoot, ct);
            return;
        }

        ToolingPlan tools = await new ToolingService(new SystemToolingRuntime(), new VerifiedCMakeInstaller())
            .PlanAsync(["linux-clang", "vcpkg"], PackageManagerKind.None, false, null, ct,
                resolutionPolicy: ToolResolutionPolicy.ManagedOnly);
        if (plan.SchemaVersion == 2)
        {
            await ManagedEnvironmentLocks.VerifyAsync(plan.EnvironmentSha256, ct);
        }
        if (!tools.Ready)
        {
            throw new InvalidOperationException("Install the linux-clang and vcpkg profiles with explicit tooling setup approval first.");
        }

        string Tool(string id) => tools.Tools.Single(t => t.Id == id).Command!;
        if (Directory.Exists(candidate))
        {
            Directory.Delete(candidate, recursive: true);
        }

        ManagedToolStore.EnsureDirectory(candidate);
        try
        {
            string snapshot = await StageManifestAsync(plan, sourceRoot, candidate, ct);
            string chainload = Path.Combine(candidate, "toolchain.cmake");
            await File.WriteAllTextAsync(chainload, ToolchainScript(Tool), ct);
            string overlay = Path.Combine(candidate, "triplets");
            _ = Directory.CreateDirectory(overlay);
            await File.WriteAllTextAsync(Path.Combine(overlay, Triplet + ".cmake"),
                $"""
                 set(VCPKG_TARGET_ARCHITECTURE x64)
                 set(VCPKG_CRT_LINKAGE dynamic)
                 set(VCPKG_LIBRARY_LINKAGE static)
                 set(VCPKG_CMAKE_SYSTEM_NAME Linux)
                 set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE {CMakeLiteral(chainload)})
                 """, ct);

            string installed = Path.Combine(candidate, "installed");
            string downloads = Path.Combine(candidate, "downloads");
            _ = Directory.CreateDirectory(downloads);
            Dictionary<string, string> environment = InstallEnvironment(Tool);
            if (plan.SchemaVersion == 2)
            {
                environment["GIT_CONFIG_NOSYSTEM"] = "1";
                environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
                environment["GIT_ALLOW_PROTOCOL"] = "https";
            }
            SystemToolingRuntime runtime = new(snapshot);
            ToolCommandResult result = await runtime.RunAsync(Tool("vcpkg"),
                ["install", "--x-manifest-root=" + snapshot, "--x-install-root=" + installed,
                    "--x-buildtrees-root=" + Path.Combine(candidate, "buildtrees"),
                    "--x-packages-root=" + Path.Combine(candidate, "packages"),
                    "--downloads-root=" + downloads, "--triplet=" + Triplet, "--host-triplet=" + Triplet,
                    "--overlay-triplets=" + overlay, "--no-print-usage"], TimeSpan.FromMinutes(15), environment, ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidDataException(plan.SchemaVersion == 2
                    ? $"vcpkg exited with code {result.ExitCode}. Check approved registries, network and cache settings on the Host."
                    : "vcpkg manifest installation failed:\n" + result.Output + "\n" + result.Error);
            }

            if (plan != await VcpkgProjectPlanning.ReadProjectAsync(sourceRoot, plan.Commit, plan.SourceSnapshotId, ct))
            {
                throw new InvalidDataException("The vcpkg manifest changed while dependencies were being installed.");
            }

            _ = Directory.CreateDirectory(installed);
            string tree = await ManagedToolStore.TreeHashAsync(installed, allowLinks: true, excludeGit: false, ct);
            string script = await FileHashAsync(chainload, ct);
            VcpkgProjectReceipt evidence = new(plan, tree, script)
            {
                DownloadTreeSha256 = plan.SchemaVersion == 2
                    ? await ManagedToolStore.TreeHashAsync(downloads, allowLinks: false, excludeGit: false, ct)
                    : null
            };
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(evidence, ManagedEnvironmentJson.Default.VcpkgProjectReceipt);
            if (plan.SchemaVersion == 2)
            {
                receipt = await PinReceiptAsync(store, digest, evidence, receipt, ct);
            }
            string temporary = Path.Combine(candidate, "receipt.partial");
            await File.WriteAllBytesAsync(temporary, receipt, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(candidate, ReceiptName));
        }
        catch
        {
            Directory.Delete(candidate, recursive: true);
            throw;
        }
    }

    // Kept outside the install directory so retrying an incomplete or removed install
    // cannot silently publish a different package tree under an accepted workflow digest.
    private static async Task<byte[]> PinReceiptAsync(string store, string digest, VcpkgProjectReceipt evidence,
        byte[] receipt, CancellationToken ct)
    {
        string path = ReceiptPinPath(store, digest);
        if (!File.Exists(path))
        {
            string partial = path + ".partial";
            ManagedToolStore.RejectLink(new FileInfo(partial));
            await File.WriteAllBytesAsync(partial, receipt, ct);
            File.Move(partial, path);
        }
        byte[] pinned = await VcpkgProjectPlanning.ReadManifestAsync(path, ct);
        VcpkgProjectReceipt original = JsonSerializer.Deserialize(pinned, ManagedEnvironmentJson.Default.VcpkgProjectReceipt)
            ?? throw new InvalidDataException("Invalid pinned vcpkg receipt.");
        return original with { DownloadTreeSha256 = null } == evidence with { DownloadTreeSha256 = null }
            ? pinned : throw new InvalidDataException("Reinstall changed the approved vcpkg package or toolchain tree. Restore the approved installation.");
    }

    private static string ReceiptPinPath(string store, string digest)
    {
        string directory = Path.Combine(store, "locks");
        ManagedToolStore.CheckDirectoryPath(directory);
        return Path.Combine(directory, "vcpkg-" + digest + ".receipt.json");
    }

    private static async Task VerifyReceiptPinAsync(string path, byte[] receipt, CancellationToken ct)
    {
        if (!(await VcpkgProjectPlanning.ReadManifestAsync(path, ct)).AsSpan().SequenceEqual(receipt))
        {
            throw new InvalidDataException("vcpkg receipt differs from the first published package tree. Restore the approved installation.");
        }
    }

    internal static async Task<string> VerifySnapshotInstallationAsync(string digest, string snapshotId,
        string sourceRoot, CancellationToken ct)
    {
        _ = await ResolveAsync(digest, snapshotId, sourceRoot, ct);
        return ManagedEnvironmentLocks.Hash(ManagedEnvironmentLocks.Serialize());
    }

    public static Task<ManagedVcpkgInstallation> ResolveAsync(string digest, CancellationToken ct) =>
        ResolveAsync(digest, null, null, ct);

    internal static async Task<ManagedVcpkgInstallation> ResolveAsync(string digest, string? snapshotId,
        string? sourceRoot, CancellationToken ct)
    {
        ManagedEnvironmentLocks.RequireDigest(digest);
        string store = ManagedToolStore.GetRoot();
        string path = InstallationPath(store, digest);
        ManagedToolStore.CheckDirectoryPath(path);
        string receiptPath = Path.Combine(path, ReceiptName);
        if (!File.Exists(receiptPath))
        {
            throw new InvalidDataException($"vcpkg project installation {digest} is missing. " +
                (snapshotId is null ? "Review tooling vcpkg plan and explicitly apply it."
                    : "Review experiment deps plan and approve it with experiment deps apply."));
        }

        byte[] bytes = await VcpkgProjectPlanning.ReadManifestAsync(receiptPath, ct);
        VcpkgProjectReceipt receipt = JsonSerializer.Deserialize(bytes, ManagedEnvironmentJson.Default.VcpkgProjectReceipt)
            ?? throw new InvalidDataException("Invalid vcpkg project receipt.");
        VcpkgProjectPlan plan = receipt.Plan;
        string root = sourceRoot ?? plan.Directory;
        if (ManagedEnvironmentLocks.Hash(Serialize(plan)) != digest ||
            plan.SourceSnapshotId != snapshotId ||
            (snapshotId is not null && sourceRoot is null) ||
            plan != await VcpkgProjectPlanning.ReadProjectAsync(root, plan.Commit, snapshotId, ct))
        {
            throw new InvalidDataException("The vcpkg project manifest, registry, or toolchain identity changed. Review a new dependency plan.");
        }

        if (snapshotId is not null)
        {
            await VerifyReceiptPinAsync(ReceiptPinPath(store, digest), bytes, ct);
            await ManagedEnvironmentLocks.VerifyAsync(plan.EnvironmentSha256, ct);
        }
        if (!await ManagedToolStore.IsInstalledAsync(store, ToolCatalog.VcpkgRegistry, ct) ||
            (snapshotId is not null && !await ManagedToolStore.IsInstalledAsync(store, ToolCatalog.VcpkgRelease, ct)))
        {
            throw new InvalidDataException("The approved vcpkg registry checkout is missing.");
        }

        string installed = Path.Combine(path, "installed");
        string chainload = Path.Combine(path, "toolchain.cmake");
        ManagedToolStore.CheckDirectoryPath(installed);
        string tree = await ManagedToolStore.TreeHashAsync(installed, allowLinks: true, excludeGit: false, ct);
        return tree == receipt.InstalledTreeSha256 && await FileHashAsync(chainload, ct) == receipt.ToolchainSha256
            ? new(root, installed,
                Path.Combine(ManagedToolStore.Location(store, ToolCatalog.VcpkgRegistry), "scripts", "buildsystems", "vcpkg.cmake"),
                chainload, Triplet, tree)
            {
                DownloadTreeSha256 = receipt.DownloadTreeSha256
            }
            : throw new InvalidDataException("The installed vcpkg project files failed SHA-256 verification.");
    }

    private static async Task<string> StageManifestAsync(VcpkgProjectPlan plan, string sourceRoot, string candidate, CancellationToken ct)
    {
        string snapshot = Path.Combine(candidate, "manifest");
        _ = Directory.CreateDirectory(snapshot);
        await CopyAsync("vcpkg.json", plan.ManifestSha256);
        if (plan.ConfigurationSha256 is { } config)
        {
            await CopyAsync("vcpkg-configuration.json", config);
        }

        return snapshot;

        async Task CopyAsync(string name, string expected)
        {
            byte[] bytes = await VcpkgProjectPlanning.ReadManifestAsync(Path.Combine(sourceRoot, name), ct);
            if (ManagedEnvironmentLocks.Hash(bytes) != expected)
            {
                throw new InvalidDataException("The vcpkg manifest changed while preparing the approved install.");
            }

            await File.WriteAllBytesAsync(Path.Combine(snapshot, name), bytes, ct);
        }
    }

    private static string InstallationPath(string store, string digest) => Path.Combine(store, "vcpkg", "projects", digest);

    private static async Task<string> FileHashAsync(string path, CancellationToken ct)
    {
        ManagedToolStore.RejectLink(new FileInfo(path));
        await using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }

    private static string ToolchainScript(Func<string, string> tool)
    {
        string sysroot = tool("sysroot");
        string lld = tool("ld.lld");
        string bin = Path.GetDirectoryName(lld) ?? throw new InvalidDataException("Missing LLVM directory.");
        string flags = CMakeLiteral(ManagedLinuxSdk.DriverFlags(sysroot, lld));
        return $"""
            set(CMAKE_SYSTEM_NAME Linux)
            set(CMAKE_SYSTEM_PROCESSOR x86_64)
            set(CMAKE_SYSROOT {CMakeLiteral(sysroot)})
            set(CMAKE_C_COMPILER {CMakeLiteral(tool("clang"))})
            set(CMAKE_CXX_COMPILER {CMakeLiteral(tool("clang++"))})
            set(CMAKE_C_COMPILER_TARGET {ManagedLinuxSdk.TargetTriple})
            set(CMAKE_CXX_COMPILER_TARGET {ManagedLinuxSdk.TargetTriple})
            set(CMAKE_LINKER {CMakeLiteral(lld)})
            set(CMAKE_AR {CMakeLiteral(Path.Combine(bin, "llvm-ar"))})
            set(CMAKE_RANLIB {CMakeLiteral(Path.Combine(bin, "llvm-ranlib"))})
            string(APPEND CMAKE_C_FLAGS_INIT " " {flags})
            string(APPEND CMAKE_CXX_FLAGS_INIT " " {flags})
            """;
    }

    private static string CMakeLiteral(string value)
    {
        int width = 1;
        while (value.Contains("]" + new string('=', width) + "]", StringComparison.Ordinal))
        {
            width++;
        }

        string delimiter = new('=', width);
        return "[" + delimiter + "[" + value + "]" + delimiter + "]";
    }

    private static Dictionary<string, string> InstallEnvironment(Func<string, string> tool)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = string.Join(Path.PathSeparator, Path.GetDirectoryName(tool("cmake")),
                Path.GetDirectoryName(tool("ninja")), Path.GetDirectoryName(tool("clang")), "/usr/bin", "/bin"),
            ["VCPKG_ROOT"] = tool("vcpkg-registry"),
            ["VCPKG_DISABLE_METRICS"] = "1",
            ["VCPKG_FORCE_SYSTEM_BINARIES"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        foreach (string key in new[] { "HOME", "XDG_CACHE_HOME", "VCPKG_BINARY_SOURCES", "VCPKG_DEFAULT_BINARY_CACHE",
                     "X_VCPKG_ASSET_SOURCES", "SSH_AUTH_SOCK", "https_proxy", "HTTPS_PROXY", "no_proxy", "NO_PROXY", "SSL_CERT_FILE" })
        {
            if (System.Environment.GetEnvironmentVariable(key) is { } value)
            {
                environment.Add(key, value);
            }
        }

        return environment;
    }
}
