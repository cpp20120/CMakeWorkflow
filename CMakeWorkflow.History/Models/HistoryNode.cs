using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

public sealed record HistoryNode(
    string RunId,
    string Id,
    string Kind,
    string Status,
    double? DurationSeconds,
    string? Dependencies,
    string? BuildDirectory,
    string? Configuration,
    string? Target);
