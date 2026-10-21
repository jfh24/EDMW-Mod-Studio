#include "UnitEffectModsPanel.h"
#include <cassert>

int main() {
    assert(!edmw::HasUnitEffectModDescription(""));
    assert(!edmw::HasUnitEffectModDescription(" \n\t"));
    assert(!edmw::HasUnitEffectModDescription("No nearby effects."));
    assert(edmw::UnitEffectModsTooltipAddition(true, "").empty());
    assert(edmw::UnitEffectModsTooltipAddition(false, "No nearby effects.").empty());
    const std::string description = "Reconnaissance corps\n+3 line of sight.\nWithin 5 tiles of a source.";
    assert(edmw::UnitEffectModsTooltipAddition(false, description) == description);
    assert(edmw::UnitEffectModsTooltipAddition(true, description) == "\n\n" + description);
    assert(edmw::UnitEffectModsTooltipAddition(false, description).find("Mods") == std::string::npos);
    const std::string namedUnicode = "\xE6\x94\xAF\xE6\x8F\xB4\n+50 health.";
    assert(edmw::UnitEffectModsTooltipAddition(false, namedUnicode) == namedUnicode);
    assert(edmw::IsUnitEffectModsTooltipTarget(10, 10, 20, 30, 30, 40, 40));
    assert(edmw::IsUnitEffectModsTooltipTarget(20, 10, 20, 30, 30, 40, 40));
    assert(!edmw::IsUnitEffectModsTooltipTarget(50, 10, 20, 30, 30, 40, 40));
    assert(!edmw::IsUnitEffectModsTooltipTarget(10, 10, 20, 30, 31, 40, 40));
    assert(!edmw::IsUnitEffectModsTooltipTarget(10, 10, 20, 30, 30, 41, 40));
    assert(!edmw::IsUnitEffectModsTooltipTarget(10, 10, 20, 30, 30, 0, 0));
    assert(!edmw::IsUnitEffectModsTooltipTarget(0, 0, 0, 30, 30, 40, 40));
    assert(edmw::IsUnitEffectModsSelectionCurrent(true, 0, 0));
    assert(edmw::IsUnitEffectModsSelectionCurrent(true, 42, 42));
    assert(!edmw::IsUnitEffectModsSelectionCurrent(false, 42, 42)); // Native leaves +0x30 stale on unselection.
    assert(!edmw::IsUnitEffectModsSelectionCurrent(true, 43, 42));
    assert(!edmw::IsUnitEffectModsSelectionCurrent(true, UINT32_MAX, UINT32_MAX));
}
