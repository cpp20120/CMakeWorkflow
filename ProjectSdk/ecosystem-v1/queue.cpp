#include <@NAME@/queue.hpp>

#include <array>
#if CMWF_RUNTIME_THREADS
#include <mutex>
#endif

namespace cmwf_runtime {
struct Queue::State {
    std::array<std::uint32_t, 64> values{};
    std::size_t head{};
    std::size_t count{};
#if CMWF_RUNTIME_THREADS
    std::mutex mutex;
#endif
};

Queue::Queue() : state_(std::make_unique<State>()) {}
Queue::~Queue() = default;

bool Queue::push(std::uint32_t value) {
#if CMWF_RUNTIME_THREADS
    const std::lock_guard lock(state_->mutex);
#endif
    auto& state = *state_;
    if (state.count == state.values.size()) return false;
    state.values[(state.head + state.count) % state.values.size()] = value;
    ++state.count;
    return true;
}

bool Queue::pop(std::uint32_t& value) {
#if CMWF_RUNTIME_THREADS
    const std::lock_guard lock(state_->mutex);
#endif
    auto& state = *state_;
    if (state.count == 0) return false;
    value = state.values[state.head];
    state.head = (state.head + 1) % state.values.size();
    --state.count;
    return true;
}
}
