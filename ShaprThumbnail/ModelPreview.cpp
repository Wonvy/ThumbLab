#include "ModelPreview.h"

#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>

namespace shapr {
namespace {

using Microsoft::WRL::ComPtr;

constexpr std::size_t MaxImageBytes = 16 * 1024 * 1024;
constexpr UINT MaxSourceEdge = 8192;
constexpr std::uint64_t MaxSourcePixels = 32 * 1024 * 1024;
constexpr UINT MaxThumbnailEdge = 512;
constexpr std::array<BYTE, 8> PngSignature{0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a};
constexpr std::array<BYTE, 3> JpegSignature{0xff, 0xd8, 0xff};

template <std::size_t N>
bool HasSignature(const std::vector<BYTE>& image, const std::array<BYTE, N>& signature)
{
    return image.size() >= signature.size() && std::equal(signature.begin(), signature.end(), image.begin());
}

HRESULT CopyToBitmap(IWICBitmapSource* source, UINT width, UINT height,
    HBITMAP* bitmap, WTS_ALPHATYPE* alpha)
{
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(info.bmiHeader);
    info.bmiHeader.biWidth = static_cast<LONG>(width);
    info.bmiHeader.biHeight = -static_cast<LONG>(height);
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    void* pixels = nullptr;
    const HBITMAP dib = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!dib) return E_OUTOFMEMORY;

    const UINT stride = width * 4;
    const UINT size = stride * height;
    auto* bytes = static_cast<BYTE*>(pixels);
    const HRESULT result = source->CopyPixels(nullptr, stride, size, bytes);
    if (FAILED(result)) {
        DeleteObject(dib);
        return result;
    }

    *alpha = WTSAT_RGB;
    for (UINT index = 3; index < size; index += 4) {
        if (bytes[index] != 0xff) {
            *alpha = WTSAT_ARGB;
            break;
        }
    }
    *bitmap = dib;
    return S_OK;
}

} // namespace

HRESULT RenderModelPreview(const std::vector<BYTE>& image, UINT edge, HBITMAP* bitmap,
    WTS_ALPHATYPE* alpha)
{
    if (!bitmap || !alpha) return E_POINTER;
    *bitmap = nullptr;
    *alpha = WTSAT_UNKNOWN;
    if (!edge) return E_INVALIDARG;
    if (image.size() > MaxImageBytes) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);

    const CLSID* decoderClass = nullptr;
    if (HasSignature(image, PngSignature)) decoderClass = &CLSID_WICPngDecoder;
    if (HasSignature(image, JpegSignature)) decoderClass = &CLSID_WICJpegDecoder;
    if (!decoderClass) return WINCODEC_ERR_UNKNOWNIMAGEFORMAT;

    ComPtr<IWICImagingFactory> factory;
    HRESULT result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(result)) return result;

    ComPtr<IWICStream> stream;
    result = factory->CreateStream(stream.GetAddressOf());
    if (FAILED(result)) return result;
    // The decoder only reads this buffer and all WIC objects are released before it goes out of scope.
    result = stream->InitializeFromMemory(const_cast<BYTE*>(image.data()), static_cast<DWORD>(image.size()));
    if (FAILED(result)) return result;

    ComPtr<IWICBitmapDecoder> decoder;
    result = CoCreateInstance(*decoderClass, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(decoder.GetAddressOf()));
    if (FAILED(result)) return result;
    result = decoder->Initialize(stream.Get(), WICDecodeMetadataCacheOnDemand);
    if (FAILED(result)) return result;

    ComPtr<IWICBitmapFrameDecode> frame;
    result = decoder->GetFrame(0, frame.GetAddressOf());
    if (FAILED(result)) return result;

    UINT width = 0;
    UINT height = 0;
    result = frame->GetSize(&width, &height);
    if (FAILED(result)) return result;
    if (!width || !height || width > MaxSourceEdge || height > MaxSourceEdge ||
        static_cast<std::uint64_t>(width) * height > MaxSourcePixels) {
        return WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
    }

    ComPtr<IWICFormatConverter> converter;
    result = factory->CreateFormatConverter(converter.GetAddressOf());
    if (FAILED(result)) return result;
    result = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone,
        nullptr, 0, WICBitmapPaletteTypeCustom);
    if (FAILED(result)) return result;

    edge = std::min(edge, MaxThumbnailEdge);
    const UINT longestEdge = std::max(width, height);
    if (longestEdge <= edge) return CopyToBitmap(converter.Get(), width, height, bitmap, alpha);

    width = std::max(1u, (width * edge + longestEdge / 2) / longestEdge);
    height = std::max(1u, (height * edge + longestEdge / 2) / longestEdge);
    ComPtr<IWICBitmapScaler> scaler;
    result = factory->CreateBitmapScaler(scaler.GetAddressOf());
    if (FAILED(result)) return result;
    result = scaler->Initialize(converter.Get(), width, height, WICBitmapInterpolationModeFant);
    if (FAILED(result)) return result;

    return CopyToBitmap(scaler.Get(), width, height, bitmap, alpha);
}

} // namespace shapr
