namespace CMakeWorkflow.Client;

/// <summary>Options for local Host connection establishment.</summary>
/// <param name="Endpoint">Existing local named pipe or absolute Unix socket endpoint.</param>
/// <param name="Workspace">Existing workspace path, resolved to an absolute local path during connection.</param>
public sealed record ClientOptions(string Endpoint, string Workspace);
