using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using Wire = Capnp.Generated.ExecutionReport;

namespace CMakeWorkflow.CMake.Reporting;

internal static partial class NativeExecutionReportCodec
{
    private static void Write(Wire.WorkflowReport.Builder wire, WorkflowReport value)
    {
        wire.SetSchemaVersion(value.SchemaVersion);
        wire.SetWorkflow(value.Workflow);
        wire.SetManifestHash(value.ManifestHash);
        wire.SetSourceSnapshotId(value.SourceSnapshotId);
        Write(wire.InitStartedAt(), value.StartedAt);
        if (value.FinishedAt is { } finishedAt)
        {
            Write(wire.InitFinishedAt(), finishedAt);
        }
        wire.SetStatus(value.Status);
        Wire.WorkflowReport.NodesListBuilder nodesWire = wire.InitNodes(value.Nodes.Count);
        for (int i = 0; i < value.Nodes.Count; i++)
        {
            Write(nodesWire[i], value.Nodes[i]);
        }
        wire.SetError(value.Error);
        Wire.WorkflowReport.FindingsListBuilder findingsWire = wire.InitFindings(value.Findings.Count);
        for (int i = 0; i < value.Findings.Count; i++)
        {
            Write(findingsWire[i], value.Findings[i]);
        }
        wire.SetResumeCount(value.ResumeCount);
        wire.SetRecoveryPhase(value.RecoveryPhase);
        Wire.WorkflowReport.ReconciliationsListBuilder reconciliationsWire = wire.InitReconciliations(value.Reconciliations.Count);
        for (int i = 0; i < value.Reconciliations.Count; i++)
        {
            Write(reconciliationsWire[i], value.Reconciliations[i]);
        }
        if (value.Environment is { } environment)
        {
            Write(wire.InitEnvironment(), environment);
        }
        if (value.EnvironmentAtEnd is { } environmentAtEnd)
        {
            Write(wire.InitEnvironmentAtEnd(), environmentAtEnd);
        }
        Wire.WorkflowReport.BuildEnvironmentsListBuilder buildEnvironmentsWire = wire.InitBuildEnvironments(value.BuildEnvironments.Count);
        for (int i = 0; i < value.BuildEnvironments.Count; i++)
        {
            Write(buildEnvironmentsWire[i], value.BuildEnvironments[i]);
        }
        Wire.WorkflowReport.ToolchainsListBuilder toolchainsWire = wire.InitToolchains(value.Toolchains.Count);
        for (int i = 0; i < value.Toolchains.Count; i++)
        {
            Write(toolchainsWire[i], value.Toolchains[i]);
        }
    }

    private static WorkflowReport ReadWorkflowReport(Wire.WorkflowReport.Reader wire)
    {
        Wire.WorkflowReport.NodesListReader nodesWire = wire.Nodes;
        List<NodeResult> nodes = new(nodesWire.Count);
        for (int i = 0; i < nodesWire.Count; i++)
        {
            nodes.Add(ReadNodeResult(nodesWire[i]));
        }

        Wire.WorkflowReport.FindingsListReader findingsWire = wire.Findings;
        List<WorkflowFinding> findings = new(findingsWire.Count);
        for (int i = 0; i < findingsWire.Count; i++)
        {
            findings.Add(ReadWorkflowFinding(findingsWire[i]));
        }

        Wire.WorkflowReport.ReconciliationsListReader reconciliationsWire = wire.Reconciliations;
        List<WorkflowReconciliation> reconciliations = new(reconciliationsWire.Count);
        for (int i = 0; i < reconciliationsWire.Count; i++)
        {
            reconciliations.Add(ReadWorkflowReconciliation(reconciliationsWire[i]));
        }

        Wire.WorkflowReport.BuildEnvironmentsListReader buildEnvironmentsWire = wire.BuildEnvironments;
        List<BuildEnvironment> buildEnvironments = new(buildEnvironmentsWire.Count);
        for (int i = 0; i < buildEnvironmentsWire.Count; i++)
        {
            buildEnvironments.Add(ReadBuildEnvironment(buildEnvironmentsWire[i]));
        }

        Wire.WorkflowReport.ToolchainsListReader toolchainsWire = wire.Toolchains;
        List<WorkflowToolchainIdentity> toolchains = new(toolchainsWire.Count);
        for (int i = 0; i < toolchainsWire.Count; i++)
        {
            toolchains.Add(ReadWorkflowToolchainIdentity(toolchainsWire[i]));
        }

        return new()
        {
            SchemaVersion = wire.SchemaVersion,
            Workflow = Required(wire.DecodeWorkflow()),
            ManifestHash = Required(wire.DecodeManifestHash()),
            SourceSnapshotId = wire.DecodeSourceSnapshotId(),
            StartedAt = wire.HasStartedAt ? ReadTimestamp(wire.StartedAt) : throw new InvalidDataException("Missing required report timestamp StartedAt."),
            FinishedAt = wire.HasFinishedAt ? ReadTimestamp(wire.FinishedAt) : null,
            Status = Required(wire.DecodeStatus()),
            Nodes = nodes,
            Error = wire.DecodeError(),
            Findings = findings,
            ResumeCount = wire.ResumeCount,
            RecoveryPhase = wire.DecodeRecoveryPhase(),
            Reconciliations = reconciliations,
            Environment = wire.HasEnvironment ? ReadHostEnvironment(wire.Environment) : null,
            EnvironmentAtEnd = wire.HasEnvironmentAtEnd ? ReadHostEnvironment(wire.EnvironmentAtEnd) : null,
            BuildEnvironments = buildEnvironments,
            Toolchains = toolchains,
        };
    }

