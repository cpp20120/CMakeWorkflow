using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.History;

internal static class HistoryRunsPageTests
{
    private static void Check(bool ok, string detail)
    {
        if (!ok) throw new Exception("History page: " + detail);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("History page: foreign cursor or invalid query was accepted");
    }

    internal static void Run(string parent)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(parent, "history-page")).FullName;
        var database = Path.Combine(workspace, "history.sqlite");
        var started = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        static void Save(string dir, DateTimeOffset startedAt)
        {
            Directory.CreateDirectory(dir);
            var report = new WorkflowReport
            {
                Workflow = "history-page-fixture", Status = "succeeded", StartedAt = startedAt,
                FinishedAt = startedAt.AddMinutes(1)
            };
            File.WriteAllBytes(Path.Combine(dir, "run.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
        }

        using (var writer = new HistoryStore(database))
        {
            for (var i = 0; i < 4; ++i)
            {
                var dir = Path.Combine(workspace, "run-" + i);
                Save(dir, started); // Equal timestamps exercise the stable ID tie-breaker.
                writer.Import(dir, false);
            }
        }
        List<string> expected;
        string continuation;
        using (var reader = new HistoryStore(database, readOnly: true))
        {
            expected = reader.Runs(10, null, null, null, null, null, _ => true).Select(x => x.Id).ToList();
            Check(expected.Count == 4, "expected four distinct imported runs");
            var first = reader.RunsPage(database, 2, null, null, null, null, null, null, _ => true);
            Check(first.SchemaVersion == 1 && first.Items.Select(x => x.Id).SequenceEqual(expected.Take(2)) &&
                  first.NextCursor is not null, "first page ordering or next cursor");
            continuation = first.NextCursor!;
            Reject(() => reader.RunsPage(database, 2, "failed", null, null, null, null, continuation));
            Reject(() => reader.RunsPage(database + ".other", 2, null, null, null, null, null, continuation));
            Reject(() => reader.RunsPage(database, 2, null, null, null, null, null, "bad cursor"));
            Reject(() => reader.RunsPage(database, 0, null, null, null, null, null, null));
        }

        // A newer import, even with a later timestamp, cannot enter an in-flight traversal.
        using (var writer = new HistoryStore(database))
        {
            var dir = Path.Combine(workspace, "latest");
            Save(dir, started.AddYears(1));
            writer.Import(dir, false);
        }
        var before = SHA256.HashData(File.ReadAllBytes(database));
        using (var reader = new HistoryStore(database, readOnly: true))
        {
            var last = reader.RunsPage(database, 2, null, null, null, null, null, continuation, _ => true);
            Check(last.Items.Select(x => x.Id).SequenceEqual(expected.Skip(2)) && last.NextCursor is null,
                "second page moved after concurrent import");
            var fresh = reader.RunsPage(database, 1, null, null, null, null, null, null, _ => true);
            Check(fresh.Items.Count == 1 && !expected.Contains(fresh.Items[0].Id),
                "new page did not find the latest import");
            var authorized = reader.RunsPage(database, 2, null, null, null, null, null, null,
                d => d.EndsWith("run-0", StringComparison.Ordinal));
            Check(authorized.Items.Count == 1 && authorized.NextCursor is null,
                "workspace path filtering must precede page limit");
        }
        Check(SHA256.HashData(File.ReadAllBytes(database)).SequenceEqual(before),
            "read-only paging mutated history database");
        Console.WriteLine("History page checks passed: keyset, tie-break, concurrent import, cursor binding and read-only isolation.");
    }
}
