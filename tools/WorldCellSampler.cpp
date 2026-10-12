#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <vector>

#include "../native/EDMWAnchored/ResourceAlignment.h"

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

struct AnchorHeader {
    char magic[8];
    uint32_t mapId;
    uint32_t width;
    uint32_t height;
    uint32_t variance;
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

template <typename T>
bool ReadRemote(HANDLE process, uintptr_t address, T& value) {
    SIZE_T read = 0;
    return ReadProcessMemory(process, reinterpret_cast<const void*>(address),
                             &value, sizeof(value), &read) && read == sizeof(value);
}

struct KindCounts {
    uint32_t expectedWater = 0;
    uint32_t expectedLand = 0;
    uint32_t negativeHeight = 0;
    uint32_t nonnegativeHeight = 0;
};
}

int main(int argc, char** argv) {
    if (argc != 4) {
        std::fprintf(stderr, "usage: WorldCellSampler.exe <pid> <trees.bin> <payload.edmwa>\n");
        return 2;
    }

    std::vector<uint8_t> snapshot;
    std::vector<uint8_t> payload;
    if (!ReadFileBytes(argv[2], snapshot) || !ReadFileBytes(argv[3], payload) ||
        snapshot.size() < sizeof(SnapshotHeader) || payload.size() < sizeof(AnchorHeader) + 4) {
        std::fprintf(stderr, "input read failed\n");
        return 3;
    }
    SnapshotHeader snapshotHeader = {};
    std::memcpy(&snapshotHeader, snapshot.data(), sizeof(snapshotHeader));
    AnchorHeader anchor = {};
    std::memcpy(&anchor, payload.data(), sizeof(anchor));
    if (std::memcmp(snapshotHeader.magic, "EDMWTR01", 8) != 0 ||
        std::memcmp(anchor.magic, "EDMWA07", 7) != 0 ||
        snapshotHeader.afterContextBytes < 0x6F4 || anchor.width == 0 || anchor.height == 0) {
        std::fprintf(stderr, "input format failed\n");
        return 4;
    }
    const size_t afterContextOffset = sizeof(SnapshotHeader) + snapshotHeader.beforeContextBytes;
    if (afterContextOffset + snapshotHeader.afterContextBytes > snapshot.size() ||
        afterContextOffset + 0x6F4 > snapshot.size()) {
        std::fprintf(stderr, "snapshot bounds failed\n");
        return 5;
    }
    uint32_t worldAddress = 0;
    std::memcpy(&worldAddress, snapshot.data() + afterContextOffset + 0x6F0,
                sizeof(worldAddress));

    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE,
                                 std::strtoul(argv[1], nullptr, 10));
    if (!process) {
        std::fprintf(stderr, "OpenProcess failed: %lu\n", GetLastError());
        return 6;
    }
    int32_t dimension = 0;
    uint32_t cellsAddress = 0;
    if (!ReadRemote(process, worldAddress + 0x150, dimension) ||
        !ReadRemote(process, worldAddress + 0x138, cellsAddress) ||
        dimension < 2 || dimension > 4096 || !cellsAddress) {
        std::fprintf(stderr, "world header read failed\n");
        CloseHandle(process);
        return 7;
    }
    const size_t worldCount = static_cast<size_t>(dimension) * dimension;
    std::vector<uint32_t> cellPointers(worldCount);
    SIZE_T read = 0;
    if (!ReadProcessMemory(process, reinterpret_cast<const void*>(cellsAddress),
                           cellPointers.data(), cellPointers.size() * sizeof(uint32_t), &read) ||
        read != cellPointers.size() * sizeof(uint32_t)) {
        std::fprintf(stderr, "cell pointer read failed\n");
        CloseHandle(process);
        return 8;
    }
    std::vector<uint8_t> playable(worldCount, 0);
    for (size_t index = 0; index < worldCount; ++index)
        playable[index] = cellPointers[index] ? 1 : 0;
    std::vector<uint32_t> sourceIndices;
    if (!edmw::BuildWorldSourceIndexMap(anchor.width, anchor.height,
                                        dimension, dimension, playable.data(), sourceIndices)) {
        std::fprintf(stderr, "projection failed\n");
        CloseHandle(process);
        return 9;
    }
    const size_t terrainOffset = sizeof(AnchorHeader) + sizeof(uint32_t);
    const size_t sourceCount = static_cast<size_t>(anchor.width) * anchor.height;
    if (terrainOffset + sourceCount > payload.size()) {
        std::fprintf(stderr, "payload terrain bounds failed\n");
        CloseHandle(process);
        return 10;
    }
    const uint8_t* terrain = payload.data() + terrainOffset;
    std::map<uint32_t, KindCounts> counts;
    uint32_t readable = 0;
    for (size_t index = 0; index < worldCount; ++index) {
        if (!cellPointers[index] || sourceIndices[index] == UINT32_MAX) continue;
        uint32_t kind = UINT32_MAX;
        float height = 0.0f;
        if (!ReadRemote(process, cellPointers[index] + 4, kind) ||
            !ReadRemote(process, cellPointers[index] + 0x10, height))
            continue;
        ++readable;
        auto& row = counts[kind];
        if (terrain[sourceIndices[index]] > 1) ++row.expectedLand;
        else ++row.expectedWater;
        if (height < 0.0f) ++row.negativeHeight;
        else ++row.nonnegativeHeight;
    }
    CloseHandle(process);

    std::printf("WORLD 0x%08X DIM %d READABLE %u\n", worldAddress, dimension, readable);
    for (const auto& item : counts)
        std::printf("KIND %u EXPECTED_WATER %u EXPECTED_LAND %u HEIGHT_NEGATIVE %u HEIGHT_NONNEGATIVE %u\n",
                    item.first, item.second.expectedWater, item.second.expectedLand,
                    item.second.negativeHeight, item.second.nonnegativeHeight);
    return 0;
}
