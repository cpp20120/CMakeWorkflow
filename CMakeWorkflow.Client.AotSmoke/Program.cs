using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Client.AotSmoke;
using StreamJsonRpc;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Client.Recipes;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

if (args.Length == 4 && args[0] == "--verify-package")
{
    return PackageAudit.Run(args[1], args[2], args[3]);
}

bool inFlight = args.Length == 3 && args[2] == "--inflight";
if (args.Length != 2 && !inFlight)
{
    await Console.Error.WriteLineAsync("usage: CMakeWorkflow.Client.AotSmoke ENDPOINT WORKSPACE [--inflight]");
    return 2;
}

try
{
    string workspace = Path.GetFullPath(args[1]);
    if (!Directory.Exists(workspace))
    {
        throw new DirectoryNotFoundException("Smoke workspace must already exist: " + workspace);
    }

    if (JsonSerializer.IsReflectionEnabledByDefault)
    {
        throw new InvalidOperationException("Smoke must run with reflection-based JSON serialization disabled.");
    }

    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
    CancellationToken ct = timeout.Token;
    int hostPid;
    await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(args[0], workspace), ct))
    {
        hostPid = client.Host.ProcessId;
        if (client.Host.ProtocolVersion != 1 || hostPid <= 0)
        {
            throw new InvalidOperationException("Invalid initialize response.");
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(client.Host.WorkspaceDirectory, workspace, comparison))
        {
            throw new InvalidOperationException("Host bound a different workspace.");
        }

        foreach (string? capability in new[]
                 {
                     "workflow/plan", "workflow/start", "benchmark/prepare",
                     "profileRecipe/prepare", "testMatrix/prepare", "buildMatrix/prepare", "benchmarkMatrix/prepare",
                     "operations/get", "operations/find", "operations/cancel", "operations/events",
                     "operations/artifacts/list", "operations/artifacts/readChunk",
                     "workflow/recoveryInspect"
                 })
        {
            if (!client.Host.Capabilities.Contains(capability, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Host lacks " + capability);
            }
        }

        Console.WriteLine($"PASS NativeAOT initialize: Host {hostPid}; workspace={workspace}");
        if (inFlight)
        {
            await InFlightCancellationSmoke.RunAsync(client, workspace, ct);
        }

        // Never execute a repository workflow. All fixtures belong to this smoke.
        string fixture = Path.Combine(workspace, "sdk-aot-smoke-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(fixture);
        string benchmarkBuild = Directory.CreateDirectory(Path.Combine(fixture, "benchmark build")).FullName;
        PrepareBenchmarkOptions benchmarkOptions = new(Guid.NewGuid().ToString("N"),
            Path.Combine(fixture, "benchmark.json"), new BenchmarkRecipeV1("benchmark", 1,
                benchmarkBuild, "example", "Release", 2, 30, ["--case", "a b"]));
        PreparedBenchmark benchmark = await client.Benchmarks.PrepareAsync(benchmarkOptions, ct);
        // Compare the manifest's committed bytes, not lexical paths: macOS
        // temp paths may use /var while Host returns the canonical /private/var.
        byte[] requestedBytes = await File.ReadAllBytesAsync(benchmarkOptions.OutputPath, ct);
        byte[] committedBytes = await File.ReadAllBytesAsync(benchmark.Manifest, ct);
        if (benchmark.SchemaVersion != 1 || !requestedBytes.SequenceEqual(committedBytes)
                                         || benchmark.ManifestSha256 !=
                                         Convert.ToHexString(SHA256.HashData(committedBytes))
                                         || benchmark != await client.Benchmarks.PrepareAsync(benchmarkOptions, ct))
        {
            throw new InvalidOperationException("NativeAOT Benchmark preparation or replay failed.");
        }

        Console.WriteLine("PASS NativeAOT typed benchmark preparation and exact manifest hash.");
        BuildVariantV1 recipeVariant = new("main", benchmarkBuild, "Release");
        string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        PreparedRecipe[] recipePreparations =
        [
            await client.Recipes.PrepareProfileAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(fixture, "profile-recipe.json"), new ProfileRecipeV1("profile", 1,
                    [new("main", benchmarkBuild)], "example", "Release", 1, 30, [],
                    "auto", "cpu", platform)), ct),
            await client.Recipes.PrepareTestMatrixAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(fixture, "test-matrix.json"), new TestMatrixRecipeV1("test-matrix", 1,
                    benchmarkBuild, "example", "Release", [new("unit", LabelRegex: "unit")],
                    "parallel", 30)), ct),
            await client.Recipes.PrepareBuildMatrixAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(fixture, "build-matrix.json"), new BuildMatrixRecipeV1("build-matrix", 1,
                    [recipeVariant], ["example"])), ct),
            await client.Recipes.PrepareBenchmarkMatrixAsync(new(Guid.NewGuid().ToString("N"),
                Path.Combine(fixture, "benchmark-matrix.json"), new BenchmarkMatrixRecipeV1(
                    "benchmark-matrix", 1, [recipeVariant], "example", 2, 30, [],
                    [new("case", "--case", ["chain"])], "round-robin")), ct)
        ];
        foreach (PreparedRecipe? item in recipePreparations)
        {
            if (item.SchemaVersion != 1 || item.ManifestSha256 != Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(item.Manifest, ct))))
            {
                throw new InvalidOperationException("NativeAOT typed recipe response/hash mismatch.");
            }
        }

        Console.WriteLine("PASS NativeAOT typed Profile, Test Matrix, Build Matrix and Benchmark Matrix preparation.");
        string manifest = Path.Combine(fixture, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ """
                                           {"schemaVersion":2,"contexts":{"probe":{"buildDirectory":"unconfigured","mode":"observe"}},
                                            "nodes":[{"id":"inspect","kind":"inspect","context":"probe"}]}
                                           """);
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        JsonElement plan = await client.Workflows.PlanAsync(manifest, ct);
        if (!plan.GetProperty("valid").GetBoolean() || plan.GetProperty("nodes").GetArrayLength() != 1)
        {
            throw new InvalidOperationException("Read-only smoke plan is invalid.");
        }

        string requestId = "aot-smoke-" + Guid.NewGuid().ToString("N");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        StartWorkflowOptions options = new(requestId, manifest, hash, 1, Path.Combine(fixture, "results"));
        WorkflowOperation operation = await client.Workflows.StartAsync(options, ct);
        WorkflowOperation repeated = await client.Workflows.StartAsync(options, ct);
        if (repeated.OperationId != operation.OperationId)
        {
            throw new InvalidOperationException("Repeated request created another operation.");
        }

        OperationSnapshot terminal = await operation.WaitForCompletionAsync(ct: ct);
        if (terminal.State != "succeeded")
        {
            throw new InvalidOperationException("Inspect operation finished as " + terminal.State);
        }

        OperationSnapshot found = await client.Operations.FindAsync(requestId, ct);
        if (found.OperationId != operation.OperationId || found.ClientRequestId != requestId
                                                       || found.ManifestSha256 != hash || found.State != "succeeded")
        {
            throw new InvalidOperationException("Typed OperationSnapshot did not round-trip.");
        }

        Console.WriteLine($"PASS NativeAOT typed start/get/find: operation={found.OperationId}; state={found.State}");
        OperationArtifactList artifacts = await operation.ListArtifactsAsync(ct);
        OperationArtifactInfo artifact = artifacts.Artifacts.First(a => a.Size > 0);
        OperationArtifactChunk chunk = await operation.ReadArtifactChunkAsync(artifact, 0, 256, ct);
        string downloaded = await operation.DownloadArtifactAsync(artifact, Path.Combine(fixture, "downloaded-artifact"), ct: ct);
        byte[] downloadedBytes = await File.ReadAllBytesAsync(downloaded, ct);
        if (artifacts.OperationId != operation.OperationId || chunk.ArtifactId != artifact.ArtifactId ||
            chunk.Offset != 0 || chunk.TotalSize != artifact.Size || downloadedBytes.LongLength != artifact.Size ||
            chunk.Content.Length != Math.Min(256, artifact.Size) ||
            !chunk.Content.SequenceEqual(downloadedBytes.Take(chunk.Content.Length)) ||
            Convert.ToHexString(SHA256.HashData(downloadedBytes)) != artifact.Sha256)
        {
            throw new InvalidOperationException("NativeAOT artifact catalog/chunk/download did not round-trip.");
        }

        Console.WriteLine("PASS NativeAOT artifact list/chunk/download through internal SDK wire requests.");
        EventPage page = await operation.ReadEventsAsync(0, 100, ct);
        if (page.OperationId != found.OperationId || page.Events.Count == 0 || page.NextSequence <= 0)
        {
            throw new InvalidOperationException("Typed EventPage is empty or inconsistent.");
        }

        EventPage next = await operation.ReadEventsAsync(page.NextSequence, 100, ct);
        if (next.Events.Count != 0)
        {
            throw new InvalidOperationException("Event cursor repeated events.");
        }

        RecoverySnapshot recovery = await operation.InspectRecoveryAsync(ct);
        if (recovery.OperationId != found.OperationId || recovery.TerminalState != "succeeded")
        {
            throw new InvalidOperationException("Typed RecoverySnapshot did not round-trip.");
        }

        Console.WriteLine(
            $"PASS NativeAOT typed events/recovery: events={page.Events.Count}; revision={recovery.Revision}");

        try
        {
            _ = await client.Operations.GetAsync("missing-" + Guid.NewGuid().ToString("N"), ct);
            throw new InvalidOperationException("Missing operation was accepted.");
        }
        catch (RemoteInvocationException error)
        {
            if (error.ErrorCode != 1001 || error.DeserializedErrorData is not RpcErrorData
                {
                    Code: "not_found", Message.Length: > 0
                })
            {
                throw new InvalidOperationException("ApplicationError did not deserialize into RpcErrorData.", error);
            }
        }

        // An application error must not tear down the connection.
        if ((await operation.GetSnapshotAsync(ct)).State != "succeeded")
        {
            throw new InvalidOperationException("Connection did not survive ApplicationError.");
        }

        Console.WriteLine("PASS NativeAOT ApplicationError: 1001/not_found, typed data; connection remains usable.");

        using (CancellationTokenSource canceled = new())
        {
            await canceled.CancelAsync();
            try
            {
                _ = await operation.GetSnapshotAsync(canceled.Token);
                throw new InvalidOperationException("Pre-canceled request completed successfully.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested)
            {
            }
        }

        CancellationReceipt lateCancel = await operation.RequestCancellationAsync(recovery.Revision, ct);
        if (lateCancel.OperationId != operation.OperationId || lateCancel.Requested
                                                            || lateCancel.State != "succeeded" ||
                                                            lateCancel.Revision != recovery.Revision)
        {
            throw new InvalidOperationException("Late cancellation changed a terminal outcome.");
        }

        Console.WriteLine("PASS NativeAOT request cancellation and terminal cancel no-op.");
        await CancellationSmoke.RunAsync(client, fixture, ct);
    }

    // ConnectAsync must not stop a foreign Host when the consumer disconnects.
    await using CMakeWorkflowClient reconnected = await CMakeWorkflowClient.ConnectAsync(new(args[0], workspace), ct);
    if (reconnected.Host.ProcessId != hostPid)
    {
        throw new InvalidOperationException("Host ownership changed on disconnect.");
    }

    Console.WriteLine("PASS NativeAOT SDK smoke: source-generated RPC, reconnect; JSON reflection disabled.");
    return 0;
}

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
catch (Exception error)
{
    await Console.Error.WriteLineAsync("NativeAOT SDK smoke failed: " + error);
    return 1;
}
#pragma warning restore CA1031
