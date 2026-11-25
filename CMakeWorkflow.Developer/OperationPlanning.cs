using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Profiling.Workflows;

namespace CMakeWorkflow.Developer;

public static partial class DeveloperLifecycle
{
    public static async Task<DeveloperOperationPlan> PlanOperationAsync(DeveloperOperationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOperation(request);
        DiscoveredProject project = await DiscoverProjectAsync(request.Location, ct);
        if (!File.Exists(BuildVariantConfiguration.CheckedPath(project.Directory, "cmwf.config.json")))
        {
            throw new InvalidDataException("This project has no variant configuration. Use its CMake presets or workflow commands.");
        }
        (VariantLock evidence, ConfigurationPlan configuration) = await VerifyConfigurationEvidenceAsync(project.Directory, request.Configuration, ct);
        byte[] sdkLock = await ProjectSdkResolution.ReadVerifiedAsync(project.Directory, ct);
        if (BuildVariantConfiguration.Hash(sdkLock) != evidence.SdkSha256)
        {
            throw new InvalidDataException("The SDK lock changed while planning the operation.");
        }
        using JsonDocument sdkDocument = JsonDocument.Parse(sdkLock);
        SdkDescriptor sdk = ProjectSdkResolution.Descriptor(sdkDocument.RootElement);
        WorkflowDocument graph = ProjectConfiguration.CreateWorkflow(evidence, configuration.Lock.Sha256, request.Configuration.Variant);
        ProjectVariant[] variants = [.. configuration.Variants.Where(variant => graph.Contexts.ContainsKey(variant.Name))];
        ScaffoldIdentity? scaffold = await ReadScaffoldIdentityAsync(project.Directory, ct);
        ComposeOperation(graph, request, sdk, scaffold, evidence, variants);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(graph, WorkflowJson.Default.WorkflowDocument);
        string digest = BuildVariantConfiguration.Hash(bytes);
        string manifestName = ".cmwf-developer-" + request.Kind.ToString() + "-" + digest + ".json";
        return new(project.Directory, request.Kind, manifestName, digest, bytes, configuration.Lock, Array.AsReadOnly(variants))
        {
            SdkId = sdk.Id,
            SdkVersion = sdk.Version
        };
    }

    private static async Task<ScaffoldIdentity?> ReadScaffoldIdentityAsync(string root, CancellationToken ct)
    {
        string path = BuildVariantConfiguration.CheckedPath(root, "cmwf.developer.json");
        if (!File.Exists(path))
        {
            return null;
        }
        ScaffoldIdentity? identity = JsonSerializer.Deserialize(await BuildVariantConfiguration.ReadBoundedAsync(path, ct),
            DeveloperJson.Default.ScaffoldIdentity);
        return identity is { SchemaVersion: 1, Name: { } name } && ValidTarget(name)
            ? identity : throw new InvalidDataException("Invalid cmwf.developer.json scaffold identity.");
    }

    private static void ValidateOperation(DeveloperOperationRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || (request.Target is not null && !ValidTarget(request.Target)) ||
            request.Arguments.Any(argument => argument is null || argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Invalid operation, target, or literal program arguments.", nameof(request));
        }
        if ((request.Kind != DeveloperOperationKind.Test && (request.TestRegex is not null || request.LabelRegex is not null)) ||
            (request.Kind is not (DeveloperOperationKind.Run or DeveloperOperationKind.Benchmark or DeveloperOperationKind.Profile)
                && request.Arguments.Count != 0) ||
            (request.Kind is DeveloperOperationKind.Configure or DeveloperOperationKind.Pack && request.Target is not null))
        {
            throw new ArgumentException("The arguments or target do not apply to this operation.", nameof(request));
        }
        if (request.Kind == DeveloperOperationKind.Benchmark && request.Repeats is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Benchmark repeats must be between 1 and 1000.");
        }
        if (((request.ProfileBackend is not null || request.ProfileMode is not null) && request.Kind != DeveloperOperationKind.Profile) ||
            request.ProfileBackend is not (null or "auto" or "perf" or "etw" or "instruments") ||
            request.ProfileMode is not (null or "cpu" or "scheduler" or "counters" or "diagnostic"))
        {
            throw new ArgumentException("Invalid profile backend or mode.", nameof(request));
        }
    }

