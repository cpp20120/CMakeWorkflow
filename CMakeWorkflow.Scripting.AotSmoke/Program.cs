using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Client.History;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Scripting;

Check(!JsonSerializer.IsReflectionEnabledByDefault, "reflection disabled");
TestComposition();
TestScopes();
PerformanceSmoke.Run();
Console.WriteLine("PASS scripting composition, immutable snapshots, and JSON digest");

if (args is ["--self-test"])
{
    return;
}
if (args is not [string endpoint, string root])
{
    throw new ArgumentException("Usage: CMakeWorkflow.Scripting.AotSmoke --self-test | ENDPOINT WORKSPACE");
}

using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
await TestHostAsync(endpoint, Path.GetFullPath(root), deadline.Token);
Console.WriteLine("PASS scripting native execution, admission, lookup, cancellation and error propagation");

static void TestComposition()
{
    Workflow workflow = Workflow.Define("matrix", ".");
    ContextRef shared = workflow.Context("shared", "build/shared", context =>
        context.Toolchain = new(CompilerCandidateId: "gcc"));
    WorkflowNode? callbackNode = null;
    NodeRef configure = workflow.Configure("configure", shared, options: node =>
    {
        callbackNode = node;
        node.Resources.Add("configuration");
    });
    string[] configurations = ["Debug", "Release"];
    string[] compilers = ["gcc", "clang"];
    IEnumerable<NodeRef> Branches() =>
        from configuration in configurations
        from compiler in compilers
        where configuration == "Release" || compiler == "gcc"
        let id = compiler + "-" + configuration
        let build = workflow.Build("build-" + id, shared, "worker", [configure], node => node.ActionCache = true)
        from run in workflow.Benchmark(build, id, 2, new("gcc-Debug", "sequential", []))
        select run;

    WorkflowManifest FreezeMatrix() => workflow.Compose(
        [workflow.CTest("join", shared, Branches()), configure]);
    WorkflowManifest first = FreezeMatrix();
    callbackNode!.Resources.Clear();
    WorkflowManifest second = FreezeMatrix();
    byte[] firstBytes = first.CopyBytes();
    Check(firstBytes.SequenceEqual(second.CopyBytes()), "repeated enumeration bytes");
    Check(first.Sha256 == second.Sha256 && first.Sha256 == Digest(firstBytes), "exact manifest SHA-256");
    Check(first.ToDefinition().Nodes.Count == 11, "filtered matrix or shared dependencies");
    WorkflowDocument detached = first.ToDefinition();
    Check(detached.Nodes[0].DependsOn.Count == 6 && detached.Contexts.Count == 1, "matrix fan-in");
    Check(detached.Nodes.Single(node => node.Id == "configure").Resources.SequenceEqual(["configuration"]),
        "callback resource collection escaped");
    Check(detached.Nodes.Count(node => node.Kind == "build" && node.ActionCache) == 3 &&
        detached.Contexts["shared"].Toolchain?.CompilerCandidateId == "gcc", "existing options lost");
    Check(detached.Nodes.Single(node => node.Id == "timing_gcc-Debug_002").DependsOn
        .SequenceEqual(["timing_gcc-Debug_001"]), "ordered trial prerequisites");
    detached.Nodes.Clear();
    firstBytes[0] = 0;
    Check(first.ToDefinition().Nodes.Count == 11 && first.CopyBytes()[0] == (byte)'{', "snapshot escaped");

    NodeRef left = workflow.Inspect("left", shared, [configure]);
    NodeRef right = workflow.Inspect("right", shared, [configure]);
    NodeRef join = workflow.CTest("diamond", shared, [left, right, left]);
    Check(workflow.Compose([left]).ToDefinition().Nodes.Count == 2, "branch isolation");
    Check(workflow.Compose([join]).ToDefinition().Nodes.Select(node => node.Id)
        .SequenceEqual(["diamond", "left", "right", "configure"]), "diamond determinism");
    Reject<ArgumentException>(() => workflow.Compose([left, workflow.Inspect("left", shared)]));
    Reject<ArgumentException>(() => workflow.Compose([left, workflow.Inspect("other", workflow.Context("shared", "other"))]));
    Reject<ArgumentException>(() => workflow.Inspect("raw", shared, options: node => node.DependsOn.Add("configure")));
    Workflow foreign = Workflow.Define("foreign");
    ContextRef foreignContext = foreign.Context("shared", "build/shared");
    Reject<ArgumentException>(() => workflow.Configure("foreign", foreignContext));
    Reject<ArgumentException>(() => workflow.Configure("foreign", shared, [foreign.Inspect("left", foreignContext)]));
    Reject<ArgumentException>(() => workflow.Run("foreign", foreign.Build("build", foreignContext, "worker")));
}

