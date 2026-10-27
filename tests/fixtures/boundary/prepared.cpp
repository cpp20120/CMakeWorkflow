#include <cerrno>
#include <cstdio>
#include <string>
#ifdef _WIN32
#include <process.h>
#else
#include <sys/wait.h>
#include <unistd.h>
#endif

int main(int argc, char** argv) {
  if (argc != 3) return 2;

  // Exercise ownership propagation through direct process inheritance;
  // std::system() changes semantics between cmd.exe and POSIX shells.
#ifdef _WIN32
  // The Windows CRT spawn functions do not quote whitespace-containing argv
  // entries for us; provide quoted command-line arguments explicitly.
  const std::string executableArg = "\"" + std::string(argv[1]) + "\"";
  const std::string buildArg = "\"" + std::string(argv[2]) + "\"";
  const char* const args[] = {executableArg.c_str(), "target", "build",
      "--build-dir", buildArg.c_str(), "--target", "worker", nullptr};
  const intptr_t result = _spawnv(_P_WAIT, argv[1], args);
  if (result == -1) {
    std::perror("_spawnv");
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
    execl(argv[1], argv[1], "target", "build", "--build-dir", argv[2],
          "--target", "worker", static_cast<char*>(nullptr));
    std::perror("execl");
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
