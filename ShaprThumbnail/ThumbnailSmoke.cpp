#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string_view>
#include <type_traits>
#include <vector>

namespace {

using Microsoft::WRL::ComPtr;
using GetClassObject = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
using CanUnloadNow = HRESULT (STDAPICALLTYPE*)();
using Module = std::unique_ptr<std::remove_pointer_t<HMODULE>, decltype(&FreeLibrary)>;
using Bitmap = std::unique_ptr<std::remove_pointer_t<HBITMAP>, decltype(&DeleteObject)>;

constexpr CLSID providerClassId{
    0x7d076184, 0x5648, 0x487e, {0x9e, 0xb6, 0x59, 0x1d, 0xe2, 0x3c, 0x89, 0xc5}
};

bool CheckResult(HRESULT result, const wchar_t* operation)
{
    if (FAILED(result)) {
        std::fwprintf(stderr, L"%ls failed (0x%08lX)\n", operation, static_cast<unsigned long>(result));
        return false;
    }
    return true;
}

bool VerifyBitmap(HBITMAP bitmap, UINT edge, bool requireDib, const wchar_t* outputPath = nullptr)
{
    DIBSECTION section{};
    const int bytes = GetObjectW(bitmap, sizeof(section), &section);
    const LONG width = section.dsBm.bmWidth;
    const LONG height = section.dsBm.bmHeight;
    if (bytes == 0 || width <= 0 || height <= 0 || width > static_cast<LONG>(edge)
        || height > static_cast<LONG>(edge)
        || (requireDib && (bytes != static_cast<int>(sizeof(section)) || section.dsBm.bmBitsPixel != 32))) {
        std::fwprintf(stderr, L"Unexpected thumbnail bitmap (%ld x %ld)\n", width, height);
        return false;
    }

    std::vector<std::uint32_t> pixels(static_cast<std::size_t>(width) * height);
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(info.bmiHeader);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    const HDC dc = CreateCompatibleDC(nullptr);
    if (!dc) {
        return false;
    }
    const int rows = GetDIBits(dc, bitmap, 0, height, pixels.data(), &info, DIB_RGB_COLORS);
    DeleteDC(dc);
    if (rows != height) {
        std::fwprintf(stderr, L"Could not read thumbnail pixels\n");
        return false;
    }

    constexpr std::uint32_t rgbMask = 0x00ffffff;
    const auto firstColor = pixels.front() & rgbMask;
    const bool hasDetail = std::any_of(pixels.begin(), pixels.end(), [firstColor](auto pixel) {
        return (pixel & rgbMask) != firstColor;
    });
    if (!hasDetail) {
        std::fwprintf(stderr, L"Thumbnail was a single color\n");
        return false;
    }
    if (!outputPath) {
        return true;
    }

    info.bmiHeader.biSizeImage = static_cast<DWORD>(pixels.size() * sizeof(pixels.front()));
    BITMAPFILEHEADER header{};
    header.bfType = 0x4d42;
    header.bfOffBits = sizeof(header) + sizeof(info.bmiHeader);
    header.bfSize = header.bfOffBits + info.bmiHeader.biSizeImage;
    std::ofstream output(std::filesystem::path(outputPath), std::ios::binary);
    output.write(reinterpret_cast<const char*>(&header), sizeof(header));
    output.write(reinterpret_cast<const char*>(&info.bmiHeader), sizeof(info.bmiHeader));
    output.write(reinterpret_cast<const char*>(pixels.data()), info.bmiHeader.biSizeImage);
    output.close();
    if (!output) {
        std::fwprintf(stderr, L"Could not write %ls\n", outputPath);
        return false;
    }
    return true;
}

bool InitializeProvider(IClassFactory* factory, IStream* stream, ComPtr<IThumbnailProvider>& provider)
{
    ComPtr<IInitializeWithStream> initializer;
    if (!CheckResult(factory->CreateInstance(nullptr, IID_PPV_ARGS(initializer.GetAddressOf())), L"CreateInstance")) {
        return false;
    }

    return CheckResult(initializer->Initialize(stream, STGM_READ), L"Initialize")
        && CheckResult(initializer.As(&provider), L"QueryInterface(IThumbnailProvider)");
}

bool CheckProject(IClassFactory* factory, const wchar_t* path, const wchar_t* outputPath)
{
    ComPtr<IStream> stream;
    ComPtr<IThumbnailProvider> provider;
    if (!CheckResult(SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE,
            0, FALSE, nullptr, stream.GetAddressOf()), path)
        || !InitializeProvider(factory, stream.Get(), provider)) {
        return false;
    }

    for (const UINT edge : {256u, 48u}) {
        HBITMAP handle = nullptr;
        WTS_ALPHATYPE alpha = WTSAT_UNKNOWN;
        const HRESULT result = provider->GetThumbnail(edge, &handle, &alpha);
        const Bitmap bitmap(handle, DeleteObject);
        if (!CheckResult(result, L"GetThumbnail")
            || !VerifyBitmap(bitmap.get(), edge, true, edge == 256 ? outputPath : nullptr)) {
            return false;
        }
    }

