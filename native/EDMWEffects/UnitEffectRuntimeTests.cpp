#include <cassert>
#include <cstdio>
#include <limits>
#include "UnitEffectRuntime.h"

namespace {
edmw::UnitEffectRule Proximity(uint8_t id = 1, int32_t amount = 3) {
    edmw::UnitEffectRule rule;
    rule.id[0] = id;
    rule.civilization = 10;
    rule.sourceObject = 749;
    rule.targetObject = 777;
    rule.sightAmount = amount;
    return rule;
}
edmw::UnitEffectSnapshot Ranger(uint32_t id, int32_t x, int32_t y, uint32_t player = 1) {
    return { id, player, 10, 749, x, y, true };
}
edmw::UnitEffectSnapshot Sherman(uint32_t id, int32_t x, int32_t y, uint32_t player = 1) {
    return { id, player, 10, 777, x, y, true };
}
}

int main() {
    edmw::UnitEffectRuntime runtime({ Proximity() });
    std::vector<edmw::UnitEffectSnapshot> units{ Sherman(1, 10, 10), Ranger(2, 13, 14) };
    auto changed = runtime.Evaluate(units, 1000);
    assert(changed.size() == 1 && changed[0].unit == 1 && changed[0].previous == 0 && changed[0].next == 3);
    assert(runtime.Evaluate(units, 1000).empty()); // Paused/duplicate tick cannot stack.
    units.push_back(Ranger(3, 10, 11));
    assert(runtime.Evaluate(units, 1100).empty() && runtime.Bonus(1) == 3); // Multiple providers do not stack.
    units[1].alive = false;
    assert(runtime.Evaluate(units, 1200).empty()); // Another provider remains.
    units[2].x = 16;
    changed = runtime.Evaluate(units, 1300);
    assert(changed.size() == 1 && changed[0].previous == 3 && changed[0].next == 0);
    units[2].x = 10;
    units[2].player = 2;
    assert(runtime.Evaluate(units, 1400).empty() && runtime.Bonus(1) == 0); // Enemy provider ignored.
    units[2].player = 1;
    units[2].civilization = 11;
    assert(runtime.Evaluate(units, 1500).empty() && runtime.Bonus(1) == 0);
    units[2].civilization = 10;
    runtime.Evaluate(units, 1600);
    units[0].player = 2;
    changed = runtime.Evaluate(units, 1700);
    assert(changed.size() == 1 && changed[0].next == 0); // Converted target no longer matches the provider.
    units[0].player = 1;
    runtime.Evaluate(units, 1800);
    units[0].alive = false;
    assert(runtime.Evaluate(units, 1900).empty() && runtime.Bonus(1) == 0); // Do not refresh a dead pointer.
    units[0] = Sherman(1, 100, 100);
    assert(runtime.Evaluate(units, 2000).empty() && runtime.Bonus(1) == 0); // Reused native ID has no stale bonus.

    units = { Sherman(1, 0, 0), Ranger(2, 5, 0) };
    edmw::UnitEffectRuntime both({ Proximity(1), Proximity(2, 2) });
    both.Evaluate(units, 0);
    assert(both.Bonus(1) == 5);
    edmw::UnitEffectRuntime onlySecond({ Proximity(2, 2) });
    onlySecond.Evaluate(units, 0);
    assert(onlySecond.Bonus(1) == 2); // Disabling/uninstalling one package preserves the other rule.
    edmw::UnitEffectRuntime noRules;
    noRules.Evaluate(units, 0);
    assert(noRules.Bonus(1) == 0);
    edmw::UnitEffectRuntime capped({ Proximity(1, 30), Proximity(2, 30) });
    capped.Evaluate(units, 0);
    assert(capped.Bonus(1) == 32);
    units[1].x = (std::numeric_limits<int32_t>::max)();
    units[0].x = (std::numeric_limits<int32_t>::min)();
    capped.Evaluate(units, 100);
    assert(capped.Bonus(1) == 0); // Extreme input coordinates cannot overflow the distance test.
    units = { Sherman(1, -9, -8), Ranger(2, -6, -4) };
    runtime.Evaluate(units, 2100);
    assert(runtime.Bonus(1) == 3); // Negative cell boundaries still include the exact radius.
    units[1].y = -3;
    runtime.Evaluate(units, 2200);
    assert(runtime.Bonus(1) == 0);
    units[1].y = -4;
    runtime.Evaluate(units, 2300);
    units[0].object = 123;
    changed = runtime.Evaluate(units, 2400);
    assert(changed.size() == 1 && changed[0].previous == 3 && changed[0].next == 0);

    auto toggle = Proximity();
    toggle.trigger = edmw::UnitEffectTrigger::Activated;
    toggle.sourceObject = toggle.targetObject;
    toggle.activation = edmw::UnitEffectActivation::Toggle;
    edmw::UnitEffectRuntime activated({ toggle });
    units = { Sherman(1, 0, 0), Sherman(2, 0, 0) };
    assert(!activated.Activate(toggle.id, Ranger(3, 0, 0), 0));
    assert(activated.Activate(toggle.id, units[0], 100));
    activated.Evaluate(units, 100);
    assert(activated.Bonus(1) == 3 && activated.Bonus(2) == 0);
    activated.Evaluate(units, 100000);
    assert(activated.Bonus(1) == 3);
    assert(activated.Activate(toggle.id, units[0], 100000));
    activated.Evaluate(units, 100000);
    assert(activated.Bonus(1) == 0);
    assert(activated.Activate(toggle.id, units[0], 100100));
    ++units[0].generation;
    assert(activated.Activate(toggle.id, units[0], 100100));
    activated.Evaluate(units, 100100);
    assert(activated.Bonus(1) == 3); // First command for a reused ID toggles the new unit on.

    auto timed = toggle;
    timed.activation = edmw::UnitEffectActivation::Timed;
    timed.durationMilliseconds = 5000;
    edmw::UnitEffectRuntime duration({ timed });
    assert(duration.Activate(timed.id, units[0], 1000));
    duration.Evaluate(units, 5999);
    assert(duration.Bonus(1) == 3);
    duration.Evaluate(units, 6000);
    assert(duration.Bonus(1) == 0);
    assert(duration.Activate(timed.id, units[0], 7000));
    duration.Evaluate(units, 7100);
    units[0].player = 2;
    duration.Evaluate(units, 7200);
    assert(duration.Bonus(1) == 0); // Activated state cannot transfer with conversion.
    units[0].player = 1;
    assert(duration.Activate(timed.id, units[0], 8000));
    duration.Evaluate(units, 8100);
    duration.Evaluate(units, 1000);
    assert(duration.Bonus(1) == 0); // New/load clock does not inherit activation state.
    assert(duration.Activate(timed.id, units[0], 1100));
    duration.Evaluate(units, 1200);
    ++units[0].generation;
    duration.Evaluate(units, 1300);
    assert(duration.Bonus(1) == 0); // Same pointer/ID/type from a unit pool is a different lifetime.

    units = { Ranger(2, 3, 4), Sherman(1, 0, 0), Sherman(3, 9, 9) };
    edmw::UnitEffectRuntime ordered({ Proximity() }), reversed({ Proximity() });
    ordered.Evaluate(units, 100);
    std::reverse(units.begin(), units.end());
    reversed.Evaluate(units, 100);
    assert(ordered.Bonus(1) == reversed.Bonus(1) && ordered.Bonus(3) == reversed.Bonus(3));

    // Cross-check the spatial index against a simple distance calculation over
    // many positive/negative cells, players, radii, dead providers and targets.
    for (uint32_t radius : { 1u, 5u, 8u, 9u, 32u, 64u }) {
        auto rule = Proximity();
        rule.radiusTiles = radius;
        edmw::UnitEffectRuntime indexed({ rule });
        std::vector<edmw::UnitEffectSnapshot> sample;
        for (uint32_t id = 1; id <= 160; ++id) {
            const auto x = static_cast<int32_t>((id * 31) % 193) - 96;
            const auto y = static_cast<int32_t>((id * 47) % 211) - 105;
            auto unit = id % 3 ? Ranger(id, x, y, id % 2 + 1) : Sherman(id, x, y, id % 2 + 1);
            unit.alive = id % 7 != 0;
            sample.push_back(unit);
        }
        indexed.Evaluate(sample, 1);
        for (const auto& target : sample) {
            bool nearby = false;
            if (target.object == 777 && target.alive)
                for (const auto& source : sample) {
                    if (!source.alive || source.object != 749 || source.player != target.player) continue;
                    const int64_t dx = int64_t(source.x) - target.x;
                    const int64_t dy = int64_t(source.y) - target.y;
                    if (dx * dx + dy * dy <= int64_t(radius) * radius) nearby = true;
                }
            assert(indexed.Bonus(target.id) == (nearby ? 3 : 0));
        }
    }
    puts("UNIT EFFECTS PASS: radius boundary, source death, owner/civilization filters, non-stacking providers, independent mods, reused IDs, deterministic order, toggle and simulation-time expiry.");
}
