#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record EnvironmentApplyRequest(string RequirementsPath, string ApprovalSha256, bool AllowSystemChanges);
