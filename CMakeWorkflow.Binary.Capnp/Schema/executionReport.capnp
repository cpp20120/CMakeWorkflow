@0xeda0ca63491b7b9d;

# Terminal evidence binds the approved execution and its final runner observations.
struct ExecutionReport {
  version @0 :UInt16;
  workspaceId @1 :Text;
  operationId @2 :Text;
  requestDigest @3 :Text;
  workflowDocumentId @4 :Text;
  planDocumentId @5 :Text;
  dispatchIdentity @6 :Text;
  runDirectory @7 :Text;
  exitCode @8 :Int32;
  report @9 :WorkflowReport;
  operatorCancellationRequestedAt @10 :Timestamp;
}

# Pointer presence distinguishes absence from zero. Timestamp retains the original offset.
struct Timestamp { ticks @0 :Int64; offsetMinutes @1 :Int16; }
struct OptionalInt32 { value @0 :Int32; }
struct OptionalInt64 { value @0 :Int64; }
struct OptionalFloat64 { value @0 :Float64; }
struct OptionalBool { value @0 :Bool; }
struct Entry { key @0 :Text; value @1 :Text; }

struct WorkflowReport {
  schemaVersion @0 :Int32;
  workflow @1 :Text;
  manifestHash @2 :Text;
  sourceSnapshotId @3 :Text;
  startedAt @4 :Timestamp;
  finishedAt @5 :Timestamp;
  status @6 :Text;
  nodes @7 :List(NodeResult);
  error @8 :Text;
  findings @9 :List(WorkflowFinding);
  resumeCount @10 :Int32;
  recoveryPhase @11 :Text;
  reconciliations @12 :List(WorkflowReconciliation);
  environment @13 :HostEnvironment;
  environmentAtEnd @14 :HostEnvironment;
  buildEnvironments @15 :List(BuildEnvironment);
  toolchains @16 :List(WorkflowToolchainIdentity);
}

struct NodeResult {
  id @0 :Text;
  kind @1 :Text;
  benchmarkTrial @2 :BenchmarkTrial;
  dependencies @3 :List(Text);
  buildDirectory @4 :Text;
  configuration @5 :Text;
  target @6 :Text;
  status @7 :Text;
  startedAt @8 :Timestamp;
  finishedAt @9 :Timestamp;
  durationSeconds @10 :OptionalFloat64;
  processDurationSeconds @11 :OptionalFloat64;
  metrics @12 :List(BenchmarkMetricSample);
  processResources @13 :ProcessResourceUsage;
  exitCode @14 :OptionalInt32;
  executable @15 :Text;
  executableSha256 @16 :Text;
  arguments @17 :List(Text);
  environmentOverrideHash @18 :Text;
  toolchainFingerprint @19 :Text;
  buildVariantId @20 :Text;
  kitLockSha256 @21 :Text;
  compilerCacheFingerprint @22 :Text;
  compilerCache @23 :CompilerCacheStatistics;
  actionDigest @24 :Text;
  actionCacheHit @25 :OptionalBool;
  workingDirectory @26 :Text;
  stdout @27 :Text;
  stderr @28 :Text;
  error @29 :Text;
  requestedContext @30 :RequestedBuildContext;
  observedContext @31 :ObservedBuildContext;
  observationAfter @32 :ObservedBuildContext;
  buildVerification @33 :NativeBuildResult;
  artifact @34 :ArtifactReference;
  configure @35 :ConfigureProvenance;
  sessionBefore @36 :BuildContextSessionState;
  sessionAfter @37 :BuildContextSessionState;
  experimentResult @38 :Text;
  outputs @39 :List(Entry);
  declaredArtifacts @40 :List(WorkflowArtifactEvidence);
  executionConditions @41 :List(Entry);
  profile @42 :WorkflowProfileEvidence;
}

struct BenchmarkTrial {
  baselineVariant @0 :Text;
  executionOrder @1 :Text;
  axes @2 :List(BenchmarkAxisCoordinate);
}

struct BenchmarkAxisCoordinate {
  name @0 :Text;
  flag @1 :Text;
  value @2 :Text;
  role @3 :Text;
}

struct BenchmarkMetricSample {
  name @0 :Text;
  unit @1 :Text;
  direction @2 :Text;
  path @3 :Text;
  value @4 :Float64;
}