static void TestScopes()
{
    Workflow workflow = Workflow.Define("scopes", ".");
    ContextRef common = workflow.Context("common", "common");
    NodeRef source = workflow.Inspect("source", common);
    WorkflowScope root = workflow.Scope("matrix");

    (TargetBuildRef Build, NodeRef Test) Fragment(WorkflowScope instance)
    {
        WorkflowScope native = instance.Scope("native");
        ContextRef context = workflow.Context(native.Id("context"), instance.InstanceId);
        NodeRef configure = workflow.Configure(native.Id("configure"), context, [source]);
        TargetBuildRef build = workflow.Build(native.Id("build"), context, "worker", [configure]);
        return (build, workflow.CTest(native.Id("test"), context, [build]));
    }

    (TargetBuildRef alphaBuild, NodeRef alphaTest) = Fragment(root.Scope("alpha"));
    (_, NodeRef betaTest) = Fragment(root.Scope("beta"));
    NodeRef join = workflow.Inspect("join", common, [alphaTest, betaTest]);
    WorkflowDocument combined = workflow.Compose([join]).ToDefinition();
    Check(combined.Nodes.Count == 8 && combined.Contexts.Count == 3, "nested scopes and shared prerequisite");
    Check(workflow.Compose([alphaBuild]).ToDefinition().Nodes.All(node => !node.Id.Contains("beta", StringComparison.Ordinal)),
        "scope filtering");
    Check(workflow.Compose([Fragment(root.Scope("alpha")).Test]).Sha256 == workflow.Compose([alphaTest]).Sha256,
        "independent repeated fragment identity");
    Reject<ArgumentException>(() => workflow.Compose([alphaTest, Fragment(root.Scope("alpha")).Test]));
    Reject<ArgumentException>(() => root.Scope("alpha.native"));
}

