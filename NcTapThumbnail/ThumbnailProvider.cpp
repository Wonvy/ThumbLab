#include "GCode.h"

#include <windows.h>
#include <objidl.h>
#include <shobjidl_core.h>
#include <thumbcache.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <limits>
#include <new>
#include <string>
#include <vector>

namespace {

constexpr CLSID ProviderClass{
    0xd2a9e93c, 0x8766, 0x4fc8, {0x9f, 0x10, 0xf0, 0xf5, 0xb9, 0xc6, 0x05, 0x38}};
constexpr UINT MaxThumbnailEdge = 512;
constexpr HRESULT NoToolpath = HRESULT_FROM_WIN32(ERROR_NO_DATA);
std::atomic<long> moduleReferences{0};

HRESULT ReadProgram(IStream* stream, std::string& program)
{
    std::array<char, 16384> chunk;
    while (true) {
        const auto request = static_cast<ULONG>(
            std::min(chunk.size(), nctap::MaxProgramBytes + 1 - program.size()));
        ULONG received = 0;
        const HRESULT result = stream->Read(chunk.data(), request, &received);
        if (FAILED(result)) return result;
        if (received > request) return STG_E_READFAULT;
        if (received > nctap::MaxProgramBytes - program.size()) {
            return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        }
        program.append(chunk.data(), received);
        if (result == S_FALSE || received == 0) return S_OK;
    }
}

struct Bounds {
    double minX = std::numeric_limits<double>::infinity();
    double minY = minX;
    double maxX = -minX;
    double maxY = -minX;

    void Include(const nctap::Point& point)
    {
        minX = std::min(minX, point.x);
        minY = std::min(minY, point.y);
        maxX = std::max(maxX, point.x);
        maxY = std::max(maxY, point.y);
    }

    double Span() const { return std::max(maxX - minX, maxY - minY); }
};

bool MovesInXY(const nctap::Segment& segment)
{
    return segment.from.x != segment.to.x || segment.from.y != segment.to.y;
}

HRESULT Render(const std::vector<nctap::Segment>& path, UINT edge, HBITMAP* output)
{
    Bounds all, feed;
    for (const auto& segment : path) {
        if (!MovesInXY(segment)) continue;
        all.Include(segment.from);
        all.Include(segment.to);
        if (!segment.rapid) {
            feed.Include(segment.from);
            feed.Include(segment.to);
        }
    }
    const auto& bounds = feed.Span() > 0 ? feed : all;
    const double span = bounds.Span();
    if (!std::isfinite(span) || span <= 0) return NoToolpath;

    const double centerX = bounds.minX + (bounds.maxX - bounds.minX) / 2;
    const double centerY = bounds.minY + (bounds.maxY - bounds.minY) / 2;
    const double pixelCenter = (edge - 1) / 2.0;
    const double plotWidth = (edge - 1) * 0.78;
    const auto pixel = [=](double value, double center, double direction) {
        const double coordinate = pixelCenter + direction * ((value - center) / span) * plotWidth;
        return static_cast<int>(std::lround(std::clamp(coordinate, -32760.0, 32760.0)));
    };

    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(info.bmiHeader);
    info.bmiHeader.biWidth = static_cast<LONG>(edge);
    info.bmiHeader.biHeight = -static_cast<LONG>(edge);
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr;
    HBITMAP bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!bitmap) return E_OUTOFMEMORY;

    HDC dc = CreateCompatibleDC(nullptr);
    HPEN rapidPen = CreatePen(PS_SOLID, 1, RGB(114, 143, 168));
    HPEN feedPen = CreatePen(PS_SOLID, std::max(1u, edge / 100), RGB(100, 231, 207));
    if (!dc || !rapidPen || !feedPen) {
        if (dc) DeleteDC(dc);
        if (rapidPen) DeleteObject(rapidPen);
        if (feedPen) DeleteObject(feedPen);
        DeleteObject(bitmap);
        return E_OUTOFMEMORY;
    }

    const DWORD background = edge == 1 ? 0x0064e7cf : 0x00122232;
    std::fill_n(static_cast<DWORD*>(pixels), edge * edge, background);
    const auto oldBitmap = SelectObject(dc, bitmap);
    if (!oldBitmap) {
        DeleteDC(dc);
        DeleteObject(rapidPen);
        DeleteObject(feedPen);
        DeleteObject(bitmap);
        return E_FAIL;
    }
    const auto oldPen = SelectObject(dc, rapidPen);
    for (bool rapid : {true, false}) {
        if (!rapid) SelectObject(dc, feedPen);
        for (const auto& segment : path) {
            if (segment.rapid != rapid || !MovesInXY(segment)) continue;
            const int x1 = pixel(segment.from.x, centerX, 1);
            const int y1 = pixel(segment.from.y, centerY, -1);
            const int x2 = pixel(segment.to.x, centerX, 1);
            const int y2 = pixel(segment.to.y, centerY, -1);
            if (x1 == x2 && y1 == y2) continue;
            MoveToEx(dc, x1, y1, nullptr);
            LineTo(dc, x2, y2);
        }
    }
    GdiFlush();
    if (oldPen) SelectObject(dc, oldPen);
    SelectObject(dc, oldBitmap);
    DeleteDC(dc);
    DeleteObject(rapidPen);
    DeleteObject(feedPen);
    *output = bitmap;
    return S_OK;
}

class ThumbnailProvider final : public IThumbnailProvider, public IInitializeWithStream {
public:
    ThumbnailProvider() { ++moduleReferences; }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IThumbnailProvider)) {
            *object = static_cast<IThumbnailProvider*>(this);
        } else if (iid == __uuidof(IInitializeWithStream)) {
            *object = static_cast<IInitializeWithStream*>(this);
        } else {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE Initialize(IStream* stream, DWORD) override
    {
        if (!stream) return E_INVALIDARG;
        if (initialized_) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        try {
            std::string program;
            const HRESULT result = ReadProgram(stream, program);
            if (FAILED(result)) return result;
            path_ = nctap::ParseGCode(program);
            initialized_ = true;
            return S_OK;
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE GetThumbnail(UINT edge, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override
    {
        if (!bitmap || !alpha) return E_POINTER;
        *bitmap = nullptr;
        *alpha = WTSAT_UNKNOWN;
        if (edge == 0) return E_INVALIDARG;
        if (!initialized_) return CO_E_NOTINITIALIZED;
        const HRESULT result = Render(path_, std::min(edge, MaxThumbnailEdge), bitmap);
        if (SUCCEEDED(result)) *alpha = WTSAT_RGB;
        return result;
    }

private:
    ~ThumbnailProvider() { --moduleReferences; }

    std::atomic<ULONG> references_{1};
    bool initialized_ = false;
    std::vector<nctap::Segment> path_;
};

class ClassFactory final : public IClassFactory {
public:
    ClassFactory() { ++moduleReferences; }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *object = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto provider = new (std::nothrow) ThumbnailProvider();
        if (!provider) return E_OUTOFMEMORY;
        const HRESULT result = provider->QueryInterface(iid, object);
        provider->Release();
        return result;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) ++moduleReferences;
        else --moduleReferences;
        return S_OK;
    }

private:
    ~ClassFactory() { --moduleReferences; }

    std::atomic<ULONG> references_{1};
};

} // namespace

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (!object) return E_POINTER;
    *object = nullptr;
    if (clsid != ProviderClass) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}

extern "C" HRESULT WINAPI DllCanUnloadNow()
{
    return moduleReferences == 0 ? S_OK : S_FALSE;
}
