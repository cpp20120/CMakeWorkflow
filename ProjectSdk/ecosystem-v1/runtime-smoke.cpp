#include <@NAME@/queue.hpp>

int main() {
    cmwf_runtime::Queue queue;
    std::uint32_t value{};
    if (queue.pop(value)) return 1;
    for (std::uint32_t n = 0; n < 64; ++n) {
        if (!queue.push(n)) return 2;
    }
    if (queue.push(64)) return 3;
    for (std::uint32_t n = 0; n < 512; ++n) {
        if (!queue.pop(value) || value != n || !queue.push(n + 64)) return 4;
    }
    return 0;
}
