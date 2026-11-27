@0xe29bd83e9ed244e1;
using Common = import "common.capnp";

struct Probe {
  bool @0 :Bool = true;
  i8 @1 :Int8 = -7;
  i16 @2 :Int16 = -500;
  i32 @3 :Int32 = -70000;
  i64 @4 :Int64 = -9000000000;
  u8 @5 :UInt8 = 150;
  u16 @6 :UInt16 = 900;
  u32 @7 :UInt32 = 70000;
  u64 @8 :UInt64 = 9000000000;
  single @9 :Float32;
  double @10 :Float64 = inf;
  status @11 :Common.Status = running;
  text @12 :Text = "default";
  blob @13 :Data = 0x"01ff";
  child @14 :Common.Child = (amount = 19, note = "child");
  samples @15 :List(UInt16) = [2, 3];
  flags @16 :List(Bool);
  states @17 :List(Common.Status);
  children @18 :List(Common.Child);
  strings @19 :List(Text);
  blobs @20 :List(Data);
  matrix @21 :List(List(UInt32));
  payload :union {
    none @22 :Void;
    count @23 :UInt64;
    name @24 :Text;
    details :group {
      marker @25 :UInt32;
      inner :group {
        text @26 :Text;
      }
      union {
        disabled @27 :Void;
        enabled @28 :Bool;
      }
    }
  }
  tail @29 :UInt32;
  named @30 :List(Nested);
  signedDefault @31 :Float32 = -0.0;
  struct Nested {
    enum Tag {
      a @0;
      b @1;
    }
    tag @0 :Tag;
  }
}
