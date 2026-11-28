@0xbfb26eaef4a28163;

using Report = import "executionReport.capnp";

struct ProcessCheckpoint {
  version @0 :UInt16;
  workspaceId @1 :Text;
  operationId @2 :Text;
  requestDigest @3 :Text;
  runDirectory @4 :Text;
  generation @5 :Int64;
  eventCursor @6 :Int64;
  snapshotSha256 @7 :Text;
  status @8 :Text;
  error @9 :Text;
  recoveryPhase @10 :Text;
  resumeCount @11 :Int32;
  recovery @12 :Recovery;
  finishedAt @13 :Report.Timestamp;
  nodes @14 :List(ProcessNode);
}

struct Recovery {
  note @0 :Text;
  confirmedAt @1 :Report.Timestamp;
  previousCheckpoint @2 :Data;
  dispatchCheckpoint @3 :Data;
}

struct ProcessNode {
  id @0 :Text;
  status @1 :Text;
  error @2 :Text;
  exitCode @3 :Report.OptionalInt32;
  resources @4 :Report.ProcessResourceUsage;
  startedAt @5 :Report.Timestamp;
  finishedAt @6 :Report.Timestamp;
  stdout @7 :Text;
  stderr @8 :Text;
  dependencies @9 :List(Text);
}