    return true;
}

bool CheckInvalidProject(IClassFactory* factory)
{
    constexpr BYTE invalidProject[] = {'n', 'o', 't', ' ', 'a', ' ', 'S', 'H', 'A', 'P', 'R', ' ', 'p', 'r', 'o', 'j', 'e', 'c', 't'};
    ComPtr<IStream> stream;
    stream.Attach(SHCreateMemStream(invalidProject, static_cast<UINT>(sizeof(invalidProject))));
    ComPtr<IThumbnailProvider> emptyProvider;
    if (!stream || !InitializeProvider(factory, stream.Get(), emptyProvider)) {
        return false;
    }
    HBITMAP handle = nullptr;
    WTS_ALPHATYPE alpha = WTSAT_UNKNOWN;
    const HRESULT result = emptyProvider->GetThumbnail(256, &handle, &alpha);
    const Bitmap bitmap(handle, DeleteObject);
    if (SUCCEEDED(result) || bitmap) {
        std::fwprintf(stderr, L"Expected an invalid project to fail thumbnail extraction (0x%08lX)\n",
            static_cast<unsigned long>(result));
        return false;
    }
    return true;
}

bool TestDll(const wchar_t* path, const wchar_t* projectPath = nullptr, const wchar_t* outputPath = nullptr)
{
    const Module module(LoadLibraryW(path), FreeLibrary);
    if (!module) {
        std::fwprintf(stderr, L"Could not load %ls (Windows error %lu)\n", path, GetLastError());
        return false;
    }

    // GetProcAddress returns FARPROC for every export regardless of its actual signature.
#pragma warning(suppress : 4191)
    const auto getClass = reinterpret_cast<GetClassObject>(GetProcAddress(module.get(), "DllGetClassObject"));
#pragma warning(suppress : 4191)
    const auto canUnload = reinterpret_cast<CanUnloadNow>(GetProcAddress(module.get(), "DllCanUnloadNow"));
    if (!getClass || !canUnload) {
        std::fwprintf(stderr, L"Required COM exports are missing\n");
        return false;
    }

    ComPtr<IClassFactory> factory;
    if (!CheckResult(getClass(providerClassId, IID_PPV_ARGS(factory.GetAddressOf())), L"DllGetClassObject")
        || !CheckInvalidProject(factory.Get())
        || (projectPath && !CheckProject(factory.Get(), projectPath, outputPath))) {
        return false;
    }
    factory.Reset();
    if (canUnload() != S_OK) {
        std::fwprintf(stderr, L"DllCanUnloadNow still reports active objects\n");
        return false;
    }
    return true;
}

bool TestShell(const wchar_t* path, const wchar_t* outputPath = nullptr)
{
    ComPtr<IShellItem> item;
    ComPtr<IThumbnailCache> cache;
    if (!CheckResult(SHCreateItemFromParsingName(path, nullptr, IID_PPV_ARGS(item.GetAddressOf())), path)
        || !CheckResult(CoCreateInstance(CLSID_LocalThumbnailCache, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(cache.GetAddressOf())), L"CoCreateInstance(LocalThumbnailCache)")) {
        return false;
    }

    constexpr UINT edge = 256;
    ComPtr<ISharedBitmap> thumbnail;
    HBITMAP bitmap = nullptr;
    if (!CheckResult(cache->GetThumbnail(item.Get(), edge, WTS_EXTRACTDONOTCACHE,
        thumbnail.GetAddressOf(), nullptr, nullptr), path)
        || !CheckResult(thumbnail->GetSharedBitmap(&bitmap), L"GetSharedBitmap")) {
        return false;
    }
    return VerifyBitmap(bitmap, edge, false, outputPath);
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    const bool useShell = argc >= 3 && std::wstring_view(argv[1]) == L"--shell";
    const bool exportShell = argc == 4 && std::wstring_view(argv[1]) == L"--bmp";
    const bool useDll = (argc == 2 || argc == 3 || argc == 4) && argv[1][0] != L'-';
    if (!useShell && !exportShell && !useDll) {
        std::fwprintf(stderr, L"Usage: ShaprThumbnailSmoke <DLL> [project.shapr] [output BMP]\n"
            L"       ShaprThumbnailSmoke --shell <absolute .shapr path> [additional paths]\n"
            L"       ShaprThumbnailSmoke --bmp <output BMP> <absolute .shapr path>\n");
        return 2;
    }

    if (!CheckResult(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED), L"CoInitializeEx")) {
        return 1;
    }

    bool success = true;
    if (useShell) {
        for (int index = 2; index < argc; ++index) {
            success = TestShell(argv[index]) && success;
        }
    } else if (exportShell) {
        success = TestShell(argv[3], argv[2]);
    } else {
        success = TestDll(argv[1], argc >= 3 ? argv[2] : nullptr, argc == 4 ? argv[3] : nullptr);
    }

    CoUninitialize();
    return success ? 0 : 1;
}
