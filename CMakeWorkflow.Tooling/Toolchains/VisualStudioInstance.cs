using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record VisualStudioInstance(
    string Id,
    string Name,
    string Version,
    string InstallationPath,
    string? DeveloperCommandPath);
