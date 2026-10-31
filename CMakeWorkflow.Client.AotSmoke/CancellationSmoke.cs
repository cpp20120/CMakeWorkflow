using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client;

internal static class CancellationSmoke
{
    public static async Task RunAsync(CMakeWorkflowClient client, string fixture, CancellationToken ct)
    {
        var source = Path.Combine(fixture, "cancel source");
        var build = Path.Combine(fixture, "cancel build");
        Directory.CreateDirectory(source);
        var marker = Path.Combine(source, "effects.txt");
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(aot_cancel NONE)
            add_custom_target(effect COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake")
            """, ct);
        await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"), """
            file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "started\n")
            execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 30)
            file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "finished\n")
            """, ct);
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", ct);
        var start = new ProcessStartInfo("cmake")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            start.ArgumentList.Add(argument);
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("CMake did not start."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            var diagnostics = await stdout + await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("Cancellation fixture configure failed: " + diagnostics);
        }
        var manifest = Path.Combine(fixture, "cancel.json");
        var bytes = Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"contexts":{"probe":{"buildDirectory":"cancel build","mode":"adopt"}},
             "nodes":[{"id":"effect","kind":"invoke","context":"probe","target":"effect"}]}
            """);
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        var options = new StartWorkflowOptions("aot-cancel-" + Guid.NewGuid().ToString("N"), manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, Path.Combine(fixture, "cancel results"));
        var operation = await client.Workflows.StartAsync(options, ct);
        try
        {
            using var started = CancellationTokenSource.CreateLinkedTokenSource(ct);
            started.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker) || !(await File.ReadAllTextAsync(marker, started.Token)).Contains("started\n", StringComparison.Ordinal))
                await Task.Delay(30, started.Token);
            // Cancellation intent has its own CAS revision, independent of the
            // reconciliation revision returned by InspectRecoveryAsync.
            var receipt = await operation.RequestCancellationAsync(0, ct);
            if (receipt.OperationId != operation.OperationId || !receipt.Requested
                || receipt.State != "cancel_requested" || receipt.Revision != 1)
                throw new InvalidOperationException("Active cancellation receipt did not round-trip.");
            var terminal = await operation.WaitForCompletionAsync(ct: ct);
            if (terminal.State != "canceled") throw new InvalidOperationException("Active operation did not reach canceled.");
            var final = await operation.InspectRecoveryAsync(ct);
            if (!final.CancellationRequested || final.TerminalState != "canceled")
                throw new InvalidOperationException("Cancellation was not persisted.");
            var late = await operation.RequestCancellationAsync(receipt.Revision, ct);
            if (late.Requested || late.State != "canceled" || late.Revision != receipt.Revision)
                throw new InvalidOperationException("Terminal cancellation receipt changed.");
            var retry = await client.Workflows.StartAsync(options, ct);
            if (retry.OperationId != operation.OperationId || (await retry.GetSnapshotAsync(ct)).State != "canceled")
                throw new InvalidOperationException("Retry after cancellation did not preserve operation identity/outcome.");
            if (!(await File.ReadAllLinesAsync(marker, ct)).SequenceEqual(["started"]))
                throw new InvalidOperationException("Canceled effect completed or ran more than once.");
            Console.WriteLine("PASS NativeAOT active cancellation: typed receipt, durable canceled, retry without redispatch; effect stopped.");
        }
        finally
        {
            // If an assertion/timeout fails, make a bounded best-effort request to
            // stop our fixture. Disconnecting alone does not cancel Host work.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var state = await operation.InspectRecoveryAsync(cleanup.Token);
                if (state.TerminalState is null)
                    await operation.RequestCancellationAsync(state.CancellationRequested ? 1 : 0, cleanup.Token);
            }
            catch (Exception error) { Console.Error.WriteLine("Cancellation fixture cleanup failed: " + error.Message); }
        }
    }
}
