#pragma once
#include "UnitEffectRuntime.h"
#include <string>

// All hooks are checked against the supported executable before being installed.
namespace edmw {
bool InstallUnitEffects(const std::vector<UnitEffectRule>& rules);
bool RegisterGrantedUnitAbility(const UnitEffectId& effect, uint32_t technology, const std::string& caption);
std::string DescribeUnitProximityEffects(uint32_t unitId);
// Invoke only from a validated native simulation command, never a UI callback.
// Activated state belongs to the current session; save restoration is unverified.
bool ActivateUnitEffect(const UnitEffectId& effect, void* actor, uint32_t expectedPlayer);
bool ActivateUnitEffects(const UnitEffectId& effect, const std::vector<void*>& actors, uint32_t expectedPlayer);
bool ActivateUnitEffectGroup(const std::vector<UnitEffectId>& effects, const std::vector<void*>& actors, uint32_t expectedPlayer,
    const std::array<uint32_t, 4>& resourceCosts = {});
float UnitEffectBonus(void* actor, UnitEffectStat stat);
}
