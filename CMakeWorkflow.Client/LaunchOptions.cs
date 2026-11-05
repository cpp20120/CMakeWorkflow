namespace CMakeWorkflow.Client;

public sealed record LaunchOptions(string Executable, string Workspace, string? Endpoint = null);
