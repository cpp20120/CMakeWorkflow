using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Client.Workflows;

namespace CMakeWorkflow.Commands;

internal sealed record ExperimentResult(int SchemaVersion, string ExperimentId, string Repository,
    string RequestedRef, string Commit, string SourceSnapshotId, string ManifestSha256, string Host,
    string Workspace, string PipelineOperationId, string ChildRequestId, string OperationId, string State,
    string ReportSha256, string RunJson, string HtmlReport, string HistoryStatus,
    IReadOnlyList<OperationArtifactInfo> Artifacts, JsonElement Run);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExperimentResult))]
[JsonSerializable(typeof(ExperimentHostPool))]
internal sealed partial class ExperimentJson : JsonSerializerContext;
