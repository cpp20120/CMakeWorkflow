using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>Creates an immutable experiment bundle on a Linux or macOS controller using Git.</summary>
public static class ExperimentPlanner
{
    /// <summary>Publishes plan.json and source.zip in a SHA-256-named directory under the supplied store.</summary>
    /// <remarks>Does not connect to the Host or install dependencies. Review the returned plan ID before execution.</remarks>
    public static Task<PreparedExperimentPlan> CreateAsync(ExperimentPlanOptions options, string storeDirectory,
        CancellationToken ct = default) => CreateAsync(options, storeDirectory, null, ct);

    internal sealed record BinaryPlanning(string? EnvironmentLockIdentity, Func<WorkflowDocument, byte[]> EncodeWorkflow,
        Func<VcpkgProjectPlan, byte[]> EncodeDependencyPlan)
    {
        internal ControllerPlanning? Controller { get; init; }
    }

    internal sealed record ControllerPlanning(string FileName,
        Func<ExperimentPlan, string, CancellationToken, Task<(string Id, byte[] Frame)>> EncodeAsync,
        Func<string, string, CancellationToken, Task<ExperimentPlan>> ReadAsync);

    internal static async Task<PreparedExperimentPlan> CreateAsync(ExperimentPlanOptions options, string storeDirectory,
        BinaryPlanning? native, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()))
        {
            throw new PlatformNotSupportedException("Plan an experiment from a Linux or macOS controller.");
        }

        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        ArgumentNullException.ThrowIfNull(options.BuildTargets);
        ArgumentNullException.ThrowIfNull(options.BenchmarkArguments);
        if ((options.ConfigurePreset is null && options.Configuration is null) ||
            (options.ConfigurePreset is not null && options.Vcpkg))
        {
            throw new ArgumentException("Select a configure preset, or specify a managed Clang configuration. Vcpkg requires managed Clang.", nameof(options));
        }
        bool needsNativeLock = native is not null && options.ConfigurePreset is null;
        if (needsNativeLock
            ? native?.EnvironmentLockIdentity is not { Length: 64 } identity || !identity.All(char.IsAsciiHexDigitLower)
            : native?.EnvironmentLockIdentity is not null)
        {
            throw new ArgumentException("Native managed experiments require an approved binary environment lock identity.", nameof(options));
        }
        string[] args = [.. options.BenchmarkArguments];
        string[] targets = [.. options.BuildTargets];
        if (options.Repeats is < 1 or > 200 ||
            args.Any(argument => argument is null || argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Benchmark repeats must be 1 to 200 and arguments must not contain NUL characters.", nameof(options));
        }
        ExperimentHost host = options.Host.Normalize();
        string root = Path.GetFullPath(storeDirectory);
        _ = Directory.CreateDirectory(root);
        string scratch = Path.Combine(root, ".experiment-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            ExperimentPlan plan = await BuildAsync(options.Repository, options.Revision, host, options.ConfigurePreset,
                options.Configuration, targets, options.BenchmarkTarget, options.Repeats,
                args, options.Profile, options.Vcpkg, native,
                Path.Combine(scratch, "source.zip"), ct);
            plan = plan with { Placement = options.Placement };
            ControllerPlanning? controller = native?.Controller;
            string file = controller?.FileName ?? "plan.json";
            string id;
            byte[] bytes;
            if (controller is not null)
            {
                plan = plan with { SchemaVersion = 4 };
                (id, bytes) = await controller.EncodeAsync(plan, root, ct);
            }
            else
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(plan, ExperimentPlanJson.Default.ExperimentPlan);
                id = ExperimentPlan.Approval(bytes, native is not null);
            }

            string destination = Path.Combine(root, id);
            await File.WriteAllBytesAsync(Path.Combine(scratch, file), bytes, ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(scratch, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                // A matching approval can already exist, including operation history.
                // Verify it without replacing either part of the approved bundle.
                string stored = Path.Combine(destination, file);
                ExperimentPlan existingPlan = controller is null
                    ? await ExperimentPlan.ReadAsync(stored, id, ct)
                    : await controller.ReadAsync(stored, id, ct);
                PreparedExperimentPlan existing = PreparedExperimentPlan.FromPlan(existingPlan, id, stored);
                await using FileStream archive = await ExperimentService.OpenVerifiedArchiveAsync(existing.Stage.Bundle!,
                    existing.ArchivePath, ct);
                return existing;
            }
            return PreparedExperimentPlan.FromPlan(plan, id, Path.Combine(destination, file));
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task<ExperimentPlan> BuildAsync(string repository, string revision, ExperimentHost host,
        string? preset, string? configuration, string[] targets, string? benchmark, int repeats,
        string[] arguments, bool profile, bool vcpkg, BinaryPlanning? native, string archivePath,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(repository, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != "https" && !(uri.IsFile && uri.Host.Length == 0 && repository.StartsWith("file:///", StringComparison.Ordinal))) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("Use a public HTTPS Git URL or an absolute file:/// Git URL.");
        }
        if (revision.Length is 0 or > 256 || revision.StartsWith('-') || revision.Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '/' or '.')) ||
            revision.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Ref must be a Git branch, tag, fully qualified ref, or commit SHA.");
        }

        if (preset is not null)
        {
            RequireLiteral(preset, "preset");
        }
        if (configuration is not null)
        {
            RequireLiteral(configuration, "configuration");
        }
        if (targets.Length > 16)
        {
            throw new ArgumentException("An experiment supports at most 16 explicit build targets.");
        }
        foreach (string target in targets)
        {
            RequireLiteral(target, "build target");
        }
        if (benchmark is not null)
        {
            RequireLiteral(benchmark, "benchmark target");
        }
        else if (profile || arguments.Length > 0)
        {
            throw new ArgumentException("--profile and --benchmark-arg require --benchmark-target.");
        }

        DirectoryInfo checkout = Directory.CreateTempSubdirectory("cmwf-git-");
        try
        {
            Dictionary<string, string?> environment = GetGitEnvironment();
            _ = await GitAsync(checkout.FullName, ["init", "--template="], environment, ct);
            _ = await GitAsync(checkout.FullName, ["fetch", "--depth=1", "--no-tags", "--no-recurse-submodules",
                "--", uri.AbsoluteUri, revision], environment, ct);
            string commit = await GitAsync(checkout.FullName, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], environment, ct);
            _ = await GitAsync(checkout.FullName, ["checkout", "--detach", commit], environment, ct);

            GitSourceSelection selected = await new GitSourceSelector("git", environment)
                .SelectAsync(checkout.FullName, ct: ct);
            if (selected.HeadCommit != commit || selected.DeletedTrackedCount != 0 ||
                selected.Files.Count > ExperimentSourceBundle.MaximumFiles)
            {
                throw new InvalidDataException("Incomplete, changed or oversized Git source selection.");
            }
            if (!selected.Files.Contains("CMakeLists.txt", StringComparer.Ordinal))
            {
                throw new InvalidDataException("The selected commit needs CMakeLists.txt at the repository root.");
            }
            if (preset is not null && !selected.Files.Contains("CMakePresets.json", StringComparer.Ordinal))
            {
                throw new InvalidDataException("Add a committed root CMakePresets.json with a Linux configure preset, or use --managed-clang --config Release.");
            }

            IReadOnlySet<string> executable = await VerifyCheckoutAsync(checkout.FullName, selected.Files, environment, ct);
            List<SnapshotFile> evidence = [];
            long total = 0;
            await using FileStream archive = File.Open(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using (ZipArchive zip = await ZipArchive.CreateAsync(archive, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: ct))
            {
                foreach (string relative in selected.Files.Order(StringComparer.Ordinal))
                {
                    string path = Path.Combine(checkout.FullName, relative);
                    FileInfo file = new(path);
                    if (file.LinkTarget is not null || file.Length > ExperimentSourceBundle.MaximumFileBytes ||
                        file.Length > ExperimentSourceBundle.MaximumSourceBytes - total)
                    {
                        throw new InvalidDataException($"Unsupported file or source size exceeds transfer limit: {relative}");
                    }
                    await using FileStream input = file.OpenRead();
                    ReadOnlyMemory<byte> lfsSignature = "version https://git-lfs.github.com/spec/v1"u8.ToArray();
                    if (input.Length >= lfsSignature.Length)
                    {
                        byte[] prefix = new byte[lfsSignature.Length];
                        await input.ReadExactlyAsync(prefix, ct);
                        if (prefix.AsSpan().SequenceEqual(lfsSignature.Span))
                        {
                            throw new InvalidDataException($"Git LFS pointers are unsupported: {relative}");
                        }
                    }
                    input.Position = 0;
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                    total += input.Length;
                    bool canExecute = executable.Contains(relative);
                    evidence.Add(new(relative, hash, input.Length) { Executable = canExecute });

                    input.Position = 0;
                    ZipArchiveEntry entry = zip.CreateEntry(relative, CompressionLevel.NoCompression);
                    entry.LastWriteTime = DateTimeOffset.UnixEpoch.AddYears(10);
                    entry.ExternalAttributes = canExecute ? ExperimentSourceBundle.ExecutableFile : ExperimentSourceBundle.RegularFile;
                    await using Stream output = await entry.OpenAsync(ct);
                    await input.CopyToAsync(output, ct);
                }
            }
            if (archive.Length > ExperimentSourceBundle.MaximumArchiveBytes)
            {
                throw new InvalidDataException("Source ZIP exceeds the archive size limit, including file metadata.");
            }
            archive.Position = 0;
            string archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct));
            string snapshotId = SnapshotIdentity.GetId(evidence);
            VcpkgProjectPlan? dependencies = vcpkg
                ? await VcpkgProjectPlanning.PlanSnapshotAsync(checkout.FullName, commit, snapshotId, ct)
                : null;
            string? environmentLock = native?.EnvironmentLockIdentity;
            if (native is not null && dependencies is not null)
            {
                dependencies = dependencies with { EnvironmentSha256 = environmentLock! };
            }
            string? dependencyDigest = dependencies is null ? null : native is not null
                ? ExperimentPlan.NativeDependencyIdentity(native.EncodeDependencyPlan(dependencies))
                : ManagedEnvironmentIdentity.Hash(VcpkgProjectPlanning.Serialize(dependencies));
            WorkflowDocument document = CreateManifest(preset, configuration, targets, benchmark, repeats, arguments, profile,
                dependencyDigest, environmentLock ?? dependencies?.EnvironmentSha256);
            byte[]? frame = native?.EncodeWorkflow(document);
            string manifest = native is not null ? "" : JsonSerializer.Serialize(document, ExperimentPlanJson.Default.WorkflowDocument) + "\n";
            return manifest.Length <= ExperimentStageRequest.MaximumManifestCharacters &&
                (frame is null || frame.Length <= WorkflowManifestPreparation.MaximumBytes)
                ? new(native is not null ? 3 : 2, uri.AbsoluteUri, revision, commit, host, snapshotId,
                    ExperimentPlan.Hash(frame ?? Encoding.UTF8.GetBytes(manifest)), manifest, [])
                {
                    Bundle = new(archiveHash, archive.Length, evidence.Count),
                    DependencyPlanSha256 = dependencyDigest,
                    BinaryManifest = frame is null ? (ReadOnlyMemory<byte>?)null : (ReadOnlyMemory<byte>)frame
                }
                : throw new ArgumentException("Generated manifest is too large. Reduce benchmark arguments or repetitions.");
        }
        finally
        {
            checkout.Delete(recursive: true);
        }
    }

    private static async Task<IReadOnlySet<string>> VerifyCheckoutAsync(string root, IReadOnlyList<string> files,
        Dictionary<string, string?> environment, CancellationToken ct)
    {
        // Compare the checked-out bytes to index blob IDs. Attributes such as eol,
        // ident, and working-tree-encoding can otherwise change the approved source.
        string index = await GitAsync(root, ["ls-files", "--stage", "-z"], environment, ct);
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        HashSet<string> executable = new(StringComparer.Ordinal);
        foreach (string entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t', StringComparison.Ordinal);
            string[] fields = entry[..tab].Split(' ');
            string path = entry[(tab + 1)..];
            if (fields[0] is not ("100644" or "100755"))
            {
                throw new InvalidDataException($"Unsupported Git mode {fields[0]}: {path}. Experiment snapshots require regular files.");
            }
            if (path.Length > 1024)
            {
                throw new InvalidDataException($"Experiment source path exceeds 1024 characters: {path}");
            }
            ids.Add(path, fields[1]);
            if (fields[0] == "100755")
            {
                _ = executable.Add(path);
            }
        }
        foreach (string[] batch in files.Chunk(32))
        {
            string hashes = await GitAsync(root, ["hash-object", "--no-filters", "--", .. batch], environment, ct);
            if (!hashes.Split('\n', StringSplitOptions.TrimEntries).SequenceEqual(batch.Select(path => ids[path])))
            {
                throw new InvalidDataException("Git checkout differs from committed blobs. Remove checkout-transforming attributes or use a revision with byte-identical checkout.");
            }
        }
        return executable;
    }

    private static WorkflowDocument CreateManifest(string? preset, string? configuration, string[] targets,
        string? benchmark, int repeats, string[] arguments, bool profile, string? dependencyDigest, string? environmentDigest)
    {
        const string context = "experiment";
        WorkflowContext build = new()
        {
            BuildDirectory = "build",
            SourceDirectory = "source",
            ConfigurePreset = preset,
            Configuration = configuration,
            Mode = "managed",
            Toolchain = preset is null
                ? new WorkflowToolchainBinding(EnvironmentProfileId: "linux-managed-clang", Generator: "Ninja Multi-Config")
                {
                    VcpkgProjectSha256 = dependencyDigest,
                    EnvironmentLockSha256 = environmentDigest
                }
                : null
        };
        WorkflowDocument document = new() { SchemaVersion = 2, Contexts = { [context] = build } };
        document.Nodes.Add(new() { Id = "configure", Kind = "configure", Context = context });
        string prerequisite = "configure";
        foreach (string target in targets.Length == 0 ? ["all"] : targets)
        {
            string id = "build_" + document.Nodes.Count.ToString(CultureInfo.InvariantCulture);
            document.Nodes.Add(new() { Id = id, Kind = "invoke", Context = context, Target = target, DependsOn = [prerequisite] });
            prerequisite = id;
        }
        document.Nodes.Add(new() { Id = "ctest", Kind = "ctest", Context = context, DependsOn = [prerequisite] });
        if (benchmark is not null)
        {
            List<WorkflowNode> benchmarkNodes = [];
            BenchmarkWorkflowNodes.Add(benchmarkNodes, benchmark, repeats, arguments, 60, null, false);
            WorkflowNode binary = benchmarkNodes[0];
            binary.Id = "benchmark_build";
            binary.Context = context;
            binary.DependsOn = ["ctest"];
            foreach (WorkflowNode timing in benchmarkNodes.Skip(1))
            {
                timing.From = binary.Id;
                if (timing.DependsOn[0] == "build")
                {
                    timing.DependsOn[0] = binary.Id;
                }
            }
            document.Nodes.AddRange(benchmarkNodes);
            if (profile)
            {
                document.Nodes.Add(new()
                {
                    Id = "cpu_profile",
                    Kind = "profile",
                    From = binary.Id,
                    DependsOn = [document.Nodes[^1].Id],
                    Arguments = [.. arguments],
                    Options = { ["mode"] = "cpu" },
                    TimeoutSeconds = 60
                });
            }
        }
        return document;
    }

    private static Dictionary<string, string?> GetGitEnvironment()
    {
        // Clear inherited Git overrides (including config/alternate object and index paths).
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Where(name => name.StartsWith("GIT_", StringComparison.Ordinal)))
        {
            environment[name] = null;
        }
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        environment["GIT_ATTR_NOSYSTEM"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_ALLOW_PROTOCOL"] = "https:file";
        return environment;
    }

    private static async Task<string> GitAsync(string root, string[] arguments,
        Dictionary<string, string?> environment, CancellationToken ct)
    {
        CommandResult result = await CommandExecution.RunAsync("git",
            ["-c", "core.hooksPath=/dev/null", "-c", "core.attributesFile=/dev/null", .. arguments],
            root, TimeSpan.FromMinutes(2), environment, ct);
        return result.TimedOut || result.ExitCode != 0
            ? throw new IOException($"Git {arguments[0]} failed. Verify the URL, ref, and repository access. {result.Stderr?.Trim()}")
            : (result.Stdout ?? "").Trim();
    }

    private static void RequireLiteral(string? value, string description)
    {
        if (value is not { Length: > 0 and <= 128 } || value.StartsWith('-') ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '+')))
        {
            throw new ArgumentException($"Invalid experiment {description}: {value}");
        }
    }
}


[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExperimentPlan))]
[JsonSerializable(typeof(WorkflowDocument))]
internal sealed partial class ExperimentPlanJson : JsonSerializerContext;
