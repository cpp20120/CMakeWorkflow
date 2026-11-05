#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

internal sealed record RelacyVariant(string Name, string Header, RelacySuite[] Suites);
