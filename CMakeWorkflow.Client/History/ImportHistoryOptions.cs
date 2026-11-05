#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record ImportHistoryOptions(string DatabasePath, string Directory, bool Recursive = false);
