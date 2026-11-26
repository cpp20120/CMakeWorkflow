using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

public enum FormatStatus { Clean, FormattingRequired, Formatted, Failed, Cancelled, Skipped, Unprocessed }

public sealed record FormatFile(string Path, FormatStatus Status, string? Detail = null, string? Diff = null);

public sealed record FormatInvocation(string Tool, string Phase, int? ExitCode, string Error, string? UpstreamJson = null);

public sealed record FormatToolIdentity(string Tool, string Executable, string Origin, string? Version, string? ArchiveSha256);

public sealed record FormatResult(FormatStatus Status, IReadOnlyList<FormatFile> Files, IReadOnlyList<FormatInvocation> Invocations)
{
    public IReadOnlyList<FormatToolIdentity> Tools { get; init; } = [];

    public int ExitCode => Status switch
    {
        FormatStatus.FormattingRequired => 1,
        FormatStatus.Failed => 2,
        FormatStatus.Cancelled => 130,
        FormatStatus.Clean or FormatStatus.Formatted or FormatStatus.Skipped => 0,
        FormatStatus.Unprocessed => 2,
        _ => throw new InvalidOperationException("Unknown formatting status.")
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FormatResult))]
public partial class FormatReportJson : JsonSerializerContext;