struct ProcessResourceUsage {
  schemaVersion @0 :Int32;
  scope @1 :Text;
  method @2 :Text;
  sampleIntervalMilliseconds @3 :Int32;
  samples @4 :Int32;
  peakRssBytes @5 :OptionalInt64;
  cpuSeconds @6 :OptionalFloat64;
}

struct CompilerCacheStatistics {
  schemaVersion @0 :Int32;
  profile @1 :Text;
  provider @2 :Text;
  scope @3 :Text;
  hits @4 :OptionalInt64;
  misses @5 :OptionalInt64;
  remoteReadHits @6 :OptionalInt64;
  remoteWrites @7 :OptionalInt64;
  compilerWorkSeconds @8 :OptionalFloat64;
  bytesRead @9 :OptionalInt64;
  bytesWritten @10 :OptionalInt64;
  compileSeconds @11 :OptionalFloat64;
}

struct RequestedBuildContext {
  buildDirectory @0 :Text;
  configuration @1 :Text;
  configurePreset @2 :Text;
  sourceDirectory @3 :Text;
  provenancePolicy @4 :Text;
  mode @5 :Text;
  generator @6 :Text;
}

struct ObservedBuildContext {
  buildDirectory @0 :Text;
  state @1 :Text;
  freshness @2 :Text;
  presetState @3 :Text;
  sourceDirectory @4 :Text;
  generator @5 :Text;
  multiConfig @6 :OptionalBool;
  configurations @7 :List(Text);
  indexFile @8 :Text;
  indexHash @9 :Text;
  cMakeExecutable @10 :Text;
  cTestExecutable @11 :Text;
  hasCodeModel @12 :Bool;
  hasCacheReply @13 :Bool;
  toolchains @14 :List(Text);
  targets @15 :List(NativeTargetObservation);
  diagnostics @16 :List(Text);
  managedConfigure @17 :ConfigureProvenance;
}

struct NativeTargetObservation {
  name @0 :Text;
  configuration @1 :Text;
  type @2 :Text;
  executable @3 :Text;
  evidence @4 :Text;
}

struct ConfigureProvenance {
  state @0 :Text;
  at @1 :Timestamp;
  buildDirectory @2 :Text;
  sourceDirectory @3 :Text;
  preset @4 :Text;
  indexFile @5 :Text;
  indexHash @6 :Text;
  exitCode @7 :OptionalInt32;
  executable @8 :Text;
  workingDirectory @9 :Text;
  buildVariantId @10 :Text;
  arguments @11 :List(Text);
}

struct NativeBuildResult {
  target @0 :CMakeTargetRef;
  verification @1 :Text;
  backendExitCode @2 :OptionalInt32;
  observationIndex @3 :Text;
  generation @4 :GenerationObservation;
}

struct CMakeTargetRef {
  buildDirectory @0 :Text;
  target @1 :Text;
  configuration @2 :Text;
}

struct GenerationObservation {
  indexFile @0 :Text;
  indexHash @1 :Text;
}

struct ArtifactReference {
  buildNode @0 :Text;
  target @1 :CMakeTargetRef;
  executable @2 :Text;
  sha256 @3 :Text;
  materialization @4 :Text;
}

struct BuildContextSessionState {
  mode @0 :Text;
  policy @1 :Text;
  provenance @2 :Text;
  generation @3 :GenerationObservation;
  generationChange @4 :Text;
  observedPropertiesChanged @5 :Bool;
  requiresConfigure @6 :Bool;
}

struct WorkflowArtifactEvidence {
  name @0 :Text;
  path @1 :Text;
  sha256 @2 :Text;
  role @3 :Text;
  from @4 :Text;
  snapshotPath @5 :Text;
}

