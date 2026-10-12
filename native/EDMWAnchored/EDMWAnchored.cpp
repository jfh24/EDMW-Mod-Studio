#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <limits>
#include <map>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

#include "ResourceAlignment.h"

namespace {
// Legacy post-generation alignment is intentionally disabled. Painted land and stock-filtered
// eligibility are exposed before the seeded position stages, while Default.dll retains ownership
// of its occupancy grid and the resource domains it builds from those positions.
constexpr bool kEnablePostGenerationPlacementRewrite = false;
// Production builds never emit diagnostic artifacts. The tracing helpers remain
// available for controlled development builds, but are inert in the shipped DLL.
#ifdef EDMW_ENABLE_DIAGNOSTICS
constexpr bool kEnableProductionDiagnostics = true;
#else
constexpr bool kEnableProductionDiagnostics = false;
#endif
constexpr uint32_t kCurrentIdMask = 0x007FFFFFu;
constexpr uint32_t kMaxDimension = 4096;
constexpr size_t kConvergenceThresholdOffset = 0x4A8;
constexpr size_t kLandThresholdOffset = 0x4DC;
constexpr size_t kHeightPointerOffset = 0x100;
constexpr size_t kHeightWidthOffset = 0x104;
constexpr size_t kHeightHeightOffset = 0x108;
// Default.dll uses +0x118 as mutable resource-placement reservation state.
// Position generation temporarily borrows it as a zero-land/nonzero-water domain,
// so the original bytes are snapshotted and restored before height/resources.
constexpr size_t kResourceReservationPointerOffset = 0x118;
constexpr size_t kResourceReservationWidthOffset = 0x11C;
constexpr size_t kResourceReservationHeightOffset = 0x120;
constexpr size_t kSurfaceGridPointerOffset = 0x10C;
constexpr size_t kSurfaceGridWidthOffset = 0x110;
constexpr size_t kSurfaceGridHeightOffset = 0x114;
constexpr size_t kEligibilityMaskPointerOffset = 0x124;
constexpr size_t kEligibilityMaskWidthOffset = 0x128;
constexpr size_t kEligibilityMaskHeightOffset = 0x12C;
// +0x1F0 is a cache/flag grid. Bit 0x08 means an eligible cell currently has
// surface id zero; it is not a universal occupancy bit and is not a Fish gate.
constexpr size_t kSurfaceFreeCachePointerOffset = 0x1F0;
constexpr size_t kSurfaceFreeCacheWidthOffset = 0x1F4;
constexpr size_t kSurfaceFreeCacheHeightOffset = 0x1F8;
constexpr size_t kPositionVectorBeginOffset = 0x200;
constexpr size_t kPositionVectorEndOffset = 0x204;
constexpr size_t kPositionRecordSize = 0x54;
constexpr size_t kPositionXOffset = 0x30;
constexpr size_t kPositionYOffset = 0x34;
constexpr size_t kPositionGridXOffset = 0x38;
constexpr size_t kPositionGridYOffset = 0x3C;
constexpr size_t kPositionRadiusOffset = 0x40;
// Default.dll writes the 1-based player number here while constructing each
// non-neutral 0x54-byte position record. It is the stable lobby-slot identity.
constexpr size_t kPositionPlayerSlotOffset = 0x44;
constexpr size_t kPositionKindOffset = 0x48;
constexpr size_t kPositionMemberCountOffset = 0x4C;
constexpr size_t kPositionNeutralFlagOffset = 0x50;
constexpr size_t kMaxPositionRecords = 128;
constexpr size_t kTreePlacementVectorOffset = 0x248;
// The native callback receives host-generator +4. The executable's descriptor
// vector object is host +0x218, hence native-context +0x214.
constexpr size_t kUnitDescriptorVectorOffset = 0x214;
constexpr size_t kUnitDescriptorStride = 0x1C;
constexpr size_t kUnitDescriptorGlobalIdOffset = 0x14;
// Static analysis of the installed Default.dll parser and Empires_DMW.exe consumer
// proves one unified 24-byte unit-placement vector at +0x2BC. PlaceUnit,
// PlaceWorldUnit, PlaceUnitInRing, and PlaceWorldUnitInRing differ only in the
// coordinate/ring tag bytes. +0x2D4 is the unrelated 20-byte PlaceWall vector.
// Parser-created unit records are normally consumed once per non-neutral player.
// Current designer maps send no manual-fauna directives through that parser.
// The proxy validates exact build-specific descriptors and appends final records
// only after Trees, immediately before the later global unit consumer.
constexpr size_t kUnifiedUnitPlacementVectorOffset = 0x2BC;
constexpr size_t kUnifiedUnitPlacementVectorRangeOffset =
    kUnifiedUnitPlacementVectorOffset + sizeof(void*);
constexpr size_t kPlaceWallVectorOffset = 0x2D4;
constexpr size_t kCandidateUnitVectorEndOffset = 0x37C;
constexpr size_t kParserVectorStride = 0x18;
constexpr size_t kPlayerResourceRequestOffset = 0x394;
constexpr size_t kPlayerResourceTypeOffset = 0x3AC;
constexpr size_t kPlayerResourceRuleOffset = 0x3C4;
constexpr size_t kNeutralResourceRequestOffset = 0x3DC;
constexpr size_t kNeutralResourceTypeOffset = 0x3F4;
constexpr size_t kNeutralResourceRuleOffset = 0x40C;
constexpr size_t kMaximumLocalHeightVariationOffset = 0x4B0;
constexpr size_t kMaximumAnimalResourceElevationOffset = 0x4E4;
constexpr size_t kMaximumKindTwoResourceElevationOffset = 0x4E8;
constexpr size_t kPlayerResourceQuantityOffset = 0x5AC;
constexpr size_t kNeutralResourceQuantityOffset = 0x5C8;
constexpr size_t kMaxLoopCountOffset = 0x600;
constexpr size_t kResourceEdgeDistanceOffset = 0x630;
constexpr size_t kResourceWaterDistanceOffset = 0x634;
constexpr size_t kFishWaterDistanceOffset = 0x638;
constexpr size_t kRiverLandFillEnabledOffset = 0x6A4;
constexpr size_t kGeneratorExtentOffset = 0x5F4;
constexpr size_t kWorldPointerOffset = 0x6F0;
constexpr size_t kSecondaryWorldPointerOffset = 0x6F4;
constexpr size_t kWorldCellPointerOffset = 0x138;
constexpr size_t kWorldDimensionOffset = 0x150;
constexpr size_t kWorldCellKindOffset = 0x04;
constexpr size_t kWorldCellHeightOffset = 0x10;
// Live v2.0.7 diagnostics proved that declared land surfaces begin at 1024;
// Common Terrain Painting declares Beach, Grass, Forest, and Hills in that order.
constexpr uint32_t kGrasslandSurfaceValue = 1025;
constexpr uint32_t kPaintedTreeMaskFlag = 0x00000001;
constexpr uint32_t kManualResourcePlacementFlag = 0x00000002;
constexpr uint32_t kManualPlayerStartPlacementFlag = 0x00000004;
constexpr uint32_t kManualFaunaPlacementFlag = edmw::kManualFaunaPlacementFlag;
constexpr uint8_t kForestTerrainValue = 4;
constexpr int32_t kTreeResourceKind = 2;
constexpr int32_t kAnimalResourceKind = 4;
constexpr int32_t kFishResourceKind = 5;
constexpr uint32_t kFishGlobalUnitId = 0x13B;
constexpr uint32_t kMaximumGlobalUnitId = 0x800;
constexpr uintptr_t kGlobalUnitRegistryRva = 0x5D1BF8;
constexpr uint32_t kExpectedExeTimestamp = 0x41581681;
constexpr uint32_t kExpectedExeImageSize = 0x005DC000;
constexpr uint32_t kExpectedDefaultTimestamp = 0x3FF23A3B;
constexpr uint32_t kExpectedDefaultImageSize = 0x00027000;

volatile LONG g_mapId = 0;
HMODULE g_self = nullptr;
alignas(16) unsigned char g_generatorName[64] = {};
volatile LONG g_generatorNameState = 0;
SRWLOCK g_landLock = SRWLOCK_INIT;
bool g_fullLandEnvelopePrepared = false;
bool g_heightGridEngineEncoded = false;
int g_landEnvelopeWidth = 0;
int g_landEnvelopeHeight = 0;
uint32_t g_landEnvelopeMapId = 0;
std::vector<uint8_t> g_originalResourceReservations;
std::vector<uint8_t> g_originalEligibility;
int g_originalReservationWidth = 0;
int g_originalReservationHeight = 0;
int g_originalEligibilityWidth = 0;
int g_originalEligibilityHeight = 0;
uint32_t g_originalTopologyMapId = 0;

bool HeightGridUsesEngineLayout();
size_t HeightStorageIndex(int x, int y, int stride, int activeWidth, int activeHeight,
                          bool engineLayout);

#pragma pack(push, 1)
struct AnchorHeader {
    char magic[8];
    uint32_t mapId;
    uint32_t width;
    uint32_t height;
    uint32_t variance;
};
#pragma pack(pop)

struct AnchorData {
    AnchorHeader header = {};
    uint32_t flags = 0;
    std::vector<uint8_t> terrain;
    std::vector<uint8_t> roles;
    std::vector<int8_t> elevation;
    std::vector<uint8_t> ramps;
    std::vector<uint8_t> cliffs;
    struct ManualResource { uint8_t kind; int32_t x; int32_t y; };
    std::vector<ManualResource> resources;
    struct ManualPlayerStart {
        uint8_t slot;
        uint8_t reserved;
        int32_t x;
        int32_t y;
    };
    std::vector<ManualPlayerStart> playerStarts;
};

bool IsAccessibleRange(const void* address, size_t size, bool writable);

struct PositionBinding {
    uint8_t* record = nullptr;
    edmw::ResourcePosition position;
    int32_t playerSlot = 0;
    bool neutral = false;
};

using GeneratorFn = int (__cdecl*)(void*);
using SetValuesFn = int (__cdecl*)(void*, void*);

bool ModuleFolder(char (&folder)[MAX_PATH]) {
    const DWORD length = GetModuleFileNameA(g_self, folder, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;
    char* slash = strrchr(folder, '\\');
    if (!slash) return false;
    *slash = '\0';
    return true;
}

// Each exported map carries a marker named after its generator DLL, for example
// EDMW_123ABC.trace.  Keeping the marker map-specific lets the installer own it
// safely, including during an uninstall, without affecting other installed maps.
bool TracePath(const char* extension, char (&path)[MAX_PATH]) {
    const DWORD length = GetModuleFileNameA(g_self, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;
    char* slash = strrchr(path, '\\');
    char* dot = strrchr(path, '.');
    if (!slash || !dot || dot <= slash) return false;
    *dot = '\0';
    const size_t baseLength = strlen(path);
    const size_t extensionLength = strlen(extension);
    if (baseLength + extensionLength >= MAX_PATH) return false;
    memcpy(path + baseLength, extension, extensionLength + 1);
    return true;
}

bool TraceEnabled() {
    if (!kEnableProductionDiagnostics) return false;
    char enabled[MAX_PATH] = {};
    if (!TracePath(".trace", enabled)) return false;
    return GetFileAttributesA(enabled) != INVALID_FILE_ATTRIBUTES;
}

void Trace(const char* message, uint32_t value = 0) {
    if (!TraceEnabled()) return;
    char logPath[MAX_PATH] = {};
    if (!TracePath(".trace.log", logPath)) return;
    FILE* log = nullptr;
    if (fopen_s(&log, logPath, "a") == 0 && log) {
        fprintf(log, "%lu %s %08X\n", GetTickCount(), message, value);
        fclose(log);
    }
}

void TraceFaunaJson(const char* json) {
    if (!TraceEnabled() || !json) return;
    char extension[64] = {};
    _snprintf_s(extension, _TRUNCATE, ".fauna.%lu.jsonl", GetCurrentProcessId());
    char logPath[MAX_PATH] = {};
    if (!TracePath(extension, logPath)) return;
    FILE* log = nullptr;
    if (fopen_s(&log, logPath, "a") == 0 && log) {
        fputs(json, log);
        fputc('\n', log);
        fclose(log);
    }
}

uint64_t Fnv1a64(const void* bytes, size_t length, uint64_t hash = 14695981039346656037ull) {
    if (!bytes && length != 0) return 0;
    const auto* values = static_cast<const uint8_t*>(bytes);
    for (size_t index = 0; index < length; ++index)
        hash = (hash ^ values[index]) * 1099511628211ull;
    return hash;
}

uint32_t PackDimensions(int width, int height) {
    return (static_cast<uint32_t>((std::min)((std::max)(width, 0), 0xFFFF)) << 16) |
           static_cast<uint32_t>((std::min)((std::max)(height, 0), 0xFFFF));
}

bool IsAccessibleRange(const void* address, size_t size, bool writable) {
    if (!address || size == 0) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(address);
    const uintptr_t end = start + size;
    if (end < start) return false;

    uintptr_t cursor = start;
    while (cursor < end) {
        MEMORY_BASIC_INFORMATION info = {};
        if (VirtualQuery(reinterpret_cast<const void*>(cursor), &info, sizeof(info)) != sizeof(info) ||
            info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
            return false;
        if (writable) {
            const DWORD protection = info.Protect & 0xFFu;
            if (protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
                protection != PAGE_EXECUTE_READWRITE && protection != PAGE_EXECUTE_WRITECOPY)
                return false;
        }
        const uintptr_t regionEnd = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        if (regionEnd <= cursor) return false;
        cursor = (std::min)(end, regionEnd);
    }
    return true;
}

template <typename T>
bool ReadContext(const void* context, size_t offset, T& value) {
    const auto* source = static_cast<const uint8_t*>(context) + offset;
    if (!IsAccessibleRange(source, sizeof(T), false)) return false;
    memcpy(&value, source, sizeof(T));
    return true;
}

template <typename T>
bool WriteContext(void* context, size_t offset, const T& value) {
    auto* target = static_cast<uint8_t*>(context) + offset;
    if (!IsAccessibleRange(target, sizeof(T), true)) return false;
    memcpy(target, &value, sizeof(T));
    return true;
}

template <typename T>
bool ReadObjectField(const void* object, size_t offset, T& value) {
    if (!object) return false;
    const auto* source = static_cast<const uint8_t*>(object) + offset;
    if (!IsAccessibleRange(source, sizeof(T), false)) return false;
    memcpy(&value, source, sizeof(T));
    return true;
}

bool ReadActiveWorldDimensions(const void* context, int& width, int& height) {
    void* world = nullptr;
    int dimension = 0;
    width = 0;
    height = 0;
    if (!context || !ReadContext(context, kWorldPointerOffset, world) ||
        !ReadObjectField(world, kWorldDimensionOffset, dimension) ||
        dimension < 2 || dimension > static_cast<int>(kMaxDimension))
        return false;
    width = dimension;
    height = dimension;
    return true;
}

bool ReadGridDimensions(const void* context, size_t pointerOffset, size_t widthOffset,
                        size_t heightOffset, void*& values, int& width, int& height,
                        size_t elementSize) {
    values = nullptr;
    width = 0;
    height = 0;
    if (!ReadContext(context, pointerOffset, values) ||
        !ReadContext(context, widthOffset, width) ||
        !ReadContext(context, heightOffset, height) || !values ||
        width < 2 || height < 2 || width > static_cast<int>(kMaxDimension) ||
        height > static_cast<int>(kMaxDimension))
        return false;
    const size_t count = static_cast<size_t>(width) * static_cast<size_t>(height);
    return count <= static_cast<size_t>(kMaxDimension) * kMaxDimension &&
           IsAccessibleRange(values, count * elementSize, false);
}

void TraceGridDimensions(const void* context, size_t pointerOffset, size_t widthOffset,
                         size_t heightOffset, size_t elementSize, const char* message) {
    void* values = nullptr;
    int width = 0;
    int height = 0;
    Trace(message, ReadGridDimensions(context, pointerOffset, widthOffset, heightOffset,
                                      values, width, height, elementSize)
                       ? PackDimensions(width, height)
                       : UINT32_MAX);
}

bool ReadVectorRange(const void* context, size_t offset, size_t elementSize,
                     const uint8_t*& begin, size_t& count) {
    begin = nullptr;
    count = 0;
    const uint8_t* end = nullptr;
    if (!context || elementSize == 0 ||
        !ReadContext(context, offset + sizeof(void*), begin) ||
        !ReadContext(context, offset + (2 * sizeof(void*)), end)) return false;
    if (!begin && !end) return true;
    if (!begin || !end || end < begin) return false;
    const size_t bytes = static_cast<size_t>(end - begin);
    if (bytes % elementSize != 0 || bytes / elementSize > 4096 ||
        (bytes != 0 && !IsAccessibleRange(begin, bytes, false))) return false;
    count = bytes / elementSize;
    return true;
}

#pragma pack(push, 1)
struct UnifiedUnitPlacementRecord {
    float rotationRadians;
    float x;
    float y;
    uint32_t unitDescriptorIndexRaw;
    uint32_t auxiliaryRaw;
    uint8_t worldCoordinates;
    uint8_t inRing;
    uint8_t engineGenerated;
    uint8_t reservedPadding;
};

struct UnifiedUnitPlacementVectorRange {
    UnifiedUnitPlacementRecord* begin;
    UnifiedUnitPlacementRecord* end;
    UnifiedUnitPlacementRecord* capacity;
};

struct PlaceWallRecord {
    uint32_t words[5];
};
#pragma pack(pop)

static_assert(sizeof(UnifiedUnitPlacementRecord) == 24,
              "Unexpected unified unit-placement parser-record layout.");
static_assert(sizeof(UnifiedUnitPlacementVectorRange) == sizeof(void*) * 3,
              "Unexpected unified unit-placement vector range layout.");
static_assert(sizeof(PlaceWallRecord) == 20,
              "Unexpected PlaceWall parser-record layout.");

void HexEncode(const uint8_t* bytes, size_t length, char* output, size_t outputSize) {
    static constexpr char kHex[] = "0123456789ABCDEF";
    if (!output || outputSize == 0) return;
    if (!bytes || length > (outputSize - 1) / 2) {
        output[0] = '\0';
        return;
    }
    for (size_t index = 0; index < length; ++index) {
        output[index * 2] = kHex[bytes[index] >> 4];
        output[index * 2 + 1] = kHex[bytes[index] & 0x0F];
    }
    output[length * 2] = '\0';
}

const char* UnitPlacementTag(const UnifiedUnitPlacementRecord& record) {
    if (record.engineGenerated != 0) return "engine-generated";
    if (record.worldCoordinates != 0)
        return record.inRing != 0 ? "PlaceWorldUnitInRing" : "PlaceWorldUnit";
    return record.inRing != 0 ? "PlaceUnitInRing" : "PlaceUnit";
}

void TraceCandidateUnitVectors(const void* context, const char* phase) {
    if (!TraceEnabled() || !context || !phase) return;
    char json[768] = {};
    for (size_t offset = kUnifiedUnitPlacementVectorOffset;
         offset <= kCandidateUnitVectorEndOffset; offset += kParserVectorStride) {
        uint32_t header[6] = {};
        if (!ReadContext(context, offset, header)) continue;
        _snprintf_s(json, _TRUNCATE,
            "{\"event\":\"candidate_vector_header\",\"phase\":\"%s\","
            "\"offset\":%u,\"words\":[%u,%u,%u,%u,%u,%u]}",
            phase, static_cast<unsigned>(offset), header[0], header[1], header[2],
            header[3], header[4], header[5]);
        TraceFaunaJson(json);
    }

    const uint8_t* begin = nullptr;
    size_t count = 0;
    const bool unitRecordsReadable = ReadVectorRange(
        context, kUnifiedUnitPlacementVectorOffset,
        sizeof(UnifiedUnitPlacementRecord), begin, count);
    const uint64_t digest = unitRecordsReadable
        ? Fnv1a64(begin, count * sizeof(UnifiedUnitPlacementRecord)) : 0;
    _snprintf_s(json, _TRUNCATE,
        "{\"event\":\"unified_unit_placement_vector\",\"phase\":\"%s\","
        "\"contextOffset\":\"0x2BC\",\"recordBytes\":24,"
        "\"readable\":%s,\"count\":%u,\"digest\":\"%016llX\"}",
        phase, unitRecordsReadable ? "true" : "false", static_cast<unsigned>(count),
        static_cast<unsigned long long>(digest));
    TraceFaunaJson(json);
    if (unitRecordsReadable) {
        const auto* records = reinterpret_cast<const UnifiedUnitPlacementRecord*>(begin);
        for (size_t index = 0; index < (std::min)(count, static_cast<size_t>(128)); ++index) {
            const auto& record = records[index];
            uint32_t rotationBits = 0;
            uint32_t xBits = 0;
            uint32_t yBits = 0;
            memcpy(&rotationBits, &record.rotationRadians, sizeof(rotationBits));
            memcpy(&xBits, &record.x, sizeof(xBits));
            memcpy(&yBits, &record.y, sizeof(yBits));
            char raw[sizeof(UnifiedUnitPlacementRecord) * 2 + 1] = {};
            HexEncode(begin + index * sizeof(UnifiedUnitPlacementRecord),
                      sizeof(UnifiedUnitPlacementRecord), raw, sizeof(raw));
            _snprintf_s(json, _TRUNCATE,
                "{\"event\":\"unified_unit_placement_record\",\"phase\":\"%s\","
                "\"index\":%u,\"tag\":\"%s\",\"rotationBits\":\"%08X\","
                "\"rotationRadians\":%.9g,\"xBits\":\"%08X\",\"yBits\":\"%08X\","
                "\"x\":%.9g,\"y\":%.9g,\"unitDescriptorIndexRaw\":%u,"
                "\"auxiliaryRaw\":%u,"
                "\"coordinateMode\":\"%s\",\"worldCoordinates\":%u,"
                "\"inRing\":%u,\"engineGenerated\":%u,\"reservedPadding\":%u,"
                "\"raw\":\"%s\"}",
                phase, static_cast<unsigned>(index), UnitPlacementTag(record), rotationBits,
                static_cast<double>(record.rotationRadians), xBits, yBits,
                static_cast<double>(record.x), static_cast<double>(record.y),
                record.unitDescriptorIndexRaw, record.auxiliaryRaw,
                record.worldCoordinates != 0 ? "world" : "player-relative",
                static_cast<unsigned>(record.worldCoordinates),
                static_cast<unsigned>(record.inRing),
                static_cast<unsigned>(record.engineGenerated),
                static_cast<unsigned>(record.reservedPadding), raw);
            TraceFaunaJson(json);
        }
    }

    begin = nullptr;
    count = 0;
    const bool wallsReadable = ReadVectorRange(
        context, kPlaceWallVectorOffset, sizeof(PlaceWallRecord), begin, count);
    _snprintf_s(json, _TRUNCATE,
        "{\"event\":\"place_wall_vector\",\"phase\":\"%s\","
        "\"contextOffset\":\"0x2D4\",\"recordBytes\":20,"
        "\"readable\":%s,\"count\":%u,\"digest\":\"%016llX\"}",
        phase, wallsReadable ? "true" : "false", static_cast<unsigned>(count),
        static_cast<unsigned long long>(wallsReadable
            ? Fnv1a64(begin, count * sizeof(PlaceWallRecord)) : 0));
    TraceFaunaJson(json);
}

void TraceVectorCount(const void* context, size_t offset, size_t elementSize, const char* message) {
    const uint8_t* begin = nullptr;
    size_t count = 0;
    Trace(message, ReadVectorRange(context, offset, elementSize, begin, count)
                       ? static_cast<uint32_t>(count)
                       : UINT32_MAX);
}

void TraceResourceRecords(const void* context, const char* phase, size_t requestOffset,
                          size_t typeOffset, size_t ruleOffset, const char* scope) {
    const uint8_t* begin = nullptr;
    size_t count = 0;
    char message[96] = {};
    if (ReadVectorRange(context, requestOffset, 0x1C, begin, count)) {
        for (size_t index = 0; index < (std::min)(count, static_cast<size_t>(64)); ++index) {
            uint32_t resourceId = 0;
            uint32_t quantity = 0;
            memcpy(&resourceId, begin + index * 0x1C + 0x10, sizeof(resourceId));
            memcpy(&quantity, begin + index * 0x1C + 0x18, sizeof(quantity));
            _snprintf_s(message, _TRUNCATE, "Resource %s %s request %u id", phase, scope,
                        static_cast<unsigned>(index));
            Trace(message, resourceId);
            _snprintf_s(message, _TRUNCATE, "Resource %s %s request %u quantity", phase, scope,
                        static_cast<unsigned>(index));
            Trace(message, quantity);
        }
    }
    if (ReadVectorRange(context, typeOffset, 0x04, begin, count)) {
        for (size_t index = 0; index < (std::min)(count, static_cast<size_t>(64)); ++index) {
            uint32_t resourceId = 0;
            memcpy(&resourceId, begin + index * 0x04, sizeof(resourceId));
            _snprintf_s(message, _TRUNCATE, "Resource %s %s type %u", phase, scope,
                        static_cast<unsigned>(index));
            Trace(message, resourceId);
        }
    }
    if (ReadVectorRange(context, ruleOffset, 0x08, begin, count)) {
        for (size_t index = 0; index < (std::min)(count, static_cast<size_t>(64)); ++index) {
            uint32_t resourceId = 0;
            uint32_t flags = 0;
            memcpy(&resourceId, begin + index * 0x08, sizeof(resourceId));
            memcpy(&flags, begin + index * 0x08 + 0x04, sizeof(flags));
            _snprintf_s(message, _TRUNCATE, "Resource %s %s rule %u id", phase, scope,
                        static_cast<unsigned>(index));
            Trace(message, resourceId);
            _snprintf_s(message, _TRUNCATE, "Resource %s %s rule %u flags", phase, scope,
                        static_cast<unsigned>(index));
            Trace(message, flags);
        }
    }
}

void TraceResourceConfiguration(const void* context, const char* phase) {
    int maxLoopCount = 0;
    char message[96] = {};
    _snprintf_s(message, _TRUNCATE, "Resource %s max loop count", phase);
    Trace(message, ReadContext(context, kMaxLoopCountOffset, maxLoopCount)
                       ? static_cast<uint32_t>(maxLoopCount)
                       : UINT32_MAX);
    float maximumLandElevation = 0.0f;
    float maximumWaterElevation = 0.0f;
    uint32_t elevationBits = UINT32_MAX;
    if (ReadContext(context, kMaximumAnimalResourceElevationOffset, maximumLandElevation))
        memcpy(&elevationBits, &maximumLandElevation, sizeof(elevationBits));
    _snprintf_s(message, _TRUNCATE, "Resource %s maximum land elevation", phase);
    Trace(message, elevationBits);
    elevationBits = UINT32_MAX;
    if (ReadContext(context, kMaximumKindTwoResourceElevationOffset, maximumWaterElevation))
        memcpy(&elevationBits, &maximumWaterElevation, sizeof(elevationBits));
    _snprintf_s(message, _TRUNCATE, "Resource %s maximum water elevation", phase);
    Trace(message, elevationBits);

    for (size_t category = 0; category < 7; ++category) {
        uint32_t playerQuantity = UINT32_MAX;
        uint32_t neutralQuantity = UINT32_MAX;
        ReadContext(context, kPlayerResourceQuantityOffset + category * sizeof(uint32_t),
                    playerQuantity);
        ReadContext(context, kNeutralResourceQuantityOffset + category * sizeof(uint32_t),
                    neutralQuantity);
        _snprintf_s(message, _TRUNCATE, "Resource %s player category %u quantity", phase,
                    static_cast<unsigned>(category));
        Trace(message, playerQuantity);
        _snprintf_s(message, _TRUNCATE, "Resource %s neutral category %u quantity", phase,
                    static_cast<unsigned>(category));
        Trace(message, neutralQuantity);
    }

    struct VectorTrace {
        size_t offset;
        size_t elementSize;
        const char* name;
    };
    const VectorTrace vectors[] = {
        {kPlayerResourceRequestOffset, 0x1C, "player requests"},
        {kPlayerResourceTypeOffset, 0x04, "player types"},
        {kPlayerResourceRuleOffset, 0x08, "player rules"},
        {kNeutralResourceRequestOffset, 0x1C, "neutral requests"},
        {kNeutralResourceTypeOffset, 0x04, "neutral types"},
        {kNeutralResourceRuleOffset, 0x08, "neutral rules"}
    };
    for (const auto& vector : vectors) {
        _snprintf_s(message, _TRUNCATE, "Resource %s %s", phase, vector.name);
        TraceVectorCount(context, vector.offset, vector.elementSize, message);
    }
    if (strcmp(phase, "before") == 0) {
        TraceResourceRecords(context, phase, kPlayerResourceRequestOffset,
                             kPlayerResourceTypeOffset, kPlayerResourceRuleOffset, "player");
        TraceResourceRecords(context, phase, kNeutralResourceRequestOffset,
                             kNeutralResourceTypeOffset, kNeutralResourceRuleOffset, "neutral");
    }
}

constexpr size_t kTreeTraceContextBytes = 0x800;
constexpr size_t kPrimaryTreeVectorOffset = kTreePlacementVectorOffset;
constexpr size_t kSecondaryTreeVectorOffset = 0x338;
constexpr size_t kMaximumTreeTraceVectorBytes = 4 * 1024 * 1024;
constexpr size_t kMaximumTreeTraceGridBytes = 8 * 1024 * 1024;

struct TreeGridSnapshot {
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t elementSize = 0;
    std::vector<uint8_t> bytes;
};

struct TreeStageSnapshot {
    std::vector<uint8_t> context;
    std::vector<uint8_t> primary;
    std::vector<uint8_t> secondary;
    TreeGridSnapshot grids[5];
};

#pragma pack(push, 1)
struct TreeTraceGridHeader {
    uint32_t width;
    uint32_t height;
    uint32_t elementSize;
    uint32_t bytes;
};

struct TreeTraceSnapshotHeader {
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
    TreeTraceGridHeader beforeGrids[5];
    TreeTraceGridHeader afterGrids[5];
};
#pragma pack(pop)

bool CaptureTreeContext(const void* context, std::vector<uint32_t>& words) {
    words.clear();
    if (!TraceEnabled() || !context ||
        !IsAccessibleRange(context, kTreeTraceContextBytes, false))
        return false;
    const auto* bytes = static_cast<const uint8_t*>(context);
    words.resize(kTreeTraceContextBytes / sizeof(uint32_t));
    memcpy(words.data(), bytes, kTreeTraceContextBytes);
    return true;
}

void TraceTreeContextChanges(const std::vector<uint32_t>& before,
                             const std::vector<uint32_t>& after) {
    if (before.size() != after.size() || before.empty()) return;
    uint32_t changed = 0;
    for (size_t index = 0; index < before.size(); ++index) {
        if (before[index] == after[index]) continue;
        ++changed;
        if (changed > 96) continue;
        char message[96] = {};
        _snprintf_s(message, _TRUNCATE, "Trees context offset %03X before",
                    static_cast<unsigned>(index * sizeof(uint32_t)));
        Trace(message, before[index]);
        _snprintf_s(message, _TRUNCATE, "Trees context offset %03X after",
                    static_cast<unsigned>(index * sizeof(uint32_t)));
        Trace(message, after[index]);
    }
    Trace("Trees context changed words", changed);
}

bool CaptureTreeVector(const void* context, size_t offset, std::vector<uint8_t>& bytes) {
    bytes.clear();
    uint8_t* begin = nullptr;
    uint8_t* end = nullptr;
    uint8_t* capacity = nullptr;
    if (!ReadContext(context, offset, begin) ||
        !ReadContext(context, offset + sizeof(begin), end) ||
        !ReadContext(context, offset + sizeof(begin) * 2, capacity))
        return false;
    if (!begin && !end && !capacity) return true;
    if (!begin || !end || !capacity || end < begin || capacity < end) return false;

    const size_t length = static_cast<size_t>(end - begin);
    if (length > kMaximumTreeTraceVectorBytes ||
        (length != 0 && !IsAccessibleRange(begin, length, false)))
        return false;
    bytes.resize(length);
    if (length != 0) memcpy(bytes.data(), begin, length);
    return true;
}

bool CaptureTreeGrid(const void* context, size_t pointerOffset, size_t widthOffset,
                     size_t heightOffset, size_t elementSize, TreeGridSnapshot& snapshot) {
    snapshot = {};
    void* values = nullptr;
    int width = 0;
    int height = 0;
    if (!ReadGridDimensions(context, pointerOffset, widthOffset, heightOffset,
                            values, width, height, elementSize))
        return false;
    const size_t bytes = static_cast<size_t>(width) * height * elementSize;
    if (bytes > kMaximumTreeTraceGridBytes) return false;
    snapshot.width = static_cast<uint32_t>(width);
    snapshot.height = static_cast<uint32_t>(height);
    snapshot.elementSize = static_cast<uint32_t>(elementSize);
    snapshot.bytes.resize(bytes);
    memcpy(snapshot.bytes.data(), values, bytes);
    return true;
}

bool CaptureTreeStageSnapshot(const void* context, TreeStageSnapshot& snapshot) {
    snapshot = {};
    if (!TraceEnabled() || !context ||
        !IsAccessibleRange(context, kTreeTraceContextBytes, false))
        return false;
    snapshot.context.resize(kTreeTraceContextBytes);
    memcpy(snapshot.context.data(), context, snapshot.context.size());
    const bool primary = CaptureTreeVector(context, kPrimaryTreeVectorOffset, snapshot.primary);
    const bool secondary = CaptureTreeVector(context, kSecondaryTreeVectorOffset, snapshot.secondary);
    const bool height = CaptureTreeGrid(context, kHeightPointerOffset, kHeightWidthOffset,
                                        kHeightHeightOffset, sizeof(float), snapshot.grids[0]);
    const bool surface = CaptureTreeGrid(context, kSurfaceGridPointerOffset, kSurfaceGridWidthOffset,
                                         kSurfaceGridHeightOffset, sizeof(uint32_t), snapshot.grids[1]);
    const bool land = CaptureTreeGrid(context, kResourceReservationPointerOffset,
                                      kResourceReservationWidthOffset,
                                      kResourceReservationHeightOffset,
                                      sizeof(uint8_t), snapshot.grids[2]);
    const bool eligibility = CaptureTreeGrid(context, kEligibilityMaskPointerOffset,
                                             kEligibilityMaskWidthOffset, kEligibilityMaskHeightOffset,
                                             sizeof(uint8_t), snapshot.grids[3]);
    const bool occupancy = CaptureTreeGrid(context, kSurfaceFreeCachePointerOffset,
                                           kSurfaceFreeCacheWidthOffset,
                                           kSurfaceFreeCacheHeightOffset,
                                           sizeof(uint8_t), snapshot.grids[4]);
    return primary || secondary || height || surface || land || eligibility || occupancy;
}

void WriteTreeGridHeader(TreeTraceGridHeader& header, const TreeGridSnapshot& snapshot) {
    header.width = snapshot.width;
    header.height = snapshot.height;
    header.elementSize = snapshot.elementSize;
    header.bytes = static_cast<uint32_t>(snapshot.bytes.size());
}

void WriteTreeSnapshotBytes(FILE* output, const std::vector<uint8_t>& bytes) {
    if (!bytes.empty()) fwrite(bytes.data(), 1, bytes.size(), output);
}

// The stock generator's tree stage fills two previously empty vectors. Preserve all
// tree-stage inputs and outputs in a single self-contained capture. This avoids
// serial manual probes when a later analysis needs another related engine field.
void WriteTreeStageSnapshot(const TreeStageSnapshot& before, const TreeStageSnapshot& after) {
    if (before.context.empty() && after.context.empty()) return;
    Trace("Trees primary before vector bytes", static_cast<uint32_t>(before.primary.size()));
    Trace("Trees secondary before vector bytes", static_cast<uint32_t>(before.secondary.size()));
    Trace("Trees primary after vector bytes", static_cast<uint32_t>(after.primary.size()));
    Trace("Trees secondary after vector bytes", static_cast<uint32_t>(after.secondary.size()));

    char snapshotPath[MAX_PATH] = {};
    if (!TracePath(".trees.bin", snapshotPath)) return;
    FILE* output = nullptr;
    if (fopen_s(&output, snapshotPath, "wb") != 0 || !output) return;

    TreeTraceSnapshotHeader header = {};
    memcpy(header.magic, "EDMWTR01", sizeof(header.magic));
    header.version = 2;
    header.beforeContextBytes = static_cast<uint32_t>(before.context.size());
    header.afterContextBytes = static_cast<uint32_t>(after.context.size());
    header.primaryOffset = static_cast<uint32_t>(kPrimaryTreeVectorOffset);
    header.secondaryOffset = static_cast<uint32_t>(kSecondaryTreeVectorOffset);
    header.beforePrimaryBytes = static_cast<uint32_t>(before.primary.size());
    header.beforeSecondaryBytes = static_cast<uint32_t>(before.secondary.size());
    header.afterPrimaryBytes = static_cast<uint32_t>(after.primary.size());
    header.afterSecondaryBytes = static_cast<uint32_t>(after.secondary.size());
    for (size_t index = 0; index < 5; ++index) {
        WriteTreeGridHeader(header.beforeGrids[index], before.grids[index]);
        WriteTreeGridHeader(header.afterGrids[index], after.grids[index]);
    }
    fwrite(&header, sizeof(header), 1, output);
    WriteTreeSnapshotBytes(output, before.context);
    WriteTreeSnapshotBytes(output, after.context);
    WriteTreeSnapshotBytes(output, before.primary);
    WriteTreeSnapshotBytes(output, before.secondary);
    WriteTreeSnapshotBytes(output, after.primary);
    WriteTreeSnapshotBytes(output, after.secondary);
    for (size_t index = 0; index < 5; ++index) WriteTreeSnapshotBytes(output, before.grids[index].bytes);
    for (size_t index = 0; index < 5; ++index) WriteTreeSnapshotBytes(output, after.grids[index].bytes);
    fclose(output);
    Trace("Trees snapshot written", header.afterPrimaryBytes + header.afterSecondaryBytes);
}

void TraceSurfaceSemantics(const uint32_t* surfaceGrid, size_t length, const char* phase) {
    if (!surfaceGrid || !phase) return;
    std::map<uint32_t, uint32_t> counts;
    for (size_t index = 0; index < length; ++index) {
        const uint32_t value = surfaceGrid[index];
        if (value != 0) ++counts[value];
    }
    char message[96] = {};
    _snprintf_s(message, _TRUNCATE, "Surface %s distinct ids", phase);
    Trace(message, static_cast<uint32_t>(counts.size()));
    size_t emitted = 0;
    for (const auto& entry : counts) {
        if (emitted++ == 64) break;
        _snprintf_s(message, _TRUNCATE, "Surface %s id %u cells", phase, entry.first);
        Trace(message, entry.second);
    }
}

void TraceStageState(void* context) {
    int generatorExtent = 0;
    int activeWorldWidth = 0;
    int activeWorldHeight = 0;
    Trace("Stage generator extent",
          ReadContext(context, kGeneratorExtentOffset, generatorExtent)
              ? static_cast<uint32_t>(generatorExtent)
              : UINT32_MAX);
    Trace("Stage active dimensions",
          ReadActiveWorldDimensions(context, activeWorldWidth, activeWorldHeight)
              ? PackDimensions(activeWorldWidth, activeWorldHeight)
              : UINT32_MAX);
    TraceGridDimensions(context, kHeightPointerOffset, kHeightWidthOffset,
                        kHeightHeightOffset, sizeof(float), "Stage height dimensions");
    TraceGridDimensions(context, kSurfaceGridPointerOffset, kSurfaceGridWidthOffset,
                        kSurfaceGridHeightOffset, sizeof(uint32_t), "Stage surface dimensions");
    TraceGridDimensions(context, kResourceReservationPointerOffset,
                        kResourceReservationWidthOffset,
                        kResourceReservationHeightOffset, sizeof(uint8_t),
                        "Stage resource reservation dimensions");
    TraceGridDimensions(context, kEligibilityMaskPointerOffset, kEligibilityMaskWidthOffset,
                        kEligibilityMaskHeightOffset, sizeof(uint8_t), "Stage eligibility dimensions");
    TraceGridDimensions(context, kSurfaceFreeCachePointerOffset,
                        kSurfaceFreeCacheWidthOffset,
                        kSurfaceFreeCacheHeightOffset, sizeof(uint8_t),
                        "Stage surface-free cache dimensions");

    float* heights = nullptr;
    int width = 0;
    int height = 0;
    float threshold = 0.0f;
    void* raw = nullptr;
    if (ReadGridDimensions(context, kHeightPointerOffset, kHeightWidthOffset,
                           kHeightHeightOffset, raw, width, height, sizeof(float)) &&
        ReadContext(context, kLandThresholdOffset, threshold)) {
        heights = static_cast<float*>(raw);
        const size_t count = static_cast<size_t>(width) * height;
        Trace("Stage height land", static_cast<uint32_t>(std::count_if(
            heights, heights + count, [threshold](float value) { return value >= threshold; })));
        const int activeWidth = (std::min)(width, activeWorldWidth > 1 ? activeWorldWidth : width);
        const int activeHeight = (std::min)(height, activeWorldHeight > 1 ? activeWorldHeight : height);
        const bool engineLayout = HeightGridUsesEngineLayout();
        uint32_t quadrants[4] = {};
        for (int y = 0; y < activeHeight; ++y) {
            for (int x = 0; x < activeWidth; ++x) {
                if (heights[HeightStorageIndex(
                        x, y, width, activeWidth, activeHeight, engineLayout)] < threshold)
                    continue;
                const int quadrant = (x >= activeWidth / 2 ? 1 : 0) |
                                     (y >= activeHeight / 2 ? 2 : 0);
                ++quadrants[quadrant];
            }
        }
        Trace("Stage height quadrant 0", quadrants[0]);
        Trace("Stage height quadrant 1", quadrants[1]);
        Trace("Stage height quadrant 2", quadrants[2]);
        Trace("Stage height quadrant 3", quadrants[3]);
    }

    if (ReadGridDimensions(context, kResourceReservationPointerOffset,
                           kResourceReservationWidthOffset,
                           kResourceReservationHeightOffset,
                           raw, width, height, sizeof(uint8_t))) {
        const auto* values = static_cast<const uint8_t*>(raw);
        const size_t count = static_cast<size_t>(width) * height;
        Trace("Stage clear resource reservation cells", static_cast<uint32_t>(
            std::count(values, values + count, static_cast<uint8_t>(0))));
    }

    if (ReadGridDimensions(context, kSurfaceGridPointerOffset, kSurfaceGridWidthOffset,
                           kSurfaceGridHeightOffset, raw, width, height, sizeof(uint32_t))) {
        const auto* values = static_cast<const uint32_t*>(raw);
        const size_t count = static_cast<size_t>(width) * height;
        Trace("Stage surface grid", static_cast<uint32_t>(std::count_if(
            values, values + count, [](uint32_t value) { return value != 0; })));
    }

    if (ReadGridDimensions(context, kEligibilityMaskPointerOffset, kEligibilityMaskWidthOffset,
                           kEligibilityMaskHeightOffset, raw, width, height, sizeof(uint8_t))) {
        const auto* values = static_cast<const uint8_t*>(raw);
        const size_t count = static_cast<size_t>(width) * height;
        Trace("Stage eligibility mask", static_cast<uint32_t>(std::count_if(
            values, values + count, [](uint8_t value) { return value != 0; })));
    }

    if (ReadGridDimensions(context, kSurfaceFreeCachePointerOffset,
                           kSurfaceFreeCacheWidthOffset,
                           kSurfaceFreeCacheHeightOffset,
                           raw, width, height, sizeof(uint8_t))) {
        const auto* values = static_cast<const uint8_t*>(raw);
        const size_t count = static_cast<size_t>(width) * height;
        Trace("Stage surface-free cache flags", static_cast<uint32_t>(std::count_if(
            values, values + count, [](uint8_t value) { return value != 0; })));
    }
}

void TraceHeightTransitions(void* context, const char* phase) {
    if (!context || !phase) return;
    void* raw = nullptr;
    int width = 0;
    int height = 0;
    int activeWidth = 0;
    int activeHeight = 0;
    float threshold = 0.0f;
    if (!ReadGridDimensions(context, kHeightPointerOffset, kHeightWidthOffset,
                            kHeightHeightOffset, raw, width, height, sizeof(float)) ||
        !ReadActiveWorldDimensions(context, activeWidth, activeHeight) ||
        !ReadContext(context, kLandThresholdOffset, threshold))
        return;
    activeWidth = (std::min)(activeWidth, width);
    activeHeight = (std::min)(activeHeight, height);
    const auto* heights = static_cast<const float*>(raw);
    const bool engineLayout = HeightGridUsesEngineLayout();
    const auto heightAt = [=](int x, int y) {
        return heights[HeightStorageIndex(
            x, y, width, activeWidth, activeHeight, engineLayout)];
    };
    uint32_t overQuarter = 0;
    uint32_t overHalf = 0;
    uint32_t overOne = 0;
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const float value = heightAt(x, y);
            if (value < threshold) continue;
            if (x + 1 < activeWidth) {
                const float next = heightAt(x + 1, y);
                if (next >= threshold) {
                    const float delta = std::fabs(value - next);
                    if (delta > 0.25f) ++overQuarter;
                    if (delta > 0.50f) ++overHalf;
                    if (delta > 1.00f) ++overOne;
                }
            }
            if (y + 1 < activeHeight) {
                const float next = heightAt(x, y + 1);
                if (next >= threshold) {
                    const float delta = std::fabs(value - next);
                    if (delta > 0.25f) ++overQuarter;
                    if (delta > 0.50f) ++overHalf;
                    if (delta > 1.00f) ++overOne;
                }
            }
        }
    }
    char message[96] = {};
    _snprintf_s(message, _TRUNCATE, "Height transitions %s over 0.25", phase);
    Trace(message, overQuarter);
    _snprintf_s(message, _TRUNCATE, "Height transitions %s over 0.50", phase);
    Trace(message, overHalf);
    _snprintf_s(message, _TRUNCATE, "Height transitions %s over 1.00", phase);
    Trace(message, overOne);
}

uint32_t ExtractTransportMarker(void* context) {
    uint32_t bits = 0;
    if (!context || !ReadContext(context, kConvergenceThresholdOffset, bits)) return 0;
    if ((bits & 0x7F800000u) != 0x3F800000u) return 0;

    const uint32_t id = bits & kCurrentIdMask;
    const uint32_t stockThreshold = 0x3F800000u;
    if (!WriteContext(context, kConvergenceThresholdOffset, stockThreshold)) return 0;
    return id;
}

uint32_t ExtractModuleMapId() {
    char path[MAX_PATH + 1] = {};
    const DWORD length = GetModuleFileNameA(g_self, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return 0;

    const char* fileName = strrchr(path, '\\');
    fileName = fileName ? fileName + 1 : path;
    constexpr char currentPrefix[] = "EDMW_";
    if (_strnicmp(fileName, currentPrefix, sizeof(currentPrefix) - 1) != 0) return 0;
    const char* hex = fileName + sizeof(currentPrefix) - 1;

    if (strlen(hex) != 10 || _stricmp(hex + 6, ".dll") != 0) return 0;
    char idText[7] = {};
    memcpy(idText, hex, 6);
    char* end = nullptr;
    const unsigned long parsed = strtoul(idText, &end, 16);
    if (!end || *end != '\0' || parsed == 0 || parsed > kCurrentIdMask) return 0;
    return static_cast<uint32_t>(parsed);
}

// EDMW rejects an RMV unless UseMapGenerator matches the name returned by its DLL.
bool ReadModuleGeneratorName(char (&name)[MAX_PATH]) {
    char path[MAX_PATH + 1] = {};
    const DWORD length = GetModuleFileNameA(g_self, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;

    const char* fileName = strrchr(path, '\\');
    fileName = fileName ? fileName + 1 : path;
    const char* extension = strrchr(fileName, '.');
    if (!extension || _stricmp(extension, ".dll") != 0) return false;

    const size_t nameLength = static_cast<size_t>(extension - fileName);
    if (nameLength == 0 || nameLength >= MAX_PATH) return false;
    memcpy(name, fileName, nameLength);
    name[nameLength] = '\0';
    return true;
}

extern "C" FARPROC __cdecl ResolveStock(const char* name) {
    HMODULE stock = GetModuleHandleA("Default.dll");
    if (!stock) stock = LoadLibraryA("Default.dll");
    return stock ? GetProcAddress(stock, name) : nullptr;
}

#if defined(__MINGW32__)
#define STOCK_TAIL_EXPORT(functionName) \
    extern "C" const char stockName_##functionName[] = #functionName; \
    extern "C" __attribute__((dllexport, naked)) void functionName() { \
        __asm__ __volatile__( \
            "pushl $_stockName_" #functionName "\n\t" \
            "call _ResolveStock\n\t" \
            "addl $4, %esp\n\t" \
            "testl %eax, %eax\n\t" \
            "jz 1f\n\t" \
            "jmp *%eax\n\t" \
            "1: xorl %eax, %eax\n\t" \
            "ret\n\t"); \
    }
#else
#define STOCK_TAIL_EXPORT(functionName) \
    extern "C" const char stockName_##functionName[] = #functionName; \
    extern "C" __declspec(dllexport, naked) void functionName() { \
        __asm { push offset stockName_##functionName } \
        __asm { call ResolveStock } \
        __asm { add esp, 4 } \
        __asm { test eax, eax } \
        __asm { jz missing_##functionName } \
        __asm { jmp eax } \
        __asm { missing_##functionName: xor eax, eax } \
        __asm { ret } \
    }
#endif

STOCK_TAIL_EXPORT(PIMGetType)
STOCK_TAIL_EXPORT(PIMIsValid)

void* GeneratorName() {
    const LONG state = InterlockedCompareExchange(&g_generatorNameState, 1, 0);
    if (state == 0) {
        char moduleName[MAX_PATH] = {};
        HMODULE engine = GetModuleHandleA("Low-Level Engine.dll");
        if (!engine) engine = LoadLibraryA("Low-Level Engine.dll");
        using StringConstructor = void* (__thiscall*)(void*, const char*);
        const auto construct = engine ? reinterpret_cast<StringConstructor>(GetProcAddress(
            engine, "??0UWideString@@QAE@PBD@Z")) : nullptr;
        if (construct && ReadModuleGeneratorName(moduleName)) {
            construct(g_generatorName, moduleName);
            InterlockedExchange(&g_generatorNameState, 2);
        } else {
            InterlockedExchange(&g_generatorNameState, 0);
        }
    } else {
        while (InterlockedCompareExchange(&g_generatorNameState, 0, 0) == 1) Sleep(0);
    }
    return InterlockedCompareExchange(&g_generatorNameState, 0, 0) == 2 ? g_generatorName : nullptr;
}

void ResetLandEnvelope() {
    AcquireSRWLockExclusive(&g_landLock);
    g_fullLandEnvelopePrepared = false;
    g_heightGridEngineEncoded = false;
    g_landEnvelopeWidth = 0;
    g_landEnvelopeHeight = 0;
    g_landEnvelopeMapId = 0;
    g_originalResourceReservations.clear();
    g_originalEligibility.clear();
    g_originalReservationWidth = 0;
    g_originalReservationHeight = 0;
    g_originalEligibilityWidth = 0;
    g_originalEligibilityHeight = 0;
    g_originalTopologyMapId = 0;
    ReleaseSRWLockExclusive(&g_landLock);
}

bool HeightGridUsesEngineLayout() {
    AcquireSRWLockShared(&g_landLock);
    const bool encoded = g_heightGridEngineEncoded;
    ReleaseSRWLockShared(&g_landLock);
    return encoded;
}

size_t HeightStorageIndex(int x, int y, int stride, int activeWidth, int activeHeight,
                          bool engineLayout) {
    return engineLayout && x < activeWidth && y < activeHeight
        ? static_cast<size_t>(x) * stride + y
        : static_cast<size_t>(y) * stride + x;
}

uint32_t Fnv1a(const std::vector<uint8_t>& terrain, const std::vector<uint8_t>& roles,
               const std::vector<int8_t>& elevation, const std::vector<uint8_t>& ramps,
               const std::vector<uint8_t>& cliffs,
               const std::vector<AnchorData::ManualResource>& resources,
               const std::vector<AnchorData::ManualPlayerStart>& playerStarts,
               bool includeElevation, bool includeRamps, bool includeCliffs, bool includeResources,
               bool includePlayerStarts) {
    uint32_t hash = 2166136261u;
    for (const uint8_t value : terrain) hash = (hash ^ value) * 16777619u;
    for (const uint8_t value : roles) hash = (hash ^ value) * 16777619u;
    if (includeElevation)
        for (const int8_t value : elevation)
            hash = (hash ^ static_cast<uint8_t>(value)) * 16777619u;
    if (includeRamps)
        for (const uint8_t value : ramps) hash = (hash ^ value) * 16777619u;
    if (includeCliffs)
        for (const uint8_t value : cliffs) hash = (hash ^ value) * 16777619u;
    if (includeResources)
        for (const auto& resource : resources) {
            hash = (hash ^ resource.kind) * 16777619u;
            const auto* x = reinterpret_cast<const uint8_t*>(&resource.x);
            const auto* y = reinterpret_cast<const uint8_t*>(&resource.y);
            for (size_t index = 0; index < sizeof(resource.x); ++index) hash = (hash ^ x[index]) * 16777619u;
            for (size_t index = 0; index < sizeof(resource.y); ++index) hash = (hash ^ y[index]) * 16777619u;
        }
    if (includePlayerStarts)
        for (const auto& start : playerStarts) {
            hash = (hash ^ start.slot) * 16777619u;
            hash = (hash ^ start.reserved) * 16777619u;
            const auto* x = reinterpret_cast<const uint8_t*>(&start.x);
            const auto* y = reinterpret_cast<const uint8_t*>(&start.y);
            for (size_t index = 0; index < sizeof(start.x); ++index) hash = (hash ^ x[index]) * 16777619u;
            for (size_t index = 0; index < sizeof(start.y); ++index) hash = (hash ^ y[index]) * 16777619u;
        }
    return hash;
}

bool LoadAnchorData(uint32_t id, AnchorData& data) {
    char modulePath[MAX_PATH] = {};
    if (!id || !ModuleFolder(modulePath)) return false;

    char path[MAX_PATH] = {};
    _snprintf_s(path, _TRUNCATE, "%s\\Data\\Random Map Scripts\\EDMW Anchors\\%06X.edmwa", modulePath, id);
    FILE* file = nullptr;
    if (fopen_s(&file, path, "rb") != 0 || !file) return false;

    bool valid = fread(&data.header, sizeof(data.header), 1, file) == 1;
    const bool version7 = valid && memcmp(data.header.magic, "EDMWA07", sizeof(data.header.magic)) == 0;
    const bool version6 = valid && memcmp(data.header.magic, "EDMWA06", sizeof(data.header.magic)) == 0;
    const bool version5 = valid && memcmp(data.header.magic, "EDMWA05", sizeof(data.header.magic)) == 0;
    const bool version4 = valid && memcmp(data.header.magic, "EDMWA04", sizeof(data.header.magic)) == 0;
    const bool version3 = valid && memcmp(data.header.magic, "EDMWA03", sizeof(data.header.magic)) == 0;
    const bool version2 = valid && memcmp(data.header.magic, "EDMWA02", sizeof(data.header.magic)) == 0;
    const bool legacy = valid && memcmp(data.header.magic, "EDMWA01", sizeof(data.header.magic)) == 0;
    uint32_t expectedChecksum = 0;
    if (valid && (version7 || version6 || version5)) valid = fread(&data.flags, sizeof(data.flags), 1, file) == 1;
    if (valid && (version7 || version6 || version5 || version4 || version3 || version2))
        valid = fread(&expectedChecksum, sizeof(expectedChecksum), 1, file) == 1;

    const uint64_t count = static_cast<uint64_t>(data.header.width) * data.header.height;
    valid = valid && (version7 || version6 || version5 || version4 || version3 || version2 || legacy) && data.header.mapId == id &&
            ((data.flags & edmw::kForcedElevationCliffMaskFlag) == 0 || version7) &&
            data.header.width >= 8 && data.header.width <= 512 &&
            data.header.height >= 8 && data.header.height <= 512 && count <= 512ull * 512ull;
    if (valid) {
        try {
            data.terrain.resize(static_cast<size_t>(count));
            data.roles.resize(static_cast<size_t>(count));
            data.elevation.assign(static_cast<size_t>(count), static_cast<int8_t>(127));
            data.ramps.assign(static_cast<size_t>(count), 0);
            data.cliffs.assign(static_cast<size_t>(count), 0);
            valid = fread(data.terrain.data(), 1, data.terrain.size(), file) == data.terrain.size() &&
                    fread(data.roles.data(), 1, data.roles.size(), file) == data.roles.size();
            if (valid && (version7 || version6 || version5 || version4 || version3))
                valid = fread(data.elevation.data(), 1, data.elevation.size(), file) == data.elevation.size();
            if (valid && (version7 || version6 || version5 || version4))
                valid = fread(data.ramps.data(), 1, data.ramps.size(), file) == data.ramps.size();
            if (valid && (data.flags & edmw::kForcedElevationCliffMaskFlag) != 0)
                valid = fread(data.cliffs.data(), 1, data.cliffs.size(), file) == data.cliffs.size();
            if (valid && (version7 || version6)) {
                uint32_t resourceCount = 0;
                valid = fread(&resourceCount, sizeof(resourceCount), 1, file) == 1 && resourceCount <= count;
                data.resources.resize(valid ? resourceCount : 0);
                for (auto& resource : data.resources)
                    valid = valid && fread(&resource.kind, 1, 1, file) == 1 &&
                        fread(&resource.x, sizeof(resource.x), 1, file) == 1 &&
                        fread(&resource.y, sizeof(resource.y), 1, file) == 1;
            }
            if (valid && version7) {
                uint32_t playerStartCount = 0;
                valid = fread(&playerStartCount, sizeof(playerStartCount), 1, file) == 1 &&
                        playerStartCount <= 8;
                data.playerStarts.resize(valid ? playerStartCount : 0);
                for (auto& start : data.playerStarts)
                    valid = valid && fread(&start.slot, 1, 1, file) == 1 &&
                        fread(&start.reserved, 1, 1, file) == 1 &&
                        fread(&start.x, sizeof(start.x), 1, file) == 1 &&
                        fread(&start.y, sizeof(start.y), 1, file) == 1;
            }
            valid = valid && fgetc(file) == EOF;
        } catch (...) {
            valid = false;
        }
    }
    fclose(file);

    if (!valid || ((version7 || version6 || version5 || version4 || version3 || version2) &&
        Fnv1a(data.terrain, data.roles, data.elevation, data.ramps, data.cliffs,
              data.resources, data.playerStarts,
              version7 || version6 || version5 || version4 || version3,
              version7 || version6 || version5 || version4,
              (data.flags & edmw::kForcedElevationCliffMaskFlag) != 0, version7 || version6,
              version7) != expectedChecksum)) return false;
    if ((data.flags & ~(kPaintedTreeMaskFlag | kManualResourcePlacementFlag |
                        kManualPlayerStartPlacementFlag |
                        kManualFaunaPlacementFlag | edmw::kPreserveCoastlineFlag |
                        edmw::kForcedElevationCliffMaskFlag)) != 0) return false;
    for (const uint8_t value : data.terrain) if (value > 7) return false;
    for (const uint8_t value : data.roles) if (value > 4) return false;
    for (size_t index = 0; index < data.elevation.size(); ++index)
        if (!edmw::IsValidPaintedElevation(data.terrain[index], data.elevation[index]))
            return false;
    for (const uint8_t value : data.ramps) if (value > 1) return false;
    for (size_t index = 0; index < data.cliffs.size(); ++index)
        if (data.cliffs[index] > 1 || data.cliffs[index] && data.elevation[index] == 127)
            return false;
    bool hasFlaggableFauna = false;
    for (const auto& resource : data.resources) {
        // Continue accepting fauna markers in Version 7 payloads. Production
        // runtime placement remains fail-closed; the dedicated bit distinguishes
        // new evidence-capable payloads from old unflagged compatibility data.
        const bool knownResourceKind =
            edmw::ManualGeneratedResourceKind(resource.kind) >= 0 ||
            resource.kind == 0 || resource.kind == 3 ||
            (resource.kind >= 16 && resource.kind <= 31);
        if (!knownResourceKind || resource.x < 0 || resource.y < 0 ||
            resource.x >= static_cast<int32_t>(data.header.width) ||
            resource.y >= static_cast<int32_t>(data.header.height)) return false;
        hasFlaggableFauna = hasFlaggableFauna ||
            (edmw::IsFauna(resource.kind) &&
             !edmw::IsLegacyUnsupportedFauna(resource.kind));
    }
    if ((data.flags & kManualFaunaPlacementFlag) != 0 && !hasFlaggableFauna)
        return false;
    bool occupiedSlots[9] = {};
    for (const auto& start : data.playerStarts) {
        // Version 7 originally used the reserved byte for a Human/Computer hint.
        // Accept both legacy values, but bind placement exclusively by lobby slot.
        if (start.slot < 1 || start.slot > 8 || start.reserved > 1 || occupiedSlots[start.slot] ||
            start.x < 0 || start.y < 0 || start.x >= static_cast<int32_t>(data.header.width) ||
            start.y >= static_cast<int32_t>(data.header.height)) return false;
        const uint8_t terrain = data.terrain[static_cast<size_t>(start.y) * data.header.width + start.x];
        if (terrain != 2 && terrain != 3 && terrain != 5 && terrain != 7) return false;
        occupiedSlots[start.slot] = true;
    }
    return true;
}

bool ReadPositionBindings(void* context, std::vector<PositionBinding>& bindings,
                          int& worldWidth, int& worldHeight) {
    uint8_t* begin = nullptr;
    uint8_t* end = nullptr;
    if (!context ||
        !ReadContext(context, kPositionVectorBeginOffset, begin) ||
        !ReadContext(context, kPositionVectorEndOffset, end) ||
        !ReadActiveWorldDimensions(context, worldWidth, worldHeight) ||
        !begin || !end)
        return false;

    Trace("Position active dimensions", PackDimensions(worldWidth, worldHeight));

    const uintptr_t beginAddress = reinterpret_cast<uintptr_t>(begin);
    const uintptr_t endAddress = reinterpret_cast<uintptr_t>(end);
    if (endAddress < beginAddress) return false;
    const size_t bytes = static_cast<size_t>(endAddress - beginAddress);
    if (bytes == 0 || bytes % kPositionRecordSize != 0 ||
        bytes / kPositionRecordSize > kMaxPositionRecords ||
        !IsAccessibleRange(begin, bytes, true))
        return false;

    bindings.clear();
    bindings.reserve(bytes / kPositionRecordSize);
    for (uint8_t* record = begin; record < end; record += kPositionRecordSize) {
        PositionBinding binding;
        binding.record = record;
        memcpy(&binding.position.x, record + kPositionXOffset, sizeof(binding.position.x));
        memcpy(&binding.position.y, record + kPositionYOffset, sizeof(binding.position.y));
        memcpy(&binding.playerSlot, record + kPositionPlayerSlotOffset, sizeof(binding.playerSlot));
        memcpy(&binding.position.kind, record + kPositionKindOffset, sizeof(binding.position.kind));
        uint8_t neutral = 0;
        memcpy(&neutral, record + kPositionNeutralFlagOffset, sizeof(neutral));
        binding.neutral = neutral != 0;
        bindings.push_back(binding);
    }
    return true;
}

bool MergePlayableWorldMask(void* context, size_t worldOffset, int expectedDimension,
                            std::vector<uint8_t>& playableCells, bool intersect,
                            uint32_t& playableCount) {
    void* world = nullptr;
    uint8_t** cells = nullptr;
    int dimension = 0;
    if (!ReadContext(context, worldOffset, world) ||
        !ReadObjectField(world, kWorldCellPointerOffset, cells) ||
        !ReadObjectField(world, kWorldDimensionOffset, dimension) ||
        !cells || dimension != expectedDimension)
        return false;

    const size_t count = static_cast<size_t>(dimension) * dimension;
    if (!IsAccessibleRange(cells, count * sizeof(*cells), false)) return false;
    if (!intersect) playableCells.assign(count, 0);
    if (playableCells.size() != count) return false;

    playableCount = 0;
    for (size_t index = 0; index < count; ++index) {
        const bool playable = cells[index] != nullptr;
        if (intersect) playableCells[index] = playableCells[index] && playable ? 1 : 0;
        else playableCells[index] = playable ? 1 : 0;
        if (playableCells[index]) ++playableCount;
    }
    return playableCount != 0;
}

bool ReadPlayableWorldMask(void* context, int worldWidth, int worldHeight,
                           std::vector<uint8_t>& playableCells) {
    playableCells.clear();
    if (!context || worldWidth < 2 || worldWidth != worldHeight) return false;

    uint32_t primaryCount = 0;
    if (!MergePlayableWorldMask(context, kWorldPointerOffset, worldWidth,
                                playableCells, false, primaryCount))
        return false;
    Trace("Primary playable cells", primaryCount);

    uint32_t intersectionCount = 0;
    if (MergePlayableWorldMask(context, kSecondaryWorldPointerOffset, worldWidth,
                               playableCells, true, intersectionCount))
        Trace("Playable cell intersection", intersectionCount);
    if (playableCells.empty() ||
        std::find(playableCells.begin(), playableCells.end(), static_cast<uint8_t>(1)) ==
            playableCells.end())
        return false;

    int minimumSum = worldWidth + worldHeight;
    int maximumSum = 0;
    int minimumDifference = worldHeight;
    int maximumDifference = -worldWidth;
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            if (!playableCells[static_cast<size_t>(y) * worldWidth + x]) continue;
            minimumSum = (std::min)(minimumSum, x + y);
            maximumSum = (std::max)(maximumSum, x + y);
            minimumDifference = (std::min)(minimumDifference, y - x);
            maximumDifference = (std::max)(maximumDifference, y - x);
        }
    }
    Trace("Playable sum bounds", PackDimensions(minimumSum, maximumSum));
    Trace("Playable difference span", static_cast<uint32_t>(
        (std::max)(0, maximumDifference - minimumDifference)));
    return minimumSum < maximumSum && minimumDifference < maximumDifference;
}

#pragma pack(push, 1)
struct TreePlacementEntry {
    float x;
    float y;
    int32_t kind;
    uint32_t state;
};
#pragma pack(pop)

static_assert(sizeof(TreePlacementEntry) == 16, "Unexpected tree placement record layout.");

struct TreePlacementVector {
    TreePlacementEntry* begin;
    TreePlacementEntry* end;
    TreePlacementEntry* capacity;
};

static_assert(sizeof(TreePlacementVector) == sizeof(void*) * 3,
              "Unexpected tree vector layout.");

void TracePlacementVector(void* context, const char* phase) {
    TreePlacementVector vector = {};
    if (!TraceEnabled() || !context ||
        !ReadContext(context, kTreePlacementVectorOffset, vector) ||
        !vector.begin || !vector.end || vector.end < vector.begin) return;
    const size_t count = static_cast<size_t>(vector.end - vector.begin);
    Trace(phase, static_cast<uint32_t>((std::min)(count, static_cast<size_t>(UINT32_MAX))));
    const size_t traced = (std::min)(count, static_cast<size_t>(96));
    for (size_t index = 0; index < traced; ++index) {
        const auto& entry = vector.begin[index];
        char message[96] = {};
        _snprintf_s(message, _TRUNCATE, "%s entry %u kind", phase,
                    static_cast<unsigned>(index));
        Trace(message, static_cast<uint32_t>(entry.kind));
        _snprintf_s(message, _TRUNCATE, "%s entry %u xy", phase,
                    static_cast<unsigned>(index));
        Trace(message, (static_cast<uint32_t>(edmw::QuantizeWorldCoordinate(entry.x, 65536)) << 16) |
                       static_cast<uint32_t>(edmw::QuantizeWorldCoordinate(entry.y, 65536)));
        _snprintf_s(message, _TRUNCATE, "%s entry %u state", phase,
                    static_cast<unsigned>(index));
        Trace(message, entry.state);
    }
}

bool ReadTreePlacementVector(void* context, TreePlacementEntry*& begin, size_t& count,
                             size_t* capacityCount = nullptr) {
    begin = nullptr;
    count = 0;
    if (capacityCount) *capacityCount = 0;
    TreePlacementVector vector = {};
    if (!context || !ReadContext(context, kTreePlacementVectorOffset, vector))
        return false;
    if (!vector.begin && !vector.end && !vector.capacity) return true;
    if (!vector.begin || !vector.end || !vector.capacity ||
        vector.end < vector.begin || vector.capacity < vector.end)
        return false;
    const size_t bytes = static_cast<size_t>(
        reinterpret_cast<uint8_t*>(vector.end) -
        reinterpret_cast<uint8_t*>(vector.begin));
    const size_t capacityBytes = static_cast<size_t>(
        reinterpret_cast<uint8_t*>(vector.capacity) -
        reinterpret_cast<uint8_t*>(vector.begin));
    if (bytes % sizeof(TreePlacementEntry) != 0 ||
        capacityBytes % sizeof(TreePlacementEntry) != 0 ||
        (capacityBytes != 0 &&
         !IsAccessibleRange(vector.begin, capacityBytes, true)))
        return false;
    begin = vector.begin;
    count = bytes / sizeof(TreePlacementEntry);
    if (capacityCount) *capacityCount = capacityBytes / sizeof(TreePlacementEntry);
    return true;
}

bool ReadUnifiedUnitPlacementVector(void* context,
                                    UnifiedUnitPlacementRecord*& begin,
                                    size_t& count,
                                    size_t* capacityCount = nullptr) {
    begin = nullptr;
    count = 0;
    if (capacityCount) *capacityCount = 0;
    UnifiedUnitPlacementVectorRange vector = {};
    if (!context ||
        !ReadContext(context, kUnifiedUnitPlacementVectorRangeOffset, vector))
        return false;
    if (!vector.begin && !vector.end && !vector.capacity) return true;
    if (!vector.begin || !vector.end || !vector.capacity ||
        vector.end < vector.begin || vector.capacity < vector.end)
        return false;
    const size_t countBytes = static_cast<size_t>(
        reinterpret_cast<uint8_t*>(vector.end) -
        reinterpret_cast<uint8_t*>(vector.begin));
    const size_t capacityBytes = static_cast<size_t>(
        reinterpret_cast<uint8_t*>(vector.capacity) -
        reinterpret_cast<uint8_t*>(vector.begin));
    if (countBytes % sizeof(*vector.begin) != 0 ||
        capacityBytes % sizeof(*vector.begin) != 0 ||
        capacityBytes / sizeof(*vector.begin) > 300000 ||
        (capacityBytes != 0 &&
         !IsAccessibleRange(vector.begin, capacityBytes, true)))
        return false;
    begin = vector.begin;
    count = countBytes / sizeof(*vector.begin);
    if (capacityCount) *capacityCount = capacityBytes / sizeof(*vector.begin);
    return true;
}

void TraceFinalResourceEvidence(void* context, const char* phase) {
    if (!TraceEnabled() || !context || !phase) return;
    TreePlacementEntry* entries = nullptr;
    size_t count = 0;
    size_t capacity = 0;
    const bool readable = ReadTreePlacementVector(context, entries, count, &capacity);
    uint32_t fish = 0;
    uint32_t animals = 0;
    if (readable) {
        for (size_t index = 0; index < count; ++index) {
            if (entries[index].kind == kFishResourceKind) ++fish;
            if (entries[index].kind == kAnimalResourceKind) ++animals;
        }
    }
    const uint64_t digest = readable ? Fnv1a64(entries, count * sizeof(*entries)) : 0;
    char json[768] = {};
    _snprintf_s(json, _TRUNCATE,
        "{\"event\":\"resource_vector\",\"phase\":\"%s\",\"readable\":%s,"
        "\"count\":%u,\"capacity\":%u,\"fishCount\":%u,\"animalCount\":%u,"
        "\"digest\":\"%016llX\"}",
        phase, readable ? "true" : "false", static_cast<unsigned>(count),
        static_cast<unsigned>(capacity), fish, animals,
        static_cast<unsigned long long>(digest));
    TraceFaunaJson(json);
    if (!readable) return;
    for (size_t index = 0; index < (std::min)(count, static_cast<size_t>(256)); ++index) {
        const auto& entry = entries[index];
        _snprintf_s(json, _TRUNCATE,
            "{\"event\":\"resource_record\",\"phase\":\"%s\",\"index\":%u,"
            "\"x\":%.9g,\"y\":%.9g,\"quantizedX\":%d,\"quantizedY\":%d,"
            "\"kind\":%d,\"state\":%u}",
            phase, static_cast<unsigned>(index), static_cast<double>(entry.x),
            static_cast<double>(entry.y), edmw::QuantizeWorldCoordinate(entry.x, 65536),
            edmw::QuantizeWorldCoordinate(entry.y, 65536), entry.kind, entry.state);
        TraceFaunaJson(json);
    }
}

const char* FaunaCategoryName(uint8_t kind) {
    switch (edmw::FaunaCategory(kind)) {
        case edmw::ManualFaunaCategory::Water: return "water";
        case edmw::ManualFaunaCategory::Land: return "land";
        default: return "none";
    }
}

void TraceFaunaPayloadAndCells(void* context, const AnchorData& data, const char* phase) {
    if (!TraceEnabled() || !context || !phase ||
        (data.flags & kManualFaunaPlacementFlag) == 0) return;

    struct MarkerReference {
        const AnchorData::ManualResource* marker;
        size_t sequence;
    };
    std::vector<MarkerReference> markers;
    for (size_t sequence = 0; sequence < data.resources.size(); ++sequence) {
        const auto& marker = data.resources[sequence];
        if (edmw::IsFauna(marker.kind) &&
            !edmw::IsLegacyUnsupportedFauna(marker.kind))
            markers.push_back({&marker, sequence});
    }
    std::stable_sort(markers.begin(), markers.end(),
        [](const MarkerReference& left, const MarkerReference& right) {
            const uint8_t leftCategory = static_cast<uint8_t>(
                edmw::FaunaCategory(left.marker->kind));
            const uint8_t rightCategory = static_cast<uint8_t>(
                edmw::FaunaCategory(right.marker->kind));
            if (leftCategory != rightCategory) return leftCategory < rightCategory;
            if (left.marker->kind != right.marker->kind)
                return left.marker->kind < right.marker->kind;
            if (left.marker->y != right.marker->y) return left.marker->y < right.marker->y;
            if (left.marker->x != right.marker->x) return left.marker->x < right.marker->x;
            return left.sequence < right.sequence;
        });

    int worldWidth = 0;
    int worldHeight = 0;
    std::vector<uint8_t> playableCells;
    std::vector<uint32_t> resourceSourceIndices;
    std::vector<uint32_t> directUnitSourceIndices;
    const bool mappingReadable =
        ReadActiveWorldDimensions(context, worldWidth, worldHeight) &&
        ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells) &&
        edmw::BuildWorldSourceIndexMap(data.header.width, data.header.height,
            worldWidth, worldHeight, playableCells.data(), resourceSourceIndices) &&
        edmw::BuildAuthoredWorldSourceIndexMap(data.header.width, data.header.height,
            worldWidth, worldHeight, playableCells.data(), directUnitSourceIndices);

    void* rawHeight = nullptr;
    void* rawLand = nullptr;
    void* rawEligibility = nullptr;
    void* rawOccupancy = nullptr;
    void* rawSurface = nullptr;
    int heightWidth = 0;
    int heightHeight = 0;
    int landWidth = 0;
    int landHeight = 0;
    int eligibilityWidth = 0;
    int eligibilityHeight = 0;
    int occupancyWidth = 0;
    int occupancyHeight = 0;
    int surfaceWidth = 0;
    int surfaceHeight = 0;
    const bool heightReadable = ReadGridDimensions(
        context, kHeightPointerOffset, kHeightWidthOffset, kHeightHeightOffset,
        rawHeight, heightWidth, heightHeight, sizeof(float));
    const bool landReadable = ReadGridDimensions(
        context, kResourceReservationPointerOffset,
        kResourceReservationWidthOffset, kResourceReservationHeightOffset,
        rawLand, landWidth, landHeight, sizeof(uint8_t));
    const bool eligibilityReadable = ReadGridDimensions(
        context, kEligibilityMaskPointerOffset, kEligibilityMaskWidthOffset,
        kEligibilityMaskHeightOffset, rawEligibility, eligibilityWidth,
        eligibilityHeight, sizeof(uint8_t));
    const bool occupancyReadable = ReadGridDimensions(
        context, kSurfaceFreeCachePointerOffset, kSurfaceFreeCacheWidthOffset,
        kSurfaceFreeCacheHeightOffset, rawOccupancy, occupancyWidth, occupancyHeight,
        sizeof(uint8_t));
    const bool surfaceReadable = ReadGridDimensions(
        context, kSurfaceGridPointerOffset, kSurfaceGridWidthOffset,
        kSurfaceGridHeightOffset, rawSurface, surfaceWidth, surfaceHeight,
        sizeof(uint32_t));
    float landThreshold = 0.0f;
    const bool thresholdReadable = ReadContext(context, kLandThresholdOffset, landThreshold);
    const bool heightEngineLayout = HeightGridUsesEngineLayout();

    void* world = nullptr;
    uint8_t** worldCells = nullptr;
    int worldDimension = 0;
    const bool worldCellsReadable = ReadContext(context, kWorldPointerOffset, world) &&
        ReadObjectField(world, kWorldCellPointerOffset, worldCells) &&
        ReadObjectField(world, kWorldDimensionOffset, worldDimension) && worldCells &&
        worldDimension >= 2 && worldDimension <= static_cast<int>(kMaxDimension) &&
        IsAccessibleRange(worldCells,
            static_cast<size_t>(worldDimension) * worldDimension * sizeof(*worldCells), false);

    uint64_t placementDigest = 14695981039346656037ull;
    placementDigest = Fnv1a64(&data.header.mapId, sizeof(data.header.mapId), placementDigest);
    char json[1024] = {};
    _snprintf_s(json, _TRUNCATE,
        "{\"event\":\"fauna_payload\",\"phase\":\"%s\",\"mapId\":\"%06X\","
        "\"flags\":%u,\"markerCount\":%u,\"mappingReadable\":%s,"
        "\"worldWidth\":%d,\"worldHeight\":%d}",
        phase, data.header.mapId, data.flags, static_cast<unsigned>(markers.size()),
        mappingReadable ? "true" : "false", worldWidth, worldHeight);
    TraceFaunaJson(json);

    for (size_t sortedIndex = 0; sortedIndex < markers.size(); ++sortedIndex) {
        const auto& reference = markers[sortedIndex];
        const auto& marker = *reference.marker;
        const char* token = edmw::NamedFaunaToken(marker.kind);
        if (!token) token = marker.kind == 3 ? "Stock-Fish-Category" : "Unknown";
        const auto& sourceIndices = marker.kind == 3
            ? resourceSourceIndices
            : directUnitSourceIndices;
        size_t projectedIndex = SIZE_MAX;
        const bool projected = mappingReadable &&
            edmw::FindNearestWorldCellForSourcePoint(sourceIndices, data.header.width,
                marker.x, marker.y, projectedIndex);
        int worldX = projected ? static_cast<int>(projectedIndex % worldWidth) : -1;
        int worldY = projected ? static_cast<int>(projectedIndex / worldWidth) : -1;
        if (projected && marker.kind == 3) std::swap(worldX, worldY);
        const bool consumerProjected = projected && worldX >= 0 && worldY >= 0 &&
            worldX < worldWidth && worldY < worldHeight;
        const bool heightCellReadable = consumerProjected && heightReadable && thresholdReadable &&
            worldX < heightWidth && worldY < heightHeight;
        const float height = heightCellReadable
            ? static_cast<const float*>(rawHeight)[HeightStorageIndex(
                worldX, worldY, heightWidth, worldWidth, worldHeight,
                heightEngineLayout)]
            : 0.0f;
        const int heightLand = heightCellReadable ? (height >= landThreshold ? 1 : 0) : -1;
        const int landValue = consumerProjected && landReadable && worldX < landWidth && worldY < landHeight
            ? static_cast<const uint8_t*>(rawLand)[static_cast<size_t>(worldY) * landWidth + worldX]
            : -1;
        const int eligibilityValue = consumerProjected && eligibilityReadable &&
            worldX < eligibilityWidth && worldY < eligibilityHeight
            ? static_cast<const uint8_t*>(rawEligibility)[
                static_cast<size_t>(worldY) * eligibilityWidth + worldX]
            : -1;
        const int occupancyValue = consumerProjected && occupancyReadable &&
            worldX < occupancyWidth && worldY < occupancyHeight
            ? static_cast<const uint8_t*>(rawOccupancy)[
                static_cast<size_t>(worldY) * occupancyWidth + worldX]
            : -1;
        const uint32_t surfaceValue = consumerProjected && surfaceReadable &&
            worldX < surfaceWidth && worldY < surfaceHeight
            ? static_cast<const uint32_t*>(rawSurface)[
                static_cast<size_t>(worldY) * surfaceWidth + worldX]
            : UINT32_MAX;
        uint32_t worldKind = UINT32_MAX;
        float worldCellHeight = 0.0f;
        bool worldCellReadable = false;
        if (consumerProjected && worldCellsReadable &&
            worldX < worldDimension && worldY < worldDimension) {
            uint8_t* cell = worldCells[static_cast<size_t>(worldY) * worldDimension + worldX];
            worldCellReadable = ReadObjectField(cell, kWorldCellKindOffset, worldKind) &&
                ReadObjectField(cell, kWorldCellHeightOffset, worldCellHeight);
        }
        int shoreDistance = -1;
        if (consumerProjected && heightReadable && thresholdReadable &&
            worldWidth <= heightWidth && worldHeight <= heightHeight) {
            shoreDistance = worldWidth + worldHeight;
            for (int candidateY = 0; candidateY < worldHeight; ++candidateY) {
                for (int candidateX = 0; candidateX < worldWidth; ++candidateX) {
                    const float candidateHeight = static_cast<const float*>(rawHeight)[
                        HeightStorageIndex(candidateX, candidateY, heightWidth,
                                           worldWidth, worldHeight,
                                           heightEngineLayout)];
                    if ((candidateHeight >= landThreshold) == (heightLand == 1)) continue;
                    shoreDistance = (std::min)(shoreDistance,
                        abs(candidateX - worldX) + abs(candidateY - worldY));
                }
            }
            if (shoreDistance == worldWidth + worldHeight) shoreDistance = -1;
        }
        const int edgeDistance = consumerProjected ? (std::min)((std::min)(worldX, worldY),
            (std::min)(worldWidth - 1 - worldX, worldHeight - 1 - worldY)) : -1;
        const bool categoryMatches = heightLand >= 0 &&
            ((edmw::IsWaterFauna(marker.kind) && heightLand == 0) ||
             (edmw::IsLandFauna(marker.kind) && heightLand == 1));

        const uint8_t category = static_cast<uint8_t>(edmw::FaunaCategory(marker.kind));
        const uint8_t outcome = consumerProjected ? 1 : 0;
        placementDigest = Fnv1a64(&category, sizeof(category), placementDigest);
        placementDigest = Fnv1a64(&marker.kind, sizeof(marker.kind), placementDigest);
        placementDigest = Fnv1a64(&worldX, sizeof(worldX), placementDigest);
        placementDigest = Fnv1a64(&worldY, sizeof(worldY), placementDigest);
        placementDigest = Fnv1a64(&outcome, sizeof(outcome), placementDigest);

        _snprintf_s(json, _TRUNCATE,
            "{\"event\":\"fauna_marker\",\"phase\":\"%s\",\"sortedIndex\":%u,"
            "\"payloadSequence\":%u,\"kind\":%u,\"token\":\"%s\","
            "\"category\":\"%s\",\"authoredX\":%d,\"authoredY\":%d,"
            "\"projected\":%s,\"worldX\":%d,\"worldY\":%d,"
            "\"paintedTerrain\":%u,\"heightReadable\":%s,\"height\":%.9g,"
            "\"heightLand\":%d,\"landMask\":%d,\"eligibility\":%d,"
            "\"occupancy\":%d,\"surfaceId\":%u,\"worldCellReadable\":%s,"
            "\"worldCellKind\":%u,\"worldCellHeight\":%.9g,\"shoreDistance\":%d,"
            "\"edgeDistance\":%d,\"categoryMatches\":%s}",
            phase, static_cast<unsigned>(sortedIndex),
            static_cast<unsigned>(reference.sequence), marker.kind, token,
            FaunaCategoryName(marker.kind), marker.x, marker.y,
            consumerProjected ? "true" : "false", worldX, worldY,
            data.terrain[static_cast<size_t>(marker.y) * data.header.width + marker.x],
            heightCellReadable ? "true" : "false", static_cast<double>(height),
            heightLand, landValue, eligibilityValue, occupancyValue, surfaceValue,
            worldCellReadable ? "true" : "false", worldKind,
            static_cast<double>(worldCellHeight), shoreDistance, edgeDistance,
            categoryMatches ? "true" : "false");
        TraceFaunaJson(json);
    }
    _snprintf_s(json, _TRUNCATE,
        "{\"event\":\"placement_digest\",\"phase\":\"%s\",\"mapId\":\"%06X\","
        "\"algorithm\":\"fnv1a64-map-category-kind-worldxy-outcome\","
        "\"digest\":\"%016llX\"}",
        phase, data.header.mapId, static_cast<unsigned long long>(placementDigest));
    TraceFaunaJson(json);
}

using MsvcrtOperatorNewFn = void* (__cdecl*)(size_t);
using MsvcrtOperatorDeleteFn = void (__cdecl*)(void*);

bool ResolveEngineVectorAllocator(MsvcrtOperatorNewFn& allocate,
                                  MsvcrtOperatorDeleteFn& release) {
    allocate = nullptr;
    release = nullptr;
    HMODULE runtime = GetModuleHandleA("MSVCRT.dll");
    if (!runtime) return false;
    allocate = reinterpret_cast<MsvcrtOperatorNewFn>(
        GetProcAddress(runtime, "??2@YAPAXI@Z"));
    release = reinterpret_cast<MsvcrtOperatorDeleteFn>(
        GetProcAddress(runtime, "??3@YAXPAX@Z"));
    return allocate && release;
}

bool ResizeTreePlacementVector(void* context, TreePlacementEntry*& entries,
                               size_t preservedCount, size_t requiredCount,
                               size_t capacityCount) {
    if (!context || !entries || requiredCount < preservedCount) return false;
    if (requiredCount <= capacityCount) return true;
    if (requiredCount > SIZE_MAX / sizeof(TreePlacementEntry)) return false;

    MsvcrtOperatorNewFn allocate = nullptr;
    MsvcrtOperatorDeleteFn release = nullptr;
    if (!ResolveEngineVectorAllocator(allocate, release)) return false;

    const size_t requiredBytes = requiredCount * sizeof(TreePlacementEntry);
    auto* replacement = static_cast<TreePlacementEntry*>(allocate(requiredBytes));
    if (!replacement || !IsAccessibleRange(replacement, requiredBytes, true)) {
        if (replacement) release(replacement);
        return false;
    }
    if (preservedCount != 0)
        memcpy(replacement, entries, preservedCount * sizeof(TreePlacementEntry));

    TreePlacementVector replacementVector = {
        replacement,
        replacement + preservedCount,
        replacement + requiredCount
    };
    if (!WriteContext(context, kTreePlacementVectorOffset, replacementVector)) {
        release(replacement);
        return false;
    }
    release(entries);
    entries = replacement;
    return true;
}

bool KnownPeBuild(HMODULE module, uint32_t timestamp, uint32_t imageSize) {
    if (!module || !IsAccessibleRange(module, sizeof(IMAGE_DOS_HEADER), false))
        return false;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return false;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(
        reinterpret_cast<const uint8_t*>(module) + dos->e_lfanew);
    if (!IsAccessibleRange(nt, sizeof(*nt), false)) return false;
    return nt->Signature == IMAGE_NT_SIGNATURE &&
           nt->FileHeader.Machine == IMAGE_FILE_MACHINE_I386 &&
           nt->FileHeader.TimeDateStamp == timestamp &&
           nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC &&
           nt->OptionalHeader.SizeOfImage == imageSize;
}

bool KnownExecutableBuild(HMODULE& executable) {
    executable = GetModuleHandleA(nullptr);
    return KnownPeBuild(
        executable, kExpectedExeTimestamp, kExpectedExeImageSize);
}

bool KnownDefaultBuild() {
    return KnownPeBuild(GetModuleHandleA("Default.dll"),
                        kExpectedDefaultTimestamp, kExpectedDefaultImageSize);
}

bool ValidateGlobalUnitId(uint32_t globalId) {
    if (globalId == 0 || globalId > kMaximumGlobalUnitId) return false;
    HMODULE executable = nullptr;
    if (!KnownExecutableBuild(executable)) return false;
    auto* registry = reinterpret_cast<void* const*>(
        reinterpret_cast<const uint8_t*>(executable) + kGlobalUnitRegistryRva);
    if (!IsAccessibleRange(registry,
            (static_cast<size_t>(kMaximumGlobalUnitId) + 1) * sizeof(*registry), false))
        return false;
    void* unit = registry[globalId];
    return unit && IsAccessibleRange(unit, 0x33A, false);
}

bool ValidateUnitDescriptor(void* context, uint32_t descriptorIndex,
                            uint32_t& globalId) {
    globalId = 0;
    const uint8_t* descriptors = nullptr;
    size_t descriptorCount = 0;
    if (!ReadVectorRange(context, kUnitDescriptorVectorOffset,
            kUnitDescriptorStride, descriptors, descriptorCount) ||
        descriptorIndex >= descriptorCount)
        return false;
    memcpy(&globalId,
           descriptors + static_cast<size_t>(descriptorIndex) * kUnitDescriptorStride +
               kUnitDescriptorGlobalIdOffset,
           sizeof(globalId));
    return ValidateGlobalUnitId(globalId);
}

edmw::ManualFaunaCategory ClassifyExactFaunaDescriptor(
    void* context, uint32_t descriptorIndex) {
    uint32_t globalId = 0;
    if (!ValidateUnitDescriptor(context, descriptorIndex, globalId))
        return edmw::ManualFaunaCategory::None;
    for (uint8_t kind = 16; kind <= 31; ++kind)
        if (descriptorIndex == edmw::NamedFaunaDescriptorIndex(kind) &&
            globalId == edmw::NamedFaunaGlobalUnitId(kind))
            return edmw::FaunaCategory(kind);
    return edmw::ManualFaunaCategory::None;
}

bool ReadLiveFaunaTopology(void* context, int worldWidth, int worldHeight,
                           const std::vector<uint8_t>& playableCells,
                           std::vector<float>& liveHeights,
                           std::vector<uint8_t>& liveLand,
                           std::vector<uint8_t>& resourceReservations,
                           std::vector<uint8_t>& eligibility,
                           std::vector<uint8_t>& blocked,
                           std::vector<uint16_t>& shoreDistances,
                           float& landThreshold,
                           float& maximumAnimalElevation,
                           float& maximumLocalHeightVariation) {
    void* rawHeights = nullptr;
    void* rawReservations = nullptr;
    void* rawEligibility = nullptr;
    int heightWidth = 0;
    int heightHeight = 0;
    int reservationWidth = 0;
    int reservationHeight = 0;
    int eligibilityWidth = 0;
    int eligibilityHeight = 0;
    landThreshold = 0.0f;
    maximumAnimalElevation = 0.0f;
    maximumLocalHeightVariation = 0.0f;
    if (!ReadGridDimensions(context, kHeightPointerOffset, kHeightWidthOffset,
            kHeightHeightOffset, rawHeights, heightWidth, heightHeight, sizeof(float)) ||
        !ReadGridDimensions(context, kResourceReservationPointerOffset,
            kResourceReservationWidthOffset, kResourceReservationHeightOffset,
            rawReservations, reservationWidth, reservationHeight, sizeof(uint8_t)) ||
        !ReadGridDimensions(context, kEligibilityMaskPointerOffset,
            kEligibilityMaskWidthOffset, kEligibilityMaskHeightOffset,
            rawEligibility, eligibilityWidth, eligibilityHeight, sizeof(uint8_t)) ||
        !ReadContext(context, kLandThresholdOffset, landThreshold) ||
        !ReadContext(context, kMaximumAnimalResourceElevationOffset,
                     maximumAnimalElevation) ||
        !ReadContext(context, kMaximumLocalHeightVariationOffset,
                     maximumLocalHeightVariation) ||
        !std::isfinite(landThreshold) ||
        !std::isfinite(maximumAnimalElevation) ||
        !std::isfinite(maximumLocalHeightVariation) ||
        maximumAnimalElevation < landThreshold ||
        maximumLocalHeightVariation <= 0.0f ||
        heightWidth < worldWidth || heightHeight < worldHeight ||
        reservationWidth < worldWidth || reservationHeight < worldHeight ||
        eligibilityWidth < worldWidth || eligibilityHeight < worldHeight)
        return false;
    const auto* heights = static_cast<const float*>(rawHeights);
    const auto* reservations = static_cast<const uint8_t*>(rawReservations);
    const auto* eligible = static_cast<const uint8_t*>(rawEligibility);
    const size_t worldCount = static_cast<size_t>(worldWidth) * worldHeight;
    liveHeights.assign(worldCount, 0.0f);
    liveLand.assign(worldCount, 0);
    resourceReservations.assign(worldCount, 0);
    eligibility.assign(worldCount, 0);
    blocked.assign(worldCount, 0);
    const bool engineLayout = HeightGridUsesEngineLayout();
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t worldIndex = edmw::RowMajorCellIndex(x, y, worldWidth);
            const float value = heights[HeightStorageIndex(
                x, y, heightWidth, worldWidth, worldHeight, engineLayout)];
            liveHeights[worldIndex] = value;
            resourceReservations[worldIndex] =
                reservations[static_cast<size_t>(y) * reservationWidth + x];
            eligibility[worldIndex] =
                eligible[static_cast<size_t>(y) * eligibilityWidth + x];
            if (!std::isfinite(value) ||
                resourceReservations[worldIndex] != 0) {
                blocked[worldIndex] = 1;
                continue;
            }
            liveLand[worldIndex] = value >= landThreshold ? 1 : 0;
        }
    }
    return playableCells.size() == worldCount &&
           edmw::BuildCoastalDistanceMap(liveLand.data(), worldWidth,
               worldHeight, shoreDistances, playableCells.data());
}

bool ReadFinalFaunaCell(void* context, int worldWidth, int worldHeight,
                        int x, int y, uint32_t& kind, float& height) {
    if (x < 0 || y < 0 || x >= worldWidth || y >= worldHeight) return false;
    void* world = nullptr;
    uint8_t** cells = nullptr;
    int dimension = 0;
    if (!ReadContext(context, kWorldPointerOffset, world) ||
        !ReadObjectField(world, kWorldCellPointerOffset, cells) ||
        !ReadObjectField(world, kWorldDimensionOffset, dimension) ||
        dimension != worldWidth || dimension != worldHeight || !cells ||
        !IsAccessibleRange(cells,
            static_cast<size_t>(dimension) * dimension * sizeof(*cells), false))
        return false;
    uint8_t* cell = cells[edmw::RowMajorCellIndex(x, y, dimension)];
    kind = UINT32_MAX;
    height = 0.0f;
    return ReadObjectField(cell, kWorldCellKindOffset, kind) &&
           ReadObjectField(cell, kWorldCellHeightOffset, height);
}

bool ValidateFinalFaunaCell(void* context, int worldWidth, int worldHeight,
                            uint8_t sourceKind, int x, int y) {
    uint32_t kind = UINT32_MAX;
    float height = 0.0f;
    return ReadFinalFaunaCell(context, worldWidth, worldHeight, x, y, kind, height) &&
           edmw::IsFinalWorldFaunaCellUsable(sourceKind, kind, height);
}

void TracePlannedFaunaPlacements(
        void* context, int worldWidth, int worldHeight,
        edmw::ManualFaunaCategory category,
        const std::vector<edmw::PlannedFaunaPlacement>& placements,
        bool committed) {
    if (!TraceEnabled()) return;
    const char* categoryName = category == edmw::ManualFaunaCategory::Water
        ? "water" : "land";
    for (const auto& placement : placements) {
        uint32_t worldKind = UINT32_MAX;
        float worldHeightValue = 0.0f;
        const bool readable = ReadFinalFaunaCell(
            context, worldWidth, worldHeight, placement.worldX, placement.worldY,
            worldKind, worldHeightValue);
        uint32_t worldHeightBits = 0;
        memcpy(&worldHeightBits, &worldHeightValue, sizeof(worldHeightBits));
        char json[768] = {};
        _snprintf_s(json, _TRUNCATE,
            "{\"event\":\"manual_fauna_placement\",\"category\":\"%s\","
            "\"committed\":%s,\"sequence\":%u,\"kind\":%u,"
            "\"worldX\":%d,\"worldY\":%d,\"unitDescriptorIndexRaw\":%u,"
            "\"worldCellReadable\":%s,\"worldCellKind\":%u,"
            "\"worldHeightBits\":\"%08X\",\"worldHeight\":%.9g}",
            categoryName, committed ? "true" : "false", placement.sequence,
            static_cast<unsigned>(placement.kind), placement.worldX, placement.worldY,
            placement.unitDescriptorIndexRaw, readable ? "true" : "false",
            worldKind, worldHeightBits, static_cast<double>(worldHeightValue));
        TraceFaunaJson(json);
    }
}

uint16_t BoundedFaunaDistance(void* context, size_t offset, uint16_t fallback) {
    int32_t value = fallback;
    if (!ReadContext(context, offset, value)) return fallback;
    return static_cast<uint16_t>((std::max)(1, (std::min)(32, value)));
}

void ReserveFaunaCell(std::vector<uint8_t>& reserved, int width, int height,
                      float x, float y) {
    if (!std::isfinite(x) || !std::isfinite(y)) return;
    if (x < 0.0f || y < 0.0f ||
        x > static_cast<float>(width - 1) ||
        y > static_cast<float>(height - 1))
        return;
    const int cellX = edmw::QuantizeWorldCoordinate(x, width);
    const int cellY = edmw::QuantizeWorldCoordinate(y, height);
    if (cellX >= 0 && cellY >= 0 && cellX < width && cellY < height)
        reserved[edmw::RowMajorCellIndex(cellX, cellY, width)] = 1;
}

void ReserveFaunaDisk(std::vector<uint8_t>& reserved, int width, int height,
                      float x, float y, int radius) {
    if (!std::isfinite(x) || !std::isfinite(y) ||
        x < 0.0f || y < 0.0f || x > static_cast<float>(width - 1) ||
        y > static_cast<float>(height - 1))
        return;
    const int centerX = edmw::QuantizeWorldCoordinate(x, width);
    const int centerY = edmw::QuantizeWorldCoordinate(y, height);
    radius = (std::max)(0, (std::min)(32, radius));
    const int radiusSquared = radius * radius;
    for (int offsetY = -radius; offsetY <= radius; ++offsetY) {
        const int targetY = centerY + offsetY;
        if (targetY < 0 || targetY >= height) continue;
        for (int offsetX = -radius; offsetX <= radius; ++offsetX) {
            const int targetX = centerX + offsetX;
            if (targetX < 0 || targetX >= width ||
                offsetX * offsetX + offsetY * offsetY > radiusSquared)
                continue;
            reserved[edmw::RowMajorCellIndex(targetX, targetY, width)] = 1;
        }
    }
}

bool ReconcileManualFauna(void* context, const AnchorData* suppliedData) {
    if (!context) return false;
    if (!suppliedData ||
        (suppliedData->flags & kManualFaunaPlacementFlag) == 0)
        return true;
    if (!KnownDefaultBuild()) return true;
    const AnchorData& data = *suppliedData;

    struct MarkerBinding {
        const AnchorData::ManualResource* marker = nullptr;
        uint32_t sequence = 0;
        uint32_t descriptorIndex = UINT32_MAX;
    };
    std::vector<MarkerBinding> markers;
    for (size_t sequence = 0; sequence < data.resources.size(); ++sequence) {
        const auto& marker = data.resources[sequence];
        if (edmw::IsFauna(marker.kind) &&
            !edmw::IsLegacyUnsupportedFauna(marker.kind)) {
            MarkerBinding binding;
            binding.marker = &marker;
            binding.sequence = static_cast<uint32_t>(sequence);
            markers.push_back(binding);
        }
    }
    std::stable_sort(markers.begin(), markers.end(),
        [](const MarkerBinding& left, const MarkerBinding& right) {
            const auto leftCategory = edmw::FaunaCategory(left.marker->kind);
            const auto rightCategory = edmw::FaunaCategory(right.marker->kind);
            if (leftCategory != rightCategory) return leftCategory < rightCategory;
            if (left.marker->kind != right.marker->kind)
                return left.marker->kind < right.marker->kind;
            if (left.marker->y != right.marker->y) return left.marker->y < right.marker->y;
            if (left.marker->x != right.marker->x) return left.marker->x < right.marker->x;
            return left.sequence < right.sequence;
        });
    uint32_t namedCount = 0;
    for (auto& binding : markers) {
        if (!edmw::IsNamedFauna(binding.marker->kind)) continue;
        ++namedCount;
        binding.descriptorIndex =
            edmw::NamedFaunaDescriptorIndex(binding.marker->kind);
    }

    UnifiedUnitPlacementRecord* unitRecords = nullptr;
    size_t unitCount = 0;
    size_t unitCapacity = 0;
    TreePlacementEntry* resourceRecords = nullptr;
    size_t resourceCount = 0;
    size_t resourceCapacity = 0;
    int worldWidth = 0;
    int worldHeight = 0;
    std::vector<uint8_t> playableCells;
    std::vector<uint32_t> resourceSourceIndices;
    std::vector<uint32_t> directUnitSourceIndices;
    std::vector<float> liveHeights;
    std::vector<uint8_t> liveLand;
    std::vector<uint8_t> resourceReservations;
    std::vector<uint8_t> eligibility;
    std::vector<uint8_t> blocked;
    std::vector<uint16_t> shoreDistances;
    std::vector<uint16_t> playableEdgeDistances;
    float landThreshold = 0.0f;
    float maximumAnimalElevation = 0.0f;
    float maximumLocalHeightVariation = 0.0f;
    if (!ReadUnifiedUnitPlacementVector(
            context, unitRecords, unitCount, &unitCapacity) ||
        !ReadTreePlacementVector(context, resourceRecords, resourceCount,
                                 &resourceCapacity) ||
        !ReadActiveWorldDimensions(context, worldWidth, worldHeight) ||
        !ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells) ||
        !edmw::BuildWorldSourceIndexMap(data.header.width, data.header.height,
            worldWidth, worldHeight, playableCells.data(), resourceSourceIndices) ||
        !edmw::BuildAuthoredWorldSourceIndexMap(data.header.width, data.header.height,
            worldWidth, worldHeight, playableCells.data(), directUnitSourceIndices) ||
        !ReadLiveFaunaTopology(context, worldWidth, worldHeight, playableCells,
                              liveHeights, liveLand, resourceReservations,
                              eligibility, blocked, shoreDistances, landThreshold,
                              maximumAnimalElevation,
                              maximumLocalHeightVariation) ||
        !edmw::BuildPlayableEdgeDistanceMap(playableCells.data(), worldWidth,
                                            worldHeight, playableEdgeDistances)) {
        return true;
    }

    bool waterIntegrity = true;
    bool landIntegrity = true;
    for (const auto& binding : markers) {
        if (!edmw::IsNamedFauna(binding.marker->kind)) continue;
        uint32_t globalId = 0;
        if (binding.descriptorIndex !=
                edmw::NamedFaunaDescriptorIndex(binding.marker->kind) ||
            !ValidateUnitDescriptor(context, binding.descriptorIndex, globalId) ||
            globalId != edmw::NamedFaunaGlobalUnitId(binding.marker->kind)) {
            if (edmw::FaunaCategory(binding.marker->kind) ==
                    edmw::ManualFaunaCategory::Water)
                waterIntegrity = false;
            else
                landIntegrity = false;
        }
    }

    bool waterAuthored = false;
    bool landAuthored = false;
    bool fishAuthored = false;
    std::vector<edmw::ManualFaunaRequest> waterRequests;
    std::vector<edmw::ManualFaunaRequest> landRequests;
    for (const auto& binding : markers) {
        const auto category = edmw::FaunaCategory(binding.marker->kind);
        edmw::ManualFaunaRequest request;
        request.kind = binding.marker->kind;
        request.sourceX = binding.marker->x;
        request.sourceY = binding.marker->y;
        request.sequence = binding.sequence;
        request.unitDescriptorIndexRaw = binding.descriptorIndex;
        if (category == edmw::ManualFaunaCategory::Water) {
            waterAuthored = true;
            fishAuthored = fishAuthored || binding.marker->kind == 3;
            waterRequests.push_back(request);
        } else if (category == edmw::ManualFaunaCategory::Land) {
            landAuthored = true;
            landRequests.push_back(request);
        }
    }
    if (fishAuthored && !ValidateGlobalUnitId(kFishGlobalUnitId))
        waterIntegrity = false;

    const size_t worldCount = static_cast<size_t>(worldWidth) * worldHeight;
    std::vector<uint8_t> reservations(worldCount, 0);
    std::vector<PositionBinding> positions;
    int positionWidth = 0;
    int positionHeight = 0;
    if (!ReadPositionBindings(context, positions, positionWidth, positionHeight) ||
        positionWidth != worldWidth || positionHeight != worldHeight) {
        return true;
    }
    for (const auto& position : positions)
        ReserveFaunaDisk(reservations, worldWidth, worldHeight,
                         position.position.x, position.position.y, 6);
    const auto reserveStockCategory = [&](edmw::ManualFaunaCategory category) {
        for (size_t index = 0; index < resourceCount; ++index) {
            const auto& record = resourceRecords[index];
            if ((category == edmw::ManualFaunaCategory::Water &&
                 record.kind == kFishResourceKind) ||
                (category == edmw::ManualFaunaCategory::Land &&
                 record.kind == kAnimalResourceKind))
                ReserveFaunaCell(reservations, worldWidth, worldHeight,
                                 record.x, record.y);
        }
        for (size_t index = 0; index < unitCount; ++index) {
            const auto& record = unitRecords[index];
            if (ClassifyExactFaunaDescriptor(
                    context, record.unitDescriptorIndexRaw) == category &&
                record.worldCoordinates == 1)
                ReserveFaunaCell(reservations, worldWidth, worldHeight,
                                 record.x, record.y);
        }
    };

    for (size_t index = 0; index < resourceCount; ++index) {
        const auto& record = resourceRecords[index];
        const bool replaceableWater = waterAuthored && record.kind == kFishResourceKind;
        const bool replaceableLand = landAuthored && record.kind == kAnimalResourceKind;
        if (!replaceableWater && !replaceableLand)
            ReserveFaunaCell(reservations, worldWidth, worldHeight,
                             record.x, record.y);
    }
    for (size_t index = 0; index < unitCount; ++index) {
        const auto& record = unitRecords[index];
        const auto category = ClassifyExactFaunaDescriptor(
            context, record.unitDescriptorIndexRaw);
        const bool replaceableGeneratedFauna =
            record.engineGenerated == 1 && record.worldCoordinates == 1 &&
            ((category == edmw::ManualFaunaCategory::Water && waterAuthored) ||
             (category == edmw::ManualFaunaCategory::Land && landAuthored));
        if (replaceableGeneratedFauna)
            continue;
        if (record.worldCoordinates == 1)
            ReserveFaunaCell(reservations, worldWidth, worldHeight,
                             record.x, record.y);
    }

    edmw::FaunaPlannerConstraints constraints;
    constraints.edgeDistance = BoundedFaunaDistance(
        context, kResourceEdgeDistanceOffset, 1);
    constraints.landShoreDistance = BoundedFaunaDistance(
        context, kResourceWaterDistanceOffset, 1);
    constraints.waterShoreDistance = BoundedFaunaDistance(
        context, kFishWaterDistanceOffset, 1);
    constraints.minimumSeparation = 2;
    if (playableEdgeDistances.size() != worldCount)
        return true;
    for (size_t index = 0; index < worldCount; ++index)
        if (playableEdgeDistances[index] < constraints.edgeDistance)
            blocked[index] = 1;

    std::vector<uint8_t> waterBlocked = blocked;
    std::vector<uint8_t> landBlocked = blocked;
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t index = edmw::RowMajorCellIndex(x, y, worldWidth);
            if (blocked[index] != 0) continue;
            if (fishAuthored && !edmw::IsManualFishCellEligible(
                    liveHeights.data(), liveLand.data(), playableCells.data(),
                    resourceReservations.data(), worldWidth, worldHeight, x, y,
                    landThreshold, maximumLocalHeightVariation))
                waterBlocked[index] = 1;
            if (!edmw::IsManualLandFaunaCellEligible(
                    liveHeights.data(), liveLand.data(), eligibility.data(),
                    resourceReservations.data(), worldWidth, worldHeight, x, y,
                    landThreshold, maximumAnimalElevation,
                    maximumLocalHeightVariation))
                landBlocked[index] = 1;
        }
    }

    const std::vector<uint8_t> baseReservations = reservations;

    const auto planCategory = [&](edmw::ManualFaunaCategory category,
                                  const std::vector<edmw::ManualFaunaRequest>& requests,
                                  bool integrity,
                                  std::vector<uint8_t>& categoryBlocked,
                                  std::vector<edmw::PlannedFaunaPlacement>& placements,
                                  uint64_t& digest) {
        if (!integrity || requests.empty()) return false;
        for (size_t retry = 0; retry < 128; ++retry) {
            std::vector<uint8_t> attemptedReservations = reservations;
            if (!edmw::PlanManualFaunaCategory(
                    data.terrain.data(), data.header.width, data.header.height,
                    worldWidth, worldHeight, resourceSourceIndices,
                    directUnitSourceIndices, playableCells.data(),
                    liveLand.data(), categoryBlocked.data(), shoreDistances.data(), category,
                    constraints, requests, attemptedReservations, placements, digest))
                return false;
            bool valid = true;
            for (const auto& placement : placements) {
                if (!ValidateFinalFaunaCell(context, worldWidth, worldHeight,
                        placement.kind, placement.worldX, placement.worldY)) {
                    categoryBlocked[edmw::RowMajorCellIndex(
                        placement.worldX, placement.worldY, worldWidth)] = 1;
                    valid = false;
                }
            }
            if (valid) {
                reservations.swap(attemptedReservations);
                return true;
            }
        }
        return false;
    };

    std::vector<edmw::PlannedFaunaPlacement> waterPlacements;
    std::vector<edmw::PlannedFaunaPlacement> landPlacements;
    uint64_t waterDigest = 0;
    uint64_t landDigest = 0;
    bool waterCommitted = waterAuthored && planCategory(
        edmw::ManualFaunaCategory::Water, waterRequests, waterIntegrity, waterBlocked,
        waterPlacements, waterDigest);
    if (waterAuthored && !waterCommitted)
        reserveStockCategory(edmw::ManualFaunaCategory::Water);
    bool landCommitted = landAuthored && planCategory(
        edmw::ManualFaunaCategory::Land, landRequests, landIntegrity, landBlocked,
        landPlacements, landDigest);
    if (waterCommitted && landAuthored && !landCommitted) {
        reservations = baseReservations;
        reserveStockCategory(edmw::ManualFaunaCategory::Land);
        waterCommitted = planCategory(edmw::ManualFaunaCategory::Water,
            waterRequests, waterIntegrity, waterBlocked,
            waterPlacements, waterDigest);
    }

    std::vector<TreePlacementEntry> newResources;
    newResources.reserve(resourceCount + waterPlacements.size());
    for (size_t index = 0; index < resourceCount; ++index) {
        const auto& record = resourceRecords[index];
        if ((waterCommitted && record.kind == kFishResourceKind) ||
            (landCommitted && record.kind == kAnimalResourceKind))
            continue;
        newResources.push_back(record);
    }
    if (waterCommitted) {
        for (const auto& placement : waterPlacements) {
            if (placement.kind != 3) continue;
            TreePlacementEntry fish = {};
            // The planner already reports final resource-consumer coordinates;
            // its raw authored projection was transposed exactly once upstream.
            fish.x = static_cast<float>(placement.worldX);
            fish.y = static_cast<float>(placement.worldY);
            fish.kind = kFishResourceKind;
            fish.state = 0;
            newResources.push_back(fish);
        }
    }

    std::vector<UnifiedUnitPlacementRecord> newUnits;
    newUnits.reserve(unitCount + namedCount);
    for (size_t index = 0; index < unitCount; ++index) {
        const auto& record = unitRecords[index];
        const auto category = ClassifyExactFaunaDescriptor(
            context, record.unitDescriptorIndexRaw);
        if (record.engineGenerated == 1 && record.worldCoordinates == 1 &&
            ((waterCommitted && category == edmw::ManualFaunaCategory::Water) ||
             (landCommitted && category == edmw::ManualFaunaCategory::Land)))
            continue;
        newUnits.push_back(record);
    }

    // Construct named fauna directly from the exact build-gated descriptor map
    // after all stock world-generation stages have finished. Fish remains a
    // kind-5 resource record and therefore is not appended here.
    for (const auto& binding : markers) {
        if (!edmw::IsNamedFauna(binding.marker->kind)) continue;
        const auto category = edmw::FaunaCategory(binding.marker->kind);
        const bool committed = category == edmw::ManualFaunaCategory::Water
            ? waterCommitted : landCommitted;
        if (!committed) continue;
        const auto& placements = category == edmw::ManualFaunaCategory::Water
            ? waterPlacements : landPlacements;
        const auto found = std::find_if(placements.begin(), placements.end(),
            [&binding](const edmw::PlannedFaunaPlacement& placement) {
                return placement.sequence == binding.sequence;
            });
        if (found == placements.end() || found->kind == 3 ||
            found->unitDescriptorIndexRaw != binding.descriptorIndex) {
            Trace("Manual fauna append validation failed", binding.sequence);
            return true;
        }
        UnifiedUnitPlacementRecord converted = {};
        converted.rotationRadians = 0.0f;
        converted.x = static_cast<float>(found->worldX);
        converted.y = static_cast<float>(found->worldY);
        converted.unitDescriptorIndexRaw = binding.descriptorIndex;
        converted.auxiliaryRaw = 0;
        converted.worldCoordinates = 1;
        converted.inRing = 0;
        converted.engineGenerated = 1;
        // The installed parser and stock wildlife writer leave byte 23
        // uninitialized and the host consumer never reads it. Zero the padding
        // so direct records remain deterministic instead of copying stack residue.
        converted.reservedPadding = 0;
        newUnits.push_back(converted);
    }
    auto* unitVectorRangeField = static_cast<uint8_t*>(context) +
        kUnifiedUnitPlacementVectorRangeOffset;
    auto* resourceVectorField = static_cast<uint8_t*>(context) +
        kTreePlacementVectorOffset;
    if (!IsAccessibleRange(unitVectorRangeField,
                           sizeof(UnifiedUnitPlacementVectorRange), true) ||
        !IsAccessibleRange(resourceVectorField, sizeof(TreePlacementVector), true))
        return true;

    const bool replaceUnitBuffer = newUnits.size() > unitCapacity;
    const bool replaceResourceBuffer = newResources.size() > resourceCapacity;
    if ((replaceUnitBuffer &&
         newUnits.size() > SIZE_MAX / sizeof(UnifiedUnitPlacementRecord)) ||
        (replaceResourceBuffer &&
         newResources.size() > SIZE_MAX / sizeof(TreePlacementEntry)))
        return true;

    MsvcrtOperatorNewFn allocate = nullptr;
    MsvcrtOperatorDeleteFn release = nullptr;
    if (replaceUnitBuffer || replaceResourceBuffer) {
        if (!ResolveEngineVectorAllocator(allocate, release)) return true;
    }

    UnifiedUnitPlacementRecord* unitDestination = unitRecords;
    if (replaceUnitBuffer) {
        const size_t bytes = newUnits.size() * sizeof(*unitDestination);
        unitDestination = static_cast<UnifiedUnitPlacementRecord*>(allocate(bytes));
        if (!unitDestination || !IsAccessibleRange(unitDestination, bytes, true)) {
            if (unitDestination) release(unitDestination);
            return true;
        }
    }

    TreePlacementEntry* resourceDestination = resourceRecords;
    if (replaceResourceBuffer) {
        const size_t bytes = newResources.size() * sizeof(TreePlacementEntry);
        resourceDestination = static_cast<TreePlacementEntry*>(allocate(bytes));
        if (!resourceDestination ||
            !IsAccessibleRange(resourceDestination, bytes, true)) {
            if (resourceDestination) release(resourceDestination);
            if (replaceUnitBuffer) release(unitDestination);
            return true;
        }
    }

    if (!newUnits.empty())
        memcpy(unitDestination, newUnits.data(),
               newUnits.size() * sizeof(*unitDestination));
    if (!newResources.empty())
        memcpy(resourceDestination, newResources.data(),
               newResources.size() * sizeof(*resourceDestination));

    UnifiedUnitPlacementVectorRange committedUnits = {
        unitDestination,
        unitDestination ? unitDestination + newUnits.size() : nullptr,
        replaceUnitBuffer
            ? unitDestination + newUnits.size()
            : (unitRecords ? unitRecords + unitCapacity : nullptr)
    };
    TreePlacementVector committedResources = {
        resourceDestination,
        resourceDestination ? resourceDestination + newResources.size() : nullptr,
        replaceResourceBuffer
            ? resourceDestination + newResources.size()
            : (resourceRecords ? resourceRecords + resourceCapacity : nullptr)
    };
    memcpy(unitVectorRangeField, &committedUnits, sizeof(committedUnits));
    memcpy(resourceVectorField, &committedResources, sizeof(committedResources));
    if (replaceUnitBuffer) release(unitRecords);
    if (replaceResourceBuffer) release(resourceRecords);

    Trace("Manual water fauna committed", waterCommitted ?
        static_cast<uint32_t>(waterPlacements.size()) : 0u);
    Trace("Manual land fauna committed", landCommitted ?
        static_cast<uint32_t>(landPlacements.size()) : 0u);
    Trace("Manual water fauna digest low", static_cast<uint32_t>(waterDigest));
    Trace("Manual water fauna digest high", static_cast<uint32_t>(waterDigest >> 32));
    Trace("Manual land fauna digest low", static_cast<uint32_t>(landDigest));
    Trace("Manual land fauna digest high", static_cast<uint32_t>(landDigest >> 32));
    TracePlannedFaunaPlacements(context, worldWidth, worldHeight,
        edmw::ManualFaunaCategory::Water, waterPlacements, waterCommitted);
    TracePlannedFaunaPlacements(context, worldWidth, worldHeight,
        edmw::ManualFaunaCategory::Land, landPlacements, landCommitted);
    return true;
}

