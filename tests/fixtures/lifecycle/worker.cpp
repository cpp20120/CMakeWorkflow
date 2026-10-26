#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
int main(int argc, char** argv) {
  if (argc > 1 && std::string(argv[1]) == "write") { std::ofstream(argv[2]) << argv[3]; return 0; }
  if (argc > 1 && std::string(argv[1]) == "external") return std::system(argv[2]) == 0 ? 0 : 1;
  std::cout << "EXECUTED\n";
}
