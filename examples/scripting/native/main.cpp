#include <cstdint>
#include <iostream>
#include <random>

int main()
{
    std::mt19937 random{42};
    std::uint64_t sum = 0;
    for (int index = 0; index < 2000000; ++index)
    {
        sum += random();
    }
    std::cout << sum << '\n';
    return sum == 0;
}
