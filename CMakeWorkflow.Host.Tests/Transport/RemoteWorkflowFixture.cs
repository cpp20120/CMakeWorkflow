using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Client;

// Creates files on the simulated remote machine, never in SDK submission code.
// A release file makes disconnect/cancellation checks independent of build speed.
internal sealed record RemoteWorkflowFixture(string Workspace, string Marker, string Release,
    StartWorkflowOptions Request)
{
    internal string[] Effects() => File.Exists(Marker)
        ? File.ReadAllLines(Marker)
        : [];

    internal async Task WaitForStartAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!Effects().Contains("started")) await Task.Delay(25, deadline.Token);
        SshTransportTests.Check(!Effects().Contains("finished"), "Effect finished before the release gate.");
    }

    internal void Finish() => File.WriteAllText(Release, "release");

    internal static async Task<RemoteWorkflowFixture> CreateAsync(string root, string name, CancellationToken ct)
    {
        var workspace = Path.Combine(root, "remote workspace " + name);
        var source = Path.Combine(workspace, "source");
        var build = Path.Combine(workspace, "build");
        Directory.CreateDirectory(source);
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(remote_workflow NONE)
            add_custom_target(effect COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake")
            """);
        File.WriteAllText(Path.Combine(source, "effect.cmake"), """
            file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "started\n")
            set(attempt 0)
            while(NOT EXISTS "${CMAKE_CURRENT_LIST_DIR}/release")
              if(attempt GREATER_EQUAL 400)
                message(FATAL_ERROR "Test release gate timed out")
              endif()
              math(EXPR attempt "${attempt} + 1")
              execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 0.05)
            endwhile()
            file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "finished\n")
            """);
        await ConfigureAsync(source, build, ct);
        var manifest = JsonNode.Parse("""
            {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
             "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
            """)!;
        manifest["contexts"]!["w"]!["buildDirectory"] = build;
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var path = Path.Combine(workspace, "workflow.json");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new(workspace, Path.Combine(source, "effects.txt"), Path.Combine(source, "release"),
            new("remote-" + name, path, Convert.ToHexString(SHA256.HashData(bytes)), 1,
                Path.Combine(workspace, "results")));
    }

    private static async Task ConfigureAsync(string source, string build, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo("cmake")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Cannot configure remote fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new Exception("Remote fixture configuration failed: " + await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}
