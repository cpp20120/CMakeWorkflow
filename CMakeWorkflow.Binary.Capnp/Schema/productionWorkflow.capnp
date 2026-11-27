@0x98b4eea7fb521be4;

using Cxx = import "/capnp/c++.capnp";
$Cxx.namespace("cmwf::production");
# Wire contract 1. The field below is the public WorkflowDocument version.
struct Manifest {
  schemaVersion @0 :UInt32;
  contexts @1 :List(ContextEntry);
  nodes @2 :List(Node);
  formatVersion @3 :UInt16;
}

struct ContextEntry {
  key @0 :Text;
  buildDirectory @1 :Text;
  configuration @2 :Text;
  configurePreset @3 :Text;
  sourceDirectory @4 :Text;
  mode @5 :Text;
  generator @6 :Text;
  provenancePolicy @7 :Text;
  compilerCacheProfile @8 :Text;
  toolchain @9 :Toolchain;
  buildVariant @10 :BuildVariant;
}

struct Toolchain {
  cmakeCandidateId @0 :Text;
  ninjaCandidateId @1 :Text;
  compilerCandidateId @2 :Text;
  sdkCandidateId @3 :Text;
  environmentProfileId @4 :Text;
  generator @5 :Text;
  hostArchitecture @6 :Text;
  targetArchitecture @7 :Text;
  environmentLockSha256 @8 :Text;
  vcpkgProjectSha256 @9 :Text;
}

struct BuildVariant {
  id @0 :Text;
  lockSha256 @1 :Text;
  kitLockSha256 @2 :Text;
}

enum Operation {
  unspecified @0;
  inspect @1;
  configure @2;
  build @3;
  invoke @4;
  run @5;
  profile @6;
  profileSnapshot @7;
  formatCheck @8;
  ctest @9;
}

struct Node {
  id @0 :Text;
  kind @1 :Operation;
  context @2 :Text;
  target @3 :Text;
  from @4 :Text;
  snapshotExecutable @5 :Text;
  snapshotSha256 @6 :Text;
  dependsOn @7 :List(Text);
  arguments @8 :List(Text);
  metrics @9 :Metrics;
  benchmarkTrial @10 :Trial;
  workingDirectory @11 :Text;
  environment @12 :List(Property);
  options @13 :List(Property);
  harnessDirectory @14 :Text;
  testRegex @15 :Text;
  labelRegex @16 :Text;
  resources @17 :List(Text);
  exclusive @18 :Bool;
  timeout :union {
    noTimeout @19 :Void;
    seconds @20 :Float64;
  }
  inputs @21 :List(Input);
  outputs @22 :List(Output);
  constraints @23 :Constraints;
  actionCache @24 :Bool;
}

struct Property {
  key @0 :Text;
  value @1 :Text;
}

struct Metrics {
  source @0 :Text;
  format @1 :Text;
  fields @2 :List(MetricField);
}

struct MetricField {
  path @0 :Text;
  name @1 :Text;
  unit @2 :Text;
  direction @3 :Text;
}

struct Trial {
  baselineVariant @0 :Text;
  executionOrder @1 :Text;
  axes @2 :List(Axis);
}

struct Axis {
  name @0 :Text;
  flag @1 :Text;
  value @2 :Text;
  role @3 :Text;
}

struct Input {
  path @0 :Text;
  sha256 @1 :Text;
  from @2 :Text;
  artifact @3 :Text;
  snapshot @4 :Bool;
}

struct Output {
  name @0 :Text;
  path @1 :Text;
  root @2 :Text;
  fresh @3 :Bool;
}

struct Constraints {
  cpuSet @0 :Text;
  profiler @1 :Profiler;
}

struct Profiler {
  backend @0 :Text;
  mode @1 :Text;
  eventSource @2 :Text;
}
