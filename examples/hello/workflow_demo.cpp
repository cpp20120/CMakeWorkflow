#include <chrono>
#include <fstream>
#include <iostream>
#include <string>
#include <thread>

static bool append_line(const char* path, const char* line)
{
    std::ofstream log(path, std::ios::app);
    log << line << '\n';
    log.close();
    if (log)
    {
        return true;
    }

    std::cerr << "Cannot write " << path << '\n';
    return false;
}

int main(int argc, char* argv[])
{
    if (argc == 2 && std::string(argv[1]) == "--hold")
    {
        std::this_thread::sleep_for(std::chrono::seconds(2));
        return 0;
    }

    if (argc == 3 && std::string(argv[1]) == "--record-and-hold")
    {
        if (!append_line(argv[2], "started"))
        {
            return 1;
        }

        std::this_thread::sleep_for(std::chrono::seconds(10));
        return append_line(argv[2], "finished") ? 0 : 1;
    }

    std::cerr << "Usage: workflow_demo --hold | --record-and-hold FILE\n";
    return 2;
}
