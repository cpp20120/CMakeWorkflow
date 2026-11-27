@0xba393938177a8371;
struct Child {
  amount @0 :UInt32 = 7;
  note @1 :Text;
}

enum Status {
  idle @0;
  running @1;
}

const limit :UInt16 = 1024;
const label :Text = "constant";
const numbers :List(UInt16) = [12, 13];
