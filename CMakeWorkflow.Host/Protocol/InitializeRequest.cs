#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// Protocol v1 is capability-negotiated. DTOs are shared by the CLI client and
// server; TypeScript clients use positional JSON-RPC arguments: [request].
public sealed record InitializeRequest(int ProtocolVersion, string WorkspaceDirectory);
