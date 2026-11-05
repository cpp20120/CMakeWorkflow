using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Client;
using CMakeWorkflow.History;
using CMakeWorkflow.Host;
using CMakeWorkflow.Profiling;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

internal static class ApplicationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }

    private static async Task Refused(Func<Task> call, string code)
    {
        try
        {
            await call();
        }
        catch (RemoteInvocationException error)
        {
            Check(error.ErrorCode == 1001, "Stable application error code missing: " + error);
            Check(error.DeserializedErrorData is ApplicationError data && data.Code == code,
                "Stable domain code missing: " + error.DeserializedErrorData);
            return;
        }

        throw new System.Diagnostics.UnreachableException("Expected refusal: " + code);
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        string root = BuildTreeOwnership.CanonicalPath(Directory
            .CreateDirectory(Path.Combine(parent, "application workspace")).FullName);
        string outside = Directory.CreateDirectory(Path.Combine(parent, "other application workspace")).FullName;
        string build = Directory.CreateDirectory(Path.Combine(root, "build space")).FullName;
        await File.WriteAllTextAsync(Path.Combine(build, "CMakeCache.txt"),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={root}\nCMAKE_GENERATOR:INTERNAL=Ninja\nCMAKE_BUILD_TYPE:STRING=Debug\n", CancellationToken.None);
        string bridge = Directory.CreateDirectory(Path.Combine(build, "cmake-workflow", "targets", "Debug")).FullName;
        string executable = Path.Combine(build, "worker with spaces");
        await File.WriteAllTextAsync(Path.Combine(bridge, "worker.json"),
            $$"""{"schemaVersion":1,"target":"worker","configuration":"Debug","executable":{{JsonSerializer.Serialize(executable)}}} """, CancellationToken.None);
        string manifest = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(manifest, /*lang=json,strict*/ """
                                    {"schemaVersion":2,"contexts":{"x":{"buildDirectory":"build space","mode":"adopt"}},
                                     "nodes":[{"id":"build","kind":"build","context":"x","target":"worker",
                                               "inputs":[{"path":"build space/CMakeCache.txt","snapshot":true}]},
                                              {"id":"run","kind":"run","from":"build",
                                               "outputs":[{"name":"example","root":"run","path":"example.json"}]}]}
                                    """, CancellationToken.None);
        string run = Directory.CreateDirectory(Path.Combine(root, "run directory")).FullName;
        WorkflowReport report = new()
        {
            Workflow = manifest,
            Status = "failed",
            FinishedAt = DateTimeOffset.UtcNow,
            Nodes = [new() { Id = "run", Kind = "run", Status = "failed" }]
        };
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
        await File.WriteAllBytesAsync(Path.Combine(run, "run.json"), raw, CancellationToken.None);
        string otherRun = Directory.CreateDirectory(Path.Combine(outside, "run")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(otherRun, "run.json"), raw, CancellationToken.None);
        string database = Path.Combine(root, "history.sqlite");
        string ownId, otherId;
        using (HistoryStore db = new(database))
        {
            _ = db.Import(run, false);
            _ = db.Import(otherRun, false);
            List<CMakeWorkflow.History.HistoryRun> runs = db.Runs(10, null, null, null, null, null, ct: ct);
            ownId = runs.Single(r => r.Directory == run).Id;
            otherId = runs.Single(r => r.Directory == otherRun).Id;
        }

        report.Status = "outcome_unknown";
        report.Nodes[0].Status = "outcome_unknown";
        raw = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
        await File.WriteAllBytesAsync(Path.Combine(run, "run.json"), raw, CancellationToken.None);
        byte[] databaseBefore = await File.ReadAllBytesAsync(database, ct);
        string cwd = Directory.GetCurrentDirectory();
        using Stream wire = await LocalTransport.ConnectAsync(endpoint, ct);
        using JsonRpc rpc = HostConnection.Create(wire);
        IHostRpc client = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        await Refused(() => client.RunGetAsync(new(run), ct), "not_initialized");
        await Refused(() => client.WorkflowStartAsync(
            new("preinit", manifest, "0", 1, Path.Combine(root, "preinit-results")), ct), "not_initialized");
        HostInfo info = await client.InitializeAsync(new(1, root), ct);
        Check(info.Capabilities.Contains("workflow/plan") && info.Capabilities.Contains("history/detect")
                                                          && info.Capabilities.Contains("workflow/start") &&
                                                          info.Capabilities.Contains("profile/prepare")
                                                          && info.Capabilities.Contains("benchmark/prepare"),
            "Capabilities omit implemented profile/workflow lifecycle");
        ProfilePrepareRequest prepareRequest = new("0123456789abcdef0123456789abcdef",
            Path.Combine(root, "profile definitions"), "worker", [new("main", build)],
            Repeats: 1, Arguments: ["--case", "chain"]);
        ProfilePreparation prepared = await client.ProfilePrepareAsync(prepareRequest, ct);
        Check(prepared.SchemaVersion == 1 && File.Exists(prepared.Manifest)
                                          && prepared.ManifestSha256 ==
                                          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(prepared.Manifest, ct))),
            "Profile preparation missing versioned immutable manifest and digest");
        Check(prepared == await client.ProfilePrepareAsync(prepareRequest, ct),
            "Profile preparation must be idempotent with the same request ID and contents");
        WorkflowPlanView profilePlan = await client.WorkflowPlanAsync(new(prepared.Manifest), ct);
        Check(
            profilePlan.Nodes.Any(node => node.Kind == "profile") && profilePlan.Nodes.Any(node => node.Kind == "run"),
            "Profile graph must include separate timing and instrumented capture nodes");
        await P3ApiTests.RunAsync(endpoint, root, prepared.Manifest, prepared.ManifestSha256,
            profilePlan.Nodes.Count, ct);
        await Refused(() => client.ProfilePrepareAsync(prepareRequest with { Target = "other" }, ct),
            "request_id_conflict");
        string otherProfileDirectory = Path.Combine(root, "other profile definitions");
        await Refused(() => client.ProfilePrepareAsync(prepareRequest with
        {
            ResultsDirectory = otherProfileDirectory
        }, ct), "request_id_conflict");
        Check(!Directory.Exists(otherProfileDirectory),
            "Conflicting profile request created an alternate result directory");
        await Refused(() => client.ProfilePrepareAsync(prepareRequest with
        {
            RequestId = "11111111111111111111111111111111",
            Variants = [new("escape", outside)]
        }, ct),
            "path_not_allowed");
        await Refused(() => client.ProfilePrepareAsync(prepareRequest with { RequestId = "../escape" }, ct),
            "invalid_argument");
        BenchmarkRecipeV1 benchmarkRecipe = new("benchmark", 1, build, "worker", "Debug",
            10, 30, ["--case", "a b", ""]);
        BenchmarkPrepareRequest benchmarkRequest = new("abcdef0123456789abcdef0123456789",
            Path.Combine(root, "benchmark definitions", "chain.json"), benchmarkRecipe);
        BenchmarkPreparation benchmarkPrepared = await client.BenchmarkPrepareAsync(benchmarkRequest, ct);
        // WorkspaceReadPaths canonicalizes filesystem aliases; the response may
        // use /private/var even when the request uses /var on macOS.
        byte[] benchmarkBytes = await File.ReadAllBytesAsync(benchmarkPrepared.Manifest, ct);
        Check(benchmarkPrepared.SchemaVersion == 1 && File.Exists(benchmarkRequest.OutputPath)
                                                   && (await File.ReadAllBytesAsync(benchmarkRequest.OutputPath, ct))
                                                       .SequenceEqual(benchmarkBytes)
                                                   && benchmarkPrepared.ManifestSha256 == Convert.ToHexString(
                                                       SHA256.HashData(
                                                           benchmarkBytes)), "Benchmark preparation hash or path");
        Check(benchmarkPrepared == await client.BenchmarkPrepareAsync(benchmarkRequest, ct),
            "Benchmark preparation retry changed committed result");
        WorkflowPlanView benchmarkPlan = await client.WorkflowPlanAsync(new(benchmarkPrepared.Manifest), ct);
        Check(benchmarkPlan.Valid && benchmarkPlan.Nodes.Count == 11
                                  && benchmarkPlan.Nodes.Count(n => n.Kind == "build") == 1,
            "Prepared benchmark was not accepted by existing planner");
        await Refused(() => client.BenchmarkPrepareAsync(benchmarkRequest with
        {
            Recipe = benchmarkRecipe with { Repeats = 11 }
        }, ct), "request_id_conflict");
        string conflictingOutput = Path.Combine(root, "other benchmark definitions", "chain.json");
        await Refused(() => client.BenchmarkPrepareAsync(benchmarkRequest with
        {
            OutputPath = conflictingOutput
        }, ct), "request_id_conflict");
        Check(!File.Exists(conflictingOutput), "Conflicting Benchmark request created a manifest");
        await Refused(() => client.BenchmarkPrepareAsync(benchmarkRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OutputPath = Path.Combine(outside, "outside.json")
        }, ct),
            "path_not_allowed");
        await Refused(() => client.BenchmarkPrepareAsync(benchmarkRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Recipe = benchmarkRecipe with
            {
                BuildDirectory = outside
            }
        }, ct), "path_not_allowed");
        await Refused(() => client.BenchmarkPrepareAsync(benchmarkRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Recipe = benchmarkRecipe with
            {
                SchemaVersion = 2
            }
        }, ct), "invalid_argument");
        Check(!File.Exists(executable), "Benchmark preparation launched a native executable");
        string platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        ProfileRecipeV1 recipeProfile = new("profile", 1,
            [new("main", build)], "worker", "Debug", 1, 30, ["a b"], "auto", "cpu", platform);
        ProfileRecipePrepareRequest profileRecipeRequest = new(Guid.NewGuid().ToString("N"),
            Path.Combine(root, "recipe-profile.json"), recipeProfile);
        ProfileRecipePreparation profileRecipeReady = await client.ProfileRecipePrepareAsync(profileRecipeRequest, ct);
        Check(profileRecipeReady == await client.ProfileRecipePrepareAsync(profileRecipeRequest, ct)
              && profileRecipeReady.ManifestSha256 == Convert.ToHexString(SHA256.HashData(
                  await File.ReadAllBytesAsync(profileRecipeReady.Manifest, ct)))
              && (await client.WorkflowPlanAsync(new(profileRecipeReady.Manifest), ct)).Valid,
            "Profile recipe wire preparation/hash/plan");
        TestMatrixRecipeV1 recipeTest = new("test-matrix", 1, build, "worker", "Debug",
            [new("unit", LabelRegex: "unit")], "parallel", 30);
        TestMatrixPrepareRequest testMatrixRequest = new(Guid.NewGuid().ToString("N"),
            Path.Combine(root, "recipe-test.json"), recipeTest);
        TestMatrixPreparation testMatrixReady = await client.TestMatrixPrepareAsync(testMatrixRequest, ct);
        Check(testMatrixReady == await client.TestMatrixPrepareAsync(testMatrixRequest, ct)
              && testMatrixReady.ManifestSha256 == Convert.ToHexString(SHA256.HashData(
                  await File.ReadAllBytesAsync(testMatrixReady.Manifest, ct)))
              && (await client.WorkflowPlanAsync(new(testMatrixReady.Manifest), ct)).Valid,
            "Test Matrix wire preparation/hash/plan");
        BuildVariantV1[] recipeVariants = [new BuildVariantV1("main", build, "Debug")];
        BuildMatrixRecipeV1 recipeBuild = new("build-matrix", 1, recipeVariants, ["worker"]);
        BuildMatrixPrepareRequest buildMatrixRequest = new(Guid.NewGuid().ToString("N"),
            Path.Combine(root, "recipe-build.json"), recipeBuild);
        BuildMatrixPreparation buildMatrixReady = await client.BuildMatrixPrepareAsync(buildMatrixRequest, ct);
        Check(buildMatrixReady == await client.BuildMatrixPrepareAsync(buildMatrixRequest, ct)
              && buildMatrixReady.ManifestSha256 == Convert.ToHexString(SHA256.HashData(
                  await File.ReadAllBytesAsync(buildMatrixReady.Manifest, ct)))
              && (await client.WorkflowPlanAsync(new(buildMatrixReady.Manifest), ct)).Valid,
            "Build Matrix wire preparation/hash/plan");
        BenchmarkMatrixRecipeV1 recipeMatrix = new("benchmark-matrix", 1,
            recipeVariants, "worker", 2, 30, ["x"], [new("case", "--case", ["a", "b"])],
            "round-robin");
        BenchmarkMatrixPrepareRequest matrixRequest = new(Guid.NewGuid().ToString("N"),
            Path.Combine(root, "recipe-matrix.json"), recipeMatrix);
        BenchmarkMatrixPreparation matrixReady = await client.BenchmarkMatrixPrepareAsync(matrixRequest, ct);
        Check(matrixReady == await client.BenchmarkMatrixPrepareAsync(matrixRequest, ct)
              && matrixReady.ManifestSha256 == Convert.ToHexString(SHA256.HashData(
                  await File.ReadAllBytesAsync(matrixReady.Manifest, ct)))
              && (await client.WorkflowPlanAsync(new(matrixReady.Manifest), ct)).Valid,
            "Benchmark Matrix wire preparation/hash/plan");
        await Refused(() => client.BenchmarkMatrixPrepareAsync(matrixRequest with
        {
            Recipe = recipeMatrix with { Repeats = 3 }
        }, ct), "request_id_conflict");
        await Refused(() => client.BenchmarkMatrixPrepareAsync(matrixRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Recipe = recipeMatrix with { Repeats = 3 }
        }, ct),
            "request_id_conflict");
        BenchmarkMatrixPrepareRequest concurrentMatrix = matrixRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OutputPath = Path.Combine(root, "concurrent-matrix.json")
        };
        BenchmarkMatrixPreparation[] matrixReplays = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.BenchmarkMatrixPrepareAsync(concurrentMatrix, ct)));
        Check(matrixReplays.All(p => p == matrixReplays[0]),
            "Concurrent Benchmark Matrix preparation changed committed response");
        await Refused(() => client.BuildMatrixPrepareAsync(buildMatrixRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Recipe = recipeBuild with
            {
                Variants = [new("escape", outside)]
            }
        }, ct), "path_not_allowed");
        Check(!File.Exists(executable), "Typed recipe preparation launched an executable");
        BenchmarkPrepareRequest concurrentRequest = benchmarkRequest with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OutputPath = Path.Combine(root, "concurrent", "chain.json")
        };
        BenchmarkPreparation[] concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.BenchmarkPrepareAsync(concurrentRequest, ct)));
        Check(concurrent.All(x => x == concurrent[0])
              && File.Exists(concurrent[0].Manifest), "Concurrent Benchmark preparation was not idempotent");
        using (Stream retryWire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc retryRpc = HostConnection.Create(retryWire))
        {
            IHostRpc retryClient = retryRpc.Attach<IHostRpc>();
            retryRpc.StartListening();
            _ = await retryClient.InitializeAsync(new(1, root), ct);
            Check(await retryClient.BenchmarkPrepareAsync(benchmarkRequest, ct) == benchmarkPrepared,
                "Lost response/reconnect changed Benchmark preparation");
            Check(await retryClient.BenchmarkMatrixPrepareAsync(matrixRequest, ct) == matrixReady,
                "Lost response/reconnect changed Benchmark Matrix preparation");
        }

        string fixture = Directory.CreateDirectory(Path.Combine(root, "profile analysis fixture")).FullName;
        await File.WriteAllTextAsync(Path.Combine(fixture, "raw.txt"), "raw fixture", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture, "capture.results.json"),
                                 /*lang=json,strict*/
                                 """{"captureId":"capture","backend":"fixture","mode":"cpu","eventSource":"test","rawTrace":"raw.txt"}""", CancellationToken.None);
        ProfileAnalysis analyzed = await client.ProfileAnalyzeAsync(new(fixture), ct);
        Check(analyzed.SchemaVersion == 1 && analyzed.AnalysisFile == Path.Combine(fixture, "analysis.json")
                                          && File.Exists(analyzed.AnalysisFile),
            "Profile analyze RPC must return the saved artifact path");
        await Refused(() => client.ProfileAnalyzeAsync(new(otherRun), ct), "path_not_allowed");
        // Recovery mutation and event replay must be real wire methods, not local-only helpers.
        string recoveryManifest = Path.Combine(root, "recovery-wire.json");
        byte[] recoveryBytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"schemaVersion\":1}");
        await File.WriteAllBytesAsync(recoveryManifest, recoveryBytes, CancellationToken.None);
        string resumeId, blockedId;
        using (DurableAcceptanceStore durable = new(root))
        {
            string hash = Convert.ToHexString(SHA256.HashData(recoveryBytes));
            resumeId = durable.Accept("wire-resume-admit", recoveryManifest, hash, 1, root).OperationId;
            blockedId = durable.Accept("wire-resume-block", recoveryManifest, hash, 1, root).OperationId;
            _ = durable.AuthorizeDispatch(resumeId);
            _ = durable.AuthorizeDispatch(blockedId);
            _ = durable.Reconcile(resumeId, 0, "needs_attention", "manual repair");
            _ = durable.Reconcile(blockedId, 0, "outcome_unknown", "unresolved process outcome");
        }

        RecoveryInspectionView admitted = await client.WorkflowResumeAsync(new(resumeId, 1, true), ct);
        Check(admitted.Revision == 2 && admitted.Decision == "needs_attention", "resume wire CAS admission");
        Check((await client.WorkflowResumeAsync(new(resumeId, 2, true), ct)).Revision == 2,
            "resume wire idempotence");
        CMakeWorkflow.Host.OperationOverview overview = await client.OperationOverviewAsync(new(resumeId), ct);
        Check(!overview.IsFinalized && overview.Nodes.Count == 0 &&
              overview.ReportSha256 is null && overview.Operation.OperationId == resumeId,
            "Preterminal overview must not project an unverified checkpoint as node evidence");
        OperationEventsPage emptyFeed = await client.OperationEventsAsync(new(resumeId, 0, 10), ct);
        Check(emptyFeed.Events.Count == 0 && !emptyFeed.HasMore, "event wire empty feed");
        try
        {
            _ = await client.WorkflowResumeAsync(new(blockedId, 1, true), ct);
            throw new System.Diagnostics.UnreachableException("unknown resume allowed");
        }
        catch (RemoteInvocationException)
        {
        }

        // Baseline after the mutation fixture (reconciliation + SQLite) is created.
        // Only subsequent read-only calls are forbidden from changing files.
        string[] filesBefore = [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()];
        ObservedBuildContext context = await client.ContextInspectAsync(new(build), ct);
        Check(context.State == "ObservedLimited" && context.Configurations.SequenceEqual(["Debug"]),
            "Context wire result");
        TargetResolutionView resolved = await client.TargetResolveAsync(new(build, "worker"), ct);
        Check(resolved.Executable == executable && resolved.Configuration == "Debug",
            "Target resolution changed paths/configuration");
        Check(!File.Exists(executable), "Read-only target resolution built an executable");
        WorkflowPlanView plan = await client.WorkflowPlanAsync(new(manifest), ct);
        WorkflowPlanView standalone = WorkflowQueries.PlanView(await WorkflowPlanner.LoadAsync(manifest, [], null, ct));
        Check(JsonSerializer.Serialize(plan, HostJson.Default.WorkflowPlanView)
              == JsonSerializer.Serialize(standalone, HostJson.Default.WorkflowPlanView),
            "CLI/Host plan semantics differ");
        Check(plan.Nodes[0].Inputs.Count == 1 && plan.Nodes[1].Outputs.Count == 1,
            "Host plan omitted typed artifact declarations.");
        WorkflowReportSnapshot saved = await client.RunGetAsync(new(run), ct);
        Check(saved.Revision == Convert.ToHexString(SHA256.HashData(raw)) && saved.Report.Status == "outcome_unknown",
            "Report revision/status differs from authoritative bytes");
        Check((await File.ReadAllBytesAsync(Path.Combine(run, "run.json"), ct)).SequenceEqual(raw), "Report query performed recovery");
        List<CMakeWorkflow.History.HistoryRun> history = await client.HistoryRunsAsync(new(database, 1), ct);
        Check(history.Count == 1 && history[0].Id == ownId, "Workspace filtering must precede limit");
        CMakeWorkflow.History.HistoryDetail detail = await client.HistoryShowAsync(new(database, ownId), ct);
        Check(detail.Run.Id == ownId, "History detail wire result");
        await Refused(() => client.HistoryShowAsync(new(database, otherId), ct), "path_not_allowed");
        Check((await client.HistoryMetricsAsync(new(database, ownId), ct)).Count == 0, "Unexpected metrics");
        Check((await client.HistoryQueryAsync(new(database, Metric: "duration"), ct)).Count == 0,
            "Unexpected query result");
        List<CMakeWorkflow.History.HistoryDifference> comparison = await client.HistoryCompareAsync(new(database, ownId, ownId), ct);
        Check(comparison.Single().Status == "missing", "History comparison invented measurements");
        CMakeWorkflow.History.HistoryDetectionReport detection = await client.HistoryDetectAsync(new(database, ownId, ownId, "duration", "higher-worse", 5), ct);
        Check(detection.SchemaVersion == 1, "History detect wire result");
        await Refused(() => client.HistoryRunsAsync(new(database, 1001), ct), "invalid_argument");
        await Refused(() => client.HistoryRunsAsync(new(database, Since: "invalid"), ct), "invalid_argument");
        await Refused(() => client.HistoryRunsAsync(new(Path.Combine(root, "missing.sqlite")), ct), "not_found");
        await Refused(() => client.RunGetAsync(new(otherRun), ct), "path_not_allowed");
        await Refused(() => client.ContextInspectAsync(new("relative"), ct), "invalid_argument");
        Check(
            (await File.ReadAllBytesAsync(database, ct)).SequenceEqual(databaseBefore) &&
            !File.Exists(Path.Combine(root, "missing.sqlite")),
            "History query mutated or created a database");
        Check(
            Directory.GetCurrentDirectory() == cwd &&
            filesBefore.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()),
            "Read APIs created files or changed global cwd");

        // Invalid plans are data, not a transport success masquerading as valid.
        string invalid = Path.Combine(root, "invalid.json");
        await File.WriteAllTextAsync(invalid, (await File.ReadAllTextAsync(manifest, ct)).Replace("\"adopt\"", "\"observe\"", StringComparison.Ordinal), CancellationToken.None);
        Check(!(await client.WorkflowPlanAsync(new(invalid), ct)).Valid, "Known-invalid plan lost valid=false");
        if (!OperatingSystem.IsWindows())
        {
            string alias = Path.Combine(root, "escape");
            _ = Directory.CreateSymbolicLink(alias, outside);
            await Refused(() => client.RunGetAsync(new(Path.Combine(alias, "run")), ct), "path_not_allowed");
            Directory.Delete(alias);
            // Checking just the build directory is insufficient: metadata can redirect reads.
            string cache = Path.Combine(build, "CMakeCache.txt");
            File.Move(cache, Path.Combine(outside, "cache.txt"));
            _ = File.CreateSymbolicLink(cache, Path.Combine(outside, "cache.txt"));
            await Refused(() => client.ContextInspectAsync(new(build), ct), "path_not_allowed");
            File.Delete(cache);
            File.Move(Path.Combine(outside, "cache.txt"), cache);
        }

        // Public SDK parity: no Host DTO dependency or manually authored JSON-RPC.
        await using (CMakeWorkflowClient sdk = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, root), ct))
        {
            CMakeWorkflow.Client.HistoryRunPage historyPage = await sdk.History.RunsPageAsync(new(database, Limit: 1), ct);
            Check(historyPage.SchemaVersion == 1 && historyPage.Items.Count == 1 &&
                  historyPage.Items[0].Id == ownId && historyPage.NextCursor is null,
                "SDK history keyset page projection");
            List<CMakeWorkflow.Client.HistoryRun> historyRows = await sdk.History.RunsAsync(new(database, Limit: 1), ct);
            Check(historyRows.Count == 1 && historyRows[0].Id == ownId,
                "SDK history runs projection or filtering");
            CMakeWorkflow.Client.HistoryDetail historyDetail = await sdk.History.ShowAsync(new(database, ownId), ct);
            Check(historyDetail.Run.Id == ownId, "SDK history detail projection");
            Check((await sdk.History.MetricsAsync(new(database, RunId: ownId), ct)).Count == 0,
                "SDK history metrics projection");
            Check((await sdk.History.QueryAsync(new(database, Metric: "duration"), ct)).Count == 0,
                "SDK history query projection");
            List<CMakeWorkflow.Client.HistoryDifference> historyComparison = await sdk.History.CompareAsync(new(database, ownId, ownId), ct);
            Check(historyComparison.Count == 1 && historyComparison[0].Status == "missing",
                "SDK history compare projection");
            CMakeWorkflow.Client.HistoryDetectionReport detected = await sdk.History.DetectAsync(
                new(database, ownId, ownId, "duration", "higher-worse", 5), ct);
            Check(detected.SchemaVersion == 1, "SDK history detection projection");
        }

        // Read-only API must refuse old schema rather than silently migrate it.
        using (SqliteConnection db = new($"Data Source={database};Pooling=False"))
        {
            await db.OpenAsync(ct);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=3";
            _ = await command.ExecuteNonQueryAsync(ct);
        }

        byte[] oldBytes = await File.ReadAllBytesAsync(database, ct);
        await Refused(() => client.HistoryRunsAsync(new(database), ct), "invalid_data");
        Check((await File.ReadAllBytesAsync(database, ct)).SequenceEqual(oldBytes), "Read-only API migrated an old database");
        // Recovery fixtures already created the operations store. Lookup must not mutate it.
        string operationsDb = Path.Combine(root, ".cmakeworkflow", "operations.sqlite");
        byte[] operationsDbBefore = await File.ReadAllBytesAsync(operationsDb, ct);
        await Refused(() => client.OperationFindAsync(new("unknown"), ct), "not_found");
        Check((await File.ReadAllBytesAsync(operationsDb, ct)).SequenceEqual(operationsDbBefore),
            "Lookup modified the operations store");
        string operationId, acceptedWorkspaceId;
        using (DurableAcceptanceStore admissions = new(root))
        {
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifest, ct);
            AcceptedWorkflow accepted = admissions.Accept("stable-client-id", manifest,
                Convert.ToHexString(SHA256.HashData(manifestBytes)), 1, root);
            operationId = accepted.OperationId;
            acceptedWorkspaceId = accepted.WorkspaceId;
        }

        OperationReference found = await client.OperationFindAsync(new("stable-client-id"), ct);
        OperationReference byId = await client.OperationGetAsync(new(operationId), ct);
        Check(found == byId && found.OperationId == operationId && found.WorkspaceId == acceptedWorkspaceId
              && found.State == "accepted_not_dispatched" && found.Kind == "workflow", "Operation read DTO mismatch");
        OperationListPage listed = await client.OperationListAsync(new(Limit: 1), ct);
        Check(listed.SchemaVersion == 1 && listed.Items.Count == 1 &&
              listed.Items[0].OperationId == operationId && listed.Items[0].Kind == "workflow" &&
              listed.NextCursor is not null,
            "Operation list RPC result mismatch");
        OperationListPage nextListed = await client.OperationListAsync(new(Limit: 1, Cursor: listed.NextCursor), ct);
        Check(nextListed.Items.Count == 1 && nextListed.Items[0].OperationId != operationId,
            "Operation list RPC cursor failed to advance");
        await Refused(() => client.OperationListAsync(new(Limit: 0), ct), "invalid_argument");
        await Refused(() => client.OperationListAsync(new(Cursor: "malformed"), ct), "invalid_argument");
        await using (CMakeWorkflowClient sdk = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, root), ct))
        {
            CMakeWorkflow.Client.OperationOverview sdkOverview = await sdk.Operations.GetOverviewAsync(resumeId, ct);
            Check(!sdkOverview.IsFinalized && sdkOverview.Nodes.Count == 0 &&
                  sdkOverview.Operation.OperationId == resumeId,
                "SDK nonterminal overview projection");
            OperationPage sdkPage = await sdk.Operations.ListAsync(new(Limit: 1), ct);
            Check(sdkPage.Items.Count == 1 && sdkPage.Items[0].OperationId == operationId &&
                  sdkPage.Items[0].WorkspaceId == acceptedWorkspaceId,
                "SDK operation listing changed operation identity");
        }
        await Refused(() => client.OperationGetAsync(new("missing"), ct), "not_found");
        await Refused(() => client.OperationFindAsync(new(""), ct), "invalid_argument");
        using (Stream secondWire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc secondRpc = HostConnection.Create(secondWire))
        {
            IHostRpc second = secondRpc.Attach<IHostRpc>();
            secondRpc.StartListening();
            _ = await second.InitializeAsync(new(1, outside), ct);
            OperationListPage empty = await second.OperationListAsync(new(), ct);
            Check(empty.Items.Count == 0 && empty.NextCursor is null,
                "Foreign workspace leaked operations");
            await Refused(() => second.OperationListAsync(new(Cursor: listed.NextCursor), ct),
                "invalid_argument");
            await Refused(() => second.OperationGetAsync(new(operationId), ct), "not_found");
            await Refused(() => second.OperationFindAsync(new("stable-client-id"), ct), "not_found");
            Check(!Directory.Exists(Path.Combine(outside, ".cmakeworkflow")), "Foreign query created store");
        }

        Check((await File.ReadAllBytesAsync(Path.Combine(run, "run.json"), ct)).SequenceEqual(raw),
            "Operation lookup modified runner report");
        // Domain RPCs are typed and confined to this initialized workspace.
        TargetPrepareRequest targetRequest = new("acdcacdcacdcacdcacdcacdcacdcacdc",
            Path.Combine(root, "target-definitions"), "run", build, "worker", Arguments: ["--demo"]);
        TargetPreparation targetReady = await client.TargetPrepareAsync(targetRequest, ct);
        Check(targetReady.ManifestSha256.Length == 64 && File.Exists(targetReady.Manifest),
            "Target preparation did not emit an immutable manifest.");
        Check(targetReady == await client.TargetPrepareAsync(targetRequest, ct),
            "Target preparation should be idempotent across retries.");
        await Refused(() => client.TargetPrepareAsync(targetRequest with { Target = "different" }, ct),
            "request_id_conflict");
        await Refused(() => client.TargetPrepareAsync(targetRequest with { BuildDirectory = outside }, ct),
            "path_not_allowed");
        await Refused(() => client.TargetPrepareAsync(targetRequest with { Action = "unknown" }, ct),
            "invalid_argument");
        await Refused(() => client.ProfileReplayAsync(new(otherRun, run, "run",
            Path.Combine(root, "profile-replays")), ct), "path_not_allowed");
        await Refused(() => client.CampaignMutationAsync(new(outside), ct), "path_not_allowed");
        await Refused(() => client.CampaignWeakMemoryAsync(new(outside, outside), ct), "path_not_allowed");
        await Refused(() => client.CampaignFuzzRunAsync(new(outside, "exceptions",
            Path.Combine(root, "fuzz")), ct), "path_not_allowed");
        await Refused(() => client.CampaignFuzzTriageAsync(new(otherRun), ct), "path_not_allowed");
        await Refused(() => client.CampaignFuzzMergeAsync(new(otherRun), ct), "path_not_allowed");
        CampaignExecutionResult seeded = await client.CampaignFuzzSeedAsync(new("exceptions", Path.Combine(root, "fuzz-seeds")), ct);
        Check(seeded.Status == "succeeded" && seeded.Kind == "fuzz-seed"
                                           && Directory.Exists(seeded.ArtifactDirectory),
            "Typed fuzz seed RPC failed.");
        Check(info.Capabilities.Contains("target/prepare") && info.Capabilities.Contains("profile/replay")
                                                           && info.Capabilities.Contains("dagflow/weakMemory/run") &&
                                                           info.Capabilities.Contains("dagflow/fuzz/merge"),
            "Host capabilities do not match typed RPC contract.");
        Console.WriteLine(
            "Typed target/profile/campaign RPC checks passed: preparation, idempotence, isolation, seed.");
        Console.WriteLine(
            "Operation RPC wire checks passed: find/get, restart persistence, isolation and no dispatch.");
        Console.WriteLine(
            "Read API wire checks passed: plan/context/report/history, revisions, isolation, symlink policy, no mutations and refusals.");
    }
}