uint32_t PaintedTreeOrder(uint32_t mapId, int x, int y) {
    uint32_t value = mapId ^ (static_cast<uint32_t>(x) * 0x9E3779B9u) ^
                     (static_cast<uint32_t>(y) * 0x85EBCA6Bu);
    value ^= value >> 16;
    value *= 0x7FEB352Du;
    value ^= value >> 15;
    value *= 0x846CA68Bu;
    return value ^ (value >> 16);
}

struct PaintedTreeCandidate {
    int x = 0;
    int y = 0;
    uint32_t order = 0;
};

bool RelocateGeneratedLandResources(
    void* context, const AnchorData& data, TreePlacementEntry* entries,
    size_t resourceCount, int worldWidth, int worldHeight,
    const std::vector<uint8_t>& playableCells,
    const std::vector<uint32_t>& sourceIndices) {
    if (!context || !entries || resourceCount == 0 ||
        sourceIndices.size() != static_cast<size_t>(worldWidth) * worldHeight)
        return false;

    std::vector<PositionBinding> bindings;
    int bindingWidth = 0;
    int bindingHeight = 0;
    if (!ReadPositionBindings(context, bindings, bindingWidth, bindingHeight) ||
        bindingWidth != worldWidth || bindingHeight != worldHeight)
        return false;
    std::vector<edmw::ResourcePosition> playerCenters;
    for (const PositionBinding& binding : bindings)
        if (!binding.neutral) playerCenters.push_back(binding.position);
    if (playerCenters.empty()) return false;

    constexpr int kForestClearance = 4;
    constexpr int kResourceSeparation = 3;
    const size_t worldCount = static_cast<size_t>(worldWidth) * worldHeight;
    std::vector<uint8_t> blocked(worldCount, static_cast<uint8_t>(0));
    for (int forestY = 0; forestY < worldHeight; ++forestY) {
        for (int forestX = 0; forestX < worldWidth; ++forestX) {
            const size_t forestIndex = static_cast<size_t>(forestY) * worldWidth + forestX;
            const uint32_t source = sourceIndices[forestIndex];
            if (source == UINT32_MAX || source >= data.terrain.size() ||
                data.terrain[source] != kForestTerrainValue)
                continue;
            for (int y = (std::max)(0, forestY - kForestClearance);
                 y <= (std::min)(worldHeight - 1, forestY + kForestClearance); ++y) {
                const int dy = y - forestY;
                for (int x = (std::max)(0, forestX - kForestClearance);
                     x <= (std::min)(worldWidth - 1, forestX + kForestClearance); ++x) {
                    const int dx = x - forestX;
                    if (dx * dx + dy * dy <= kForestClearance * kForestClearance)
                        blocked[static_cast<size_t>(y) * worldWidth + x] = 1;
                }
            }
        }
    }

    std::vector<PaintedTreeCandidate> candidates;
    candidates.reserve(worldCount / 4);
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t index = static_cast<size_t>(y) * worldWidth + x;
            const uint32_t source = sourceIndices[index];
            if (playableCells[index] == 0 || blocked[index] != 0 ||
                source == UINT32_MAX || source >= data.terrain.size() ||
                data.terrain[source] <= 1)
                continue;
            candidates.push_back({x, y, static_cast<uint32_t>(index)});
        }
    }
    if (candidates.empty()) return false;

    uint32_t playerQuantities[7] = {};
    for (size_t category = 0; category < 7; ++category)
        ReadContext(context, kPlayerResourceQuantityOffset + category * sizeof(uint32_t),
                    playerQuantities[category]);
    uint32_t categoryOrdinals[7] = {};
    std::vector<edmw::ResourcePosition> reserved;
    reserved.reserve(resourceCount);
    size_t moved = 0;
    size_t playerAssigned = 0;
    size_t forestBefore = 0;

    auto nearestPlayer = [&playerCenters](float x, float y) {
        size_t selected = 0;
        float best = (std::numeric_limits<float>::max)();
        for (size_t index = 0; index < playerCenters.size(); ++index) {
            const float dx = x - playerCenters[index].x;
            const float dy = y - playerCenters[index].y;
            const float distance = dx * dx + dy * dy;
            if (distance < best) {
                best = distance;
                selected = index;
            }
        }
        return selected;
    };

    for (size_t entryIndex = 0; entryIndex < resourceCount; ++entryIndex) {
        TreePlacementEntry& entry = entries[entryIndex];
        const int kind = entry.kind;
        const bool knownCategory = kind >= 0 && kind < 7;
        const uint32_t ordinal = knownCategory ? categoryOrdinals[kind]++ : UINT32_MAX;
        const bool belongsToPlayer = knownCategory && ordinal < playerQuantities[kind];
        const size_t sourcePlayer = nearestPlayer(entry.x, entry.y);
        const size_t targetPlayer = belongsToPlayer
            ? static_cast<size_t>(ordinal) % playerCenters.size() : sourcePlayer;
        if (belongsToPlayer) ++playerAssigned;

        const int oldX = edmw::QuantizeWorldCoordinate(entry.x, worldWidth);
        const int oldY = edmw::QuantizeWorldCoordinate(entry.y, worldHeight);
        const size_t oldIndex = static_cast<size_t>(oldY) * worldWidth + oldX;
        if (oldIndex < sourceIndices.size()) {
            const uint32_t source = sourceIndices[oldIndex];
            if (source != UINT32_MAX && source < data.terrain.size() &&
                data.terrain[source] == kForestTerrainValue)
                ++forestBefore;
        }

        float idealX = entry.x;
        float idealY = entry.y;
        if (belongsToPlayer) {
            idealX = playerCenters[targetPlayer].x + (entry.x - playerCenters[sourcePlayer].x);
            idealY = playerCenters[targetPlayer].y + (entry.y - playerCenters[sourcePlayer].y);
        }

        const PaintedTreeCandidate* selected = nullptr;
        float bestScore = (std::numeric_limits<float>::max)();
        for (int pass = 0; pass < 2 && !selected; ++pass) {
            for (const PaintedTreeCandidate& candidate : candidates) {
                if (belongsToPlayer &&
                    nearestPlayer(static_cast<float>(candidate.x),
                                  static_cast<float>(candidate.y)) != targetPlayer)
                    continue;
                bool separated = true;
                if (pass == 0) {
                    for (const edmw::ResourcePosition& position : reserved) {
                        const float dx = candidate.x - position.x;
                        const float dy = candidate.y - position.y;
                        if (dx * dx + dy * dy <
                            kResourceSeparation * kResourceSeparation) {
                            separated = false;
                            break;
                        }
                    }
                }
                if (!separated) continue;
                const float dx = candidate.x - idealX;
                const float dy = candidate.y - idealY;
                const float score = dx * dx + dy * dy;
                if (score < bestScore) {
                    bestScore = score;
                    selected = &candidate;
                }
            }
        }
        if (!selected) return false;

        const float oldYValue = entry.y;
        if (entry.x != selected->x || entry.y != selected->y) ++moved;
        entry.x = static_cast<float>(selected->x);
        entry.y = static_cast<float>(selected->y);
        if (entry.state == static_cast<uint32_t>(edmw::QuantizeWorldCoordinate(
                               oldYValue, worldHeight)))
            entry.state = static_cast<uint32_t>(selected->y);
        reserved.push_back({entry.x, entry.y, entry.kind});
    }

    Trace("Resource placement records", static_cast<uint32_t>(resourceCount));
    Trace("Resource player records distributed", static_cast<uint32_t>(playerAssigned));
    Trace("Resource records originally in forest", static_cast<uint32_t>(forestBefore));
    Trace("Resource records relocated", static_cast<uint32_t>(moved));
    Trace("Resource relocation forest clearance", kForestClearance);
    return true;
}

