using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client.Workflows;

namespace CMakeWorkflow.Client.AotSmoke;

internal static class CancellationSmoke
{
    public static async Task RunAsync(CMakeWorkflowClient client, string fixture, CancellationToken ct)
    {
        string source = Path.Combine(fixture, "cancel source");
        string build = Path.Combine(fixture, "cancel build");
        _ = Directory.CreateDirectory(source);
        string marker = Path.Combine(source, "effects.txt");
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
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", ct);
        ProcessStartInfo start = new("cmake")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? argument in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
        {
            start.ArgumentList.Add(argument);
        }

        using (Process process = Process.Start(start) ?? throw new InvalidOperationException("CMake did not start."))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            string diagnostics = await stdout + await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Cancellation fixture configure failed: " + diagnostics);
            }
        }

        string manifest = Path.Combine(fixture, "cancel.json");
        byte[] bytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ """
                                                                   {"schemaVersion":1,"contexts":{"probe":{"buildDirectory":"cancel build","mode":"adopt"}},
                                                                    "nodes":[{"id":"effect","kind":"invoke","context":"probe","target":"effect"}]}
                                                                   """);
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        StartWorkflowOptions options = new("aot-cancel-" + Guid.NewGuid().ToString("N"), manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, Path.Combine(fixture, "cancel results"));
        WorkflowOperation operation = await client.Workflows.StartAsync(options, ct);
        try
        {
            using CancellationTokenSource started = CancellationTokenSource.CreateLinkedTokenSource(ct);
            started.CancelAfter(TimeSpan.FromSeconds(15));
            // CMake file(APPEND) can hold an exclusive file handle briefly on
            // Windows. File existence does not imply the marker is readable yet.
            while ((await TryReadEffectLinesAsync(marker, started.Token)) is not { } lines
                   || !lines.Contains("started", StringComparer.Ordinal))
            {
                await Task.Delay(30, started.Token);
            }
            // Cancellation intent has its own CAS revision, independent of the
            // reconciliation revision returned by InspectRecoveryAsync.
            CancellationReceipt receipt = await operation.RequestCancellationAsync(0, ct);
            if (receipt.OperationId != operation.OperationId || !receipt.Requested
                                                             || receipt.State != "cancel_requested" ||
                                                             receipt.Revision != 1)
            {
                throw new InvalidOperationException("Active cancellation receipt did not round-trip.");
            }

            OperationSnapshot terminal = await operation.WaitForCompletionAsync(ct: ct);
            if (terminal.State != "canceled")
            {
                throw new InvalidOperationException("Active operation did not reach canceled.");
            }

            RecoverySnapshot final = await operation.InspectRecoveryAsync(ct);
            if (!final.CancellationRequested || final.TerminalState != "canceled")
            {
                throw new InvalidOperationException("Cancellation was not persisted.");
            }

            CancellationReceipt late = await operation.RequestCancellationAsync(receipt.Revision, ct);
            if (late.Requested || late.State != "canceled" || late.Revision != receipt.Revision)
            {
                throw new InvalidOperationException("Terminal cancellation receipt changed.");
            }

            WorkflowOperation retry = await client.Workflows.StartAsync(options, ct);
            if (retry.OperationId != operation.OperationId || (await retry.GetSnapshotAsync(ct)).State != "canceled")
            {
                throw new InvalidOperationException(
                    "Retry after cancellation did not preserve operation identity/outcome.");
            }
            // Do not treat a transient Windows sharing violation as an
            // execution failure. Still require the complete, exact effect log;
            // a writer that never releases the file must fail this smoke.
            using CancellationTokenSource markerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            markerDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            string[]? effects;
            while ((effects = await TryReadEffectLinesAsync(marker, markerDeadline.Token)) is null)
            {
                await Task.Delay(30, markerDeadline.Token);
            }

            if (!effects.SequenceEqual(["started"]))
            {
                throw new InvalidOperationException("Canceled effect completed or ran more than once.");
            }

            Console.WriteLine(
                "PASS NativeAOT active cancellation: typed receipt, durable canceled, retry without redispatch; effect stopped.");
        }
        finally
        {
            // If an assertion/timeout fails, make a bounded best-effort request to
            // stop our fixture. Disconnecting alone does not cancel Host work.
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                RecoverySnapshot state = await operation.InspectRecoveryAsync(cleanup.Token);
                if (state.TerminalState is null)
                {
                    _ = await operation.RequestCancellationAsync(state.CancellationRequested ? 1 : 0, cleanup.Token);
                }
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync("Cancellation fixture cleanup failed: " + error.Message);
            }
#pragma warning restore CA1031

        }
    }

    private static async Task<string[]?> TryReadEffectLinesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllLinesAsync(path, ct);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // A concurrent CMake APPEND may temporarily deny readers on Windows.
            return null;
        }
    }
}