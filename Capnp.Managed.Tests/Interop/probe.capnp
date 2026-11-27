@0xa29584798b0bfc90;

struct Probe {
  number @0 :UInt32 = 17;
  label @1 :Text = "schema-default";
  leaf @2 :Leaf;
  flags @3 :List(Bool);
  samples @4 :List(UInt16);
  entries @5 :List(Entry);
  blob @6 :Data;
}

struct Leaf {
  number @0 :UInt64;
}

struct Entry {
  number @0 :UInt16;
  label @1 :Text;
}
