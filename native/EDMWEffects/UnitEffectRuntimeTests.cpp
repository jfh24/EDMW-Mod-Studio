#include <cassert>
#include <cstdio>
#include <limits>
#include "UnitEffectRuntime.h"
#include "UnitStatProjection.h"
#include "UnitEffectExtras.h"
#include "NativeAbilityBindings.h"

namespace {
edmw::UnitEffectRule Proximity(uint8_t id = 1, float amount = 3.0f) {
    edmw::UnitEffectRule rule;
    rule.id[0] = id;
    rule.civilization = 10;
    rule.sourceObject = 749;
    rule.targetObject = 777;
    rule.amount = amount;
    return rule;
}
edmw::UnitEffectSnapshot Ranger(uint32_t id, int32_t x, int32_t y, uint32_t player = 1) {
    return { id, player, 10, 749, x, y, true };
}
edmw::UnitEffectSnapshot Sherman(uint32_t id, int32_t x, int32_t y, uint32_t player = 1) {
    return { id, player, 10, 777, x, y, true };
}
void VerifyProximityExcludesEmitter() {
    auto rule = Proximity(72);
    rule.civilization = 4;
    rule.sourceObject = rule.targetObject = 878;
    rule.radiusTiles = 5;
    rule.outputs = { { edmw::UnitEffectStat::Health, 1000.0f }, { edmw::UnitEffectStat::LineOfSight, 30.0f } };
    edmw::UnitEffectRuntime runtime({ rule });
    const edmw::UnitEffectSnapshot first{ 18, 1, 4, 878, 10, 10, true, 1 };
    auto second = first; second.id = 19; second.x = 13; second.y = 14;
    const auto expect = [&](uint32_t id, bool affected) {
        assert(runtime.Bonus(id, edmw::UnitEffectStat::Health) == (affected ? 1000.0f : 0.0f));
        assert(runtime.Bonus(id, edmw::UnitEffectStat::LineOfSight) == (affected ? 30.0f : 0.0f));
    };
    assert(runtime.Evaluate({ first }, 0).empty());
    expect(18, false); // Matching source/target types never make an isolated emitter buff itself.
    assert(runtime.Evaluate({ first, second }, 1).size() == 4);
    expect(18, true); expect(19, true); // Two emitters affect each other at the exact 3-4-5 boundary.
    second.x = 16; second.y = 10;
    assert(runtime.Evaluate({ first, second }, 2).size() == 4);
    expect(18, false); expect(19, false);
    second.x = 15;
    runtime.Evaluate({ first, second }, 3);
    expect(18, true); expect(19, true); // Axis-aligned distance exactly five is included.
    second.y = 11;
    runtime.Evaluate({ first, second }, 4);
    expect(18, false); expect(19, false); // Squared distance 26 is outside radius five.
    second.x = 10; second.y = 10; second.player = 2;
    runtime.Evaluate({ first, second }, 5);
    expect(18, false); expect(19, false);
    second.player = 1; second.civilization = 5;
    runtime.Evaluate({ first, second }, 6);
    expect(18, false); expect(19, false);
    second.civilization = 4; second.alive = false;
    runtime.Evaluate({ first, second }, 7);
    expect(18, false); expect(19, false);
    second.alive = true; second.x = 13; second.y = 14;
    auto third = first; third.id = 20; third.y = 11;
    runtime.Evaluate({ first, second, third }, 8);
    expect(18, true); expect(19, true); expect(20, true); // Multiple other providers do not multiply either output.
    second.alive = false;
    runtime.Evaluate({ first, second, third }, 9);
    expect(18, true); expect(19, false); expect(20, true);
    third.alive = false;
    runtime.Evaluate({ first, second, third }, 10);
    expect(18, false); expect(19, false); expect(20, false);
}
}

