using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record EnvironmentApplyRequest(string RequirementsPath, string ApprovalSha256, bool AllowSystemChanges);
