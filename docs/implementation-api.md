# Implementation assembly API inventory

This appendix records public CLR declarations in the eight production implementation assemblies. These are not the
external .NET SDK package boundary. Use [External .NET API](public-api.md) for the Client/Contracts inventory and the
support boundary, including the distinction between SDK, CLI and Host wire contracts.

Public visibility here supports composition between repository assemblies. In particular, server sessions, stores,
serializers, runner hooks and backend contracts do not become third-party embedding/plugin contracts through this
inventory. See [architecture](en/architecture.md) for their responsibilities.

Serializer contexts below show only source declarations; generated metadata accessors are omitted.

Signatures retain parameter defaults, nullable annotations, generic constraints and base types. Referenced types are
namespace-qualified. Public and externally accessible protected members are included; internal/private types, test
projects, inherited members, implicit default constructors, record-synthesized equality/deconstruction members and
source-generator output are omitted. Primary constructors are shown on type declarations. Positional record properties
are represented by their constructor parameters. Attributes, method bodies and non-constant initializers remain in the
linked source.

Type descriptions come from the existing XML documentation. Member-level contracts, including cancellation,
acceptance, ownership and recovery, remain in the linked source. Type and member identities were verified against the
Release assembly metadata.

## Assemblies

| Assembly | Publicly accessible source types | Member entries |
| --- | ---: | ---: |
| [CMakeWorkflow.Core](#cmakeworkflowcore) | 5 | 4 |
| [CMakeWorkflow.CMake](#cmakeworkflowcmake) | 58 | 133 |
| [CMakeWorkflow.Host](#cmakeworkflowhost) | 98 | 153 |
| [CMakeWorkflow.Profiling](#cmakeworkflowprofiling) | 28 | 38 |
| [CMakeWorkflow.History](#cmakeworkflowhistory) | 17 | 11 |
| [CMakeWorkflow.Tooling](#cmakeworkflowtooling) | 47 | 61 |
| [CMakeWorkflow.Campaigns](#cmakeworkflowcampaigns) | 14 | 45 |
| [CMakeWorkflow](#cmakeworkflow) | 0 | 0 |
| **Total** | **267** | **445** |

## CMakeWorkflow.Core

### Namespace `CMakeWorkflow.Core`

#### `CommandExecution`

Source: [CMakeWorkflow.Core/CommandExecution.cs](../CMakeWorkflow.Core/CommandExecution.cs#L8)

```csharp
public static class CommandExecution
{
    public static System.Threading.Tasks.Task<CMakeWorkflow.Core.CommandResult> RunAsync(string executable, System.Collections.Generic.IEnumerable<string> arguments, string workingDirectory, System.TimeSpan timeout, System.Collections.Generic.IReadOnlyDictionary<string, string?>? environment, System.Threading.CancellationToken cancellationToken);
    public static System.Threading.Tasks.Task<string> Sha256Async(string path, System.Threading.CancellationToken ct);
}
```

#### `CommandResult`

Source: [CMakeWorkflow.Core/CommandResult.cs](../CMakeWorkflow.Core/CommandResult.cs#L5)

```csharp
public sealed record CommandResult(int ExitCode, string Output, bool TimedOut, int? ProcessId = null, string? Stdout = null, string? Stderr = null);
```

#### `IWorkflowOperationExecutor`

Extension boundary for node kinds handled outside the built-in CMake executor.

The runner verifies the built executable before invocation and owns DAG admission/checkpoint persistence. The extension
owns capture/process resources and must finish cleanup before its returned task completes. Do not retain the request or
borrowed execution resources for work after completion.

Source: [CMakeWorkflow.Core/IWorkflowOperationExecutor.cs](../CMakeWorkflow.Core/IWorkflowOperationExecutor.cs#L9)

```csharp
public interface IWorkflowOperationExecutor
{
    bool Supports(string kind);
    System.Threading.Tasks.Task<CMakeWorkflow.Core.WorkflowOperationResult> ExecuteAsync(CMakeWorkflow.Core.WorkflowOperationRequest request, System.Threading.CancellationToken cancellationToken);
}
```

#### `WorkflowOperationRequest`

Admitted extension-node inputs with Host-local paths and verified executable identity.

Read-only collection interfaces are borrowed inputs, not permission to mutate underlying values. The runner owns
checkpoint/admission; the backend owns capture and child cleanup.

Source: [CMakeWorkflow.Core/WorkflowOperationRequest.cs](../CMakeWorkflow.Core/WorkflowOperationRequest.cs#L25)

```csharp
public sealed record WorkflowOperationRequest(string Kind, string Executable, System.Collections.Generic.IReadOnlyList<string> Arguments, string WorkingDirectory, System.Collections.Generic.IReadOnlyDictionary<string, string> Environment, System.Collections.Generic.IReadOnlyDictionary<string, string> Options, string OutputDirectory, string NodeId, string? CpuSet = null, CMakeWorkflow.Contracts.Workflows.WorkflowProfilerRequirement? RequiredProfiler = null, string? CpuAffinityLauncher = null);
```

#### `WorkflowOperationResult`

Extension outcome awaiting runner integration and durable persistence.

Source: [CMakeWorkflow.Core/WorkflowOperationResult.cs](../CMakeWorkflow.Core/WorkflowOperationResult.cs#L10)

```csharp
public sealed record WorkflowOperationResult(int ExitCode, string Status, System.Collections.Generic.IReadOnlyDictionary<string, string> Outputs, string? Message = null);
```

## CMakeWorkflow.CMake

### Namespace `CMakeWorkflow.CMake.Artifacts`

#### `ArtifactReference`

Source: [CMakeWorkflow.CMake/Artifacts/ArtifactReference.cs](../CMakeWorkflow.CMake/Artifacts/ArtifactReference.cs#L5)

```csharp
public sealed record ArtifactReference(string BuildNode, CMakeWorkflow.CMake.Build.CMakeTargetRef Target, string Executable, string Sha256, string Materialization);
```

#### `WorkflowArtifactEvidence`

Source: [CMakeWorkflow.CMake/Artifacts/WorkflowArtifactEvidence.cs](../CMakeWorkflow.CMake/Artifacts/WorkflowArtifactEvidence.cs#L3)

```csharp
public sealed record WorkflowArtifactEvidence(string Name, string Path, string Sha256, string Role, string? From = null, string? SnapshotPath = null);
```

#### `WorkflowArtifactView`

Source: [CMakeWorkflow.CMake/Artifacts/WorkflowArtifactView.cs](../CMakeWorkflow.CMake/Artifacts/WorkflowArtifactView.cs#L3)

```csharp
public sealed record WorkflowArtifactView(string Type, string Path, string? Sha256);
```

### Namespace `CMakeWorkflow.CMake.Build`

#### `BuildContextSession`

Source: [CMakeWorkflow.CMake/Build/BuildContextSession.cs](../CMakeWorkflow.CMake/Build/BuildContextSession.cs#L8)

```csharp
public sealed class BuildContextSession
{
    public CMakeWorkflow.CMake.Build.BuildContextSessionState Observe(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed, string phase);
    public CMakeWorkflow.CMake.Build.BuildContextSessionState State(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed);
    public void Validate(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed);
    public void BeginConfigure();
    public void Configured(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed);
    public static CMakeWorkflow.CMake.Build.RequestedBuildContext ResolutionRequest(CMakeWorkflow.CMake.Build.RequestedBuildContext request);
    public static void ValidateAdmission(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed);
}
```

#### `BuildContextSessionState`

Source: [CMakeWorkflow.CMake/Build/BuildContextSessionState.cs](../CMakeWorkflow.CMake/Build/BuildContextSessionState.cs#L3)

```csharp
public sealed record BuildContextSessionState(string Mode, string Policy, string Provenance, CMakeWorkflow.CMake.Build.GenerationObservation Generation, string GenerationChange, bool ObservedPropertiesChanged, bool RequiresConfigure);
```

#### `BuildTreeOwnership`

Source: [CMakeWorkflow.CMake/Build/BuildTreeOwnership.cs](../CMakeWorkflow.CMake/Build/BuildTreeOwnership.cs#L10)

```csharp
public sealed class BuildTreeOwnership : System.IDisposable
{
    public static string CanonicalPath(string path);
    public static void RejectController(string tree, string? controller);
    public static void PropagateTo(System.Diagnostics.ProcessStartInfo info);
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.BuildTreeOwnership> AcquireAsync(System.Collections.Generic.IEnumerable<string> trees, System.Threading.CancellationToken ct);
    public void Dispose();
}
```

#### `BuiltArtifact`

Source: [CMakeWorkflow.CMake/Build/BuiltArtifact.cs](../CMakeWorkflow.CMake/Build/BuiltArtifact.cs#L3)

```csharp
public sealed record BuiltArtifact(CMakeWorkflow.CMake.Build.CMakeTargetRef TargetRef, string ExecutablePath);
```

#### `CMakeBuildCommand`

Source: [CMakeWorkflow.CMake/Build/CMakeBuildCommand.cs](../CMakeWorkflow.CMake/Build/CMakeBuildCommand.cs#L5)

```csharp
public sealed record CMakeBuildCommand(System.Collections.Generic.IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public static CMakeWorkflow.CMake.Build.CMakeBuildCommand Create(CMakeWorkflow.CMake.Build.CMakeTargetRef target);
}
```

#### `CMakeConfiguration`

Source: [CMakeWorkflow.CMake/Build/CMakeConfiguration.cs](../CMakeWorkflow.CMake/Build/CMakeConfiguration.cs#L4)

```csharp
public static class CMakeConfiguration
{
    public static CMakeWorkflow.CMake.Build.CMakeTargetRef Resolve(CMakeWorkflow.CMake.Build.CMakeTargetRef target);
}
```

#### `CMakeFileApi`

Source: [CMakeWorkflow.CMake/Build/CMakeFileApi.cs](../CMakeWorkflow.CMake/Build/CMakeFileApi.cs#L3)

```csharp
public static class CMakeFileApi
{
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.BuiltArtifact> ResolveExecutableAsync(CMakeWorkflow.CMake.Build.CMakeTargetRef target, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
}
```

#### `CMakeInspection`

Source: [CMakeWorkflow.CMake/Build/CMakeInspection.cs](../CMakeWorkflow.CMake/Build/CMakeInspection.cs#L7)

```csharp
public static class CMakeInspection
{
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.ObservedBuildContext> InspectAsync(string buildDirectory, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
    public static void ValidateIdentity(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed);
    public static CMakeWorkflow.CMake.Build.CMakeTargetRef Resolve(CMakeWorkflow.CMake.Build.RequestedBuildContext request, CMakeWorkflow.CMake.Build.ObservedBuildContext observed, string target = "");
    public static CMakeWorkflow.CMake.Build.NativeTargetObservation RequireTarget(CMakeWorkflow.CMake.Build.ObservedBuildContext observed, CMakeWorkflow.CMake.Build.CMakeTargetRef target, bool executableOnly = false);
}
```

#### `CMakeTargetRef`

Source: [CMakeWorkflow.CMake/Build/CMakeTargetRef.cs](../CMakeWorkflow.CMake/Build/CMakeTargetRef.cs#L3)

```csharp
public sealed record CMakeTargetRef(string BuildDirectory, string Target, string? Configuration);
```

#### `ConfigureProvenance`

Source: [CMakeWorkflow.CMake/Build/ConfigureProvenance.cs](../CMakeWorkflow.CMake/Build/ConfigureProvenance.cs#L3)

```csharp
public sealed class ConfigureProvenance
{
    public string State { get; set; }
    public System.DateTimeOffset At { get; set; }
    public string BuildDirectory { get; set; }
    public string SourceDirectory { get; set; }
    public string? Preset { get; set; }
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? WorkingDirectory { get; set; }
    public System.Collections.Generic.List<string> Arguments { get; set; }
}
```

#### `ExternalCMakeBuildContext`

Source: [CMakeWorkflow.CMake/Build/ExternalCMakeBuildContext.cs](../CMakeWorkflow.CMake/Build/ExternalCMakeBuildContext.cs#L4)

```csharp
public sealed class ExternalCMakeBuildContext : CMakeWorkflow.CMake.Build.ICMakeBuildContext
{
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.BuiltArtifact> EnsureBuiltAsync(CMakeWorkflow.CMake.Build.CMakeTargetRef target, System.Threading.CancellationToken cancellationToken);
}
```

#### `GenerationObservation`

Source: [CMakeWorkflow.CMake/Build/GenerationObservation.cs](../CMakeWorkflow.CMake/Build/GenerationObservation.cs#L4)

```csharp
public sealed record GenerationObservation(string? IndexFile, string? IndexHash)
{
    public static CMakeWorkflow.CMake.Build.GenerationObservation From(CMakeWorkflow.CMake.Build.ObservedBuildContext context);
    public bool Known { get; }
}
```

#### `ICMakeBuildContext`

Source: [CMakeWorkflow.CMake/Build/ICMakeBuildContext.cs](../CMakeWorkflow.CMake/Build/ICMakeBuildContext.cs#L4)

```csharp
public interface ICMakeBuildContext
{
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.BuiltArtifact> EnsureBuiltAsync(CMakeWorkflow.CMake.Build.CMakeTargetRef target, System.Threading.CancellationToken cancellationToken);
}
```

#### `NativeBuildResult`

Source: [CMakeWorkflow.CMake/Build/NativeBuildResult.cs](../CMakeWorkflow.CMake/Build/NativeBuildResult.cs#L3)

```csharp
public sealed record NativeBuildResult(CMakeWorkflow.CMake.Build.CMakeTargetRef Target, string Verification, int? BackendExitCode, string? ObservationIndex, CMakeWorkflow.CMake.Build.GenerationObservation? Generation = null);
```

#### `NativeTargetObservation`

Source: [CMakeWorkflow.CMake/Build/NativeTargetObservation.cs](../CMakeWorkflow.CMake/Build/NativeTargetObservation.cs#L3)

```csharp
public sealed record NativeTargetObservation(string Name, string Configuration, string Type, string? Executable, string Evidence);
```

#### `ObservedBuildContext`

Source: [CMakeWorkflow.CMake/Build/ObservedBuildContext.cs](../CMakeWorkflow.CMake/Build/ObservedBuildContext.cs#L3)

```csharp
public sealed class ObservedBuildContext
{
    public string BuildDirectory { get; set; }
    public string State { get; set; }
    public string Freshness { get; set; }
    public string PresetState { get; set; }
    public string? SourceDirectory { get; set; }
    public string? Generator { get; set; }
    public bool? MultiConfig { get; set; }
    public System.Collections.Generic.List<string> Configurations { get; set; }
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public string? CMakeExecutable { get; set; }
    public string? CTestExecutable { get; set; }
    public bool HasCodeModel { get; set; }
    public bool HasCacheReply { get; set; }
    public System.Collections.Generic.List<string> Toolchains { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Build.NativeTargetObservation> Targets { get; set; }
    public System.Collections.Generic.List<string> Diagnostics { get; set; }
    public CMakeWorkflow.CMake.Build.ConfigureProvenance? ManagedConfigure { get; set; }
}
```

#### `PreparedCMakeBuildContext`

Source: [CMakeWorkflow.CMake/Build/PreparedCMakeBuildContext.cs](../CMakeWorkflow.CMake/Build/PreparedCMakeBuildContext.cs#L5)

```csharp
public sealed class PreparedCMakeBuildContext : CMakeWorkflow.CMake.Build.ICMakeBuildContext
{
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.BuiltArtifact> EnsureBuiltAsync(CMakeWorkflow.CMake.Build.CMakeTargetRef target, System.Threading.CancellationToken cancellationToken);
}
```

#### `RequestedBuildContext`

Source: [CMakeWorkflow.CMake/Build/RequestedBuildContext.cs](../CMakeWorkflow.CMake/Build/RequestedBuildContext.cs#L3)

```csharp
public sealed record RequestedBuildContext(string BuildDirectory, string? Configuration = null, string? ConfigurePreset = null, string? SourceDirectory = null, string ProvenancePolicy = "strict", string Mode = "legacy", string? Generator = null);
```

#### `TargetResolutionView`

Source: [CMakeWorkflow.CMake/Build/TargetResolutionView.cs](../CMakeWorkflow.CMake/Build/TargetResolutionView.cs#L3)

```csharp
public sealed record TargetResolutionView(int SchemaVersion, string BuildDirectory, string Target, string? Configuration, string Executable);
```

### Namespace `CMakeWorkflow.CMake.Execution`

#### `ProcessRunner`

Source: [CMakeWorkflow.CMake/Execution/ProcessRunner.cs](../CMakeWorkflow.CMake/Execution/ProcessRunner.cs#L8)

```csharp
public static class ProcessRunner
{
    public static System.Threading.Tasks.Task RunAsync(string file, System.Collections.Generic.IReadOnlyList<string> args, string workingDirectory, System.Threading.CancellationToken ct);
}
```

#### `WorkflowCommandDispatcher`

Journal-backed adapter for an ordered series of external commands. This is not a second DAG planner: the caller owns its
domain-specific step order; the regular WorkflowProcessDispatcher owns all child-process execution/cleanup.

Source: [CMakeWorkflow.CMake/Execution/WorkflowCommandDispatcher.cs](../CMakeWorkflow.CMake/Execution/WorkflowCommandDispatcher.cs#L14)

```csharp
public sealed class WorkflowCommandDispatcher
{
    public WorkflowCommandDispatcher(string output, System.Text.Json.Nodes.JsonArray commands, bool resume, System.Collections.Generic.IReadOnlyDictionary<string, string> environment, System.Action<CMakeWorkflow.CMake.Execution.WorkflowDispatchEvent>? observer = null);
    public System.Threading.Tasks.Task<string> RunAsync(string label, string executable, System.Collections.Generic.IEnumerable<string> arguments, string? declaredOutput, int timeoutSeconds, System.Threading.CancellationToken ct);
}
```

#### `WorkflowDispatchEvent`

Source: [CMakeWorkflow.CMake/Execution/WorkflowDispatchEvent.cs](../CMakeWorkflow.CMake/Execution/WorkflowDispatchEvent.cs#L3)

```csharp
public sealed record WorkflowDispatchEvent(string Type, string Label, System.TimeSpan Elapsed);
```

#### `WorkflowProcessDispatcher`

Source: [CMakeWorkflow.CMake/Execution/WorkflowProcessDispatcher.cs](../CMakeWorkflow.CMake/Execution/WorkflowProcessDispatcher.cs#L7)

```csharp
public static class WorkflowProcessDispatcher
{
    public static System.Threading.Tasks.Task<int> RunAsync(CMakeWorkflow.CMake.Reporting.NodeResult result, System.Collections.Generic.IReadOnlyDictionary<string, string> environment, string output, System.Threading.CancellationToken ct, System.Action<System.TimeSpan>? heartbeat = null);
}
```

#### `WorkflowRunner`

Owns a CMake run lease, DAG scheduling and durable run checkpoints.

The Host/CLI caller owns outer operation admission and finalization; this runner owns run-file persistence and domain
execution resources. Events are audit/progress evidence, not recovery authority. Interrupted running intents are
quarantined as outcome_unknown rather than automatically re-executed.

Source: [CMakeWorkflow.CMake/Execution/WorkflowRunner.cs](../CMakeWorkflow.CMake/Execution/WorkflowRunner.cs#L19)

```csharp
public static class WorkflowRunner
{
    public static System.Threading.Tasks.Task<int> RunAsync(CMakeWorkflow.CMake.Planning.WorkflowPlan plan, int jobs, string output, System.Threading.CancellationToken ct, CMakeWorkflow.Core.IWorkflowOperationExecutor? extension = null, bool eventsToStdout = false, bool resume = false, System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task>? beforeExternalDispatch = null, System.Collections.Generic.IReadOnlyDictionary<string, CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime>? toolchains = null);
}
```

### Namespace `CMakeWorkflow.CMake.Planning`

#### `PlanDiagnostic`

Source: [CMakeWorkflow.CMake/Planning/PlanDiagnostic.cs](../CMakeWorkflow.CMake/Planning/PlanDiagnostic.cs#L3)

```csharp
public sealed record PlanDiagnostic(string Severity, string Message);
```

#### `PlannedNode`

Source: [CMakeWorkflow.CMake/Planning/PlannedNode.cs](../CMakeWorkflow.CMake/Planning/PlannedNode.cs#L6)

```csharp
public sealed record PlannedNode(CMakeWorkflow.Contracts.Workflows.WorkflowNode Definition, CMakeWorkflow.CMake.Build.CMakeTargetRef Target, string WorkingDirectory, System.Collections.Generic.IReadOnlyList<string> Dependencies, CMakeWorkflow.CMake.Build.RequestedBuildContext Requested, CMakeWorkflow.CMake.Build.ObservedBuildContext Observed, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.CMake.Planning.PlanDiagnostic> Diagnostics, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain = null);
```

#### `WorkflowPlan`

Source: [CMakeWorkflow.CMake/Planning/WorkflowPlan.cs](../CMakeWorkflow.CMake/Planning/WorkflowPlan.cs#L3)

```csharp
public sealed record WorkflowPlan(string File, string ManifestHash, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.CMake.Planning.PlannedNode> Nodes);
```

#### `WorkflowPlanNodeView`

Source: [CMakeWorkflow.CMake/Planning/WorkflowPlanNodeView.cs](../CMakeWorkflow.CMake/Planning/WorkflowPlanNodeView.cs#L5)

```csharp
public sealed record WorkflowPlanNodeView(string Id, string Kind, string Target, string BuildDirectory, string? Configuration, System.Collections.Generic.List<string> Dependencies, string ObservedState, System.Collections.Generic.List<CMakeWorkflow.CMake.Planning.PlanDiagnostic> Diagnostics, System.Collections.Generic.List<string> Resources, bool Exclusive, System.Collections.Generic.List<CMakeWorkflow.Contracts.Workflows.WorkflowArtifactInput> Inputs, System.Collections.Generic.List<CMakeWorkflow.Contracts.Workflows.WorkflowArtifactOutput> Outputs, CMakeWorkflow.Contracts.Workflows.WorkflowExecutionConstraints? Constraints);
```

#### `WorkflowPlanView`

Source: [CMakeWorkflow.CMake/Planning/WorkflowPlanView.cs](../CMakeWorkflow.CMake/Planning/WorkflowPlanView.cs#L3)

```csharp
public sealed record WorkflowPlanView(int SchemaVersion, string Workflow, string ManifestHash, bool Valid, System.Collections.Generic.List<CMakeWorkflow.CMake.Planning.WorkflowPlanNodeView> Nodes);
```

#### `WorkflowPlanner`

Source: [CMakeWorkflow.CMake/Planning/WorkflowPlanner.cs](../CMakeWorkflow.CMake/Planning/WorkflowPlanner.cs#L9)

```csharp
public static class WorkflowPlanner
{
    public static System.StringComparer PathComparer { get; }
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Planning.WorkflowPlan> LoadAsync(string file, System.Collections.Generic.IReadOnlyList<string> only, string? controllerDirectory, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Planning.WorkflowPlan> LoadSnapshotAsync(string originalManifestPath, byte[] snapshot, System.Collections.Generic.IReadOnlyList<string> only, string? controllerDirectory, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
}
```

### Namespace `CMakeWorkflow.CMake.Recipes`

#### `BenchmarkMatrixWorkflowBuilder`

Validates Cartesian expansion limits and creates builds followed by serialized timing nodes.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.CMake/Recipes/BenchmarkMatrixWorkflowBuilder.cs](../CMakeWorkflow.CMake/Recipes/BenchmarkMatrixWorkflowBuilder.cs#L13)

```csharp
public static class BenchmarkMatrixWorkflowBuilder
{
    public const int MaxNodes = 4096;
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(CMakeWorkflow.Contracts.Recipes.BenchmarkMatrixRecipeV1 recipe);
}
```

#### `BenchmarkWorkflowBuilder`

Validates a benchmark recipe and creates a build followed by serial timing dependencies.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.CMake/Recipes/BenchmarkWorkflowBuilder.cs](../CMakeWorkflow.CMake/Recipes/BenchmarkWorkflowBuilder.cs#L14)

```csharp
public static class BenchmarkWorkflowBuilder
{
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(CMakeWorkflow.Contracts.Recipes.BenchmarkRecipeV1 recipe);
}
```

#### `BuildMatrixWorkflowBuilder`

Validates matrix limits and creates every variant/target build node.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.CMake/Recipes/BuildMatrixWorkflowBuilder.cs](../CMakeWorkflow.CMake/Recipes/BuildMatrixWorkflowBuilder.cs#L13)

```csharp
public static class BuildMatrixWorkflowBuilder
{
    public const int MaxNodes = 512;
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(CMakeWorkflow.Contracts.Recipes.BuildMatrixRecipeV1 recipe);
}
```

#### `TargetWorkflowBuilder`

Source: [CMakeWorkflow.CMake/Recipes/TargetWorkflowBuilder.cs](../CMakeWorkflow.CMake/Recipes/TargetWorkflowBuilder.cs#L6)

```csharp
public static class TargetWorkflowBuilder
{
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(string action, string buildDirectory, string? target, string? secondTarget, string? configuration, System.Collections.Generic.IReadOnlyList<string>? arguments, System.Collections.Generic.IReadOnlyList<string>? secondArguments, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? toolchain = null);
}
```

#### `TestMatrixWorkflowBuilder`

Validates CTest filters and creates one build with parallel or chained test groups.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.CMake/Recipes/TestMatrixWorkflowBuilder.cs](../CMakeWorkflow.CMake/Recipes/TestMatrixWorkflowBuilder.cs#L14)

```csharp
public static class TestMatrixWorkflowBuilder
{
    public const int MaxGroups = 64;
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(CMakeWorkflow.Contracts.Recipes.TestMatrixRecipeV1 recipe);
}
```

#### `TwoTargetWorkflow`

Minimal interleaved-build probe, NOT a general workflow scheduler. A single external context owns both build operations
and preserves their order.

Source: [CMakeWorkflow.CMake/Recipes/TwoTargetWorkflow.cs](../CMakeWorkflow.CMake/Recipes/TwoTargetWorkflow.cs#L10)

```csharp
public static class TwoTargetWorkflow
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.CMake.Recovery`

#### `WorkflowEvent`

Source: [CMakeWorkflow.CMake/Recovery/WorkflowEvent.cs](../CMakeWorkflow.CMake/Recovery/WorkflowEvent.cs#L4)

```csharp
public sealed record WorkflowEvent(int SchemaVersion, long Sequence, System.DateTimeOffset Timestamp, string Type, string RunDirectory, string? NodeId, string? NodeKind, string? Status, int? ExitCode, string? Error, System.Collections.Generic.List<CMakeWorkflow.CMake.Artifacts.WorkflowArtifactView> Artifacts);
```

#### `WorkflowReconciliation`

Source: [CMakeWorkflow.CMake/Recovery/WorkflowReconciliation.cs](../CMakeWorkflow.CMake/Recovery/WorkflowReconciliation.cs#L5)

```csharp
public sealed record WorkflowReconciliation(string Id, System.DateTimeOffset Timestamp, string Actor, string Host, string? NodeId, string Decision, string Reason, string CheckpointSha256, string PreviousRunStatus, string? PreviousRecoveryPhase, CMakeWorkflow.CMake.Reporting.NodeResult? PreviousNode, bool ProcessesConfirmedStopped);
```

### Namespace `CMakeWorkflow.CMake.Reporting`

#### `NodeResult`

Source: [CMakeWorkflow.CMake/Reporting/NodeResult.cs](../CMakeWorkflow.CMake/Reporting/NodeResult.cs#L5)

```csharp
public sealed class NodeResult
{
    public string Id { get; set; }
    public string Kind { get; set; }
    public System.Collections.Generic.List<string> Dependencies { get; set; }
    public string BuildDirectory { get; set; }
    public string? Configuration { get; set; }
    public string Target { get; set; }
    public string Status { get; set; }
    public System.DateTimeOffset? StartedAt { get; set; }
    public System.DateTimeOffset? FinishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public double? ProcessDurationSeconds { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? ExecutableSha256 { get; set; }
    public System.Collections.Generic.List<string> Arguments { get; set; }
    public string? EnvironmentOverrideHash { get; set; }
    public string? ToolchainFingerprint { get; set; }
    public string WorkingDirectory { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? Error { get; set; }
    public CMakeWorkflow.CMake.Build.RequestedBuildContext? RequestedContext { get; set; }
    public CMakeWorkflow.CMake.Build.ObservedBuildContext? ObservedContext { get; set; }
    public CMakeWorkflow.CMake.Build.ObservedBuildContext? ObservationAfter { get; set; }
    public CMakeWorkflow.CMake.Build.NativeBuildResult? BuildVerification { get; set; }
    public CMakeWorkflow.CMake.Artifacts.ArtifactReference? Artifact { get; set; }
    public CMakeWorkflow.CMake.Build.ConfigureProvenance? Configure { get; set; }
    public CMakeWorkflow.CMake.Build.BuildContextSessionState? SessionBefore { get; set; }
    public CMakeWorkflow.CMake.Build.BuildContextSessionState? SessionAfter { get; set; }
    public string ExperimentResult { get; set; }
    public System.Collections.Generic.Dictionary<string, string> Outputs { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Artifacts.WorkflowArtifactEvidence> DeclaredArtifacts { get; set; }
    public System.Collections.Generic.Dictionary<string, string> ExecutionConditions { get; set; }
}
```

#### `WorkflowFinding`

Source: [CMakeWorkflow.CMake/Reporting/WorkflowFinding.cs](../CMakeWorkflow.CMake/Reporting/WorkflowFinding.cs#L3)

```csharp
public sealed record WorkflowFinding(string Code, string Severity, string NodeId, string Message, string[] SuggestedProfiles);
```

#### `WorkflowQueries`

Source: [CMakeWorkflow.CMake/Reporting/WorkflowQueries.cs](../CMakeWorkflow.CMake/Reporting/WorkflowQueries.cs#L10)

```csharp
public static class WorkflowQueries
{
    public static CMakeWorkflow.CMake.Planning.WorkflowPlanView PlanView(CMakeWorkflow.CMake.Planning.WorkflowPlan plan);
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot> ReportAsync(string directory, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
    public static CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot ReportFromBytes(string directory, byte[] bytes);
    public static System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.TargetResolutionView> ResolveAsync(string buildDirectory, string target, string? configuration, System.Threading.CancellationToken ct, System.Action<string>? readGuard = null);
}
```

#### `WorkflowReport`

Source: [CMakeWorkflow.CMake/Reporting/WorkflowReport.cs](../CMakeWorkflow.CMake/Reporting/WorkflowReport.cs#L6)

```csharp
public sealed class WorkflowReport
{
    public int SchemaVersion { get; set; }
    public string Workflow { get; set; }
    public string ManifestHash { get; set; }
    public System.DateTimeOffset StartedAt { get; set; }
    public System.DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Reporting.NodeResult> Nodes { get; set; }
    public string? Error { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Reporting.WorkflowFinding> Findings { get; set; }
    public int ResumeCount { get; set; }
    public string? RecoveryPhase { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Recovery.WorkflowReconciliation> Reconciliations { get; set; }
    public CMakeWorkflow.CMake.Toolchains.HostEnvironment? Environment { get; set; }
    public CMakeWorkflow.CMake.Toolchains.HostEnvironment? EnvironmentAtEnd { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Toolchains.BuildEnvironment> BuildEnvironments { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.CMake.Toolchains.WorkflowToolchainIdentity> Toolchains { get; set; }
}
```

#### `WorkflowReportSnapshot`

Source: [CMakeWorkflow.CMake/Reporting/WorkflowReportSnapshot.cs](../CMakeWorkflow.CMake/Reporting/WorkflowReportSnapshot.cs#L3)

```csharp
public sealed record WorkflowReportSnapshot(int SchemaVersion, string RunDirectory, string Revision, CMakeWorkflow.CMake.Reporting.WorkflowReport Report);
```

### Namespace `CMakeWorkflow.CMake.Serialization`

#### `WorkflowJson`

Source: [CMakeWorkflow.CMake/Serialization/WorkflowJson.cs](../CMakeWorkflow.CMake/Serialization/WorkflowJson.cs#L9)

```csharp
public class WorkflowJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
```

#### `WorkflowManifestBytes`

Source: [CMakeWorkflow.CMake/Serialization/WorkflowManifestBytes.cs](../CMakeWorkflow.CMake/Serialization/WorkflowManifestBytes.cs#L7)

```csharp
public static class WorkflowManifestBytes
{
    public static byte[] Serialize(CMakeWorkflow.Contracts.Workflows.WorkflowDocument document);
    public static System.Threading.Tasks.Task WriteNewAsync(string output, CMakeWorkflow.Contracts.Workflows.WorkflowDocument document, System.Threading.CancellationToken ct);
}
```

#### `WorkflowProtocolJson`

Source: [CMakeWorkflow.CMake/Serialization/WorkflowProtocolJson.cs](../CMakeWorkflow.CMake/Serialization/WorkflowProtocolJson.cs#L8)

```csharp
public class WorkflowProtocolJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
```

### Namespace `CMakeWorkflow.CMake.Toolchains`

#### `BuildEnvironment`

Source: [CMakeWorkflow.CMake/Toolchains/BuildEnvironment.cs](../CMakeWorkflow.CMake/Toolchains/BuildEnvironment.cs#L5)

```csharp
public sealed record BuildEnvironment(string Directory, string? Configuration, string? Generator, string? Compiler, string? CompilerId, string? CompilerVersion, string? Flags, string Fingerprint);
```

#### `HostEnvironment`

Source: [CMakeWorkflow.CMake/Toolchains/HostEnvironment.cs](../CMakeWorkflow.CMake/Toolchains/HostEnvironment.cs#L5)

```csharp
public sealed record HostEnvironment(string? HostId, string? CpuModel, string Architecture, int LogicalProcessors, int AvailableProcessors, int? PhysicalPackages, int? PhysicalCores, string OperatingSystem, string KernelVersion, string? Affinity, string? Governor, string Fingerprint);
```

#### `IWorkflowToolchainProvider`

Source: [CMakeWorkflow.CMake/Toolchains/IWorkflowToolchainProvider.cs](../CMakeWorkflow.CMake/Toolchains/IWorkflowToolchainProvider.cs#L7)

```csharp
public interface IWorkflowToolchainProvider
{
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime> ResolveAsync(CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding binding, System.Threading.CancellationToken ct);
}
```

#### `MissingWorkflowToolException`

Source: [CMakeWorkflow.CMake/Toolchains/MissingWorkflowToolException.cs](../CMakeWorkflow.CMake/Toolchains/MissingWorkflowToolException.cs#L9)

```csharp
public sealed class MissingWorkflowToolException(string executable, System.Exception inner) : System.IO.IOException
{
    public string Executable { get; }
}
```

#### `WorkflowToolchainAdmission`

Source: [CMakeWorkflow.CMake/Toolchains/WorkflowToolchainAdmission.cs](../CMakeWorkflow.CMake/Toolchains/WorkflowToolchainAdmission.cs#L9)

```csharp
public static class WorkflowToolchainAdmission
{
    public static System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyDictionary<string, CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime>> ResolveAsync(CMakeWorkflow.CMake.Planning.WorkflowPlan plan, CMakeWorkflow.CMake.Toolchains.IWorkflowToolchainProvider? provider, System.Threading.CancellationToken ct);
    public static System.Collections.Generic.List<CMakeWorkflow.CMake.Toolchains.WorkflowToolchainIdentity> Identities(System.Collections.Generic.IReadOnlyDictionary<string, CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime> selected);
    public static void RequireSameIdentity(System.Collections.Generic.IReadOnlyList<CMakeWorkflow.CMake.Toolchains.WorkflowToolchainIdentity> recorded, System.Collections.Generic.IReadOnlyDictionary<string, CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime> selected);
    public static void ValidateBuildTree(string buildDirectory, CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime selected);
}
```

#### `WorkflowToolchainIdentity`

Source: [CMakeWorkflow.CMake/Toolchains/WorkflowToolchainIdentity.cs](../CMakeWorkflow.CMake/Toolchains/WorkflowToolchainIdentity.cs#L5)

```csharp
public sealed record WorkflowToolchainIdentity(string BuildDirectory, string CMake, string? Ninja, string Compiler, string? Sdk, string? EnvironmentProfileId, string Generator, string Fingerprint);
```

#### `WorkflowToolchainRuntime`

Source: [CMakeWorkflow.CMake/Toolchains/WorkflowToolchainRuntime.cs](../CMakeWorkflow.CMake/Toolchains/WorkflowToolchainRuntime.cs#L6)

```csharp
public sealed record WorkflowToolchainRuntime(string CMake, string? Ninja, string Compiler, string? Sdk, string? EnvironmentProfileId, string Generator, string Fingerprint, System.Collections.Generic.IReadOnlyDictionary<string, string> Variables);
```

### Namespace `CMakeWorkflow.CMake`

#### `WorkflowCommand`

Source: [CMakeWorkflow.CMake/WorkflowCommand.cs](../CMakeWorkflow.CMake/WorkflowCommand.cs#L10)

```csharp
public static class WorkflowCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string command, string[] args, System.Threading.CancellationToken ct, CMakeWorkflow.Core.IWorkflowOperationExecutor? extension = null, System.Action<string>? onRunDirectory = null, CMakeWorkflow.CMake.Toolchains.IWorkflowToolchainProvider? toolchainProvider = null);
}
```

#### `WorkflowReconcileCommand`

Source: [CMakeWorkflow.CMake/WorkflowReconcileCommand.cs](../CMakeWorkflow.CMake/WorkflowReconcileCommand.cs#L10)

```csharp
public static class WorkflowReconcileCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

## CMakeWorkflow.Host

### Namespace `CMakeWorkflow.Host.Campaigns`

#### `CampaignExecutionResult`

Source: [CMakeWorkflow.Host/Campaigns/CampaignExecutionResult.cs](../CMakeWorkflow.Host/Campaigns/CampaignExecutionResult.cs#L5)

```csharp
public sealed record CampaignExecutionResult(int SchemaVersion, string Kind, string Status, int ExitCode, string? ArtifactDirectory);
```

#### `CampaignFuzzMergeRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignFuzzMergeRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignFuzzMergeRequest.cs#L5)

```csharp
public sealed record CampaignFuzzMergeRequest(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);
```

#### `CampaignFuzzRunRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignFuzzRunRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignFuzzRunRequest.cs#L5)

```csharp
public sealed record CampaignFuzzRunRequest(string BuildDirectory, string Layer, string ResultsDirectory, string? Backend = null, string? CorpusDirectory = null, string? Configuration = null, int DurationSeconds = 60, int InputTimeoutSeconds = 15, int MaxLength = 4096, bool ReplayOnly = false);
```

#### `CampaignFuzzSeedRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignFuzzSeedRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignFuzzSeedRequest.cs#L5)

```csharp
public sealed record CampaignFuzzSeedRequest(string Layer, string OutputDirectory);
```

#### `CampaignFuzzTriageRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignFuzzTriageRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignFuzzTriageRequest.cs#L5)

```csharp
public sealed record CampaignFuzzTriageRequest(string RunDirectory);
```

#### `CampaignMutationRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignMutationRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignMutationRequest.cs#L5)

```csharp
public sealed record CampaignMutationRequest(string SourceDirectory, string? ReportPath = null, string Suite = "all", string Compiler = "c++", string Seed = "496833221358", int Cases = 48, int Steps = 48, int TimeoutSeconds = 30);
```

#### `CampaignWeakMemoryRequest`

Source: [CMakeWorkflow.Host/Campaigns/CampaignWeakMemoryRequest.cs](../CMakeWorkflow.Host/Campaigns/CampaignWeakMemoryRequest.cs#L5)

```csharp
public sealed record CampaignWeakMemoryRequest(string SourceDirectory, string RelacyDirectory, string? ReportPath = null, string Protocol = "all", string Compiler = "c++", int TimeoutSeconds = 60);
```

### Namespace `CMakeWorkflow.Host.History`

#### `HistoryCompareRequest`

Source: [CMakeWorkflow.Host/History/HistoryCompareRequest.cs](../CMakeWorkflow.Host/History/HistoryCompareRequest.cs#L5)

```csharp
public sealed record HistoryCompareRequest(string DatabasePath, string BaselineRunId, string CandidateRunId);
```

#### `HistoryDetectRequest`

Source: [CMakeWorkflow.Host/History/HistoryDetectRequest.cs](../CMakeWorkflow.Host/History/HistoryDetectRequest.cs#L5)

```csharp
public sealed record HistoryDetectRequest(string DatabasePath, string BaselineRunId, string CandidateRunId, string Metric, string Direction, double MinimumEffectPercent, string? Variant = null, string? Scenario = null, double Alpha = 0.05);
```

#### `HistoryImportRequest`

Source: [CMakeWorkflow.Host/History/HistoryImportRequest.cs](../CMakeWorkflow.Host/History/HistoryImportRequest.cs#L5)

```csharp
public sealed record HistoryImportRequest(string DatabasePath, string Directory, bool Recursive = false);
```

#### `HistoryMetricsRequest`

Source: [CMakeWorkflow.Host/History/HistoryMetricsRequest.cs](../CMakeWorkflow.Host/History/HistoryMetricsRequest.cs#L5)

```csharp
public sealed record HistoryMetricsRequest(string DatabasePath, string? RunId = null, string? Metric = null, string? Variant = null, string? Scenario = null);
```

#### `HistoryRunsPageRequest`

Source: [CMakeWorkflow.Host/History/HistoryRunsPageRequest.cs](../CMakeWorkflow.Host/History/HistoryRunsPageRequest.cs#L5)

```csharp
public sealed record HistoryRunsPageRequest(string DatabasePath, int Limit = 50, string? Status = null, string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null, string? Cursor = null);
```

#### `HistoryRunsRequest`

Source: [CMakeWorkflow.Host/History/HistoryRunsRequest.cs](../CMakeWorkflow.Host/History/HistoryRunsRequest.cs#L5)

```csharp
public sealed record HistoryRunsRequest(string DatabasePath, int Limit = 50, string? Status = null, string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null);
```

#### `HistoryShowRequest`

Source: [CMakeWorkflow.Host/History/HistoryShowRequest.cs](../CMakeWorkflow.Host/History/HistoryShowRequest.cs#L5)

```csharp
public sealed record HistoryShowRequest(string DatabasePath, string RunId);
```

### Namespace `CMakeWorkflow.Host`

#### `HostCommand`

Source: [CMakeWorkflow.Host/HostCommand.cs](../CMakeWorkflow.Host/HostCommand.cs#L10)

```csharp
public static class HostCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Host.Profiling`

#### `ProfileAnalysis`

Source: [CMakeWorkflow.Host/Profiling/ProfileAnalysis.cs](../CMakeWorkflow.Host/Profiling/ProfileAnalysis.cs#L5)

```csharp
public sealed record ProfileAnalysis(int SchemaVersion, string RunDirectory, string AnalysisFile);
```

#### `ProfileAnalyzeRequest`

Source: [CMakeWorkflow.Host/Profiling/ProfileAnalyzeRequest.cs](../CMakeWorkflow.Host/Profiling/ProfileAnalyzeRequest.cs#L5)

```csharp
public sealed record ProfileAnalyzeRequest(string RunDirectory);
```

#### `ProfilePreparation`

Source: [CMakeWorkflow.Host/Profiling/ProfilePreparation.cs](../CMakeWorkflow.Host/Profiling/ProfilePreparation.cs#L5)

```csharp
public sealed record ProfilePreparation(int SchemaVersion, string Manifest, string ManifestSha256, string ResultsDirectory);
```

#### `ProfilePrepareRequest`

Source: [CMakeWorkflow.Host/Profiling/ProfilePrepareRequest.cs](../CMakeWorkflow.Host/Profiling/ProfilePrepareRequest.cs#L6)

```csharp
public sealed record ProfilePrepareRequest(string RequestId, string ResultsDirectory, string Target, System.Collections.Generic.List<CMakeWorkflow.Host.Profiling.ProfileVariantRequest> Variants, int Repeats = 3, double TimeoutSeconds = 300, string Backend = "auto", string Mode = "cpu", string? Configuration = null, string? SourceDirectory = null, string? ConfigurePreset = null, string[]? Arguments = null);
```

#### `ProfileReplayRequest`

Source: [CMakeWorkflow.Host/Profiling/ProfileReplayRequest.cs](../CMakeWorkflow.Host/Profiling/ProfileReplayRequest.cs#L7)

```csharp
public sealed record ProfileReplayRequest(string BaselineDirectory, string CandidateDirectory, string NodeId, string ResultsDirectory, string? CaseName = null, int HarnessIndex = 0, string Backend = "auto", double TimeoutSeconds = 300);
```

#### `ProfileReplayResponse`

Source: [CMakeWorkflow.Host/Profiling/ProfileReplayResponse.cs](../CMakeWorkflow.Host/Profiling/ProfileReplayResponse.cs#L5)

```csharp
public sealed record ProfileReplayResponse(int SchemaVersion, string RunDirectory, string AnalysisFile);
```

#### `ProfileVariantRequest`

Source: [CMakeWorkflow.Host/Profiling/ProfileVariantRequest.cs](../CMakeWorkflow.Host/Profiling/ProfileVariantRequest.cs#L7)

```csharp
public sealed record ProfileVariantRequest(string Name, string BuildDirectory, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain = null);
```

### Namespace `CMakeWorkflow.Host.Protocol`

#### `ApplicationError`

Source: [CMakeWorkflow.Host/Protocol/ApplicationError.cs](../CMakeWorkflow.Host/Protocol/ApplicationError.cs#L5)

```csharp
public sealed record ApplicationError(string Code, string Message);
```

#### `HostInfo`

Source: [CMakeWorkflow.Host/Protocol/HostInfo.cs](../CMakeWorkflow.Host/Protocol/HostInfo.cs#L7)

```csharp
public sealed record HostInfo(int ProtocolVersion, int ProcessId, string WorkspaceDirectory, string[] Capabilities, System.Collections.Generic.List<CMakeWorkflow.Tooling.Tools.ToolingProfileView> Profiles);
```

#### `HostJson`

Source: [CMakeWorkflow.Host/Protocol/HostJson.cs](../CMakeWorkflow.Host/Protocol/HostJson.cs#L24)

```csharp
public class HostJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
```

#### `IHostNotifications`

Source: [CMakeWorkflow.Host/Protocol/IHostNotifications.cs](../CMakeWorkflow.Host/Protocol/IHostNotifications.cs#L7)

```csharp
public interface IHostNotifications
{
    void Progress(CMakeWorkflow.Host.Provisioning.DoctorProgress progress);
    void FixProgress(CMakeWorkflow.Host.Provisioning.FixProgress progress);
}
```

#### `IHostRpc`

Source: [CMakeWorkflow.Host/Protocol/ApplicationProtocol.cs](../CMakeWorkflow.Host/Protocol/ApplicationProtocol.cs#L16), [CMakeWorkflow.Host/Protocol/HostProtocol.cs](../CMakeWorkflow.Host/Protocol/HostProtocol.cs#L11)

```csharp
public interface IHostRpc
{
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.WorkflowPreflightResult> WorkflowPreflightAsync(CMakeWorkflow.Host.Workflows.WorkflowPreflightRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<System.Text.Json.JsonElement> ExecutionReportAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> ExecutionStartAsync(CMakeWorkflow.Host.Workflows.ExecutionStartRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> WorkflowStartAsync(CMakeWorkflow.Host.Workflows.WorkflowStartRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationCancelResult> OperationCancelAsync(CMakeWorkflow.Host.Workflows.OperationCancelRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> WorkflowResumeAsync(CMakeWorkflow.Host.Workflows.WorkflowResumeRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> WorkflowResumeExecuteAsync(CMakeWorkflow.Host.Workflows.WorkflowResumeRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationEventsPage> OperationEventsAsync(CMakeWorkflow.Host.Workflows.OperationEventsRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> RecoveryInspectAsync(CMakeWorkflow.Host.Workflows.RecoveryInspectRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> RecoveryReconcileAsync(CMakeWorkflow.Host.Workflows.RecoveryReconcileRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> OperationFindAsync(CMakeWorkflow.Host.Workflows.OperationFindRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationListPage> OperationListAsync(CMakeWorkflow.Host.Workflows.OperationListRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationOverview> OperationOverviewAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> OperationGetAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot> OperationReportAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationArtifactList> OperationArtifactListAsync(CMakeWorkflow.Host.Workflows.OperationArtifactListRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationArtifactChunk> OperationArtifactReadAsync(CMakeWorkflow.Host.Workflows.OperationArtifactChunkRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.ObservedBuildContext> ContextInspectAsync(CMakeWorkflow.Host.Workflows.ContextInspectRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.TargetResolutionView> TargetResolveAsync(CMakeWorkflow.Host.Workflows.TargetResolveRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Planning.WorkflowPlanView> WorkflowPlanAsync(CMakeWorkflow.Host.Workflows.WorkflowPlanRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfilePreparation> ProfilePrepareAsync(CMakeWorkflow.Host.Profiling.ProfilePrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BenchmarkPreparation> BenchmarkPrepareAsync(CMakeWorkflow.Host.Recipes.BenchmarkPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.ProfileRecipePreparation> ProfileRecipePrepareAsync(CMakeWorkflow.Host.Recipes.ProfileRecipePrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.TestMatrixPreparation> TestMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.TestMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BuildMatrixPreparation> BuildMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.BuildMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BenchmarkMatrixPreparation> BenchmarkMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.BenchmarkMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfileAnalysis> ProfileAnalyzeAsync(CMakeWorkflow.Host.Profiling.ProfileAnalyzeRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.TargetPreparation> TargetPrepareAsync(CMakeWorkflow.Host.Workflows.TargetPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfileReplayResponse> ProfileReplayAsync(CMakeWorkflow.Host.Profiling.ProfileReplayRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignMutationAsync(CMakeWorkflow.Host.Campaigns.CampaignMutationRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignWeakMemoryAsync(CMakeWorkflow.Host.Campaigns.CampaignWeakMemoryRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzSeedAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzSeedRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzRunAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzRunRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzTriageAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzTriageRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzMergeAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzMergeRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot> RunGetAsync(CMakeWorkflow.Host.Workflows.RunGetRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.History.Models.HistoryImportResult> HistoryImportAsync(CMakeWorkflow.Host.History.HistoryImportRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.History.HistoryRunPage> HistoryRunsPageAsync(CMakeWorkflow.Host.History.HistoryRunsPageRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryRun>> HistoryRunsAsync(CMakeWorkflow.Host.History.HistoryRunsRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.History.Models.HistoryDetail> HistoryShowAsync(CMakeWorkflow.Host.History.HistoryShowRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement>> HistoryMetricsAsync(CMakeWorkflow.Host.History.HistoryMetricsRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement>> HistoryQueryAsync(CMakeWorkflow.Host.History.HistoryMetricsRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Analysis.HistoryDifference>> HistoryCompareAsync(CMakeWorkflow.Host.History.HistoryCompareRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.History.Analysis.HistoryDetectionReport> HistoryDetectAsync(CMakeWorkflow.Host.History.HistoryDetectRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Protocol.HostInfo> InitializeAsync(CMakeWorkflow.Host.Protocol.InitializeRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolingPlan> DoctorAsync(CMakeWorkflow.Host.Provisioning.DoctorRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Provisioning.FixPlan> PlanFixAsync(CMakeWorkflow.Host.Provisioning.PlanFixRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.ToolchainInventory> ToolchainsDiscoverAsync(CMakeWorkflow.Host.Toolchains.ToolchainDiscoverRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.ToolchainResolution> ToolchainsResolveAsync(CMakeWorkflow.Host.Toolchains.ToolchainResolveRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(CMakeWorkflow.Host.Toolchains.ToolchainInspectEnvironmentRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationPlan> EnvironmentPlanAsync(CMakeWorkflow.Host.Provisioning.EnvironmentPlanRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationResult> EnvironmentApplyAsync(CMakeWorkflow.Host.Provisioning.EnvironmentApplyRequest request, System.Threading.CancellationToken cancellationToken);
    System.Threading.Tasks.Task<CMakeWorkflow.Host.Provisioning.FixResult> ApplyFixAsync(CMakeWorkflow.Host.Provisioning.ApplyFixRequest request, System.Threading.CancellationToken cancellationToken);
}
```

#### `InitializeRequest`

Source: [CMakeWorkflow.Host/Protocol/InitializeRequest.cs](../CMakeWorkflow.Host/Protocol/InitializeRequest.cs#L7)

```csharp
public sealed record InitializeRequest(int ProtocolVersion, string WorkspaceDirectory);
```

### Namespace `CMakeWorkflow.Host.Provisioning`

#### `ApplyFixRequest`

Source: [CMakeWorkflow.Host/Provisioning/ApplyFixRequest.cs](../CMakeWorkflow.Host/Provisioning/ApplyFixRequest.cs#L5)

```csharp
public sealed record ApplyFixRequest(string OperationId, string PlanId);
```

#### `DoctorProgress`

Source: [CMakeWorkflow.Host/Provisioning/DoctorProgress.cs](../CMakeWorkflow.Host/Provisioning/DoctorProgress.cs#L7)

```csharp
public sealed record DoctorProgress(string OperationId, int Sequence, string Stage, CMakeWorkflow.Tooling.Tools.ToolStatus? Tool);
```

#### `DoctorRequest`

Source: [CMakeWorkflow.Host/Provisioning/DoctorRequest.cs](../CMakeWorkflow.Host/Provisioning/DoctorRequest.cs#L5)

```csharp
public sealed record DoctorRequest(string OperationId, string[] Profiles, string? InstallDirectory);
```

#### `EnvironmentApplyRequest`

Source: [CMakeWorkflow.Host/Provisioning/EnvironmentApplyRequest.cs](../CMakeWorkflow.Host/Provisioning/EnvironmentApplyRequest.cs#L5)

```csharp
public sealed record EnvironmentApplyRequest(string RequirementsPath, string ApprovalSha256, bool AllowSystemChanges);
```

#### `EnvironmentPlanRequest`

Source: [CMakeWorkflow.Host/Provisioning/EnvironmentPlanRequest.cs](../CMakeWorkflow.Host/Provisioning/EnvironmentPlanRequest.cs#L5)

```csharp
public sealed record EnvironmentPlanRequest(string RequirementsPath);
```

#### `FixCoordinator`

Source: [CMakeWorkflow.Host/Provisioning/FixCoordinator.cs](../CMakeWorkflow.Host/Provisioning/FixCoordinator.cs#L8)

```csharp
public sealed class FixCoordinator(string? lockDirectory = null) : System.IDisposable
{
    public System.IDisposable? TryAcquire();
    public void Dispose();
}
```

#### `FixPlan`

Source: [CMakeWorkflow.Host/Provisioning/FixPlan.cs](../CMakeWorkflow.Host/Provisioning/FixPlan.cs#L7)

```csharp
public sealed record FixPlan(int SchemaVersion, string PlanId, System.DateTimeOffset ExpiresAt, CMakeWorkflow.Tooling.Tools.ToolingPlan Plan, string? RunDirectory = null, string? RunSnapshot = null);
```

#### `FixProgress`

Source: [CMakeWorkflow.Host/Provisioning/FixProgress.cs](../CMakeWorkflow.Host/Provisioning/FixProgress.cs#L5)

```csharp
public sealed record FixProgress(string OperationId, int Sequence, string Stage, string? Tool, string? Message);
```

#### `FixResult`

Source: [CMakeWorkflow.Host/Provisioning/FixResult.cs](../CMakeWorkflow.Host/Provisioning/FixResult.cs#L7)

```csharp
public sealed record FixResult(int SchemaVersion, string Status, CMakeWorkflow.Tooling.Tools.ToolingResult? Installation, CMakeWorkflow.Tooling.Tools.ToolingPlan? Verification, string[] Diagnostics, bool ReadyToResume = false);
```

#### `PlanFixRequest`

Source: [CMakeWorkflow.Host/Provisioning/PlanFixRequest.cs](../CMakeWorkflow.Host/Provisioning/PlanFixRequest.cs#L7)

```csharp
public sealed record PlanFixRequest(string[] Profiles, string? InstallDirectory, CMakeWorkflow.Tooling.Tools.PackageManagerKind PackageManager = Auto, bool AllowFallback = true, string? RunDirectory = null);
```

### Namespace `CMakeWorkflow.Host.Recipes`

#### `BenchmarkMatrixPreparation`

Source: [CMakeWorkflow.Host/Recipes/BenchmarkMatrixPreparation.cs](../CMakeWorkflow.Host/Recipes/BenchmarkMatrixPreparation.cs#L5)

```csharp
public sealed record BenchmarkMatrixPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `BenchmarkMatrixPrepareRequest`

Source: [CMakeWorkflow.Host/Recipes/BenchmarkMatrixPrepareRequest.cs](../CMakeWorkflow.Host/Recipes/BenchmarkMatrixPrepareRequest.cs#L7)

```csharp
public sealed record BenchmarkMatrixPrepareRequest(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BenchmarkMatrixRecipeV1 Recipe);
```

#### `BenchmarkPreparation`

Source: [CMakeWorkflow.Host/Recipes/BenchmarkPreparation.cs](../CMakeWorkflow.Host/Recipes/BenchmarkPreparation.cs#L5)

```csharp
public sealed record BenchmarkPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `BenchmarkPrepareRequest`

Source: [CMakeWorkflow.Host/Recipes/BenchmarkPrepareRequest.cs](../CMakeWorkflow.Host/Recipes/BenchmarkPrepareRequest.cs#L7)

```csharp
public sealed record BenchmarkPrepareRequest(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BenchmarkRecipeV1 Recipe);
```

#### `BuildMatrixPreparation`

Source: [CMakeWorkflow.Host/Recipes/BuildMatrixPreparation.cs](../CMakeWorkflow.Host/Recipes/BuildMatrixPreparation.cs#L5)

```csharp
public sealed record BuildMatrixPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `BuildMatrixPrepareRequest`

Source: [CMakeWorkflow.Host/Recipes/BuildMatrixPrepareRequest.cs](../CMakeWorkflow.Host/Recipes/BuildMatrixPrepareRequest.cs#L7)

```csharp
public sealed record BuildMatrixPrepareRequest(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BuildMatrixRecipeV1 Recipe);
```

#### `ProfileRecipePreparation`

Source: [CMakeWorkflow.Host/Recipes/ProfileRecipePreparation.cs](../CMakeWorkflow.Host/Recipes/ProfileRecipePreparation.cs#L5)

```csharp
public sealed record ProfileRecipePreparation(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `ProfileRecipePrepareRequest`

Source: [CMakeWorkflow.Host/Recipes/ProfileRecipePrepareRequest.cs](../CMakeWorkflow.Host/Recipes/ProfileRecipePrepareRequest.cs#L7)

```csharp
public sealed record ProfileRecipePrepareRequest(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.ProfileRecipeV1 Recipe);
```

#### `TestMatrixPreparation`

Source: [CMakeWorkflow.Host/Recipes/TestMatrixPreparation.cs](../CMakeWorkflow.Host/Recipes/TestMatrixPreparation.cs#L5)

```csharp
public sealed record TestMatrixPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `TestMatrixPrepareRequest`

Source: [CMakeWorkflow.Host/Recipes/TestMatrixPrepareRequest.cs](../CMakeWorkflow.Host/Recipes/TestMatrixPrepareRequest.cs#L7)

```csharp
public sealed record TestMatrixPrepareRequest(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.TestMatrixRecipeV1 Recipe);
```

### Namespace `CMakeWorkflow.Host.Sessions`

#### `DoctorSession`

Source: [CMakeWorkflow.Host/Sessions/ApplicationSession.cs](../CMakeWorkflow.Host/Sessions/ApplicationSession.cs#L20), [CMakeWorkflow.Host/Sessions/BenchmarkSession.cs](../CMakeWorkflow.Host/Sessions/BenchmarkSession.cs#L10), [CMakeWorkflow.Host/Sessions/DoctorSession.cs](../CMakeWorkflow.Host/Sessions/DoctorSession.cs#L12), [CMakeWorkflow.Host/Sessions/EnvironmentSession.cs](../CMakeWorkflow.Host/Sessions/EnvironmentSession.cs#L7), [CMakeWorkflow.Host/Sessions/ExecutionSession.cs](../CMakeWorkflow.Host/Sessions/ExecutionSession.cs#L5), [CMakeWorkflow.Host/Sessions/FixSession.cs](../CMakeWorkflow.Host/Sessions/FixSession.cs#L9), [CMakeWorkflow.Host/Sessions/ProcessExecutionSession.cs](../CMakeWorkflow.Host/Sessions/ProcessExecutionSession.cs#L7), [CMakeWorkflow.Host/Sessions/ProfileAnalysisSession.cs](../CMakeWorkflow.Host/Sessions/ProfileAnalysisSession.cs#L9), [CMakeWorkflow.Host/Sessions/ProfileSession.cs](../CMakeWorkflow.Host/Sessions/ProfileSession.cs#L13), [CMakeWorkflow.Host/Sessions/RecipeSession.cs](../CMakeWorkflow.Host/Sessions/RecipeSession.cs#L13), [CMakeWorkflow.Host/Sessions/TargetCampaignSession.cs](../CMakeWorkflow.Host/Sessions/TargetCampaignSession.cs#L19), [CMakeWorkflow.Host/Sessions/ToolchainSession.cs](../CMakeWorkflow.Host/Sessions/ToolchainSession.cs#L7), [CMakeWorkflow.Host/Sessions/WorkflowPreflightSession.cs](../CMakeWorkflow.Host/Sessions/WorkflowPreflightSession.cs#L9)

```csharp
public sealed class DoctorSession(System.Func<string, CMakeWorkflow.Tooling.Tools.ToolingService> createTooling, System.Func<CMakeWorkflow.Host.Provisioning.DoctorProgress, System.Threading.Tasks.Task> progress, System.Threading.CancellationToken disconnected, CMakeWorkflow.Host.Provisioning.FixCoordinator fixes, System.Func<CMakeWorkflow.Host.Provisioning.FixProgress, System.Threading.Tasks.Task> fixProgress, System.TimeProvider? timeProvider = null, CMakeWorkflow.Host.Workflows.DurableExecutionCoordinator? durableExecutions = null) : CMakeWorkflow.Host.Protocol.IHostRpc
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> WorkflowStartAsync(CMakeWorkflow.Host.Workflows.WorkflowStartRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationCancelResult> OperationCancelAsync(CMakeWorkflow.Host.Workflows.OperationCancelRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> RecoveryInspectAsync(CMakeWorkflow.Host.Workflows.RecoveryInspectRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> RecoveryReconcileAsync(CMakeWorkflow.Host.Workflows.RecoveryReconcileRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> WorkflowResumeAsync(CMakeWorkflow.Host.Workflows.WorkflowResumeRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.RecoveryInspectionView> WorkflowResumeExecuteAsync(CMakeWorkflow.Host.Workflows.WorkflowResumeRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationEventsPage> OperationEventsAsync(CMakeWorkflow.Host.Workflows.OperationEventsRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationListPage> OperationListAsync(CMakeWorkflow.Host.Workflows.OperationListRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> OperationFindAsync(CMakeWorkflow.Host.Workflows.OperationFindRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> OperationGetAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationOverview> OperationOverviewAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot> OperationReportAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationArtifactList> OperationArtifactListAsync(CMakeWorkflow.Host.Workflows.OperationArtifactListRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationArtifactChunk> OperationArtifactReadAsync(CMakeWorkflow.Host.Workflows.OperationArtifactChunkRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.ObservedBuildContext> ContextInspectAsync(CMakeWorkflow.Host.Workflows.ContextInspectRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Build.TargetResolutionView> TargetResolveAsync(CMakeWorkflow.Host.Workflows.TargetResolveRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Planning.WorkflowPlanView> WorkflowPlanAsync(CMakeWorkflow.Host.Workflows.WorkflowPlanRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Reporting.WorkflowReportSnapshot> RunGetAsync(CMakeWorkflow.Host.Workflows.RunGetRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.History.Models.HistoryImportResult> HistoryImportAsync(CMakeWorkflow.Host.History.HistoryImportRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.History.HistoryRunPage> HistoryRunsPageAsync(CMakeWorkflow.Host.History.HistoryRunsPageRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryRun>> HistoryRunsAsync(CMakeWorkflow.Host.History.HistoryRunsRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.History.Models.HistoryDetail> HistoryShowAsync(CMakeWorkflow.Host.History.HistoryShowRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement>> HistoryMetricsAsync(CMakeWorkflow.Host.History.HistoryMetricsRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement>> HistoryQueryAsync(CMakeWorkflow.Host.History.HistoryMetricsRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.History.Analysis.HistoryDifference>> HistoryCompareAsync(CMakeWorkflow.Host.History.HistoryCompareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.History.Analysis.HistoryDetectionReport> HistoryDetectAsync(CMakeWorkflow.Host.History.HistoryDetectRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BenchmarkPreparation> BenchmarkPrepareAsync(CMakeWorkflow.Host.Recipes.BenchmarkPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task DrainAsync();
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Protocol.HostInfo> InitializeAsync(CMakeWorkflow.Host.Protocol.InitializeRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolingPlan> DoctorAsync(CMakeWorkflow.Host.Provisioning.DoctorRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationPlan> EnvironmentPlanAsync(CMakeWorkflow.Host.Provisioning.EnvironmentPlanRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationResult> EnvironmentApplyAsync(CMakeWorkflow.Host.Provisioning.EnvironmentApplyRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationReference> ExecutionStartAsync(CMakeWorkflow.Host.Workflows.ExecutionStartRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Provisioning.FixPlan> PlanFixAsync(CMakeWorkflow.Host.Provisioning.PlanFixRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Provisioning.FixResult> ApplyFixAsync(CMakeWorkflow.Host.Provisioning.ApplyFixRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<System.Text.Json.JsonElement> ExecutionReportAsync(CMakeWorkflow.Host.Workflows.OperationGetRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfileAnalysis> ProfileAnalyzeAsync(CMakeWorkflow.Host.Profiling.ProfileAnalyzeRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfilePreparation> ProfilePrepareAsync(CMakeWorkflow.Host.Profiling.ProfilePrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.ProfileRecipePreparation> ProfileRecipePrepareAsync(CMakeWorkflow.Host.Recipes.ProfileRecipePrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.TestMatrixPreparation> TestMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.TestMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BuildMatrixPreparation> BuildMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.BuildMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Recipes.BenchmarkMatrixPreparation> BenchmarkMatrixPrepareAsync(CMakeWorkflow.Host.Recipes.BenchmarkMatrixPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.TargetPreparation> TargetPrepareAsync(CMakeWorkflow.Host.Workflows.TargetPrepareRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Profiling.ProfileReplayResponse> ProfileReplayAsync(CMakeWorkflow.Host.Profiling.ProfileReplayRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignMutationAsync(CMakeWorkflow.Host.Campaigns.CampaignMutationRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignWeakMemoryAsync(CMakeWorkflow.Host.Campaigns.CampaignWeakMemoryRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzSeedAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzSeedRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzRunAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzRunRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzTriageAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzTriageRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Campaigns.CampaignExecutionResult> CampaignFuzzMergeAsync(CMakeWorkflow.Host.Campaigns.CampaignFuzzMergeRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.ToolchainInventory> ToolchainsDiscoverAsync(CMakeWorkflow.Host.Toolchains.ToolchainDiscoverRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.ToolchainResolution> ToolchainsResolveAsync(CMakeWorkflow.Host.Toolchains.ToolchainResolveRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(CMakeWorkflow.Host.Toolchains.ToolchainInspectEnvironmentRequest request, System.Threading.CancellationToken cancellationToken);
    public System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.WorkflowPreflightResult> WorkflowPreflightAsync(CMakeWorkflow.Host.Workflows.WorkflowPreflightRequest request, System.Threading.CancellationToken cancellationToken);
}
```

### Namespace `CMakeWorkflow.Host.Toolchains`

#### `ToolchainDiscoverRequest`

Source: [CMakeWorkflow.Host/Toolchains/ToolchainDiscoverRequest.cs](../CMakeWorkflow.Host/Toolchains/ToolchainDiscoverRequest.cs#L5)

```csharp
public sealed record ToolchainDiscoverRequest();
```

#### `ToolchainInspectEnvironmentRequest`

Source: [CMakeWorkflow.Host/Toolchains/ToolchainInspectEnvironmentRequest.cs](../CMakeWorkflow.Host/Toolchains/ToolchainInspectEnvironmentRequest.cs#L7)

```csharp
public sealed record ToolchainInspectEnvironmentRequest(CMakeWorkflow.Tooling.Toolchains.DeveloperEnvironmentRequest Environment);
```

#### `ToolchainResolveRequest`

Source: [CMakeWorkflow.Host/Toolchains/ToolchainResolveRequest.cs](../CMakeWorkflow.Host/Toolchains/ToolchainResolveRequest.cs#L7)

```csharp
public sealed record ToolchainResolveRequest(CMakeWorkflow.Tooling.Toolchains.ToolchainSelection Selection);
```

#### `WindowsWorkflowToolchainProvider`

Source: [CMakeWorkflow.Host/Toolchains/WindowsWorkflowToolchainProvider.cs](../CMakeWorkflow.Host/Toolchains/WindowsWorkflowToolchainProvider.cs#L15)

```csharp
public sealed class WindowsWorkflowToolchainProvider : CMakeWorkflow.CMake.Toolchains.IWorkflowToolchainProvider
{
    public System.Threading.Tasks.Task<CMakeWorkflow.CMake.Toolchains.WorkflowToolchainRuntime> ResolveAsync(CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding binding, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Host.Transport`

#### `HostConnection`

Creates header-delimited JSON-RPC connections using closed source-generated Host DTOs.

No local targets are registered and listening has not started. The caller configures the session and owns disposal.
Application failures use code 1001 with ApplicationError data; no CLR exception graph crosses the wire.

Source: [CMakeWorkflow.Host/Transport/HostConnection.cs](../CMakeWorkflow.Host/Transport/HostConnection.cs#L14)

```csharp
public static class HostConnection
{
    public static StreamJsonRpc.JsonRpc Create(System.IO.Stream stream);
    public static StreamJsonRpc.JsonRpc Create(System.IO.Stream input, System.IO.Stream output);
}
```

#### `HostServer`

Hosts initialized RPC sessions and shared durable execution coordinators.

Source: [CMakeWorkflow.Host/Transport/HostServer.cs](../CMakeWorkflow.Host/Transport/HostServer.cs#L15)

```csharp
public static class HostServer
{
    public static System.Threading.Tasks.Task RunStdioAsync(System.IO.Stream input, System.IO.Stream output, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task RunAsync(string endpoint, System.Threading.CancellationToken ct);
}
```

#### `LocalTransport`

Source: [CMakeWorkflow.Host/Transport/LocalTransport.cs](../CMakeWorkflow.Host/Transport/LocalTransport.cs#L9)

```csharp
public static class LocalTransport
{
    public static string PipeName(string endpoint);
    public static void ValidateUnixEndpoint(string endpoint);
    public static System.Threading.Tasks.Task<System.IO.Stream> ConnectAsync(string endpoint, System.Threading.CancellationToken ct);
}
```

#### `StdioBridge`

A byte tunnel to a persistent Host, with no ownership of that Host.

Source: [CMakeWorkflow.Host/Transport/StdioBridge.cs](../CMakeWorkflow.Host/Transport/StdioBridge.cs#L6)

```csharp
public static class StdioBridge
{
    public static System.Threading.Tasks.Task RunAsync(string endpoint, System.IO.Stream input, System.IO.Stream output, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task ForwardAsync(System.IO.Stream input, System.IO.Stream output, System.IO.Stream connection, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Host.Workflows`

#### `AcceptedWorkflow`

Source: [CMakeWorkflow.Host/Workflows/AcceptedWorkflow.cs](../CMakeWorkflow.Host/Workflows/AcceptedWorkflow.cs#L7)

```csharp
public sealed record AcceptedWorkflow(string WorkspaceId, string OperationId, string ClientRequestId, string RequestDigest, string ManifestSha256, string ManifestBaseDirectory, string State);
```

#### `ContextInspectRequest`

Source: [CMakeWorkflow.Host/Workflows/ContextInspectRequest.cs](../CMakeWorkflow.Host/Workflows/ContextInspectRequest.cs#L5)

```csharp
public sealed record ContextInspectRequest(string BuildDirectory);
```

#### `DurableAcceptanceStore`

Transactional workspace ledger for immutable acceptance, dispatch evidence and recovery.

The caller must authorize the workspace and own/dispose this store connection; do not share it concurrently. Acceptance
commits exact snapshot bytes and request identity. Dispatch authorization is a separate one-way boundary. Only a
verified runner checkpoint binds possible external effects; authorization alone does not prove launch. Results
reservations and uncertain attempts survive failure. This store does not launch processes, prove quiescence or clean run
files.

Source: [CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs](../CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs#L18)

```csharp
public sealed class DurableAcceptanceStore : System.IDisposable
{
    public string WorkspaceId { get; }
    public DurableAcceptanceStore(string workspaceDirectory, bool readOnly = false);
    public CMakeWorkflow.Host.Workflows.AcceptedWorkflow Accept(string clientRequestId, string manifestPath, string expectedManifestSha256, int jobs, string resultsDirectory);
    public string GetDomainKind(string operationId);
    public CMakeWorkflow.Host.Workflows.OperationListPage ListOperations(CMakeWorkflow.Host.Workflows.OperationListRequest request, System.Threading.CancellationToken ct = default);
    public CMakeWorkflow.Host.Workflows.AcceptedWorkflow? Find(string clientRequestId);
    public CMakeWorkflow.Host.Workflows.AcceptedWorkflow? Get(string operationId);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.RecoveryInspection InspectRecovery(string operationId);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.RecoveryInspection Reconcile(string operationId, long expectedRevision, string decision, string note);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.RecoveryInspection AdmitResume(string operationId, long expectedRevision);
    public string ClaimResumeAttempt(string operationId, string runDirectory, string? expectedCheckpointSha256 = null);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.CancellationDecision RequestCancellation(string operationId, long expectedRevision);
    public void ReserveResultsDirectory(string operationId, string resultsDirectory);
    public CMakeWorkflow.Host.Workflows.AcceptedWorkflow AuthorizeDispatch(string operationId);
    public string ConfirmRunnerCheckpoint(string operationId, string runDirectory);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.Finalization? GetFinalization(string operationId);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.Finalization FinalizeExecution(string operationId, string runDirectory, int exitCode);
    public (string RunDirectory, string CheckpointSha256)? GetDispatchCheckpoint(string operationId);
    public byte[] GetSnapshot(string operationId);
    public void Dispose();
}
```

#### `DurableAcceptanceStore.CancellationDecision`

Source: [CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs](../CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs#L926)

```csharp
public sealed record CancellationDecision(string OperationId, string State, long Revision, bool Requested);
```

#### `DurableAcceptanceStore.Finalization`

Source: [CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs](../CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs#L1242)

```csharp
public sealed record Finalization(string RunDirectory, string ReportSha256, string State, int ExitCode);
```

#### `DurableAcceptanceStore.RecoveryInspection`

Source: [CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs](../CMakeWorkflow.Host/Workflows/DurableAcceptanceStore.cs#L595)

```csharp
public sealed record RecoveryInspection(string OperationId, string AcceptanceState, string? TerminalState, bool CancellationRequested, string? RunDirectory, string? CheckpointSha256, string? Decision, long Revision, string? Note);
```

#### `DurableExecutionCoordinator`

Owns background execution tasks independently of individual RPC connections.

Accepted work is linked to the supplied Host shutdown token, never the submitting request token. The in-memory task map
prevents duplicate scheduling here; executor file leases and ledger transitions guard cross-process execution. The Host
owner signals shutdown and drains tasks before disposing shared resources. Background failures are observed and logged;
they do not manufacture terminal success/failure evidence for an uncertain runner.

Source: [CMakeWorkflow.Host/Workflows/DurableExecutionCoordinator.cs](../CMakeWorkflow.Host/Workflows/DurableExecutionCoordinator.cs#L16)

```csharp
public sealed class DurableExecutionCoordinator(System.Threading.CancellationToken shutdown) : System.IDisposable
{
    public CMakeWorkflow.Host.Workflows.OperationReference Start(string workspace, CMakeWorkflow.Host.Workflows.WorkflowStartRequest request);
    public CMakeWorkflow.Host.Workflows.OperationReference StartProcess(string workspace, CMakeWorkflow.Host.Workflows.ExecutionStartRequest request);
    public void Resume(string workspace, string operationId);
    public CMakeWorkflow.Host.Workflows.DurableAcceptanceStore.CancellationDecision Cancel(string workspace, string operationId, long expectedRevision);
    public bool IsActive(string workspaceId, string operationId);
    public System.Threading.Tasks.Task DrainAsync();
    public void Dispose();
}
```

#### `DurableRunnerDispatchGate`

Binds a runner checkpoint immediately before external dispatch.

The caller must hold the runner lease and have durably persisted running intent. This gate acquires no execution lease
and neither schedules work nor changes acceptance authorization.

Source: [CMakeWorkflow.Host/Workflows/DurableRunnerDispatchGate.cs](../CMakeWorkflow.Host/Workflows/DurableRunnerDispatchGate.cs#L10)

```csharp
public sealed class DurableRunnerDispatchGate(string workspace, string operationId)
{
    public System.Threading.Tasks.Task ConfirmAsync(string runDirectory, System.Threading.CancellationToken cancellationToken);
}
```

#### `DurableWorkflowExecutor`

Host-owned CMake execution bridge across durable acceptance and runner evidence.

The operation file lease covers validation, dispatch authorization, runner lifetime and finalization. The runner also
owns its run-directory lease. Cancellation is the coordinator execution token, not an RPC token. Exceptions preserve
unresolved state; only verified normal runner returns can finalize an operation.

Source: [CMakeWorkflow.Host/Workflows/DurableWorkflowExecutor.cs](../CMakeWorkflow.Host/Workflows/DurableWorkflowExecutor.cs#L20)

```csharp
public static class DurableWorkflowExecutor
{
    public static System.Threading.Tasks.Task<int> ExecuteResumedAsync(string workspace, string operationId, System.Threading.CancellationToken cancellationToken);
    public static System.Threading.Tasks.Task<int> ExecuteAcceptedAsync(string workspace, string operationId, string clientRequestId, string originalManifestPath, string expectedManifestHash, int jobs, string resultsDirectory, System.Threading.CancellationToken cancellationToken, System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task>? faultBarrier = null);
}
```

#### `ExecutionStartRequest`

Source: [CMakeWorkflow.Host/Workflows/ExecutionStartRequest.cs](../CMakeWorkflow.Host/Workflows/ExecutionStartRequest.cs#L6)

```csharp
public sealed record ExecutionStartRequest(string Domain, string ClientRequestId, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

#### `OperationArtifactChunk`

Source: [CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs#L18)

```csharp
public sealed record OperationArtifactChunk(string ArtifactId, long Offset, long TotalSize, long LastWriteUtcTicks, byte[] Content, bool Eof);
```

#### `OperationArtifactChunkRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs#L8)

```csharp
public sealed record OperationArtifactChunkRequest(string OperationId, string ArtifactId, long Offset, int Count, long ExpectedSize, long ExpectedLastWriteUtcTicks);
```

#### `OperationArtifactInfo`

Source: [CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs#L11)

```csharp
public sealed record OperationArtifactInfo(string ArtifactId, string Kind, string? NodeId, string RelativePath, long Size, long LastWriteUtcTicks, string Sha256);
```

#### `OperationArtifactList`

Source: [CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs#L15)

```csharp
public sealed record OperationArtifactList(string OperationId, string ReportSha256, System.Collections.Generic.List<CMakeWorkflow.Host.Workflows.OperationArtifactInfo> Artifacts);
```

#### `OperationArtifactListRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Host/Workflows/OperationArtifactContracts.cs#L7)

```csharp
public sealed record OperationArtifactListRequest(string OperationId);
```

#### `OperationCancelRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationCancelRequest.cs](../CMakeWorkflow.Host/Workflows/OperationCancelRequest.cs#L5)

```csharp
public sealed record OperationCancelRequest(string OperationId, long ExpectedRevision);
```

#### `OperationCancelResult`

Source: [CMakeWorkflow.Host/Workflows/OperationCancelResult.cs](../CMakeWorkflow.Host/Workflows/OperationCancelResult.cs#L5)

```csharp
public sealed record OperationCancelResult(string OperationId, string State, long Revision, bool Requested);
```

#### `OperationEventReplay`

Reads operation audit/progress journals; events never authorize recovery dispatch.

Source: [CMakeWorkflow.Host/Workflows/OperationEventReplay.cs](../CMakeWorkflow.Host/Workflows/OperationEventReplay.cs#L11)

```csharp
public static class OperationEventReplay
{
    public static System.Threading.Tasks.Task<CMakeWorkflow.Host.Workflows.OperationEventsPage> ReadJournalAsync(string journalPath, string operationId, long afterSequence, int limit, System.Threading.CancellationToken ct);
}
```

#### `OperationEventsPage`

Source: [CMakeWorkflow.Host/Workflows/OperationEventsPage.cs](../CMakeWorkflow.Host/Workflows/OperationEventsPage.cs#L7)

```csharp
public sealed record OperationEventsPage(string OperationId, long AfterSequence, long NextSequence, bool HasMore, System.Collections.Generic.List<CMakeWorkflow.CMake.Recovery.WorkflowEvent> Events);
```

#### `OperationEventsRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationEventsRequest.cs](../CMakeWorkflow.Host/Workflows/OperationEventsRequest.cs#L5)

```csharp
public sealed record OperationEventsRequest(string OperationId, long AfterSequence = 0, int Limit = 100);
```

#### `OperationFindRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationFindRequest.cs](../CMakeWorkflow.Host/Workflows/OperationFindRequest.cs#L5)

```csharp
public sealed record OperationFindRequest(string ClientRequestId);
```

#### `OperationGetRequest`

Source: [CMakeWorkflow.Host/Workflows/OperationGetRequest.cs](../CMakeWorkflow.Host/Workflows/OperationGetRequest.cs#L5)

```csharp
public sealed record OperationGetRequest(string OperationId);
```

#### `OperationListPage`

Source: [CMakeWorkflow.Host/Workflows/OperationListPage.cs](../CMakeWorkflow.Host/Workflows/OperationListPage.cs#L6)

```csharp
public sealed record OperationListPage(int SchemaVersion, System.Collections.Generic.List<CMakeWorkflow.Host.Workflows.OperationReference> Items, string? NextCursor);
```

#### `OperationListRequest`

Query durable operations in this initialized workspace, newest first.

Cursor pagination excludes later admissions; mutable operation states may change between pages.

Source: [CMakeWorkflow.Host/Workflows/OperationListRequest.cs](../CMakeWorkflow.Host/Workflows/OperationListRequest.cs#L7)

```csharp
public sealed record OperationListRequest(int Limit = 50, string? State = null, string? Domain = null, string? Cursor = null);
```

#### `OperationNodeView`

Source: [CMakeWorkflow.Host/Workflows/OperationOverview.cs](../CMakeWorkflow.Host/Workflows/OperationOverview.cs#L18)

```csharp
public sealed record OperationNodeView(string Id, string Kind, string Status, System.Collections.Generic.List<string> Dependencies, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt, int? ExitCode, string? Error, string? Stdout, string? Stderr);
```

#### `OperationOverview`

A durable read projection, independent of CMake and process report schemas.

Node information exists only after the Host verifies finalization and report bytes. During execution the only
authoritative read state is the operation admission/cancellation state; use operations/events for live progress.

Source: [CMakeWorkflow.Host/Workflows/OperationOverview.cs](../CMakeWorkflow.Host/Workflows/OperationOverview.cs#L11)

```csharp
public sealed record OperationOverview(int SchemaVersion, CMakeWorkflow.Host.Workflows.OperationReference Operation, bool IsFinalized, string? RunDirectory, string? ReportSha256, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt, string? Error, string? HostId, string? EnvironmentFingerprint, System.Collections.Generic.List<CMakeWorkflow.Host.Workflows.OperationNodeView> Nodes);
```

#### `OperationReference`

Source: [CMakeWorkflow.Host/Workflows/OperationReference.cs](../CMakeWorkflow.Host/Workflows/OperationReference.cs#L5)

```csharp
public sealed record OperationReference(string WorkspaceId, string OperationId, string ClientRequestId, string Kind, string State, string ManifestSha256, string ManifestBaseDirectory);
```

#### `RecoveryInspectRequest`

Source: [CMakeWorkflow.Host/Workflows/RecoveryInspectRequest.cs](../CMakeWorkflow.Host/Workflows/RecoveryInspectRequest.cs#L5)

```csharp
public sealed record RecoveryInspectRequest(string OperationId);
```

#### `RecoveryInspectionView`

Source: [CMakeWorkflow.Host/Workflows/RecoveryInspectionView.cs](../CMakeWorkflow.Host/Workflows/RecoveryInspectionView.cs#L5)

```csharp
public sealed record RecoveryInspectionView(string OperationId, string AcceptanceState, string? TerminalState, bool CancellationRequested, string? RunDirectory, string? CheckpointSha256, string? Decision, long Revision, string? Note);
```

#### `RecoveryReconcileRequest`

Source: [CMakeWorkflow.Host/Workflows/RecoveryReconcileRequest.cs](../CMakeWorkflow.Host/Workflows/RecoveryReconcileRequest.cs#L5)

```csharp
public sealed record RecoveryReconcileRequest(string OperationId, long ExpectedRevision, string Decision, string Note, bool ConfirmedQuiescent);
```

#### `RunGetRequest`

Source: [CMakeWorkflow.Host/Workflows/RunGetRequest.cs](../CMakeWorkflow.Host/Workflows/RunGetRequest.cs#L5)

```csharp
public sealed record RunGetRequest(string RunDirectory);
```

#### `TargetPreparation`

Source: [CMakeWorkflow.Host/Workflows/TargetPreparation.cs](../CMakeWorkflow.Host/Workflows/TargetPreparation.cs#L5)

```csharp
public sealed record TargetPreparation(int SchemaVersion, string Manifest, string ManifestSha256, string ResultsDirectory);
```

#### `TargetPrepareRequest`

Source: [CMakeWorkflow.Host/Workflows/TargetPrepareRequest.cs](../CMakeWorkflow.Host/Workflows/TargetPrepareRequest.cs#L8)

```csharp
public sealed record TargetPrepareRequest(string RequestId, string ResultsDirectory, string Action, string BuildDirectory, string? Target = null, string? SecondTarget = null, string? Configuration = null, string[]? Arguments = null, string[]? SecondArguments = null, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain = null);
```

#### `TargetResolveRequest`

Source: [CMakeWorkflow.Host/Workflows/TargetResolveRequest.cs](../CMakeWorkflow.Host/Workflows/TargetResolveRequest.cs#L5)

```csharp
public sealed record TargetResolveRequest(string BuildDirectory, string Target, string? Configuration = null);
```

#### `WorkflowPlanRequest`

Source: [CMakeWorkflow.Host/Workflows/WorkflowPlanRequest.cs](../CMakeWorkflow.Host/Workflows/WorkflowPlanRequest.cs#L5)

```csharp
public sealed record WorkflowPlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);
```

#### `WorkflowPreflightDiagnostic`

Source: [CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs#L12)

```csharp
public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);
```

#### `WorkflowPreflightNode`

Source: [CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs#L14)

```csharp
public sealed record WorkflowPreflightNode(string Id, string Kind, string Target, string BuildDirectory, string? Configuration, System.Collections.Generic.List<string> Dependencies, System.Collections.Generic.List<string> Resources, bool Exclusive, string ObservedState, System.Collections.Generic.List<CMakeWorkflow.Host.Workflows.WorkflowPreflightDiagnostic> Diagnostics);
```

#### `WorkflowPreflightRequest`

Source: [CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs#L9)

```csharp
public sealed record WorkflowPreflightRequest(string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

#### `WorkflowPreflightResult`

Source: [CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Host/Workflows/WorkflowPreflight.cs#L22)

```csharp
public sealed record WorkflowPreflightResult(int SchemaVersion, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory, bool Valid, System.Collections.Generic.List<CMakeWorkflow.Host.Workflows.WorkflowPreflightNode> Nodes);
```

#### `WorkflowResumeRequest`

Source: [CMakeWorkflow.Host/Workflows/WorkflowResumeRequest.cs](../CMakeWorkflow.Host/Workflows/WorkflowResumeRequest.cs#L5)

```csharp
public sealed record WorkflowResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
```

#### `WorkflowStartRequest`

Source: [CMakeWorkflow.Host/Workflows/WorkflowStartRequest.cs](../CMakeWorkflow.Host/Workflows/WorkflowStartRequest.cs#L5)

```csharp
public sealed record WorkflowStartRequest(string ClientRequestId, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

## CMakeWorkflow.Profiling

### Namespace `CMakeWorkflow.Profiling.Analysis`

#### `EtwAnalysisResult`

Source: [CMakeWorkflow.Profiling/Analysis/EtwAnalysisResult.cs](../CMakeWorkflow.Profiling/Analysis/EtwAnalysisResult.cs#L5)

```csharp
public sealed record EtwAnalysisResult(string EventDump, string Summary, string Samples, int CpuSamples, int ContextSwitchesIn, int ReadyEvents);
```

#### `EtwTraceAnalyzer`

Source: [CMakeWorkflow.Profiling/Analysis/EtwTraceAnalyzer.cs](../CMakeWorkflow.Profiling/Analysis/EtwTraceAnalyzer.cs#L14)

```csharp
public sealed class EtwTraceAnalyzer(CMakeWorkflow.Profiling.Capture.IProfileProcess? process = null)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Analysis.EtwAnalysisResult> AnalyzeAsync(string trace, string outputDirectory, string captureId, string executable, int? targetProcessId, System.DateTimeOffset? targetStartedAt, System.DateTimeOffset? targetFinishedAt, System.Threading.CancellationToken ct);
    public static CMakeWorkflow.Profiling.Analysis.EtwAnalysisResult ParseXml(string dump, string outputDirectory, string captureId, string executable, int? targetProcessId, System.DateTimeOffset? targetStartedAt, System.DateTimeOffset? targetFinishedAt, System.Threading.CancellationToken ct);
}
```

#### `PerfSchedulerAnalyzer`

Source: [CMakeWorkflow.Profiling/Analysis/PerfSchedulerAnalyzer.cs](../CMakeWorkflow.Profiling/Analysis/PerfSchedulerAnalyzer.cs#L11)

```csharp
public static class PerfSchedulerAnalyzer
{
    public static int Parse(System.IO.TextReader reader, System.IO.TextWriter writer, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<string> AnalyzeAsync(string raw, string directory, string captureId, System.Threading.CancellationToken ct);
}
```

#### `PerfStackAnalyzer`

Source: [CMakeWorkflow.Profiling/Analysis/PerfStackAnalyzer.cs](../CMakeWorkflow.Profiling/Analysis/PerfStackAnalyzer.cs#L7)

```csharp
public static class PerfStackAnalyzer
{
    public static CMakeWorkflow.Profiling.Analysis.StackEvidence Parse(System.IO.TextReader reader, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<string> AnalyzeAsync(string trace, string directory, string captureId, System.Threading.CancellationToken ct);
}
```

#### `ProfileAnalyzer`

Source: [CMakeWorkflow.Profiling/Analysis/ProfileAnalyzer.cs](../CMakeWorkflow.Profiling/Analysis/ProfileAnalyzer.cs#L12)

```csharp
public static class ProfileAnalyzer
{
    public static System.Threading.Tasks.Task<int> AnalyzeAsync(string directory, System.Threading.CancellationToken ct, bool emitResultPath = true);
}
```

#### `StackEvidence`

Source: [CMakeWorkflow.Profiling/Analysis/StackEvidence.cs](../CMakeWorkflow.Profiling/Analysis/StackEvidence.cs#L9)

```csharp
public sealed class StackEvidence
{
    public int Samples { get; }
    public string AnalysisStatus { get; }
    public void RecordParseQuality(bool nonempty, int rejected, int truncated);
    public void Add(System.Collections.Generic.IReadOnlyList<string> leafFirst);
    public System.Text.Json.Nodes.JsonObject ToJson(string backend, string captureId);
}
```

### Namespace `CMakeWorkflow.Profiling.Backends`

#### `LinuxPerfBackend`

Source: [CMakeWorkflow.Profiling/Backends/LinuxPerfBackend.cs](../CMakeWorkflow.Profiling/Backends/LinuxPerfBackend.cs#L6)

```csharp
public sealed class LinuxPerfBackend(CMakeWorkflow.Profiling.Capture.IProfileProcess process) : CMakeWorkflow.Profiling.Capture.IProfilerBackend
{
    public CMakeWorkflow.Profiling.Capture.ProfileCapabilities Capabilities { get; }
    public System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Capture.ProfileResult> CaptureAsync(CMakeWorkflow.Profiling.Capture.ProfileRequest request, System.Threading.CancellationToken cancellationToken);
}
```

#### `MacInstrumentsBackend`

Source: [CMakeWorkflow.Profiling/Backends/MacInstrumentsBackend.cs](../CMakeWorkflow.Profiling/Backends/MacInstrumentsBackend.cs#L6)

```csharp
public sealed class MacInstrumentsBackend(CMakeWorkflow.Profiling.Capture.IProfileProcess process) : CMakeWorkflow.Profiling.Capture.IProfilerBackend
{
    public CMakeWorkflow.Profiling.Capture.ProfileCapabilities Capabilities { get; }
    public System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Capture.ProfileResult> CaptureAsync(CMakeWorkflow.Profiling.Capture.ProfileRequest request, System.Threading.CancellationToken cancellationToken);
}
```

#### `WindowsEtwBackend`

Source: [CMakeWorkflow.Profiling/Backends/WindowsEtwBackend.cs](../CMakeWorkflow.Profiling/Backends/WindowsEtwBackend.cs#L6)

```csharp
public sealed class WindowsEtwBackend(CMakeWorkflow.Profiling.Capture.IProfileProcess process) : CMakeWorkflow.Profiling.Capture.IProfilerBackend
{
    public CMakeWorkflow.Profiling.Capture.ProfileCapabilities Capabilities { get; }
    public System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Capture.ProfileResult> CaptureAsync(CMakeWorkflow.Profiling.Capture.ProfileRequest request, System.Threading.CancellationToken cancellationToken);
}
```

### Namespace `CMakeWorkflow.Profiling.Capture`

#### `IProfileProcess`

Source: [CMakeWorkflow.Profiling/Capture/IProfileProcess.cs](../CMakeWorkflow.Profiling/Capture/IProfileProcess.cs#L7)

```csharp
public interface IProfileProcess
{
    System.Threading.Tasks.Task<CMakeWorkflow.Core.CommandResult> RunAsync(string executable, System.Collections.Generic.IReadOnlyList<string> arguments, string workingDirectory, System.TimeSpan timeout, System.Collections.Generic.IReadOnlyDictionary<string, string?>? environment, System.Threading.CancellationToken cancellationToken);
}
```

#### `IProfilerBackend`

Source: [CMakeWorkflow.Profiling/Capture/IProfilerBackend.cs](../CMakeWorkflow.Profiling/Capture/IProfilerBackend.cs#L5)

```csharp
public interface IProfilerBackend
{
    CMakeWorkflow.Profiling.Capture.ProfileCapabilities Capabilities { get; }
    System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Capture.ProfileResult> CaptureAsync(CMakeWorkflow.Profiling.Capture.ProfileRequest request, System.Threading.CancellationToken cancellationToken);
}
```

#### `ProfileArtifactHash`

Source: [CMakeWorkflow.Profiling/Capture/ProfileArtifactHash.cs](../CMakeWorkflow.Profiling/Capture/ProfileArtifactHash.cs#L9)

```csharp
public static class ProfileArtifactHash
{
    public static System.Threading.Tasks.Task<string> Sha256Async(string path, System.Threading.CancellationToken ct);
}
```

#### `ProfileCapabilities`

Source: [CMakeWorkflow.Profiling/Capture/ProfileCapabilities.cs](../CMakeWorkflow.Profiling/Capture/ProfileCapabilities.cs#L5)

```csharp
public sealed record ProfileCapabilities(string Backend, System.Collections.Generic.IReadOnlyList<string> Modes, string EventSource, string LaunchModel, string StartGuarantee, string Attribution, System.Collections.Generic.IReadOnlyList<string> RawFormats);
```

#### `ProfileProcess`

Source: [CMakeWorkflow.Profiling/Capture/ProfileProcess.cs](../CMakeWorkflow.Profiling/Capture/ProfileProcess.cs#L7)

```csharp
public sealed class ProfileProcess : CMakeWorkflow.Profiling.Capture.IProfileProcess
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Core.CommandResult> RunAsync(string executable, System.Collections.Generic.IReadOnlyList<string> arguments, string workingDirectory, System.TimeSpan timeout, System.Collections.Generic.IReadOnlyDictionary<string, string?>? environment, System.Threading.CancellationToken cancellationToken);
}
```

#### `ProfileRequest`

Source: [CMakeWorkflow.Profiling/Capture/ProfileRequest.cs](../CMakeWorkflow.Profiling/Capture/ProfileRequest.cs#L5)

```csharp
public sealed record ProfileRequest(string Executable, System.Collections.Generic.IReadOnlyList<string> Arguments, string WorkingDirectory, System.Collections.Generic.IReadOnlyDictionary<string, string> Environment, string OutputDirectory, string CaptureId, string Mode, System.TimeSpan Timeout, string? CpuSet = null, string? CpuAffinityLauncher = null);
```

#### `ProfileResult`

Source: [CMakeWorkflow.Profiling/Capture/ProfileResult.cs](../CMakeWorkflow.Profiling/Capture/ProfileResult.cs#L5)

```csharp
public sealed record ProfileResult(string Backend, string Mode, int ExitCode, string RawTrace, System.DateTimeOffset StartedAt, System.DateTimeOffset FinishedAt, string CaptureStatus, CMakeWorkflow.Profiling.Capture.ProfileCapabilities Capabilities, string Output, int? TargetProcessId = null, System.DateTimeOffset? TargetStartedAt = null, System.DateTimeOffset? TargetFinishedAt = null);
```

#### `ProfileSession`

Source: [CMakeWorkflow.Profiling/Capture/ProfileSession.cs](../CMakeWorkflow.Profiling/Capture/ProfileSession.cs#L9)

```csharp
public sealed class ProfileSession(string outputDirectory, string captureId)
{
    public System.Threading.Tasks.Task StartAsync(CMakeWorkflow.Profiling.Capture.ProfileCapabilities capabilities, CMakeWorkflow.Profiling.Capture.ProfileRequest request, System.Threading.CancellationToken ct);
    public System.Threading.Tasks.Task CompleteAsync(CMakeWorkflow.Profiling.Capture.ProfileResult result, System.Threading.CancellationToken ct);
    public System.Threading.Tasks.Task FailAsync(System.Exception error, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Profiling.Historical`

#### `DagFlowHistoricalCapture`

Source: [CMakeWorkflow.Profiling/Historical/DagFlowHistoricalCapture.cs](../CMakeWorkflow.Profiling/Historical/DagFlowHistoricalCapture.cs#L15)

```csharp
public static class DagFlowHistoricalCapture
{
    public static System.Threading.Tasks.Task<int> RunAsync(CMakeWorkflow.Profiling.Historical.DagFlowHistoricalOptions options, System.Threading.CancellationToken ct);
}
```

#### `DagFlowHistoricalFlamegraph`

Source: [CMakeWorkflow.Profiling/Historical/DagFlowHistoricalFlamegraph.cs](../CMakeWorkflow.Profiling/Historical/DagFlowHistoricalFlamegraph.cs#L12)

```csharp
public static class DagFlowHistoricalFlamegraph
{
    public static System.Text.Json.Nodes.JsonObject Parse(System.IO.TextReader text, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject> DecodeAsync(string perfData, string output, System.Threading.CancellationToken ct);
    public static string Svg(System.Text.Json.Nodes.JsonObject tree, string title);
}
```

#### `DagFlowHistoricalOptions`

Historical capture inputs; schedule randomization is independent of trial counts.

Omitted scheduling options retain the archived Python ordering. Seeds identify separate experiments, not independent
performance samples. Each experiment persists its complete schedule before measurements.

Source: [CMakeWorkflow.Profiling/Historical/DagFlowHistoricalOptions.cs](../CMakeWorkflow.Profiling/Historical/DagFlowHistoricalOptions.cs#L20)

```csharp
public sealed record DagFlowHistoricalOptions(string SourceDirectory, string OutputDirectory, int TimingTrials = 5, int StatTrials = 3, int Jobs = 4, bool Resume = false, string ShuffleAlgorithm = "python-legacy", System.Collections.Generic.IReadOnlyList<uint>? Seeds = null, string ScheduleStrategy = "random-interleaved", string ShuffleScope = "block");
```

#### `DagFlowHistoricalReport`

Source: [CMakeWorkflow.Profiling/Historical/DagFlowHistoricalReport.cs](../CMakeWorkflow.Profiling/Historical/DagFlowHistoricalReport.cs#L12)

```csharp
public static class DagFlowHistoricalReport
{
    public static System.Threading.Tasks.Task<int> RenderAsync(string directory, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Profiling`

#### `ProfileCommand`

Source: [CMakeWorkflow.Profiling/ProfileCommand.cs](../CMakeWorkflow.Profiling/ProfileCommand.cs#L12)

```csharp
public static class ProfileCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct, System.Action<string>? onRunDirectory = null);
}
```

### Namespace `CMakeWorkflow.Profiling.Replay`

#### `ProfileReplayCommand`

Source: [CMakeWorkflow.Profiling/Replay/ProfileReplayCommand.cs](../CMakeWorkflow.Profiling/Replay/ProfileReplayCommand.cs#L15)

```csharp
public static class ProfileReplayCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct, System.Action<string>? onRunDirectory = null);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Profiling.Replay.ProfileReplayOutcome> ReplayAsync(CMakeWorkflow.Profiling.Replay.ProfileReplayOptions request, System.Threading.CancellationToken ct, System.Action<string>? onRunDirectory = null, System.Action<string>? pathGuard = null);
}
```

#### `ProfileReplayOptions`

Source: [CMakeWorkflow.Profiling/Replay/ProfileReplayOptions.cs](../CMakeWorkflow.Profiling/Replay/ProfileReplayOptions.cs#L5)

```csharp
public sealed record ProfileReplayOptions(string BaselineDirectory, string CandidateDirectory, string NodeId, string? CaseName = null, int HarnessIndex = 0, string Backend = "auto", string ResultsDirectory = "out/profiles", double TimeoutSeconds = 300);
```

#### `ProfileReplayOutcome`

Source: [CMakeWorkflow.Profiling/Replay/ProfileReplayOutcome.cs](../CMakeWorkflow.Profiling/Replay/ProfileReplayOutcome.cs#L5)

```csharp
public sealed record ProfileReplayOutcome(int SchemaVersion, string RunDirectory, string AnalysisFile);
```

### Namespace `CMakeWorkflow.Profiling.Workflows`

#### `ProfileRecipeBuilder`

Validates a versioned profile recipe and derives platform-specific capture dependencies.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.Profiling/Workflows/ProfileRecipeBuilder.cs](../CMakeWorkflow.Profiling/Workflows/ProfileRecipeBuilder.cs#L16)

```csharp
public static class ProfileRecipeBuilder
{
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(CMakeWorkflow.Contracts.Recipes.ProfileRecipeV1 recipe);
}
```

#### `ProfileWorkflowBuilder`

Creates configured/adopted build contexts, serial timings and exclusive profiler captures.

Pure in-memory graph construction: no filesystem access, build, target execution or profiler launch. The caller owns
manifest persistence, Host path authorization, planning and durable admission. At execution time, builds/configuration
can mutate native trees and run/profile/test commands can have external effects. Recovery must use verified checkpoints
and must not replay uncertain running nodes blindly.

Source: [CMakeWorkflow.Profiling/Workflows/ProfileWorkflowBuilder.cs](../CMakeWorkflow.Profiling/Workflows/ProfileWorkflowBuilder.cs#L20)

```csharp
public static class ProfileWorkflowBuilder
{
    public static CMakeWorkflow.Contracts.Workflows.WorkflowDocument Build(System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Contracts.Recipes.ProfileVariant> variants, string target, string? sourceDirectory, string? configuration, string? configurePreset, int repeats, double timeoutSeconds, System.Collections.Generic.IReadOnlyList<string> arguments, string backend = "auto", string mode = "cpu", string? capturePlatform = null);
}
```

#### `ProfileWorkflowExecutor`

Source: [CMakeWorkflow.Profiling/Workflows/ProfileWorkflowExecutor.cs](../CMakeWorkflow.Profiling/Workflows/ProfileWorkflowExecutor.cs#L9)

```csharp
public sealed class ProfileWorkflowExecutor(CMakeWorkflow.Profiling.Capture.IProfileProcess? process = null) : CMakeWorkflow.Core.IWorkflowOperationExecutor
{
    public bool Supports(string kind);
    public System.Threading.Tasks.Task<CMakeWorkflow.Core.WorkflowOperationResult> ExecuteAsync(CMakeWorkflow.Core.WorkflowOperationRequest request, System.Threading.CancellationToken cancellationToken);
}
```

## CMakeWorkflow.History

### Namespace `CMakeWorkflow.History.Analysis`

#### `HistoryComparison`

Source: [CMakeWorkflow.History/Analysis/HistoryComparison.cs](../CMakeWorkflow.History/Analysis/HistoryComparison.cs#L8)

```csharp
public static class HistoryComparison
{
    public static System.Collections.Generic.List<CMakeWorkflow.History.Analysis.HistoryDifference> Compare(CMakeWorkflow.History.Models.HistoryDetail baseline, CMakeWorkflow.History.Models.HistoryDetail candidate);
}
```

#### `HistoryDetection`

Source: [CMakeWorkflow.History/Analysis/HistoryDetection.cs](../CMakeWorkflow.History/Analysis/HistoryDetection.cs#L5)

```csharp
public sealed record HistoryDetection(string Metric, string Unit, string? Variant, string? Scenario, string Method, string Status, string Reason, CMakeWorkflow.History.Analysis.HistoryStats? Baseline, CMakeWorkflow.History.Analysis.HistoryStats? Candidate, double? AbsoluteChange, double? PercentChange, double? PValue, double? AdjustedPValue, int? Permutations, bool? Exact);
```

#### `HistoryDetectionReport`

Source: [CMakeWorkflow.History/Analysis/HistoryDetectionReport.cs](../CMakeWorkflow.History/Analysis/HistoryDetectionReport.cs#L6)

```csharp
public sealed record HistoryDetectionReport(int SchemaVersion, string BaselineRun, string CandidateRun, string Metric, string Direction, double MinimumEffectPercent, double Alpha, string Test, System.Collections.Generic.List<CMakeWorkflow.History.Analysis.HistoryDetection> Results);
```

#### `HistoryDetector`

Source: [CMakeWorkflow.History/Analysis/HistoryDetector.cs](../CMakeWorkflow.History/Analysis/HistoryDetector.cs#L12)

```csharp
public static class HistoryDetector
{
    public static CMakeWorkflow.History.Analysis.HistoryDetectionReport Detect(CMakeWorkflow.History.Models.HistoryDetail baseline, CMakeWorkflow.History.Models.HistoryDetail candidate, string metric, string? variant, string? scenario, string direction, double minimumEffectPercent, double alpha, System.Threading.CancellationToken ct);
}
```

#### `HistoryDifference`

Source: [CMakeWorkflow.History/Analysis/HistoryDifference.cs](../CMakeWorkflow.History/Analysis/HistoryDifference.cs#L5)

```csharp
public sealed record HistoryDifference(string Metric, string Unit, string? Variant, string? Scenario, string Method, string Status, string? Reason, CMakeWorkflow.History.Analysis.HistoryStats? Baseline, CMakeWorkflow.History.Analysis.HistoryStats? Candidate, double? AbsoluteChange, double? PercentChange);
```

#### `HistoryStats`

Source: [CMakeWorkflow.History/Analysis/HistoryStats.cs](../CMakeWorkflow.History/Analysis/HistoryStats.cs#L5)

```csharp
public sealed record HistoryStats(int Count, double Median, double Min, double Max, double Mean, double StandardDeviation);
```

### Namespace `CMakeWorkflow.History`

#### `HistoryCommand`

Source: [CMakeWorkflow.History/HistoryCommand.cs](../CMakeWorkflow.History/HistoryCommand.cs#L9)

```csharp
public static class HistoryCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

#### `HistoryRunPage`

Workspace-authorized, keyset-paginated run history.

Source: [CMakeWorkflow.History/HistoryRunPage.cs](../CMakeWorkflow.History/HistoryRunPage.cs#L7)

```csharp
public sealed record HistoryRunPage(int SchemaVersion, System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryRun> Items, string? NextCursor);
```

### Namespace `CMakeWorkflow.History.Models`

#### `HistoryArtifact`

Source: [CMakeWorkflow.History/Models/HistoryArtifact.cs](../CMakeWorkflow.History/Models/HistoryArtifact.cs#L5)

```csharp
public sealed record HistoryArtifact(string RunId, string? NodeId, string Type, string Path, string? Sha256, string? Metadata, bool Available = true);
```

#### `HistoryBuildEnvironment`

Source: [CMakeWorkflow.History/Models/HistoryBuildEnvironment.cs](../CMakeWorkflow.History/Models/HistoryBuildEnvironment.cs#L5)

```csharp
public sealed record HistoryBuildEnvironment(string Directory, string? Configuration, string? Generator, string? Compiler, string? CompilerId, string? CompilerVersion, string? Flags, string Fingerprint);
```

#### `HistoryDetail`

Source: [CMakeWorkflow.History/Models/HistoryDetail.cs](../CMakeWorkflow.History/Models/HistoryDetail.cs#L6)

```csharp
public sealed record HistoryDetail(CMakeWorkflow.History.Models.HistoryRun Run, System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryNode> Nodes, System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement> Measurements, System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryArtifact> Artifacts);
```

#### `HistoryHostEnvironment`

Source: [CMakeWorkflow.History/Models/HistoryHostEnvironment.cs](../CMakeWorkflow.History/Models/HistoryHostEnvironment.cs#L5)

```csharp
public sealed record HistoryHostEnvironment(string? HostId, string? CpuModel, string? Architecture, int? LogicalProcessors, int? AvailableProcessors, int? PhysicalPackages, int? PhysicalCores, string? OperatingSystem, string? KernelVersion, string? Affinity, string? Governor, string? Fingerprint);
```

#### `HistoryImportResult`

Source: [CMakeWorkflow.History/Models/HistoryImportResult.cs](../CMakeWorkflow.History/Models/HistoryImportResult.cs#L5)

```csharp
public sealed record HistoryImportResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
```

#### `HistoryMeasurement`

Source: [CMakeWorkflow.History/Models/HistoryMeasurement.cs](../CMakeWorkflow.History/Models/HistoryMeasurement.cs#L5)

```csharp
public sealed record HistoryMeasurement(string RunId, string? NodeId, string Metric, string Unit, double Value, string? Variant, string? Scenario, string Source, string Method, int SampleIndex, string? Workload, string? Environment);
```

#### `HistoryNode`

Source: [CMakeWorkflow.History/Models/HistoryNode.cs](../CMakeWorkflow.History/Models/HistoryNode.cs#L5)

```csharp
public sealed record HistoryNode(string RunId, string Id, string Kind, string Status, double? DurationSeconds, string? Dependencies, string? BuildDirectory, string? Configuration, string? Target);
```

#### `HistoryRun`

Source: [CMakeWorkflow.History/Models/HistoryRun.cs](../CMakeWorkflow.History/Models/HistoryRun.cs#L6)

```csharp
public sealed record HistoryRun(string Id, string Directory, string Format, string? Workflow, string? ManifestHash, string? StartedAt, string? FinishedAt, string Status, string? Revision, string SourceHash, bool SourceAvailable = true, string? HostId = null, string? EnvironmentFingerprint = null, CMakeWorkflow.History.Models.HistoryHostEnvironment? Environment = null, System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryBuildEnvironment>? BuildEnvironments = null);
```

### Namespace `CMakeWorkflow.History.Storage`

#### `HistoryStore`

Source: [CMakeWorkflow.History/Storage/HistoryStore.cs](../CMakeWorkflow.History/Storage/HistoryStore.cs#L13)

```csharp
public sealed class HistoryStore : System.IDisposable
{
    public static string DefaultDatabasePath { get; }
    public HistoryStore(string path, bool readOnly = false);
    public string Import(string directory, bool recursive, System.Action<CMakeWorkflow.History.Models.HistoryImportResult>? onComplete = null, System.Action<string>? pathGuard = null);
    public System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryRun> Runs(int limit, string? status, string? since, string? until, string? workflow, string? variant, System.Func<string, bool>? includeDirectory = null, System.Threading.CancellationToken ct = default);
    public CMakeWorkflow.History.HistoryRunPage RunsPage(string databaseIdentity, int limit, string? status, string? since, string? until, string? workflow, string? variant, string? cursor, System.Func<string, bool>? includeDirectory = null, System.Threading.CancellationToken ct = default);
    public CMakeWorkflow.History.Models.HistoryDetail Show(string id, System.Action<string>? validateDirectory = null, int maxMeasurements = 2147483647, System.Func<string, bool>? artifactAllowed = null, System.Threading.CancellationToken ct = default);
    public System.Collections.Generic.List<CMakeWorkflow.History.Models.HistoryMeasurement> Metrics(string? run, string? metric, string? variant, string? scenario, System.Func<string, bool>? includeDirectory = null, int maxRows = 2147483647, System.Threading.CancellationToken ct = default);
    public void Dispose();
}
```

## CMakeWorkflow.Tooling

### Namespace `CMakeWorkflow.Tooling.Environment`

#### `EtwProbeResult`

Source: [CMakeWorkflow.Tooling/Environment/EtwProbeResult.cs](../CMakeWorkflow.Tooling/Environment/EtwProbeResult.cs#L5)

```csharp
public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);
```

#### `HostFinding`

Source: [CMakeWorkflow.Tooling/Environment/HostFinding.cs](../CMakeWorkflow.Tooling/Environment/HostFinding.cs#L5)

```csharp
public sealed record HostFinding(string Id, string Status, string? Observed, string Expected, string? Diagnostic);
```

#### `HostPreparation`

Source: [CMakeWorkflow.Tooling/Environment/HostPreparation.cs](../CMakeWorkflow.Tooling/Environment/HostPreparation.cs#L11)

```csharp
public static class HostPreparation
{
    public static CMakeWorkflow.Tooling.Environment.HostRequirements ReadRequirements(string path);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationPlan> InspectAsync(string path, CMakeWorkflow.Tooling.Tools.ToolingService service, CMakeWorkflow.Tooling.Tools.PackageManagerKind packageManager, string? installDirectory, System.Threading.CancellationToken ct, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend? backend = null, System.Collections.Generic.IReadOnlyList<string>? cpuPaths = null, CMakeWorkflow.Tooling.Toolchains.IWindowsDevelopmentProbe? windowsProbe = null);
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostFinding> InspectMachine(CMakeWorkflow.Tooling.Environment.HostRequirements requirements, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend? backend = null, System.Collections.Generic.IReadOnlyList<string>? cpuPaths = null);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Environment.HostPreparationResult> ApplyAsync(CMakeWorkflow.Tooling.Environment.HostPreparationPlan approved, CMakeWorkflow.Tooling.Tools.ToolingService service, CMakeWorkflow.Tooling.Tools.PackageManagerKind packageManager, string? installDirectory, System.Threading.CancellationToken ct, bool allowSystemChanges = false, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend? backend = null, System.Collections.Generic.IReadOnlyList<string>? cpuPaths = null, string? auditDirectory = null, CMakeWorkflow.Tooling.Toolchains.IWindowsDevelopmentProbe? windowsProbe = null);
}
```

#### `HostPreparationPlan`

Source: [CMakeWorkflow.Tooling/Environment/HostPreparationPlan.cs](../CMakeWorkflow.Tooling/Environment/HostPreparationPlan.cs#L7)

```csharp
public sealed record HostPreparationPlan(int SchemaVersion, string RequirementsSha256, string Source, CMakeWorkflow.Tooling.Tools.ToolingPlan Tools, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostFinding> Findings, bool Ready, string ApprovalSha256, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange>? SystemChanges = null, System.Collections.Generic.IReadOnlyList<string>? CpuPolicyPaths = null);
```

#### `HostPreparationResult`

Source: [CMakeWorkflow.Tooling/Environment/HostPreparationResult.cs](../CMakeWorkflow.Tooling/Environment/HostPreparationResult.cs#L7)

```csharp
public sealed record HostPreparationResult(int SchemaVersion, bool Ready, CMakeWorkflow.Tooling.Tools.ToolingResult? AppliedTools, CMakeWorkflow.Tooling.Environment.HostPreparationPlan Checked, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange>? AppliedSystemChanges = null, string? AuditPath = null);
```

#### `HostRequirements`

Source: [CMakeWorkflow.Tooling/Environment/HostRequirements.cs](../CMakeWorkflow.Tooling/Environment/HostRequirements.cs#L7)

```csharp
public sealed record HostRequirements(int SchemaVersion, string[] ToolProfiles, int? MaxPerfEventParanoid = null, string? CpuGovernor = null, string? WindowsPowerScheme = null, bool RequireWindowsMsvc = false, bool RequireWindowsSdk = false, bool RequireWindowsWpr = false);
```

#### `HostSettingChange`

Source: [CMakeWorkflow.Tooling/Environment/HostSettingChange.cs](../CMakeWorkflow.Tooling/Environment/HostSettingChange.cs#L6)

```csharp
public sealed record HostSettingChange(string Id, string Path, string? Before, string After);
```

#### `HostSettingsProvisioner`

Source: [CMakeWorkflow.Tooling/Environment/HostSettingsProvisioner.cs](../CMakeWorkflow.Tooling/Environment/HostSettingsProvisioner.cs#L7)

```csharp
public static class HostSettingsProvisioner
{
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange> Plan(CMakeWorkflow.Tooling.Environment.HostRequirements requirements, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend backend, System.Collections.Generic.IReadOnlyList<string>? cpuPaths = null);
    public static System.Collections.Generic.IReadOnlyList<string> EnumerateCpuPaths();
    public static void Apply(System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange> changes, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend backend, System.Threading.CancellationToken ct);
}
```

#### `IHostSettingsBackend`

Source: [CMakeWorkflow.Tooling/Environment/IHostSettingsBackend.cs](../CMakeWorkflow.Tooling/Environment/IHostSettingsBackend.cs#L5)

```csharp
public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}
```

#### `IWindowsEtwProbe`

Source: [CMakeWorkflow.Tooling/Environment/IWindowsEtwProbe.cs](../CMakeWorkflow.Tooling/Environment/IWindowsEtwProbe.cs#L5)

```csharp
public interface IWindowsEtwProbe
{
    CMakeWorkflow.Tooling.Environment.EtwProbeResult Probe(string capability, System.Threading.CancellationToken cancellationToken);
}
```

#### `LinuxHostSettingsBackend`

Source: [CMakeWorkflow.Tooling/Environment/LinuxHostSettingsBackend.cs](../CMakeWorkflow.Tooling/Environment/LinuxHostSettingsBackend.cs#L5)

```csharp
public sealed class LinuxHostSettingsBackend : CMakeWorkflow.Tooling.Environment.IHostSettingsBackend
{
    public string? Read(string path);
    public void Write(string path, string value);
}
```

#### `NativeWindowsEtwProbe`

Explicit, short-lived ETW session admission probe; never stops sessions it does not own.

Source: [CMakeWorkflow.Tooling/Environment/NativeWindowsEtwProbe.cs](../CMakeWorkflow.Tooling/Environment/NativeWindowsEtwProbe.cs#L9)

```csharp
public sealed class NativeWindowsEtwProbe : CMakeWorkflow.Tooling.Environment.IWindowsEtwProbe
{
    public CMakeWorkflow.Tooling.Environment.EtwProbeResult Probe(string capability, System.Threading.CancellationToken cancellationToken);
}
```

#### `ProvisioningAuditEntry`

Source: [CMakeWorkflow.Tooling/Environment/ProvisioningAuditEntry.cs](../CMakeWorkflow.Tooling/Environment/ProvisioningAuditEntry.cs#L5)

```csharp
public sealed record ProvisioningAuditEntry(System.DateTimeOffset At, string ApprovalSha256, string RequirementsSha256, string Status, System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange> Changes, string? Error);
```

#### `WindowsHostSettingsProvisioner`

Source: [CMakeWorkflow.Tooling/Environment/WindowsHostSettingsProvisioner.cs](../CMakeWorkflow.Tooling/Environment/WindowsHostSettingsProvisioner.cs#L7)

```csharp
public static class WindowsHostSettingsProvisioner
{
    public const string SchemePath = "windows/power/active-scheme";
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange> Plan(CMakeWorkflow.Tooling.Environment.HostRequirements requirements, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend backend);
    public static void Apply(System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostSettingChange> changes, CMakeWorkflow.Tooling.Environment.IHostSettingsBackend backend, System.Threading.CancellationToken ct);
}
```

#### `WindowsPowerSettingsBackend`

Source: [CMakeWorkflow.Tooling/Environment/WindowsPowerSettingsBackend.cs](../CMakeWorkflow.Tooling/Environment/WindowsPowerSettingsBackend.cs#L5)

```csharp
public sealed class WindowsPowerSettingsBackend : CMakeWorkflow.Tooling.Environment.IHostSettingsBackend
{
    public string? Read(string path);
    public void Write(string path, string value);
}
```

### Namespace `CMakeWorkflow.Tooling`

#### `EnvironmentCommand`

Source: [CMakeWorkflow.Tooling/EnvironmentCommand.cs](../CMakeWorkflow.Tooling/EnvironmentCommand.cs#L7)

```csharp
public static class EnvironmentCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

#### `ToolingCommand`

Source: [CMakeWorkflow.Tooling/ToolingCommand.cs](../CMakeWorkflow.Tooling/ToolingCommand.cs#L6)

```csharp
public static class ToolingCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Tooling.Toolchains`

#### `DeveloperEnvironmentRequest`

Source: [CMakeWorkflow.Tooling/Toolchains/DeveloperEnvironmentRequest.cs](../CMakeWorkflow.Tooling/Toolchains/DeveloperEnvironmentRequest.cs#L5)

```csharp
public sealed record DeveloperEnvironmentRequest(string EnvironmentProfileId, string HostArchitecture = "x64", string TargetArchitecture = "x64", string? WindowsSdkVersion = null, string? ToolsetVersion = null);
```

#### `DeveloperEnvironmentSnapshot`

Source: [CMakeWorkflow.Tooling/Toolchains/DeveloperEnvironmentSnapshot.cs](../CMakeWorkflow.Tooling/Toolchains/DeveloperEnvironmentSnapshot.cs#L6)

```csharp
public sealed record DeveloperEnvironmentSnapshot(int SchemaVersion, string EnvironmentProfileId, string HostArchitecture, string TargetArchitecture, string Fingerprint, System.Collections.Generic.Dictionary<string, string> VisibleVariables, System.Collections.Generic.List<string> Diagnostics);
```

#### `IWindowsDevelopmentProbe`

Source: [CMakeWorkflow.Tooling/Toolchains/IWindowsDevelopmentProbe.cs](../CMakeWorkflow.Tooling/Toolchains/IWindowsDevelopmentProbe.cs#L6)

```csharp
public interface IWindowsDevelopmentProbe
{
    string? MsvcToolset();
    string? WindowsSdk();
    string? WindowsPerformanceRecorder();
}
```

#### `SystemWindowsDevelopmentProbe`

Source: [CMakeWorkflow.Tooling/Toolchains/SystemWindowsDevelopmentProbe.cs](../CMakeWorkflow.Tooling/Toolchains/SystemWindowsDevelopmentProbe.cs#L8)

```csharp
public sealed class SystemWindowsDevelopmentProbe : CMakeWorkflow.Tooling.Toolchains.IWindowsDevelopmentProbe
{
    public string? MsvcToolset();
    public string? WindowsSdk();
    public string? WindowsPerformanceRecorder();
}
```

#### `ToolchainCandidate`

Source: [CMakeWorkflow.Tooling/Toolchains/ToolchainCandidate.cs](../CMakeWorkflow.Tooling/Toolchains/ToolchainCandidate.cs#L7)

```csharp
public sealed record ToolchainCandidate(string Id, string Role, string Flavor, string Provider, string Path, string? Version = null, string? VisualStudioInstanceId = null, string? EnvironmentProfileId = null);
```

#### `ToolchainInventory`

Source: [CMakeWorkflow.Tooling/Toolchains/ToolchainInventory.cs](../CMakeWorkflow.Tooling/Toolchains/ToolchainInventory.cs#L6)

```csharp
public sealed record ToolchainInventory(int SchemaVersion, string Platform, System.Collections.Generic.List<CMakeWorkflow.Tooling.Toolchains.VisualStudioInstance> VisualStudio, System.Collections.Generic.List<CMakeWorkflow.Tooling.Toolchains.ToolchainCandidate> Candidates, System.Collections.Generic.List<string> Diagnostics);
```

#### `ToolchainResolution`

Source: [CMakeWorkflow.Tooling/Toolchains/ToolchainResolution.cs](../CMakeWorkflow.Tooling/Toolchains/ToolchainResolution.cs#L6)

```csharp
public sealed record ToolchainResolution(int SchemaVersion, string Status, CMakeWorkflow.Tooling.Toolchains.ToolchainCandidate? CMake, CMakeWorkflow.Tooling.Toolchains.ToolchainCandidate? Ninja, CMakeWorkflow.Tooling.Toolchains.ToolchainCandidate? Compiler, CMakeWorkflow.Tooling.Toolchains.ToolchainCandidate? Sdk, string? EnvironmentProfileId, System.Collections.Generic.List<string> Diagnostics);
```

#### `ToolchainSelection`

Source: [CMakeWorkflow.Tooling/Toolchains/ToolchainSelection.cs](../CMakeWorkflow.Tooling/Toolchains/ToolchainSelection.cs#L7)

```csharp
public sealed record ToolchainSelection(string? CMakeCandidateId = null, string? NinjaCandidateId = null, string? CompilerCandidateId = null, string? SdkCandidateId = null, string? EnvironmentProfileId = null, string Generator = "Ninja");
```

#### `VisualStudioInstance`

Source: [CMakeWorkflow.Tooling/Toolchains/VisualStudioInstance.cs](../CMakeWorkflow.Tooling/Toolchains/VisualStudioInstance.cs#L5)

```csharp
public sealed record VisualStudioInstance(string Id, string Name, string Version, string InstallationPath, string? DeveloperCommandPath);
```

#### `WindowsDevelopmentDoctor`

Source: [CMakeWorkflow.Tooling/Toolchains/WindowsDevelopmentDoctor.cs](../CMakeWorkflow.Tooling/Toolchains/WindowsDevelopmentDoctor.cs#L7)

```csharp
public static class WindowsDevelopmentDoctor
{
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Environment.HostFinding> Inspect(CMakeWorkflow.Tooling.Environment.HostRequirements requirements, CMakeWorkflow.Tooling.Toolchains.IWindowsDevelopmentProbe probe);
}
```

#### `WindowsToolchains`

Source: [CMakeWorkflow.Tooling/Toolchains/WindowsToolchains.cs](../CMakeWorkflow.Tooling/Toolchains/WindowsToolchains.cs#L12)

```csharp
public static class WindowsToolchains
{
    public static string CandidateId(string role, string path);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.ToolchainInventory> DiscoverAsync(System.Threading.CancellationToken ct);
    public static CMakeWorkflow.Tooling.Toolchains.ToolchainResolution Resolve(CMakeWorkflow.Tooling.Toolchains.ToolchainInventory inventory, CMakeWorkflow.Tooling.Toolchains.ToolchainSelection selection);
    public static System.Collections.Generic.Dictionary<string, string> ParseVisibleEnvironment(string output);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Toolchains.DeveloperEnvironmentSnapshot> InspectEnvironmentAsync(CMakeWorkflow.Tooling.Toolchains.ToolchainInventory inventory, CMakeWorkflow.Tooling.Toolchains.DeveloperEnvironmentRequest request, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Tooling.Tools`

#### `ICMakeInstaller`

Source: [CMakeWorkflow.Tooling/Tools/ICMakeInstaller.cs](../CMakeWorkflow.Tooling/Tools/ICMakeInstaller.cs#L5)

```csharp
public interface ICMakeInstaller
{
    System.Threading.Tasks.Task InstallAsync(string installDirectory, System.Threading.CancellationToken ct);
}
```

#### `IToolingRuntime`

Source: [CMakeWorkflow.Tooling/Tools/IToolingRuntime.cs](../CMakeWorkflow.Tooling/Tools/IToolingRuntime.cs#L5)

```csharp
public interface IToolingRuntime
{
    CMakeWorkflow.Tooling.Tools.ToolPlatform Platform { get; }
    bool IsAdministrator { get; }
    string? Resolve(string command, string installDirectory);
    System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolCommandResult> RunAsync(string executable, System.Collections.Generic.IReadOnlyList<string> arguments, System.TimeSpan timeout, System.Threading.CancellationToken ct);
}
```

#### `PackageManagerKind`

Source: [CMakeWorkflow.Tooling/Tools/PackageManagerKind.cs](../CMakeWorkflow.Tooling/Tools/PackageManagerKind.cs#L5)

```csharp
public enum PackageManagerKind
{
    Auto = 0,
    None = 1,
    Apt = 2,
    Pacman = 3,
    Dnf = 4,
    Brew = 5,
    Winget = 6,
}
```

#### `SystemToolingRuntime`

Source: [CMakeWorkflow.Tooling/Tools/SystemToolingRuntime.cs](../CMakeWorkflow.Tooling/Tools/SystemToolingRuntime.cs#L8)

```csharp
public sealed class SystemToolingRuntime(string? workingDirectory = null) : CMakeWorkflow.Tooling.Tools.IToolingRuntime
{
    public CMakeWorkflow.Tooling.Tools.ToolPlatform Platform { get; }
    public bool IsAdministrator { get; }
    public string? Resolve(string command, string installDirectory);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolCommandResult> RunAsync(string executable, System.Collections.Generic.IReadOnlyList<string> arguments, System.TimeSpan timeout, System.Threading.CancellationToken ct);
}
```

#### `ToolCatalog`

Source: [CMakeWorkflow.Tooling/Tools/ToolCatalog.cs](../CMakeWorkflow.Tooling/Tools/ToolCatalog.cs#L5)

```csharp
public static class ToolCatalog
{
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Tools.ToolProfile> AllProfiles { get; }
    public static CMakeWorkflow.Tooling.Tools.ToolDefinition GetTool(string id);
    public static System.Collections.Generic.IReadOnlyList<CMakeWorkflow.Tooling.Tools.ToolDefinition> ResolveProfiles(System.Collections.Generic.IEnumerable<string> profileIds, CMakeWorkflow.Tooling.Tools.ToolPlatform platform);
}
```

#### `ToolCommandResult`

Source: [CMakeWorkflow.Tooling/Tools/ToolCommandResult.cs](../CMakeWorkflow.Tooling/Tools/ToolCommandResult.cs#L5)

```csharp
public sealed record ToolCommandResult(int ExitCode, string Output, string Error);
```

#### `ToolDefinition`

Source: [CMakeWorkflow.Tooling/Tools/ToolDefinition.cs](../CMakeWorkflow.Tooling/Tools/ToolDefinition.cs#L5)

```csharp
public sealed record ToolDefinition(string Id, string DisplayName, string[] Commands, string[] ProbeArguments, System.Version? MinimumVersion, System.Collections.Generic.IReadOnlyDictionary<CMakeWorkflow.Tooling.Tools.PackageManagerKind, string[]> Packages, bool StandaloneCMake = false, string? ManualHint = null);
```

#### `ToolPlatform`

Source: [CMakeWorkflow.Tooling/Tools/ToolPlatform.cs](../CMakeWorkflow.Tooling/Tools/ToolPlatform.cs#L5)

```csharp
public enum ToolPlatform
{
    Linux = 0,
    MacOS = 1,
    Windows = 2,
}
```

#### `ToolProfile`

Source: [CMakeWorkflow.Tooling/Tools/ToolProfile.cs](../CMakeWorkflow.Tooling/Tools/ToolProfile.cs#L5)

```csharp
public sealed record ToolProfile(string Id, string Description, string[] Tools);
```

#### `ToolStatus`

Source: [CMakeWorkflow.Tooling/Tools/ToolStatus.cs](../CMakeWorkflow.Tooling/Tools/ToolStatus.cs#L5)

```csharp
public sealed record ToolStatus(string Id, string DisplayName, bool Installed, string? Command, string? Version, string? MinimumVersion, string? Diagnostic);
```

#### `ToolingExecutionProgress`

Source: [CMakeWorkflow.Tooling/Tools/ToolingExecutionProgress.cs](../CMakeWorkflow.Tooling/Tools/ToolingExecutionProgress.cs#L5)

```csharp
public sealed record ToolingExecutionProgress(string Tool, string Stage, string? Message);
```

#### `ToolingJson`

Source: [CMakeWorkflow.Tooling/Tools/ToolingJson.cs](../CMakeWorkflow.Tooling/Tools/ToolingJson.cs#L5)

```csharp
public class ToolingJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
```

#### `ToolingPlan`

Source: [CMakeWorkflow.Tooling/Tools/ToolingPlan.cs](../CMakeWorkflow.Tooling/Tools/ToolingPlan.cs#L6)

```csharp
public sealed record ToolingPlan(int SchemaVersion, CMakeWorkflow.Tooling.Tools.ToolPlatform Platform, string[] Profiles, string InstallDirectory, CMakeWorkflow.Tooling.Tools.PackageManagerKind PackageManager, bool Ready, System.Collections.Generic.List<CMakeWorkflow.Tooling.Tools.ToolStatus> Tools, System.Collections.Generic.List<CMakeWorkflow.Tooling.Tools.ToolingStep> Steps);
```

#### `ToolingProfileList`

Source: [CMakeWorkflow.Tooling/Tools/ToolingProfileList.cs](../CMakeWorkflow.Tooling/Tools/ToolingProfileList.cs#L6)

```csharp
public sealed record ToolingProfileList(int SchemaVersion, System.Collections.Generic.List<CMakeWorkflow.Tooling.Tools.ToolingProfileView> Profiles);
```

#### `ToolingProfileView`

Source: [CMakeWorkflow.Tooling/Tools/ToolingProfileView.cs](../CMakeWorkflow.Tooling/Tools/ToolingProfileView.cs#L5)

```csharp
public sealed record ToolingProfileView(string Id, string Description, string[] Tools);
```

#### `ToolingResult`

Source: [CMakeWorkflow.Tooling/Tools/ToolingResult.cs](../CMakeWorkflow.Tooling/Tools/ToolingResult.cs#L5)

```csharp
public sealed record ToolingResult(int SchemaVersion, bool Success, string[] Ready, string[] Failed, string[] Diagnostics);
```

#### `ToolingService`

Source: [CMakeWorkflow.Tooling/Tools/ToolingService.cs](../CMakeWorkflow.Tooling/Tools/ToolingService.cs#L7)

```csharp
public sealed class ToolingService(CMakeWorkflow.Tooling.Tools.IToolingRuntime runtime, CMakeWorkflow.Tooling.Tools.ICMakeInstaller installer)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolingPlan> PlanAsync(System.Collections.Generic.IReadOnlyList<string> requestedProfiles, CMakeWorkflow.Tooling.Tools.PackageManagerKind requestedManager, bool allowFallback, string? installDirectory, System.Threading.CancellationToken ct, System.Func<CMakeWorkflow.Tooling.Tools.ToolStatus, System.Threading.Tasks.Task>? onTool = null);
    public System.Threading.Tasks.Task<CMakeWorkflow.Tooling.Tools.ToolingResult> ExecuteAsync(CMakeWorkflow.Tooling.Tools.ToolingPlan plan, System.Threading.CancellationToken ct, System.Func<CMakeWorkflow.Tooling.Tools.ToolingExecutionProgress, System.Threading.Tasks.Task>? onProgress = null);
    public static System.Version? ExtractVersion(string text);
    public CMakeWorkflow.Tooling.Tools.PackageManagerKind DetectAutoManager(string directory);
    public static (string Executable, string[] Arguments) InstallCommand(CMakeWorkflow.Tooling.Tools.PackageManagerKind manager, string[] packages);
}
```

#### `ToolingStep`

Source: [CMakeWorkflow.Tooling/Tools/ToolingStep.cs](../CMakeWorkflow.Tooling/Tools/ToolingStep.cs#L5)

```csharp
public sealed record ToolingStep(string Tool, string Method, string? Executable, string[] Arguments, string? Source, string? Diagnostic);
```

#### `VerifiedCMakeInstaller`

Source: [CMakeWorkflow.Tooling/Tools/VerifiedCMakeInstaller.cs](../CMakeWorkflow.Tooling/Tools/VerifiedCMakeInstaller.cs#L11)

```csharp
public sealed class VerifiedCMakeInstaller(System.Net.Http.HttpClient? client = null) : CMakeWorkflow.Tooling.Tools.ICMakeInstaller
{
    public System.Threading.Tasks.Task InstallAsync(string installDirectory, System.Threading.CancellationToken ct);
    public static System.Net.Http.HttpClient CreateDownloadClient();
}
```

## CMakeWorkflow.Campaigns

### Namespace `CMakeWorkflow.Campaigns`

#### `CampaignCommand`

Source: [CMakeWorkflow.Campaigns/CampaignCommand.cs](../CMakeWorkflow.Campaigns/CampaignCommand.cs#L10)

```csharp
public static class CampaignCommand
{
    public static System.Threading.Tasks.Task<int> ExecuteAsync(string[] args, System.Threading.CancellationToken ct);
}
```

#### `CampaignService`

Source: [CMakeWorkflow.Campaigns/CampaignService.cs](../CMakeWorkflow.Campaigns/CampaignService.cs#L7)

```csharp
public static class CampaignService
{
    public static System.Threading.Tasks.Task<int> MutationAsync(CMakeWorkflow.Campaigns.Mutation.MutationOptions request, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<int> WeakMemoryAsync(CMakeWorkflow.Campaigns.WeakMemory.WeakMemoryOptions request, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<int> FuzzSeedAsync(CMakeWorkflow.Campaigns.FuzzCorpus.FuzzSeedOptions request, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<int> FuzzRunAsync(CMakeWorkflow.Campaigns.FuzzCorpus.FuzzRunOptions request, System.Threading.CancellationToken ct, System.Action<string>? onRunDirectory = null, System.Action<string>? pathGuard = null);
    public static System.Threading.Tasks.Task<int> FuzzTriageAsync(CMakeWorkflow.Campaigns.FuzzCorpus.FuzzTriageOptions request, System.Threading.CancellationToken ct, System.Action<string>? pathGuard = null);
    public static System.Threading.Tasks.Task<int> FuzzMergeAsync(CMakeWorkflow.Campaigns.FuzzCorpus.FuzzMergeOptions request, System.Threading.CancellationToken ct, System.Action<string>? pathGuard = null);
}
```

### Namespace `CMakeWorkflow.Campaigns.FuzzCorpus`

#### `FuzzMergeOptions`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzMergeOptions.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzMergeOptions.cs#L3)

```csharp
public sealed record FuzzMergeOptions(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);
```

#### `FuzzRunCampaign`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunCampaign.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunCampaign.cs#L8)

```csharp
public static class FuzzRunCampaign
{
    public static System.Threading.Tasks.Task<int> RunAsync(CMakeWorkflow.Campaigns.FuzzCorpus.FuzzRunOptions options, System.Threading.CancellationToken ct, System.Action<string>? onRunDirectory = null, System.Action<string>? pathGuard = null);
    public static System.Threading.Tasks.Task<int> TriageAsync(string runDirectory, System.Threading.CancellationToken ct, System.Action<string>? pathGuard = null);
    public static System.Threading.Tasks.Task<int> MergeAsync(string runDirectory, string? outputDirectory, System.Threading.CancellationToken ct);
    public static System.Threading.Tasks.Task<int> MergeAsync(string runDirectory, string? outputDirectory, bool coverage, System.Threading.CancellationToken ct, System.Action<string>? pathGuard = null);
}
```

#### `FuzzRunOptions`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunOptions.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunOptions.cs#L3)

```csharp
public sealed record FuzzRunOptions(string BuildDirectory, string Layer, string? Backend, string? CorpusDirectory, string ResultsDirectory, string? Configuration, int DurationSeconds, int InputTimeoutSeconds, int MaxLength, bool ReplayOnly);
```

#### `FuzzRunReport`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunReport.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzRunReport.cs#L3)

```csharp
public sealed class FuzzRunReport
{
    public int SchemaVersion { get; set; }
    public string Layer { get; set; }
    public string Backend { get; set; }
    public string BuildDirectory { get; set; }
    public string Target { get; set; }
    public string Executable { get; set; }
    public string ExecutableSha256 { get; set; }
    public string SourceCorpus { get; set; }
    public System.DateTimeOffset StartedAt { get; set; }
    public System.DateTimeOffset FinishedAt { get; set; }
    public int InitialInputs { get; set; }
    public int FinalInputs { get; set; }
    public int ReplayedInputs { get; set; }
    public System.Collections.Generic.List<string> FailedInputs { get; set; }
    public System.Collections.Generic.List<string> NewInputs { get; set; }
    public System.Collections.Generic.List<string> CrashArtifacts { get; set; }
    public System.Collections.Generic.Dictionary<string, string> ArtifactSha256 { get; set; }
    public bool ReplayOnly { get; set; }
    public string ReplayMechanism { get; set; }
    public int DurationSeconds { get; set; }
    public int InputTimeoutSeconds { get; set; }
    public int MaxLength { get; set; }
    public System.Collections.Generic.List<string> BuildArguments { get; set; }
    public string? CampaignTool { get; set; }
    public System.Collections.Generic.List<string> CampaignArguments { get; set; }
    public int? CampaignExitCode { get; set; }
    public bool CampaignTimedOut { get; set; }
    public bool InfrastructureError { get; set; }
    public string Status { get; set; }
    public string? Error { get; set; }
}
```

#### `FuzzSeedCampaign`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzSeedCampaign.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzSeedCampaign.cs#L6)

```csharp
public static class FuzzSeedCampaign
{
    public static System.Threading.Tasks.Task<int> RunAsync(string layer, string outputRoot, System.Threading.CancellationToken ct);
}
```

#### `FuzzSeedOptions`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzSeedOptions.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzSeedOptions.cs#L3)

```csharp
public sealed record FuzzSeedOptions(string Layer, string OutputDirectory);
```

#### `FuzzTriageOptions`

Source: [CMakeWorkflow.Campaigns/FuzzCorpus/FuzzTriageOptions.cs](../CMakeWorkflow.Campaigns/FuzzCorpus/FuzzTriageOptions.cs#L3)

```csharp
public sealed record FuzzTriageOptions(string RunDirectory);
```

### Namespace `CMakeWorkflow.Campaigns.Mutation`

#### `MutationCampaign`

Source: [CMakeWorkflow.Campaigns/Mutation/MutationCampaign.cs](../CMakeWorkflow.Campaigns/Mutation/MutationCampaign.cs#L9)

```csharp
public static class MutationCampaign
{
    public static System.Threading.Tasks.Task<int> RunAsync(string sourceRoot, string suite, string compiler, string seed, int cases, int steps, int timeoutSeconds, string reportPath, System.Threading.CancellationToken ct);
}
```

#### `MutationOptions`

Source: [CMakeWorkflow.Campaigns/Mutation/MutationOptions.cs](../CMakeWorkflow.Campaigns/Mutation/MutationOptions.cs#L4)

```csharp
public sealed record MutationOptions(string SourceDirectory, string Suite = "all", string Compiler = "c++", string Seed = "496833221358", int Cases = 48, int Steps = 48, int TimeoutSeconds = 30, string? ReportPath = null);
```

#### `QueueMutationCampaign`

Source: [CMakeWorkflow.Campaigns/Mutation/QueueMutationCampaign.cs](../CMakeWorkflow.Campaigns/Mutation/QueueMutationCampaign.cs#L7)

```csharp
public static class QueueMutationCampaign
{
    public static System.Threading.Tasks.Task<int> RunAsync(string sourceRoot, string compiler, string seed, int cases, int steps, int timeoutSeconds, string reportPath, System.Threading.CancellationToken ct);
}
```

### Namespace `CMakeWorkflow.Campaigns.WeakMemory`

#### `WeakMemoryCampaign`

Source: [CMakeWorkflow.Campaigns/WeakMemory/WeakMemoryCampaign.cs](../CMakeWorkflow.Campaigns/WeakMemory/WeakMemoryCampaign.cs#L8)

```csharp
public static class WeakMemoryCampaign
{
    public static System.Threading.Tasks.Task<int> RunAsync(string sourceRoot, string protocol, string relacyRoot, string compiler, int timeoutSeconds, string reportPath, string? mutant, System.Threading.CancellationToken ct);
}
```

#### `WeakMemoryOptions`

Source: [CMakeWorkflow.Campaigns/WeakMemory/WeakMemoryOptions.cs](../CMakeWorkflow.Campaigns/WeakMemory/WeakMemoryOptions.cs#L3)

```csharp
public sealed record WeakMemoryOptions(string SourceDirectory, string Protocol, string RelacyDirectory, string Compiler = "c++", int TimeoutSeconds = 60, string? ReportPath = null, string? Mutant = null);
```

## CMakeWorkflow

This executable has no source-declared public CLR types. Integrate through its documented CLI commands or the Host
wire protocol; the entry point and command handlers are internal implementation.
