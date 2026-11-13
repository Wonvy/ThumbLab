#include "ProjectArchive.h"

extern "C" {
#include "third_party/puff/puff.h"
}

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <mutex>
#include <new>

namespace {

constexpr HRESULT InvalidZip = HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
constexpr HRESULT EntryMissing = HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
constexpr HRESULT EntryTooLarge = HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
constexpr std::uint32_t EndSignature = 0x06054b50;
constexpr std::uint32_t EntrySignature = 0x02014b50;
constexpr std::uint32_t LocalSignature = 0x04034b50;
constexpr std::size_t EndHeaderSize = 22;
constexpr std::size_t EntryHeaderSize = 46;
constexpr std::size_t LocalHeaderSize = 30;
constexpr std::size_t MaxDirectoryBytes = 4 * 1024 * 1024;
constexpr std::size_t MaxEntryBytes = 128 * 1024 * 1024;
constexpr std::uint16_t DataDescriptor = 0x0008;
constexpr std::uint16_t EncryptionFlags = 0x2041;

std::uint16_t UInt16(const BYTE* data)
{
    return static_cast<std::uint16_t>(data[0] | (data[1] << 8));
}

std::uint32_t UInt32(const BYTE* data)
{
    return UInt16(data) | (static_cast<std::uint32_t>(UInt16(data + 2)) << 16);
}

HRESULT ReadAt(IStream* stream, std::uint64_t offset, BYTE* data, std::size_t size)
{
    if (offset > static_cast<std::uint64_t>(std::numeric_limits<LONGLONG>::max())) return InvalidZip;
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(offset);
    HRESULT result = stream->Seek(position, STREAM_SEEK_SET, nullptr);
    if (FAILED(result)) return result;
    while (size != 0) {
        const ULONG requested = static_cast<ULONG>(size);
        ULONG read = 0;
        result = stream->Read(data, requested, &read);
        if (FAILED(result)) return result;
        if (read == 0 || read > requested) return InvalidZip;
        data += read;
        size -= read;
    }
    return S_OK;
}

struct Directory {
    std::uint32_t offset;
    std::uint32_t size;
    std::uint16_t entries;
};

HRESULT FindDirectory(IStream* stream, Directory& directory)
{
    ULARGE_INTEGER end{};
    const HRESULT seekResult = stream->Seek({}, STREAM_SEEK_END, &end);
    if (FAILED(seekResult)) return seekResult;
    if (end.QuadPart < EndHeaderSize) return InvalidZip;

    constexpr std::uint64_t MaxTailSize = 65535 + EndHeaderSize;
    const auto size = static_cast<std::size_t>(std::min(end.QuadPart, MaxTailSize));
    const std::uint64_t offset = end.QuadPart - size;
    std::vector<BYTE> tail(size);
    const HRESULT readResult = ReadAt(stream, offset, tail.data(), size);
    if (FAILED(readResult)) return readResult;
    for (std::size_t index = size - EndHeaderSize + 1; index != 0; --index) {
        const std::size_t headerOffset = index - 1;
        const BYTE* header = tail.data() + headerOffset;
        if (UInt32(header) != EndSignature || UInt16(header + 20) != size - EndHeaderSize - headerOffset) {
            continue;
        }

        const std::uint16_t disk = UInt16(header + 4);
        const std::uint16_t directoryDisk = UInt16(header + 6);
        const std::uint16_t diskEntries = UInt16(header + 8);
        const std::uint16_t entries = UInt16(header + 10);
        const std::uint32_t directorySize = UInt32(header + 12);
        const std::uint32_t directoryOffset = UInt32(header + 16);
        if (disk != 0 || directoryDisk != 0 || diskEntries != entries || entries == 0xffff ||
            directorySize > MaxDirectoryBytes || directorySize < entries * EntryHeaderSize ||
            static_cast<std::uint64_t>(directoryOffset) + directorySize != offset + headerOffset) {
            continue;
        }
        directory = {directoryOffset, directorySize, entries};
        return S_OK;
    }
    return InvalidZip;
}

struct Entry {
    std::uint16_t flags;
    std::uint16_t method;
    std::uint32_t checksum;
    std::uint32_t compressedSize;
    std::uint32_t size;
    std::uint32_t offset;
};

HRESULT FindEntry(IStream* stream, const Directory& directory, std::string_view name, Entry& entry)
{
    std::vector<BYTE> bytes(directory.size);
    const HRESULT result = ReadAt(stream, directory.offset, bytes.data(), bytes.size());
    if (FAILED(result)) return result;

    bool found = false;
    std::size_t offset = 0;
    for (unsigned index = 0; index < directory.entries; ++index) {
        const std::size_t remaining = bytes.size() - offset;
        if (remaining < EntryHeaderSize) return InvalidZip;
        const BYTE* record = bytes.data() + offset;
        if (UInt32(record) != EntrySignature) return InvalidZip;

        const std::size_t nameSize = UInt16(record + 28);
        const std::size_t recordSize = EntryHeaderSize + nameSize + UInt16(record + 30) + UInt16(record + 32);
        if (recordSize > remaining) return InvalidZip;
        const std::string_view recordName(reinterpret_cast<const char*>(record + EntryHeaderSize), nameSize);
        if (recordName == name) {
            if (found || UInt16(record + 34) != 0) return InvalidZip;
            entry = {UInt16(record + 8), UInt16(record + 10), UInt32(record + 16),
                UInt32(record + 20), UInt32(record + 24), UInt32(record + 42)};
            found = true;
        }
        offset += recordSize;
    }
    if (offset != bytes.size()) return InvalidZip;
    return found ? S_OK : EntryMissing;
}

constexpr std::array<std::uint32_t, 256> MakeCrcTable()
{
    std::array<std::uint32_t, 256> table{};
    for (std::uint32_t index = 0; index < table.size(); ++index) {
        std::uint32_t value = index;
        for (int bit = 0; bit < 8; ++bit) {
            value = (value >> 1) ^ ((value & 1) ? 0xedb88320u : 0u);
        }
        table[index] = value;
    }
    return table;
}

std::uint32_t Crc32(const std::vector<BYTE>& data)
{
    static constexpr auto table = MakeCrcTable();
    std::uint32_t checksum = 0xffffffffu;
    for (const BYTE byte : data) {
        checksum = (checksum >> 8) ^ table[(checksum ^ byte) & 0xff];
    }
    return checksum ^ 0xffffffffu;
}

HRESULT ExtractEntry(IStream* stream, const Directory& directory, const Entry& entry,
    std::string_view name, std::size_t maxBytes, std::vector<BYTE>& output)
{
    const std::size_t sizeLimit = std::min(maxBytes, MaxEntryBytes);
    if (entry.size > sizeLimit || entry.compressedSize > sizeLimit + 65536) return EntryTooLarge;
    if ((entry.flags & EncryptionFlags) != 0 || (entry.method != 0 && entry.method != 8)) return InvalidZip;
    if (entry.method == 0 && entry.size != entry.compressedSize) return InvalidZip;

    std::array<BYTE, LocalHeaderSize> header{};
    const std::uint64_t headerEnd = static_cast<std::uint64_t>(entry.offset) + header.size();
    if (headerEnd > directory.offset) return InvalidZip;
    HRESULT result = ReadAt(stream, entry.offset, header.data(), header.size());
    if (FAILED(result)) return result;
    if (UInt32(header.data()) != LocalSignature || UInt16(header.data() + 6) != entry.flags ||
        UInt16(header.data() + 8) != entry.method || UInt16(header.data() + 26) != name.size()) {
        return InvalidZip;
    }
    if ((entry.flags & DataDescriptor) == 0 &&
        (UInt32(header.data() + 14) != entry.checksum ||
         UInt32(header.data() + 18) != entry.compressedSize ||
         UInt32(header.data() + 22) != entry.size)) {
        return InvalidZip;
    }

    const std::uint64_t dataOffset = headerEnd + name.size() + UInt16(header.data() + 28);
    if (dataOffset + entry.compressedSize > directory.offset) return InvalidZip;
    std::vector<BYTE> localName(name.size());
    result = ReadAt(stream, headerEnd, localName.data(), localName.size());
    if (FAILED(result)) return result;
    if (std::string_view(reinterpret_cast<const char*>(localName.data()), localName.size()) != name) {
        return InvalidZip;
    }

    std::vector<BYTE> data(entry.size);
    if (entry.method == 0) {
        result = ReadAt(stream, dataOffset, data.data(), data.size());
        if (FAILED(result)) return result;
    } else {
        std::vector<BYTE> compressed(entry.compressedSize);
        result = ReadAt(stream, dataOffset, compressed.data(), compressed.size());
        if (FAILED(result)) return result;
        if (compressed.empty()) return InvalidZip;

        unsigned long written = entry.size;
        unsigned long consumed = entry.compressedSize;
        BYTE emptyDestination = 0;
        {
            // puff initializes its fixed-code tables on first use.
            static std::mutex decoderMutex;
            const std::lock_guard<std::mutex> lock(decoderMutex);
            if (puff(data.empty() ? &emptyDestination : data.data(), &written,
                    compressed.data(), &consumed) != 0) {
                return InvalidZip;
            }
        }
        if (written != entry.size || consumed != entry.compressedSize) return InvalidZip;
    }
    if (Crc32(data) != entry.checksum) return InvalidZip;
    output.swap(data);
    return S_OK;
}

} // namespace

HRESULT ReadProjectEntry(IStream* stream, std::string_view name, std::size_t maxBytes,
    std::vector<BYTE>& data)
{
    data.clear();
    if (!stream) return E_POINTER;
    if (name.empty() || name.size() > 65535) return E_INVALIDARG;

    try {
        Directory directory{};
        HRESULT result = FindDirectory(stream, directory);
        if (FAILED(result)) return result;
        Entry entry{};
        result = FindEntry(stream, directory, name, entry);
        if (FAILED(result)) return result;
        return ExtractEntry(stream, directory, entry, name, maxBytes, data);
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}
