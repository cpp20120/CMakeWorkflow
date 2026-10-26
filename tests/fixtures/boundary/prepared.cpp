#include <cstdlib>
#include <string>
int main(int argc, char** argv) {
  if (argc != 3) return 2;
  std::string command = "\"" + std::string(argv[1]) + "\" target build --build-dir \"" + argv[2] + "\" --target worker";
  return std::system(command.c_str()) == 0 ? 0 : 1;
}
