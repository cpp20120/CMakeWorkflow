using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public sealed record HostFinding(
    string Id,
    string Status,
    string? Observed,
    string Expected,
    string? Diagnostic);
