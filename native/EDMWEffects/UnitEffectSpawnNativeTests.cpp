#include "UnitEffectSpawnNative.h"
#include <cassert>
#include <iostream>
#include <set>
#include <utility>
#include <vector>

int main() {
    int32_t x = -1, y = -1;
    std::vector<std::pair<int32_t, int32_t>> visited;
    const auto record = [&](int32_t px, int32_t py) { visited.emplace_back(px, py); return visited.size() == 10; };
    assert(edmw::FindEffectSpawnPosition(20, 20, 1, 128, record, x, y));
    assert(visited.size() == 10 && visited[0] == std::make_pair(20, 20));
    const std::vector<std::pair<int32_t, int32_t>> firstRing = {
        {19,19},{20,19},{21,19},{21,20},{21,21},{20,21},{19,21},{19,20}
    };
    assert(std::equal(firstRing.begin(), firstRing.end(), visited.begin() + 1));
    assert((std::set<std::pair<int32_t, int32_t>>(visited.begin(), visited.end()).size() == visited.size()));
    std::vector<std::pair<int32_t, int32_t>> repeat;
    assert(edmw::FindEffectSpawnPosition(20, 20, 1, 128,
        [&](int32_t px, int32_t py) { repeat.emplace_back(px, py); return repeat.size() == 10; }, x, y));
    assert(repeat == visited);
    uint32_t count = 0;
    assert(!edmw::FindEffectSpawnPosition(128, 128, 1, 256,
        [&](int32_t, int32_t) { ++count; return false; }, x, y));
    assert(count == 1024);
    std::set<std::pair<int32_t, int32_t>> occupied;
    for (uint32_t index = 0; index < 64; ++index) {
        assert(edmw::FindEffectSpawnPosition(20, 20, 1, 128,
            [&](int32_t px, int32_t py) { return occupied.count({px,py}) == 0; }, x, y));
        assert(occupied.emplace(x, y).second);
    }
    count = 0;
    assert(edmw::FindEffectSpawnPosition(0, 0, 5, 128,
        [&](int32_t px, int32_t py) { ++count; assert(px >= 1 && py >= 1 && px + 5 < 128 && py + 5 < 128); return true; }, x, y));
    assert(count == 1);
    assert(!edmw::FindEffectSpawnPosition(INT32_MAX, INT32_MAX, 1, 256,
        [](int32_t, int32_t) { assert(false); return true; }, x, y));
    assert(!edmw::FindEffectSpawnPosition(1, 1, 65, 256,
        [](int32_t, int32_t) { assert(false); return true; }, x, y));
    assert(!edmw::FindEffectSpawnPosition(1, 1, 64, 32,
        [](int32_t, int32_t) { assert(false); return true; }, x, y));
    std::cout << "Native effect spawn placement tests passed.\n";
}
