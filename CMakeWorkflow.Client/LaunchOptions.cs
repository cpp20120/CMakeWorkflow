namespace CMakeWorkflow.Client;

/// <summary>Options for local Host connection establishment.</summary>
/// <param name="Executable">Host executable to launch using an argument list, without shell expansion.</param>
/// <param name="Workspace">Workspace path resolved locally before initialization.</param>
/// <param name="Endpoint">Explicit local endpoint, or null for a generated private endpoint.</param>
public sealed record LaunchOptions(string Executable, string Workspace, string? Endpoint = null);
