using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

public sealed class FuzzRunReport
{
    public int SchemaVersion { get; set; }
    public string Layer { get; set; } = "";
    public string Backend { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public string Target { get; set; } = "";
    public string Executable { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string SourceCorpus { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int InitialInputs { get; set; }
    public int FinalInputs { get; set; }
    public int ReplayedInputs { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> FailedInputs { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> NewInputs { get; set; } = [];
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> CrashArtifacts { get; set; } = [];
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public Dictionary<string, string> ArtifactSha256 { get; set; } = [];
    public bool ReplayOnly { get; set; }
    public string ReplayMechanism { get; set; } = "direct-input";
    public int DurationSeconds { get; set; }
    public int InputTimeoutSeconds { get; set; }
    public int MaxLength { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> BuildArguments { get; set; } = [];
    public string? CampaignTool { get; set; }
    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    [SuppressMessage("Usage", "CA2227", Justification = "The existing mutable DTO setter is required for source and serialization compatibility.")]
    public List<string> CampaignArguments { get; set; } = [];
    public int? CampaignExitCode { get; set; }
    public bool CampaignTimedOut { get; set; }
    public bool InfrastructureError { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}