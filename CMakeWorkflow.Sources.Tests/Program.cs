using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Sources;

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("Usage: store-path <canonical-workspace> | capture-git <store> <repo-root> [--include-untracked] | capture-files <store> <source> <path>... | inspect <store> <id> | restore <store> <id> <destination> | self-test");
    return 2;
}

try
{
    switch (args[0])
    {
        case "store-path" when args.Length == 2:
            Console.WriteLine(SourceStorePaths.ForWorkspace(args[1]));
            break;
        case "capture-git" when args.Length is 3 or 4:
            {
                bool includeUntracked = args.Length == 4 && args[3] == "--include-untracked";
                if (args.Length == 4 && !includeUntracked)
                {
                    throw new ArgumentException("Unknown capture-git flag.");
                }
                GitSourceCapture result = await new SourceCaptureService(
                    new LoreSnapshotStore(args[1]), new GitSourceSelector())
                    .CaptureGitAsync(args[2], includeUntracked);
                Console.WriteLine(JsonSerializer.Serialize(result));
                break;
            }
        case "capture-files" when args.Length >= 4:
            Console.WriteLine(await new LoreSnapshotStore(args[1]).CaptureAsync(args[2], args[3..]));
            break;
        case "inspect" when args.Length == 3:
            Console.WriteLine(JsonSerializer.Serialize(await new LoreSnapshotStore(args[1]).InspectAsync(args[2])));
            break;
        case "restore" when args.Length == 4:
            await new LoreSnapshotStore(args[1]).MaterializeAsync(args[2], args[3]);
            Console.WriteLine("Materialized " + args[2]);
            break;
        case "self-test" when args.Length == 1:
            await SelfTestAsync();
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
    await Console.Error.WriteLineAsync(ex.ToString());
    return 1;
}
#pragma warning restore CA1031

static async Task SelfTestAsync()
{
    string tmp = Path.Combine(Path.GetTempPath(), "cmwf-sources-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(tmp, "repo");
    string store = Path.Combine(tmp, "store");
    _ = Directory.CreateDirectory(source);
    try
    {
        RunGit(source, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "*.tmp\n");
        await File.WriteAllTextAsync(Path.Combine(source, "main.cpp"), "int main(){return 1;}\n");
        await File.WriteAllTextAsync(Path.Combine(source, "old.cpp"), "int old(){return 0;}\n");
        _ = Directory.CreateDirectory(Path.Combine(source, "space dir"));
        await File.WriteAllTextAsync(Path.Combine(source, "space dir", "file name.cpp"), "// path with spaces\n");
        byte[] binary = [0, 1, 255, 23, 0, 200];
        await File.WriteAllBytesAsync(Path.Combine(source, "asset.bin"), binary);
        RunGit(source, "add", ".");
        RunGit(source, "-c", "user.name=cmwf-tests", "-c", "user.email=cmwf@invalid.local", "commit", "-qm", "initial");

        await File.WriteAllTextAsync(Path.Combine(source, "main.cpp"), "int main(){return 2;}\n"); // unstaged
        _ = Directory.CreateDirectory(Path.Combine(source, "include"));
        await File.WriteAllTextAsync(Path.Combine(source, "include", "lib.hpp"), "#pragma once\n");
        RunGit(source, "add", "include/lib.hpp"); // staged addition
        File.Delete(Path.Combine(source, "old.cpp")); // unstaged deletion
        await File.WriteAllTextAsync(Path.Combine(source, "draft.txt"), "untracked\n");
        await File.WriteAllTextAsync(Path.Combine(source, "secret.tmp"), "ignored\n");

        GitSourceSelector git = new();
        using (CancellationTokenSource canceled = new())
        {
            await canceled.CancelAsync();
            try
            {
                _ = await git.SelectAsync(source, ct: canceled.Token);
                throw new UnreachableException("Canceled Git selection succeeded.");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { }
        }
        GitSourceSelection selected = await git.SelectAsync(source);
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
        GitSourceSelection withUntracked = await git.SelectAsync(source, includeUntracked: true);
        Check(withUntracked.Files.Contains("draft.txt", StringComparer.Ordinal), "Untracked opt-in did not work.");
        Check(!withUntracked.Files.Contains("secret.tmp", StringComparer.Ordinal), "Ignored file leaked with opt-in.");

        LoreSnapshotStore lore = new(store);
        SourceCaptureService capture = new(lore, git);
        string first = (await capture.CaptureGitAsync(source)).SnapshotId;
        string same = (await capture.CaptureGitAsync(source)).SnapshotId;
        Check(first == same, "Content identity must be deterministic.");
        string selectedUntracked = (await capture.CaptureGitAsync(source, includeUntracked: true)).SnapshotId;
        Check(selectedUntracked != first, "Untracked opt-in should change identity.");

        await File.WriteAllTextAsync(Path.Combine(source, "main.cpp"), "int main(){return 3;}\n");
        string second = (await new SourceCaptureService(new LoreSnapshotStore(store), git).CaptureGitAsync(source)).SnapshotId;
        Check(first != second, "Changed working tree must change snapshot ID.");
        LoreSnapshotStore reopened = new(store);
        string one = Path.Combine(tmp, "first");
        string two = Path.Combine(tmp, "second");
        using (CancellationTokenSource canceled = new())
        {
            await canceled.CancelAsync();
            try
            {
                await reopened.MaterializeAsync(first, one, canceled.Token);
                throw new UnreachableException("Canceled materialization succeeded.");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { }
        }
        Check(!Directory.Exists(one), "Canceled materialization published a source tree.");
        await reopened.MaterializeAsync(first, one);
        await reopened.MaterializeAsync(second, two);
        Check(await File.ReadAllTextAsync(Path.Combine(one, "main.cpp")) == "int main(){return 2;}\n",
            "Old snapshot must retain original working-tree content.");
        Check(await File.ReadAllTextAsync(Path.Combine(two, "main.cpp")) == "int main(){return 3;}\n",
            "New snapshot must contain updated working-tree content.");
        Check((await File.ReadAllBytesAsync(Path.Combine(one, "asset.bin"))).AsSpan().SequenceEqual(binary),
            "Binary payload not preserved.");
        Check(!File.Exists(Path.Combine(one, "old.cpp")) && !File.Exists(Path.Combine(one, "draft.txt")),
            "Deleted or excluded file was materialized.");
        Check(!Directory.Exists(Path.Combine(one, ".git")), "Materialization must not create Git metadata.");
        Check(File.Exists(Path.Combine(one, "space dir", "file name.cpp")),
            "Git NUL-delimited path with spaces was not materialized.");

        ExpectFailure(() => SourcePathPolicy.Validate("../outside"), "path traversal");
        ExpectFailure(() => SourcePathPolicy.Validate("CON.txt"), "Windows device name");
        ExpectFailure(() => SourcePathPolicy.Validate("A?.cpp"), "Windows wildcard");
        await ExpectFailureAsync(() => reopened.MaterializeAsync(first, one), "restore overwrite");
        await ExpectFailureAsync(() => reopened.CaptureAsync(source, ["main.cpp", "MAIN.CPP"]), "path collision");
        await ExpectFailureAsync(() => reopened.InspectAsync(new string('0', 64)), "missing snapshot");
        await ExpectFailureAsync(() => git.SelectAsync(Path.Combine(source, "include")), "subdirectory Git root");

        if (!OperatingSystem.IsWindows())
        {
            // Index mode 120000 is not a regular source file; never follow its target.
            string link = Path.Combine(source, "linked.cpp");
            _ = File.CreateSymbolicLink(link, "main.cpp");
            RunGit(source, "add", "linked.cpp");
            await ExpectFailureAsync(() => git.SelectAsync(source), "tracked symlink must fail closed");
            RunGit(source, "reset", "-q", "--", "linked.cpp");
            File.Delete(link);
        }

        string orphanStore = Path.Combine(tmp, "orphan");
        _ = Directory.CreateDirectory(Path.Combine(orphanStore, "manifests"));
        File.Copy(Path.Combine(store, "manifests", first + ".json"),
            Path.Combine(orphanStore, "manifests", first + ".json"));
        await ExpectFailureAsync(() => new LoreSnapshotStore(orphanStore).CaptureAsync(source, ["main.cpp"]),
            "missing Lore store must not be recreated");
        string manifest = Path.Combine(store, "manifests", first + ".json");
        await File.AppendAllTextAsync(manifest, "\n", Encoding.UTF8);
        await ExpectFailureAsync(() => reopened.InspectAsync(first), "manifest corruption");
    }
    finally
    {
        if (Directory.Exists(tmp))
        {
            // Git marks loose objects read-only on some Windows installations.
            // Clear only that attribute before removing our disposable test repo.
            if (OperatingSystem.IsWindows())
            {
                foreach (string file in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
            }

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

static async Task ExpectFailureAsync(Func<Task> action, string name)
{
    try
    {
        await action();
        throw new UnreachableException("Expected refusal: " + name);
    }
    catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { }
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
