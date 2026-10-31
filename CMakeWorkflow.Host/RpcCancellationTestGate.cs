#if RPC_CANCELLATION_TESTING
using System.Diagnostics;

namespace CMakeWorkflow.Host;

// Compiled only into isolated test builds. No test RPC methods or test switches
// exist in the production Host. The gate runs inside the real read/session gate.
internal static class RpcCancellationTestGate
{
    internal static async Task WaitAsync(WorkspaceReadPaths paths, string manifest,
        CancellationToken request, CancellationToken disconnected, CancellationToken lifetime)
    {
        var configured = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_RPC_TEST_ROOT");
        if (string.IsNullOrEmpty(configured)) return;
        var root = paths.Require(configured);
        var file = paths.Require(manifest);
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.GetFileName(file) != "inflight-plan.json") return;

        try
        {
            await File.WriteAllTextAsync(file + ".started", "started", lifetime);
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(file + ".release"))
            {
                // A timeout fails the test; it must never impersonate RPC cancellation.
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("RPC test gate was not released.");
                await Task.Delay(10, lifetime);
            }
            lifetime.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(file + ".completed", "completed", lifetime);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested && !disconnected.IsCancellationRequested)
        {
            // Only the actual RPC token can acknowledge cancellation, never a
            // consumer timeout, a file release, Host shutdown or a disconnect.
            await File.WriteAllTextAsync(file + ".canceled", "request-canceled", CancellationToken.None);
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(file + ".exited", "exited", CancellationToken.None);
        }
    }
}
#endif