    private static void Write(Wire.NodeResult.Builder wire, NodeResult value)
    {
        wire.SetId(value.Id);
        wire.SetKind(value.Kind);
        if (value.BenchmarkTrial is { } benchmarkTrial)
        {
            Write(wire.InitBenchmarkTrial(), benchmarkTrial);
        }
        Wire.NodeResult.DependenciesListBuilder dependenciesWire = wire.InitDependencies(value.Dependencies.Count);
        for (int i = 0; i < value.Dependencies.Count; i++)
        {
            dependenciesWire.Set(i, value.Dependencies[i]);
        }
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetConfiguration(value.Configuration);
        wire.SetTarget(value.Target);
        wire.SetStatus(value.Status);
        if (value.StartedAt is { } startedAt)
        {
            Write(wire.InitStartedAt(), startedAt);
        }
        if (value.FinishedAt is { } finishedAt)
        {
            Write(wire.InitFinishedAt(), finishedAt);
        }
        if (value.DurationSeconds is { } durationSeconds)
        {
            wire.InitDurationSeconds().SetValue(durationSeconds);
        }
        if (value.ProcessDurationSeconds is { } processDurationSeconds)
        {
            wire.InitProcessDurationSeconds().SetValue(processDurationSeconds);
        }
        if (value.Metrics is { } metrics)
        {
            Wire.NodeResult.MetricsListBuilder metricsWire = wire.InitMetrics(metrics.Count);
            for (int i = 0; i < metrics.Count; i++)
            {
                Write(metricsWire[i], metrics[i]);
            }
        }
        if (value.ProcessResources is { } processResources)
        {
            Write(wire.InitProcessResources(), processResources);
        }
        if (value.ExitCode is { } exitCode)
        {
            wire.InitExitCode().SetValue(exitCode);
        }
        wire.SetExecutable(value.Executable);
        wire.SetExecutableSha256(value.ExecutableSha256);
        Wire.NodeResult.ArgumentsListBuilder argumentsWire = wire.InitArguments(value.Arguments.Count);
        for (int i = 0; i < value.Arguments.Count; i++)
        {
            argumentsWire.Set(i, value.Arguments[i]);
        }
        wire.SetEnvironmentOverrideHash(value.EnvironmentOverrideHash);
        wire.SetToolchainFingerprint(value.ToolchainFingerprint);
        wire.SetBuildVariantId(value.BuildVariantId);
        wire.SetKitLockSha256(value.KitLockSha256);
        wire.SetCompilerCacheFingerprint(value.CompilerCacheFingerprint);
        if (value.CompilerCache is { } compilerCache)
        {
            Write(wire.InitCompilerCache(), compilerCache);
        }
        wire.SetActionDigest(value.ActionDigest);
        if (value.ActionCacheHit is { } actionCacheHit)
        {
            wire.InitActionCacheHit().SetValue(actionCacheHit);
        }
        wire.SetWorkingDirectory(value.WorkingDirectory);
        wire.SetStdout(value.Stdout);
        wire.SetStderr(value.Stderr);
        wire.SetError(value.Error);
        if (value.RequestedContext is { } requestedContext)
        {
            Write(wire.InitRequestedContext(), requestedContext);
        }
        if (value.ObservedContext is { } observedContext)
        {
            Write(wire.InitObservedContext(), observedContext);
        }
        if (value.ObservationAfter is { } observationAfter)
        {
            Write(wire.InitObservationAfter(), observationAfter);
        }
        if (value.BuildVerification is { } buildVerification)
        {
            Write(wire.InitBuildVerification(), buildVerification);
        }
        if (value.Artifact is { } artifact)
        {
            Write(wire.InitArtifact(), artifact);
        }
        if (value.Configure is { } configure)
        {
            Write(wire.InitConfigure(), configure);
        }
        if (value.SessionBefore is { } sessionBefore)
        {
            Write(wire.InitSessionBefore(), sessionBefore);
        }
        if (value.SessionAfter is { } sessionAfter)
        {
            Write(wire.InitSessionAfter(), sessionAfter);
        }
        wire.SetExperimentResult(value.ExperimentResult);
        Wire.NodeResult.OutputsListBuilder outputsWire = wire.InitOutputs(value.Outputs.Count);
        int outputsIndex = 0;
        foreach ((string key, string item) in value.Outputs)
        {
            Wire.Entry.Builder entry = outputsWire[outputsIndex++];
            entry.SetKey(key);
            entry.SetValue(item);
        }
        Wire.NodeResult.DeclaredArtifactsListBuilder declaredArtifactsWire = wire.InitDeclaredArtifacts(value.DeclaredArtifacts.Count);
        for (int i = 0; i < value.DeclaredArtifacts.Count; i++)
        {
            Write(declaredArtifactsWire[i], value.DeclaredArtifacts[i]);
        }
        Wire.NodeResult.ExecutionConditionsListBuilder executionConditionsWire = wire.InitExecutionConditions(value.ExecutionConditions.Count);
        int executionConditionsIndex = 0;
        foreach ((string key, string item) in value.ExecutionConditions)
        {
            Wire.Entry.Builder entry = executionConditionsWire[executionConditionsIndex++];
            entry.SetKey(key);
            entry.SetValue(item);
        }
        if (value.Profile is { } profile)
        {
            Write(wire.InitProfile(), profile);
        }
    }

