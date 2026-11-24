#pragma once

#include <cstdint>
#include <memory>

namespace cmwf_runtime {
class Queue {
public:
    Queue();
    ~Queue();
    Queue(const Queue&) = delete;
    Queue& operator=(const Queue&) = delete;
    bool push(std::uint32_t value);
    bool pop(std::uint32_t& value);

private:
    struct State;
    std::unique_ptr<State> state_;
};
}