struct WorkflowProfileEvidence {
  captureId @0 :Text;
  backend @1 :Text;
  mode @2 :Text;
  status @3 :Text;
  exitCode @4 :Int32;
  startedAt @5 :Timestamp;
  finishedAt @6 :Timestamp;
  executable @7 :Text;
  executableSha256 @8 :Text;
  rawTrace @9 :Text;
  rawTraceSha256 @10 :Text;
  counterConfiguration @11 :Text;
  counterConfigurationSha256 @12 :Text;
  targetProcessId @13 :OptionalInt32;
  targetStartedAt @14 :Timestamp;
  targetFinishedAt @15 :Timestamp;
  eventSource @16 :Text;
  launchModel @17 :Text;
  startGuarantee @18 :Text;
  attribution @19 :Text;
  supportedModes @20 :List(Text);
  rawFormats @21 :List(Text);
  cpuSet @22 :Text;
  cpuAffinityLauncher @23 :Text;
  memoryStallEvent @24 :Text;
  pmcEvents @25 :Text;
  pmcInterval @26 :Int32;
  timeoutTicks @27 :Int64;
  analysis @28 :WorkflowProfileAnalysis;
}

struct WorkflowProfileAnalysis {
  version @0 :UInt16;
  status @1 :Text;
  measurements @2 :List(WorkflowProfileMeasurement);
}

struct WorkflowProfileMeasurement {
  name @0 :Text;
  unit @1 :Text;
  value @2 :Float64;
  variant @3 :Text;
  method @4 :Text;
}

struct WorkflowFinding {
  code @0 :Text;
  severity @1 :Text;
  nodeId @2 :Text;
  message @3 :Text;
  suggestedProfiles @4 :List(Text);
}

struct WorkflowReconciliation {
  id @0 :Text;
  timestamp @1 :Timestamp;
  actor @2 :Text;
  host @3 :Text;
  nodeId @4 :Text;
  decision @5 :Text;
  reason @6 :Text;
  checkpointSha256 @7 :Text;
  previousRunStatus @8 :Text;
  previousRecoveryPhase @9 :Text;
  previousNode @10 :NodeResult;
  processesConfirmedStopped @11 :Bool;
}

struct HostEnvironment {
  hostId @0 :Text;
  cpuModel @1 :Text;
  architecture @2 :Text;
  logicalProcessors @3 :Int32;
  availableProcessors @4 :Int32;
  physicalPackages @5 :OptionalInt32;
  physicalCores @6 :OptionalInt32;
  operatingSystem @7 :Text;
  kernelVersion @8 :Text;
  affinity @9 :Text;
  governor @10 :Text;
  fingerprint @11 :Text;
}

struct BuildEnvironment {
  directory @0 :Text;
  configuration @1 :Text;
  generator @2 :Text;
  compiler @3 :Text;
  compilerId @4 :Text;
  compilerVersion @5 :Text;
  flags @6 :Text;
  fingerprint @7 :Text;
}

struct WorkflowToolchainIdentity {
  buildDirectory @0 :Text;
  cMake @1 :Text;
  ninja @2 :Text;
  compiler @3 :Text;
  sdk @4 :Text;
  environmentProfileId @5 :Text;
  generator @6 :Text;
  fingerprint @7 :Text;
  linux @8 :LinuxToolchainIdentity;
}

struct LinuxToolchainIdentity {
  cMake @0 :WorkflowToolSelection;
  cTest @1 :WorkflowToolSelection;
  ninja @2 :WorkflowToolSelection;
  cCompiler @3 :Text;
  cCompilerSha256 @4 :Text;
  cxxCompilerSha256 @5 :Text;
  targetTriple @6 :Text;
  compilerVersion @7 :Text;
  compilerArtifactSha256 @8 :Text;
  linker @9 :WorkflowToolSelection;
  sdkArtifactSha256 @10 :Text;
  runtime @11 :Text;
  environmentLockSha256 @12 :Text;
  vcpkgProjectSha256 @13 :Text;
  vcpkgPackageTreeSha256 @14 :Text;
  vcpkgDownloadTreeSha256 @15 :Text;
}

struct WorkflowToolSelection {
  origin @0 :Text;
  version @1 :Text;
  artifactSha256 @2 :Text;
  binarySha256 @3 :Text;
}

# A versioned runner snapshot held in the operation ledger. Generations and event
# cursors are operation-local. This record cannot authorize dispatch by itself.
struct ExecutionCheckpoint {
  version @0 :UInt16;
  workspaceId @1 :Text;
  operationId @2 :Text;
  requestDigest @3 :Text;
  workflowDocumentId @4 :Text;
  planDocumentId @5 :Text;
  dispatchIdentity @6 :Text;
  runDirectory @7 :Text;
  generation @8 :Int64;
  eventCursor @9 :Int64;
  report @10 :WorkflowReport;
}