    private static NodeResult ReadNodeResult(Wire.NodeResult.Reader wire)
    {
        Wire.NodeResult.DependenciesListReader dependenciesWire = wire.Dependencies;
        List<string> dependencies = new(dependenciesWire.Count);
        for (int i = 0; i < dependenciesWire.Count; i++)
        {
            dependencies.Add(Required(dependenciesWire.Decode(i)));
        }

        Wire.NodeResult.MetricsListReader metricsWire = wire.Metrics;
        List<BenchmarkMetricSample> metrics = new(metricsWire.Count);
        for (int i = 0; i < metricsWire.Count; i++)
        {
            metrics.Add(ReadBenchmarkMetricSample(metricsWire[i]));
        }

        Wire.NodeResult.ArgumentsListReader argumentsWire = wire.Arguments;
        List<string> arguments = new(argumentsWire.Count);
        for (int i = 0; i < argumentsWire.Count; i++)
        {
            arguments.Add(Required(argumentsWire.Decode(i)));
        }

        Wire.NodeResult.OutputsListReader outputsWire = wire.Outputs;
        Dictionary<string, string> outputs = new(outputsWire.Count, StringComparer.Ordinal);
        for (int i = 0; i < outputsWire.Count; i++)
        {
            Wire.Entry.Reader entry = outputsWire[i];
            if (!outputs.TryAdd(Required(entry.DecodeKey()), Required(entry.DecodeValue())))
            {
                throw new InvalidDataException("Duplicate report map key.");
            }
        }

        Wire.NodeResult.DeclaredArtifactsListReader declaredArtifactsWire = wire.DeclaredArtifacts;
        List<WorkflowArtifactEvidence> declaredArtifacts = new(declaredArtifactsWire.Count);
        for (int i = 0; i < declaredArtifactsWire.Count; i++)
        {
            declaredArtifacts.Add(ReadWorkflowArtifactEvidence(declaredArtifactsWire[i]));
        }

        Wire.NodeResult.ExecutionConditionsListReader executionConditionsWire = wire.ExecutionConditions;
        Dictionary<string, string> executionConditions = new(executionConditionsWire.Count, StringComparer.Ordinal);
        for (int i = 0; i < executionConditionsWire.Count; i++)
        {
            Wire.Entry.Reader entry = executionConditionsWire[i];
            if (!executionConditions.TryAdd(Required(entry.DecodeKey()), Required(entry.DecodeValue())))
            {
                throw new InvalidDataException("Duplicate report map key.");
            }
        }

        return new()
        {
            Id = Required(wire.DecodeId()),
            Kind = Required(wire.DecodeKind()),
            BenchmarkTrial = wire.HasBenchmarkTrial ? ReadBenchmarkTrial(wire.BenchmarkTrial) : null,
            Dependencies = dependencies,
            BuildDirectory = Required(wire.DecodeBuildDirectory()),
            Configuration = wire.DecodeConfiguration(),
            Target = Required(wire.DecodeTarget()),
            Status = Required(wire.DecodeStatus()),
            StartedAt = wire.HasStartedAt ? ReadTimestamp(wire.StartedAt) : null,
            FinishedAt = wire.HasFinishedAt ? ReadTimestamp(wire.FinishedAt) : null,
            DurationSeconds = wire.HasDurationSeconds ? wire.DurationSeconds.Value : null,
            ProcessDurationSeconds = wire.HasProcessDurationSeconds ? wire.ProcessDurationSeconds.Value : null,
            Metrics = wire.HasMetrics ? metrics : null,
            ProcessResources = wire.HasProcessResources ? ReadProcessResourceUsage(wire.ProcessResources) : null,
            ExitCode = wire.HasExitCode ? wire.ExitCode.Value : null,
            Executable = wire.DecodeExecutable(),
            ExecutableSha256 = wire.DecodeExecutableSha256(),
            Arguments = arguments,
            EnvironmentOverrideHash = wire.DecodeEnvironmentOverrideHash(),
            ToolchainFingerprint = wire.DecodeToolchainFingerprint(),
            BuildVariantId = wire.DecodeBuildVariantId(),
            KitLockSha256 = wire.DecodeKitLockSha256(),
            CompilerCacheFingerprint = wire.DecodeCompilerCacheFingerprint(),
            CompilerCache = wire.HasCompilerCache ? ReadCompilerCacheStatistics(wire.CompilerCache) : null,
            ActionDigest = wire.DecodeActionDigest(),
            ActionCacheHit = wire.HasActionCacheHit ? wire.ActionCacheHit.Value : null,
            WorkingDirectory = Required(wire.DecodeWorkingDirectory()),
            Stdout = wire.DecodeStdout(),
            Stderr = wire.DecodeStderr(),
            Error = wire.DecodeError(),
            RequestedContext = wire.HasRequestedContext ? ReadRequestedBuildContext(wire.RequestedContext) : null,
            ObservedContext = wire.HasObservedContext ? ReadObservedBuildContext(wire.ObservedContext) : null,
            ObservationAfter = wire.HasObservationAfter ? ReadObservedBuildContext(wire.ObservationAfter) : null,
            BuildVerification = wire.HasBuildVerification ? ReadNativeBuildResult(wire.BuildVerification) : null,
            Artifact = wire.HasArtifact ? ReadArtifactReference(wire.Artifact) : null,
            Configure = wire.HasConfigure ? ReadConfigureProvenance(wire.Configure) : null,
            SessionBefore = wire.HasSessionBefore ? ReadBuildContextSessionState(wire.SessionBefore) : null,
            SessionAfter = wire.HasSessionAfter ? ReadBuildContextSessionState(wire.SessionAfter) : null,
            ExperimentResult = Required(wire.DecodeExperimentResult()),
            Outputs = outputs,
            DeclaredArtifacts = declaredArtifacts,
            ExecutionConditions = executionConditions,
            Profile = wire.HasProfile ? ReadWorkflowProfileEvidence(wire.Profile) : null,
        };
    }

    private static void Write(Wire.BenchmarkTrial.Builder wire, BenchmarkTrial value)
    {
        wire.SetBaselineVariant(value.BaselineVariant);
        wire.SetExecutionOrder(value.ExecutionOrder);
        Wire.BenchmarkTrial.AxesListBuilder axesWire = wire.InitAxes(value.Axes.Count);
        for (int i = 0; i < value.Axes.Count; i++)
        {
            Write(axesWire[i], value.Axes[i]);
        }
    }

    private static BenchmarkTrial ReadBenchmarkTrial(Wire.BenchmarkTrial.Reader wire)
    {
        Wire.BenchmarkTrial.AxesListReader axesWire = wire.Axes;
        List<BenchmarkAxisCoordinate> axes = new(axesWire.Count);
        for (int i = 0; i < axesWire.Count; i++)
        {
            axes.Add(ReadBenchmarkAxisCoordinate(axesWire[i]));
        }

        return new(
            Required(wire.DecodeBaselineVariant()),
            Required(wire.DecodeExecutionOrder()),
            axes);
    }

    private static void Write(Wire.BenchmarkAxisCoordinate.Builder wire, BenchmarkAxisCoordinate value)
    {
        wire.SetName(value.Name);
        wire.SetFlag(value.Flag);
        wire.SetValue(value.Value);
        wire.SetRole(value.Role);
    }

    private static BenchmarkAxisCoordinate ReadBenchmarkAxisCoordinate(Wire.BenchmarkAxisCoordinate.Reader wire)
    {
        return new(
            Required(wire.DecodeName()),
            Required(wire.DecodeFlag()),
            Required(wire.DecodeValue()),
            wire.DecodeRole());
    }

    private static void Write(Wire.BenchmarkMetricSample.Builder wire, BenchmarkMetricSample value)
    {
        wire.SetName(value.Name);
        wire.SetUnit(value.Unit);
        wire.SetDirection(value.Direction);
        wire.SetPath(value.Path);
        wire.SetValue(value.Value);
    }

    private static BenchmarkMetricSample ReadBenchmarkMetricSample(Wire.BenchmarkMetricSample.Reader wire)
    {
        return new(
            Required(wire.DecodeName()),
            Required(wire.DecodeUnit()),
            Required(wire.DecodeDirection()),
            Required(wire.DecodePath()),
            wire.Value);
    }

