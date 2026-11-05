using System.Diagnostics;
using System.Security.Cryptography;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Execution;

// Executes a CMake workflow node and produces domain evidence. This component
// does not own DAG admission, WorkflowReport, durable checkpoints or dispatch gates.
internal static class CMakeWorkflowNodeExecutor
{
    internal static async Task<NodeResult> ExecuteAsync(PlannedNode node, NodeResult? prerequisite,
        bool snapshotExecutable, BuildContextSession session, string output, string logStem, IWorkflowOperationExecutor? extension, WorkflowToolchainRuntime? toolchain,
        string manifest, IReadOnlyDictionary<string, NodeResult> artifactSources, CancellationToken ct)
    {
        WorkflowNode definition = node.Definition;
        NodeResult result = new()
        {
            Id = definition.Id,
            Kind = definition.Kind,
            Dependencies = [.. node.Dependencies],
            BuildDirectory = node.Target.BuildDirectory,
            Configuration = node.Target.Configuration,
            Target = node.Target.Target,
            WorkingDirectory = node.WorkingDirectory,
            RequestedContext = node.Requested,
            EnvironmentOverrideHash = EnvironmentProvenance.FingerprintOverrides(definition.Environment),
            ToolchainFingerprint = toolchain?.Fingerprint,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
            Stdout = logStem + ".stdout.log",
            Stderr = logStem + ".stderr.log"
        };
        Stopwatch timer = Stopwatch.StartNew();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (definition.TimeoutSeconds is { } seconds)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            if (toolchain is not null)
            {
                WorkflowToolchainAdmission.ValidateBuildTree(node.Target.BuildDirectory, toolchain);
            }

            result.ExecutionConditions = WorkflowExecutionAdmission.Require(definition.Constraints);
            if (definition.Constraints?.Profiler is { } requiredProfiler)
            {
                result.ExecutionConditions["profilerRequired"] = requiredProfiler.Backend + "/" + requiredProfiler.Mode;
            }

            for (int index = 0; index < definition.Inputs.Count; index++)
            {
                WorkflowArtifactInput input = definition.Inputs[index];
                NodeResult? producer = input.From is null ? null : artifactSources[input.From];
                WorkflowArtifactEvidence? produced = producer?.DeclaredArtifacts.SingleOrDefault(x =>
                                   x.Role == "output" && x.Name == input.Artifact)
                               ?? (input.From is null
                                   ? null
                                   : throw new InvalidDataException(
                                       $"{definition.Id}: producer output '{input.Artifact}' has no verified evidence."));
                string path = input.Path is null
                    ? produced!.Path
                    : WorkflowArtifacts.ManifestPath(Path.GetDirectoryName(manifest)!, input.Path);
                string sha = await WorkflowArtifacts.HashAsync(path, deadline.Token);
                if ((input.Sha256 is { } expected && !sha.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    || (produced is not null && sha != produced.Sha256))
                {
                    throw new InvalidDataException($"{definition.Id}: declared input changed: {path}");
                }

                string? snapshot = null;
                if (input.Snapshot)
                {
                    snapshot = Path.Combine(output, "artifacts", definition.Id, "inputs", index.ToString(System.Globalization.CultureInfo.CurrentCulture));
                    await WorkflowArtifacts.CopySnapshotAsync(path, snapshot, deadline.Token);
                    if (await WorkflowArtifacts.HashAsync(snapshot, deadline.Token) != sha)
                    {
                        throw new InvalidDataException($"{definition.Id}: source changed during snapshot: {path}");
                    }
                }

                result.DeclaredArtifacts.Add(new(input.Artifact ?? index.ToString(System.Globalization.CultureInfo.CurrentCulture), path, sha,
                    "input", input.From, snapshot));
            }

            foreach (WorkflowArtifactOutput? declared in definition.Outputs.Where(x => x.Fresh))
            {
                string path = WorkflowArtifacts.OutputPath(manifest, output, declared);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new InvalidDataException($"{definition.Id}: fresh output already exists: {path}");
                }
            }

            result.ObservedContext = await CMakeInspection.InspectAsync(node.Requested.BuildDirectory, deadline.Token);
            // Inspect never changes the execution session or claims ownership.
            result.SessionBefore = definition.Kind == "inspect"
                ? session.State(node.Requested with { Mode = "observe" }, result.ObservedContext)
                : session.Observe(node.Requested, result.ObservedContext, "BeforeOperation");
            if (definition.Kind == "inspect")
            {
                result.Status = "succeeded";
                result.SessionAfter = result.SessionBefore;
            }
            else if (definition.Kind == "configure")
            {
                session.BeginConfigure();
                await ConfigureAsync(node, result, session, output, toolchain, ct: deadline.Token);
            }
            else
            {
                // Re-inspect under ownership at every operation, including after configure.
                session.Validate(node.Requested, result.ObservedContext);
                CMakeTargetRef target = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested),
                    result.ObservedContext, node.Target.Target);
                result.Configuration = target.Configuration;
                switch (definition.Kind)
                {
                    case "build":
                    case "invoke":
                        if (definition.Kind == "build")
                        {
                            _ = CMakeInspection.RequireTarget(result.ObservedContext, target);
                        }

                        CMakeBuildCommand command = CMakeBuildCommand.Create(target);
                        result.Executable = toolchain?.CMake ?? result.ObservedContext.CMakeExecutable ?? "cmake";
                        result.Arguments = [.. command.Arguments];
                        break;
                    case "run":
                    case "profile":
                        if (prerequisite?.BuildVerification is not
                            { Verification: "BackendSucceeded" or "ManuallyAccepted" } verified
                            || verified.Target != target)
                        {
                            throw new InvalidOperationException(
                                "Run requires a successful native build verification for this exact target/configuration.");
                        }

                        if (verified.Generation != GenerationObservation.From(result.ObservedContext))
                        {
                            throw new InvalidOperationException(
                                "Generation changed since the referenced build verification; add a new build before Run. Artifact existence does not verify the new buildsystem.");
                        }

                        NativeTargetObservation item = CMakeInspection.RequireTarget(result.ObservedContext, target, executableOnly: true);
                        if (item.Executable is null)
                        {
                            throw new InvalidOperationException(
                                "Executable artifact is Unknown/ambiguous; no path is guessed.");
                        }

                        if (prerequisite?.Artifact is not { } builtArtifact
                            || builtArtifact.Target != target
                            || !WorkflowPlanner.PathComparer.Equals(builtArtifact.Executable, item.Executable))
                        {
                            throw new InvalidOperationException(
                                "Run requires the executable artifact produced and recorded by its build node.");
                        }

                        string currentHash = await HashExecutableAsync(item.Executable, deadline.Token);
                        if (currentHash != builtArtifact.Sha256)
                        {
                            throw new InvalidOperationException(
                                "Executable changed since the referenced build node; add a new build dependency before Run.");
                        }

                        result.Executable = item.Executable;
                        result.ExecutableSha256 = currentHash;
                        result.Artifact = new ArtifactReference(definition.From!, target, item.Executable,
                            currentHash, "VerifiedAgainstBuild");
                        result.Arguments = [.. definition.Arguments];
                        break;
                    case "ctest":
                        result.Executable = result.ObservedContext.CTestExecutable ?? "ctest";
                        result.Arguments =
                            ["--test-dir", target.BuildDirectory, "--output-on-failure", "--no-tests=error"];
                        if (!string.IsNullOrEmpty(target.Configuration))
                        {
                            result.Arguments.AddRange(["-C", target.Configuration]);
                        }

                        if (definition.TestRegex is not null)
                        {
                            result.Arguments.AddRange(["-R", definition.TestRegex]);
                        }

                        if (definition.LabelRegex is not null)
                        {
                            result.Arguments.AddRange(["-L", definition.LabelRegex]);
                        }

                        break;
                    default:
                        break;
                }

