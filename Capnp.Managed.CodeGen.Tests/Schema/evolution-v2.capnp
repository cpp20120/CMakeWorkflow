@0xaaa699528fc24e6a;
struct History {
  values @0 :List(Wrapped);
  state @1 :State;
  union {
    idle @2 :Void;
    busy @3 :Text;
    newKind @4 :UInt32;
  }
  counter @5 :UInt32 = 31;
  label @6 :Text = "future";

  struct Wrapped {
    value @0 :UInt16;
    note @1 :Text;
  }
}

enum State {
  old @0;
  extra @1;
}
