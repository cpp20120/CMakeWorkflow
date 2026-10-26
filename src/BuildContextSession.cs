namespace CMakeWorkflow;

// This is an observation token, NOT a semantic configuration key or freshness key.
public sealed record GenerationObservation(string? IndexFile, string? IndexHash)
{
    public static GenerationObservation From(ObservedBuildContext context) => new(context.IndexFile, context.IndexHash);
    public bool Known => IndexFile is not null && IndexHash is not null;
}

public sealed record BuildContextSessionState(string Mode, string Policy, string Provenance,
    GenerationObservation Generation, string GenerationChange, bool ObservedPropertiesChanged, bool RequiresConfigure);

// One coordinator per canonical physical tree. The existing scheduler serializes
// accesses and the existing workflow lease protects cooperating CLI processes.
// No native dependency graph, configuration fingerprint, new lock or retries.
public sealed class BuildContextSession
{
    private ObservedBuildContext? previous;
    private bool requiresConfigure;
    private string generationChange = "InitialObservation";
    private bool observedPropertiesChanged;

    public BuildContextSessionState Observe(RequestedBuildContext request, ObservedBuildContext observed,
        string phase)
    {
        var generation = GenerationObservation.From(observed);
        observedPropertiesChanged = previous is not null && (previous.SourceDirectory != observed.SourceDirectory
            || previous.Generator != observed.Generator || previous.MultiConfig != observed.MultiConfig
            || !previous.Configurations.SequenceEqual(observed.Configurations)
            || !previous.Toolchains.SequenceEqual(observed.Toolchains));
        if (previous is not null && GenerationObservation.From(previous) != generation)
        {
            generationChange = phase switch
            {
                "AfterBuild" => "ObservedDuringBuildRequest",
                "AfterInvoke" => "ObservedDuringTargetInvocation",
                _ => "ExternalOrUnknown"
            };
            if (request.ProvenancePolicy == "strict") requiresConfigure = true;
        }
        else generationChange = previous is null ? "InitialObservation" : "Unchanged";
        if (observed.State == "ConfigureFailed") requiresConfigure = true;
        if (request.Mode == "managed" && (observed.ManagedConfigure is not { State: "Succeeded" }
            || request.ProvenancePolicy == "strict" && observed.PresetState != "RecordedInvocation"))
            requiresConfigure = true;
        return State(request, observed);
    }

    public BuildContextSessionState State(RequestedBuildContext request, ObservedBuildContext observed) => new(
        request.Mode, request.ProvenancePolicy,
        requiresConfigure || observed.State is not ("Observed" or "ObservedLimited") ? "Unverified"
            : request.Mode == "adopt" ? "AdoptedUnverified" : observed.PresetState == "RecordedInvocation"
            ? "RecordedInvocation" : "Unverified", GenerationObservation.From(observed), generationChange,
        observedPropertiesChanged, requiresConfigure);

    public void Validate(RequestedBuildContext request, ObservedBuildContext observed)
    {
        CMakeInspection.ValidateIdentity(request with { ConfigurePreset = null }, observed);
        // Changes in generation alone are uncertainty. These changes in observable
        // tree identity are actual conflicts, even under AllowUnverified.
        if (previous is not null)
        {
            if (previous.SourceDirectory is not null && observed.SourceDirectory is not null
                && !WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(previous.SourceDirectory),
                    BuildTreeOwnership.CanonicalPath(observed.SourceDirectory)))
                throw new InvalidOperationException("Observed source identity changed within this build context session.");
            if (previous.Generator != observed.Generator || previous.MultiConfig != observed.MultiConfig)
                throw new InvalidOperationException("Observed generator identity changed within this build context session.");
            if (previous.MultiConfig == false && !previous.Configurations.SequenceEqual(observed.Configurations))
                throw new InvalidOperationException("Observed single-config configuration changed within this build context session.");
        }
        ValidateAdmission(request, observed);
        if (requiresConfigure)
            throw new InvalidOperationException("Session provenance is Unverified: Strict generation changes and failed configure attempts require an explicit configure barrier.");
        previous = observed;
    }

    public void BeginConfigure()
    {
        requiresConfigure = true;
        previous = null;
    }

    public void Configured(RequestedBuildContext request, ObservedBuildContext observed)
    {
        ValidateAdmission(request with { Mode = "managed", ProvenancePolicy = "strict" }, observed);
        if (request.Configuration is not null || observed.MultiConfig == false)
            CMakeInspection.Resolve(ResolutionRequest(request), observed);
        previous = observed;
        requiresConfigure = false;
        generationChange = "ExplicitConfigure";
        observedPropertiesChanged = false;
    }

    // Adopt explicitly accepts missing preset evidence, even with Strict. Strict
    // then guards generation changes during this session. Known conflicts remain errors.
    public static RequestedBuildContext ResolutionRequest(RequestedBuildContext request) =>
        request.Mode == "adopt" ? request with { ProvenancePolicy = "allowUnverified" } : request;

    public static void ValidateAdmission(RequestedBuildContext request, ObservedBuildContext observed)
    {
        if (request.Mode == "observe")
            throw new InvalidOperationException("Observe permits inspection only. Explicitly set mode: adopt for an unmanaged tree, or add a managed configure barrier; provenancePolicy: allowUnverified alone does not adopt a tree.");
        // Validate observable conflicts before asking whether provenance suffices.
        CMakeInspection.ValidateIdentity(request with { ConfigurePreset = null }, observed);
        if (request.Configuration is not null && !observed.Configurations.Contains(request.Configuration, StringComparer.Ordinal))
            throw new InvalidOperationException($"Requested configuration '{request.Configuration}' disagrees with observed configurations [{string.Join(", ", observed.Configurations)}].");
        CMakeInspection.ValidateIdentity(ResolutionRequest(request), observed);
        if (observed.SourceDirectory is null || observed.Generator is null)
            throw new InvalidOperationException("Source/generator identity is Unknown; explicit configure is required.");
        if (request.Mode == "managed")
        {
            var invocation = observed.ManagedConfigure;
            if (invocation is not { State: "Succeeded" }
                || !WorkflowPlanner.PathComparer.Equals(invocation.BuildDirectory, request.BuildDirectory)
                || (request.ConfigurePreset is not null && invocation.Preset != request.ConfigurePreset)
                || (request.SourceDirectory is not null && !WorkflowPlanner.PathComparer.Equals(
                    BuildTreeOwnership.CanonicalPath(invocation.SourceDirectory), request.SourceDirectory)))
                throw new InvalidOperationException("Managed context has no matching successful configure invocation. Add an explicit configure barrier or explicitly Adopt the existing tree.");
            if (observed.PresetState != "RecordedInvocation" && request.ProvenancePolicy == "strict")
                throw new InvalidOperationException("Managed configure provenance is Unverified; Strict requires an explicit configure barrier.");
        }
    }
}
