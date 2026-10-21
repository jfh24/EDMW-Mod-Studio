#pragma once
#include <algorithm>
#include <cstdint>
#include <string>

namespace edmw {
inline bool HasUnitEffectModDescription(const std::string& text) {
    return !text.empty() && text != "No nearby effects." &&
        std::any_of(text.begin(), text.end(), [](unsigned char character) { return character > ' '; });
}
inline std::string UnitEffectModsTooltipAddition(bool nativeHelp, const std::string& description) {
    return HasUnitEffectModDescription(description) ? (nativeHelp ? "\n\n" : "") + description : std::string{};
}
inline bool IsUnitEffectModsTooltipTarget(uintptr_t control, uintptr_t name, uintptr_t portrait,
    uintptr_t form, uintptr_t controlForm, uintptr_t destination, uintptr_t formTooltip) {
    return control && form && controlForm == form && destination && destination == formTooltip &&
        (control == name || control == portrait);
}
inline bool IsUnitEffectModsSelectionCurrent(bool hasDisplayedUnit, uint32_t displayedUnit, uint32_t capturedUnit) {
    return hasDisplayedUnit && capturedUnit != UINT32_MAX && displayedUnit == capturedUnit;
}
bool InstallUnitEffectModsPanel();
// Publishes descriptions only. Native tooltip rendering stays on the game's UI
// thread and uses the existing selected-unit information controls and popup.
void UpdateUnitEffectModsPanel();
}
