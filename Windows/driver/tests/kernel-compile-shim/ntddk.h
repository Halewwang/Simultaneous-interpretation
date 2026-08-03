#ifndef EMKE_TEST_NTDDK_H
#define EMKE_TEST_NTDDK_H

#if defined(_MSC_VER)
using SIZE_T = unsigned __int64;
using ULONG = unsigned long;
using LONG = long;
using LONGLONG = __int64;
#else
using SIZE_T = __SIZE_TYPE__;
using ULONG = unsigned int;
using LONG = int;
using LONGLONG = long long;
#endif

#endif
