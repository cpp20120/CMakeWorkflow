@0xfc605d1528f82869;

# Immutable snapshot of a verified Host operation artifact catalog.
# A mutable pin is held separately in SQLite.
struct ArtifactManifest {
  formatVersion @0 :UInt16;
  workspaceId @1 :Text;
  operationId @2 :Text;
  hostId @3 :Text;
  reportBlob @4 :Text;
  entries @5 :List(Artifact);
  nativeReportDocumentId @6 :Text;
  nativeReportBlob @7 :Text;
  reportProjectionBlob @8 :Text;
}

struct Artifact {
  id @0 :Text;
  role @1 :Text;
  logicalPath @2 :Text;
  blob @3 :Text;
  size @4 :UInt64;
  mediaType @5 :Text;
}