                if (definition.Kind == "profile")
                {
                    if (extension is null || !extension.Supports("profile"))
                    {
                        throw new InvalidOperationException("Profile operation is unavailable in this CLI.");
                    }

                    WorkflowOperationResult operation = await extension.ExecuteAsync(new WorkflowOperationRequest(
                        "profile", result.Executable!, result.Arguments, node.WorkingDirectory,
                        MergeEnvironment(toolchain, definition.Environment), definition.Options, output, definition.Id,
                        definition.Constraints?.CpuSet, definition.Constraints?.Profiler,
                        result.ExecutionConditions.GetValueOrDefault("cpuAffinityLauncher")), deadline.Token);
                    result.ExitCode = operation.ExitCode;
                    result.Outputs = new Dictionary<string, string>(operation.Outputs);
                    if (operation.Outputs.TryGetValue("backend", out string? selectedBackend))
                    {
                        result.ExecutionConditions["profilerBackend"] = selectedBackend;
                    }

                    if (operation.Outputs.TryGetValue("eventSource", out string? eventSource))
                    {
                        result.ExecutionConditions["profilerEventSource"] = eventSource;
                    }

                    result.ExperimentResult = operation.Status;
                    if (operation.Message is not null)
                    {
                        result.Error = operation.Message;
                    }
                }
                else
                {
                    if (definition.Kind == "invoke" && definition.HarnessDirectory is not null)
                    {
                        DagFlowHarnessSnapshot.RequireFresh(node.Target.BuildDirectory, definition.HarnessDirectory);
                    }

                    result.ExitCode = await WorkflowProcessDispatcher.RunAsync(result,
                        MergeEnvironment(toolchain, definition.Environment), output, deadline.Token,
                        elapsed => Console.Error.WriteLine(
                            $"[CMakeWorkflow] RUNNING {result.Id} {elapsed:hh\\:mm\\:ss} elapsed"));
                }

