using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed record VcpkgProjectPlan(int SchemaVersion, string Directory, string ManifestSha256,
    string? ConfigurationSha256, string EnvironmentSha256, ManagedToolRelease Executable, ManagedToolRelease Checkout);

internal sealed record VcpkgProjectReceipt(VcpkgProjectPlan Plan, string InstalledTreeSha256, string ToolchainSha256);

public sealed record ManagedVcpkgInstallation(string ManifestRoot, string InstalledDirectory, string VcpkgToolchain,
    string ChainloadToolchain, string Triplet, string PackageTreeSha256);

public static class VcpkgProjects
{
    internal const string Triplet = "x64-linux-cmwf";
    private const string ReceiptName = "receipt.json";

    internal static byte[] Serialize(VcpkgProjectPlan plan) =>
        JsonSerializer.SerializeToUtf8Bytes(plan, ManagedEnvironmentJson.Default.VcpkgProjectPlan);

    internal static async Task<VcpkgProjectPlan> PlanAsync(string project, CancellationToken ct)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        byte[] manifest = await ReadManifestAsync(Path.Combine(root, "vcpkg.json"), ct);
        using JsonDocument document = JsonDocument.Parse(manifest);
        JsonElement properties = document.RootElement;
        if (properties.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected a vcpkg manifest object.");
        }

        if (properties.TryGetProperty("builtin-baseline", out JsonElement baseline) &&
            baseline.GetString() != ToolCatalog.VcpkgCommit)
        {
            throw new InvalidDataException($"The pinned checkout supports builtin-baseline {ToolCatalog.VcpkgCommit}. " +
                                           "Use an explicit pinned Git registry for another baseline.");
        }

        if (properties.TryGetProperty("vcpkg-configuration", out JsonElement embedded))
        {
            ValidateConfiguration(embedded);
        }

        string path = Path.Combine(root, "vcpkg-configuration.json");
        byte[]? config = File.Exists(path) ? await ReadManifestAsync(path, ct) : null;
        if (config is not null)
        {
            using JsonDocument configured = JsonDocument.Parse(config);
            ValidateConfiguration(configured.RootElement);
        }

