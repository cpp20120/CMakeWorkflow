#include "probe.capnp.h"

#include <capnp/serialize.h>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string_view>

static void writeBytes(const char* path, kj::ArrayPtr<const kj::byte> bytes) {
  std::ofstream output(path, std::ios::binary);
  output.write(reinterpret_cast<const char*>(bytes.begin()), bytes.size());
  if (!output) throw std::runtime_error("output write failed");
}

static void build(capnp::MallocMessageBuilder& builder) {
  auto root = builder.initRoot<Probe>();
  auto blob = builder.getOrphanage().newOrphan<capnp::Data>(168);
  root.adoptBlob(kj::mv(blob));
  root.setBool(false);
  root.setI8(-9);
  root.setI16(-987);
  root.setI32(-87654);
  root.setI64(-9876543210ll);
  root.setU8(220);
  root.setU16(54321);
  root.setU32(87654321);
  root.setU64(9876543210ull);
  root.setSingle(-0.0f);
  uint64_t bits = 0x7ff8000000000042ull;
  double nan;
  std::memcpy(&nan, &bits, sizeof(nan));
  root.setDouble(nan);
  root.setStatus(static_cast<Status>(99));
  root.setText("");
  root.initChild().setAmount(23);
  root.initSamples(2).set(1, 456);
  root.initFlags(11).set(10, true);
  root.initStates(1).set(0, Status::RUNNING);
  root.initChildren(2)[1].setNote("nested");
  root.initStrings(2).set(1, "");
  auto datum = root.initBlobs(1).init(0, 4);
  std::memcpy(datum.begin(), "data", 4);
  root.initMatrix(2).init(1, 2).set(1, 40000);
  root.initNamed(1)[0].setTag(Probe::Nested::Tag::B);
  root.setTail(889);
  auto details = root.getPayload().initDetails();
  details.setMarker(77);
  details.initInner().setText("active");
  details.setEnabled(true);
}

static void verify(Probe::Reader root) {
  uint64_t bits;
  const double nan = root.getDouble();
  std::memcpy(&bits, &nan, sizeof(bits));
  if (root.getBool() || root.getI8() != -9 || root.getI16() != -987 ||
      root.getI32() != -87654 || root.getI64() != -9876543210ll ||
      root.getU8() != 220 || root.getU16() != 54321 ||
      root.getU32() != 87654321 || root.getU64() != 9876543210ull ||
      !std::signbit(root.getSingle()) || bits != 0x7ff8000000000042ull ||
      static_cast<unsigned>(root.getStatus()) != 99 || !root.hasText() ||
      root.getText().size() != 0 || root.getBlob().size() != 168 ||
      root.getChild().getAmount() != 23 || root.getSamples()[1] != 456 ||
      !root.getFlags()[10] || root.getStates()[0] != Status::RUNNING ||
      root.getChildren()[1].getNote() != "nested" || root.getStrings()[1].size() != 0 ||
      root.getBlobs()[0].size() != 4 || root.getMatrix()[1][1] != 40000 ||
      root.getNamed()[0].getTag() != Probe::Nested::Tag::B || root.getTail() != 889) {
    throw std::runtime_error("generated message differs from typed C++ projection");
  }
  auto details = root.getPayload().getDetails();
  if (details.getMarker() != 77 || !details.getEnabled() || details.getInner().getText() != "active") {
    throw std::runtime_error("generated union or nested group differs");
  }
}

int main(int argc, char** argv) {
  if (argc != 4) return 2;
  try {
    std::string_view mode(argv[1]);
    if (mode == "generate") {
      std::string_view form(argv[2]);
      capnp::MallocMessageBuilder builder(form == "single" ? 512 : form == "far" ? 1 : 21,
                                          capnp::AllocationStrategy::FIXED_SIZE);
      build(builder);
      auto frame = capnp::messageToFlatArray(builder);
      writeBytes(argv[3], frame.asBytes());
      return 0;
    }
    std::ifstream input(argv[2], std::ios::binary | std::ios::ate);
    auto bytes = input.tellg();
    if (bytes < 16 || bytes > 1024 * 1024 || bytes % 8 != 0) return 2;
    auto words = kj::heapArray<capnp::word>(static_cast<size_t>(bytes) / 8);
    input.seekg(0);
    input.read(reinterpret_cast<char*>(words.begin()), bytes);
    if (!input) return 2;
    capnp::FlatArrayMessageReader reader(words.asPtr());
    if (mode == "canonicalize-any") {
      auto canonical = reader.getRoot<capnp::AnyStruct>().canonicalize();
      writeBytes(argv[3], canonical.asBytes());
      return 0;
    }
    auto root = reader.getRoot<Probe>();
    if (mode == "defaults") {
      if (!root.getBool() || root.getStatus() != Status::RUNNING || root.hasText() ||
          root.getText() != "default" || root.hasChild() || root.getChild().getAmount() != 19 ||
          root.getChild().getNote() != "child" || root.getSamples()[1] != 3 || root.getBlob()[1] != 255) {
        throw std::runtime_error("official schema defaults differ");
      }
      auto canonical = capnp::canonicalize(root);
      writeBytes(argv[3], canonical.asBytes());
      return 0;
    }
    verify(root);
    if (mode == "verify" && !reader.isCanonical()) {
      throw std::runtime_error("managed output is not canonical");
    }
    auto canonical = capnp::canonicalize(root);
    writeBytes(argv[3], canonical.asBytes());
    return 0;
  } catch (const kj::Exception& e) {
    std::cerr << e.getDescription().cStr() << '\n';
  } catch (const std::exception& e) {
    std::cerr << e.what() << '\n';
  }
  return 1;
}
