#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <array>
#include <cstring>
#include "UnitEffectSpawnNative.h"

namespace {
constexpr uintptr_t complexUnitVtable = 0x007F6C48;
constexpr uintptr_t placementAddress = 0x0073B25B;
constexpr uintptr_t completionAddress = 0x006BC86C;
using Placement = bool(__cdecl*)(void*, uint32_t, int32_t, int32_t, bool, bool, bool);
using Create = void*(__thiscall*)(void*, int32_t, int32_t, uint32_t, bool, void*, float);
using IsBuilding = bool(__thiscall*)(void*);
using Complete = void(__thiscall*)(void*, bool, bool, bool);

bool Copy(const void* source, void* destination, size_t size) {
    __try { memcpy(destination, source, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template<class T> T Read(const void* base, size_t offset) {
    T value{};
    if (base) Copy(static_cast<const uint8_t*>(base) + offset, &value, sizeof(value));
    return value;
}
template<size_t N> bool Signature(uintptr_t address, const std::array<uint8_t, N>& expected) {
    std::array<uint8_t, N> actual{};
    return Copy(reinterpret_cast<const void*>(address), actual.data(), N) && actual == expected;
}
bool KnownFactory(uintptr_t factory) {
    // Every complex-unit prototype subtype's six-argument +0x48 override.
    switch (factory) {
    case 0x00703C3F: case 0x006D7686: case 0x006D9500:
    case 0x006DE5E5: case 0x006DD49A: case 0x006DF0E8: return true;
    default: return false;
    }
}
bool CanPlace(void* owner, uint32_t object, int32_t x, int32_t y, int32_t size, uint32_t& budget) {
    const auto test = reinterpret_cast<Placement>(placementAddress);
    for (int32_t dx = 0; dx < size; ++dx) {
        for (int32_t dy = 0; dy < size; ++dy) {
            if (!budget) return false;
            --budget;
            // Native +0x1C mode avoids consulting the owner's synchronized
            // selection for movement connectivity. Terrain/slope/occupancy and
            // the owner's native construction restrictions still run.
            if (!test(owner, object, x + dx, y + dy, true, false, false)) return false;
        }
    }
    return true;
}
}

namespace edmw {
bool ValidateNativeEffectSpawning() {
    return Signature(placementAddress, std::array<uint8_t, 9>{ 0x55, 0x8B, 0xEC, 0xD9, 0x05, 0x84, 0xA3, 0x7F, 0x00 }) &&
        Signature(completionAddress, std::array<uint8_t, 6>{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C }) &&
        Signature(0x00703C3F, std::array<uint8_t, 9>{ 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00 }) &&
        Signature(0x005A3D5C, std::array<uint8_t, 7>{ 0x8B, 0x44, 0x24, 0x04, 0xC1, 0xE8, 0x18 });
}

void* SpawnEffectObject(void* owner, uint32_t object, int32_t x, int32_t y) {
    if (!owner || object >= 2049 || !ValidateNativeEffectSpawning()) return nullptr;
    const auto world = reinterpret_cast<void*>(0x0099EE58);
    const auto playerCount = Read<uint32_t>(world, 0xD8);
    const auto player = Read<uint32_t>(owner, 0x478);
    if (!playerCount || playerCount > 16 || player >= playerCount ||
        Read<void*>(reinterpret_cast<void*>(0x0099EED8), player * 4) != owner) return nullptr;
    const auto prototype = Read<void*>(reinterpret_cast<void*>(0x009D1BF8), object * 4);
    if (!prototype || Read<uint32_t>(prototype, 0x1AC) != object || Read<uint32_t>(prototype, 0x1A4) != 1) return nullptr;
    const auto table = Read<void*>(prototype, 0);
    if (reinterpret_cast<uintptr_t>(table) < 0x007E3000 || reinterpret_cast<uintptr_t>(table) > 0x0083CF00) return nullptr;
    const auto factory = Read<uintptr_t>(table, 0x48);
    const auto buildingPredicate = Read<uintptr_t>(table, 0x7C);
    if (!KnownFactory(factory) || (buildingPredicate != 0x007109E4 && buildingPredicate != 0x0074CAA7)) return nullptr;
    // Native construction requires a canonical per-owner statistics entry.
    const auto first = Read<uintptr_t>(owner, 0xAC8);
    const auto end = Read<uintptr_t>(owner, 0xACC);
    const auto capacity = Read<uintptr_t>(owner, 0xAD0);
    if (!first || end < first || capacity < end || (end - first) % 4 || (capacity - first) % 4 ||
        (capacity - first) / 4 > 100000 || object >= (end - first) / 4) return nullptr;
    const auto statistics = Read<void*>(reinterpret_cast<void*>(first), object * 4);
    if (!statistics || Read<void*>(statistics, 0xB4) != prototype) return nullptr;
    const auto terrain = Read<void*>(reinterpret_cast<void*>(0x0099EF20), 0);
    const auto mapSize = Read<uint32_t>(terrain, 0x150);
    const auto nativeFootprint = Read<int32_t>(prototype, 0x284);
    if (nativeFootprint < 0 || nativeFootprint > 64) return nullptr;
    const auto footprint = (std::max)(1, nativeFootprint);
    int32_t spawnX = 0, spawnY = 0;
    uint32_t tileBudget = 16384;
    if (!FindEffectSpawnPosition(x, y, footprint, mapSize,
        [&](int32_t candidateX, int32_t candidateY) {
            return CanPlace(owner, object, candidateX, candidateY, footprint, tileBudget);
        }, spawnX, spawnY)) return nullptr;
    // The native factory owns allocation, population/type counts, geometry,
    // visibility registration, world IDs and the owning player's actor list.
    const auto created = reinterpret_cast<Create>(factory)(prototype, spawnX, spawnY, player, false, nullptr, 0.0f);
    const auto actor = Read<void*>(created, 0x68);
    if (!actor || Read<uintptr_t>(actor, 0) != complexUnitVtable || Read<void*>(actor, 0x18) != owner ||
        Read<void*>(actor, 0x64) != prototype || Read<int32_t>(actor, 0x90) <= 0) return nullptr;
    // The factory queues world registration; ID +0x24 remains UINT32_MAX
    // until that queue runs. The returned live actor is already the result.
    if (Read<void*>(actor, 0x68) != actor) return nullptr;
    if (reinterpret_cast<IsBuilding>(buildingPredicate)(prototype)) {
        // Same completed-building transition as native scenario placement at
        // 0x0063BA08..0x0063BA10. +0x61B is never written by this adapter.
        reinterpret_cast<Complete>(completionAddress)(actor, true, false, true);
    }
    return actor;
}
}
