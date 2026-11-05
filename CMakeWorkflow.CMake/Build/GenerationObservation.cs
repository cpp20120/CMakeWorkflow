#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// This is an observation token, NOT a semantic configuration key or freshness key.
public sealed record GenerationObservation(string? IndexFile, string? IndexHash)
{
    public static GenerationObservation From(ObservedBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(context.IndexFile, context.IndexHash);
    }
    public bool Known => IndexFile is not null && IndexHash is not null;
}
