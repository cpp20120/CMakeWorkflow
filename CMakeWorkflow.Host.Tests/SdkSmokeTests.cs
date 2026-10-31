using CMakeWorkflow.Client;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class SdkSmokeTests
{
    private static void Check(bool success, string diagnostic)
    {
        if (!success) throw new InvalidOperationException("SDK: " + diagnostic);
    }

    public static async Task RunAsync(string endpoint, string workspace, CancellationToken cancellationToken)
    {
        // Connect must not acquire ownership of an existing Host process.
        int hostPid;
        await using (var client = await CMakeWorkflowClient.ConnectAsync(
            new ClientOptions(endpoint, workspace), cancellationToken))
        {
            hostPid = client.Host.ProcessId;
            Check(client.Host.ProtocolVersion == 1, "protocol version");
            Check(client.Host.WorkspaceDirectory == workspace, "workspace binding");
            Check(client.Host.Capabilities.Contains("workflow/start"), "workflow capability");
            var absent = client.Operations.GetHandle(Guid.NewGuid().ToString("N"));
            Check(absent.OperationId.Length == 32, "lightweight operation reference");
        }
        await using (var other = await CMakeWorkflowClient.ConnectAsync(
            new ClientOptions(endpoint, workspace), cancellationToken))
        {
            Check(other.Host.ProcessId == hostPid, "ConnectAsync disposed a foreign Host");
        }

        // Locate the CLI apphost built by the solution. Do not assume test working directory.
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? executable = null;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CMakeWorkflow.slnx")))
            {
                executable = Path.Combine(current.FullName, "bin", "Release", "net10.0",
                    OperatingSystem.IsWindows() ? "CMakeWorkflow.exe" : "CMakeWorkflow");
                break;
            }
            current = current.Parent;
        }
        Check(executable is not null && File.Exists(executable), "CLI apphost is missing; build the solution in Release");
        int ownedPid;
        await using (var launched = await CMakeWorkflowClient.LaunchAsync(
            new LaunchOptions(executable!, workspace), cancellationToken))
        {
            ownedPid = launched.Host.ProcessId;
            Check(ownedPid != hostPid, "LaunchAsync attached instead of spawning a private Host");
            Check(launched.Host.WorkspaceDirectory == workspace, "owned Host workspace");
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(ownedPid);
            Check(process.HasExited, "LaunchAsync leaked its owned Host");
        }
        catch (ArgumentException)
        {
            // The OS has already reaped this process.
        }
        await VerifyLifecycleAsync(endpoint, workspace, cancellationToken);
        Console.WriteLine("SDK transport checks passed: connect, foreign ownership, launch and owned shutdown.");
    }

    private static async Task VerifyLifecycleAsync(string endpoint, string parent, CancellationToken ct)
    {
        var workspace = Path.Combine(parent, "sdk lifecycle " + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(workspace, "src");
        var build = Path.Combine(workspace, "build");
        var results = Path.Combine(workspace, "results");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(build, ".cmake", "api", "v1", "query"));
        File.WriteAllText(Path.Combine(build, ".cmake", "api", "v1", "query", "codemodel-v2"), "");
        var marker = Path.Combine(source, "effects.txt");
        File.WriteAllText(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"start\\n\")\n" +
            "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 1)\n" +
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finish\\n\")\n");
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(sdk_smoke NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n");
        var start = new ProcessStartInfo("cmake")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            start.ArgumentList.Add(arg);
        using (var process = Process.Start(start) ?? throw new IOException("Cannot configure SDK fixture"))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new Exception("SDK fixture configure failed: " + await stdout + await stderr);
            await Task.WhenAll(stdout, stderr);
        }
        var document = JsonNode.Parse("""
            {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
             "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
            """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        var manifest = Path.Combine(workspace, "workflow.json");
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        var options = new StartWorkflowOptions("sdk-" + Guid.NewGuid().ToString("N"), manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, results);
        await using var client = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        var operation = await client.Workflows.StartAsync(options, ct);
        var retry = await client.Workflows.StartAsync(options, ct);
        Check(operation.OperationId == retry.OperationId, "SDK retry changed operation identity");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var terminal = await operation.WaitForCompletionAsync(ct: timeout.Token);
        Check(terminal.State == "succeeded", "SDK WaitForCompletion did not observe succeeded");
        var snapshot = await operation.GetSnapshotAsync(timeout.Token);
        Check(snapshot.OperationId == operation.OperationId && snapshot.State == "succeeded", "SDK snapshot mismatch");
        var page = await operation.ReadEventsAsync(0, 100, timeout.Token);
        Check(page.Events.Count > 0 && page.NextSequence > 0, "SDK event page empty");
        var next = await operation.ReadEventsAsync(page.NextSequence, 100, timeout.Token);
        Check(next.Events.Count == 0, "SDK event cursor repeated events");
        Check(File.ReadAllLines(marker).SequenceEqual(["start", "finish"]), "SDK duplicated external effect");
        Console.WriteLine("SDK lifecycle checks passed: start, retry, wait, snapshot, event cursor and single effect.");
    }
}
