#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#ifdef _WIN32
#include <process.h>
#include <vector>
#else
#include <sys/wait.h>
#include <unistd.h>
#endif

int main(int argc, char** argv) {
  if (argc > 1 && std::string(argv[1]) == "write") {
    std::ofstream(argv[2]) << argv[3];
    return 0;
  }
  if (argc > 1 && std::string(argv[1]) == "external") {
    if (argc < 3) return 2;
#ifdef _WIN32
    std::vector<std::string> quotedArguments;
    for (int i = 2; i < argc; ++i)
      quotedArguments.emplace_back("\"" + std::string(argv[i]) + "\"");
    std::vector<const char*> childArguments;
    for (const auto& arg : quotedArguments) childArguments.push_back(arg.c_str());
    childArguments.push_back(nullptr);
    const intptr_t result = _spawnvp(_P_WAIT, argv[2], childArguments.data());
    if (result == -1) {
      std::perror("_spawnvp");
      return 3;
    }
    return result == 0 ? 0 : 1;
#else
    const pid_t child = fork();
    if (child < 0) {
      std::perror("fork");
      return 3;
    }
    if (child == 0) {
      execvp(argv[2], argv + 2);
      std::perror("execvp");
      _exit(127);
    }
    int status = 0;
    while (waitpid(child, &status, 0) == -1) {
      if (errno == EINTR) continue;
      std::perror("waitpid");
      return 3;
    }
    return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? 0 : 1;
#endif
  }
  std::cout << "EXECUTED\n";
}
