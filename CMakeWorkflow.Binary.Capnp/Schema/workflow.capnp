@0xab902799e692892d;

struct Workflow {
  schemaVersion @0 :UInt32;
  contexts @1 :List(Context);
  nodes @2 :List(Node);
}

struct Context {
  id @0 :Text;
  buildDirectory @1 :Text;
  configuration @2 :Text;
  configurePreset @3 :Text;
  sourceDirectory @4 :Text;
  mode @5 :Text;
  generator @6 :Text;
  provenancePolicy @7 :Text;
  compilerCacheProfile @8 :Text;
}

struct Node {
  id @0 :Text;
  context @1 :Text;
  # Zero-based declaration indices. Run and input references add implicit edges.
  dependsOn @2 :List(UInt32);
  arguments @3 :List(Text);
  workingDirectory @4 :Text;
  environment @5 :List(Property);
  resources @6 :List(Text);
  exclusive @7 :Bool;
  hasTimeout @8 :Bool;
  timeoutSeconds @9 :Float64;
  inputs @10 :List(Input);
  outputs @11 :List(Output);
  cpuSet @12 :Text;
  hasConstraints @13 :Bool;
  actionCache @14 :Bool;
  operation :union {
    configure @15 :Void;
    build @16 :Text;
    run @17 :UInt32;  # One-based source node index, zero is missing.
    ctest @18 :TestFilters;
  }
}

struct Property {
  key @0 :Text;
  value @1 :Text;
}

struct Input {
  path @0 :Text;
  sha256 @1 :Text;
  from @2 :UInt32;  # One-based producer index, zero is absent.
  artifact @3 :Text;
  snapshot @4 :Bool;
}

struct Output {
  name @0 :Text;
  path @1 :Text;
  root @2 :Text;
  fresh @3 :Bool;
}

struct TestFilters {
  testRegex @0 :Text;
  labelRegex @1 :Text;
}
