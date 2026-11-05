namespace CMakeWorkflow.Client.Protocol;

internal sealed record InitRequest(int ProtocolVersion, string WorkspaceDirectory);
