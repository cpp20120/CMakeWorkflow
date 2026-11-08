namespace CMakeWorkflow.Host.Provisioning;

public sealed record DoctorRequest(string OperationId,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Profiles
#pragma warning restore CA1819
, string? InstallDirectory);
