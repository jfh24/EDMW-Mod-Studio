#pragma once
#include "UnitEffectRuntime.h"

// All hooks are checked against the supported executable before being installed.
namespace edmw {
bool InstallUnitEffects(const std::vector<UnitEffectRule>& rules);
// Invoke only from a validated native simulation command, never a UI callback.
// Activated state belongs to the current session; save restoration is unverified.
bool ActivateUnitEffect(const UnitEffectId& effect, void* actor, uint32_t expectedPlayer);
bool ActivateUnitEffects(const UnitEffectId& effect, const std::vector<void*>& actors, uint32_t expectedPlayer);
float UnitEffectBonus(void* actor, UnitEffectStat stat);
}
