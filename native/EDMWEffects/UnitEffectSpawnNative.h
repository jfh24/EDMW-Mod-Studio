#pragma once
#include <algorithm>
#include <cstdint>

namespace edmw {
// Deterministic, bounded placement order shared by the native adapter and its
// portable tests. The callback validates the entire prospective footprint.
template<class CanPlace>
bool FindEffectSpawnPosition(int32_t centerX, int32_t centerY, int32_t footprint,
    uint32_t mapSize, CanPlace canPlace, int32_t& resultX, int32_t& resultY) {
    if (footprint < 1 || footprint > 64 || mapSize < 3 || mapSize > 65536 ||
        centerX < 0 || centerY < 0 || static_cast<uint32_t>(centerX) >= mapSize ||
        static_cast<uint32_t>(centerY) >= mapSize) return false;
    const int32_t step = (std::max)(1, footprint / 2);
    uint32_t candidates = 0;
    const auto visit = [&](int32_t offsetX, int32_t offsetY) {
        const int64_t x = static_cast<int64_t>(centerX) + offsetX * step;
        const int64_t y = static_cast<int64_t>(centerY) + offsetY * step;
        // The native placement predicate also samples adjacent terrain cells.
        if (x < 1 || y < 1 || x + footprint >= mapSize || y + footprint >= mapSize) return false;
        if (++candidates > 1024) return false;
        if (!canPlace(static_cast<int32_t>(x), static_cast<int32_t>(y))) return false;
        resultX = static_cast<int32_t>(x); resultY = static_cast<int32_t>(y);
        return true;
    };
    if (visit(0, 0)) return true;
    for (int32_t radius = 1; radius <= 32 && candidates < 1024; ++radius) {
        // Clockwise square rings, starting at the top left. No RNG, clocks,
        // current selection, or iteration through pointer-ordered containers.
        for (int32_t x = -radius; x <= radius && candidates < 1024; ++x)
            if (visit(x, -radius)) return true;
        for (int32_t y = -radius + 1; y <= radius && candidates < 1024; ++y)
            if (visit(radius, y)) return true;
        for (int32_t x = radius - 1; x >= -radius && candidates < 1024; --x)
            if (visit(x, radius)) return true;
        for (int32_t y = radius - 1; y > -radius && candidates < 1024; --y)
            if (visit(-radius, y)) return true;
    }
    return false;
}

bool ValidateNativeEffectSpawning();
// Call only on the simulation thread, after finishing actor-list iteration.
// Returns the new complex actor, or nullptr when no valid nearby space exists.
void* SpawnEffectObject(void* owner, uint32_t object, int32_t x, int32_t y);
}
