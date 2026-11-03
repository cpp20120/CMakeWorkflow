using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record ToolchainSelection(
    [property: JsonPropertyName("cmakeCandidateId")]
    string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null,
    string? SdkCandidateId = null,
    string? EnvironmentProfileId = null,
    string Generator = "Ninja");