                result.Status = result.ExitCode == 0 ? "succeeded" : "failed";
                if (result.ExitCode != 0)
                {
                    result.Error ??= $"Process exited with code {result.ExitCode}.";
                }
                // The command's result remains factual even if subsequent context
                // validation rejects the node and blocks dependent execution.
                if (definition.Kind is "run" or "ctest" or "invoke")
                {
                    result.ExperimentResult = result.Status;
                }

                if (definition.Kind == "build")
                {
                    result.BuildVerification = new(target, result.ExitCode == 0 ? "BackendSucceeded" : "BackendFailed",
                        result.ExitCode.Value, result.ObservedContext.IndexFile,
                        GenerationObservation.From(result.ObservedContext));
                    if (result.ExitCode == 0)
                    {
                        result.ObservationAfter =
                            await CMakeInspection.InspectAsync(target.BuildDirectory, deadline.Token);
                        result.BuildVerification = result.BuildVerification with
                        {
                            ObservationIndex = result.ObservationAfter.IndexFile,
                            Generation = GenerationObservation.From(result.ObservationAfter)
                        };
                        result.SessionAfter = session.Observe(node.Requested, result.ObservationAfter, "AfterBuild");
                        // Classify actual target/configuration conflicts before applying
                        // provenance policy; a new generation alone is not a mismatch.
                        CMakeTargetRef after = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested) with
                        {
                            ProvenancePolicy = "allowUnverified"
                        }, result.ObservationAfter, target.Target);
                        if (after != target)
                        {
                            throw new InvalidOperationException(
                                "Build regenerated a different configuration; dependent execution is blocked.");
                        }

                        NativeTargetObservation builtTarget = CMakeInspection.RequireTarget(result.ObservationAfter, after);
                        NativeTargetObservation beforeTarget = CMakeInspection.RequireTarget(result.ObservedContext, target);
                        if (builtTarget.Type != beforeTarget.Type || builtTarget.Executable != beforeTarget.Executable)
                        {
                            throw new InvalidOperationException(
                                "Build regenerated a different target type or artifact path; dependent execution is blocked.");
                        }

