using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class ConfigureProvenance
{
    public string State { get; set; } = "Attempted";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string BuildDirectory { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public string? Preset { get; set; }
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? WorkingDirectory { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> Arguments { get; set; } = [];
}
