#pragma once

#include <windows.h>
#include <objidl.h>

#include <vector>

namespace shapr {

HRESULT ReadCachedModelPreview(IStream* project, std::vector<BYTE>& image);

}
