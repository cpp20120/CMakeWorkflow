using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class SdkSmokeTests
{
    private static void Check(bool success, string diagnostic)
    {
        if (!success)
        {
            throw new InvalidOperationException("SDK: " + diagnostic);
        }
    }

    public static async Task RunAsync(string endpoint, string workspace, CancellationToken cancellationToken)
    {
        // Connect must not acquire ownership of an existing Host process.
        int hostPid;
        await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, workspace), cancellationToken))
        {
            hostPid = client.Host.ProcessId;
            Check(client.Host.ProtocolVersion == 1, "protocol version");
            Check(client.Host.WorkspaceDirectory == workspace, "workspace binding");
            Check(client.Host.Capabilities.Contains("workflow/start"), "workflow capability");
            WorkflowOperation absent = client.Operations.GetHandle(Guid.NewGuid().ToString("N"));
            Check(absent.OperationId.Length == 32, "lightweight operation reference");
        }

        await using (CMakeWorkflowClient other = await CMakeWorkflowClient.ConnectAsync(
                         new ClientOptions(endpoint, workspace), cancellationToken))
        {
            Check(other.Host.ProcessId == hostPid, "ConnectAsync disposed a foreign Host");
        }

        // Locate the CLI apphost built by the solution. Do not assume test working directory.
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
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

        Check(executable is not null && File.Exists(executable),
            "CLI apphost is missing; build the solution in Release");
        int ownedPid;
        await using (CMakeWorkflowClient launched = await CMakeWorkflowClient.LaunchAsync(
                         new LaunchOptions(executable!, workspace), cancellationToken))
        {
            ownedPid = launched.Host.ProcessId;
            Check(ownedPid != hostPid, "LaunchAsync attached instead of spawning a private Host");
            Check(launched.Host.WorkspaceDirectory == workspace, "owned Host workspace");
        }

        try
        {
            using Process process = Process.GetProcessById(ownedPid);
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
        string workspace = Path.Combine(parent, "sdk lifecycle " + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "src");
        string build = Path.Combine(workspace, "build");
        string results = Path.Combine(workspace, "results");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(Path.Combine(build, ".cmake", "api", "v1", "query"));
        await File.WriteAllTextAsync(Path.Combine(build, ".cmake", "api", "v1", "query", "codemodel-v2"), "", CancellationToken.None);
        string marker = Path.Combine(source, "effects.txt");
        await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"start\\n\")\n" +
            "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 1)\n" +
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finish\\n\")\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(sdk_smoke NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n", CancellationToken.None);
        ProcessStartInfo start = new("cmake")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string? arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
        {
            start.ArgumentList.Add(arg);
        }

        using (Process process = Process.Start(start) ?? throw new IOException("Cannot configure SDK fixture"))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                throw new UnreachableException("SDK fixture configure failed: " + await stdout + await stderr);
            }

            _ = await Task.WhenAll(stdout, stderr);
        }

        JsonNode document = JsonNode.Parse("""
                                      {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                       "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                      """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        string manifest = Path.Combine(workspace, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        StartWorkflowOptions options = new("sdk-" + Guid.NewGuid().ToString("N"), manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, results);
        await using CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        WorkflowOperation operation = await client.Workflows.StartAsync(options, ct);
        WorkflowOperation retry = await client.Workflows.StartAsync(options, ct);
        Check(operation.OperationId == retry.OperationId, "SDK retry changed operation identity");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        OperationSnapshot terminal = await operation.WaitForCompletionAsync(ct: timeout.Token);
        Check(terminal.State == "succeeded", "SDK WaitForCompletion did not observe succeeded");
        OperationSnapshot snapshot = await operation.GetSnapshotAsync(timeout.Token);
        Check(snapshot.OperationId == operation.OperationId && snapshot.State == "succeeded", "SDK snapshot mismatch");
        EventPage page = await operation.ReadEventsAsync(0, 100, timeout.Token);
        Check(page.Events.Count > 0 && page.NextSequence > 0, "SDK event page empty");
        EventPage next = await operation.ReadEventsAsync(page.NextSequence, 100, timeout.Token);
        Check(next.Events.Count == 0, "SDK event cursor repeated events");
        Check((await File.ReadAllLinesAsync(marker, ct)).SequenceEqual(["start", "finish"]), "SDK duplicated external effect");
        Console.WriteLine("SDK lifecycle checks passed: start, retry, wait, snapshot, event cursor and single effect.");
    }
}