    private static void Write(Wire.ProcessResourceUsage.Builder wire, ProcessResourceUsage value)
    {
        wire.SetSchemaVersion(value.SchemaVersion);
        wire.SetScope(value.Scope);
        wire.SetMethod(value.Method);
        wire.SetSampleIntervalMilliseconds(value.SampleIntervalMilliseconds);
        wire.SetSamples(value.Samples);
        if (value.PeakRssBytes is { } peakRssBytes)
        {
            wire.InitPeakRssBytes().SetValue(peakRssBytes);
        }
        if (value.CpuSeconds is { } cpuSeconds)
        {
            wire.InitCpuSeconds().SetValue(cpuSeconds);
        }
    }

    private static ProcessResourceUsage ReadProcessResourceUsage(Wire.ProcessResourceUsage.Reader wire)
    {
        return new(
            wire.SchemaVersion,
            Required(wire.DecodeScope()),
            Required(wire.DecodeMethod()),
            wire.SampleIntervalMilliseconds,
            wire.Samples,
            wire.HasPeakRssBytes ? wire.PeakRssBytes.Value : null,
            wire.HasCpuSeconds ? wire.CpuSeconds.Value : null);
    }

    private static void Write(Wire.CompilerCacheStatistics.Builder wire, CompilerCacheStatistics value)
    {
        wire.SetSchemaVersion(value.SchemaVersion);
        wire.SetProfile(value.Profile);
        wire.SetProvider(value.Provider);
        wire.SetScope(value.Scope);
        if (value.Hits is { } hits)
        {
            wire.InitHits().SetValue(hits);
        }
        if (value.Misses is { } misses)
        {
            wire.InitMisses().SetValue(misses);
        }
        if (value.RemoteReadHits is { } remoteReadHits)
        {
            wire.InitRemoteReadHits().SetValue(remoteReadHits);
        }
        if (value.RemoteWrites is { } remoteWrites)
        {
            wire.InitRemoteWrites().SetValue(remoteWrites);
        }
        if (value.CompilerWorkSeconds is { } compilerWorkSeconds)
        {
            wire.InitCompilerWorkSeconds().SetValue(compilerWorkSeconds);
        }
        if (value.BytesRead is { } bytesRead)
        {
            wire.InitBytesRead().SetValue(bytesRead);
        }
        if (value.BytesWritten is { } bytesWritten)
        {
            wire.InitBytesWritten().SetValue(bytesWritten);
        }
        if (value.CompileSeconds is { } compileSeconds)
        {
            wire.InitCompileSeconds().SetValue(compileSeconds);
        }
    }

    private static CompilerCacheStatistics ReadCompilerCacheStatistics(Wire.CompilerCacheStatistics.Reader wire)
    {
        return new(
            wire.SchemaVersion,
            Required(wire.DecodeProfile()),
            Required(wire.DecodeProvider()),
            Required(wire.DecodeScope()),
            wire.HasHits ? wire.Hits.Value : null,
            wire.HasMisses ? wire.Misses.Value : null,
            wire.HasRemoteReadHits ? wire.RemoteReadHits.Value : null,
            wire.HasRemoteWrites ? wire.RemoteWrites.Value : null,
            wire.HasCompilerWorkSeconds ? wire.CompilerWorkSeconds.Value : null,
            wire.HasBytesRead ? wire.BytesRead.Value : null,
            wire.HasBytesWritten ? wire.BytesWritten.Value : null,
            wire.HasCompileSeconds ? wire.CompileSeconds.Value : null);
    }

    private static void Write(Wire.RequestedBuildContext.Builder wire, RequestedBuildContext value)
    {
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetConfiguration(value.Configuration);
        wire.SetConfigurePreset(value.ConfigurePreset);
        wire.SetSourceDirectory(value.SourceDirectory);
        wire.SetProvenancePolicy(value.ProvenancePolicy);
        wire.SetMode(value.Mode);
        wire.SetGenerator(value.Generator);
    }

    private static RequestedBuildContext ReadRequestedBuildContext(Wire.RequestedBuildContext.Reader wire)
    {
        return new(
            Required(wire.DecodeBuildDirectory()),
            wire.DecodeConfiguration(),
            wire.DecodeConfigurePreset(),
            wire.DecodeSourceDirectory(),
            Required(wire.DecodeProvenancePolicy()),
            Required(wire.DecodeMode()),
            wire.DecodeGenerator());
    }

    private static void Write(Wire.ObservedBuildContext.Builder wire, ObservedBuildContext value)
    {
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetState(value.State);
        wire.SetFreshness(value.Freshness);
        wire.SetPresetState(value.PresetState);
        wire.SetSourceDirectory(value.SourceDirectory);
        wire.SetGenerator(value.Generator);
        if (value.MultiConfig is { } multiConfig)
        {
            wire.InitMultiConfig().SetValue(multiConfig);
        }
        Wire.ObservedBuildContext.ConfigurationsListBuilder configurationsWire = wire.InitConfigurations(value.Configurations.Count);
        for (int i = 0; i < value.Configurations.Count; i++)
        {
            configurationsWire.Set(i, value.Configurations[i]);
        }
        wire.SetIndexFile(value.IndexFile);
        wire.SetIndexHash(value.IndexHash);
        wire.SetCMakeExecutable(value.CMakeExecutable);
        wire.SetCTestExecutable(value.CTestExecutable);
        wire.SetHasCodeModel(value.HasCodeModel);
        wire.SetHasCacheReply(value.HasCacheReply);
        Wire.ObservedBuildContext.ToolchainsListBuilder toolchainsWire = wire.InitToolchains(value.Toolchains.Count);
        for (int i = 0; i < value.Toolchains.Count; i++)
        {
            toolchainsWire.Set(i, value.Toolchains[i]);
        }
        Wire.ObservedBuildContext.TargetsListBuilder targetsWire = wire.InitTargets(value.Targets.Count);
        for (int i = 0; i < value.Targets.Count; i++)
        {
            Write(targetsWire[i], value.Targets[i]);
        }
        Wire.ObservedBuildContext.DiagnosticsListBuilder diagnosticsWire = wire.InitDiagnostics(value.Diagnostics.Count);
        for (int i = 0; i < value.Diagnostics.Count; i++)
        {
            diagnosticsWire.Set(i, value.Diagnostics[i]);
        }
        if (value.ManagedConfigure is { } managedConfigure)
        {
            Write(wire.InitManagedConfigure(), managedConfigure);
        }
    }

