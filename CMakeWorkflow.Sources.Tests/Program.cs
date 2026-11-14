using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Sources;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: capture-git <store> <repo-root> [--include-untracked] | capture-files <store> <source> <path>... | inspect <store> <id> | restore <store> <id> <destination> | self-test");
    return 2;
}

try
{
    switch (args[0])
    {
        case "capture-git" when args.Length is 3 or 4:
            {
                bool includeUntracked = args.Length == 4 && args[3] == "--include-untracked";
                if (args.Length == 4 && !includeUntracked)
                {
                    throw new ArgumentException("Unknown capture-git flag.");
                }
                GitSourceCapture result = new SourceCaptureService(
                    new LoreSnapshotStore(args[1]), new GitSourceSelector())
                    .CaptureGit(args[2], includeUntracked);
                Console.WriteLine(JsonSerializer.Serialize(result));
                break;
            }
        case "capture-files" when args.Length >= 4:
            Console.WriteLine(new LoreSnapshotStore(args[1]).Capture(args[2], args[3..]));
            break;
        case "inspect" when args.Length == 3:
            Console.WriteLine(JsonSerializer.Serialize(new LoreSnapshotStore(args[1]).Inspect(args[2])));
            break;
        case "restore" when args.Length == 4:
            new LoreSnapshotStore(args[1]).Materialize(args[2], args[3]);
            Console.WriteLine("Materialized " + args[2]);
            break;
        case "self-test" when args.Length == 1:
            SelfTest();
            Console.WriteLine("Source P2/P3: Lore capture, persistence, restore, Git staged/unstaged/deleted/ignored/untracked, safety passed.");
            break;
        default:
            throw new ArgumentException("Invalid command or arguments.");
    }
    return 0;
}
#pragma warning disable CA1031 // CLI boundary must turn any failure into nonzero exit.
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
#pragma warning restore CA1031

