using System.Diagnostics;
using System.Globalization;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Coordinator;
using CMakeWorkflow.Pipelines.Persistence;
using Microsoft.Data.Sqlite;

internal static class PipelineChildCoordinatorTests
{
    private const string Sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p2b-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);
        try
        {
            await SuccessAndDependenciesAsync(Path.Combine(root, "success.sqlite"));
            await LostReplyRecoveryAsync(Path.Combine(root, "lost-reply.sqlite"));
            await IntentWithoutDispatchAsync(Path.Combine(root, "before-dispatch.sqlite"));
            await MismatchedIdentityAsync(Path.Combine(root, "identity.sqlite"));
            await UnsafeLaunchRefusedAsync(Path.Combine(root, "validation.sqlite"));
            UpgradeSchema(Path.Combine(root, "migration.sqlite"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("P2b child coordinator: durable launch, lost reply, recovery and finalization passed.");
    }

    private static PipelineDocument Document() => new()
    {
        SchemaVersion = 1,
        Name = "pipeline",
        Stages =
        [
            Local("build", "workflows/build.json"),
            Local("test", "workflows/test.json", ["build"])
        ]
    };

    private static PipelineStage Local(string id, string manifest, IReadOnlyList<string>? dependsOn = null) => new()
    {
        Id = id,
        Kind = "workflow",
        DependsOn = dependsOn ?? [],
        Workflow = new PipelineWorkflowStage { Manifest = manifest }
    };

    private static PipelineChildLaunch Launch(string path = "workflows/build.json", string workspace = "/workspace") =>
        new(workspace, workspace + "/" + path, Sha, 2, workspace + "/out/pipeline");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> operation, string message) where T : Exception
    {
        try
        {
            await operation();
        }
        catch (T)
        {
            return;
        }

        throw new UnreachableException(message);
    }

    private static async Task SuccessAndDependenciesAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        FakeHost host = new();
        PipelineChildCoordinator coordinator = new(ledger, host);
        PipelineRunSnapshot accepted = ledger.Accept("pipeline-success", Document());
        await RefusedAsync<InvalidOperationException>(async () =>
        {
            _ = await coordinator.StartReadyAsync(accepted.OperationId, "test", Launch("workflows/test.json"));
        },
            "A blocked stage was started.");
        Check(host.StartCount == 0, "Blocked stage dispatched a child.");

        PipelineRunSnapshot started = await coordinator.StartReadyAsync(accepted.OperationId, "build", Launch());
        Check(started.Stages[0].Status == PipelineStageStates.WaitingExternal && host.StartCount == 1,
            "Initial child not bound after durable Host acceptance.");
        PipelineChildLaunch launch = ledger.GetChildLaunch(accepted.OperationId, "build")!;
        Check(launch.ManifestSha256 == Sha && launch.Manifest == "/workspace/workflows/build.json",
            "Admission identity was not pinned transactionally.");
        PipelineRunSnapshot pending = await coordinator.RecoverAsync(accepted.OperationId, "build");
        Check(pending.Revision == started.Revision, "Nonterminal child advanced pipeline.");
        host.FinalizeChild(started.Stages[0].ChildRequestId!, "succeeded");
        PipelineRunSnapshot buildDone = await coordinator.RecoverAsync(accepted.OperationId, "build");
        Check(buildDone.Stages[0].Status == PipelineStageStates.Succeeded &&
              buildDone.Stages[0].EvidenceReference!.Contains("sha256:", StringComparison.Ordinal),
            "Finalized Host report did not advance stage.");

        PipelineRunSnapshot test = await coordinator.StartReadyAsync(accepted.OperationId, "test",
            Launch("workflows/test.json"));
        host.FinalizeChild(test.Stages[1].ChildRequestId!, "failed");
        PipelineRunSnapshot failure = await coordinator.RecoverAsync(accepted.OperationId, "test");
        Check(failure.Stages[1].Status == PipelineStageStates.Failed && failure.Status == "failed",
            "Failed child incorrectly marked successful.");
        Check(ledger.Events(failure.OperationId).Select(x => x.Revision)
                .SequenceEqual(Enumerable.Range(0, 7).Select(i => (long)i)),
            "Ledger journal not contiguous across child launch and finalization.");
    }

    private static async Task LostReplyRecoveryAsync(string path)
    {
        string pipelineId;
        FakeHost host = new() { DropStartReply = true };
        using (PipelineLedger original = new(path))
        {
            PipelineRunSnapshot accepted = original.Accept("lost-reply", Document());
            pipelineId = accepted.OperationId;
            PipelineChildCoordinator coordinator = new(original, host);
            await RefusedAsync<IOException>(async () =>
                {
                    _ = await coordinator.StartReadyAsync(pipelineId, "build", Launch());
                },
                "Lost start reply was not propagated.");
            Check(original.Get(pipelineId).Stages[0].Status == PipelineStageStates.DispatchIntent &&
                  host.StartCount == 1, "Lost reply improperly rolled back dispatch intent.");
        }

        // New process / ledger connection: explicit lookup, never a second StartAsync.
        using PipelineLedger reopened = new(path);
        PipelineChildCoordinator recovered = new(reopened, host);
        PipelineRunSnapshot bound = await recovered.RecoverAsync(pipelineId, "build");
        Check(bound.Stages[0].Status == PipelineStageStates.WaitingExternal && host.StartCount == 1 &&
              host.FindCount == 1, "Recovery redispatched instead of finding the accepted child.");
        host.FinalizeChild(bound.Stages[0].ChildRequestId!, "succeeded");
        PipelineRunSnapshot done = await recovered.RecoverAsync(pipelineId, "build");
        Check(done.Stages[0].Status == PipelineStageStates.Succeeded && host.StartCount == 1,
            "Recovery failed to record verified completion or redispatched.");
        Check((await recovered.RecoverAsync(pipelineId, "build")).Revision == done.Revision,
            "Terminal stage recovery was not idempotent.");
    }

    private static async Task IntentWithoutDispatchAsync(string path)
    {
        string pipelineId;
        using (PipelineLedger initial = new(path))
        {
            PipelineRunSnapshot admitted = initial.Accept("crash-before-dispatch", Document());
            pipelineId = admitted.OperationId;
            _ = initial.ReserveWorkflow(pipelineId, "build", admitted.Revision, Launch());
        }

        FakeHost host = new();
        using PipelineLedger reopened = new(path);
        PipelineChildCoordinator coordinator = new(reopened, host);
        await RefusedAsync<KeyNotFoundException>(async () =>
        {
            _ = await coordinator.RecoverAsync(pipelineId, "build");
        },
            "Missing child lookup should remain unresolved.");
        Check(reopened.Get(pipelineId).Stages[0].Status == PipelineStageStates.DispatchIntent &&
              host.StartCount == 0, "Missing lookup authorized a second start.");
        await RefusedAsync<InvalidOperationException>(async () =>
        {
            _ = await coordinator.StartReadyAsync(pipelineId, "build", Launch());
        },
            "Reserved stage was started again.");
        Check(host.StartCount == 0, "Repeated dispatch after crash.");
    }

    private static async Task MismatchedIdentityAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        FakeHost host = new() { SpoofStartHash = true };
        PipelineRunSnapshot accepted = ledger.Accept("mismatch", Document());
        PipelineChildCoordinator coordinator = new(ledger, host);
        await RefusedAsync<InvalidDataException>(async () =>
        {
            _ = await coordinator.StartReadyAsync(accepted.OperationId, "build", Launch());
        },
            "Unexpected child manifest hash was accepted.");
        Check(ledger.Get(accepted.OperationId).Stages[0].Status == PipelineStageStates.DispatchIntent,
            "Identity mismatch was treated as an acknowledged binding.");
        host.SpoofFindHash = true;
        await RefusedAsync<InvalidDataException>(async () =>
        {
            _ = await coordinator.RecoverAsync(accepted.OperationId, "build");
        },
            "Mismatched recovered child was bound.");
        Check(ledger.Get(accepted.OperationId).Stages[0].Status == PipelineStageStates.DispatchIntent &&
              host.StartCount == 1, "Unsafe identity recovery changed ledger state.");
    }

    private static async Task UnsafeLaunchRefusedAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        FakeHost host = new();
        PipelineChildCoordinator coordinator = new(ledger, host);
        PipelineRunSnapshot accepted = ledger.Accept("unsafe", Document());
        await RefusedAsync<InvalidOperationException>(async () =>
        {
            _ = await coordinator.StartReadyAsync(accepted.OperationId, "build", Launch(workspace: "/different"));
        },
            "Different Host workspace was admitted.");
        await RefusedAsync<InvalidOperationException>(async () =>
        {
            _ = await coordinator.StartReadyAsync(accepted.OperationId, "build", Launch("workflows/other.json"));
        },
            "Mismatched manifest path was admitted.");
        Check(ledger.Get(accepted.OperationId).Revision == 0 && host.StartCount == 0,
            "Invalid launch mutated ledger or dispatched.");
    }

    private static void UpgradeSchema(string path)
    {
        string operationId;
        using (PipelineLedger ledger = new(path))
        {
            operationId = ledger.Accept("migration", Document()).OperationId;
        }

        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE pipeline_child_launches; PRAGMA user_version=1;";
            _ = cmd.ExecuteNonQuery();
        }

        using PipelineLedger upgraded = new(path);
        Check(upgraded.Get(operationId).Revision == 0 &&
              upgraded.GetChildLaunch(operationId, "build") is null,
            "Schema v1 data was not readable after additive migration.");
    }

    private sealed class FakeHost : IWorkflowChildHost
    {
        private readonly Dictionary<string, WorkflowChildIdentity> children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowChildProgress> progress = new(StringComparer.Ordinal);
        private int sequence;

        public string WorkspaceDirectory => "/workspace";
        public int StartCount { get; private set; }
        public int FindCount { get; private set; }
        public bool DropStartReply { get; init; }
        public bool SpoofStartHash { get; init; }
        public bool SpoofFindHash { get; set; }

        public Task ValidatePreflightAsync(PipelineChildLaunch launch,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<WorkflowChildIdentity> StartAsync(PipelineChildLaunch launch, string childRequestId,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            WorkflowChildIdentity actual = new("child-" + ++sequence, childRequestId, "workflow", launch.ManifestSha256);
            children.Add(childRequestId, actual);
            progress.Add(actual.OperationId, new(actual, "dispatch_authorized", false, null));
            return DropStartReply
                ? throw new IOException("Response lost after durable Host acceptance.")
                : Task.FromResult(SpoofStartHash ? actual with { ManifestSha256 = new string('F', 64) } : actual);
        }

        public Task<WorkflowChildIdentity> FindAsync(string childRequestId,
            CancellationToken cancellationToken = default)
        {
            FindCount++;
            WorkflowChildIdentity found = children[childRequestId];
            return Task.FromResult(SpoofFindHash ? found with { ManifestSha256 = new string('F', 64) } : found);
        }

        public Task<WorkflowChildProgress> ObserveAsync(string childOperationId,
            CancellationToken cancellationToken = default) => Task.FromResult(progress[childOperationId]);

        public void FinalizeChild(string requestId, string state)
        {
            WorkflowChildIdentity identity = children[requestId];
            progress[identity.OperationId] = new(identity, state, true, Sha);
        }
    }
}
