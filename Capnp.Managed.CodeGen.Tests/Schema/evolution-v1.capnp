@0x91bd6c76f0e1b483;
struct History {
  values @0 :List(UInt16);
  state @1 :State;
  union {
    idle @2 :Void;
    busy @3 :Text;
  }
}

enum State {
  old @0;
}
