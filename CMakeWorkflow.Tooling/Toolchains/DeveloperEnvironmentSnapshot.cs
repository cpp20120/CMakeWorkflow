using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record DeveloperEnvironmentSnapshot(
    int SchemaVersion,
    string EnvironmentProfileId,
    string HostArchitecture,
    string TargetArchitecture,
    string Fingerprint,
    Dictionary<string, string> VisibleVariables,
    List<string> Diagnostics);
