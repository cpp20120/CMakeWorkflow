namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Bounded transport of exact manifest bytes to an initialized Host workspace.</summary>
/// <param name="ManifestBase">Existing workspace-relative directory using slash separators, or "." for the workspace root.</param>
/// <param name="ResultsDirectory">Absolute Host path or workspace-relative path for a proposed result directory.</param>
/// <param name="Sha256">Expected SHA-256 of ManifestBytes.</param>
/// <param name="ManifestBytes">Exact UTF-8 workflow manifest.</param>
public sealed record WorkflowManifestPreparation(string ManifestBase, string ResultsDirectory, string Sha256,
    ReadOnlyMemory<byte> ManifestBytes)
{
    public const int MaximumBytes = 1024 * 1024;
}

/// <summary>Host receipt for a verified manifest. Preparation does not admit execution.</summary>
public sealed record WorkflowManifestPreparationReceipt(int SchemaVersion, string Manifest, string ManifestSha256,
    string ResultsDirectory);
