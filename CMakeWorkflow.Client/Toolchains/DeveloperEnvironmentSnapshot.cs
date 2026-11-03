using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record DeveloperEnvironmentSnapshot(
    int SchemaVersion,
    string EnvironmentProfileId,
    string HostArchitecture,
    string TargetArchitecture,
    string Fingerprint,
    Dictionary<string, string> VisibleVariables,
    List<string> Diagnostics);
