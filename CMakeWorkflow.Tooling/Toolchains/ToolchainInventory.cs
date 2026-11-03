using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,
    List<VisualStudioInstance> VisualStudio,
    List<ToolchainCandidate> Candidates,
    List<string> Diagnostics);