int main() {
    VerifyProximityExcludesEmitter();
    {
        auto aura = Proximity(70);
        aura.outputs = { { edmw::UnitEffectStat::Health, 1000.0f }, { edmw::UnitEffectStat::LineOfSight, 30.0f },
            { edmw::UnitEffectStat::ModelScale, 0.75f } };
        auto ability = aura;
        ability.trigger = edmw::UnitEffectTrigger::Activated;
        ability.sourceObject = ability.targetObject;
        ability.radiusTiles = 0;
        ability.activation = edmw::UnitEffectActivation::Timed;
        ability.durationMilliseconds = 30000;
        edmw::UnitEffectRuntime auraRuntime({ aura }), abilityRuntime({ ability });
        const auto target = Sherman(100, 0, 0), nearby = Ranger(101, 3, 4);
        assert(abilityRuntime.Activate(ability.id, target, 0));
        auraRuntime.Evaluate({ target, nearby }, 0);
        abilityRuntime.Evaluate({ target, nearby }, 0);
        edmw::UnitStatProjection auraStats, abilityStats;
        edmw::UnitStatProjection::Put(auraStats.prototype, 0x254, 250);
        edmw::UnitStatProjection::Put(auraStats.statistics, 0x184, 250);
        abilityStats = auraStats;
        for (const auto& output : aura.outputs) {
            assert(auraRuntime.Bonus(100, output.stat) == abilityRuntime.Bonus(100, output.stat));
            auraStats.Apply(output.stat, auraRuntime.Bonus(100, output.stat));
            abilityStats.Apply(output.stat, abilityRuntime.Bonus(100, output.stat));
        }
        assert(auraStats.prototype == abilityStats.prototype && auraStats.statistics == abilityStats.statistics);
        assert((edmw::UnitStatProjection::Read<0x1C0, int32_t>(auraStats.statistics, 0x184) == 1250));
        assert(auraRuntime.Bonus(100) == 30.0f && abilityRuntime.Bonus(100) == 30.0f);
        auto distant = nearby;
        distant.x = 6;
        distant.y = 0;
        auraRuntime.Evaluate({ target, distant }, 1);
        assert(auraRuntime.Bonus(100, edmw::UnitEffectStat::Health) == 0.0f);
        assert(abilityRuntime.Bonus(100, edmw::UnitEffectStat::Health) == 1000.0f);
    }
    {
        auto rule = Proximity(71);
        rule.trigger = edmw::UnitEffectTrigger::Activated;
        rule.sourceObject = rule.targetObject;
        rule.activation = edmw::UnitEffectActivation::Toggle;
        rule.outputs = { { edmw::UnitEffectStat::Health, 1000.0f }, { edmw::UnitEffectStat::ModelScale, 0.75f } };
        auto first = Sherman(201, 0, 0), second = Sherman(202, 0, 0), foreign = Sherman(203, 0, 0, 2);
        auto dead = Sherman(204, 0, 0); dead.alive = false;
        auto wrongType = Ranger(205, 0, 0);
        std::vector<edmw::UnitEffectSnapshot> selection{ second, first, second, foreign, dead, wrongType };
        edmw::UnitEffectRuntime group({ rule });
        assert(group.Activate(rule.id, first, 0));
        assert(group.ActivateGroup(rule.id, selection, 1, 1) == 2);
        group.Evaluate(selection, 1);
        assert(group.Bonus(201, edmw::UnitEffectStat::Health) == 1000 && group.Bonus(202, edmw::UnitEffectStat::Health) == 1000);
        assert(group.Bonus(203, edmw::UnitEffectStat::Health) == 0 && group.Bonus(204, edmw::UnitEffectStat::Health) == 0);
        assert(group.ActivateGroup(rule.id, selection, 1, 2) == 2);
        group.Evaluate(selection, 2);
        assert(group.Bonus(201, edmw::UnitEffectStat::Health) == 0 && group.Bonus(202, edmw::UnitEffectStat::Health) == 0);
        rule.activation = edmw::UnitEffectActivation::Timed;
        rule.durationMilliseconds = 30000;
        edmw::UnitEffectRuntime timedGroup({ rule });
        assert(timedGroup.ActivateGroup(rule.id, selection, 1, 100) == 2);
        timedGroup.Evaluate(selection, 30099);
        assert(timedGroup.Bonus(201, edmw::UnitEffectStat::Health) == 1000 && timedGroup.Bonus(202, edmw::UnitEffectStat::Health) == 1000);
        assert(timedGroup.Activate(rule.id, first, 30100));
        timedGroup.Evaluate(selection, 30100);
        assert(timedGroup.Bonus(201, edmw::UnitEffectStat::Health) == 1000 && timedGroup.Bonus(202, edmw::UnitEffectStat::Health) == 0);
    }
    // Reproduce an actual Chinese citizen command across network/world identity
    // boundaries, then exercise both activation and native overlay restoration.
    for (const auto identity : { 0u, 1u, 42u, 254u }) {
        const auto player = edmw::NativeAbilityCommandPlayerIndex(identity, identity, 1, 3);
        assert(player == 1);
        edmw::UnitEffectRule hulk;
        hulk.id[0] = 99;
        hulk.civilization = 4;
        hulk.sourceObject = hulk.targetObject = 878;
        hulk.trigger = edmw::UnitEffectTrigger::Activated;
        hulk.radiusTiles = 0;
        hulk.activation = edmw::UnitEffectActivation::Timed;
        hulk.durationMilliseconds = 30000;
        hulk.outputs = { { edmw::UnitEffectStat::ModelScale, 0.75f }, { edmw::UnitEffectStat::MovementSpeed, 0.125f } };
        edmw::UnitEffectRuntime hulkRuntime({ hulk });
        const edmw::UnitEffectSnapshot citizen{ 200, player, edmw::NativeCivilizationToWireId(3), 878, 0, 0, true, 1 };
        auto neighbor = citizen;
        neighbor.id = 201;
        assert(hulkRuntime.Activate(hulk.id, citizen, 1500));
        hulkRuntime.Evaluate({ citizen, neighbor }, 1500);
        assert(hulkRuntime.Bonus(200, edmw::UnitEffectStat::ModelScale) == 0.75f &&
            hulkRuntime.Bonus(200, edmw::UnitEffectStat::MovementSpeed) == 0.125f &&
            hulkRuntime.Bonus(201, edmw::UnitEffectStat::ModelScale) == 0.0f);
        edmw::ReplacedFloatOverlay scale;
        auto visibleScale = scale.Update(1.0f, hulkRuntime.Bonus(200, edmw::UnitEffectStat::ModelScale));
        assert(visibleScale == 1.75f);
        hulkRuntime.Evaluate({ citizen, neighbor }, 31499);
        visibleScale = scale.Update(visibleScale, hulkRuntime.Bonus(200, edmw::UnitEffectStat::ModelScale));
        assert(visibleScale == 1.75f);
        hulkRuntime.Evaluate({ citizen, neighbor }, 31500);
        visibleScale = scale.Update(visibleScale, hulkRuntime.Bonus(200, edmw::UnitEffectStat::ModelScale));
        assert(visibleScale == 1.0f && hulkRuntime.Bonus(200, edmw::UnitEffectStat::MovementSpeed) == 0.0f);
    }
    assert(edmw::NativeCivilizationToWireId(3) == 4 && edmw::NativeCivilizationToWireId(11) == 12);
    assert(edmw::NativeCivilizationToWireId(2) == 0 && edmw::NativeCivilizationToWireId(12) == 0 &&
        edmw::NativeCivilizationToWireId(UINT32_MAX) == 0);
    auto chineseEffect = Proximity();
    chineseEffect.civilization = 4;
    edmw::UnitEffectRuntime chineseRuntime({ chineseEffect });
    auto chineseTarget = Sherman(1, 0, 0), chineseSource = Ranger(2, 0, 0);
    chineseTarget.civilization = chineseSource.civilization = edmw::NativeCivilizationToWireId(3);
    chineseRuntime.Evaluate({ chineseTarget, chineseSource }, 0);
    assert(chineseRuntime.Bonus(1) == 3);
    chineseTarget.civilization = edmw::NativeCivilizationToWireId(4);
    chineseRuntime.Evaluate({ chineseTarget, chineseSource }, 1);
    assert(chineseRuntime.Bonus(1) == 0); // Native row 4 is another civilization, not wire China 4.
    chineseTarget.civilization = edmw::NativeCivilizationToWireId(3);
    chineseSource.civilization = edmw::NativeCivilizationToWireId(4);
    chineseRuntime.Evaluate({ chineseTarget, chineseSource }, 2);
    assert(chineseRuntime.Bonus(1) == 0);
    edmw::UnitEffectCounterContribution population;
    assert(population.Update(90, 20) == 110);
    assert(population.Update(80, 20) == 80); // Native removal of 30 population remains intact.
    assert(population.Update(80, 0) == 60); // Expiry removes only this runtime's contribution.
    edmw::UnitEffectCounterContribution populationSaturation;
    assert(populationSaturation.Update(INT32_MAX - 5, 10) == INT32_MAX);
    assert(populationSaturation.Update(INT32_MAX, 0) == INT32_MAX - 5);
    edmw::UnitEffectCounterContribution populationReduction;
    assert(populationReduction.Update(5, -20) == 0);
    assert(populationReduction.Update(2, -20) == 0); // Native additions while clamped are not lost.
    assert(populationReduction.Update(0, 0) == 7);
    for (uint32_t sequence = 0; sequence < 100; ++sequence) {
        assert(edmw::UnitEffectAccuracyHit(0.0f, 10, 20, 500, sequence));
        assert(!edmw::UnitEffectAccuracyHit(-100.0f, 10, 20, 500, sequence));
        assert(edmw::UnitEffectAccuracyHit(-50.0f, 10, 20, 500, sequence) ==
            edmw::UnitEffectAccuracyHit(-50.0f, 10, 20, 500, sequence));
    }
    bool observedHit = false, observedMiss = false;
    for (uint32_t sequence = 0; sequence < 100; ++sequence) {
        const auto hit = edmw::UnitEffectAccuracyHit(-50.0f, 10, 20, 500, sequence);
        observedHit |= hit; observedMiss |= !hit;
    }
    assert(observedHit && observedMiss);
    edmw::UnitEffectRegeneration regeneration;
    assert(regeneration.Update(0, 50, 100, 0.25f) == 50);
    assert(regeneration.Update(1000, 40, 100, 0.25f) == 40); // Native combat damage remains authoritative.
    assert(regeneration.Update(4000, 40, 100, 0.25f) == 41); // Fractional healing carries across ticks.
    regeneration.Reset();
    regeneration.Update(0, 100, 100, 5.5f);
    assert(regeneration.Update(1000, 100, 100, 5.5f) == 100);
    assert(regeneration.Update(1100, 80, 100, 0.0f) == 80); // No healing stored while at maximum.
    regeneration.Reset();
    regeneration.Update(0, 2, 100, -3.0f);
    assert(regeneration.Update(1000, 2, 100, -3.0f) == 0);
    assert(regeneration.Update(2000, 0, 100, 5.0f) == 0); // Regeneration cannot resurrect native dead units.
    assert(regeneration.Update(500, 50, 100, 1.0f) == 50); // New/load simulation clock resets the remainder.

    edmw::ReplacedFloatOverlay model;
    edmw::ProportionalHealth proportionalHealth;
    auto toggledHealth = 1;
    for (int index = 0; index < 100; ++index) {
        toggledHealth = proportionalHealth.Resize(toggledHealth, 100, 110);
        toggledHealth = proportionalHealth.Resize(toggledHealth, 110, 100);
    }
    assert(toggledHealth == 1); // Repeated max-health toggles cannot heal or damage through rounding.
    edmw::ProportionalHealth combatHealth;
    assert(combatHealth.Resize(50, 100, 110) == 55);
    const auto reducedHealth = combatHealth.Resize(40, 110, 100);
    assert(combatHealth.Resize(reducedHealth, 100, 110) == 40);
    assert(model.Update(2.0f, 3.0f) == 5.0f);
    assert(model.Update(5.0f, 3.0f) == 5.0f);
    assert(model.Update(4.0f, 3.0f) == 7.0f); // Native growth replaces the baseline.
    assert(model.Update(7.0f, 0.0f) == 4.0f); // Suspend before a native additive modifier.
    assert(model.Update(5.0f, 3.0f) == 8.0f);
    assert(model.Update(8.0f, 0.0f) == 5.0f);
    assert(model.Update(1.0f, -5.0f) == 0.001f);
    edmw::ConsumedIntegerOverlay resource;
    assert(resource.Update(10, 5.0f) == 15);
    assert(resource.Update(12, 5.0f) == 12); // Harvested resources stay consumed.
    assert(resource.Update(12, 0.0f) == 7);
    assert(resource.Update(2, -5.0f) == 0);
    assert(resource.Update(0, -5.0f) == 0);
    assert(resource.Update(0, 0.0f) == 2); // Removal restores only the amount actually subtracted.
    edmw::ConsumedIntegerOverlay consumedBonus;
    assert(consumedBonus.Update(3, 5.0f) == 8);
    assert(consumedBonus.Suspend(2) == 0); // Six resources have been harvested, including some temporary resources.
    assert(consumedBonus.Resume(0, 5.0f) == 2); // Native research/save suspension cannot refill the consumed bonus.
    assert(consumedBonus.Suspend(2) == 0);
    assert(consumedBonus.Resume(3, 5.0f) == 5); // Preserve native resource additions while suspended.

    edmw::UnitStatProjection canonical, projected, unrelated;
    edmw::UnitStatProjection::Put(canonical.prototype, 0x208, int32_t(100));
    edmw::UnitStatProjection::Put(canonical.statistics, 0x11C, 100.0f);
    edmw::UnitStatProjection::Put(canonical.prototype, 0x21C, int32_t(77));
    projected.Refresh(canonical.prototype.data(), canonical.statistics.data());
    unrelated.Refresh(canonical.prototype.data(), canonical.statistics.data());
    assert(projected.Apply(edmw::UnitEffectStat::AttackDamage, 10.0f));
    assert((edmw::UnitStatProjection::Read<0x1C0, float>(projected.statistics, 0x11C) == 110.0f));
    assert((edmw::UnitStatProjection::Read<0x1C0, float>(unrelated.statistics, 0x11C) == 100.0f));
    assert((edmw::UnitStatProjection::Read<0x348, int32_t>(canonical.prototype, 0x208) == 100));
    edmw::UnitStatProjection::Put(canonical.statistics, 0x11C, 120.0f); // A native research upgrade changes the canonical value.
    projected.Refresh(canonical.prototype.data(), canonical.statistics.data());
    projected.Apply(edmw::UnitEffectStat::AttackDamage, 10.0f);
    assert((edmw::UnitStatProjection::Read<0x1C0, float>(projected.statistics, 0x11C) == 130.0f));
    projected.Refresh(canonical.prototype.data(), canonical.statistics.data());
    assert((edmw::UnitStatProjection::Read<0x1C0, float>(projected.statistics, 0x11C) == 120.0f));
    projected.Apply(edmw::UnitEffectStat::GatherRate, 3.0f);
    assert((edmw::UnitStatProjection::Read<0x348, int32_t>(projected.prototype, 0x21C) == 77)); // Unknown seventh interval stays untouched.
    for (const auto offset : { 0x210u, 0x214u, 0x218u, 0x220u, 0x224u, 0x228u })
        assert((edmw::UnitStatProjection::Read<0x348, int32_t>(projected.prototype, offset) == 3));

    assert(edmw::ApplyMovementAmount(0.0f, 3.0f) == 0.0f);
    assert(edmw::ApplyMovementAmount(-1.0f, 3.0f) == -1.0f);
    assert(edmw::ApplyMovementAmount(1.5f, -3.0f) == 0.0f);
    assert(edmw::ApplyMovementAmount(1.5f, 0.125f) == 1.625f);
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
    auto movement = Proximity(3, 0.125f);
    movement.stat = edmw::UnitEffectStat::MovementSpeed;
    edmw::UnitEffectRuntime mixedStats({ Proximity(), movement });
    changed = mixedStats.Evaluate(units, 0);
    assert(changed.size() == 2 && mixedStats.Bonus(1) == 3.0f &&
        mixedStats.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0.125f);
    units[1].x = 100;
    changed = mixedStats.Evaluate(units, 100);
    assert(changed.size() == 2 && changed[0].stat == edmw::UnitEffectStat::LineOfSight &&
        changed[1].stat == edmw::UnitEffectStat::MovementSpeed &&
        mixedStats.Bonus(1) == 0 && mixedStats.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0);
    units[1].x = 5;
    auto speedToggle = movement;
    speedToggle.trigger = edmw::UnitEffectTrigger::Activated;
    speedToggle.sourceObject = speedToggle.targetObject;
    speedToggle.activation = edmw::UnitEffectActivation::Toggle;
    speedToggle.durationMilliseconds = 0;
    edmw::UnitEffectRuntime movementActivation({ Proximity(), speedToggle });
    assert(movementActivation.Activate(speedToggle.id, units[0], 0));
    movementActivation.Evaluate(units, 0);
    assert(movementActivation.Bonus(1) == 3 &&
        movementActivation.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0.125f);
    assert(movementActivation.Activate(speedToggle.id, units[0], 100));
    movementActivation.Evaluate(units, 100);
    assert(movementActivation.Bonus(1) == 3 &&
        movementActivation.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0);
    auto multiOutput = speedToggle;
    multiOutput.outputs = { { edmw::UnitEffectStat::LineOfSight, 3.0f }, { edmw::UnitEffectStat::MovementSpeed, 0.125f } };
    edmw::UnitEffectRuntime coupledOutputs({ multiOutput });
    assert(coupledOutputs.Activate(multiOutput.id, units[0], 0));
    changed = coupledOutputs.Evaluate(units, 0);
    assert(changed.size() == 2 && coupledOutputs.Bonus(1) == 3 &&
        coupledOutputs.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0.125f);
    assert(coupledOutputs.Activate(multiOutput.id, units[0], 100));
    changed = coupledOutputs.Evaluate(units, 100);
    assert(changed.size() == 2 && coupledOutputs.Bonus(1) == 0 &&
        coupledOutputs.Bonus(1, edmw::UnitEffectStat::MovementSpeed) == 0);
    auto looseLimit = Proximity(11, 5.0f), strictLimit = Proximity(12, 2.0f);
    looseLimit.stat = strictLimit.stat = edmw::UnitEffectStat::QueueCapacity;
    edmw::UnitEffectRuntime capacityLimits({ looseLimit, strictLimit });
    capacityLimits.Evaluate(units, 0);
    assert(capacityLimits.Bonus(1, edmw::UnitEffectStat::QueueCapacity) == 2.0f);
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
    puts("UNIT EFFECTS PASS: independent stats, fractional movement, radius boundary, source death, owner/civilization filters, non-stacking providers, independent mods, reused IDs, deterministic order, toggle and simulation-time expiry.");
}
