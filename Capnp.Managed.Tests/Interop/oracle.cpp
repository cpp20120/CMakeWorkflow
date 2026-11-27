#include "probe.capnp.h"

#include <capnp/serialize.h>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string_view>

static void writeBytes(const char* path, kj::ArrayPtr<const kj::byte> bytes) {
  std::ofstream output(path, std::ios::binary);
  output.write(reinterpret_cast<const char*>(bytes.begin()), bytes.size());
  output.close();
  if (!output) {
    throw std::runtime_error("output write failed");
  }
}

static void buildFixture(capnp::MallocMessageBuilder& builder) {
  auto root = builder.initRoot<Probe>();
  // With eight words per segment, the root fills segment zero and the orphan
  // fills its own segment. Adoption must allocate a double-far landing pad.
  auto blob = builder.getOrphanage().newOrphan<capnp::Data>(64);
  for (unsigned i = 0; i < 64; ++i) {
    blob.get()[i] = static_cast<kj::byte>(i);
  }
  root.adoptBlob(kj::mv(blob));
  root.setNumber(31);
  root.setLabel("managed");
  root.initLeaf().setNumber(0x1122334455667788ull);
  auto flags = root.initFlags(11);
  flags.set(0, true);
  flags.set(10, true);
  auto samples = root.initSamples(3);
  samples.set(0, 7);
  samples.set(2, 255);
  auto entries = root.initEntries(2);
  entries[1].setNumber(513);
  entries[0].setLabel("entry");
}

static void verifyFixture(Probe::Reader root) {
  if (root.getNumber() != 31 || root.getLabel() != "managed" ||
      root.getLeaf().getNumber() != 0x1122334455667788ull ||
      root.getFlags().size() != 11 || !root.getFlags()[0] || !root.getFlags()[10] ||
      root.getSamples().size() != 3 || root.getSamples()[0] != 7 || root.getSamples()[2] != 255 ||
      root.getEntries().size() != 2 || root.getEntries()[0].getLabel() != "entry" ||
      root.getEntries()[1].getNumber() != 513 || root.getBlob().size() != 64 ||
      root.getBlob()[63] != 63) {
    throw std::runtime_error("fixture fields differ");
  }
}

int main(int argc, char** argv) {
  if (argc != 4) {
    std::cerr << "usage: capnp-managed-oracle generate single|far|double output\n"
                 "       capnp-managed-oracle canonicalize|verify frame output\n";
    return 2;
  }

  try {
    const std::string_view mode(argv[1]);
    if (mode == "generate") {
      const std::string_view shape(argv[2]);
      unsigned capacity = shape == "single" ? 512 : (shape == "far" ? 1 : 8);
      capnp::MallocMessageBuilder builder(capacity, capnp::AllocationStrategy::FIXED_SIZE);
      buildFixture(builder);
      auto frame = capnp::messageToFlatArray(builder);
      writeBytes(argv[3], frame.asBytes());
      return 0;
    }

    std::ifstream input(argv[2], std::ios::binary | std::ios::ate);
    const auto size = input.tellg();
    if (size < 16 || size > 1024 * 1024 || size % 8 != 0) {
      throw std::runtime_error("invalid frame length");
    }
    auto words = kj::heapArray<capnp::word>(static_cast<size_t>(size) / 8);
    input.seekg(0);
    input.read(reinterpret_cast<char*>(words.begin()), size);
    if (!input) {
      throw std::runtime_error("frame read failed");
    }

    capnp::FlatArrayMessageReader message(words.asPtr());
    auto root = message.getRoot<Probe>();
    if (mode == "verify") {
      verifyFixture(root);
      if (!message.isCanonical()) {
        throw std::runtime_error("official reader rejected canonicality");
      }
    } else if (mode != "canonicalize") {
      return 2;
    }
    auto canonical = capnp::canonicalize(root);
    writeBytes(argv[3], canonical.asBytes());
    return 0;
  } catch (const kj::Exception& error) {
    std::cerr << error.getDescription().cStr() << '\n';
  } catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
  }
  return 1;
}