// Default.dll also initializes the resource templates consumed by later map stages.
// Once that engine-owned setup completes, replace only its random Tree position records
// with deterministic points sourced from the editor's painted Forest cells.
bool RewritePaintedTreePositions(void* context, const AnchorData& data,
                                 size_t entriesBeforeStock) {
    if ((data.flags & kPaintedTreeMaskFlag) == 0) return false;

    TreePlacementEntry* entries = nullptr;
    size_t entriesAfterStock = 0;
    size_t capacityAfterStock = 0;
    int worldWidth = 0;
    int worldHeight = 0;
    if (!ReadTreePlacementVector(context, entries, entriesAfterStock,
                                 &capacityAfterStock) ||
        entriesAfterStock < entriesBeforeStock ||
        !ReadActiveWorldDimensions(context, worldWidth, worldHeight)) {
        Trace("Painted tree mask vector unavailable", data.header.mapId);
        return false;
    }

    std::vector<uint8_t> playableCells;
    std::vector<uint32_t> sourceIndices;
    if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells) ||
        !edmw::BuildWorldSourceIndexMap(data.header.width, data.header.height,
                                        worldWidth, worldHeight, playableCells.data(),
                                        sourceIndices)) {
        Trace("Painted tree mask topology unavailable", data.header.mapId);
        return false;
    }

    std::vector<PaintedTreeCandidate> candidates;
    candidates.reserve(static_cast<size_t>(worldWidth) * worldHeight / 4);
    for (int y = 0; y < worldHeight; ++y) {
        for (int x = 0; x < worldWidth; ++x) {
            const size_t worldIndex = static_cast<size_t>(y) * worldWidth + x;
            const uint32_t source = sourceIndices[worldIndex];
            if (source == UINT32_MAX || source >= data.terrain.size() ||
                data.terrain[source] != kForestTerrainValue)
                continue;
            candidates.push_back({x, y, PaintedTreeOrder(data.header.mapId, x, y)});
        }
    }
    std::sort(candidates.begin(), candidates.end(), [](const auto& left, const auto& right) {
        return left.order < right.order;
    });
    Trace("Painted tree mask candidates", static_cast<uint32_t>((std::min)(
        candidates.size(), static_cast<size_t>(UINT32_MAX))));

    if (candidates.empty()) {
        auto* newEnd = reinterpret_cast<uint8_t*>(entries + entriesBeforeStock);
        if (!WriteContext(context, kTreePlacementVectorOffset + sizeof(entries), newEnd)) {
            Trace("Painted tree mask clear failed", data.header.mapId);
            return false;
        }
        Trace("Painted tree mask rewritten", 0);
        return true;
    }

    const size_t requiredCount = entriesBeforeStock + candidates.size();
    if (requiredCount < entriesBeforeStock ||
        !ResizeTreePlacementVector(context, entries, entriesBeforeStock,
                                   requiredCount, capacityAfterStock)) {
        Trace("Painted tree mask allocation failed", data.header.mapId);
        return false;
    }

    const size_t placedTreeCount = candidates.size();
    for (size_t index = 0; index < placedTreeCount; ++index) {
        const PaintedTreeCandidate& candidate = candidates[index];
        TreePlacementEntry& entry = entries[entriesBeforeStock + index];
        // Transpose only the emitted resource point. The painted Forest mask and
        // every terrain, land, and height grid retain their established mapping.
        edmw::EncodeResourcePointCoordinates(
            static_cast<float>(candidate.x), static_cast<float>(candidate.y),
            entry.x, entry.y);
        entry.kind = kTreeResourceKind;
        entry.state = 0;
    }
    // Replace the sparse stock suffix with precisely the painted forest mask.
    auto* newEnd = reinterpret_cast<uint8_t*>(
        entries + entriesBeforeStock + placedTreeCount);
    if (!WriteContext(context, kTreePlacementVectorOffset + sizeof(entries), newEnd)) {
        Trace("Painted tree mask trim failed", data.header.mapId);
        return false;
    }
    Trace("Painted tree mask rewritten", static_cast<uint32_t>((std::min)(
        placedTreeCount, static_cast<size_t>(UINT32_MAX))));
    return true;
}

