using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record DeveloperEnvironmentRequest(
    string EnvironmentProfileId,
    string HostArchitecture = "x64",
    string TargetArchitecture = "x64",
    string? WindowsSdkVersion = null,
    string? ToolsetVersion = null);
