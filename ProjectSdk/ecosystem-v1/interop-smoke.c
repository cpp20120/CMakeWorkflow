#include <@NAME@/api.h>

int main(void) {
    @NAME@_counter* counter = 0;
    uint32_t value = 0;
    if (@NAME@_create(40, &counter) != @NAME@_OK) return 1;
    int32_t status = @NAME@_add(counter, 2, &value);
    @NAME@_destroy(counter);
    @NAME@_destroy(0);
    return status == @NAME@_OK && value == 42 ? 0 : 2;
}
