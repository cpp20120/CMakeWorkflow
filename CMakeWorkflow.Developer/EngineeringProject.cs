using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Kits;

namespace CMakeWorkflow.Developer;

/// <summary>Read-only entry point for composing selections against an explicitly discovered SDK project.</summary>
public sealed class EngineeringProject
{
    private readonly ProjectLocation location;

    private EngineeringProject(DiscoveredProject project, SdkDescriptor sdk,
        IReadOnlyList<EngineeringKitDefinition> kits, IReadOnlyList<string> profiles, SelectedEngineeringKit? activeKit)
    {
        Project = project;
        Sdk = sdk;
        Kits = kits;
        Profiles = profiles;
        ActiveKit = activeKit;
        location = new(project.Directory) { ProjectDirectory = project.Directory };
    }

    public DiscoveredProject Project { get; }
    public SdkDescriptor Sdk { get; }
    public IReadOnlyList<EngineeringKitDefinition> Kits { get; }
    public IReadOnlyList<string> Profiles { get; }
    public SelectedEngineeringKit? ActiveKit { get; }

    public static async Task<EngineeringProject> OpenAsync(ProjectLocation location, CancellationToken ct = default)
    {
        DiscoveredProject project = await DeveloperLifecycle.DiscoverProjectAsync(location, ct);
        string directory = project.Directory;
        SdkDescriptor sdk = await DeveloperLifecycle.GetProjectSdkAsync(directory, ct);
        string config = BuildVariantConfiguration.CheckedPath(directory, "cmwf.config.json");
        IReadOnlyList<string> profiles = File.Exists(config)
            ? Array.AsReadOnly((await DeveloperLifecycle.ListConfigurationProfilesAsync(directory, ct))
                .EnumerateObject().Select(entry => entry.Name).ToArray()) : [];
        return new(project, sdk, await DeveloperLifecycle.ListKitsAsync(directory, ct),
            profiles, await DeveloperLifecycle.GetSelectedKitAsync(directory, ct: ct));
    }

    public DeveloperOperationRequest Operation(DeveloperOperationKind kind, ConfigurationSelection? selection = null) =>
        new(location, kind) { Configuration = selection ?? new() };

    public Task<ProjectInspection> InspectAsync(ConfigurationSelection selection, CancellationToken ct = default) =>
        DeveloperLifecycle.InspectProjectAsync(location, selection, ct);
}