        return new(1, root, ManagedEnvironmentLocks.Hash(manifest),
            config is null ? null : ManagedEnvironmentLocks.Hash(config),
            ManagedEnvironmentLocks.Hash(ManagedEnvironmentLocks.Serialize()), ToolCatalog.VcpkgRelease, ToolCatalog.VcpkgRegistry);
    }

    private static void ValidateConfiguration(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || config.TryGetProperty("overlay-ports", out _) ||
            config.TryGetProperty("overlay-triplets", out _))
        {
            throw new InvalidDataException("Managed vcpkg projects need versioned registries and the pinned Linux triplet. Overlay paths are unsupported.");
        }

        if (config.TryGetProperty("default-registry", out JsonElement registry) && registry.ValueKind != JsonValueKind.Null)
        {
            ValidateRegistry(registry);
        }

        if (config.TryGetProperty("registries", out JsonElement registries))
        {
            foreach (JsonElement entry in registries.EnumerateArray())
            {
                ValidateRegistry(entry);
            }
        }
    }

    private static void ValidateRegistry(JsonElement registry)
    {
        if (!registry.TryGetProperty("kind", out JsonElement kind))
        {
            throw new InvalidDataException("A vcpkg registry must declare its kind.");
        }

        string? source = kind.GetString();
        string? baseline = registry.TryGetProperty("baseline", out JsonElement identity) ? identity.GetString() : null;
        if (baseline is not { Length: 40 } || !baseline.All(char.IsAsciiHexDigitLower) ||
            source is not ("git" or "builtin") || (source == "builtin" && baseline != ToolCatalog.VcpkgCommit))
        {
            throw new InvalidDataException("Use a Git registry with an exact commit baseline or the managed builtin checkout.");
        }
    }

    internal static async Task InstallAsync(VcpkgProjectPlan plan, string digest, CancellationToken ct)
    {
        string store = ManagedToolStore.GetRoot();
        string paths = Path.Combine(store, "locks");
        ManagedToolStore.EnsureDirectory(paths);
        string lockPath = Path.Combine(paths, "vcpkg-" + digest + ".lock");
        ManagedToolStore.RejectLink(new FileInfo(lockPath));
        await using FileStream lease = await ManagedToolStore.LockAsync(lockPath, ct);
        if (plan != await PlanAsync(plan.Directory, ct) || ManagedEnvironmentLocks.Hash(Serialize(plan)) != digest)
        {
            throw new InvalidDataException("The vcpkg project plan changed after approval.");
        }

        string candidate = InstallationPath(store, digest);
        ManagedToolStore.CheckDirectoryPath(candidate);
        if (File.Exists(Path.Combine(candidate, ReceiptName)))
        {
            _ = await ResolveAsync(digest, ct);
            return;
        }

        ToolingPlan tools = await new ToolingService(new SystemToolingRuntime(), new VerifiedCMakeInstaller())
            .PlanAsync(["linux-clang", "vcpkg"], PackageManagerKind.None, false, null, ct,
                resolutionPolicy: ToolResolutionPolicy.ManagedOnly);
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
            string snapshot = await StageManifestAsync(plan, candidate, ct);
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
            SystemToolingRuntime runtime = new(plan.Directory);
            ToolCommandResult result = await runtime.RunAsync(Tool("vcpkg"),
                ["install", "--x-manifest-root=" + snapshot, "--x-install-root=" + installed,
                    "--x-buildtrees-root=" + Path.Combine(candidate, "buildtrees"),
                    "--x-packages-root=" + Path.Combine(candidate, "packages"),
                    "--downloads-root=" + downloads, "--triplet=" + Triplet, "--host-triplet=" + Triplet,
                    "--overlay-triplets=" + overlay, "--no-print-usage"], TimeSpan.FromMinutes(15), environment, ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidDataException("vcpkg manifest installation failed:\n" + result.Output + "\n" + result.Error);
            }

            if (plan != await PlanAsync(plan.Directory, ct))
            {
                throw new InvalidDataException("The vcpkg manifest changed while dependencies were being installed.");
            }

            _ = Directory.CreateDirectory(installed);
            string tree = await ManagedToolStore.TreeHashAsync(installed, allowLinks: true, excludeGit: false, ct);
            string script = await FileHashAsync(chainload, ct);
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new VcpkgProjectReceipt(plan, tree, script),
                ManagedEnvironmentJson.Default.VcpkgProjectReceipt);
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

    public static async Task<ManagedVcpkgInstallation> ResolveAsync(string digest, CancellationToken ct)
    {
        ManagedEnvironmentLocks.RequireDigest(digest);
        string store = ManagedToolStore.GetRoot();
        string path = InstallationPath(store, digest);
        ManagedToolStore.CheckDirectoryPath(path);
        string receiptPath = Path.Combine(path, ReceiptName);
        if (!File.Exists(receiptPath))
        {
            throw new InvalidDataException($"vcpkg project installation {digest} is missing. Review tooling vcpkg plan and explicitly apply it.");
        }

        byte[] bytes = await ReadManifestAsync(receiptPath, ct);
        VcpkgProjectReceipt receipt = JsonSerializer.Deserialize(bytes, ManagedEnvironmentJson.Default.VcpkgProjectReceipt)
            ?? throw new InvalidDataException("Invalid vcpkg project receipt.");
        if (ManagedEnvironmentLocks.Hash(Serialize(receipt.Plan)) != digest ||
            receipt.Plan != await PlanAsync(receipt.Plan.Directory, ct))
        {
            throw new InvalidDataException("The vcpkg project manifest, registry, or toolchain identity changed. Review a new dependency plan.");
        }

        if (!await ManagedToolStore.IsInstalledAsync(store, ToolCatalog.VcpkgRegistry, ct))
        {
            throw new InvalidDataException("The approved vcpkg registry checkout is missing.");
        }

        string installed = Path.Combine(path, "installed");
        string chainload = Path.Combine(path, "toolchain.cmake");
        ManagedToolStore.CheckDirectoryPath(installed);
        string tree = await ManagedToolStore.TreeHashAsync(installed, allowLinks: true, excludeGit: false, ct);
        return tree == receipt.InstalledTreeSha256 && await FileHashAsync(chainload, ct) == receipt.ToolchainSha256
            ? new(receipt.Plan.Directory, installed,
                Path.Combine(ManagedToolStore.Location(store, ToolCatalog.VcpkgRegistry), "scripts", "buildsystems", "vcpkg.cmake"),
                chainload, Triplet, tree)
            : throw new InvalidDataException("The installed vcpkg project files failed SHA-256 verification.");
    }

    private static async Task<string> StageManifestAsync(VcpkgProjectPlan plan, string candidate, CancellationToken ct)
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
            byte[] bytes = await ReadManifestAsync(Path.Combine(plan.Directory, name), ct);
            if (ManagedEnvironmentLocks.Hash(bytes) != expected)
            {
                throw new InvalidDataException("The vcpkg manifest changed while preparing the approved install.");
            }

            await File.WriteAllBytesAsync(Path.Combine(snapshot, name), bytes, ct);
        }
    }

    private static string InstallationPath(string store, string digest) => Path.Combine(store, "vcpkg", "projects", digest);

    private static async Task<byte[]> ReadManifestAsync(string path, CancellationToken ct)
    {
        FileInfo file = new(path);
        ManagedToolStore.RejectLink(file);
        return file.Exists && file.Length <= 1024 * 1024
            ? await File.ReadAllBytesAsync(path, ct)
            : throw new InvalidDataException($"Expected a manifest of at most 1 MiB at {path}.");
    }

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
