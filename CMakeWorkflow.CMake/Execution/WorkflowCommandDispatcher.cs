using System.Text.Json.Nodes;
using System.Text;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

/// <summary>
/// Journal-backed adapter for an ordered series of external commands. This is not
/// a second DAG planner: the caller owns its domain-specific step order; the
/// regular WorkflowProcessDispatcher owns all child-process execution/cleanup.
/// </summary>
public sealed class WorkflowCommandDispatcher
{
    private readonly string output;
    private readonly JsonArray commands;
    private readonly bool resume;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly Action<WorkflowDispatchEvent>? observer;
    private readonly string commandsPath;

    public WorkflowCommandDispatcher(string output, JsonArray commands, bool resume,
        IReadOnlyDictionary<string, string> environment,
        Action<WorkflowDispatchEvent>? observer = null)
    {
        this.output = Path.GetFullPath(output);
        this.commands = commands;
        this.resume = resume;
        this.environment = environment;
        this.observer = observer;
        commandsPath = Path.Combine(this.output, "commands.json");
    }

    public async Task<string> RunAsync(string label, string executable, IEnumerable<string> arguments,
        string? declaredOutput, int timeoutSeconds, CancellationToken ct)
    {
        if (timeoutSeconds is <= 0 or > (86400 * 30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        if (string.IsNullOrWhiteSpace(label) || label.IndexOfAny(['/', '\\', '\0']) >= 0 ||
            label is "." or "..")
        {
            throw new ArgumentException("Invalid command label.", nameof(label));
        }

        string[] argv = [.. arguments];
        string stdoutPath = Path.Combine(output, "logs", label + ".stdout");
        string stderrPath = Path.Combine(output, "logs", label + ".stderr");

        if (resume)
        {
            JsonObject? previous = commands.Reverse().OfType<JsonObject>()
                .FirstOrDefault(x => x["label"]?.GetValue<string>() == label);
            if (previous is not null)
            {
                if (previous["returncode"] is null || previous["timed_out"]?.GetValue<bool>() == true)
                {
                    throw new InvalidDataException(
                        $"Cannot resume '{label}': prior execution outcome is unknown or timed out. Use a fresh output directory.");
                }

                if (previous["returncode"]!.GetValue<int>() != 0)
                {
                    throw new InvalidDataException(
                        $"Cannot resume '{label}': previous invocation failed. Use a fresh output directory.");
                }

                if (previous["argv"]?.AsArray().Select(x => x!.GetValue<string>())
                        .SequenceEqual(new[] { executable }.Concat(argv)) != true
                    || !File.Exists(stdoutPath) || !File.Exists(stderrPath))
                {
                    throw new InvalidDataException(
                        $"Cannot resume '{label}': command identity or captured logs changed.");
                }

                foreach ((string? path, string? key) in new[]
                         {
                             (stdoutPath, "stdout_sha256"),
                             (stderrPath, "stderr_sha256")
                         })
                {
                    if (previous[key] is null || await CommandExecution.Sha256Async(path, ct)
                        != previous[key]!.GetValue<string>())
                    {
                        throw new InvalidDataException($"Cannot resume '{label}': captured log changed: {path}");
                    }
                }

                if (declaredOutput is not null)
                {
                    if (!File.Exists(declaredOutput) || previous["external_output_sha256"] is null
                                                     || await CommandExecution.Sha256Async(declaredOutput, ct)
                                                     != previous["external_output_sha256"]!.GetValue<string>())
                    {
                        throw new InvalidDataException(
                            $"Cannot resume '{label}': generated output changed: {declaredOutput}");
                    }
                }

                Notify(new WorkflowDispatchEvent("verified", label, TimeSpan.Zero));
                return await File.ReadAllTextAsync(stdoutPath, ct);
            }
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        JsonObject entry = new()
        {
            ["label"] = label,
            ["started_utc"] = start.ToString("O"),
            ["argv"] = new JsonArray([.. new[] { executable }.Concat(argv).Select(x => (JsonNode?)JsonValue.Create(x))])
        };
        commands.Add((JsonNode?)entry);
        // Dispatch must not precede durable admission. On a crash this entry is
        // intentionally outcome-unknown, never silently replayable.
        await PersistAsync();
        Notify(new WorkflowDispatchEvent("started", label, TimeSpan.Zero));
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            NodeResult result = new()
            {
                Id = label,
                Kind = "external",
                Executable = executable,
                Arguments = [.. argv],
                WorkingDirectory = output,
                Stdout = Path.Combine("logs", label + ".stdout"),
                Stderr = Path.Combine("logs", label + ".stderr")
            };
            int exitCode = await WorkflowProcessDispatcher.RunAsync(result, environment, output,
                deadline.Token, elapsed => Notify(new WorkflowDispatchEvent("heartbeat", label, elapsed)));
            // The process executor persists the output streams. Do not read them
            // before the child has exited and the streams are closed.
            string stdout = await File.ReadAllTextAsync(stdoutPath, ct);
            string stderr = await File.ReadAllTextAsync(stderrPath, ct);
            entry["stdout_sha256"] = await CommandExecution.Sha256Async(stdoutPath, ct);
            entry["stderr_sha256"] = await CommandExecution.Sha256Async(stderrPath, ct);
            if (exitCode == 0 && declaredOutput is not null)
            {
                if (!File.Exists(declaredOutput))
                {
                    throw new InvalidDataException($"{label}: successful command did not produce {declaredOutput}.");
                }

                entry["external_output_sha256"] = await CommandExecution.Sha256Async(declaredOutput, ct);
            }

            entry["returncode"] = exitCode;
            entry["timed_out"] = false;
            return exitCode != 0
                ? throw new IOException($"{label} failed ({exitCode}): "
                                      + (stdout + stderr)[^Math.Min(3000, stdout.Length + stderr.Length)..])
                : stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // Timeout is a terminal failure, but its external effects remain
            // uncertain. Never permit an automatic resume of this operation.
            entry["timed_out"] = true;
            throw new IOException($"{label} timed out after {timeoutSeconds} seconds.");
        }
        finally
        {
            entry["elapsed_seconds"] = (DateTimeOffset.UtcNow - start).TotalSeconds;
            await PersistAsync();
            Notify(new WorkflowDispatchEvent(
                entry["returncode"]?.GetValue<int>() == 0 && entry["timed_out"]?.GetValue<bool>() != true
                    ? "succeeded"
                    : "stopped", label, DateTimeOffset.UtcNow - start));
        }
    }

    private void Notify(WorkflowDispatchEvent item)
    {
        // Progress is observational; a closed terminal must not invalidate an
        // otherwise successful external operation or change its durable outcome.
        try
        {
            observer?.Invoke(item);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Task PersistAsync() => WorkflowCheckpoint.WriteDurableBytesAsync(
        commandsPath, Encoding.UTF8.GetBytes(commands.ToJsonString() + "\n"), CancellationToken.None);
}
