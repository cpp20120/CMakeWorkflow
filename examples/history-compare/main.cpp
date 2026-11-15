#include <algorithm>
#include <iostream>
#include <random>
#include <vector>

int main()
{
    std::mt19937 random(42);
    std::vector<std::mt19937::result_type> values(1'000'001);
    std::generate(values.begin(), values.end(), [&random] { return random(); });
    const auto middle = values.begin() + values.size() / 2;

#if USE_NTH_ELEMENT
    std::nth_element(values.begin(), middle, values.end());
#else
    std::sort(values.begin(), values.end());
#endif

    std::cout << *middle << '\n';
}
