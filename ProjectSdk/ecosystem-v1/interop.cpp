#include <@NAME@/api.h>

#include <limits>
#include <new>

struct @NAME@_counter { uint32_t value; };

int32_t @NAME@_create(uint32_t initial, @NAME@_counter** result) noexcept {
    if (!result) return @NAME@_INVALID;
    *result = new (std::nothrow) @NAME@_counter{initial};
    return *result ? @NAME@_OK : @NAME@_NO_MEMORY;
}

int32_t @NAME@_add(@NAME@_counter* counter, uint32_t increment, uint32_t* result) noexcept {
    if (!counter || !result) return @NAME@_INVALID;
    if (increment > std::numeric_limits<uint32_t>::max() - counter->value) return @NAME@_OVERFLOW;
    counter->value += increment;
    *result = counter->value;
    return @NAME@_OK;
}

void @NAME@_destroy(@NAME@_counter* counter) noexcept {
    delete counter;
}
