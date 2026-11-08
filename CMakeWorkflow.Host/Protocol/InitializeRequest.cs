namespace CMakeWorkflow.Host.Protocol;

// Protocol v1 is capability-negotiated. DTOs are shared by the CLI client and
// server; TypeScript clients use positional JSON-RPC arguments: [request].
public sealed record InitializeRequest(int ProtocolVersion, string WorkspaceDirectory);