    private static ObservedBuildContext ReadObservedBuildContext(Wire.ObservedBuildContext.Reader wire)
    {
        Wire.ObservedBuildContext.ConfigurationsListReader configurationsWire = wire.Configurations;
        List<string> configurations = new(configurationsWire.Count);
        for (int i = 0; i < configurationsWire.Count; i++)
        {
            configurations.Add(Required(configurationsWire.Decode(i)));
        }

        Wire.ObservedBuildContext.ToolchainsListReader toolchainsWire = wire.Toolchains;
        List<string> toolchains = new(toolchainsWire.Count);
        for (int i = 0; i < toolchainsWire.Count; i++)
        {
            toolchains.Add(Required(toolchainsWire.Decode(i)));
        }

        Wire.ObservedBuildContext.TargetsListReader targetsWire = wire.Targets;
        List<NativeTargetObservation> targets = new(targetsWire.Count);
        for (int i = 0; i < targetsWire.Count; i++)
        {
            targets.Add(ReadNativeTargetObservation(targetsWire[i]));
        }

        Wire.ObservedBuildContext.DiagnosticsListReader diagnosticsWire = wire.Diagnostics;
        List<string> diagnostics = new(diagnosticsWire.Count);
        for (int i = 0; i < diagnosticsWire.Count; i++)
        {
            diagnostics.Add(Required(diagnosticsWire.Decode(i)));
        }

        return new()
        {
            BuildDirectory = Required(wire.DecodeBuildDirectory()),
            State = Required(wire.DecodeState()),
            Freshness = Required(wire.DecodeFreshness()),
            PresetState = Required(wire.DecodePresetState()),
            SourceDirectory = wire.DecodeSourceDirectory(),
            Generator = wire.DecodeGenerator(),
            MultiConfig = wire.HasMultiConfig ? wire.MultiConfig.Value : null,
            Configurations = configurations,
            IndexFile = wire.DecodeIndexFile(),
            IndexHash = wire.DecodeIndexHash(),
            CMakeExecutable = wire.DecodeCMakeExecutable(),
            CTestExecutable = wire.DecodeCTestExecutable(),
            HasCodeModel = wire.HasCodeModel,
            HasCacheReply = wire.HasCacheReply,
            Toolchains = toolchains,
            Targets = targets,
            Diagnostics = diagnostics,
            ManagedConfigure = wire.HasManagedConfigure ? ReadConfigureProvenance(wire.ManagedConfigure) : null,
        };
    }

    private static void Write(Wire.NativeTargetObservation.Builder wire, NativeTargetObservation value)
    {
        wire.SetName(value.Name);
        wire.SetConfiguration(value.Configuration);
        wire.SetType(value.Type);
        wire.SetExecutable(value.Executable);
        wire.SetEvidence(value.Evidence);
    }

    private static NativeTargetObservation ReadNativeTargetObservation(Wire.NativeTargetObservation.Reader wire)
    {
        return new(
            Required(wire.DecodeName()),
            Required(wire.DecodeConfiguration()),
            Required(wire.DecodeType()),
            wire.DecodeExecutable(),
            Required(wire.DecodeEvidence()));
    }

    private static void Write(Wire.ConfigureProvenance.Builder wire, ConfigureProvenance value)
    {
        wire.SetState(value.State);
        Write(wire.InitAt(), value.At);
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetSourceDirectory(value.SourceDirectory);
        wire.SetPreset(value.Preset);
        wire.SetIndexFile(value.IndexFile);
        wire.SetIndexHash(value.IndexHash);
        if (value.ExitCode is { } exitCode)
        {
            wire.InitExitCode().SetValue(exitCode);
        }
        wire.SetExecutable(value.Executable);
        wire.SetWorkingDirectory(value.WorkingDirectory);
        wire.SetBuildVariantId(value.BuildVariantId);
        Wire.ConfigureProvenance.ArgumentsListBuilder argumentsWire = wire.InitArguments(value.Arguments.Count);
        for (int i = 0; i < value.Arguments.Count; i++)
        {
            argumentsWire.Set(i, value.Arguments[i]);
        }
    }

    private static ConfigureProvenance ReadConfigureProvenance(Wire.ConfigureProvenance.Reader wire)
    {
        Wire.ConfigureProvenance.ArgumentsListReader argumentsWire = wire.Arguments;
        List<string> arguments = new(argumentsWire.Count);
        for (int i = 0; i < argumentsWire.Count; i++)
        {
            arguments.Add(Required(argumentsWire.Decode(i)));
        }

        return new()
        {
            State = Required(wire.DecodeState()),
            At = wire.HasAt ? ReadTimestamp(wire.At) : throw new InvalidDataException("Missing required report timestamp At."),
            BuildDirectory = Required(wire.DecodeBuildDirectory()),
            SourceDirectory = Required(wire.DecodeSourceDirectory()),
            Preset = wire.DecodePreset(),
            IndexFile = wire.DecodeIndexFile(),
            IndexHash = wire.DecodeIndexHash(),
            ExitCode = wire.HasExitCode ? wire.ExitCode.Value : null,
            Executable = wire.DecodeExecutable(),
            WorkingDirectory = wire.DecodeWorkingDirectory(),
            BuildVariantId = wire.DecodeBuildVariantId(),
            Arguments = arguments,
        };
    }

    private static void Write(Wire.NativeBuildResult.Builder wire, NativeBuildResult value)
    {
        Write(wire.InitTarget(), value.Target);
        wire.SetVerification(value.Verification);
        if (value.BackendExitCode is { } backendExitCode)
        {
            wire.InitBackendExitCode().SetValue(backendExitCode);
        }
        wire.SetObservationIndex(value.ObservationIndex);
        if (value.Generation is { } generation)
        {
            Write(wire.InitGeneration(), generation);
        }
    }

    private static NativeBuildResult ReadNativeBuildResult(Wire.NativeBuildResult.Reader wire)
    {
        return new(
            wire.HasTarget ? ReadCMakeTargetRef(wire.Target) : throw new InvalidDataException("Missing required report field Target."),
            Required(wire.DecodeVerification()),
            wire.HasBackendExitCode ? wire.BackendExitCode.Value : null,
            wire.DecodeObservationIndex(),
            wire.HasGeneration ? ReadGenerationObservation(wire.Generation) : null);
    }

