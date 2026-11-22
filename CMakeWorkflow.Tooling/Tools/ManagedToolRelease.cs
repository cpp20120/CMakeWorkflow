#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Tooling.Tools;
#endif

#if EXPERIMENT_SDK
internal
#else
public
#endif
sealed record ManagedToolRelease(string Id, string Version, string Platform, string Source,
    string Sha256, string TreeSha256, string? TarRoot);
