#include "workflow.capnp.h"
#include "productionWorkflow.capnp.h"

#include <capnp/serialize.h>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string_view>

int main(int argc, char** argv) {
  if (argc != 4) {
    std::cerr << "usage: cmwf-capnp-oracle canonicalize|verify|verify-production|resegment frame output\n";
    return 2;
  }

  try {
    std::ifstream input(argv[2], std::ios::binary | std::ios::ate);
    const auto size = input.tellg();
    if (size < 16 || size > 16 * 1024 * 1024 + 8 || size % 8 != 0) {
      throw std::runtime_error("invalid frame length");
    }
    auto words = kj::heapArray<capnp::word>(static_cast<size_t>(size) / 8);
    input.seekg(0);
    input.read(reinterpret_cast<char*>(words.begin()), size);
    if (!input) {
      throw std::runtime_error("frame read failed");
    }

    capnp::FlatArrayMessageReader message(words.asPtr());
    auto root = message.getRoot<Workflow>();
    const std::string_view mode(argv[1]);
    if (mode == "resegment") {
      capnp::MallocMessageBuilder builder(1, capnp::AllocationStrategy::FIXED_SIZE);
      builder.setRoot(root);
      auto frame = capnp::messageToFlatArray(builder);
      std::ofstream output(argv[3], std::ios::binary);
      output.write(reinterpret_cast<const char*>(frame.begin()), frame.size() * 8);
      output.close();
      return output ? 0 : 1;
    }
    if (mode == "verify-production") {
      auto manifest = message.getRoot<cmwf::production::Manifest>();
      if (manifest.getFormatVersion() != 1 || manifest.getSchemaVersion() < 1 ||
          manifest.getSchemaVersion() > 2 || manifest.getNodes().size() == 0) {
        throw std::runtime_error("invalid production manifest header");
      }
      for (auto node : manifest.getNodes()) {
        if (node.getId().size() == 0 || node.getKind() == cmwf::production::Operation::UNSPECIFIED) {
          throw std::runtime_error("invalid production manifest node");
        }
      }
    }
    if ((mode == "verify" || mode == "verify-production") && !message.isCanonical()) {
      throw std::runtime_error("message rejected by official isCanonical()");
    }
    auto canonical = capnp::canonicalize(root);
    std::ofstream output(argv[3], std::ios::binary);
    output.write(reinterpret_cast<const char*>(canonical.begin()), canonical.size() * 8);
    output.close();
    return output ? 0 : 1;
  } catch (const kj::Exception& error) {
    std::cerr << error.getDescription().cStr() << '\n';
  } catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
  }
  return 1;
}