bool RewriteManualResourcePositions(void* context, const AnchorData& data) {
    if ((data.flags & kManualResourcePlacementFlag) == 0) return true;
    TreePlacementEntry* entries = nullptr;
    size_t totalCount = 0;
    size_t capacityCount = 0;
    int worldWidth = 0;
    int worldHeight = 0;
    if (!ReadTreePlacementVector(context, entries, totalCount, &capacityCount) ||
        !ReadActiveWorldDimensions(context, worldWidth, worldHeight)) return false;

    std::vector<uint8_t> playableCells;
    std::vector<uint32_t> sourceIndices;
    if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells) ||
        !edmw::BuildWorldSourceIndexMap(data.header.width, data.header.height,
                                        worldWidth, worldHeight, playableCells.data(),
                                        sourceIndices)) return false;

    const std::vector<TreePlacementEntry> stockEntries(entries, entries + totalCount);
    std::vector<TreePlacementEntry> paintedTrees;
    paintedTrees.reserve(stockEntries.size());
    std::vector<TreePlacementEntry> stockUnitResources;
    stockUnitResources.reserve(stockEntries.size());
    for (const auto& entry : stockEntries)
        if (entry.kind == kTreeResourceKind && entry.state == 0)
            paintedTrees.push_back(entry);
        else if (entry.kind == 4 || entry.kind == 5)
            // Fish and Animal records remain completely owned by Default.dll.
            // They must not be suppressed while manual fauna placement is paused.
            stockUnitResources.push_back(entry);
    std::vector<const AnchorData::ManualResource*> generatedResources;
    generatedResources.reserve(data.resources.size());
    for (const auto& resource : data.resources)
        if (edmw::ManualGeneratedResourceKind(resource.kind) >= 0)
            generatedResources.push_back(&resource);
    const size_t requiredCount = paintedTrees.size() + stockUnitResources.size() +
                                 generatedResources.size();
    // The exact forest pass marks its records with state 0; Default.dll's later
    // generated Tree records use state 500. Preserve the editor-authored trees and
    // every stock Fish/Animal record, replace stock mineral/berry records, then
    // append only the currently supported manual point resources below.
    if (!ResizeTreePlacementVector(context, entries, 0, requiredCount, capacityCount))
        return false;
    std::copy(paintedTrees.begin(), paintedTrees.end(), entries);
    std::copy(stockUnitResources.begin(), stockUnitResources.end(),
              entries + paintedTrees.size());
    const size_t generatedResourceOffset = paintedTrees.size() + stockUnitResources.size();
    for (size_t index = 0; index < generatedResources.size(); ++index) {
        const auto& source = *generatedResources[index];
        auto& target = entries[generatedResourceOffset + index];
        const int32_t liveKind = edmw::ManualGeneratedResourceKind(source.kind);
        if (liveKind < 0 || liveKind > 5 || liveKind == kTreeResourceKind ||
            stockEntries.empty()) {
            Trace("Manual resource template unavailable", source.kind);
            return false;
        }
        target = stockEntries.front();
        size_t bestWorld = SIZE_MAX;
        if (!edmw::FindNearestWorldCellForSourcePoint(
                sourceIndices, data.header.width,
                source.x, source.y, bestWorld))
            return false;
        edmw::EncodeResourcePointCoordinates(
            static_cast<float>(bestWorld % worldWidth),
            static_cast<float>(bestWorld / worldWidth),
            target.x, target.y);
        target.kind = liveKind;
        target.state = 0;
    }
    auto* newEnd = reinterpret_cast<uint8_t*>(entries + requiredCount);
    if (!WriteContext(context, kTreePlacementVectorOffset + sizeof(entries), newEnd)) return false;
    Trace("Painted forest records retained", static_cast<uint32_t>(paintedTrees.size()));
    Trace("Stock unit resource records retained",
          static_cast<uint32_t>(stockUnitResources.size()));
    Trace("Manual resource records written", static_cast<uint32_t>(generatedResources.size()));
    return true;
}

