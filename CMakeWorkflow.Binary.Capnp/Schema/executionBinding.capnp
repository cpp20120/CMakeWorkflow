@0x91d2f5cbb397306a;

# CMake Host dispatch binding. WorkflowDocument owns operation arguments/options.
# This schema captures the stable resolved values checked again during recovery.
struct DispatchBinding {
  version @0 :UInt16;
  operationId @1 :Text;
  workspaceId @2 :Text;
  requestDigest @3 :Text;
  documentId @4 :Text;
  blobId @5 :Text;
  canonicalDigest @6 :Text;
  manifestPath @7 :Text;
  resultsDirectory @8 :Text;
  jobs @9 :UInt32;
  nodes @10 :List(NodeBinding);
  sourceSnapshotId @11 :Text;
}

struct NodeBinding {
  id @0 :Text;
  kind @1 :Text;
  target @2 :Text;
  buildDirectory @3 :Text;
  requestedConfiguration @4 :Text;
  workingDirectory @5 :Text;
  sourceDirectory @6 :Text;
  configurePreset @7 :Text;
  mode @8 :Text;
  generator @9 :Text;
  provenancePolicy @10 :Text;
  dependencies @11 :List(Text);
  runtimeFingerprint @12 :Text;
  managedEnvironmentSha256 @13 :Text;
  compilerCacheFingerprint @14 :Text;
  dependencyTriplet @15 :Text;
  dependencyManifestRoot @16 :Text;
  dependencyInstalledDirectory @17 :Text;
  dependencyToolchain @18 :Text;
  dependencyChainloadToolchain @19 :Text;
  environmentLockIdentity @20 :Text;
  dependencyPlanIdentity @21 :Text;
  dependencyReceiptIdentity @22 :Text;
}
