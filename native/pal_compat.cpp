// Copyright (c) Wiesław Šoltés. MIT licensed.
#ifndef _WIN32
#include <minipal/guid.h>
#include <cstring>
// Only GUID comparison is required by the SDK callback base. Do not pull in the runtime PAL.
extern "C" bool minipal_guid_equals(const GUID* a,const GUID* b) { static_assert(sizeof(GUID)==16); return std::memcmp(a,b,sizeof(GUID))==0; }
#endif
