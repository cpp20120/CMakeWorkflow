using System.Text.Json.Serialization;

namespace CMakeWorkflow.CMake.Toolchains;

public sealed record LinuxToolchainIdentity(
    WorkflowToolSelection CMake,
    WorkflowToolSelection CTest,
    WorkflowToolSelection Ninja,
    string CCompiler,
    string CCompilerSha256,
    string CxxCompilerSha256,
    string TargetTriple)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompilerVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompilerArtifactSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowToolSelection? Linker { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkArtifactSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentLockSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VcpkgProjectSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VcpkgPackageTreeSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VcpkgDownloadTreeSha256 { get; init; }

}
