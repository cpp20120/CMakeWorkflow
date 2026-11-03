using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record LaunchOptions(string Executable, string Workspace, string? Endpoint = null);
