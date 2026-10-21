#include "UnitEffectInstallRules.h"
#include "EffectRules.h"
#include <cassert>
#include <cstdio>
#include <fstream>
#include <iterator>

namespace {
edmw::UnitEffectRule FailedSessionRule() {
    using namespace edmw;
    UnitEffectRule rule;
    rule.id[0] = 1; rule.familyId = rule.id; rule.civilization = 4;
    rule.sourceObject = 901; rule.targetObject = 878;
    rule.trigger = UnitEffectTrigger::ProximityAbility;
    rule.radiusTiles = 5; rule.activation = UnitEffectActivation::Instant;
    rule.durationMilliseconds = 0; rule.grantFamilyTechnology = 10000;
    rule.delayMilliseconds = 10000; rule.maxAffectedTargets = 5; rule.maxConcurrentTargets = 3;
    rule.resourceCosts = { 0, 0, 50, 0 };
    rule.actions = { { UnitEffectActionKind::Transform, 901, 1, UnitEffectOwner::AffectedPlayer, 1, "Taoist Sorcerer" } };
    return rule;
}
}

int main(int argc, char** argv) {
    using namespace edmw;
    const auto failed = FailedSessionRule();
    assert(ValidInstalledUnitEffectRule(failed));
    auto clicked = failed;
    clicked.trigger = UnitEffectTrigger::Activated; clicked.targetObject = clicked.sourceObject;
    clicked.radiusTiles = 0; clicked.grantFamilyTechnology = 0; clicked.delayMilliseconds = 0;
    clicked.maxAffectedTargets = 0; clicked.maxConcurrentTargets = 0;
    assert(ValidInstalledUnitEffectRule(clicked));
    auto toggle = clicked; toggle.activation = UnitEffectActivation::Toggle;
    toggle.actions.clear(); toggle.outputs = { { UnitEffectStat::Health, 100 } };
    assert(ValidInstalledUnitEffectRule(toggle));
    auto timed = toggle; timed.activation = UnitEffectActivation::Timed; timed.durationMilliseconds = 1000;
    assert(ValidInstalledUnitEffectRule(timed));
    timed.durationMilliseconds = 3600000; assert(ValidInstalledUnitEffectRule(timed));
    auto granted = failed; granted.activation = UnitEffectActivation::Toggle;
    granted.actions.clear(); granted.outputs = { { UnitEffectStat::LineOfSight, 5 } };
    assert(ValidInstalledUnitEffectRule(granted));
    auto aura = granted; aura.trigger = UnitEffectTrigger::Proximity; aura.grantFamilyTechnology = 0;
    aura.resourceCosts = {}; assert(ValidInstalledUnitEffectRule(aura));
    auto invalid = failed; invalid.outputs = { { UnitEffectStat::Health, 100 } }; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.actions.clear(); assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.activation = UnitEffectActivation::Timed; invalid.durationMilliseconds = 1000;
    assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.activation = static_cast<UnitEffectActivation>(99); assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.trigger = static_cast<UnitEffectTrigger>(99); assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.durationMilliseconds = 1; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.radiusTiles = 0; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.radiusTiles = 65; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.grantFamilyTechnology = 9999; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.grantFamilyTechnology = 15000; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = clicked; invalid.targetObject++; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = clicked; invalid.delayMilliseconds = 1; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = clicked; invalid.maxAffectedTargets = 1; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = clicked; invalid.maxConcurrentTargets = 1; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = clicked; invalid.grantFamilyTechnology = 10000; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = aura; invalid.activation = UnitEffectActivation::Instant; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = aura; invalid.resourceCosts[2] = 1; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = timed; invalid.durationMilliseconds = 999; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = timed; invalid.durationMilliseconds = 3600001; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.delayMilliseconds = 3600001; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.maxAffectedTargets = 100001; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.maxConcurrentTargets = 100001; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.resourceCosts[2] = 100001; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.civilization = 3; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.civilization = 13; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.sourceObject = 100000; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.targetObject = 100000; assert(!ValidInstalledUnitEffectRule(invalid));
    invalid = failed; invalid.id = {}; assert(!ValidInstalledUnitEffectRule(invalid));
    for (int index = 1; index < argc; ++index) {
        std::ifstream file(argv[index], std::ios::binary);
        assert(file);
        const std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        std::vector<EffectRule> research;
        std::vector<UnitEffectRule> units;
        std::vector<NativeAbilityBinding> abilities;
        assert(ReadRuntimeRules(bytes, research, units, abilities));
        for (const auto& rule : units) assert(ValidInstalledUnitEffectRule(rule));
        printf("CONFIG INSTALL RULES PASS: %s (%u unit rules)\n", argv[index], static_cast<uint32_t>(units.size()));
    }
    puts("UNIT INSTALL RULES PASS: failed-session instant training, ordinary instant actions, legacy modes and invalid rule matrices.");
}
