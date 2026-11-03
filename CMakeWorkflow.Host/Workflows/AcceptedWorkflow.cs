using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host;

// Durable admission only. This store never dispatches or resumes a workflow.
// Snapshot bytes and admission are committed in the same SQLite transaction.
public sealed record AcceptedWorkflow(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string RequestDigest,
    string ManifestSha256,
    string ManifestBaseDirectory,
    string State);
