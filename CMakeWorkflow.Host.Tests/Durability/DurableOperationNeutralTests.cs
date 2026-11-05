using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Host;

internal static class DurableOperationNeutralTests
{
    public static void Run(string parent)
    {
        string workspace = Path.Combine(parent, "neutral-operation-workspace");
        _ = Directory.CreateDirectory(workspace);
        // This is deliberately not a CMake workflow manifest.
        string snapshotPath = Path.Combine(workspace, "unrelated-job.json");
        byte[] snapshot = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"tool\":\"example\",\"operation\":\"lint\"}");
        File.WriteAllBytes(snapshotPath, snapshot);
        string sha = Convert.ToHexString(SHA256.HashData(snapshot));
        string digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("lint/start:v1\n" + snapshotPath + "\n" + sha)));
        DurableImmutableRequest request = new("neutral-job-1", snapshotPath, sha,
            workspace, digest);

        string operationId;
        using (DurableAcceptanceStore store = new(workspace))
        {
            AcceptedWorkflow accepted = store.AcceptImmutable(request);
            operationId = accepted.OperationId;
            if (accepted.State != "accepted_not_dispatched" ||
                !store.GetSnapshot(operationId).SequenceEqual(snapshot))
            {
                throw new System.Diagnostics.UnreachableException("neutral immutable snapshot was not accepted");
            }
            // The retry must use the persisted digest, not the changed source bytes.
            File.WriteAllText(snapshotPath, "edited after acceptance");
            if (store.AcceptImmutable(request).OperationId != operationId)
            {
                throw new System.Diagnostics.UnreachableException("neutral retry was not idempotent");
            }

            try
            {
                _ = store.AcceptImmutable(request with { RequestDigest = new string('A', 64) });
                throw new System.Diagnostics.UnreachableException("changed neutral request digest was accepted");
            }
            catch (InvalidOperationException e) when (e.Message == "request_id_conflict")
            {
            }
            if (store.AuthorizeDispatch(operationId).State != "dispatch_authorized")
            {
                throw new System.Diagnostics.UnreachableException("neutral authorization failed");
            }
        }

        using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
        {
            if (reopened.Get(operationId)?.State != "dispatch_authorized" ||
                !reopened.GetSnapshot(operationId).SequenceEqual(snapshot))
            {
                throw new System.Diagnostics.UnreachableException("neutral operation state did not survive restart");
            }
        }

        // Exercise the same SQLite checkpoint-binding and finalization protocol
        // with a non-CMake codec, after the immutable snapshot has been accepted.
        string runDirectory = Path.Combine(workspace, "neutral-results");
        _ = Directory.CreateDirectory(runDirectory);
        using (DurableAcceptanceStore store = new(workspace, false, new NeutralFixtureCheckpointCodec()))
        {
            store.ReserveResultsDirectory(operationId, runDirectory);
            byte[] runningBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + sha + "\",\"state\":\"running\",\"running\":true}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), runningBytes);
            string binding = store.ConfirmRunnerCheckpoint(operationId, runDirectory);
            if (binding != Convert.ToHexString(SHA256.HashData(runningBytes)) ||
                store.GetDispatchCheckpoint(operationId)?.CheckpointSha256 != binding)
            {
                throw new System.Diagnostics.UnreachableException("neutral checkpoint exact byte binding failed");
            }

            byte[] foreignBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + new string('0', 64) +
                "\",\"state\":\"running\",\"running\":true}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), foreignBytes);
            try
            {
                _ = store.ConfirmRunnerCheckpoint(operationId, runDirectory);
                throw new System.Diagnostics.UnreachableException("foreign neutral checkpoint was confirmed");
            }
            catch (InvalidOperationException e) when (e.Message == "dispatch_checkpoint_mismatch")
            {
            }
            if (store.GetDispatchCheckpoint(operationId)?.CheckpointSha256 != binding)
            {
                throw new System.Diagnostics.UnreachableException("rejected checkpoint replaced the durable binding");
            }

            byte[] terminalBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + sha + "\",\"state\":\"succeeded\",\"running\":false}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), terminalBytes);
            DurableAcceptanceStore.Finalization final = store.FinalizeExecution(operationId, runDirectory, 0);
            if (final.State != "succeeded" || final.ReportSha256 !=
                Convert.ToHexString(SHA256.HashData(terminalBytes)))
            {
                throw new System.Diagnostics.UnreachableException("neutral finalization did not bind exact bytes");
            }

            if (store.FinalizeExecution(operationId, runDirectory, 0) != final)
            {
                throw new System.Diagnostics.UnreachableException("neutral finalization not idempotent");
            }
        }

        using (DurableAcceptanceStore reopened = new(workspace, readOnly: true))
        {
            if (reopened.GetFinalization(operationId)?.State != "succeeded" ||
                reopened.GetDispatchCheckpoint(operationId) is null)
            {
                throw new System.Diagnostics.UnreachableException("neutral finalization did not survive restart");
            }
        }

        Console.WriteLine("Neutral durable operation checks passed: snapshot, dispatch, exact-byte binding, finalization, restart.");
    }
}

// A deliberately non-CMake operation codec. It validates its own domain format;
// the SQLite authority knows only snapshot identity, validated operation facts
// and the actual bytes whose hashes it persists.
internal sealed class NeutralFixtureCheckpointCodec : IOperationCheckpointEvidenceCodec
{
    private static (string Hash, string State, bool Running) Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        return (root.GetProperty("snapshot").GetString()!, root.GetProperty("state").GetString()!,
            root.GetProperty("running").GetBoolean());
    }

    public OperationCheckpointEvidence ForDispatch(byte[] bytes)
    {
        (string? hash, string? state, bool running) = Parse(bytes);
        return state != "running" || !running ? throw new InvalidDataException("Neutral fixture has no admitted dispatch.") : new(hash);
    }

    public OperationCheckpointEvidence ForResume(byte[] bytes)
    {
        (string? hash, string? state, bool running) = Parse(bytes);
        return state != "needs_attention" || running ? throw new InvalidOperationException("resume_checkpoint_not_safe") : new(hash);
    }

    public OperationCheckpointEvidence ForFinalization(byte[] bytes, int exitCode)
    {
        (string? hash, string? state, bool running) = Parse(bytes);
        return state != "succeeded" || running || exitCode != 0
            ? throw new InvalidOperationException("runner_exit_report_mismatch")
            : new(hash, state);
    }
}