bool PreserveOriginalPositionTopologyGrids(
    uint32_t id, const uint8_t* resourceReservations,
    int reservationWidth, int reservationHeight,
    const uint8_t* eligibility, int eligibilityWidth, int eligibilityHeight) {
    if (!id || !resourceReservations || !eligibility ||
        reservationWidth < 2 || reservationHeight < 2 ||
        eligibilityWidth < 2 || eligibilityHeight < 2)
        return false;

    AcquireSRWLockShared(&g_landLock);
    const bool alreadyPreserved = g_originalTopologyMapId == id &&
        g_originalReservationWidth == reservationWidth &&
        g_originalReservationHeight == reservationHeight &&
        g_originalEligibilityWidth == eligibilityWidth &&
        g_originalEligibilityHeight == eligibilityHeight &&
        g_originalResourceReservations.size() ==
            static_cast<size_t>(reservationWidth) * reservationHeight &&
        g_originalEligibility.size() ==
            static_cast<size_t>(eligibilityWidth) * eligibilityHeight;
    ReleaseSRWLockShared(&g_landLock);
    if (alreadyPreserved) return true;

    std::vector<uint8_t> reservationCopy(
        resourceReservations,
        resourceReservations + static_cast<size_t>(reservationWidth) * reservationHeight);
    std::vector<uint8_t> eligibilityCopy(
        eligibility,
        eligibility + static_cast<size_t>(eligibilityWidth) * eligibilityHeight);

    AcquireSRWLockExclusive(&g_landLock);
    if (g_originalTopologyMapId != id ||
        g_originalResourceReservations.empty() || g_originalEligibility.empty()) {
        g_originalResourceReservations.swap(reservationCopy);
        g_originalEligibility.swap(eligibilityCopy);
        g_originalReservationWidth = reservationWidth;
        g_originalReservationHeight = reservationHeight;
        g_originalEligibilityWidth = eligibilityWidth;
        g_originalEligibilityHeight = eligibilityHeight;
        g_originalTopologyMapId = id;
    }
    const bool preserved = g_originalTopologyMapId == id &&
        g_originalReservationWidth == reservationWidth &&
        g_originalReservationHeight == reservationHeight &&
        g_originalEligibilityWidth == eligibilityWidth &&
        g_originalEligibilityHeight == eligibilityHeight;
    ReleaseSRWLockExclusive(&g_landLock);
    return preserved;
}

