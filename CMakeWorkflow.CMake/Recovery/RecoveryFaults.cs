#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// No environment-controlled execution hooks in a normal/release CLI.
// Test builds pause at deterministic boundaries; the test process kills them.
internal static class RecoveryFaults
{
    public static async Task HitAsync(string point, string? node, CancellationToken ct)
    {
#if RECOVERY_FAULT_INJECTION
        if (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAILURE") == point
            && Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_NODE") == node)
        {
            throw new IOException("Injected checkpoint I/O failure at " + point);
        }
        if (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAULT") == point
            && Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_NODE") == node)
        {
            string marker = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_MARKER")
                ?? throw new InvalidOperationException("Fault marker is required.");
            if (!Path.IsPathFullyQualified(marker))
            {
                throw new InvalidOperationException("Fault marker must be absolute.");
            }
            await File.WriteAllTextAsync(marker, point, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
#else
        await Task.CompletedTask;
#endif
    }
}