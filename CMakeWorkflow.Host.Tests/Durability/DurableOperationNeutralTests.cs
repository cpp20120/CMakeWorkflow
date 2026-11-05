using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class DurableOperationNeutralTests
{
    public static void Run(string parent)
    {
        var workspace = Path.Combine(parent, "neutral-operation-workspace");
        Directory.CreateDirectory(workspace);
        // This is deliberately not a CMake workflow manifest.
        var snapshotPath = Path.Combine(workspace, "unrelated-job.json");
        var snapshot = Encoding.UTF8.GetBytes("{\"tool\":\"example\",\"operation\":\"lint\"}");
        File.WriteAllBytes(snapshotPath, snapshot);
        var sha = Convert.ToHexString(SHA256.HashData(snapshot));
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("lint/start:v1\n" + snapshotPath + "\n" + sha)));
        var request = new DurableImmutableRequest("neutral-job-1", snapshotPath, sha,
            workspace, digest);

        string operationId;
        using (var store = new DurableAcceptanceStore(workspace))
        {
            var accepted = store.AcceptImmutable(request);
            operationId = accepted.OperationId;
            if (accepted.State != "accepted_not_dispatched" ||
                !store.GetSnapshot(operationId).SequenceEqual(snapshot))
                throw new Exception("neutral immutable snapshot was not accepted");
            // The retry must use the persisted digest, not the changed source bytes.
            File.WriteAllText(snapshotPath, "edited after acceptance");
            if (store.AcceptImmutable(request).OperationId != operationId)
                throw new Exception("neutral retry was not idempotent");
            try
            {
                store.AcceptImmutable(request with { RequestDigest = new string('A', 64) });
                throw new Exception("changed neutral request digest was accepted");
            }
            catch (InvalidOperationException e) when (e.Message == "request_id_conflict")
            {
            }
            if (store.AuthorizeDispatch(operationId).State != "dispatch_authorized")
                throw new Exception("neutral authorization failed");
        }

        using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            if (reopened.Get(operationId)?.State != "dispatch_authorized" ||
                !reopened.GetSnapshot(operationId).SequenceEqual(snapshot))
                throw new Exception("neutral operation state did not survive restart");
        }

        // Exercise the same SQLite checkpoint-binding and finalization protocol
        // with a non-CMake codec, after the immutable snapshot has been accepted.
        var runDirectory = Path.Combine(workspace, "neutral-results");
        Directory.CreateDirectory(runDirectory);
        using (var store = new DurableAcceptanceStore(workspace, false, new NeutralFixtureCheckpointCodec()))
        {
            store.ReserveResultsDirectory(operationId, runDirectory);
            var runningBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + sha + "\",\"state\":\"running\",\"running\":true}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), runningBytes);
            var binding = store.ConfirmRunnerCheckpoint(operationId, runDirectory);
            if (binding != Convert.ToHexString(SHA256.HashData(runningBytes)) ||
                store.GetDispatchCheckpoint(operationId)?.CheckpointSha256 != binding)
                throw new Exception("neutral checkpoint exact byte binding failed");

            var foreignBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + new string('0', 64) +
                "\",\"state\":\"running\",\"running\":true}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), foreignBytes);
            try
            {
                store.ConfirmRunnerCheckpoint(operationId, runDirectory);
                throw new Exception("foreign neutral checkpoint was confirmed");
            }
            catch (InvalidOperationException e) when (e.Message == "dispatch_checkpoint_mismatch")
            {
            }
            if (store.GetDispatchCheckpoint(operationId)?.CheckpointSha256 != binding)
                throw new Exception("rejected checkpoint replaced the durable binding");

            var terminalBytes = Encoding.UTF8.GetBytes(
                "{\"snapshot\":\"" + sha + "\",\"state\":\"succeeded\",\"running\":false}");
            File.WriteAllBytes(Path.Combine(runDirectory, "run.json"), terminalBytes);
            var final = store.FinalizeExecution(operationId, runDirectory, 0);
            if (final.State != "succeeded" || final.ReportSha256 !=
                Convert.ToHexString(SHA256.HashData(terminalBytes)))
                throw new Exception("neutral finalization did not bind exact bytes");
            if (store.FinalizeExecution(operationId, runDirectory, 0) != final)
                throw new Exception("neutral finalization not idempotent");
        }

        using (var reopened = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            if (reopened.GetFinalization(operationId)?.State != "succeeded" ||
                reopened.GetDispatchCheckpoint(operationId) is null)
                throw new Exception("neutral finalization did not survive restart");
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
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var root = document.RootElement;
        return (root.GetProperty("snapshot").GetString()!, root.GetProperty("state").GetString()!,
            root.GetProperty("running").GetBoolean());
    }

    public OperationCheckpointEvidence ForDispatch(byte[] bytes)
    {
        var (hash, state, running) = Parse(bytes);
        if (state != "running" || !running)
            throw new InvalidDataException("Neutral fixture has no admitted dispatch.");
        return new(hash);
    }

    public OperationCheckpointEvidence ForResume(byte[] bytes)
    {
        var (hash, state, running) = Parse(bytes);
        if (state != "needs_attention" || running)
            throw new InvalidOperationException("resume_checkpoint_not_safe");
        return new(hash);
    }

    public OperationCheckpointEvidence ForFinalization(byte[] bytes, int exitCode)
    {
        var (hash, state, running) = Parse(bytes);
        if (state != "succeeded" || running || exitCode != 0)
            throw new InvalidOperationException("runner_exit_report_mismatch");
        return new(hash, state);
    }
}