static void SelfTest()
{
    string tmp = Path.Combine(Path.GetTempPath(), "cmwf-sources-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(tmp, "repo");
    string store = Path.Combine(tmp, "store");
    _ = Directory.CreateDirectory(source);
    try
    {
        RunGit(source, "init", "-q");
        File.WriteAllText(Path.Combine(source, ".gitignore"), "*.tmp\n");
        File.WriteAllText(Path.Combine(source, "main.cpp"), "int main(){return 1;}\n");
        File.WriteAllText(Path.Combine(source, "old.cpp"), "int old(){return 0;}\n");
        _ = Directory.CreateDirectory(Path.Combine(source, "space dir"));
        File.WriteAllText(Path.Combine(source, "space dir", "file name.cpp"), "// path with spaces\n");
        byte[] binary = [0, 1, 255, 23, 0, 200];
        File.WriteAllBytes(Path.Combine(source, "asset.bin"), binary);
        RunGit(source, "add", ".");
        RunGit(source, "-c", "user.name=cmwf-tests", "-c", "user.email=cmwf@invalid.local", "commit", "-qm", "initial");

        File.WriteAllText(Path.Combine(source, "main.cpp"), "int main(){return 2;}\n"); // unstaged
        _ = Directory.CreateDirectory(Path.Combine(source, "include"));
        File.WriteAllText(Path.Combine(source, "include", "lib.hpp"), "#pragma once\n");
        RunGit(source, "add", "include/lib.hpp"); // staged addition
        File.Delete(Path.Combine(source, "old.cpp")); // unstaged deletion
        File.WriteAllText(Path.Combine(source, "draft.txt"), "untracked\n");
        File.WriteAllText(Path.Combine(source, "secret.tmp"), "ignored\n");

        GitSourceSelector git = new();
        GitSourceSelection selected = git.Select(source);
        Check(selected.HeadCommit is { Length: 40 }, "HEAD should be present.");
        Check(selected.Files.Contains("main.cpp", StringComparer.Ordinal), "Modified tracked file missing.");
        Check(selected.Files.Contains("include/lib.hpp", StringComparer.Ordinal), "Staged addition missing.");
        Check(selected.Files.Contains("space dir/file name.cpp", StringComparer.Ordinal),
            "Git NUL-delimited path with spaces missing.");
        Check(!selected.Files.Contains("old.cpp", StringComparer.Ordinal), "Deleted file reappeared.");
        Check(!selected.Files.Contains("draft.txt", StringComparer.Ordinal), "Untracked files must require opt-in.");
        Check(!selected.Files.Contains("secret.tmp", StringComparer.Ordinal), "Ignored file leaked.");
        Check(selected.ExcludedUntrackedCount == 1 && selected.DeletedTrackedCount == 1,
            "Excluded/deleted counters incorrect.");
        GitSourceSelection withUntracked = git.Select(source, includeUntracked: true);
        Check(withUntracked.Files.Contains("draft.txt", StringComparer.Ordinal), "Untracked opt-in did not work.");
        Check(!withUntracked.Files.Contains("secret.tmp", StringComparer.Ordinal), "Ignored file leaked with opt-in.");

        LoreSnapshotStore lore = new(store);
        SourceCaptureService capture = new(lore, git);
        string first = capture.CaptureGit(source).SnapshotId;
        string same = capture.CaptureGit(source).SnapshotId;
        Check(first == same, "Content identity must be deterministic.");
        string selectedUntracked = capture.CaptureGit(source, includeUntracked: true).SnapshotId;
        Check(selectedUntracked != first, "Untracked opt-in should change identity.");

        File.WriteAllText(Path.Combine(source, "main.cpp"), "int main(){return 3;}\n");
        string second = new SourceCaptureService(new LoreSnapshotStore(store), git).CaptureGit(source).SnapshotId;
        Check(first != second, "Changed working tree must change snapshot ID.");
        LoreSnapshotStore reopened = new(store);
        string one = Path.Combine(tmp, "first");
        string two = Path.Combine(tmp, "second");
        reopened.Materialize(first, one);
        reopened.Materialize(second, two);
        Check(File.ReadAllText(Path.Combine(one, "main.cpp")) == "int main(){return 2;}\n",
            "Old snapshot must retain original working-tree content.");
        Check(File.ReadAllText(Path.Combine(two, "main.cpp")) == "int main(){return 3;}\n",
            "New snapshot must contain updated working-tree content.");
        Check(File.ReadAllBytes(Path.Combine(one, "asset.bin")).AsSpan().SequenceEqual(binary),
            "Binary payload not preserved.");
        Check(!File.Exists(Path.Combine(one, "old.cpp")) && !File.Exists(Path.Combine(one, "draft.txt")),
            "Deleted or excluded file was materialized.");
        Check(!Directory.Exists(Path.Combine(one, ".git")), "Materialization must not create Git metadata.");
        Check(File.Exists(Path.Combine(one, "space dir", "file name.cpp")),
            "Git NUL-delimited path with spaces was not materialized.");

        ExpectFailure(() => SourcePathPolicy.Validate("../outside"), "path traversal");
        ExpectFailure(() => SourcePathPolicy.Validate("CON.txt"), "Windows device name");
        ExpectFailure(() => SourcePathPolicy.Validate("A?.cpp"), "Windows wildcard");
        ExpectFailure(() => reopened.Materialize(first, one), "restore overwrite");
        ExpectFailure(() => reopened.Capture(source, ["main.cpp", "MAIN.CPP"]), "path collision");
        ExpectFailure(() => reopened.Inspect(new string('0', 64)), "missing snapshot");
        ExpectFailure(() => git.Select(Path.Combine(source, "include")), "subdirectory Git root");

        if (!OperatingSystem.IsWindows())
        {
            // Index mode 120000 is not a regular source file; never follow its target.
            string link = Path.Combine(source, "linked.cpp");
            _ = File.CreateSymbolicLink(link, "main.cpp");
            RunGit(source, "add", "linked.cpp");
            ExpectFailure(() => git.Select(source), "tracked symlink must fail closed");
            RunGit(source, "reset", "-q", "--", "linked.cpp");
            File.Delete(link);
        }

        string orphanStore = Path.Combine(tmp, "orphan");
        _ = Directory.CreateDirectory(Path.Combine(orphanStore, "manifests"));
        File.Copy(Path.Combine(store, "manifests", first + ".json"),
            Path.Combine(orphanStore, "manifests", first + ".json"));
        ExpectFailure(() => new LoreSnapshotStore(orphanStore).Capture(source, ["main.cpp"]),
            "missing Lore store must not be recreated");
        string manifest = Path.Combine(store, "manifests", first + ".json");
        File.AppendAllText(manifest, "\n", Encoding.UTF8);
        ExpectFailure(() => reopened.Inspect(first), "manifest corruption");
    }
    finally
    {
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}

static void RunGit(string root, params string[] arguments)
{
    ProcessStartInfo start = new("git")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using Process process = Process.Start(start)
        ?? throw new IOException("Failed to start git.");
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    string error = stderr.GetAwaiter().GetResult();
    _ = stdout.GetAwaiter().GetResult();
    if (process.ExitCode != 0)
    {
        throw new IOException($"git {arguments[0]} failed: {error}");
    }
}

static void Check(bool ok, string message)
{
    if (!ok)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectFailure(Action action, string name)
{
    try
    {
        action();
    }
    catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected failure: {name}");
}