    private static bool ValidTarget(string value) => value.Length > 0 && value[0] != '-' &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '+' or '-');

    private static void ComposeOperation(WorkflowDocument graph, DeveloperOperationRequest request, SdkDescriptor sdk,
        ScaffoldIdentity? scaffold, VariantLock evidence, ProjectVariant[] variants)
    {
        graph.Nodes.Clear();
        string? target = request.Target;
        bool runtimeBenchmark = request.Kind == DeveloperOperationKind.Benchmark && sdk.Id == "Native.Runtime" && scaffold is not null;
        if (request.Kind is DeveloperOperationKind.Run or DeveloperOperationKind.Benchmark or DeveloperOperationKind.Profile)
        {
            target ??= scaffold is null ? null : request.Kind switch
            {
                DeveloperOperationKind.Run when sdk.ProjectKind == "application" => scaffold.Name,
                DeveloperOperationKind.Benchmark when runtimeBenchmark => scaffold.Name + "_bench",
                DeveloperOperationKind.Profile when sdk.Capabilities.Profiling =>
                    sdk.BuildTarget.Replace("@NAME@", scaffold.Name, StringComparison.Ordinal),
                DeveloperOperationKind.Configure or DeveloperOperationKind.Build or DeveloperOperationKind.Test or
                    DeveloperOperationKind.Run or DeveloperOperationKind.Benchmark or DeveloperOperationKind.Profile or DeveloperOperationKind.Pack => null,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            if (target is null)
            {
                throw new InvalidDataException("Specify an executable CMake target with --target.");
            }
        }
        if (request.Kind == DeveloperOperationKind.Pack &&
            (!sdk.Capabilities.Packaging || evidence.Variants.Any(variant =>
                graph.Contexts.ContainsKey(variant.Name) && variant.Effective.Instrumentation != VariantInstrumentation.None)))
        {
            throw new InvalidDataException("The SDK or selected instrumentation does not support packaging.");
        }

        foreach (ProjectVariant variant in variants)
        {
            string prefix = variant.Name + "-";
            graph.Nodes.Add(new() { Id = prefix + "configure", Kind = "configure", Context = variant.Name });
            if (request.Kind == DeveloperOperationKind.Configure)
            {
                continue;
            }
            if (request.Kind == DeveloperOperationKind.Benchmark)
            {
                if (graph.Nodes.Count + request.Repeats + 1 > 1000)
                {
                    throw new InvalidDataException("The operation exceeds the workflow node limit. Reduce variants or repeats.");
                }
                BenchmarkMetrics? metrics = runtimeBenchmark && target == scaffold!.Name + "_bench"
                    ? new("stdout", "json", [new("elapsedNs", "elapsed", "ns", "lower-is-better")]) : null;
                BenchmarkTrial trial = new(variants[0].Name, "sequential", [new("configuration", "", variant.Name, null)]);
                BenchmarkWorkflowNodes.Add(graph.Nodes, target!, request.Repeats, request.Arguments,
                    60, metrics, managed: true, variant.Name, prefix, trial);
                continue;
            }
            bool executable = request.Kind is DeveloperOperationKind.Run or DeveloperOperationKind.Profile;
            graph.Nodes.Add(new()
            {
                Id = prefix + "build",
                Kind = executable ? "build" : "invoke",
                Context = variant.Name,
                Target = target ?? "all",
                DependsOn = [prefix + "configure"]
            });
            AddTerminalNode(graph, request, variant, prefix, scaffold);
        }
    }

    private static void AddTerminalNode(WorkflowDocument graph, DeveloperOperationRequest request, ProjectVariant variant,
        string prefix, ScaffoldIdentity? scaffold)
    {
        switch (request.Kind)
        {
            case DeveloperOperationKind.Configure:
            case DeveloperOperationKind.Build:
            case DeveloperOperationKind.Benchmark:
                break;
            case DeveloperOperationKind.Test:
                if (!variant.Testing)
                {
                    throw new InvalidDataException("Testing is disabled for variant " + variant.Name);
                }
                graph.Nodes.Add(new()
                {
                    Id = prefix + "test",
                    Kind = "ctest",
                    Context = variant.Name,
                    DependsOn = [prefix + "build"],
                    TestRegex = request.TestRegex,
                    LabelRegex = request.LabelRegex
                });
                break;
            case DeveloperOperationKind.Run:
                graph.Nodes.Add(new() { Id = prefix + "run", Kind = "run", From = prefix + "build", Arguments = [.. request.Arguments] });
                break;
            case DeveloperOperationKind.Profile:
                string build = prefix + "build";
                string backend = request.ProfileBackend ?? "auto";
                string mode = request.ProfileMode ?? "cpu";
                string capture = prefix + "profile";
                graph.Nodes.Add(ProfileWorkflowBuilder.ProfileNode(capture, build, build,
                    request.Arguments, 60, "60", backend, mode == "diagnostic" ? "cpu" : mode));
                if (mode == "diagnostic")
                {
                    graph.Nodes.Add(ProfileWorkflowBuilder.ProfileNode(prefix + "scheduler", build, capture,
                        request.Arguments, 60, "60", backend, "scheduler"));
                }
                break;
            case DeveloperOperationKind.Pack:
                if (scaffold is null)
                {
                    throw new InvalidDataException("No package output contract is declared for this project. Use its existing CPack preset.");
                }
                graph.Nodes.Add(new()
                {
                    Id = prefix + "pack",
                    Kind = "invoke",
                    Context = variant.Name,
                    Target = "package",
                    DependsOn = [prefix + "build"],
                    Outputs = [new() { Name = "package", Path = variant.BuildDirectory + "/cmwf-package.zip" }]
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}
