#pragma once
#include "UnitEffectRuntime.h"
#include <string>

namespace edmw {
struct NativeAbilityBinding {
    UnitEffectId effect{};
    uint32_t technology = 0;
    uint32_t civilization = 0;
    uint32_t sourceObject = 0;
    std::string caption;
    std::string tooltip;
    bool enabled = true;
};

// Installs native HUD localization and synchronized command bindings only.
// The caller must first install matching enabled activated UnitEffectRules. Disabled
// bindings retain their caption/help and consume activation without applying it. Activated
// state is session-only until the save integration has been implemented.
bool InstallNativeAbilities(const std::vector<NativeAbilityBinding>& bindings);
}
