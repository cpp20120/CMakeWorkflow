@0xd593f360bb8c2bff;

struct EnvironmentLock {
  formatVersion @0 :UInt16;
  schemaVersion @1 :UInt32;
  profile @2 :Text;
  hostOs @3 :Text;
  hostArchitecture @4 :Text;
  targetOs @5 :Text;
  targetArchitecture @6 :Text;
  targetAbi @7 :Text;
  targetTriple @8 :Text;
  generator @9 :Text;
  policy @10 :Text;
  components @11 :List(Component);
}

struct Component {
  id @0 :Text;
  version @1 :Text;
  origin @2 :Text;
  distribution @3 :Release;
  dependencies @4 :List(Requirement);
}

struct Release {
  id @0 :Text;
  version @1 :Text;
  platform @2 :Text;
  source @3 :Text;
  archiveSha256 @4 :Text;
  treeSha256 @5 :Text;
  tarRoot @6 :Text;
}

struct Requirement {
  id @0 :Text;
  version @1 :Text;
}

struct DependencyPlan {
  formatVersion @0 :UInt16;
  schemaVersion @1 :UInt32;
  directory @2 :Text;
  manifestSha256 @3 :Text;
  configurationSha256 @4 :Text;
  environmentIdentity @5 :Text;
  executable @6 :Release;
  checkout @7 :Release;
  commit @8 :Text;
  sourceSnapshotId @9 :Text;
  targetTriplet @10 :Text;
  hostTriplet @11 :Text;
  registries @12 :Text;
}

struct ProvisioningReceipt {
  formatVersion @0 :UInt16;
  plan @1 :DependencyPlan;
  planIdentity @2 :Text;
  installedTreeSha256 @3 :Text;
  toolchainSha256 @4 :Text;
  downloadTreeSha256 @5 :Text;
}

struct ToolingPlan {
  formatVersion @0 :UInt16;
  schemaVersion @1 :UInt32;
  platform @2 :Text;
  profiles @3 :List(Text);
  installDirectory @4 :Text;
  packageManager @5 :Text;
  ready @6 :Bool;
  tools @7 :List(ToolStatus);
  steps @8 :List(ToolingStep);
  resolutionPolicy @9 :Text;
}

struct ToolStatus {
  id @0 :Text;
  displayName @1 :Text;
  installed @2 :Bool;
  command @3 :Text;
  version @4 :Text;
  minimumVersion @5 :Text;
  diagnostic @6 :Text;
  resolution @7 :Text;
  origin @8 :Text;
  sha256 @9 :Text;
  systemResolution @10 :Text;
  systemVersion @11 :Text;
}

struct ToolingStep {
  tool @0 :Text;
  method @1 :Text;
  executable @2 :Text;
  arguments @3 :List(Text);
  source @4 :Text;
  diagnostic @5 :Text;
  release @6 :Release;
}
