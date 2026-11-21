using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Commands;

internal sealed record ExperimentHost(string Destination, string Workspace, string Endpoint, string Executable,
    string? Username, string? KeyFile, string? HostKeySha256, int Port)
{
    internal Task<CMakeWorkflowClient> ConnectAsync(CancellationToken ct) => Username is null
        ? CMakeWorkflowClient.ConnectSshAsync(new SshHostOptions(Destination, Endpoint, Workspace)
        { RemoteExecutable = Executable }, ct)
        : CMakeWorkflowClient.ConnectSshNetAsync(
            new SshNetHostOptions(Destination, Username, KeyFile!, HostKeySha256!, Endpoint, Workspace)
            { Port = Port, RemoteExecutable = Executable }, ct);
}

internal sealed record ExperimentPlan(int SchemaVersion, string Repository, string RequestedRef, string Commit,
    ExperimentHost Host, string SourceSnapshotId, string ManifestSha256, string Manifest,
    IReadOnlyList<ExperimentSourceFile> Files)
{
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed record ExperimentResult(int SchemaVersion, string ExperimentId, string Repository,
    string RequestedRef, string Commit, string SourceSnapshotId, string ManifestSha256, string Host,
    string Workspace, string PipelineOperationId, string ChildRequestId, string OperationId, string State,
    string ReportSha256, string RunJson, string HtmlReport, string HistoryStatus,
    IReadOnlyList<OperationArtifactInfo> Artifacts, JsonElement Run);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExperimentPlan))]
[JsonSerializable(typeof(ExperimentResult))]
internal sealed partial class ExperimentJson : JsonSerializerContext;

internal static class ExperimentPlanning
{
    internal static async Task<ExperimentPlan> CreateAsync(string repository, string revision, ExperimentHost host,
        string? preset, string? configuration, IReadOnlyList<string> targets, string? benchmark, int repeats,
        IReadOnlyList<string> arguments, bool profile, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Plan an experiment from a Linux or macOS controller.");
        }
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
        if (targets.Count > 16)
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
        else if (profile || arguments.Count > 0)
        {
            throw new ArgumentException("--profile and --benchmark-arg require --benchmark-target.");
        }

        string manifest = CreateManifest(preset, configuration, targets, benchmark, repeats, arguments, profile);
        if (manifest.Length > ExperimentStageRequest.MaximumManifestCharacters)
        {
            throw new ArgumentException("Generated manifest is too large. Reduce benchmark arguments or repetitions.");
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
                selected.Files.Count > ExperimentStageRequest.MaximumFiles)
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

            await VerifyCheckoutAsync(checkout.FullName, selected.Files, environment, ct);
            List<ExperimentSourceFile> files = [];
            List<SnapshotFile> evidence = [];
            long total = 0;
            foreach (string relative in selected.Files)
            {
                string path = Path.Combine(checkout.FullName, relative);
                FileInfo file = new(path);
                if (file.LinkTarget is not null || file.Length > ExperimentStageRequest.MaximumSourceBytes - total)
                {
                    throw new InvalidDataException($"Unsupported file or source exceeds 8 MiB: {relative}");
                }
                byte[] bytes = await File.ReadAllBytesAsync(path, ct);
                total += bytes.Length;
                if (total > ExperimentStageRequest.MaximumSourceBytes ||
                    bytes.AsSpan().StartsWith("version https://git-lfs.github.com/spec/v1"u8))
                {
                    throw new InvalidDataException($"Git LFS or oversized source is unsupported: {relative}");
                }
                files.Add(new(relative, Convert.ToBase64String(bytes)));
                evidence.Add(new(relative, ExperimentPlan.Hash(bytes), bytes.LongLength));
            }

            return new(1, uri.AbsoluteUri, revision, commit, host, LoreSnapshotStore.GetSnapshotId(evidence),
                ExperimentPlan.Hash(Encoding.UTF8.GetBytes(manifest)), manifest, files);
        }
        finally
        {
            checkout.Delete(recursive: true);
        }
    }

    private static async Task VerifyCheckoutAsync(string root, IReadOnlyList<string> files,
        Dictionary<string, string?> environment, CancellationToken ct)
    {
        // Compare the checked-out bytes to index blob IDs. Attributes such as eol,
        // ident, and working-tree-encoding can otherwise change the approved source.
        string index = await GitAsync(root, ["ls-files", "--stage", "-z"], environment, ct);
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach (string entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t', StringComparison.Ordinal);
            string[] fields = entry[..tab].Split(' ');
            string path = entry[(tab + 1)..];
            if (fields[0] != "100644")
            {
                throw new InvalidDataException($"Unsupported Git mode {fields[0]}: {path}. Experiment snapshots require non-executable regular files.");
            }
            if (path.Length > 1024)
            {
                throw new InvalidDataException($"Experiment source path exceeds 1024 characters: {path}");
            }
            ids.Add(path, fields[1]);
        }
        foreach (string[] batch in files.Chunk(32))
        {
            string hashes = await GitAsync(root, ["hash-object", "--no-filters", "--", .. batch], environment, ct);
            if (!hashes.Split('\n', StringSplitOptions.TrimEntries).SequenceEqual(batch.Select(path => ids[path])))
            {
                throw new InvalidDataException("Git checkout differs from committed blobs. Remove checkout-transforming attributes or use a revision with byte-identical checkout.");
            }
        }
    }

    private static string CreateManifest(string? preset, string? configuration, IReadOnlyList<string> targets,
        string? benchmark, int repeats, IReadOnlyList<string> arguments, bool profile)
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
                : null
        };
        WorkflowDocument document = new() { SchemaVersion = 2, Contexts = { [context] = build } };
        document.Nodes.Add(new() { Id = "configure", Kind = "configure", Context = context });
        string prerequisite = "configure";
        foreach (string target in targets.Count == 0 ? ["all"] : targets)
        {
            string id = "build_" + document.Nodes.Count.ToString(CultureInfo.InvariantCulture);
            document.Nodes.Add(new() { Id = id, Kind = "invoke", Context = context, Target = target, DependsOn = [prerequisite] });
            prerequisite = id;
        }
        document.Nodes.Add(new() { Id = "ctest", Kind = "ctest", Context = context, DependsOn = [prerequisite] });
        if (benchmark is not null)
        {
            WorkflowDocument recipe = BenchmarkWorkflowBuilder.Build(new BenchmarkRecipeV1(
                "benchmark", 1, Path.GetFullPath("build"), benchmark, configuration, repeats, 60, [.. arguments]));
            WorkflowNode binary = recipe.Nodes[0];
            binary.Id = "benchmark_build";
            binary.Context = context;
            binary.DependsOn = ["ctest"];
            foreach (WorkflowNode timing in recipe.Nodes.Skip(1))
            {
                timing.From = binary.Id;
                if (timing.DependsOn[0] == "build")
                {
                    timing.DependsOn[0] = binary.Id;
                }
            }
            document.Nodes.AddRange(recipe.Nodes);
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
        return Encoding.UTF8.GetString(WorkflowManifestBytes.Serialize(document));
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

    private static void RequireLiteral(string value, string description)
    {
        if (value.Length is 0 or > 128 || value.StartsWith('-') ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '+')))
        {
            throw new ArgumentException($"Invalid experiment {description}: {value}");
        }
    }
}