    private static void Write(Wire.CMakeTargetRef.Builder wire, CMakeTargetRef value)
    {
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetTarget(value.Target);
        wire.SetConfiguration(value.Configuration);
    }

    private static CMakeTargetRef ReadCMakeTargetRef(Wire.CMakeTargetRef.Reader wire)
    {
        return new(
            Required(wire.DecodeBuildDirectory()),
            Required(wire.DecodeTarget()),
            wire.DecodeConfiguration());
    }

    private static void Write(Wire.GenerationObservation.Builder wire, GenerationObservation value)
    {
        wire.SetIndexFile(value.IndexFile);
        wire.SetIndexHash(value.IndexHash);
    }

    private static GenerationObservation ReadGenerationObservation(Wire.GenerationObservation.Reader wire)
    {
        return new(
            wire.DecodeIndexFile(),
            wire.DecodeIndexHash());
    }

    private static void Write(Wire.ArtifactReference.Builder wire, ArtifactReference value)
    {
        wire.SetBuildNode(value.BuildNode);
        Write(wire.InitTarget(), value.Target);
        wire.SetExecutable(value.Executable);
        wire.SetSha256(value.Sha256);
        wire.SetMaterialization(value.Materialization);
    }

    private static ArtifactReference ReadArtifactReference(Wire.ArtifactReference.Reader wire)
    {
        return new(
            Required(wire.DecodeBuildNode()),
            wire.HasTarget ? ReadCMakeTargetRef(wire.Target) : throw new InvalidDataException("Missing required report field Target."),
            Required(wire.DecodeExecutable()),
            Required(wire.DecodeSha256()),
            Required(wire.DecodeMaterialization()));
    }

    private static void Write(Wire.BuildContextSessionState.Builder wire, BuildContextSessionState value)
    {
        wire.SetMode(value.Mode);
        wire.SetPolicy(value.Policy);
        wire.SetProvenance(value.Provenance);
        Write(wire.InitGeneration(), value.Generation);
        wire.SetGenerationChange(value.GenerationChange);
        wire.SetObservedPropertiesChanged(value.ObservedPropertiesChanged);
        wire.SetRequiresConfigure(value.RequiresConfigure);
    }

    private static BuildContextSessionState ReadBuildContextSessionState(Wire.BuildContextSessionState.Reader wire)
    {
        return new(
            Required(wire.DecodeMode()),
            Required(wire.DecodePolicy()),
            Required(wire.DecodeProvenance()),
            wire.HasGeneration ? ReadGenerationObservation(wire.Generation) : throw new InvalidDataException("Missing required report field Generation."),
            Required(wire.DecodeGenerationChange()),
            wire.ObservedPropertiesChanged,
            wire.RequiresConfigure);
    }

    private static void Write(Wire.WorkflowArtifactEvidence.Builder wire, WorkflowArtifactEvidence value)
    {
        wire.SetName(value.Name);
        wire.SetPath(value.Path);
        wire.SetSha256(value.Sha256);
        wire.SetRole(value.Role);
        wire.SetFrom(value.From);
        wire.SetSnapshotPath(value.SnapshotPath);
    }

    private static WorkflowArtifactEvidence ReadWorkflowArtifactEvidence(Wire.WorkflowArtifactEvidence.Reader wire)
    {
        return new(
            Required(wire.DecodeName()),
            Required(wire.DecodePath()),
            Required(wire.DecodeSha256()),
            Required(wire.DecodeRole()),
            wire.DecodeFrom(),
            wire.DecodeSnapshotPath());
    }

    private static void Write(Wire.WorkflowProfileEvidence.Builder wire, WorkflowProfileEvidence value)
    {
        wire.SetCaptureId(value.CaptureId);
        wire.SetBackend(value.Backend);
        wire.SetMode(value.Mode);
        wire.SetStatus(value.Status);
        wire.SetExitCode(value.ExitCode);
        Write(wire.InitStartedAt(), value.StartedAt);
        Write(wire.InitFinishedAt(), value.FinishedAt);
        wire.SetExecutable(value.Executable);
        wire.SetExecutableSha256(value.ExecutableSha256);
        wire.SetRawTrace(value.RawTrace);
        wire.SetRawTraceSha256(value.RawTraceSha256);
        wire.SetCounterConfiguration(value.CounterConfiguration);
        wire.SetCounterConfigurationSha256(value.CounterConfigurationSha256);
        if (value.TargetProcessId is { } targetProcessId)
        {
            wire.InitTargetProcessId().SetValue(targetProcessId);
        }
        if (value.TargetStartedAt is { } targetStartedAt)
        {
            Write(wire.InitTargetStartedAt(), targetStartedAt);
        }
        if (value.TargetFinishedAt is { } targetFinishedAt)
        {
            Write(wire.InitTargetFinishedAt(), targetFinishedAt);
        }
        wire.SetEventSource(value.EventSource);
        wire.SetLaunchModel(value.LaunchModel);
        wire.SetStartGuarantee(value.StartGuarantee);
        wire.SetAttribution(value.Attribution);
        Wire.WorkflowProfileEvidence.SupportedModesListBuilder supportedModesWire = wire.InitSupportedModes(value.SupportedModes.Count);
        for (int i = 0; i < value.SupportedModes.Count; i++)
        {
            supportedModesWire.Set(i, value.SupportedModes[i]);
        }
        Wire.WorkflowProfileEvidence.RawFormatsListBuilder rawFormatsWire = wire.InitRawFormats(value.RawFormats.Count);
        for (int i = 0; i < value.RawFormats.Count; i++)
        {
            rawFormatsWire.Set(i, value.RawFormats[i]);
        }
        wire.SetCpuSet(value.CpuSet);
        wire.SetCpuAffinityLauncher(value.CpuAffinityLauncher);
        wire.SetMemoryStallEvent(value.MemoryStallEvent);
        wire.SetPmcEvents(value.PmcEvents);
        wire.SetPmcInterval(value.PmcInterval);
        wire.SetTimeoutTicks(value.TimeoutTicks);
    }