bool ExposePaintedTopologyForPositionGeneration(
    void* context, uint32_t id, edmw::AlignmentTarget target) {
    const bool player = target == edmw::AlignmentTarget::Player;
    if (!context || !id) {
        Trace(player ? "Player placement topology unavailable" :
                       "Neutral placement topology unavailable", id);
        return false;
    }

    AnchorData data;
    if (!LoadAnchorData(id, data)) {
        Trace(player ? "Player placement payload unavailable" :
                       "Neutral placement payload unavailable", id);
        return false;
    }

    void* rawLandMask = nullptr;
    void* rawEligibilityMask = nullptr;
    int landWidth = 0;
    int landHeight = 0;
    int eligibilityWidth = 0;
    int eligibilityHeight = 0;
    if (!ReadGridDimensions(context, kResourceReservationPointerOffset,
                            kResourceReservationWidthOffset,
                            kResourceReservationHeightOffset,
                            rawLandMask, landWidth, landHeight,
                            sizeof(uint8_t)) ||
        !ReadGridDimensions(context, kEligibilityMaskPointerOffset,
                            kEligibilityMaskWidthOffset, kEligibilityMaskHeightOffset,
                            rawEligibilityMask, eligibilityWidth, eligibilityHeight,
                            sizeof(uint8_t))) {
        Trace(player ? "Player placement topology grids unavailable" :
                       "Neutral placement topology grids unavailable", id);
        return false;
    }

    int activeWidth = 0;
    int activeHeight = 0;
    if (!ReadActiveWorldDimensions(context, activeWidth, activeHeight) ||
        activeWidth > landWidth || activeHeight > landHeight ||
        activeWidth > eligibilityWidth || activeHeight > eligibilityHeight) {
        Trace(player ? "Player placement dimensions invalid" :
                       "Neutral placement dimensions invalid",
              PackDimensions(activeWidth, activeHeight));
        return false;
    }

    std::vector<uint8_t> playableCells;
    if (!ReadPlayableWorldMask(context, activeWidth, activeHeight, playableCells)) {
        Trace(player ? "Player placement playable mask unavailable" :
                       "Neutral placement playable mask unavailable", id);
        return false;
    }

    const size_t landCount = static_cast<size_t>(landWidth) * landHeight;
    const size_t eligibilityCount = static_cast<size_t>(eligibilityWidth) * eligibilityHeight;
    auto* landMask = static_cast<uint8_t*>(rawLandMask);
    auto* eligibilityMask = static_cast<uint8_t*>(rawEligibilityMask);
    if (!IsAccessibleRange(landMask, landCount, true) ||
        !IsAccessibleRange(eligibilityMask, eligibilityCount, true)) {
        Trace(player ? "Player placement topology grids not writable" :
                       "Neutral placement topology grids not writable", id);
        return false;
    }

    // This is the only intentional temporary use of +0x118 as a topology
    // domain. Preserve both engine-owned grids before the first write; the
    // height stage restores them before stock resource generation can observe
    // the temporary land encoding.
    if (!PreserveOriginalPositionTopologyGrids(
            id, landMask, landWidth, landHeight,
            eligibilityMask, eligibilityWidth, eligibilityHeight)) {
        Trace(player ? "Player placement topology snapshot failed" :
                       "Neutral placement topology snapshot failed", id);
        return false;
    }

    // The stock surface grid at +0x10C remains untouched. Build a temporary
    // painted domain for the stock position generators only.
    std::vector<uint8_t> candidateLand(landCount, static_cast<uint8_t>(1));
    std::vector<uint8_t> paintedEligibility(eligibilityCount, static_cast<uint8_t>(0));
    std::vector<uint8_t> candidateEligibility(
        eligibilityMask, eligibilityMask + eligibilityCount);
    const edmw::TopologyGridLayout landLayout = {
        landWidth, landHeight, activeWidth, activeHeight, playableCells.data()
    };
    const edmw::TopologyGridLayout eligibilityLayout = {
        eligibilityWidth, eligibilityHeight, activeWidth, activeHeight, playableCells.data()
    };
    if (!edmw::BuildAuthoritativeLandMask(
            data.terrain.data(), data.header.width, data.header.height,
            landLayout, candidateLand.data()) ||
        !edmw::BuildAuthoritativeEligibilityMask(
            data.terrain.data(), data.header.width, data.header.height,
            eligibilityLayout, paintedEligibility.data()) ||
        !edmw::ConstrainEligibilityToPaintedLand(
            paintedEligibility.data(), eligibilityCount, candidateEligibility.data())) {
        Trace(player ? "Player placement topology projection failed" :
                       "Neutral placement topology projection failed", id);
        return false;
    }

    const size_t projectedLandCount = static_cast<size_t>(std::count(
        candidateLand.begin(), candidateLand.end(), static_cast<uint8_t>(0)));
    const size_t eligibleLandCount = static_cast<size_t>(std::count_if(
        candidateEligibility.begin(), candidateEligibility.end(),
        [](uint8_t value) { return value != 0; }));
    bool coherent = true;
    for (int y = 0; coherent && y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const bool land = candidateLand[static_cast<size_t>(y) * landWidth + x] == 0;
            const bool eligible =
                candidateEligibility[static_cast<size_t>(y) * eligibilityWidth + x] != 0;
            if (eligible && !land) {
                coherent = false;
                break;
            }
        }
    }
    if (projectedLandCount == 0 || eligibleLandCount == 0 || !coherent) {
        Trace(player ? "Player placement topology is inconsistent" :
                       "Neutral placement topology is inconsistent", id);
        return false;
    }

    memcpy(landMask, candidateLand.data(), landCount);
    memcpy(eligibilityMask, candidateEligibility.data(), eligibilityCount);
    Trace(player ? "Player placement topology dimensions" :
                   "Neutral placement topology dimensions",
          PackDimensions(activeWidth, activeHeight));
    Trace(player ? "Player placement topology land cells" :
                   "Neutral placement topology land cells",
          static_cast<uint32_t>((std::min)(
              projectedLandCount, static_cast<size_t>(UINT32_MAX))));
    Trace(player ? "Player placement eligible land cells" :
                   "Neutral placement eligible land cells",
          static_cast<uint32_t>((std::min)(
              eligibleLandCount, static_cast<size_t>(UINT32_MAX))));
    return true;
}

void WriteAlignedPosition(const PositionBinding& binding, const edmw::ResourcePosition& position,
                          int worldWidth, int worldHeight) {
    const int32_t gridX = edmw::QuantizeWorldCoordinate(position.x, worldWidth);
    const int32_t gridY = edmw::QuantizeWorldCoordinate(position.y, worldHeight);
    memcpy(binding.record + kPositionXOffset, &position.x, sizeof(position.x));
    memcpy(binding.record + kPositionYOffset, &position.y, sizeof(position.y));
    memcpy(binding.record + kPositionGridXOffset, &gridX, sizeof(gridX));
    memcpy(binding.record + kPositionGridYOffset, &gridY, sizeof(gridY));
}

float MinimumSafeAlignmentClearance(edmw::AlignmentTarget target,
                                    int worldWidth, int worldHeight) {
    if (worldWidth < 2 || worldHeight < 2) return 0.0f;
    const float extent = static_cast<float>((std::min)(worldWidth, worldHeight) - 1);
    if (target == edmw::AlignmentTarget::Player)
        return std::ceil((std::max)(8.0f, (std::min)(12.0f, extent * 0.0375f)));
    return std::ceil((std::max)(6.0f, (std::min)(10.0f, extent * 0.025f)));
}

bool ApplyManualPlayerStarts(void* context, const AnchorData& data) {
    if ((data.flags & kManualPlayerStartPlacementFlag) == 0 || data.playerStarts.empty())
        return true;

    std::vector<PositionBinding> bindings;
    int worldWidth = 0;
    int worldHeight = 0;
    if (!ReadPositionBindings(context, bindings, worldWidth, worldHeight)) return false;

    std::vector<uint8_t> playableCells;
    if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells)) return false;
    std::vector<uint32_t> sourceIndices;
    if (!edmw::BuildWorldSourceIndexMap(
            data.header.width, data.header.height, worldWidth, worldHeight,
            playableCells.data(), sourceIndices)) return false;

    struct PlannedStart {
        size_t bindingIndex = 0;
        edmw::ResourcePosition position;
        uint8_t slot = 0;
    };
    std::vector<const AnchorData::ManualPlayerStart*> orderedStarts;
    orderedStarts.reserve(data.playerStarts.size());
    for (const auto& start : data.playerStarts) orderedStarts.push_back(&start);
    std::sort(orderedStarts.begin(), orderedStarts.end(),
        [](const auto* left, const auto* right) { return left->slot < right->slot; });

    std::vector<PlannedStart> planned;
    planned.reserve(orderedStarts.size());

    for (const auto* start : orderedStarts) {
        size_t bindingIndex = SIZE_MAX;
        for (size_t index = 0; index < bindings.size(); ++index) {
            if (!bindings[index].neutral && bindings[index].playerSlot == start->slot) {
                bindingIndex = index;
                break;
            }
        }
        // A saved slot may be closed in a smaller match. It is intentionally ignored.
        if (bindingIndex == SIZE_MAX) continue;

        size_t bestWorld = SIZE_MAX;
        if (!edmw::FindNearestWorldCellForAuthoredPoint(
                sourceIndices, data.header.width, data.header.height,
                start->x, start->y, bestWorld))
            return false;
        if (bestWorld == SIZE_MAX) return false;

        edmw::ResourcePosition position = {
            static_cast<float>(bestWorld % static_cast<size_t>(worldWidth)),
            static_cast<float>(bestWorld / static_cast<size_t>(worldWidth)),
            bindings[bindingIndex].position.kind
        };
        planned.push_back({bindingIndex, position, start->slot});
    }

    for (const auto& start : planned) {
        WriteAlignedPosition(bindings[start.bindingIndex], start.position, worldWidth, worldHeight);
        Trace("Manual player start slot", start.slot);
        Trace("Manual player start XY", PackDimensions(
            edmw::QuantizeWorldCoordinate(start.position.x, worldWidth),
            edmw::QuantizeWorldCoordinate(start.position.y, worldHeight)));
    }
    Trace("Manual player starts written", static_cast<uint32_t>(planned.size()));
    return true;
}


bool AlignGeneratedPositions(void* context, uint32_t id, edmw::AlignmentTarget target) {
    if (!context || !id) return false;
    try {
        AnchorData data;
        if (!LoadAnchorData(id, data)) {
            Trace("Position payload unavailable", id);
            return false;
        }
        const bool exactForestPlayer = target == edmw::AlignmentTarget::Player;

        std::vector<PositionBinding> bindings;
        int worldWidth = 0;
        int worldHeight = 0;
        if (!ReadPositionBindings(context, bindings, worldWidth, worldHeight)) {
            Trace("Position vector unavailable", id);
            return false;
        }

        std::vector<uint8_t> playableCells;
        if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells)) {
            Trace("Position playable mask unavailable", id);
            return false;
        }

        std::vector<edmw::ResourcePosition> fixed;
        std::vector<edmw::ResourcePosition> selected;
        std::vector<size_t> selectedBindings;
        fixed.reserve(bindings.size());
        selected.reserve(bindings.size());
        selectedBindings.reserve(bindings.size());
        for (size_t index = 0; index < bindings.size(); ++index) {
            const bool neutral = bindings[index].neutral;
            const bool manualPlayer = target == edmw::AlignmentTarget::Player && !neutral &&
                (data.flags & kManualPlayerStartPlacementFlag) != 0 &&
                std::any_of(data.playerStarts.begin(), data.playerStarts.end(),
                    [&bindings, index](const auto& start) {
                        return bindings[index].playerSlot == start.slot;
                    });
            const bool invalidNeutral = target == edmw::AlignmentTarget::Neutral && neutral &&
                !edmw::PositionMapsToPaintedLand(
                    data.terrain.data(), data.header.width, data.header.height,
                    worldWidth, worldHeight, bindings[index].position,
                    playableCells.data());
            const bool select = target == edmw::AlignmentTarget::Player
                ? !neutral && !manualPlayer
                : invalidNeutral;
            if (select) {
                selected.push_back(bindings[index].position);
                selectedBindings.push_back(index);
            } else {
                fixed.push_back(bindings[index].position);
            }
        }
        if (selected.empty()) {
            if (target == edmw::AlignmentTarget::Neutral)
                Trace("Neutral positions already valid", id);
            return true;
        }

        const float requiredClearance = edmw::RecommendedAlignmentClearance(
            target, worldWidth, worldHeight);
        const float minimumClearance = MinimumSafeAlignmentClearance(
            target, worldWidth, worldHeight);
        std::vector<uint8_t> treeFreeTerrain;
        const uint8_t* alignmentTerrain = data.terrain.data();
        if (exactForestPlayer) {
            treeFreeTerrain = data.terrain;
            for (uint8_t& terrain : treeFreeTerrain)
                if (terrain == kForestTerrainValue) terrain = 0;
            alignmentTerrain = treeFreeTerrain.data();
        }
        const edmw::AlignmentSummary summary = edmw::AlignResourcePositionsAdaptive(
            alignmentTerrain, data.roles.data(), data.header.width, data.header.height,
            worldWidth, worldHeight, target, requiredClearance, minimumClearance,
            fixed, selected,
            playableCells.data());
        Trace(target == edmw::AlignmentTarget::Player ? "Player requested clearance" :
              "Neutral requested clearance", summary.requestedClearance);
        Trace(target == edmw::AlignmentTarget::Player ? "Player alignment clearance" :
              "Neutral alignment clearance", summary.minimumClearance);
        Trace(target == edmw::AlignmentTarget::Player ? "Player alignment separation x100" :
              "Neutral alignment separation x100", static_cast<uint32_t>((std::max)(
                  0.0f, summary.minimumSeparation * 100.0f + 0.5f)));
        if (target == edmw::AlignmentTarget::Player) {
            Trace("Player viable land components",
                  static_cast<uint32_t>(summary.viableComponents));
            Trace("Player land components used",
                  static_cast<uint32_t>(summary.componentsUsed));
            Trace("Player maximum component occupancy",
                  static_cast<uint32_t>(summary.maximumComponentOccupancy));
        }
        if (summary.aligned != selected.size()) {
            Trace("Position alignment incomplete", static_cast<uint32_t>(summary.aligned));
            return false;
        }

        if (std::any_of(selected.begin(), selected.end(),
                [&](const edmw::ResourcePosition& position) {
                    return !edmw::PositionMapsToPaintedLand(
                        data.terrain.data(), data.header.width, data.header.height,
                        worldWidth, worldHeight, position, playableCells.data());
                })) {
            Trace("Position alignment validation failed", id);
            return false;
        }

        for (size_t index = 0; index < selected.size(); ++index) {
            WriteAlignedPosition(bindings[selectedBindings[index]], selected[index],
                                 worldWidth, worldHeight);
            const PositionBinding& binding = bindings[selectedBindings[index]];
            Trace("Aligned record index", static_cast<uint32_t>(selectedBindings[index]));
            Trace("Aligned record player slot", static_cast<uint32_t>(binding.playerSlot));
            Trace("Aligned record kind", static_cast<uint32_t>(selected[index].kind));
            Trace("Aligned record neutral", binding.neutral ? 1u : 0u);
            Trace("Aligned record XY", PackDimensions(
                edmw::QuantizeWorldCoordinate(selected[index].x, worldWidth),
                edmw::QuantizeWorldCoordinate(selected[index].y, worldHeight)));
        }
        const uint32_t traceValue = static_cast<uint32_t>(
            ((std::min)(summary.aligned, static_cast<size_t>(0xFFFF)) << 16) |
            (std::min)(summary.moved, static_cast<size_t>(0xFFFF)));
        Trace(target == edmw::AlignmentTarget::Player ? "Player positions aligned" :
              "Neutral positions aligned", traceValue);
        return true;
    } catch (...) {
        Trace("Position alignment failed", id);
        return false;
    }
}

struct PositionMemberVector {
    float* values = nullptr;
    size_t count = 0;
};

bool ReadPositionMemberVector(uint8_t* record, PositionMemberVector& members) {
    members = {};
    uint8_t* begin = nullptr;
    uint8_t* end = nullptr;
    uint32_t declaredCount = 0;
    if (!record) return false;
    memcpy(&begin, record + 0x04, sizeof(begin));
    memcpy(&end, record + 0x08, sizeof(end));
    memcpy(&declaredCount, record + kPositionMemberCountOffset, sizeof(declaredCount));
    if (!begin || !end || end < begin) return false;
    const size_t bytes = static_cast<size_t>(end - begin);
    if (bytes == 0 || bytes % (2 * sizeof(float)) != 0) return false;
    const size_t count = bytes / (2 * sizeof(float));
    if (count != declaredCount || count > static_cast<size_t>(kMaxDimension) * kMaxDimension ||
        !IsAccessibleRange(begin, bytes, true))
        return false;
    members.values = reinterpret_cast<float*>(begin);
    members.count = count;
    return true;
}

void AppendDigestBytes(uint32_t& digest, const void* values, size_t count) {
    const auto* bytes = static_cast<const uint8_t*>(values);
    for (size_t index = 0; index < count; ++index)
        digest = (digest ^ bytes[index]) * 16777619u;
}

void TraceGeneratedPositionState(
    void* context, uint32_t id, edmw::AlignmentTarget target, bool includeMembers) {
    if (!TraceEnabled() || !context || !id) return;
    const bool player = target == edmw::AlignmentTarget::Player;
    try {
        AnchorData data;
        std::vector<PositionBinding> bindings;
        int worldWidth = 0;
        int worldHeight = 0;
        if (!LoadAnchorData(id, data) ||
            !ReadPositionBindings(context, bindings, worldWidth, worldHeight)) {
            Trace(player ? "Player domain trace unavailable" :
                           "Neutral domain trace unavailable", id);
            return;
        }

        std::vector<uint8_t> playableCells;
        if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells)) {
            Trace(player ? "Player domain playable mask unavailable" :
                           "Neutral domain playable mask unavailable", id);
            return;
        }

        void* rawEligibility = nullptr;
        int eligibilityWidth = 0;
        int eligibilityHeight = 0;
        if (!ReadGridDimensions(context, kEligibilityMaskPointerOffset,
                                kEligibilityMaskWidthOffset, kEligibilityMaskHeightOffset,
                                rawEligibility, eligibilityWidth, eligibilityHeight,
                                sizeof(uint8_t))) {
            Trace(player ? "Player live placement grids unavailable" :
                           "Neutral live placement grids unavailable", id);
            return;
        }
        const auto* eligibility = static_cast<const uint8_t*>(rawEligibility);

        size_t selectedRecords = 0;
        size_t invalidVectors = 0;
        size_t totalMembers = 0;
        size_t centersOutsideLand = 0;
        size_t centersOutsideEligibility = 0;
        size_t membersOutsideLand = 0;
        size_t membersInPaintedForest = 0;
        uint32_t digest = 2166136261u;
        for (const PositionBinding& binding : bindings) {
            if (player == binding.neutral) continue;
            ++selectedRecords;
            AppendDigestBytes(digest, &binding.position.x, sizeof(binding.position.x));
            AppendDigestBytes(digest, &binding.position.y, sizeof(binding.position.y));
            AppendDigestBytes(digest, &binding.position.kind, sizeof(binding.position.kind));
            const bool finiteCenter = std::isfinite(binding.position.x) &&
                                      std::isfinite(binding.position.y);
            if (!finiteCenter || !edmw::PositionMapsToPaintedLand(
                    data.terrain.data(), data.header.width, data.header.height,
                    worldWidth, worldHeight, binding.position, playableCells.data()))
                ++centersOutsideLand;

            const int centerX = finiteCenter
                ? edmw::QuantizeWorldCoordinate(binding.position.x, worldWidth) : -1;
            const int centerY = finiteCenter
                ? edmw::QuantizeWorldCoordinate(binding.position.y, worldHeight) : -1;
            if (centerX < 0 || centerY < 0 || centerX >= eligibilityWidth ||
                centerY >= eligibilityHeight ||
                eligibility[static_cast<size_t>(centerY) * eligibilityWidth + centerX] == 0)
                ++centersOutsideEligibility;

            if (!includeMembers) continue;
            PositionMemberVector members;
            if (!ReadPositionMemberVector(binding.record, members)) {
                ++invalidVectors;
                continue;
            }
            totalMembers += members.count;
            for (size_t member = 0; member < members.count; ++member) {
                edmw::ResourcePosition position;
                position.x = members.values[member * 2];
                position.y = members.values[member * 2 + 1];
                AppendDigestBytes(digest, &position.x, sizeof(position.x));
                AppendDigestBytes(digest, &position.y, sizeof(position.y));
                if (!std::isfinite(position.x) || !std::isfinite(position.y) ||
                    !edmw::PositionMapsToPaintedLand(
                        data.terrain.data(), data.header.width, data.header.height,
                        worldWidth, worldHeight, position, playableCells.data()))
                    ++membersOutsideLand;
                if (std::isfinite(position.x) && std::isfinite(position.y)) {
                    uint32_t sourceX = 0;
                    uint32_t sourceY = 0;
                    const int worldX = edmw::QuantizeWorldCoordinate(position.x, worldWidth);
                    const int worldY = edmw::QuantizeWorldCoordinate(position.y, worldHeight);
                    if (edmw::MapWorldCellToSource(
                            worldX, worldY, data.header.width, data.header.height,
                            worldWidth, worldHeight, playableCells.data(), sourceX, sourceY) &&
                        data.terrain[static_cast<size_t>(data.header.height - 1 - sourceY) *
                                         data.header.width + sourceX] ==
                            kForestTerrainValue)
                        ++membersInPaintedForest;
                }
            }
        }

        Trace(player ? "Player generated domain records" :
                       "Neutral generated domain records",
              static_cast<uint32_t>((std::min)(selectedRecords, static_cast<size_t>(UINT32_MAX))));
        Trace(player ? "Player centers outside painted land" :
                       "Neutral centers outside painted land",
              static_cast<uint32_t>((std::min)(centersOutsideLand, static_cast<size_t>(UINT32_MAX))));
        Trace(player ? "Player centers outside eligibility" :
                       "Neutral centers outside eligibility",
              static_cast<uint32_t>((std::min)(
                  centersOutsideEligibility, static_cast<size_t>(UINT32_MAX))));
        if (includeMembers) {
            Trace(player ? "Player generated invalid domains" :
                           "Neutral generated invalid domains",
                  static_cast<uint32_t>((std::min)(
                      invalidVectors, static_cast<size_t>(UINT32_MAX))));
            Trace(player ? "Player generated domain members" :
                           "Neutral generated domain members",
                  static_cast<uint32_t>((std::min)(
                      totalMembers, static_cast<size_t>(UINT32_MAX))));
            Trace(player ? "Player members outside painted land" :
                           "Neutral members outside painted land",
                  static_cast<uint32_t>((std::min)(
                      membersOutsideLand, static_cast<size_t>(UINT32_MAX))));
            Trace(player ? "Player members in painted forest" :
                           "Neutral members in painted forest",
                  static_cast<uint32_t>((std::min)(
                      membersInPaintedForest, static_cast<size_t>(UINT32_MAX))));
        }
        Trace(player ? "Player generated domain digest" :
                       "Neutral generated domain digest", digest);
    } catch (...) {
        Trace(player ? "Player generated domain trace failed" :
                       "Neutral generated domain trace failed", id);
    }
}

uint16_t ResourceDomainClearance(const void* context) {
    int edgeDistance = 0;
    int waterDistance = 0;
    int fishDistance = 0;
    ReadContext(context, kResourceEdgeDistanceOffset, edgeDistance);
    ReadContext(context, kResourceWaterDistanceOffset, waterDistance);
    ReadContext(context, kFishWaterDistanceOffset, fishDistance);
    Trace("Resource edge distance", static_cast<uint32_t>((std::max)(0, edgeDistance)));
    Trace("Resource water distance", static_cast<uint32_t>((std::max)(0, waterDistance)));
    Trace("Fish water distance", static_cast<uint32_t>((std::max)(0, fishDistance)));
    const int distance = (std::max)(edgeDistance, waterDistance);
    return static_cast<uint16_t>((std::max)(2, (std::min)(64, distance + 2)));
}

