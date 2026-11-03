using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

internal sealed record ResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
