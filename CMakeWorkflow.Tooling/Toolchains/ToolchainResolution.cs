using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record ToolchainResolution(
    int SchemaVersion,
    string Status,
    ToolchainCandidate? CMake,
    ToolchainCandidate? Ninja,
    ToolchainCandidate? Compiler,
    ToolchainCandidate? Sdk,
    string? EnvironmentProfileId,
    List<string> Diagnostics);
