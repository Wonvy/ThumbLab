#pragma once

#include <windows.h>
#include <objidl.h>

#include <cstddef>
#include <string_view>
#include <vector>

HRESULT ReadProjectEntry(IStream* stream, std::string_view name, std::size_t maxBytes,
    std::vector<BYTE>& data);
