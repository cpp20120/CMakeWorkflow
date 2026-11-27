namespace CMakeWorkflow.Tooling.Tools;

// Fault-enabled builds only. Production binaries do not consult these variables.
internal static class ProvisioningInterruption
{
#if RECOVERY_FAULT_INJECTION
    internal static async Task PauseAsync(string boundary, CancellationToken ct)
    {
        if (System.Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_PROVISION_BOUNDARY") == boundary)
        {
            string marker = System.Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_PROVISION_MARKER")
                ?? throw new InvalidOperationException("A provisioning fault marker is required.");
            if (!Path.IsPathFullyQualified(marker))
            {
                throw new InvalidOperationException("The provisioning fault marker must be absolute.");
            }

            await File.WriteAllTextAsync(marker, boundary, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
#else
    internal static Task PauseAsync(string boundary, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
    }
#endif
}
