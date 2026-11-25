using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Kits;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Developer;

public static partial class DeveloperLifecycle
{
    private static async Task<DeveloperOperationPlan> PlanKitMatrixAsync(DeveloperOperationRequest request, CancellationToken ct)
    {
        ValidateOperation(request);
        if (request.Kind != DeveloperOperationKind.Benchmark || request.Configuration.Kit is not null ||
            request.KitMatrix.Count is < 2 or > 8 ||
            request.KitMatrix.Distinct(StringComparer.Ordinal).Count() != request.KitMatrix.Count ||
            (request.Repeats + 2) * request.KitMatrix.Count > 1000)
        {
            throw new ArgumentException("A benchmark Kit matrix requires 2–8 distinct Kits, no --kit, and at most 1000 nodes.", nameof(request));
        }

        DiscoveredProject project = await DiscoverProjectAsync(request.Location, ct);
        WorkflowDocument combined = new() { SchemaVersion = 2 };
        List<DeveloperKitMatrixEntry> selections = [];
        List<ProjectVariant> variants = [];
        DeveloperOperationPlan? first = null;
        KitPlatform? host = null;
        string? baseline = null;
        string? previousTrial = null;
        HashSet<string> buildTrees = new(StringComparer.Ordinal);
        foreach (string kit in request.KitMatrix)
        {
            EngineeringKitDefinition definition = await GetKitAsync(kit, project.Directory, ct);
            host ??= definition.ExecutionHost;
            if (host != definition.ExecutionHost)
            {
                throw new InvalidDataException("A Kit matrix requires Kits on the same execution platform.");
            }
            DeveloperOperationPlan branch = await PlanOperationCoreAsync(request with
            {
                Location = new(project.Directory) { ProjectDirectory = project.Directory },
                Configuration = request.Configuration with { Kit = kit },
                KitMatrix = []
            }, useActive: false, ct);
            first ??= branch;
            if (branch.SdkId != first.SdkId || branch.SdkVersion != first.SdkVersion)
            {
                throw new InvalidDataException("The SDK changed while composing the Kit matrix.");
            }

            WorkflowDocument graph = JsonSerializer.Deserialize(branch.ManifestJson.Span, WorkflowJson.Default.WorkflowDocument)!;
            if (combined.Nodes.Count + graph.Nodes.Count > 1000 || variants.Count + graph.Contexts.Count > 32)
            {
                throw new InvalidDataException("The Kit matrix exceeds 1000 nodes or 32 build variants.");
            }
            string prefix = "kit-" + selections.Count.ToString("D2", CultureInfo.InvariantCulture) + "-";
            foreach ((string key, WorkflowContext context) in graph.Contexts)
            {
                if (!buildTrees.Add(context.BuildDirectory))
                {
                    throw new InvalidDataException("The Kit matrix contains duplicate build environments.");
                }
                combined.Contexts.Add(prefix + key, context);
            }
            Dictionary<string, string> nodeIds = graph.Nodes.ToDictionary(node => node.Id, node => node.BenchmarkTrial is null
                ? prefix + node.Id
                : "timing_" + prefix + node.From![..^"-build".Length] + "_" + node.Id[(node.Id.LastIndexOf('_') + 1)..], StringComparer.Ordinal);
            foreach (WorkflowNode node in graph.Nodes)
            {
                node.Id = nodeIds[node.Id];
                node.Context = node.Context is { } context ? prefix + context : null;
                node.From = node.From is { } from ? nodeIds[from] : null;
                node.DependsOn = [.. node.DependsOn.Select(dependency => nodeIds[dependency])];
                if (node.BenchmarkTrial is { } trial)
                {
                    baseline ??= prefix + trial.BaselineVariant;
                    BenchmarkTrial matrixTrial = new(baseline, trial.ExecutionOrder, [new("kit", "", kit, null), .. trial.Axes]);
                    if (previousTrial is not null && !node.DependsOn.Contains(previousTrial, StringComparer.Ordinal))
                    {
                        node.DependsOn.Add(previousTrial);
                    }
                    previousTrial = node.Id;
                    combined.Nodes.Add(new()
                    {
                        Id = node.Id,
                        Kind = node.Kind,
                        From = node.From,
                        DependsOn = node.DependsOn,
                        Arguments = node.Arguments,
                        Metrics = node.Metrics,
                        BenchmarkTrial = matrixTrial,
                        TimeoutSeconds = node.TimeoutSeconds,
                        Resources = node.Resources,
                        Exclusive = true
                    });
                }
                else
                {
                    combined.Nodes.Add(node);
                }
            }
            if (branch.KitLockSha256 is null)
            {
                throw new InvalidDataException("A Kit matrix branch must have approved Kit evidence.");
            }
            selections.Add(new(kit, branch.KitLockSha256, branch.ConfigurationLock));
            variants.AddRange(branch.Variants.Select(variant => variant with { Name = prefix + variant.Name }));
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(combined, WorkflowJson.Default.WorkflowDocument);
        string digest = BuildVariantConfiguration.Hash(bytes);
        return first! with
        {
            ManifestSha256 = digest,
            ManifestName = ".cmwf-developer-Benchmark-" + digest + ".json",
            ManifestJson = bytes,
            SelectedKit = null,
            KitLockSha256 = null,
            KitMatrix = selections.AsReadOnly(),
            Variants = variants.AsReadOnly()
        };
    }
}
