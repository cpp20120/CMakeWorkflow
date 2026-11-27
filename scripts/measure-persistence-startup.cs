using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

if (args.Length is < 1 or > 2)
{
    throw new ArgumentException("Supply the Linux NativeAOT CLI path and, optionally, the configured CMake build tree.");
}

string executable = Path.GetFullPath(args[0]);
List<double> cli = [], host = [], idleCpu = [];
List<long> idleRss = [];
List<int> idleThreads = [], idleFileDescriptors = [];
for (int i = 0; i < 9; i++)
{
    bool sample = i >= 2;
    long start = Stopwatch.GetTimestamp();
    using (Process command = Start(["tooling", "catalog"], drainError: true))
    {
        await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (command.ExitCode != 0)
        {
            throw new InvalidOperationException($"CLI catalog exited {command.ExitCode}.");
        }

        if (sample)
        {
            cli.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    DirectoryInfo socketDirectory = Directory.CreateTempSubdirectory("cmwf-startup-");
    string endpoint = Path.Combine(socketDirectory.FullName, "host.sock");
    start = Stopwatch.GetTimestamp();
    using Process server = Start(["host", "serve", "--endpoint", endpoint], drainError: false);
    try
    {
        string? ready = await server.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (ready is null || !ready.StartsWith("[Host] Listening: ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Host failed to create endpoint. " + ready);
        }

        if (sample)
        {
            host.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        await Task.Delay(50);
        server.Refresh();
        double before = server.TotalProcessorTime.TotalMilliseconds;
        await Task.Delay(250);
        server.Refresh();
        if (sample)
        {
            idleRss.Add(server.WorkingSet64);
            idleCpu.Add(server.TotalProcessorTime.TotalMilliseconds - before);
            idleThreads.Add(server.Threads.Count);
            idleFileDescriptors.Add(Directory.EnumerateFileSystemEntries($"/proc/{server.Id}/fd").Count());
        }
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill();
        }

        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        socketDirectory.Delete(recursive: true);
    }
}

Console.WriteLine(FormattableString.Invariant(
    $"cli_catalog_ms={Median(cli):F3} host_endpoint_ms={Median(host):F3} host_idle_rss_bytes={Median(idleRss)} idle_250ms_cpu_ms={Median(idleCpu):F3}"));
long distribution = Directory.EnumerateFiles(Path.GetDirectoryName(executable)!, "*", SearchOption.AllDirectories)
    .Sum(file => new FileInfo(file).Length);
Console.WriteLine($"distribution_bytes={distribution} executable_bytes={new FileInfo(executable).Length} host_threads={Median(idleThreads)} host_fds={Median(idleFileDescriptors)}");
if (args.Length == 2)
{
    await MeasureMatrixAsync(Path.GetFullPath(args[1]));
}

async Task MeasureMatrixAsync(string buildTree)
{
    DirectoryInfo directory = Directory.CreateTempSubdirectory("cmwf-plan-benchmark-");
    try
    {
        foreach (int nodes in new[] { 4, 65, 256, 512 })
        {
            string manifest = Path.Combine(directory.FullName, "matrix.json");
            WriteMatrix(manifest, buildTree, nodes);
            List<double> latency = [];
            for (int i = 0; i < 9; i++)
            {
                long start = Stopwatch.GetTimestamp();
                using Process command = Start(["workflow", "plan", manifest, "--json"], drainError: true);
                await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (command.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Matrix planning exited {command.ExitCode}.");
                }

                if (i >= 2)
                {
                    latency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }

            Console.WriteLine(FormattableString.Invariant(
                $"nodes={nodes} plan_ms={Median(latency):F3} input_bytes={new FileInfo(manifest).Length}"));
        }
    }
    finally
    {
        directory.Delete(recursive: true);
    }
}

static void WriteMatrix(string manifest, string buildTree, int count)
{
    using FileStream output = File.Create(manifest);
    using Utf8JsonWriter json = new(output);
    json.WriteStartObject();
    json.WriteNumber("schemaVersion", 2);
    json.WriteStartObject("contexts");
    json.WriteStartObject("native");
    json.WriteString("buildDirectory", buildTree);
    json.WriteString("mode", "observe");
    json.WriteString("provenancePolicy", "allowUnverified");
    json.WriteEndObject();
    json.WriteEndObject();
    json.WriteStartArray("nodes");
    for (int i = 0; i < count; i++)
    {
        json.WriteStartObject();
        json.WriteString("id", NodeName(i));
        json.WriteString("kind", "inspect");
        json.WriteString("context", "native");
        if (count == 256)
        {
            json.WriteStartArray("dependsOn");
            for (int j = Math.Max(0, i - 16); j < i; j++)
            {
                json.WriteStringValue(NodeName(j));
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    json.WriteEndArray();
    json.WriteEndObject();
    json.Flush();
}

static string NodeName(int ordinal) => "node-" + ordinal.ToString("D3", CultureInfo.InvariantCulture);

Process Start(string[] arguments, bool drainError)
{
    ProcessStartInfo info = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in arguments)
    {
        info.ArgumentList.Add(arg);
    }

    Process process = Process.Start(info) ?? throw new InvalidOperationException("Process start failed.");
    process.BeginOutputReadLine();
    if (drainError)
    {
        process.BeginErrorReadLine();
    }

    return process;
}

static T Median<T>(List<T> list) => list.Order().ElementAt(list.Count / 2);