    private static WorkflowProfileEvidence ReadWorkflowProfileEvidence(Wire.WorkflowProfileEvidence.Reader wire)
    {
        Wire.WorkflowProfileEvidence.SupportedModesListReader supportedModesWire = wire.SupportedModes;
        List<string> supportedModes = new(supportedModesWire.Count);
        for (int i = 0; i < supportedModesWire.Count; i++)
        {
            supportedModes.Add(Required(supportedModesWire.Decode(i)));
        }

        Wire.WorkflowProfileEvidence.RawFormatsListReader rawFormatsWire = wire.RawFormats;
        List<string> rawFormats = new(rawFormatsWire.Count);
        for (int i = 0; i < rawFormatsWire.Count; i++)
        {
            rawFormats.Add(Required(rawFormatsWire.Decode(i)));
        }

        return new(
            Required(wire.DecodeCaptureId()),
            Required(wire.DecodeBackend()),
            Required(wire.DecodeMode()),
            Required(wire.DecodeStatus()),
            wire.ExitCode,
            wire.HasStartedAt ? ReadTimestamp(wire.StartedAt) : throw new InvalidDataException("Missing required report timestamp StartedAt."),
            wire.HasFinishedAt ? ReadTimestamp(wire.FinishedAt) : throw new InvalidDataException("Missing required report timestamp FinishedAt."),
            Required(wire.DecodeExecutable()),
            Required(wire.DecodeExecutableSha256()),
            Required(wire.DecodeRawTrace()),
            Required(wire.DecodeRawTraceSha256()),
            wire.DecodeCounterConfiguration(),
            wire.DecodeCounterConfigurationSha256(),
            wire.HasTargetProcessId ? wire.TargetProcessId.Value : null,
            wire.HasTargetStartedAt ? ReadTimestamp(wire.TargetStartedAt) : null,
            wire.HasTargetFinishedAt ? ReadTimestamp(wire.TargetFinishedAt) : null,
            Required(wire.DecodeEventSource()),
            Required(wire.DecodeLaunchModel()),
            Required(wire.DecodeStartGuarantee()),
            Required(wire.DecodeAttribution()),
            supportedModes,
            rawFormats,
            wire.DecodeCpuSet(),
            wire.DecodeCpuAffinityLauncher(),
            wire.DecodeMemoryStallEvent(),
            wire.DecodePmcEvents(),
            wire.PmcInterval,
            wire.TimeoutTicks);
    }

    private static void Write(Wire.WorkflowFinding.Builder wire, WorkflowFinding value)
    {
        wire.SetCode(value.Code);
        wire.SetSeverity(value.Severity);
        wire.SetNodeId(value.NodeId);
        wire.SetMessage(value.Message);
        Wire.WorkflowFinding.SuggestedProfilesListBuilder suggestedProfilesWire = wire.InitSuggestedProfiles(value.SuggestedProfiles.Length);
        for (int i = 0; i < value.SuggestedProfiles.Length; i++)
        {
            suggestedProfilesWire.Set(i, value.SuggestedProfiles[i]);
        }
    }

    private static WorkflowFinding ReadWorkflowFinding(Wire.WorkflowFinding.Reader wire)
    {
        Wire.WorkflowFinding.SuggestedProfilesListReader suggestedProfilesWire = wire.SuggestedProfiles;
        string[] suggestedProfiles = new string[suggestedProfilesWire.Count];
        for (int i = 0; i < suggestedProfilesWire.Count; i++)
        {
            suggestedProfiles[i] = Required(suggestedProfilesWire.Decode(i));
        }

        return new(
            Required(wire.DecodeCode()),
            Required(wire.DecodeSeverity()),
            Required(wire.DecodeNodeId()),
            Required(wire.DecodeMessage()),
            suggestedProfiles);
    }

    private static void Write(Wire.WorkflowReconciliation.Builder wire, WorkflowReconciliation value)
    {
        wire.SetId(value.Id);
        Write(wire.InitTimestamp(), value.Timestamp);
        wire.SetActor(value.Actor);
        wire.SetHost(value.Host);
        wire.SetNodeId(value.NodeId);
        wire.SetDecision(value.Decision);
        wire.SetReason(value.Reason);
        wire.SetCheckpointSha256(value.CheckpointSha256);
        wire.SetPreviousRunStatus(value.PreviousRunStatus);
        wire.SetPreviousRecoveryPhase(value.PreviousRecoveryPhase);
        if (value.PreviousNode is { } previousNode)
        {
            Write(wire.InitPreviousNode(), previousNode);
        }
        wire.SetProcessesConfirmedStopped(value.ProcessesConfirmedStopped);
    }

    private static WorkflowReconciliation ReadWorkflowReconciliation(Wire.WorkflowReconciliation.Reader wire)
    {
        return new(
            Required(wire.DecodeId()),
            wire.HasTimestamp ? ReadTimestamp(wire.Timestamp) : throw new InvalidDataException("Missing required report timestamp Timestamp."),
            Required(wire.DecodeActor()),
            Required(wire.DecodeHost()),
            wire.DecodeNodeId(),
            Required(wire.DecodeDecision()),
            Required(wire.DecodeReason()),
            Required(wire.DecodeCheckpointSha256()),
            Required(wire.DecodePreviousRunStatus()),
            wire.DecodePreviousRecoveryPhase(),
            wire.HasPreviousNode ? ReadNodeResult(wire.PreviousNode) : null,
            wire.ProcessesConfirmedStopped);
    }

    private static void Write(Wire.HostEnvironment.Builder wire, HostEnvironment value)
    {
        wire.SetHostId(value.HostId);
        wire.SetCpuModel(value.CpuModel);
        wire.SetArchitecture(value.Architecture);
        wire.SetLogicalProcessors(value.LogicalProcessors);
        wire.SetAvailableProcessors(value.AvailableProcessors);
        if (value.PhysicalPackages is { } physicalPackages)
        {
            wire.InitPhysicalPackages().SetValue(physicalPackages);
        }
        if (value.PhysicalCores is { } physicalCores)
        {
            wire.InitPhysicalCores().SetValue(physicalCores);
        }
        wire.SetOperatingSystem(value.OperatingSystem);
        wire.SetKernelVersion(value.KernelVersion);
        wire.SetAffinity(value.Affinity);
        wire.SetGovernor(value.Governor);
        wire.SetFingerprint(value.Fingerprint);
    }

