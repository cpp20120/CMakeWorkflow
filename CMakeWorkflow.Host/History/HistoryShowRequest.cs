// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.History;
#pragma warning restore IDE0130

public sealed record HistoryShowRequest(string DatabasePath, string RunId);
