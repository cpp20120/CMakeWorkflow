#include <@NAME@/queue.hpp>

#include <charconv>
#include <chrono>
#include <iostream>
#include <string_view>

int main(int argc, char** argv) {
    std::uint32_t iterations = 100000;
    if (argc > 2) return 1;
    if (argc == 2) {
        const std::string_view text(argv[1]);
        const auto parsed = std::from_chars(text.data(), text.data() + text.size(), iterations);
        if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size() ||
            iterations == 0 || iterations > 5000000) return 1;
    }

    cmwf_runtime::Queue queue;
    std::uint64_t checksum = 0;
    const auto start = std::chrono::steady_clock::now();
    for (std::uint32_t i = 0; i < iterations; ++i) {
        std::uint32_t value{};
        if (!queue.push(i) || !queue.pop(value)) return 2;
        checksum += value;
    }
    const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now() - start);
    std::cout << "{\"iterations\":" << iterations << ",\"checksum\":" << checksum
              << ",\"elapsedNs\":" << elapsed.count() << "}\n";
}
