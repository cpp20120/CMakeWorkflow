using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;

namespace CMakeWorkflow.Host.History;

internal static class WorkflowHistoryPolicy
{
    internal static (HistoryPolicyMode Mode, string? Database) Normalize(HistoryPolicy? policy, WorkspaceReadPaths paths)
    {
        return policy switch
        {
            null or { Mode: HistoryPolicyMode.Disabled, DatabasePath: null } => (HistoryPolicyMode.Disabled, null),
            { Mode: HistoryPolicyMode.Default, DatabasePath: null } => (HistoryPolicyMode.Default,
                DurableHistoryIndexer.AuthorizeDatabase(paths, Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "history.sqlite"))),
            { Mode: HistoryPolicyMode.ExplicitDatabase, DatabasePath: { } database } =>
                (HistoryPolicyMode.ExplicitDatabase, DurableHistoryIndexer.AuthorizeDatabase(paths, database)),
            _ => throw new ApplicationRefusal("invalid_history_policy", "Invalid HistoryPolicy mode and database path combination.")
        };
    }
}

public sealed record HistoryOperationRequest(string OperationId);
