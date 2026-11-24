#include <@NAME@/api.h>

static_assert(noexcept(@NAME@_destroy(nullptr)));
static_assert(noexcept(@NAME@_create(0, nullptr)));
static_assert(noexcept(@NAME@_add(nullptr, 0, nullptr)));

int main() {
    @NAME@_counter* counter = nullptr;
    uint32_t result = 0;
    if (@NAME@_create(0, nullptr) != @NAME@_INVALID ||
        @NAME@_create(UINT32_MAX, &counter) != @NAME@_OK) return 1;
    const int32_t overflow = @NAME@_add(counter, 1, &result);
    const int32_t status = @NAME@_add(counter, 0, &result);
    @NAME@_destroy(counter);
    return overflow == @NAME@_OVERFLOW && status == @NAME@_OK && result == UINT32_MAX ? 0 : 2;
}
