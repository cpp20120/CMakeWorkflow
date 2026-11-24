#pragma once
#include <stdint.h>

#if defined(@NAME@_STATIC)
#define @NAME@_API
#elif defined(_WIN32)
#if defined(@NAME@_BUILD)
#define @NAME@_API __declspec(dllexport)
#else
#define @NAME@_API __declspec(dllimport)
#endif
#else
#define @NAME@_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
#define @NAME@_NOTHROW noexcept
extern "C" {
#else
#define @NAME@_NOTHROW
#endif

typedef struct @NAME@_counter @NAME@_counter;
enum { @NAME@_OK = 0, @NAME@_INVALID = 1, @NAME@_NO_MEMORY = 2, @NAME@_OVERFLOW = 3 };

/* The library allocates and destroys its own handles. Do not use free() on them.
   Destroy accepts null. Non-null handles must be released exactly once. */
@NAME@_API int32_t @NAME@_create(uint32_t initial, @NAME@_counter** result) @NAME@_NOTHROW;
@NAME@_API int32_t @NAME@_add(@NAME@_counter* counter, uint32_t increment, uint32_t* result) @NAME@_NOTHROW;
@NAME@_API void @NAME@_destroy(@NAME@_counter* counter) @NAME@_NOTHROW;
#ifdef __cplusplus
}
#endif
