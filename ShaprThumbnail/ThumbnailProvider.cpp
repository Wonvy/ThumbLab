#include "ModelPreview.h"
#include "PreviewCache.h"
#include "ProjectArchive.h"

#include <windows.h>
#include <objidl.h>
#include <shobjidl_core.h>
#include <thumbcache.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <new>
#include <vector>

namespace {

constexpr CLSID ProviderClass{
    0x7d076184, 0x5648, 0x487e, {0x9e, 0xb6, 0x59, 0x1d, 0xe2, 0x3c, 0x89, 0xc5}};
constexpr size_t MaxModelPreviewBytes = 16 * 1024 * 1024;
constexpr UINT MaxThumbnailEdge = 512;
std::atomic<long> moduleReferences{0};

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
        if (stream_) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        stream_ = stream;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetThumbnail(UINT edge, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override
    {
        if (!bitmap || !alpha) return E_POINTER;
        *bitmap = nullptr;
        *alpha = WTSAT_UNKNOWN;
        if (!edge) return E_INVALIDARG;
        if (!stream_) return CO_E_NOTINITIALIZED;

        try {
            std::vector<BYTE> image;
            const HRESULT cached = shapr::ReadCachedModelPreview(stream_.Get(), image);
            if (SUCCEEDED(cached)) {
                const HRESULT rendered = shapr::RenderModelPreview(image, std::min(edge, MaxThumbnailEdge), bitmap, alpha);
                if (SUCCEEDED(rendered)) return rendered;
            }

            // A regular Shapr3D export can contain drawing PNGs. Use only
            // explicitly named model previews and never pick an arbitrary image.
            for (const auto name : {"model-preview.png", "model-preview.jpg"}) {
                const HRESULT embedded = ReadProjectEntry(stream_.Get(), name, MaxModelPreviewBytes, image);
                if (SUCCEEDED(embedded)) {
                    const HRESULT rendered = shapr::RenderModelPreview(image, std::min(edge, MaxThumbnailEdge), bitmap, alpha);
                    if (SUCCEEDED(rendered)) return rendered;
                }
            }
            return HRESULT_FROM_WIN32(ERROR_NO_DATA);
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

private:
    ~ThumbnailProvider()
    {
        stream_.Reset();
        --moduleReferences;
    }

    std::atomic<ULONG> references_{1};
    Microsoft::WRL::ComPtr<IStream> stream_;
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
