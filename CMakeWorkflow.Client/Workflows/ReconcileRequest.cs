using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

internal sealed record ReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent);
