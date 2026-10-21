#pragma once
#include "UnitEffectRuntime.h"

namespace edmw {
// Shared, platform-independent installation contract. Keep permanent actions'
// Instant activation valid in both ordinary and proximity-granted abilities.
inline bool ValidInstalledUnitEffectRule(const UnitEffectRule& rule) {
    if (rule.civilization < 4 || rule.civilization > 12 || rule.sourceObject >= 100000 ||
        rule.targetObject >= 100000 || !ValidUnitEffectOutputs(rule) ||
        std::all_of(rule.id.begin(), rule.id.end(), [](uint8_t value) { return value == 0; }) ||
        rule.delayMilliseconds > 3600000 || rule.maxAffectedTargets > 100000 ||
        rule.maxConcurrentTargets > 100000 ||
        std::any_of(rule.resourceCosts.begin(), rule.resourceCosts.end(), [](uint32_t value) { return value > 100000; }))
        return false;
    if (rule.trigger != UnitEffectTrigger::ProximityAbility && rule.grantFamilyTechnology) return false;
    if (rule.trigger == UnitEffectTrigger::Proximity) {
        return rule.radiusTiles >= 1 && rule.radiusTiles <= 64 &&
            rule.activation == UnitEffectActivation::Toggle && !rule.durationMilliseconds &&
            std::all_of(rule.resourceCosts.begin(), rule.resourceCosts.end(), [](uint32_t value) { return value == 0; });
    }
    if (rule.trigger == UnitEffectTrigger::Activated) {
        if (rule.radiusTiles || rule.sourceObject != rule.targetObject || rule.delayMilliseconds ||
            rule.maxAffectedTargets || rule.maxConcurrentTargets) return false;
    } else if (rule.trigger == UnitEffectTrigger::ProximityAbility) {
        if (!rule.radiusTiles || rule.radiusTiles > 64 || rule.activation == UnitEffectActivation::Timed ||
            rule.grantFamilyTechnology < 10000 || rule.grantFamilyTechnology > 14999) return false;
    } else return false;
    if (rule.activation == UnitEffectActivation::Toggle || rule.activation == UnitEffectActivation::Instant)
        return !rule.durationMilliseconds;
    return rule.activation == UnitEffectActivation::Timed && rule.durationMilliseconds >= 1000 &&
        rule.durationMilliseconds <= 3600000;
}
}
