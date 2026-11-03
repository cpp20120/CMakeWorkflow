using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record WorkflowResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