                        session.Validate(node.Requested, result.ObservationAfter);
                        if (snapshotExecutable && builtTarget.Type == "EXECUTABLE" &&
                            builtTarget.Executable is { } executable
                            && File.Exists(executable))
                        {
                            string sha = await HashExecutableAsync(executable, deadline.Token);
                            result.Artifact = new ArtifactReference(definition.Id, target, executable,
                                sha, "VerifiedAfterBuild");
                            result.ExecutableSha256 = sha;
                        }
                    }
                }
                else if (definition.Kind == "invoke" && result.ExitCode == 0)
                {
                    result.ObservationAfter = await CMakeInspection.InspectAsync(target.BuildDirectory, deadline.Token);
                    result.SessionAfter = session.Observe(node.Requested, result.ObservationAfter, "AfterInvoke");
                    CMakeTargetRef after = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested) with
                    {
                        ProvenancePolicy = "allowUnverified"
                    }, result.ObservationAfter, target.Target);
                    if (after != target)
                    {
                        throw new InvalidOperationException(
                            "Invoke regenerated a different configuration; dependent execution is blocked.");
                    }

                    session.Validate(node.Requested, result.ObservationAfter);
                    // Only attach measurements after the CLI's provenance checks pass.
                    if (definition.HarnessDirectory is not null)
                    {
                        result.Outputs = await DagFlowHarnessSnapshot.CaptureAsync(
                            node.Target.BuildDirectory, definition.HarnessDirectory, output, definition.Id,
                            deadline.Token);
                    }
                    else if (result.Stdout is not null && result.StartedAt is { } invokedAt)
                    {
                        result.Outputs = await DagFlowHarnessSnapshot.CaptureReportedAsync(
                            node.Target.BuildDirectory, Path.Combine(output, result.Stdout),
                            output, definition.Id, invokedAt, deadline.Token);
                    }
                }
            }

            if (result.Status == "succeeded")
            {
                foreach (WorkflowArtifactEvidence? evidence in result.DeclaredArtifacts.Where(x => x.Role == "input"))
                {
                    if (await WorkflowArtifacts.HashAsync(evidence.Path, deadline.Token) != evidence.Sha256)
                    {
                        throw new InvalidDataException(
                            $"{definition.Id}: input changed during execution: {evidence.Path}");
                    }
                }

                foreach (WorkflowArtifactOutput declared in definition.Outputs)
                {
                    string path = WorkflowArtifacts.OutputPath(manifest, output, declared);
                    string sha = await WorkflowArtifacts.HashAsync(path, deadline.Token);
                    result.DeclaredArtifacts.Add(new(declared.Name, path, sha, "output"));
                }
            }
        }
        catch (MissingWorkflowToolException error)
        {
            result.Status = "needs_attention";
            result.Error = error.Message;
        }
        catch (OperationCanceledException)
        {
            result.Status = ct.IsCancellationRequested ? "canceled" : "failed";
            result.Error = ct.IsCancellationRequested
                ? "Canceled."
                : $"Timed out after {definition.TimeoutSeconds} seconds.";
            if (definition.Kind is "run" or "profile" or "ctest" or "invoke" && result.Executable is not null)
            {
                result.ExperimentResult = ct.IsCancellationRequested ? "canceled" : "timedOut";
            }
        }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
            // Preserve the factual process result if later provenance or snapshot
            // validation rejects the node after the command has completed.
            if (definition.Kind is "run" or "profile" or "ctest" or "invoke"
                && result.ExperimentResult == "NotRun")
            {
                result.ExperimentResult = "failed";
            }
        }
