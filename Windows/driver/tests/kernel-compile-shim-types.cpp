#include <ntddk.h>

static_assert(sizeof(SIZE_T) == 8u, "frozen x64 SIZE_T must be 64-bit");
static_assert(sizeof(ULONG) == 4u, "Windows ULONG must be 32-bit");
static_assert(sizeof(LONG) == 4u, "Windows LONG must be 32-bit");
static_assert(sizeof(LONGLONG) == 8u, "Windows LONGLONG must be 64-bit");
