using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public enum ToolPlatform { Linux, MacOS, Windows }
public enum PackageManagerKind { Auto, None, Apt, Pacman, Dnf, Brew, Winget }

public sealed record ToolDefinition(string Id, string DisplayName, string[] Commands,
    string[] ProbeArguments, Version? MinimumVersion,
    IReadOnlyDictionary<PackageManagerKind, string[]> Packages,
    bool StandaloneCMake = false, string? ManualHint = null);
public sealed record ToolProfile(string Id, string Description, string[] Tools);
public sealed record ToolStatus(string Id, string DisplayName, bool Installed,
    string? Command, string? Version, string? MinimumVersion, string? Diagnostic);
public sealed record ToolingStep(string Tool, string Method, string? Executable,
    string[] Arguments, string? Source, string? Diagnostic);
public sealed record ToolingPlan(int SchemaVersion, ToolPlatform Platform,
    string[] Profiles, string InstallDirectory, PackageManagerKind PackageManager,
    bool Ready, List<ToolStatus> Tools, List<ToolingStep> Steps);
public sealed record ToolingResult(int SchemaVersion, bool Success,
    string[] Ready, string[] Failed, string[] Diagnostics);
public sealed record ToolingProfileView(string Id, string Description, string[] Tools);
public sealed record ToolingProfileList(int SchemaVersion, List<ToolingProfileView> Profiles);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)])]
[JsonSerializable(typeof(ToolingPlan))]
[JsonSerializable(typeof(ToolingResult))]
[JsonSerializable(typeof(ToolingProfileList))]
public partial class ToolingJson : JsonSerializerContext;