bool RebindPositionGroup(
    const AnchorData& data,
    int worldWidth,
    int worldHeight,
    const std::vector<uint8_t>& playableCells,
    uint16_t minimumClearance,
    edmw::AlignmentTarget target,
    const std::vector<PositionBinding>& bindings,
    const std::vector<edmw::ResourcePosition>& reservedPositions,
    const std::vector<size_t>& selectedBindings) {
    if (selectedBindings.empty()) return true;

    std::vector<edmw::ResourcePosition> positions;
    std::vector<PositionMemberVector> memberVectors;
    positions.reserve(selectedBindings.size());
    memberVectors.reserve(selectedBindings.size());
    for (const size_t bindingIndex : selectedBindings) {
        if (bindingIndex >= bindings.size()) return false;
        PositionMemberVector members;
        if (!ReadPositionMemberVector(bindings[bindingIndex].record, members)) return false;
        positions.push_back(bindings[bindingIndex].position);
        memberVectors.push_back(members);
    }

    std::vector<std::vector<edmw::ResourcePosition>> domains;
    if (!edmw::BuildPositionLandDomains(
            data.terrain.data(), data.roles.data(), data.header.width, data.header.height,
            worldWidth, worldHeight, target, minimumClearance, positions,
            reservedPositions, domains, playableCells.data()) ||
        domains.size() != positions.size())
        return false;

    uint32_t totalCandidates = 0;
    uint32_t totalMembers = 0;
    for (size_t index = 0; index < positions.size(); ++index) {
        const auto& domain = domains[index];
        PositionMemberVector& members = memberVectors[index];
        if (domain.empty() || members.count == 0) return false;
        const size_t offset = (selectedBindings[index] * 2654435761u) % domain.size();
        float maximumRadiusSquared = 0.0f;
        for (size_t member = 0; member < members.count; ++member) {
            const size_t sample = (static_cast<size_t>(
                (static_cast<uint64_t>(member) * domain.size()) / members.count) + offset) %
                domain.size();
            members.values[member * 2] = domain[sample].x;
            members.values[member * 2 + 1] = domain[sample].y;
            const float dx = domain[sample].x - positions[index].x;
            const float dy = domain[sample].y - positions[index].y;
            maximumRadiusSquared = (std::max)(maximumRadiusSquared, dx * dx + dy * dy);
        }
        const float radius = std::sqrt(maximumRadiusSquared);
        memcpy(bindings[selectedBindings[index]].record + kPositionRadiusOffset,
               &radius, sizeof(radius));
        totalCandidates += static_cast<uint32_t>((std::min)(
            domain.size(), static_cast<size_t>(UINT32_MAX - totalCandidates)));
        totalMembers += static_cast<uint32_t>((std::min)(
            members.count, static_cast<size_t>(UINT32_MAX - totalMembers)));
    }
    Trace(target == edmw::AlignmentTarget::Player ? "Player domain records" :
          "Neutral domain records", static_cast<uint32_t>(positions.size()));
    Trace(target == edmw::AlignmentTarget::Player ? "Player domain candidates" :
          "Neutral domain candidates", totalCandidates);
    Trace(target == edmw::AlignmentTarget::Player ? "Player members rebound" :
          "Neutral members rebound", totalMembers);
    return true;
}

bool RebindPaintedLandDomains(void* context, const AnchorData& data,
                              int worldWidth, int worldHeight,
                              const std::vector<uint8_t>& playableCells) {
    std::vector<PositionBinding> bindings;
    int bindingWidth = 0;
    int bindingHeight = 0;
    if (!ReadPositionBindings(context, bindings, bindingWidth, bindingHeight) ||
        bindingWidth != worldWidth || bindingHeight != worldHeight) {
        Trace("Position domain vector unavailable", data.header.mapId);
        return false;
    }

    std::vector<size_t> playerBindings;
    std::vector<size_t> neutralBindings;
    std::vector<edmw::ResourcePosition> reservedPositions;
    reservedPositions.reserve(bindings.size());
    for (size_t index = 0; index < bindings.size(); ++index) {
        reservedPositions.push_back(bindings[index].position);
        (bindings[index].neutral ? neutralBindings : playerBindings).push_back(index);
    }
    const uint16_t minimumClearance = ResourceDomainClearance(context);
    const bool playerRebound = RebindPositionGroup(
        data, worldWidth, worldHeight, playableCells, minimumClearance,
        edmw::AlignmentTarget::Player, bindings, reservedPositions, playerBindings);
    if (!playerRebound)
        Trace("Player domain rebind failed", data.header.mapId);
    const bool neutralRebound = RebindPositionGroup(
        data, worldWidth, worldHeight, playableCells, minimumClearance,
        edmw::AlignmentTarget::Neutral, bindings, reservedPositions, neutralBindings);
    if (!neutralRebound)
        Trace("Neutral domain rebind failed", data.header.mapId);
    return playerRebound && neutralRebound;
}

void RefreshPaintedLandDomains(void* context, uint32_t id) {
    if (!context || !id) return;
    if (!kEnablePostGenerationPlacementRewrite) {
        Trace("Resource domain rebind skipped (multiplayer sync)", id);
        return;
    }
    try {
        AnchorData data;
        if (!LoadAnchorData(id, data)) {
            Trace("Resource domain payload unavailable", id);
            return;
        }

        int worldWidth = 0;
        int worldHeight = 0;
        if (!ReadActiveWorldDimensions(context, worldWidth, worldHeight)) {
            Trace("Resource domain dimensions unavailable", id);
            return;
        }

        std::vector<uint8_t> playableCells;
        if (!ReadPlayableWorldMask(context, worldWidth, worldHeight, playableCells)) {
            Trace("Resource domain playable mask unavailable", id);
            return;
        }
        if (RebindPaintedLandDomains(context, data, worldWidth, worldHeight, playableCells))
            Trace("Resource domains refreshed", id);
        else
            Trace("Resource domain refresh incomplete", id);
    } catch (...) {
        Trace("Resource domain refresh failed", id);
    }
}

bool PrepareFullLandEnvelope(void* context, uint32_t id) {
    if (!context || !id) return false;
    void* rawReservations = nullptr;
    void* rawEligibility = nullptr;
    int reservationWidth = 0;
    int reservationHeight = 0;
    int eligibilityWidth = 0;
    int eligibilityHeight = 0;
    if (!ReadGridDimensions(context, kResourceReservationPointerOffset,
            kResourceReservationWidthOffset, kResourceReservationHeightOffset,
            rawReservations, reservationWidth, reservationHeight, sizeof(uint8_t)) ||
        !ReadGridDimensions(context, kEligibilityMaskPointerOffset,
            kEligibilityMaskWidthOffset, kEligibilityMaskHeightOffset,
            rawEligibility, eligibilityWidth, eligibilityHeight, sizeof(uint8_t)))
        return false;

    const size_t reservationCount =
        static_cast<size_t>(reservationWidth) * reservationHeight;
    const size_t eligibilityCount =
        static_cast<size_t>(eligibilityWidth) * eligibilityHeight;
    auto* reservations = static_cast<uint8_t*>(rawReservations);
    auto* eligibility = static_cast<uint8_t*>(rawEligibility);
    if (!IsAccessibleRange(reservations, reservationCount, true) ||
        !IsAccessibleRange(eligibility, eligibilityCount, true))
        return false;

    AcquireSRWLockExclusive(&g_landLock);
    const bool canRestore = g_originalTopologyMapId == id &&
        g_originalReservationWidth == reservationWidth &&
        g_originalReservationHeight == reservationHeight &&
        g_originalEligibilityWidth == eligibilityWidth &&
        g_originalEligibilityHeight == eligibilityHeight &&
        g_originalResourceReservations.size() == reservationCount &&
        g_originalEligibility.size() == eligibilityCount;
    if (canRestore) {
        memcpy(reservations, g_originalResourceReservations.data(), reservationCount);
        memcpy(eligibility, g_originalEligibility.data(), eligibilityCount);
    } else {
        // At this pre-height seam no resources exist yet. Preserve historical
        // fail-safe behavior if an unusual script skipped position generation,
        // but never synthesize land/water state into the reservation grid again.
        std::fill(reservations, reservations + reservationCount, static_cast<uint8_t>(0));
    }
    g_fullLandEnvelopePrepared = true;
    g_landEnvelopeWidth = reservationWidth;
    g_landEnvelopeHeight = reservationHeight;
    g_landEnvelopeMapId = id;
    ReleaseSRWLockExclusive(&g_landLock);
    Trace(canRestore ? "Position topology grids restored" :
                       "Resource reservations initialized",
          static_cast<uint32_t>((std::min)(
              reservationCount, static_cast<size_t>(UINT32_MAX))));
    return true;
}

bool HasPreparedLandEnvelope(uint32_t id) {
    AcquireSRWLockShared(&g_landLock);
    const bool prepared = id != 0 && g_fullLandEnvelopePrepared && g_landEnvelopeMapId == id;
    ReleaseSRWLockShared(&g_landLock);
    return prepared;
}

bool HasValidAnchorPayload(uint32_t id) {
    AnchorData data;
    return id != 0 && LoadAnchorData(id, data);
}

template <typename T, typename IsLand>
void TraceTopologyAgreementForGrid(const AnchorData& data, const T* values,
                                   int width, int height, int activeWidth, int activeHeight,
                                   const std::vector<uint32_t>& sourceIndices,
                                   bool engineHeightLayout,
                                   IsLand isLand,
                                   const char* missingMessage, const char* extraMessage) {
    uint32_t missing = 0;
    uint32_t extra = 0;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            bool expected = false;
            if (x < activeWidth && y < activeHeight) {
                const uint32_t source =
                    sourceIndices[edmw::RowMajorCellIndex(x, y, activeWidth)];
                expected = source != UINT32_MAX && data.terrain[source] > 1;
            }
            const bool actual = isLand(values[HeightStorageIndex(
                x, y, width, activeWidth, activeHeight, engineHeightLayout)]);
            if (expected && !actual) ++missing;
            if (!expected && actual) ++extra;
        }
    }
    Trace(missingMessage, missing);
    Trace(extraMessage, extra);
}

void TraceTopologyAgreement(void* context, uint32_t id) {
    if (!TraceEnabled() || !context || !id) return;
    AnchorData data;
    if (!LoadAnchorData(id, data)) return;

    void* raw = nullptr;
    int width = 0;
    int height = 0;
    int activeWorldWidth = 0;
    int activeWorldHeight = 0;
    if (!ReadActiveWorldDimensions(context, activeWorldWidth, activeWorldHeight)) return;
    std::vector<uint8_t> playableCells;
    if (!ReadPlayableWorldMask(
            context, activeWorldWidth, activeWorldHeight, playableCells))
        return;
    std::vector<uint32_t> sourceIndices;
    if (!edmw::BuildAuthoredWorldSourceIndexMap(
            data.header.width, data.header.height, activeWorldWidth, activeWorldHeight,
            playableCells.data(), sourceIndices))
        return;
    float threshold = 0.0f;
    if (ReadGridDimensions(context, kHeightPointerOffset, kHeightWidthOffset,
                           kHeightHeightOffset, raw, width, height, sizeof(float)) &&
        ReadContext(context, kLandThresholdOffset, threshold)) {
        TraceTopologyAgreementForGrid(data, static_cast<const float*>(raw), width, height,
            (std::min)(width, activeWorldWidth > 1 ? activeWorldWidth : width),
            (std::min)(height, activeWorldHeight > 1 ? activeWorldHeight : height),
            sourceIndices,
            HeightGridUsesEngineLayout(),
            [threshold](float value) { return value >= threshold; },
            "Agreement height missing", "Agreement height extra");
    }
    if (ReadGridDimensions(context, kResourceReservationPointerOffset,
                           kResourceReservationWidthOffset,
                           kResourceReservationHeightOffset,
                           raw, width, height, sizeof(uint8_t))) {
        TraceTopologyAgreementForGrid(data, static_cast<const uint8_t*>(raw), width, height,
            (std::min)(width, activeWorldWidth > 1 ? activeWorldWidth : width),
            (std::min)(height, activeWorldHeight > 1 ? activeWorldHeight : height),
            sourceIndices,
            false,
            [](uint8_t value) { return value == 0; },
            "Agreement land mask missing", "Agreement land mask extra");
    }
    if (ReadGridDimensions(context, kEligibilityMaskPointerOffset, kEligibilityMaskWidthOffset,
                           kEligibilityMaskHeightOffset, raw, width, height, sizeof(uint8_t))) {
        TraceTopologyAgreementForGrid(data, static_cast<const uint8_t*>(raw), width, height,
            (std::min)(width, activeWorldWidth > 1 ? activeWorldWidth : width),
            (std::min)(height, activeWorldHeight > 1 ? activeWorldHeight : height),
            sourceIndices,
            false,
            [](uint8_t value) { return value != 0; },
            "Agreement eligibility missing", "Agreement eligibility extra");
    }
    if (ReadGridDimensions(context, kSurfaceGridPointerOffset, kSurfaceGridWidthOffset,
                           kSurfaceGridHeightOffset, raw, width, height, sizeof(uint32_t))) {
        TraceTopologyAgreementForGrid(data, static_cast<const uint32_t*>(raw), width, height,
            (std::min)(width, activeWorldWidth > 1 ? activeWorldWidth : width),
            (std::min)(height, activeWorldHeight > 1 ? activeWorldHeight : height),
            sourceIndices,
            false,
            [](uint32_t value) { return value != 0; },
            "Agreement surface missing", "Agreement surface extra");
    }
}

uint32_t CountLandComponents(const std::vector<uint8_t>& land, int width, int height) {
    if (width < 1 || height < 1 ||
        land.size() != static_cast<size_t>(width) * static_cast<size_t>(height))
        return 0;

    std::vector<uint8_t> visited(land.size(), 0);
    std::vector<size_t> pending;
    uint32_t components = 0;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t start = static_cast<size_t>(y) * width + x;
            if (!land[start] || visited[start]) continue;
            ++components;
            visited[start] = 1;
            pending.push_back(start);
            while (!pending.empty()) {
                const size_t current = pending.back();
                pending.pop_back();
                const int currentX = static_cast<int>(current % width);
                const int currentY = static_cast<int>(current / width);
                const int neighborX[] = {currentX - 1, currentX + 1, currentX, currentX};
                const int neighborY[] = {currentY, currentY, currentY - 1, currentY + 1};
                for (int direction = 0; direction < 4; ++direction) {
                    const int nextX = neighborX[direction];
                    const int nextY = neighborY[direction];
                    if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                        continue;
                    const size_t next = static_cast<size_t>(nextY) * width + nextX;
                    if (!land[next] || visited[next]) continue;
                    visited[next] = 1;
                    pending.push_back(next);
                }
            }
        }
    }
    return components;
}

uint32_t MeasureLandClearance(const std::vector<uint8_t>& land, int dimension,
                              int centerX, int centerY) {
    if (dimension < 2 || centerX < 0 || centerY < 0 ||
        centerX >= dimension || centerY >= dimension ||
        land.size() != static_cast<size_t>(dimension) * dimension ||
        !land[static_cast<size_t>(centerY) * dimension + centerX])
        return 0;

    constexpr int maximumMeasuredClearance = 64;
    for (int radius = 1; radius <= maximumMeasuredClearance; ++radius) {
        const int left = centerX - radius;
        const int right = centerX + radius;
        const int top = centerY - radius;
        const int bottom = centerY + radius;
        if (left < 0 || top < 0 || right >= dimension || bottom >= dimension)
            return static_cast<uint32_t>(radius - 1);
        for (int x = left; x <= right; ++x) {
            if (!land[static_cast<size_t>(top) * dimension + x] ||
                !land[static_cast<size_t>(bottom) * dimension + x])
                return static_cast<uint32_t>(radius - 1);
        }
        for (int y = top + 1; y < bottom; ++y) {
            if (!land[static_cast<size_t>(y) * dimension + left] ||
                !land[static_cast<size_t>(y) * dimension + right])
                return static_cast<uint32_t>(radius - 1);
        }
    }
    return maximumMeasuredClearance + 1;
}

void TraceWorldState(void* context, uint32_t id) {
    if (!TraceEnabled() || !context || !id) return;
    try {
        AnchorData data;
        void* world = nullptr;
        uint8_t** cells = nullptr;
        int dimension = 0;
        float threshold = 0.0f;
        if (!LoadAnchorData(id, data) ||
            !ReadContext(context, kWorldPointerOffset, world) ||
            !ReadObjectField(world, kWorldCellPointerOffset, cells) ||
            !ReadObjectField(world, kWorldDimensionOffset, dimension) ||
            !ReadContext(context, kLandThresholdOffset, threshold) || !cells ||
            dimension < 2 || dimension > static_cast<int>(kMaxDimension)) {
            Trace("World state unavailable", id);
            return;
        }

        const size_t count = static_cast<size_t>(dimension) * dimension;
        if (!IsAccessibleRange(cells, count * sizeof(*cells), false)) {
            Trace("World cell matrix unavailable", id);
            return;
        }

        std::vector<uint8_t> heightLand(count, 0);
        std::vector<uint8_t> typedLand(count, 0);
        std::vector<uint8_t> playableCells(count, 0);
        for (size_t index = 0; index < count; ++index)
            playableCells[index] = cells[index] ? 1 : 0;
        std::vector<uint32_t> sourceIndices;
        if (!edmw::BuildAuthoredWorldSourceIndexMap(
                data.header.width, data.header.height, dimension, dimension,
                playableCells.data(), sourceIndices)) {
            Trace("World source mapping unavailable", id);
            return;
        }
        uint32_t readable = 0;
        uint32_t kindNine = 0;
        uint32_t kindNineOnExpectedLand = 0;
        uint32_t kindNineOnExpectedWater = 0;
        uint32_t heightMissing = 0;
        uint32_t heightExtra = 0;
        uint32_t typeMissing = 0;
        uint32_t typeExtra = 0;
        uint32_t heightQuadrants[4] = {};
        uint32_t typeQuadrants[4] = {};
        for (int y = 0; y < dimension; ++y) {
            for (int x = 0; x < dimension; ++x) {
                uint8_t* cell = cells[static_cast<size_t>(y) * dimension + x];
                const size_t index = static_cast<size_t>(y) * dimension + x;
                const uint32_t source = sourceIndices[index];
                const bool expected = source != UINT32_MAX && data.terrain[source] > 1;
                uint32_t kind = 0;
                float elevation = 0.0f;
                if (!ReadObjectField(cell, kWorldCellKindOffset, kind) ||
                    !ReadObjectField(cell, kWorldCellHeightOffset, elevation))
                    continue;

                ++readable;
                if (kind == 9) {
                    ++kindNine;
                    if (expected) ++kindNineOnExpectedLand;
                    else ++kindNineOnExpectedWater;
                }
                const int quadrant = (x >= dimension / 2 ? 1 : 0) |
                                     (y >= dimension / 2 ? 2 : 0);
                const bool actualHeightLand = elevation >= threshold;
                const bool actualTypedLand = kind != 9;
                heightLand[index] = actualHeightLand ? 1 : 0;
                typedLand[index] = actualTypedLand ? 1 : 0;
                if (expected && !actualHeightLand) ++heightMissing;
                if (!expected && actualHeightLand) ++heightExtra;
                if (expected && !actualTypedLand) ++typeMissing;
                if (!expected && actualTypedLand) ++typeExtra;
                if (actualHeightLand) ++heightQuadrants[quadrant];
                if (actualTypedLand) ++typeQuadrants[quadrant];
            }
        }

        Trace("World dimension", static_cast<uint32_t>(dimension));
        Trace("World readable cells", readable);
        Trace("World height land", static_cast<uint32_t>(
            std::count(heightLand.begin(), heightLand.end(), static_cast<uint8_t>(1))));
        Trace("World height components", CountLandComponents(heightLand, dimension, dimension));
        Trace("World height quadrant 0", heightQuadrants[0]);
        Trace("World height quadrant 1", heightQuadrants[1]);
        Trace("World height quadrant 2", heightQuadrants[2]);
        Trace("World height quadrant 3", heightQuadrants[3]);
        Trace("World height agreement missing", heightMissing);
        Trace("World height agreement extra", heightExtra);
        Trace("World typed land", static_cast<uint32_t>(
            std::count(typedLand.begin(), typedLand.end(), static_cast<uint8_t>(1))));
        Trace("World typed components", CountLandComponents(typedLand, dimension, dimension));
        Trace("World typed quadrant 0", typeQuadrants[0]);
        Trace("World typed quadrant 1", typeQuadrants[1]);
        Trace("World typed quadrant 2", typeQuadrants[2]);
        Trace("World typed quadrant 3", typeQuadrants[3]);
        Trace("World type agreement missing", typeMissing);
        Trace("World type agreement extra", typeExtra);
        Trace("World kind 9 cells", kindNine);
        Trace("World kind 9 expected land", kindNineOnExpectedLand);
        Trace("World kind 9 expected water", kindNineOnExpectedWater);

        std::vector<PositionBinding> bindings;
        int positionWidth = 0;
        int positionHeight = 0;
        if (ReadPositionBindings(context, bindings, positionWidth, positionHeight) &&
            positionWidth == dimension && positionHeight == dimension) {
            for (size_t index = 0; index < bindings.size(); ++index) {
                int32_t gridX = 0;
                int32_t gridY = 0;
                memcpy(&gridX, bindings[index].record + kPositionGridXOffset, sizeof(gridX));
                memcpy(&gridY, bindings[index].record + kPositionGridYOffset, sizeof(gridY));
                char heightMessage[96] = {};
                char typeMessage[96] = {};
                _snprintf_s(heightMessage, _TRUNCATE,
                            "World height clearance record %u", static_cast<unsigned>(index));
                _snprintf_s(typeMessage, _TRUNCATE,
                            "World type clearance record %u", static_cast<unsigned>(index));
                Trace(heightMessage, MeasureLandClearance(heightLand, dimension, gridX, gridY));
                Trace(typeMessage, MeasureLandClearance(typedLand, dimension, gridX, gridY));
            }
        }
    } catch (...) {
        Trace("World state trace failed", id);
    }
}

