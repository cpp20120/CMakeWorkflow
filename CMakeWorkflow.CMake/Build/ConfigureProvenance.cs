using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.CMake.Build;

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
