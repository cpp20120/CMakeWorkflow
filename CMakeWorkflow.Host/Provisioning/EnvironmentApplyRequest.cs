namespace CMakeWorkflow.Host.Provisioning;

public sealed record EnvironmentApplyRequest(string RequirementsPath, string ApprovalSha256, bool AllowSystemChanges);
