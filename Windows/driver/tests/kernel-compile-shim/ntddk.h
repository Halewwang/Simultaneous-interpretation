#ifndef EMKE_TEST_NTDDK_H
#define EMKE_TEST_NTDDK_H

using SIZE_T = decltype(sizeof(0));
using ULONG = unsigned long;
using LONG = long;
#if defined(_MSC_VER)
using LONGLONG = __int64;
#else
using LONGLONG = long long;
#endif

#endif