void ApplyPaintedTopology(void* context, uint32_t id) {
    if (!context || !id) return;
    AnchorData data;
    if (!LoadAnchorData(id, data)) {
        Trace("Payload unavailable", id);
        return;
    }
    const bool preserveCoastline = edmw::PreservesAuthoredCoastline(data.flags);
    const size_t promotedBeachCells = preserveCoastline ? 0 : edmw::GuaranteeBeachCoastline(
        data.terrain.data(), static_cast<int>(data.header.width),
        static_cast<int>(data.header.height), 0.60, data.elevation.data(), data.cliffs.data());
    Trace("Coast cells promoted to beach", static_cast<uint32_t>((std::min)(
        promotedBeachCells, static_cast<size_t>(UINT32_MAX))));

    float* heights = nullptr;
    int width = 0;
    int height = 0;
    float threshold = 0;
    if (!ReadContext(context, kHeightPointerOffset, heights) ||
        !ReadContext(context, kHeightWidthOffset, width) ||
        !ReadContext(context, kHeightHeightOffset, height) ||
        !ReadContext(context, kLandThresholdOffset, threshold) || !heights ||
        width < 2 || height < 2 || width > static_cast<int>(kMaxDimension) ||
        height > static_cast<int>(kMaxDimension))
        return;

    const size_t heightCount = static_cast<size_t>(width) * static_cast<size_t>(height);
    if (!IsAccessibleRange(heights, heightCount * sizeof(float), true))
        return;

    Trace("Height dimensions", PackDimensions(width, height));
    Trace("Height land before", static_cast<uint32_t>(std::count_if(
        heights, heights + heightCount, [threshold](float value) { return value >= threshold; })));

    int activeWidth = 0;
    int activeHeight = 0;
    if (!ReadActiveWorldDimensions(context, activeWidth, activeHeight) ||
        activeWidth > width || activeHeight > height) {
        Trace("Invalid active topology dimensions", PackDimensions(activeWidth, activeHeight));
        return;
    }
    Trace("Active topology dimensions", PackDimensions(activeWidth, activeHeight));
    std::vector<uint8_t> playableCells;
    if (!ReadPlayableWorldMask(context, activeWidth, activeHeight, playableCells)) {
        Trace("Topology playable mask unavailable", id);
        return;
    }
    std::vector<uint32_t> sourceIndices;
    if (!edmw::BuildAuthoredWorldSourceIndexMap(
            data.header.width, data.header.height, activeWidth, activeHeight,
            playableCells.data(), sourceIndices)) {
        Trace("Topology source mapping unavailable", id);
        return;
    }
    AcquireSRWLockShared(&g_landLock);
    const bool envelopeAvailable = g_fullLandEnvelopePrepared && g_landEnvelopeMapId == id &&
        g_landEnvelopeWidth == width && g_landEnvelopeHeight == height;
    ReleaseSRWLockShared(&g_landLock);
    if (!envelopeAvailable) {
        Trace("Topology skipped without full envelope", id);
        return;
    }

    // +0x118 is resource reservation state and +0x124 is the engine's general
    // eligibility domain. The position-stage snapshot was restored before this
    // call; painted topology is now expressed only through the height grid.
    Trace("Resource topology grids preserved", id);

    std::vector<uint8_t> heightTerrain(heightCount, 0);
    std::vector<uint8_t> heightLand(heightCount, 0);
    std::vector<uint8_t> heightPlayable(heightCount, 0);
    std::vector<int8_t> heightElevation(heightCount, static_cast<int8_t>(127));
    std::vector<uint8_t> heightRamps(heightCount, 0);
    std::vector<uint8_t> heightCliffs(heightCount, 0);
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const uint32_t source =
                sourceIndices[edmw::RowMajorCellIndex(x, y, activeWidth)];
            if (source == UINT32_MAX) continue;
            const size_t target = static_cast<size_t>(y) * width + x;
            heightTerrain[target] = data.terrain[source];
            heightPlayable[target] = 1;
            heightLand[target] = data.terrain[source] > 1 ? 1 : 0;
            heightElevation[target] = data.elevation[source];
            heightRamps[target] = data.ramps[source];
            heightCliffs[target] = data.cliffs[source];
        }
    }
    std::vector<uint16_t> coastalDistances;
    if (!edmw::BuildCoastalDistanceMap(
            heightLand.data(), width, height, coastalDistances,
            preserveCoastline ? heightPlayable.data() : nullptr)) {
        Trace("Coastal distance map failed", id);
        return;
    }
    uint32_t coastalLandCells = 0;
    uint32_t coastalWaterCells = 0;
    for (size_t index = 0; index < heightCount; ++index) {
        if (coastalDistances[index] != 1) continue;
        if (heightLand[index]) ++coastalLandCells;
        else ++coastalWaterCells;
    }
    Trace("Coastal land edge cells", coastalLandCells);
    Trace("Coastal water edge cells", coastalWaterCells);

    const float unlockedNoise = (std::min)(0.16f, 0.02f + data.header.variance * 0.0028f);
    float maximumResourceElevation = 0.0f;
    const bool validResourceElevation = ReadContext(
        context, kMaximumAnimalResourceElevationOffset, maximumResourceElevation) &&
        std::isfinite(maximumResourceElevation) && maximumResourceElevation > threshold;
    const float stockElevationStep = edmw::StockElevationStep(
        threshold, maximumResourceElevation);
    uint32_t elevationBits = UINT32_MAX;
    if (validResourceElevation)
        memcpy(&elevationBits, &maximumResourceElevation, sizeof(elevationBits));
    Trace("Height land threshold bits", [&threshold]() {
        uint32_t bits = 0;
        memcpy(&bits, &threshold, sizeof(bits));
        return bits;
    }());
    Trace("Height resource ceiling bits", elevationBits);
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t target = static_cast<size_t>(y) * width + x;
            float& value = heights[target];
            if (x >= activeWidth || y >= activeHeight) {
                value = validResourceElevation
                    ? edmw::StockElevationForTerrain(
                        0, coastalDistances[target], threshold, maximumResourceElevation)
                    : edmw::CoastalElevationForTerrain(
                        0, coastalDistances[target], threshold);
                continue;
            }
            const uint32_t source =
                sourceIndices[edmw::RowMajorCellIndex(x, y, activeWidth)];
            if (source == UINT32_MAX) {
                value = validResourceElevation
                    ? edmw::StockElevationForTerrain(
                        0, coastalDistances[target], threshold, maximumResourceElevation)
                    : edmw::CoastalElevationForTerrain(
                        0, coastalDistances[target], threshold);
                continue;
            }
            const float stockOffset = value - threshold;
            const float noiseLimit = data.roles[source] ? 0.02f : unlockedNoise;
            const int8_t paintedElevation = heightElevation[target];
            if (paintedElevation != 127 && paintedElevation != edmw::kPaintedCliffElevation) {
                // The editor's 25-point increments now correspond exactly to one stock
                // integer elevation: -25 shallows, 0 threshold, +25 flat land, +50 the
                // resource ceiling, and so on.
                value = threshold +
                    (static_cast<float>(paintedElevation) / 25.0f) * stockElevationStep;
            } else if (preserveCoastline && heightTerrain[target] == 0) {
                // Keep authored deep water genuinely deep throughout the shore
                // band, rather than dipping only its final coastal sample.
                const uint16_t depthDistance = coastalDistances[target] == UINT16_MAX
                    ? UINT16_MAX : (std::max)(static_cast<uint16_t>(5), coastalDistances[target]);
                value = edmw::StockElevationForTerrain(0, depthDistance, threshold,
                    threshold + 2.0f * stockElevationStep);
            } else {
                const float detail = (std::max)(
                    -noiseLimit, (std::min)(noiseLimit, stockOffset * 0.08f));
                value = validResourceElevation
                    ? edmw::ResourceCompatibleElevationForTerrain(
                        heightTerrain[target], coastalDistances[target], threshold,
                        maximumResourceElevation, detail)
                    : edmw::CoastalElevationForTerrain(
                        heightTerrain[target], coastalDistances[target], threshold) + detail;
            }
        }
    }
    edmw::HeightGradientSummary coastalGradientSummary;
    if (preserveCoastline) {
        Trace("Authored coast traversability preserved", id);
    } else if (edmw::ConstrainCoastalHeightGradient(
            heightLand.data(), coastalDistances.data(), width, height,
            threshold, stockElevationStep * 2.0f, heights, coastalGradientSummary)) {
        uint32_t maximumBeforeBits = 0;
        uint32_t maximumAfterBits = 0;
        memcpy(&maximumBeforeBits, &coastalGradientSummary.maximumBefore,
               sizeof(maximumBeforeBits));
        memcpy(&maximumAfterBits, &coastalGradientSummary.maximumAfter,
               sizeof(maximumAfterBits));
        Trace("Traversable coast adjusted cells", static_cast<uint32_t>((std::min)(
            coastalGradientSummary.adjustedCells, static_cast<size_t>(UINT32_MAX))));
        Trace("Traversable coast maximum before bits", maximumBeforeBits);
        Trace("Traversable coast maximum after bits", maximumAfterBits);
    } else {
        Trace("Traversable coast generation failed", id);
    }
    edmw::HeightGradientSummary gradientSummary;
    if (edmw::LimitLandHeightGradient(
            heightLand.data(), width, height, 0.16f, heights, gradientSummary)) {
        uint32_t maximumBeforeBits = 0;
        uint32_t maximumAfterBits = 0;
        memcpy(&maximumBeforeBits, &gradientSummary.maximumBefore, sizeof(maximumBeforeBits));
        memcpy(&maximumAfterBits, &gradientSummary.maximumAfter, sizeof(maximumAfterBits));
        Trace("Traversable slope adjusted cells", static_cast<uint32_t>((std::min)(
            gradientSummary.adjustedCells, static_cast<size_t>(UINT32_MAX))));
        Trace("Traversable slope maximum before bits", maximumBeforeBits);
        Trace("Traversable slope maximum after bits", maximumAfterBits);
    } else {
        Trace("Traversable slope generation failed", id);
    }
    edmw::AccessRampSummary accessRampSummary;
    const int accessRampHalfWidth = (std::max)(3, (std::min)(
        5, (std::min)(activeWidth, activeHeight) / 96));
    if (edmw::CarveElevatedLandAccessCorridors(
            heightLand.data(), heightTerrain.data(), heightElevation.data(), heightRamps.data(), width, height,
            0.04f, 0.16f, accessRampHalfWidth, heights, accessRampSummary, heightCliffs.data())) {
        uint32_t maximumStepBits = 0;
        memcpy(&maximumStepBits, &accessRampSummary.maximumCorridorStepAfter,
               sizeof(maximumStepBits));
        Trace("Access ramp elevated components", static_cast<uint32_t>((std::min)(
            accessRampSummary.elevatedComponents, static_cast<size_t>(UINT32_MAX))));
        Trace("Access ramp separated lowlands", static_cast<uint32_t>((std::min)(
            accessRampSummary.separatedLowlandRegions, static_cast<size_t>(UINT32_MAX))));
        Trace("Access ramp corridors", static_cast<uint32_t>((std::min)(
            accessRampSummary.corridors, static_cast<size_t>(UINT32_MAX))));
        Trace("Access ramp corridor cells", static_cast<uint32_t>((std::min)(
            accessRampSummary.corridorCells, static_cast<size_t>(UINT32_MAX))));
        Trace("Access ramp adjusted cells", static_cast<uint32_t>((std::min)(
            accessRampSummary.adjustedCells, static_cast<size_t>(UINT32_MAX))));
        Trace("Access ramp maximum step bits", maximumStepBits);
    } else {
        Trace("Access ramp generation failed", id);
    }

    // Gradient and access passes may reshape nearby land. Preserve flagged shore
    // types; legacy maps retain their guaranteed beach and shallow-water plane.
    if (preserveCoastline) {
        edmw::CoastHeightSummary coastSummary;
        if (!edmw::RestoreAuthoredCoastHeightGrid(data.flags,
                heightLand.data(), heightTerrain.data(), heightElevation.data(), heightRamps.data(),
                heightPlayable.data(), width, height, activeWidth, activeHeight, false,
                threshold, stockElevationStep, heights, coastSummary))
            Trace("Authored coast restoration failed", id);
    } else {
        for (size_t index = 0; index < heightCount; ++index) {
            if (coastalDistances[index] == 1 &&
                (heightTerrain[index] == 2 || heightTerrain[index] <= 1))
                heights[index] = threshold - stockElevationStep;
        }
    }
    edmw::CliffHeightSummary cliffSummary;
    if (!edmw::RestorePaintedCliffHeights(
            heightLand.data(), heightTerrain.data(), heightElevation.data(), heightRamps.data(),
            width, height, threshold, stockElevationStep, 0.04f, 2, heights, cliffSummary))
        Trace("Painted cliff restoration failed", id);
    if (cliffSummary.cliffCells) {
        Trace("Painted cliff cells", static_cast<uint32_t>(cliffSummary.cliffCells));
        Trace("Painted cliff steep edges", static_cast<uint32_t>(cliffSummary.steepBoundaryEdges));
        Trace("Painted cliff manual ramps", static_cast<uint32_t>(cliffSummary.rampComponents));
    }
    edmw::CliffHeightSummary forcedCliffSummary;
    if (!edmw::RestoreForcedElevationCliffHeightGrid(
            heightTerrain.data(), heightElevation.data(), heightCliffs.data(), heightPlayable.data(),
            width, height, activeWidth, activeHeight, false, threshold, stockElevationStep,
            heights, forcedCliffSummary))
        Trace("Forced elevation cliff restoration failed", id);
    if (forcedCliffSummary.cliffCells) {
        Trace("Forced elevation cliff cells", static_cast<uint32_t>(forcedCliffSummary.cliffCells));
        Trace("Forced elevation cliff steep edges", static_cast<uint32_t>(forcedCliffSummary.steepBoundaryEdges));
    }
    if (validResourceElevation) {
        const uint32_t eligibleHeightCells = static_cast<uint32_t>(std::count_if(
            heights, heights + heightCount,
            [threshold, maximumResourceElevation](float value) {
                return value >= threshold && value <= maximumResourceElevation;
            }));
        Trace("Height resource-eligible land", eligibleHeightCells);
    }
    Trace("Height land after", static_cast<uint32_t>(std::count_if(
        heights, heights + heightCount, [threshold](float value) { return value >= threshold; })));
    if (!edmw::EncodeHeightGridForEngine(
            heights, width, height, activeWidth, activeHeight)) {
        Trace("Height engine layout encoding failed", id);
        return;
    }
    AcquireSRWLockExclusive(&g_landLock);
    g_heightGridEngineEncoded = true;
    ReleaseSRWLockExclusive(&g_landLock);
    Trace("Height engine layout encoded", id);
    Trace("Topology applied", id);
}

void RestoreAuthoredHeightsAfterStockStage(void* context) {
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    AnchorData data;
    if (!context || !id || !LoadAnchorData(id, data)) return;
    const bool preserveCoastline = edmw::PreservesAuthoredCoastline(data.flags);
    if (!preserveCoastline && (data.flags & edmw::kForcedElevationCliffMaskFlag) == 0 &&
        std::find(data.elevation.begin(), data.elevation.end(), edmw::kPaintedCliffElevation) ==
            data.elevation.end()) return;
    if (!preserveCoastline)
        edmw::GuaranteeBeachCoastline(data.terrain.data(), static_cast<int>(data.header.width),
            static_cast<int>(data.header.height), 0.60, data.elevation.data(), data.cliffs.data());
    float* heights = nullptr;
    int width = 0, height = 0, activeWidth = 0, activeHeight = 0;
    float threshold = 0.0f, maximumResourceElevation = 0.0f;
    if (!ReadContext(context, kHeightPointerOffset, heights) ||
        !ReadContext(context, kHeightWidthOffset, width) ||
        !ReadContext(context, kHeightHeightOffset, height) ||
        !ReadContext(context, kLandThresholdOffset, threshold) || !heights ||
        width < 2 || height < 2 || width > static_cast<int>(kMaxDimension) ||
        height > static_cast<int>(kMaxDimension) ||
        !ReadActiveWorldDimensions(context, activeWidth, activeHeight) ||
        activeWidth > width || activeHeight > height) return;
    const size_t count = static_cast<size_t>(width) * height;
    if (!IsAccessibleRange(heights, count * sizeof(float), true)) return;
    std::vector<uint8_t> playableCells;
    std::vector<uint32_t> sourceIndices;
    if (!ReadPlayableWorldMask(context, activeWidth, activeHeight, playableCells) ||
        !edmw::BuildAuthoredWorldSourceIndexMap(data.header.width, data.header.height,
            activeWidth, activeHeight, playableCells.data(), sourceIndices)) return;
    std::vector<uint8_t> land(count, 0), terrain(count, 0), ramps(count, 0);
    std::vector<uint8_t> cliffs(count, 0);
    std::vector<uint8_t> heightPlayable(count, 0);
    std::vector<int8_t> elevation(count, 127);
    for (int y = 0; y < activeHeight; ++y) {
        for (int x = 0; x < activeWidth; ++x) {
            const uint32_t source = sourceIndices[edmw::RowMajorCellIndex(x, y, activeWidth)];
            if (source == UINT32_MAX) continue;
            const size_t target = edmw::RowMajorCellIndex(x, y, width);
            terrain[target] = data.terrain[source];
            heightPlayable[target] = 1;
            land[target] = terrain[target] > 1 ? 1 : 0;
            elevation[target] = data.elevation[source];
            ramps[target] = data.ramps[source];
            cliffs[target] = data.cliffs[source];
        }
    }
    ReadContext(context, kMaximumAnimalResourceElevationOffset, maximumResourceElevation);
    const float stockElevationStep = edmw::StockElevationStep(threshold, maximumResourceElevation);
    edmw::CoastHeightSummary coastSummary;
    if (!edmw::RestoreAuthoredCoastHeightGrid(data.flags, land.data(), terrain.data(),
            elevation.data(), ramps.data(), heightPlayable.data(), width, height,
            activeWidth, activeHeight, HeightGridUsesEngineLayout(),
            threshold, stockElevationStep, heights, coastSummary)) {
        Trace("Post-stock coast restoration failed", id);
        return;
    }
    if (preserveCoastline) {
        Trace("Post-stock authored shore cells", static_cast<uint32_t>(coastSummary.shorelineCells));
        Trace("Post-stock boundary land cells", static_cast<uint32_t>(coastSummary.boundaryLandCells));
    }
    edmw::CliffHeightSummary summary;
    if (!edmw::RestorePaintedCliffHeightGrid(land.data(), terrain.data(), elevation.data(),
            ramps.data(), width, height, activeWidth, activeHeight, HeightGridUsesEngineLayout(),
            threshold, stockElevationStep,
            0.04f, 2, heights, summary)) {
        Trace("Post-stock cliff restoration failed", id);
        return;
    }
    Trace("Post-stock cliff cells", static_cast<uint32_t>(summary.cliffCells));
    Trace("Post-stock cliff steep edges", static_cast<uint32_t>(summary.steepBoundaryEdges));
    Trace("Post-stock cliff manual ramps", static_cast<uint32_t>(summary.rampComponents));
    edmw::CliffHeightSummary forcedCliffSummary;
    if (!edmw::RestoreForcedElevationCliffHeightGrid(
            terrain.data(), elevation.data(), cliffs.data(), heightPlayable.data(),
            width, height, activeWidth, activeHeight, HeightGridUsesEngineLayout(),
            threshold, stockElevationStep, heights, forcedCliffSummary)) {
        Trace("Post-stock forced elevation cliff restoration failed", id);
        return;
    }
    Trace("Post-stock forced elevation cliff cells", static_cast<uint32_t>(forcedCliffSummary.cliffCells));
    Trace("Post-stock forced elevation cliff steep edges", static_cast<uint32_t>(forcedCliffSummary.steepBoundaryEdges));
}

int RunStockStage(void* context, const char* exportName, const char* traceName) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock(exportName));
    const int result = stock ? stock(context) : 0;
    Trace(traceName, static_cast<uint32_t>(result));
    TraceStageState(context);
    TraceTopologyAgreement(context, static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0)));
    TraceCandidateUnitVectors(context, traceName);
    return result;
}
}

#define STOCK_STAGE_EXPORT(functionName, traceName) \
    extern "C" __declspec(dllexport) int __cdecl functionName(void* context) { \
        return RunStockStage(context, #functionName, traceName); \
    }

STOCK_STAGE_EXPORT(EERMPIMCreateFlatMap, "Flat map result")

// Default.dll owns the internal tree template setup required by later map stages.
// When the designer enables a painted tree mask, its random coordinates are replaced
// immediately afterward with deterministic Forest-only positions from the payload.
extern "C" __declspec(dllexport) int __cdecl EERMPIMCreateTrees(void* context) {
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    AnchorData treeData;
    const bool hasTreeData = id != 0 && LoadAnchorData(id, treeData);
    const bool paintTreeMask = hasTreeData && (treeData.flags & kPaintedTreeMaskFlag) != 0;
    const bool manualResources = hasTreeData && (treeData.flags & kManualResourcePlacementFlag) != 0;
    const bool manualFauna = hasTreeData &&
        (treeData.flags & kManualFaunaPlacementFlag) != 0;
    TreePlacementEntry* treeEntriesBefore = nullptr;
    size_t treeEntryCountBefore = 0;
    if ((paintTreeMask || manualResources) &&
        !ReadTreePlacementVector(context, treeEntriesBefore, treeEntryCountBefore))
        Trace("Painted tree mask preflight unavailable", id);
    Trace("Trees stage begin", id);
    TraceResourceConfiguration(context, "trees before");
    TraceStageState(context);
    std::vector<uint32_t> before;
    CaptureTreeContext(context, before);
    TreeStageSnapshot beforeSnapshot;
    CaptureTreeStageSnapshot(context, beforeSnapshot);
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMCreateTrees"));
    int result = stock ? stock(context) : 0;
    std::vector<uint32_t> after;
    if (CaptureTreeContext(context, after)) TraceTreeContextChanges(before, after);
    TreeStageSnapshot afterSnapshot;
    CaptureTreeStageSnapshot(context, afterSnapshot);
    WriteTreeStageSnapshot(beforeSnapshot, afterSnapshot);
    if (paintTreeMask && treeEntriesBefore &&
        !RewritePaintedTreePositions(context, treeData, treeEntryCountBefore))
        Trace("Painted tree mask rewrite failed", id);
    if (result == 0 && manualFauna) {
        try {
            if (!ReconcileManualFauna(context, &treeData))
                Trace("Manual fauna reconciliation unavailable", id);
        } catch (...) {
            Trace("Manual fauna reconciliation failed", id);
        }
    }
    TraceFinalResourceEvidence(context, "after manual fauna reconciliation");
    Trace("Trees result", static_cast<uint32_t>(result));
    TraceResourceConfiguration(context, "trees after");
    TraceStageState(context);
    TraceWorldState(context, id);
    TraceCandidateUnitVectors(context, "after create trees");
    Trace("Trees stage end", id);
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMCreateNeutralLandWaterMap(void* context) {
    const int result = RunStockStage(
        context, "EERMPIMCreateNeutralLandWaterMap", "Neutral land result");
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Neutral, true);
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMFilterHeightMap(void* context) {
    TraceHeightTransitions(context, "before filter");
    const int result = RunStockStage(
        context, "EERMPIMFilterHeightMap", "Filter height result");
    if (result == 0) RestoreAuthoredHeightsAfterStockStage(context);
    TraceHeightTransitions(context, "after filter");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGenerateRamps(void* context) {
    TraceHeightTransitions(context, "before ramps");
    const int result = RunStockStage(context, "EERMPIMGenerateRamps", "Ramps result");
    if (result == 0) RestoreAuthoredHeightsAfterStockStage(context);
    TraceHeightTransitions(context, "after ramps");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGenerateRiverLandFill(void* context) {
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    if (HasValidAnchorPayload(id)) {
        uint8_t enabled = UINT8_MAX;
        if (ReadContext(context, kRiverLandFillEnabledOffset, enabled))
            Trace("River land fill pre-call enabled", enabled);
        const uint8_t disabled = 0;
        Trace("River land fill pre-call disable result",
              WriteContext(context, kRiverLandFillEnabledOffset, disabled) ? 1u : 0u);
    }
    const int result = RunStockStage(
        context, "EERMPIMGenerateRiverLandFill", "River fill result");
    if (result == 0) RestoreAuthoredHeightsAfterStockStage(context);
    if (HasValidAnchorPayload(id))
        Trace("Post-river topology mutation bypassed", id);
    // Empires_DMW.exe performs its internal-height-to-world-cell transfer only
    // after this exported callback returns (0x0079862E -> 0x007984CA and
    // 0x0079867C -> 0x0079E22B in the installed executable). Keep this label
    // explicit so a pre-transfer snapshot cannot be mistaken for validator input.
    Trace("World snapshot before host height transfer", id);
    TraceWorldState(context, id);
    return result;
}

extern "C" __declspec(dllexport) void* __cdecl PIMGetName() {
    void* name = GeneratorName();
    if (name) return name;
    using NameFn = void* (__cdecl*)();
    const auto stock = reinterpret_cast<NameFn>(ResolveStock("PIMGetName"));
    return stock ? stock() : nullptr;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMSetRandomMapValues(void* context, void* values) {
    uint32_t id = ExtractModuleMapId();
    const bool moduleTransport = id != 0;
    if (!moduleTransport) id = ExtractTransportMarker(context);
    InterlockedExchange(&g_mapId, static_cast<LONG>(id));
    ResetLandEnvelope();

    const auto stock = reinterpret_cast<SetValuesFn>(ResolveStock("EERMPIMSetRandomMapValues"));
    if (!stock) return 0;
    const int result = stock(context, values);

    if (!moduleTransport) {
        const uint32_t postSetId = ExtractTransportMarker(context);
        if (postSetId) id = postSetId;
    }
    InterlockedExchange(&g_mapId, static_cast<LONG>(id));
    if (HasValidAnchorPayload(id)) {
        uint8_t enabled = UINT8_MAX;
        if (ReadContext(context, kRiverLandFillEnabledOffset, enabled))
            Trace("River land fill parsed enabled", enabled);
        const uint8_t disabled = 0;
        Trace("River land fill parse-time disable result",
              WriteContext(context, kRiverLandFillEnabledOffset, disabled) ? 1u : 0u);
    }
    AnchorData faunaData;
    const bool hasFaunaData = id != 0 && LoadAnchorData(id, faunaData);
    Trace("Map initialized", id);
    TraceCandidateUnitVectors(context, "after set random map values");
    if (hasFaunaData)
        TraceFaunaPayloadAndCells(context, faunaData, "after set random map values");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMCreatePlayerLandWaterMap(void* context) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMCreatePlayerLandWaterMap"));
    if (!stock) return 0;
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    Trace("Player land stage begin", id);
    const int result = stock(context);
    Trace("Player land result", static_cast<uint32_t>(result));
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Player, true);
    TraceStageState(context);
    TraceTopologyAgreement(context, id);
    TraceCandidateUnitVectors(context, "after create player land water");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGeneratePlayerPositions(void* context) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMGeneratePlayerPositions"));
    if (!stock) return 0;
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    bool prepared = false;
    try {
        prepared = ExposePaintedTopologyForPositionGeneration(
            context, id, edmw::AlignmentTarget::Player);
    } catch (...) {
        Trace("Player placement topology preparation failed", id);
    }
    if (!prepared) Trace("Player placement continued without painted topology", id);
    const int result = stock(context);
    Trace("Player position result", static_cast<uint32_t>(result));
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Player, false);
    try {
        AnchorData playerStartData;
        if (id != 0 && LoadAnchorData(id, playerStartData) &&
            (playerStartData.flags & kManualPlayerStartPlacementFlag) != 0 &&
            !ApplyManualPlayerStarts(context, playerStartData))
            Trace("Manual player start rewrite failed", id);
    } catch (...) {
        Trace("Manual player start rewrite failed", id);
    }
    AlignGeneratedPositions(context, id, edmw::AlignmentTarget::Player);
    TraceCandidateUnitVectors(context, "after generate player positions");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGenerateNeutralPositions(void* context) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMGenerateNeutralPositions"));
    if (!stock) return 0;
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    bool prepared = false;
    try {
        prepared = ExposePaintedTopologyForPositionGeneration(
            context, id, edmw::AlignmentTarget::Neutral);
    } catch (...) {
        Trace("Neutral placement topology preparation failed", id);
    }
    if (!prepared) Trace("Neutral placement continued without painted topology", id);
    const int result = stock(context);
    Trace("Neutral position result", static_cast<uint32_t>(result));
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Neutral, false);
    if (result == 0 &&
        !AlignGeneratedPositions(context, id, edmw::AlignmentTarget::Neutral)) {
        Trace("Neutral position repair unsafe", id);
        return 1;
    }
    if (result == 0)
        TraceGeneratedPositionState(
            context, id, edmw::AlignmentTarget::Neutral, false);
    TraceCandidateUnitVectors(context, "after generate neutral positions");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMCreateHeightMap(void* context) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMCreateHeightMap"));
    if (!stock) return 0;
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    const bool paintedTopologyEnabled = PrepareFullLandEnvelope(context, id);
    if (id != 0 && !paintedTopologyEnabled) {
        // Continuing could expose the temporary +0x118/+0x124 position domain
        // to stock height/resource stages. Fail before touching those stages.
        Trace("Position topology grid restoration failed", id);
        return 1;
    }
    const int result = stock(context);
    Trace("Height result", static_cast<uint32_t>(result));
    if (paintedTopologyEnabled)
        ApplyPaintedTopology(context, id);
    else
        Trace("Painted topology unavailable after height generation", id);
    TraceStageState(context);
    TraceTopologyAgreement(context, id);
    TraceCandidateUnitVectors(context, "after create height map");
    return result;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGenerateShallows(void* context) {
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    if (HasPreparedLandEnvelope(id) || HasValidAnchorPayload(id)) {
        Trace("Stock shallows bypassed", id);
        TraceStageState(context);
        TraceCandidateUnitVectors(context, "after bypass shallows");
        return 0;
    }
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMGenerateShallows"));
    return stock ? stock(context) : 0;
}

extern "C" __declspec(dllexport) int __cdecl EERMPIMGenerateResources(void* context) {
    const auto stock = reinterpret_cast<GeneratorFn>(ResolveStock("EERMPIMGenerateResources"));
    const uint32_t id = static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0));
    RefreshPaintedLandDomains(context, id);
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Player, true);
    TraceGeneratedPositionState(
        context, id, edmw::AlignmentTarget::Neutral, true);
    TraceResourceConfiguration(context, "before");
    AnchorData resourceData;
    const bool hasManualResources =
        id != 0 && LoadAnchorData(id, resourceData) &&
        (resourceData.flags & kManualResourcePlacementFlag) != 0;
    TraceCandidateUnitVectors(context, "before generate resources");
    if (id != 0 && !resourceData.terrain.empty())
        TraceFaunaPayloadAndCells(context, resourceData, "before generate resources");
    const int result = stock ? stock(context) : 0;
    TracePlacementVector(context, "Stock resource vector");
    TraceFinalResourceEvidence(context, "after stock generate resources");
    TraceCandidateUnitVectors(context, "after stock generate resources");
    // Point resources are reconciled here. Fish and named wildlife wait until the
    // post-Trees seam, where both stock resource generation and forest occupancy
    // are final and each authored category can commit or roll back as a unit.
    if (hasManualResources &&
        !RewriteManualResourcePositions(context, resourceData))
        Trace("Final manual resource rewrite failed", id);
    TraceFinalResourceEvidence(context, "after manual resource reconciliation");
    if (id != 0 && !resourceData.terrain.empty())
        TraceFaunaPayloadAndCells(context, resourceData,
                                  "after manual resource reconciliation");
    Trace("Resources result", static_cast<uint32_t>(result));
    TraceResourceConfiguration(context, "after");
    TraceStageState(context);
    TraceWorldState(context, static_cast<uint32_t>(InterlockedCompareExchange(&g_mapId, 0, 0)));
    return result;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
