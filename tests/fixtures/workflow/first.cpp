#include <chrono>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#include <thread>
#ifdef _WIN32
#include <process.h>
#define getpid _getpid
#else
#include <unistd.h>
#endif

int main(int argc, char** argv) {
  std::string mode = argc > 1 ? argv[1] : "args";
  if (mode == "fail") { std::cerr << "EXPECTED_FAILURE\n"; return 7; }
  if (mode == "generate") {
    std::ofstream("generated.hpp") << "#define GENERATED_VALUE " << argv[2] << "\n";
    return 0;
  }
  if (mode == "sleep") {
    { std::ofstream("sleep-invocations.txt", std::ios::app) << getpid() << '\n'; }
    { std::ofstream("ready.pid") << getpid(); }
    std::this_thread::sleep_for(std::chrono::milliseconds(std::stoi(argv[2])));
  }
  if (mode == "effect") {
    { std::ofstream("effect-invocations.txt", std::ios::app) << getpid() << '\n'; }
    { std::ofstream("ready.pid") << getpid(); }
    while (argc > 2 && !std::ifstream(argv[2]).good())
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
  }
  if (mode == "flood")
    for (int i = 0; i < 20000; ++i) { std::cout << "stdout line\n"; std::cerr << "stderr line\n"; }
  for (int i = 1; i < argc; ++i) std::cout << "[" << argv[i] << "]";
  if (auto env = std::getenv("WORKFLOW_TEST_VALUE")) std::cout << " ENV=" << env;
  std::cout << "\n";
}