static async Task TestHostAsync(string endpoint, string workspace, CancellationToken ct)
{
    await File.WriteAllTextAsync(Path.Combine(workspace, "CMakeLists.txt"), """
        cmake_minimum_required(VERSION 3.26)
        project(ScriptingSmoke LANGUAGES CXX)
        find_package(Threads REQUIRED)
        add_executable(worker worker.cpp)
        target_link_libraries(worker PRIVATE Threads::Threads)
        target_compile_features(worker PRIVATE cxx_std_11)
        enable_testing()
        add_test(NAME scripting-smoke COMMAND worker)
        """, ct);
    await File.WriteAllTextAsync(Path.Combine(workspace, "CMakePresets.json"), """
        { "version": 6, "configurePresets": [
          { "name": "smoke", "generator": "Ninja", "binaryDir": "${sourceDir}/build" }
        ] }
        """, ct);
    await File.WriteAllTextAsync(Path.Combine(workspace, "worker.cpp"), """
        #include <chrono>
        #include <fstream>
        #include <string>
        #include <thread>
        int main(int argc, char** argv) {
            if (argc > 1 && std::string(argv[1]) == "--cache") {
                std::ifstream input("worker.cpp");
                std::ofstream output("cached.txt");
                output << input.rdbuf();
                return !output;
            }
            if (argc > 1 && std::string(argv[1]) == "--hold") {
                std::ofstream("started.marker") << "started";
                std::this_thread::sleep_for(std::chrono::seconds(60));
            }
            return argc > 1 && std::string(argv[1]) == "--fail" ? 17 : 0;
        }
        """, ct);

    Workflow workflow = Workflow.Define("native-smoke", ".");
    ContextRef context = workflow.Context("native", "build", spec => spec.ConfigurePreset = "smoke");
    NodeRef configure = workflow.Configure("configure", context);
    TargetBuildRef build = workflow.Build("build", context, "worker", [configure]);
    WorkflowManifest manifest = workflow.Compose([
        workflow.CTest("test", context, [build]), workflow.Run("timing_native_001", build), build, configure]);
    string[] saved = await Task.WhenAll(manifest.SaveAsync(workspace, ct), manifest.SaveAsync(workspace, ct));
    string path = saved[0];
    byte[] written = await File.ReadAllBytesAsync(path, ct);
    Check(path == saved[1] && Digest(written) == manifest.Sha256, "atomic publication");
    string legacy = Path.Combine(workspace, "authored.json");
    byte[] authored = """
        {
          "schemaVersion": 2,
          "contexts": { "native": { "sourceDirectory": ".", "buildDirectory": "build", "configurePreset": "smoke" } },
          "nodes": [
            { "id": "configure", "kind": "configure", "context": "native" },
            { "id": "build", "kind": "build", "context": "native", "target": "worker", "dependsOn": ["configure"] },
            { "id": "test", "kind": "ctest", "context": "native", "dependsOn": ["build"] },
            { "id": "timing_native_001", "kind": "run", "from": "build" }
          ]
        }
        """u8.ToArray();
    await File.WriteAllBytesAsync(legacy, authored, ct);
    string results = Path.Combine(workspace, "results");
    await using CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
    PreparedWorkflow[] prepared = await Task.WhenAll(
        manifest.PrepareAsync(client.Workflows, results, ct: ct),
        manifest.PrepareAsync(client.Workflows, results, ct: ct));
    Check(prepared[0] == prepared[1] && prepared[0].Manifest == path, "verified reuse of existing Host manifest");
    string absentBase = Path.Combine(workspace, "stage");
    _ = Directory.CreateDirectory(absentBase);
    PreparedWorkflow[] published = await Task.WhenAll(
        manifest.PrepareAsync(client.Workflows, results, "stage", ct),
        manifest.PrepareAsync(client.Workflows, results, "stage", ct));
    Check(published[0] == published[1] && File.Exists(published[0].Manifest) &&
        !Directory.EnumerateFiles(workspace, ".cmwf-manifest-*.tmp").Any(), "concurrent publication or cleanup");
    await RefusedAsync(() => manifest.PrepareAsync(client.Workflows, results, "../escape", ct),
        (CMakeWorkflowRpcRefusalException error) => error.Code == "path_not_allowed");
    await RefusedAsync(() => client.Workflows.PrepareManifestAsync(written, new string('0', 64), results, ct: ct),
        (InvalidDataException _) => true);
    if (!OperatingSystem.IsWindows())
    {
        _ = Directory.CreateSymbolicLink(Path.Combine(workspace, "alias"), absentBase);
        await RefusedAsync(() => manifest.PrepareAsync(client.Workflows, results, "alias", ct),
            (CMakeWorkflowRpcRefusalException error) => error.Code == "path_not_allowed");
    }
    WorkflowPreflightResult preflight = await client.Workflows.PreflightAsync(
        manifest.AtHost(prepared[0].Manifest, prepared[0].ResultsDirectory) with
        {
            HistoryPolicy = new(HistoryPolicyMode.Default)
        }, ct);
    WorkflowPreflightResult equivalent = await client.Workflows.PreflightAsync(new(legacy, Digest(authored), 1, results), ct);
    Check(preflight.Valid && equivalent.Valid && preflight.Nodes.Count == 4 &&
        PlanRows(preflight).SequenceEqual(PlanRows(equivalent)), "JSON and C# equivalent Host plans: " +
        string.Join(", ", preflight.Nodes.SelectMany(node => node.Diagnostics).Select(d => d.Message)));
    string requestId = "scripting-smoke-" + Guid.NewGuid().ToString("N");
    StartWorkflowOptions start = preflight.CreateStartOptions(requestId);
    Check(!Directory.Exists(results) && !Directory.Exists(Path.Combine(workspace, "build")), "preflight dispatched");

    await TestInvalidGraphAsync(client, manifest, workspace, cycle: true, ct);
    await TestInvalidGraphAsync(client, manifest, workspace, cycle: false, ct);
    await File.AppendAllTextAsync(path, " ", ct);
    await RefusedAsync(() => manifest.SaveAsync(workspace, ct), (InvalidDataException _) => true);
    await RefusedAsync(() => client.Workflows.StartAsync(start, ct),
        (CMakeWorkflowRpcRefusalException ex) => ex.Code == "stale_manifest");
    Check(!Directory.Exists(results) && !Directory.Exists(Path.Combine(workspace, "build")), "stale admission dispatched");
    await File.WriteAllBytesAsync(path, written, ct);

    WorkflowOperation operation = await client.Workflows.StartAsync(start, ct);
    WorkflowOperation repeated = await client.Workflows.StartAsync(start, ct);
    await using CMakeWorkflowClient reconnected = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
    OperationSnapshot found = await reconnected.Operations.FindAsync(requestId, ct);
    Check(operation.OperationId == repeated.OperationId && operation.OperationId == found.OperationId &&
        string.Equals(found.ManifestSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase), "duplicate admission or lost-response lookup");
    OperationSnapshot done = await operation.WaitForCompletionAsync(ct: ct);
    OperationReportSnapshot report = await operation.GetReportAsync(ct);
    Check(done.State == "succeeded" && report.Report.GetProperty("nodes").EnumerateArray().Count(node =>
        node.GetProperty("status").GetString() == "succeeded") == 4,
        "actual configure/build/CTest: " + report.Report.GetRawText());

    HistoryIndexStatus status = await operation.WaitForHistoryAsync(ct: ct);
    OperationHistoryRuns indexed = await reconnected.History.GetOperationRunsAsync(operation.OperationId, ct);
    Check(status.State == HistoryIndexState.Indexed && indexed.Runs.Count == 1, "package-only indexed run receipt");
    IReadOnlyList<HistoryMeasurement> measurements = await reconnected.Operations.GetHandle(operation.OperationId)
        .GetMeasurementsAsync("process_wall_time", ct: ct);
    Check(measurements.Count != 0 && measurements.All(row => row.RunId == indexed.Runs[0].RunId),
        "package-only operation measurements");
    string runId = indexed.Runs.Single().RunId;
    List<HistoryDifference> compared = await reconnected.History.CompareAsync(new(indexed.DatabasePath, runId, runId)
    {
        Selection = new("process_wall_time") { BaselineVariant = "native", CandidateVariant = "native" }
    }, ct);
    Check(compared is [{ Metric: "process_wall_time", RunsSucceeded: true }], "package-only selected comparison");
    int pages = 0;
    await foreach (HistoryRunPage page in reconnected.History.EnumerateRunPagesAsync(new(indexed.DatabasePath, 1), 1, ct))
    {
        Check(page.Items.Single().Id == indexed.Runs.Single().RunId, "bounded page");
        pages++;
    }
    Check(pages == 1, "page budget");

    NodeRef cached = workflow.Run("cache", build, options: node =>
    {
        node.Arguments.Add("--cache");
        node.WorkingDirectory = ".";
        node.ActionCache = true;
        node.Inputs.Add(new() { Path = "worker.cpp" });
        node.Outputs.Add(new() { Name = "cached", Path = "cached.txt", Fresh = true });
    });
    WorkflowManifest cachedManifest = workflow.Compose([cached]);
    string cachePath = await cachedManifest.SaveAsync(workspace, ct);
    string cachedOutput = Path.Combine(workspace, "cached.txt");
    await CheckCacheAsync(expectHit: false);
    File.Delete(cachedOutput);
    await CheckCacheAsync(expectHit: true);

    WorkflowOperation failing = await StartRunAsync("--fail", "failing");
    Check((await failing.WaitForCompletionAsync(ct: ct)).State == "failed", "native failure propagation");
    Check((await failing.GetReportAsync(ct)).Report.GetProperty("nodes").EnumerateArray()
        .Single(node => node.GetProperty("id").GetString() == "failing").GetProperty("exitCode").GetInt32() == 17,
        "failed operation report");

    WorkflowOperation holding = await StartRunAsync("--hold", "holding");
    while (!File.Exists(Path.Combine(workspace, "started.marker")))
    {
        ct.ThrowIfCancellationRequested();
        Check((await holding.GetSnapshotAsync(ct)).State is not ("failed" or "canceled" or "succeeded"),
            "hold reached terminal state before native execution");
        await Task.Delay(100, ct);
    }
    RecoverySnapshot recovery = await holding.InspectRecoveryAsync(ct);
    _ = await holding.RequestCancellationAsync(recovery.Revision, ct);
    Check((await holding.WaitForCompletionAsync(ct: ct)).State == "canceled", "durable in-flight cancellation");

    async Task CheckCacheAsync(bool expectHit)
    {
        string label = expectHit ? "restore" : "publish";
        WorkflowPreflightResult plan = await client.Workflows.PreflightAsync(
            cachedManifest.AtHost(cachePath, Path.Combine(workspace, label)), ct);
        WorkflowOperation cacheOperation = await client.Workflows.StartAsync(plan.CreateStartOptions(requestId + "-" + label), ct);
        OperationSnapshot status = await cacheOperation.WaitForCompletionAsync(ct: ct);
        OperationReportSnapshot cacheReport = await cacheOperation.GetReportAsync(ct);
        Check(status.State == "succeeded" && cacheReport.Report.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetString() == "cache").GetProperty("actionCacheHit").GetBoolean() == expectHit,
            "verified action cache " + label + ": " + cacheReport.Report.GetRawText());
        byte[] actual = await File.ReadAllBytesAsync(cachedOutput, ct);
        byte[] expected = await File.ReadAllBytesAsync(Path.Combine(workspace, "worker.cpp"), ct);
        Check(actual.SequenceEqual(expected), "cached artifact bytes");
    }

    async Task<WorkflowOperation> StartRunAsync(string argument, string name)
    {
        NodeRef run = workflow.Run(name, build, options: node =>
        {
            node.Arguments.Add(argument);
            node.WorkingDirectory = ".";
        });
        WorkflowManifest runManifest = workflow.Compose([configure, build, run]);
        string runPath = await runManifest.SaveAsync(workspace, ct);
        WorkflowPreflightResult plan = await client.Workflows.PreflightAsync(
            runManifest.AtHost(runPath, Path.Combine(workspace, name)), ct);
        return await client.Workflows.StartAsync(plan.CreateStartOptions(requestId + "-" + name), ct);
    }
}

