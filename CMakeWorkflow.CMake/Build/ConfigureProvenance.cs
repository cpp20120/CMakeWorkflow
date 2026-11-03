using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

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
    public List<string> Arguments { get; set; } = [];
}
