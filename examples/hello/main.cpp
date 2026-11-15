#include <fstream>
#include <iostream>
#include <string>

int main(int argc, char* argv[])
{
    if (argc == 3 && std::string(argv[1]) == "--read-message")
    {
        std::ifstream input(argv[2]);
        std::string message;
        if (!std::getline(input, message))
        {
            std::cerr << "Cannot read message from " << argv[2] << '\n';
            return 1;
        }

        std::cout << message << '\n';
        return 0;
    }

    std::cout << "hello from CMakeWorkflow\n";
    return 0;
}
