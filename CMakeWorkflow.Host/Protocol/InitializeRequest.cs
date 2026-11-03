using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

// Protocol v1 is capability-negotiated. DTOs are shared by the CLI client and
// server; TypeScript clients use positional JSON-RPC arguments: [request].
public sealed record InitializeRequest(int ProtocolVersion, string WorkspaceDirectory);
