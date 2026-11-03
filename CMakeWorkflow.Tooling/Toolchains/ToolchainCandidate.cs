using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

// Discovery is evidence, not a promise that a particular compiler/SDK ABI works.
// Candidate IDs derive from role + canonical location; they are not client-supplied paths.
public sealed record ToolchainCandidate(
    string Id,
    string Role,
    string Flavor,
    string Provider,
    string Path,
    string? Version = null,
    string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
