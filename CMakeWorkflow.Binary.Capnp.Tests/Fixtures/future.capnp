@0xec491bed3a1d98e5;
using Base = import "workflow.capnp";

struct FutureWorkflow {
  schemaVersion @0 :UInt32;
  contexts @1 :List(FutureContext);
  nodes @2 :List(Base.Node);
  revision @3 :UInt64;
  note @4 :Text;
}

struct FutureContext {
  id @0 :Text;
  buildDirectory @1 :Text;
  configuration @2 :Text;
  configurePreset @3 :Text;
  sourceDirectory @4 :Text;
  mode @5 :Text;
  generator @6 :Text;
  provenancePolicy @7 :Text;
  compilerCacheProfile @8 :Text;
  extra @9 :Text;
}
