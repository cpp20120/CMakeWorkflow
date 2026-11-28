@0x8c86ab19aa222a4d;

struct ControllerPlan {
  version @0 :UInt16;
  repository @1 :Text;
  requestedRef @2 :Text;
  commit @3 :Text;
  sourceSnapshotId @4 :Text;
  bundleSha256 @5 :Text;
  bundleSize @6 :UInt64;
  bundleFileCount @7 :UInt32;
  host @8 :Host;
  workflow @9 :Workflow;
  dependencyPlanSha256 @10 :Text;
  placement @11 :Placement;
}

struct Host {
  destination @0 :Text;
  workspace @1 :Text;
  endpoint @2 :Text;
  executable @3 :Text;
  username @4 :Text;
  keyFile @5 :Text;
  hostKeySha256 @6 :Text;
  port @7 :UInt16;
}

struct Workflow {
  documentId @0 :Text;
  blobId @1 :Text;
  canonicalDigest @2 :Text;
  schemaVersion @3 :UInt16;
  storedLength @4 :UInt32;
}

struct Placement {
  name @0 :Text;
  minimumProcessors @1 :UInt32;
  architecture @2 :Text;
  availableProcessors @3 :UInt32;
  compilerVersion @4 :Text;
  toolchainFingerprint @5 :Text;
}
