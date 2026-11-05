#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

public sealed record FuzzMergeOptions(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);
