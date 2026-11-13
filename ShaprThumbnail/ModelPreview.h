#pragma once

#include <windows.h>
#include <thumbcache.h>

#include <vector>

namespace shapr {

HRESULT RenderModelPreview(const std::vector<BYTE>& image, UINT edge, HBITMAP* bitmap,
    WTS_ALPHATYPE* alpha);

} // namespace shapr