#pragma warning restore CA1031


        result.SessionAfter ??= session.State(node.Requested,
            result.ObservationAfter ?? result.ObservedContext ?? node.Observed);
        result.FinishedAt = DateTimeOffset.UtcNow;
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        return result;
    }

    private static async Task ConfigureAsync(PlannedNode node, NodeResult result, BuildContextSession session,
        string output, WorkflowToolchainRuntime? toolchain, CancellationToken ct)
    {
        RequestedBuildContext request = node.Requested;
        if (request.SourceDirectory is null || !Directory.Exists(request.SourceDirectory))
        {
            throw new InvalidOperationException("Configure requires an existing context.sourceDirectory.");
        }
        // Context paths are explicit CLI arguments; CMake interprets the named preset.
        // No cache variables, preset inheritance or toolchain rules are reconstructed.
        _ = Directory.CreateDirectory(request.BuildDirectory);
        result.Configure = new ConfigureProvenance
        {
            BuildDirectory = request.BuildDirectory,
            SourceDirectory = request.SourceDirectory,
            Preset = request.ConfigurePreset
        };
        result.Executable = toolchain?.CMake ?? result.ObservedContext?.CMakeExecutable ?? "cmake";
        result.WorkingDirectory = request.SourceDirectory;
        result.Arguments = [];
        if (request.ConfigurePreset is not null)
        {
            if (toolchain is not null)
            {
                throw new InvalidOperationException(
                    "Explicit toolchain and configurePreset cannot be combined without verifying preset compiler selection.");
            }

            result.Arguments.AddRange(["--preset", request.ConfigurePreset]);
        }
        else if (toolchain is not null)
        {
            result.Arguments.AddRange([
                "-G", toolchain.Generator,
                "-DCMAKE_CXX_COMPILER:FILEPATH=" + toolchain.Compiler
            ]);
            if (toolchain.Ninja is not null)
            {
                result.Arguments.Add("-DCMAKE_MAKE_PROGRAM:FILEPATH=" + toolchain.Ninja);
            }
        }

        result.Arguments.AddRange(["-S", request.SourceDirectory, "-B", request.BuildDirectory]);
        result.Configure.Executable = result.Executable;
        result.Configure.WorkingDirectory = result.WorkingDirectory;
        result.Configure.Arguments = [.. result.Arguments];
        await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
        try
        {
            string query = Path.Combine(request.BuildDirectory, ".cmake", "api", "v1", "query", "client-cmake-workflow");
            _ = Directory.CreateDirectory(query);
            foreach (string? kind in new[] { "codemodel-v2", "cache-v2", "toolchains-v1" })
            {
                await File.WriteAllTextAsync(Path.Combine(query, kind), "", ct);
            }

            result.ExitCode = await WorkflowProcessDispatcher.RunAsync(result,
                MergeEnvironment(toolchain, new Dictionary<string, string>()), output, ct,
                elapsed => Console.Error.WriteLine(
                    $"[CMakeWorkflow] RUNNING {result.Id} {elapsed:hh\\:mm\\:ss} elapsed"));
            result.Configure.ExitCode = result.ExitCode;
            result.Configure.State = result.ExitCode == 0 ? "Succeeded" : "Failed";
            await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            result.ObservationAfter = await CMakeInspection.InspectAsync(request.BuildDirectory, ct);
            result.Configure.IndexFile = result.ObservationAfter.IndexFile;
            result.Configure.IndexHash = result.ObservationAfter.IndexHash;
            await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            result.ObservationAfter = await CMakeInspection.InspectAsync(request.BuildDirectory, ct);
            result.Status = result.ExitCode == 0 ? "succeeded" : "failed";
            if (result.ExitCode != 0)
            {
                result.Error = $"Configure exited with code {result.ExitCode}.";
            }
            else
            {
                // Verify requested selection against the new observation; never silently
                // replace a requested Debug/Release configuration with the generated one.
                CMakeInspection.ValidateIdentity(request, result.ObservationAfter);
                if (request.Configuration is not null || result.ObservationAfter.MultiConfig == false)
                {
                    result.Configuration = CMakeInspection.Resolve(request, result.ObservationAfter).Configuration;
                }

                session.Configured(request, result.ObservationAfter);
            }
        }
        catch
        {
            if (result.Configure.State is "Attempted" or "Succeeded")
            {
                result.Configure.State = result.Configure.State == "Attempted" ? "Interrupted" : "Rejected";
                await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            }

            throw;
        }
    }

    internal static async Task<string> HashExecutableAsync(string executable, CancellationToken ct)
    {
        await using FileStream file = File.OpenRead(executable);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(WorkflowToolchainRuntime? toolchain,
        IReadOnlyDictionary<string, string> overrides)
    {
        if (toolchain is null)
        {
            return overrides;
        }

        Dictionary<string, string> values = toolchain.Variables.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        // A run recipe may override application variables, but not silently
        // replace the compiler/SDK activation promised by its build context.
        HashSet<string> protectedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "INCLUDE", "LIB", "LIBPATH", "VCToolsInstallDir", "VCINSTALLDIR",
            "WindowsSdkDir", "WindowsSDKVersion", "VSCMD_ARG_HOST_ARCH", "VSCMD_ARG_TGT_ARCH"
        };
        foreach (KeyValuePair<string, string> entry in overrides)
        {
            if (protectedKeys.Contains(entry.Key) && (!values.TryGetValue(entry.Key, out string? current)
                                                      || !string.Equals(current, entry.Value,
                                                          StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Run override conflicts with bound toolchain environment: " +
                                                    entry.Key);
            }

            values[entry.Key] = entry.Value;
        }

        return values;
    }
}