    private static HostEnvironment ReadHostEnvironment(Wire.HostEnvironment.Reader wire)
    {
        return new(
            wire.DecodeHostId(),
            wire.DecodeCpuModel(),
            Required(wire.DecodeArchitecture()),
            wire.LogicalProcessors,
            wire.AvailableProcessors,
            wire.HasPhysicalPackages ? wire.PhysicalPackages.Value : null,
            wire.HasPhysicalCores ? wire.PhysicalCores.Value : null,
            Required(wire.DecodeOperatingSystem()),
            Required(wire.DecodeKernelVersion()),
            wire.DecodeAffinity(),
            wire.DecodeGovernor(),
            Required(wire.DecodeFingerprint()));
    }

    private static void Write(Wire.BuildEnvironment.Builder wire, BuildEnvironment value)
    {
        wire.SetDirectory(value.Directory);
        wire.SetConfiguration(value.Configuration);
        wire.SetGenerator(value.Generator);
        wire.SetCompiler(value.Compiler);
        wire.SetCompilerId(value.CompilerId);
        wire.SetCompilerVersion(value.CompilerVersion);
        wire.SetFlags(value.Flags);
        wire.SetFingerprint(value.Fingerprint);
    }

    private static BuildEnvironment ReadBuildEnvironment(Wire.BuildEnvironment.Reader wire)
    {
        return new(
            Required(wire.DecodeDirectory()),
            wire.DecodeConfiguration(),
            wire.DecodeGenerator(),
            wire.DecodeCompiler(),
            wire.DecodeCompilerId(),
            wire.DecodeCompilerVersion(),
            wire.DecodeFlags(),
            Required(wire.DecodeFingerprint()));
    }

    private static void Write(Wire.WorkflowToolchainIdentity.Builder wire, WorkflowToolchainIdentity value)
    {
        wire.SetBuildDirectory(value.BuildDirectory);
        wire.SetCMake(value.CMake);
        wire.SetNinja(value.Ninja);
        wire.SetCompiler(value.Compiler);
        wire.SetSdk(value.Sdk);
        wire.SetEnvironmentProfileId(value.EnvironmentProfileId);
        wire.SetGenerator(value.Generator);
        wire.SetFingerprint(value.Fingerprint);
        if (value.Linux is { } linux)
        {
            Write(wire.InitLinux(), linux);
        }
    }

    private static WorkflowToolchainIdentity ReadWorkflowToolchainIdentity(Wire.WorkflowToolchainIdentity.Reader wire)
    {
        return new(
            Required(wire.DecodeBuildDirectory()),
            Required(wire.DecodeCMake()),
            wire.DecodeNinja(),
            Required(wire.DecodeCompiler()),
            wire.DecodeSdk(),
            wire.DecodeEnvironmentProfileId(),
            Required(wire.DecodeGenerator()),
            Required(wire.DecodeFingerprint()))
        {
            Linux = wire.HasLinux ? ReadLinuxToolchainIdentity(wire.Linux) : null,
        };
    }

    private static void Write(Wire.LinuxToolchainIdentity.Builder wire, LinuxToolchainIdentity value)
    {
        Write(wire.InitCMake(), value.CMake);
        Write(wire.InitCTest(), value.CTest);
        Write(wire.InitNinja(), value.Ninja);
        wire.SetCCompiler(value.CCompiler);
        wire.SetCCompilerSha256(value.CCompilerSha256);
        wire.SetCxxCompilerSha256(value.CxxCompilerSha256);
        wire.SetTargetTriple(value.TargetTriple);
        wire.SetCompilerVersion(value.CompilerVersion);
        wire.SetCompilerArtifactSha256(value.CompilerArtifactSha256);
        if (value.Linker is { } linker)
        {
            Write(wire.InitLinker(), linker);
        }
        wire.SetSdkArtifactSha256(value.SdkArtifactSha256);
        wire.SetRuntime(value.Runtime);
        wire.SetEnvironmentLockSha256(value.EnvironmentLockSha256);
        wire.SetVcpkgProjectSha256(value.VcpkgProjectSha256);
        wire.SetVcpkgPackageTreeSha256(value.VcpkgPackageTreeSha256);
        wire.SetVcpkgDownloadTreeSha256(value.VcpkgDownloadTreeSha256);
    }

    private static LinuxToolchainIdentity ReadLinuxToolchainIdentity(Wire.LinuxToolchainIdentity.Reader wire)
    {
        return new(
            wire.HasCMake ? ReadWorkflowToolSelection(wire.CMake) : throw new InvalidDataException("Missing required report field CMake."),
            wire.HasCTest ? ReadWorkflowToolSelection(wire.CTest) : throw new InvalidDataException("Missing required report field CTest."),
            wire.HasNinja ? ReadWorkflowToolSelection(wire.Ninja) : throw new InvalidDataException("Missing required report field Ninja."),
            Required(wire.DecodeCCompiler()),
            Required(wire.DecodeCCompilerSha256()),
            Required(wire.DecodeCxxCompilerSha256()),
            Required(wire.DecodeTargetTriple()))
        {
            CompilerVersion = wire.DecodeCompilerVersion(),
            CompilerArtifactSha256 = wire.DecodeCompilerArtifactSha256(),
            Linker = wire.HasLinker ? ReadWorkflowToolSelection(wire.Linker) : null,
            SdkArtifactSha256 = wire.DecodeSdkArtifactSha256(),
            Runtime = wire.DecodeRuntime(),
            EnvironmentLockSha256 = wire.DecodeEnvironmentLockSha256(),
            VcpkgProjectSha256 = wire.DecodeVcpkgProjectSha256(),
            VcpkgPackageTreeSha256 = wire.DecodeVcpkgPackageTreeSha256(),
            VcpkgDownloadTreeSha256 = wire.DecodeVcpkgDownloadTreeSha256(),
        };
    }

    private static void Write(Wire.WorkflowToolSelection.Builder wire, WorkflowToolSelection value)
    {
        wire.SetOrigin(value.Origin);
        wire.SetVersion(value.Version);
        wire.SetArtifactSha256(value.ArtifactSha256);
        wire.SetBinarySha256(value.BinarySha256);
    }

    private static WorkflowToolSelection ReadWorkflowToolSelection(Wire.WorkflowToolSelection.Reader wire)
    {
        return new(
            Required(wire.DecodeOrigin()),
            wire.DecodeVersion(),
            wire.DecodeArtifactSha256(),
            Required(wire.DecodeBinarySha256()));
    }
}
