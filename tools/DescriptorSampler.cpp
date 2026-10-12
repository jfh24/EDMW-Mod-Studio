#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace {
#pragma pack(push, 1)
struct GridHeader {
    uint32_t width;
    uint32_t height;
    uint32_t elementSize;
    uint32_t bytes;
};

struct SnapshotHeader {
    char magic[8];
    uint32_t version;
    uint32_t beforeContextBytes;
    uint32_t afterContextBytes;
    uint32_t primaryOffset;
    uint32_t beforePrimaryBytes;
    uint32_t beforeSecondaryBytes;
    uint32_t secondaryOffset;
    uint32_t afterPrimaryBytes;
    uint32_t afterSecondaryBytes;
    GridHeader beforeGrids[5];
    GridHeader afterGrids[5];
};
#pragma pack(pop)

bool ReadFileBytes(const char* path, std::vector<uint8_t>& bytes) {
    bytes.clear();
    FILE* file = nullptr;
    if (fopen_s(&file, path, "rb") != 0 || !file) return false;
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return false;
    }
    const long length = ftell(file);
    if (length < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return false;
    }
    bytes.resize(static_cast<size_t>(length));
    const bool read = bytes.empty() ||
        fread(bytes.data(), 1, bytes.size(), file) == bytes.size();
    fclose(file);
    return read;
}

bool Wanted(uint32_t globalId) {
    static const uint32_t ids[] = {
        261, 291, 292, 295, 301, 314, 315, 325, 326, 327, 349,
        370, 372, 373, 661, 1501, 1618, 1725, 1761, 1787, 1878
    };
    for (uint32_t id : ids)
        if (id == globalId) return true;
    return false;
}
}

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: DescriptorSampler.exe <pid> <trees.bin>\n");
        return 2;
    }
    std::vector<uint8_t> snapshot;
    if (!ReadFileBytes(argv[2], snapshot) || snapshot.size() < sizeof(SnapshotHeader)) {
        std::fprintf(stderr, "snapshot read failed\n");
        return 3;
    }
    SnapshotHeader header = {};
    std::memcpy(&header, snapshot.data(), sizeof(header));
    if (std::memcmp(header.magic, "EDMWTR01", 8) != 0 || header.afterContextBytes < 0x220) {
        std::fprintf(stderr, "snapshot format failed\n");
        return 4;
    }
    const size_t after = sizeof(SnapshotHeader) + header.beforeContextBytes;
    if (after + header.afterContextBytes > snapshot.size()) {
        std::fprintf(stderr, "snapshot bounds failed\n");
        return 5;
    }
    uint32_t begin = 0;
    uint32_t end = 0;
    std::memcpy(&begin, snapshot.data() + after + 0x218, sizeof(begin));
    std::memcpy(&end, snapshot.data() + after + 0x21C, sizeof(end));
    if (!begin || end < begin || (end - begin) % 0x1C != 0 ||
        (end - begin) / 0x1C > 4096) {
        std::fprintf(stderr, "descriptor vector header invalid: %08X %08X\n", begin, end);
        return 6;
    }
    const size_t bytes = end - begin;
    std::vector<uint8_t> descriptors(bytes);
    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE,
                                 std::strtoul(argv[1], nullptr, 10));
    if (!process) {
        std::fprintf(stderr, "OpenProcess failed: %lu\n", GetLastError());
        return 7;
    }
    SIZE_T read = 0;
    const bool ok = ReadProcessMemory(process, reinterpret_cast<const void*>(begin),
                                      descriptors.data(), descriptors.size(), &read) &&
                    read == descriptors.size();
    CloseHandle(process);
    if (!ok) {
        std::fprintf(stderr, "descriptor vector read failed: %lu (%zu/%zu)\n",
                     GetLastError(), static_cast<size_t>(read), descriptors.size());
        return 8;
    }
    const size_t count = descriptors.size() / 0x1C;
    std::printf("DESCRIPTORS BEGIN 0x%08X END 0x%08X COUNT %zu\n", begin, end, count);
    for (size_t index = 0; index < count; ++index) {
        uint32_t globalId = 0;
        std::memcpy(&globalId, descriptors.data() + index * 0x1C + 0x14,
                    sizeof(globalId));
        if (Wanted(globalId) || index < 8 || index == 59 || index == 66 ||
            index == 67 || index == 70 || index == 72 || index == 76 ||
            index == 79 || index == 80 || index == 81 || index == 83 ||
            index == 92 || index == 93 || index == 94 || index == 134 ||
            index == 510 || index == 611)
            std::printf("INDEX %zu GLOBAL %u\n", index, globalId);
    }
    return 0;
}
