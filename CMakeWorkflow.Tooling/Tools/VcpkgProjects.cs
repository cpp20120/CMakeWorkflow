using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed record VcpkgProjectPlan(int SchemaVersion, string Directory, string ManifestSha256,
    string? ConfigurationSha256, string EnvironmentSha256, ManagedToolRelease Executable, ManagedToolRelease Checkout)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSnapshotId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetTriplet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostTriplet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Registries { get; init; }
}

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
    internal const string Triplet = "x64-linux-cmwf";
    private const string ReceiptName = "receipt.json";

    internal static byte[] Serialize(VcpkgProjectPlan plan) =>
        JsonSerializer.SerializeToUtf8Bytes(plan, ManagedEnvironmentJson.Default.VcpkgProjectPlan);

    internal static async Task<VcpkgProjectPlan> PlanAsync(string project, CancellationToken ct)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        return await ReadProjectAsync(root, null, null, ct);
    }

    internal static Task<VcpkgProjectPlan> PlanSnapshotAsync(string directory, string commit, string snapshotId,
        CancellationToken ct) => ReadProjectAsync(directory, commit, snapshotId, ct);

    private static async Task<VcpkgProjectPlan> ReadProjectAsync(string root, string? commit, string? snapshotId,
        CancellationToken ct)
    {
        bool pinned = snapshotId is not null;
        if (pinned && (snapshotId is not { Length: 64 } || !snapshotId.All(char.IsAsciiHexDigitUpper) ||
            commit is not { Length: 40 } || !commit.All(char.IsAsciiHexDigitLower)))
        {
            throw new InvalidDataException("Expected an immutable Git commit and source snapshot.");
        }
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

        bool hasBuiltin = properties.TryGetProperty("builtin-baseline", out _);
        bool hasEmbedded = properties.TryGetProperty("vcpkg-configuration", out JsonElement embedded);
        if (pinned && (properties.TryGetProperty("overlay-ports", out _) ||
                       properties.TryGetProperty("overlay-triplets", out _)))
        {
            throw new InvalidDataException("Experiment vcpkg overlays are unsupported.");
        }
        string path = Path.Combine(root, "vcpkg-configuration.json");
        ManagedToolStore.RejectLink(new FileInfo(path));
        byte[]? config = File.Exists(path) ? await ReadManifestAsync(path, ct) : null;
        using JsonDocument? configured = config is null ? null : JsonDocument.Parse(config);
        if (pinned && hasEmbedded && configured is not null)
        {
            throw new InvalidDataException("Use either embedded vcpkg configuration or vcpkg-configuration.json.");
        }
        if (hasEmbedded)
        {
            ValidateConfiguration(embedded, pinned, hasBuiltin);
        }
        if (configured is not null)
        {
            ValidateConfiguration(configured.RootElement, pinned, hasBuiltin);
        }
        return pinned && !hasBuiltin && !hasEmbedded && configured is null
            ? throw new InvalidDataException("Experiments require an explicit builtin-baseline or pinned default registry.")
            : new(pinned ? 2 : 1, pinned ? "" : root, ManagedEnvironmentLocks.Hash(manifest),
                config is null ? null : ManagedEnvironmentLocks.Hash(config),
                ManagedEnvironmentLocks.Hash(ManagedEnvironmentLocks.Serialize()), ToolCatalog.VcpkgRelease, ToolCatalog.VcpkgRegistry)
            {
                Commit = commit,
                SourceSnapshotId = snapshotId,
                TargetTriplet = pinned ? Triplet : null,
                HostTriplet = pinned ? Triplet : null,
                Registries = pinned ? DescribeRegistries(configured?.RootElement ?? embedded) : null
            };
    }

    private static string DescribeRegistries(JsonElement config)
    {
        List<string> identities = [];
        if (config.ValueKind == JsonValueKind.Object)
        {
            if (config.TryGetProperty("default-registry", out JsonElement primary))
            {
                identities.Add(primary.ValueKind == JsonValueKind.Null ? "default disabled" : Describe(primary));
            }
            if (config.TryGetProperty("registries", out JsonElement registries))
            {
                identities.AddRange(registries.EnumerateArray().Select(Describe));
            }
        }
        return identities.Count == 0 ? "builtin " + ToolCatalog.VcpkgCommit : string.Join("\n", identities);

        static string Describe(JsonElement registry) => registry.GetProperty("kind").GetString() == "git"
            ? $"git {registry.GetProperty("repository").GetString()} {registry.GetProperty("baseline").GetString()}"
            : "builtin " + ToolCatalog.VcpkgCommit;
    }

    private static void ValidateConfiguration(JsonElement config, bool pinned, bool hasBuiltin)
    {
        if (config.ValueKind != JsonValueKind.Object || config.TryGetProperty("overlay-ports", out _) ||
            config.TryGetProperty("overlay-triplets", out _))
        {
            throw new InvalidDataException("Managed vcpkg projects need versioned registries and the pinned Linux triplet. Overlay paths are unsupported.");
        }

        if (config.TryGetProperty("default-registry", out JsonElement registry))
        {
            if (registry.ValueKind != JsonValueKind.Null)
            {
                ValidateRegistry(registry, pinned);
            }
            else if (pinned && hasBuiltin)
            {
                throw new InvalidDataException("A disabled default registry conflicts with builtin-baseline.");
            }
        }
        else if (pinned && !hasBuiltin)
        {
            throw new InvalidDataException("Declare an explicit default registry or builtin-baseline.");
        }

        if (config.TryGetProperty("registries", out JsonElement registries))
        {
            foreach (JsonElement entry in registries.EnumerateArray())
            {
                ValidateRegistry(entry, pinned);
            }
        }
    }

    private static void ValidateRegistry(JsonElement registry, bool pinned)
    {
        if (registry.ValueKind != JsonValueKind.Object || !registry.TryGetProperty("kind", out JsonElement kind))
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
        if (pinned && source == "git")
        {
            if (!registry.TryGetProperty("repository", out JsonElement repository) ||
                repository.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(repository.GetString(), UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !registry.TryGetProperty("reference", out JsonElement reference) ||
                reference.ValueKind != JsonValueKind.String || reference.GetString() != baseline)
            {
                throw new InvalidDataException("Experiment Git registries require a credential-free HTTPS repository and reference equal to the baseline commit.");
            }
        }
    }

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
        if (plan != await ReadProjectAsync(sourceRoot, plan.Commit, plan.SourceSnapshotId, ct) || ManagedEnvironmentLocks.Hash(Serialize(plan)) != digest)
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

            if (plan != await ReadProjectAsync(sourceRoot, plan.Commit, plan.SourceSnapshotId, ct))
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
        byte[] pinned = await ReadManifestAsync(path, ct);
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
        if (!(await ReadManifestAsync(path, ct)).AsSpan().SequenceEqual(receipt))
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

        byte[] bytes = await ReadManifestAsync(receiptPath, ct);
        VcpkgProjectReceipt receipt = JsonSerializer.Deserialize(bytes, ManagedEnvironmentJson.Default.VcpkgProjectReceipt)
            ?? throw new InvalidDataException("Invalid vcpkg project receipt.");
        VcpkgProjectPlan plan = receipt.Plan;
        string root = sourceRoot ?? plan.Directory;
        if (ManagedEnvironmentLocks.Hash(Serialize(plan)) != digest ||
            plan.SourceSnapshotId != snapshotId ||
            (snapshotId is not null && sourceRoot is null) ||
            plan != await ReadProjectAsync(root, plan.Commit, snapshotId, ct))
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
            byte[] bytes = await ReadManifestAsync(Path.Combine(sourceRoot, name), ct);
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
