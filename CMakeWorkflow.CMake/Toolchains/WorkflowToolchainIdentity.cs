using System.Text.Json.Serialization;

namespace CMakeWorkflow.CMake.Toolchains;

public sealed record WorkflowToolchainIdentity(
    string BuildDirectory,
    string CMake,
    string? Ninja,
    string Compiler,
    string? Sdk,
    string? EnvironmentProfileId,
    string Generator,
    string Fingerprint)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LinuxToolchainIdentity? Linux { get; init; }
}
