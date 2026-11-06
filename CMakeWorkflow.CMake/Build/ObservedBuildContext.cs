namespace CMakeWorkflow.CMake.Build;

public sealed class ObservedBuildContext
{
    public string BuildDirectory { get; set; } = "";
    public string State { get; set; } = "Unknown";
    public string Freshness { get; set; } = "Unverified";
    public string PresetState { get; set; } = "Unknown";
    public string? SourceDirectory { get; set; }
    public string? Generator { get; set; }
    public bool? MultiConfig { get; set; }

    public List<string> Configurations { get; set; } = [];
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public string? CMakeExecutable { get; set; }
    public string? CTestExecutable { get; set; }
    public bool HasCodeModel { get; set; }
    public bool HasCacheReply { get; set; }

    public List<string> Toolchains { get; set; } = [];

    public List<NativeTargetObservation> Targets { get; set; } = [];

    public List<string> Diagnostics { get; set; } = [];
    public ConfigureProvenance? ManagedConfigure { get; set; }
}