static async Task TestInvalidGraphAsync(CMakeWorkflowClient client, WorkflowManifest manifest, string workspace,
    bool cycle, CancellationToken ct)
{
    WorkflowDocument invalid = manifest.ToDefinition();
    invalid.Nodes.Single(node => node.Id == "configure").DependsOn.Add(cycle ? "test" : "absent");
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, WorkflowDefinitionJson.Default.WorkflowDocument);
    string path = Path.Combine(workspace, cycle ? "cycle.json" : "missing.json");
    await File.WriteAllBytesAsync(path, bytes, ct);
    await RefusedAsync(() => client.Workflows.PreflightAsync(new(path, Digest(bytes), 1, Path.Combine(workspace, "invalid")), ct),
        (CMakeWorkflowRpcRefusalException ex) => ex.Code == "invalid_argument" &&
            ex.Message.Contains(cycle ? "dependency cycle" : "unknown dependency", StringComparison.Ordinal));
}

static IEnumerable<string> PlanRows(WorkflowPreflightResult plan) => plan.Nodes
    .OrderBy(node => node.Id, StringComparer.Ordinal)
    .Select(node => string.Join("|", node.Id, node.Kind, node.Target, node.BuildDirectory,
        node.Configuration, string.Join(",", node.Dependencies), string.Join(",", node.Resources), node.Exclusive.ToString()));

static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static void Check(bool valid, string message)
{
    if (!valid)
    {
        throw new UnreachableException(message);
    }
}

static void Reject<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new UnreachableException($"Expected {typeof(T).Name}.");
}

static async Task RefusedAsync<T>(Func<Task> action, Func<T, bool> expected) where T : Exception
{
    try
    {
        await action();
    }
    catch (T ex) when (expected(ex))
    {
        return;
    }
    throw new UnreachableException($"Expected {typeof(T).Name}.");
}
