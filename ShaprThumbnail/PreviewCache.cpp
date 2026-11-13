#include "PreviewCache.h"

#include <bcrypt.h>
#include <shlobj.h>

#include <array>
#include <memory>
#include <string>

namespace shapr {
namespace {

constexpr size_t MaxProjectBytes = 256 * 1024 * 1024;
constexpr size_t MaxImageBytes = 16 * 1024 * 1024;
constexpr HRESULT NoPreview = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

struct HashContext {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<BYTE> storage;

    ~HashContext()
    {
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    }
};

HRESULT HashProject(IStream* project, std::array<BYTE, 32>& digest)
{
    LARGE_INTEGER offset{};
    ULARGE_INTEGER end{};
    HRESULT result = project->Seek(offset, STREAM_SEEK_END, &end);
    if (FAILED(result)) return result;
    if (end.QuadPart > MaxProjectBytes) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
    result = project->Seek(offset, STREAM_SEEK_SET, nullptr);
    if (FAILED(result)) return result;

    HashContext context;
    DWORD objectSize = 0;
    DWORD written = 0;
    if (BCryptOpenAlgorithmProvider(&context.algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0
        || BCryptGetProperty(context.algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<BYTE*>(&objectSize),
            sizeof(objectSize), &written, 0) < 0) {
        return E_FAIL;
    }
    context.storage.resize(objectSize);
    if (BCryptCreateHash(context.algorithm, &context.hash, context.storage.data(), objectSize,
        nullptr, 0, 0) < 0) {
        return E_FAIL;
    }

    std::array<BYTE, 65536> bytes;
    auto remaining = end.QuadPart;
    while (remaining != 0) {
        const ULONG request = static_cast<ULONG>(remaining < bytes.size() ? remaining : bytes.size());
        ULONG read = 0;
        result = project->Read(bytes.data(), request, &read);
        if (FAILED(result)) return result;
        if (!read || read > request) return STG_E_READFAULT;
        if (BCryptHashData(context.hash, bytes.data(), read, 0) < 0) return E_FAIL;
        remaining -= read;
    }
    if (BCryptFinishHash(context.hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) {
        return E_FAIL;
    }
    return S_OK;
}

} // namespace

HRESULT ReadCachedModelPreview(IStream* project, std::vector<BYTE>& image)
{
    PWSTR folderPath = nullptr;
    const HRESULT folderResult = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY,
        nullptr, &folderPath);
    const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> folder(folderPath, CoTaskMemFree);
    if (FAILED(folderResult)) return folderResult;

    std::wstring path(folder.get());
    path += L"\\ThumbLab\\ShaprThumbnail\\previews";
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        return NoPreview;
    }
    std::array<BYTE, 32> digest;
    const HRESULT hashResult = HashProject(project, digest);
    if (FAILED(hashResult)) return hashResult;
    path += L'\\';
    constexpr wchar_t digits[] = L"0123456789abcdef";
    for (BYTE value : digest) {
        path += digits[value >> 4];
        path += digits[value & 0xf];
    }
    path += L".image";

    const HANDLE handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    const std::unique_ptr<void, decltype(&CloseHandle)> file(handle, CloseHandle);
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file.get(), &size)) return HRESULT_FROM_WIN32(GetLastError());
    if (size.QuadPart <= 0 || size.QuadPart > MaxImageBytes) return NoPreview;
    image.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    if (!ReadFile(file.get(), image.data(), static_cast<DWORD>(image.size()), &read, nullptr)) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    return read == image.size() ? S_OK : STG_E_READFAULT;
}

} // namespace shapr
