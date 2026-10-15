#include "EffectRules.h"
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <limits>
void RequireAt(bool value, int line) { if (!value) { fprintf(stderr, "Effect rule test failed at line %d\n", line); exit(1); } }
#define Require(value) RequireAt((value), __LINE__)
void Write32(std::vector<uint8_t>& bytes, size_t offset, uint32_t value) { memcpy(bytes.data() + offset, &value, 4); }
void WriteFloat(std::vector<uint8_t>& bytes, size_t offset, float value) { memcpy(bytes.data() + offset, &value, 4); }
std::vector<uint8_t> Version3(bool timed = false, bool proximity = false) {
    const std::string caption = proximity ? "" : "Scout vision";
    std::vector<uint8_t> bytes(16 + 56 + caption.size());
    memcpy(bytes.data(), "EDMWEF03", 8);
    Write32(bytes, 12, 1);
    bytes[16] = 1;
    Write32(bytes, 32, 12);
    Write32(bytes, 36, 749);
    Write32(bytes, 40, proximity ? 777 : 749);
    Write32(bytes, 44, 3);
    Write32(bytes, 48, proximity ? 5 : 0);
    Write32(bytes, 52, proximity ? 1 : 2);
    Write32(bytes, 56, timed ? 2 : 1);
    Write32(bytes, 60, timed ? 10000 : 0);
    Write32(bytes, 64, proximity ? 0 : 10000);
    Write32(bytes, 68, static_cast<uint32_t>(caption.size()));
    if (!caption.empty()) memcpy(bytes.data() + 72, caption.data(), caption.size());
    return bytes;
}
std::vector<uint8_t> Version4(bool proximity = false) {
    auto bytes = Version3(false, proximity);
    memcpy(bytes.data(), "EDMWEF04", 8);
    bytes.insert(bytes.begin() + 72, 4, 0);
    WriteFloat(bytes, 44, 3.0f);
    return bytes;
}
std::vector<uint8_t> Version5(bool proximity = false) {
    auto bytes = Version4(proximity);
    memcpy(bytes.data(), "EDMWEF05", 8);
    Write32(bytes, 44, 0);
    Write32(bytes, 72, 2);
    bytes.insert(bytes.begin() + 76, 16, 0);
    Write32(bytes, 76, 0); WriteFloat(bytes, 80, 3.0f);
    Write32(bytes, 84, 7); WriteFloat(bytes, 88, 0.125f);
    return bytes;
}
void VerifyAbilityGroupTargets() {
    edmw::NativeAbilityBinding binding;
    binding.technology = 10000; binding.civilization = 4; binding.sourceObject = 878;
    const edmw::NativeAbilityNodeIdentity identity{ 10000, 3, 51, 0 };
    Require(edmw::NativeAbilityGroupVisibilityByte(&binding, 10000, 3, identity, 0xA1) == 0xA3);
    Require(edmw::NativeAbilityGroupVisibilityByte(&binding, 10000, 3, identity, 2) == 2);
    Require(edmw::NativeAbilityGroupVisibilityByte(&binding, 10000, 4, identity, 0xA1) == 0xA1);
    Require(edmw::NativeAbilityGroupVisibilityByte(nullptr, 10000, 3, identity, 0xA1) == 0xA1);
    const auto groupKey = edmw::NativeAbilityGroupActionKey(&binding, 10000, 3, identity, { 51, 0 }, true);
    Require(groupKey.command == 51 && groupKey.parameter == 10000 && identity.event == 0);
    auto secondBinding = binding; secondBinding.technology = 10001;
    const auto secondKey = edmw::NativeAbilityGroupActionKey(&secondBinding, 10001, 3, { 10001, 3, 51, 0 }, { 51, 0 }, true);
    Require(secondKey.command == 51 && secondKey.parameter == 10001 && secondKey.parameter != groupKey.parameter);
    for (const auto unchanged : {
        edmw::NativeAbilityGroupActionKey(&binding, 10000, 3, identity, { 51, 0 }, false),
        edmw::NativeAbilityGroupActionKey(&binding, 10000, 4, identity, { 51, 0 }, true),
        edmw::NativeAbilityGroupActionKey(nullptr, 10000, 3, identity, { 51, 0 }, true),
        edmw::NativeAbilityGroupActionKey(&binding, 10001, 3, identity, { 51, 0 }, true),
        edmw::NativeAbilityGroupActionKey(&binding, 10000, 3, { 10000, 3, 51, 1 }, { 51, 0 }, true) })
        Require(unchanged.command == 51 && unchanged.parameter == 0);
    const auto stockKey = edmw::NativeAbilityGroupActionKey(&binding, 10000, 3, identity, { 36, 0 }, true);
    const auto eventKey = edmw::NativeAbilityGroupActionKey(&binding, 10000, 3, identity, { 51, 9 }, true);
    Require(stockKey.command == 36 && stockKey.parameter == 0 && eventKey.command == 51 && eventKey.parameter == 9);
    const edmw::UnitEffectSnapshot anchor{ 102, 1, 4, 878, 0, 0, true, 7 };
    const edmw::UnitEffectSnapshot member{ 101, 1, 4, 878, 0, 0, true, 8 };
    auto foreign = member; foreign.id = 103; foreign.player = 2;
    auto dead = member; dead.id = 104; dead.alive = false;
    auto otherType = member; otherType.id = 105; otherType.object = 879;
    auto otherCivilization = member; otherCivilization.id = 106; otherCivilization.civilization = 5;
    auto invalid = member; invalid.id = UINT32_MAX;
    const std::vector<uint32_t> both{ 101, 102 }, anchorOnly{ 102 };
    Require(edmw::NativeAbilityActivationTargets(binding, anchor,
        { anchor, foreign, member, dead, member, otherType, otherCivilization, invalid, anchor }) == both);
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, { member, anchor }) == both);
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, { anchor }) == anchorOnly);
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, {}) == anchorOnly);
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, { member }) == anchorOnly); // A later selection cannot expand an old command.
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, { foreign, dead, otherType }) == anchorOnly);
    auto invalidAnchor = anchor; invalidAnchor.alive = false;
    Require(edmw::NativeAbilityActivationTargets(binding, invalidAnchor, { anchor, member }).empty());
    invalidAnchor = anchor; invalidAnchor.civilization = 5;
    Require(edmw::NativeAbilityActivationTargets(binding, invalidAnchor, { anchor, member }).empty());
    invalidAnchor = anchor; invalidAnchor.object = 879;
    Require(edmw::NativeAbilityActivationTargets(binding, invalidAnchor, { anchor, member }).empty());
    binding.enabled = false;
    Require(edmw::NativeAbilityActivationTargets(binding, anchor, { anchor, member }).empty());
}
void VerifyAbilityPlacement() {
    const std::vector<int32_t> expected{ 13, 27, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41 };
    for (int32_t slot = -1; slot <= 42; ++slot)
        Require(edmw::IsNativeAbilityHudSlot(slot) == (std::find(expected.begin(), expected.end(), slot) != expected.end()));
    for (const auto commandSlot : { 0, 1, 2, 14, 15, 16, 28, 29, 30 })
        Require(!edmw::IsNativeAbilityHudSlot(commandSlot));
    Require(edmw::ResolveNativeAbilityHudSlot(-1, 41) == 41);
    Require(edmw::ResolveNativeAbilityHudSlot(0, 13) == 13);
    Require(edmw::ResolveNativeAbilityHudSlot(27, 41) == 27);
    Require(edmw::ResolveNativeAbilityHudSlot(42, 13) == 41);
    Require(edmw::ResolveNativeAbilityHudSlot(10000, 41) == -1);
    Require(edmw::ResolveNativeAbilityHudSlot(-1, -1) == -1);
    const auto placements = edmw::AllocateNativeAbilityHudSlots({ { 10002, 41 }, { 10001, 13 }, { 10000, 1 } }, {});
    Require(placements.size() == 3 && placements[0].technology == 10000 && placements[0].slot == 13 &&
        placements[1].technology == 10001 && placements[1].slot == 27 &&
        placements[2].technology == 10002 && placements[2].slot == 41);
    const auto occupied = edmw::AllocateNativeAbilityHudSlots({ { 10001, 13 }, { 10000, 1 } }, { 13, 27, 10000, -1 });
    Require(occupied[0].slot == 31 && occupied[1].slot == 32);
    std::vector<edmw::NativeAbilityPlacement> many;
    for (uint32_t technology = 10013; technology >= 10000; --technology) many.push_back({ technology, 1 });
    const auto exhausted = edmw::AllocateNativeAbilityHudSlots(many, {});
    Require(exhausted.size() == 14);
    for (size_t index = 0; index < expected.size(); ++index)
        Require(exhausted[index].technology == 10000 + index && exhausted[index].slot == expected[index]);
    Require(exhausted.back().technology == 10013 && exhausted.back().slot == edmw::NativeAbilityHiddenHudSlot &&
        edmw::ResolveNativeAbilityHudSlot(exhausted.back().slot, 41) == -1);
    const auto noSpace = edmw::AllocateNativeAbilityHudSlots({ { 10000, 13 } }, expected);
    Require(noSpace[0].slot == edmw::NativeAbilityHiddenHudSlot);
    size_t count = 999;
    Require(edmw::NativeAbilityVectorCount(0, 0, 0, 4, 4096, count) && count == 0);
    Require(edmw::NativeAbilityVectorCount(0x1000, 0x1010, 0x1020, 8, 32, count) && count == 2);
    for (const auto bounds : { std::array<uintptr_t, 3>{ 0, 0x1000, 0x1000 },
        std::array<uintptr_t, 3>{ 0x1010, 0x1000, 0x1020 },
        std::array<uintptr_t, 3>{ 0x1000, 0x1010, 0x1008 },
        std::array<uintptr_t, 3>{ 0x1000, 0x1009, 0x1010 },
        std::array<uintptr_t, 3>{ 0x1000, 0x1008, 0x1011 },
        std::array<uintptr_t, 3>{ 0x1000, 0x1108, 0x1110 } })
        Require(!edmw::NativeAbilityVectorCount(bounds[0], bounds[1], bounds[2], 8, 32, count));
    Require(!edmw::NativeAbilityVectorCount(0x1000, 0x1000, 0x1000, 0, 32, count));
}
int main(int argc, char** argv) {
    VerifyAbilityGroupTargets();
    VerifyAbilityPlacement();
    // Network identities do not identify positions in the world player array.
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 0, 1, 3) == 1);
    Require(edmw::NativeAbilityCommandPlayerIndex(1, 1, 2, 3) == 2); // Last player is included; count includes Gaia.
    Require(edmw::NativeAbilityCommandPlayerIndex(42, 42, 7, 9) == 7);
    Require(edmw::NativeAbilityCommandPlayerIndex(254, 254, 1, 3) == 1);
    Require(edmw::NativeAbilityCommandPlayerIndex(255, 255, 1, 3) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(256, 256, 1, 3) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 1, 1, 3) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(1, 0, 1, 3) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 0, 3, 3) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 0, 16, 16) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 0, 1, 0) == UINT32_MAX);
    Require(edmw::NativeAbilityCommandPlayerIndex(0, 0, 1, 17) == UINT32_MAX);
    edmw::NativeAbilityBinding registered;
    registered.technology = 10000;
    registered.civilization = 4;
    const edmw::NativeAbilityNodeIdentity identity{ 10000, 3, 51, 0 };
    Require(edmw::NativeAbilityMinimumAge(&registered, 10000, 3, identity, 9) == 0);
    Require(edmw::NativeAbilityMinimumAge(&registered, 10000, 4, identity, 9) == 9);
    Require(edmw::NativeAbilityMinimumAge(&registered, 10000, 0, identity, 9) == 9);
    Require(edmw::NativeAbilityMinimumAge(nullptr, 10000, 3, identity, 9) == 9);
    Require(edmw::NativeAbilityMinimumAge(&registered, 10001, 3, identity, 9) == 9);
    for (const auto invalid : { edmw::NativeAbilityNodeIdentity{ 10001, 3, 51, 0 },
        edmw::NativeAbilityNodeIdentity{ 10000, 0, 51, 0 },
        edmw::NativeAbilityNodeIdentity{ 10000, 3, 3, 0 },
        edmw::NativeAbilityNodeIdentity{ 10000, 3, 51, 1 } })
        Require(edmw::NativeAbilityMinimumAge(&registered, 10000, 3, invalid, 9) == 9);
    registered.enabled = false;
    Require(edmw::NativeAbilityMinimumAge(&registered, 10000, 3, identity, 9) == 0); // Disabled bindings retain visible, inert buttons.
    for (uint32_t technology = 10000; technology <= 14000; technology += 1000) {
        registered.technology = technology;
        Require(edmw::NativeAbilityMinimumAge(&registered, technology, 3, { technology, 3, 51, 0 },
            static_cast<int32_t>(technology / 1000 - 1)) == 0);
    }
    registered.technology = 1700;
    Require(edmw::NativeAbilityMinimumAge(&registered, 1700, 3, { 1700, 3, 51, 0 }, 1) == 1);
    std::vector<uint8_t> bytes(28);
    memcpy(bytes.data(), "EDMWEF01", 8);
    bytes[8] = 1;
    edmw::EffectRule rule{9404, 10, 4, 123};
    memcpy(bytes.data() + 12, &rule, sizeof(rule));
    std::vector<edmw::EffectRule> rules;
    Require(edmw::ReadEffectRules(bytes, rules) && rules.size() == 1);
    Require(edmw::Matches(rules[0], 9404, 10, 0, false));
    Require(!edmw::Matches(rules[0], 9404, 10, 0, true));
    Require(!edmw::Matches(rules[0], 9404, 11, 0, false));
    Require(!edmw::Matches(rules[0], 9405, 10, 0, false));
    Require(!edmw::Matches(rules[0], 9404, 10, 3, false));
    bytes[8] = 255; Require(!edmw::ReadEffectRules(bytes, rules) && rules.empty());
    bytes[8] = 1; bytes[20] = 3; Require(!edmw::ReadEffectRules(bytes, rules));
    bytes[20] = 4; bytes.pop_back(); Require(!edmw::ReadEffectRules(bytes, rules));
    std::vector<edmw::UnitEffectRule> units;
    bytes.assign(52, 0);
    memcpy(bytes.data(), "EDMWEF02", 8); bytes[12] = 1;
    bytes[16] = 1; bytes[32] = 12;
    const uint32_t source = 749, target = 777, radius = 5;
    const int32_t amount = 3;
    memcpy(bytes.data() + 36, &source, 4); memcpy(bytes.data() + 40, &target, 4);
    memcpy(bytes.data() + 44, &amount, 4); memcpy(bytes.data() + 48, &radius, 4);
    Require(edmw::ReadRuntimeRules(bytes, rules, units) && rules.empty() && units.size() == 1);
    Require(units[0].sourceObject == 749 && units[0].targetObject == 777 && units[0].amount == 3);
    bytes[48] = 65; Require(!edmw::ReadRuntimeRules(bytes, rules, units) && units.empty());
    bytes[48] = 5; bytes[16] = 0; Require(!edmw::ReadRuntimeRules(bytes, rules, units));
    bytes[16] = 1; bytes.push_back(0); Require(!edmw::ReadRuntimeRules(bytes, rules, units));
    std::vector<edmw::NativeAbilityBinding> abilities;
    bytes = Version3();
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.size() == 1 && abilities.size() == 1);
    Require(units[0].trigger == edmw::UnitEffectTrigger::Activated && units[0].activation == edmw::UnitEffectActivation::Toggle &&
        units[0].sourceObject == units[0].targetObject && units[0].durationMilliseconds == 0);
    Require(abilities[0].technology == 10000 && abilities[0].caption == "Scout vision" &&
        abilities[0].tooltip == "Scout vision: +3 line of sight. Click again to turn off.");
    const auto toggleBytes = bytes;
    for (size_t length = 0; length < toggleBytes.size(); ++length) {
        bytes.assign(toggleBytes.begin(), toggleBytes.begin() + length);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.empty() && abilities.empty());
    }
    bytes = toggleBytes; bytes.push_back(0);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = Version3(true);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units[0].activation == edmw::UnitEffectActivation::Timed &&
        units[0].durationMilliseconds == 10000 && abilities[0].tooltip == "Scout vision: +3 line of sight. Lasts 10 seconds.");
    Write32(bytes, 60, 1500);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) &&
        abilities[0].tooltip == "Scout vision: +3 line of sight. Lasts 1.5 seconds.");
    Write32(bytes, 60, 1000);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) &&
        abilities[0].tooltip == "Scout vision: +3 line of sight. Lasts 1 second.");
    Write32(bytes, 60, 3600000); Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Write32(bytes, 60, 999); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Write32(bytes, 60, 3600001); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 60, 1); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 56, 3); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 52, 3); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 40, 777); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 48, 5); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 64, 9999); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 64, 15000); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 68, 0); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; Write32(bytes, 68, 41); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; bytes[72] = 0; Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; bytes[72] = 128; Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes; std::fill(bytes.begin() + 72, bytes.end(), uint8_t(' '));
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = Version3(false, true);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.empty());
    Write32(bytes, 64, 10000); Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = toggleBytes;
    bytes.insert(bytes.end(), toggleBytes.begin() + 16, toggleBytes.end());
    Write32(bytes, 12, 2);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Duplicate effect identity.
    bytes[toggleBytes.size()] = 2;
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Distinct effects cannot share a command ID.
    Write32(bytes, toggleBytes.size() + 48, 10001);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && abilities.size() == 2);
    bytes = toggleBytes;
    bytes.insert(bytes.begin() + 16, 16, 0);
    Write32(bytes, 8, 1);
    Write32(bytes, 16, 10000); Write32(bytes, 20, 11); Write32(bytes, 24, 4); Write32(bytes, 28, 123);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Native command IDs are global, across civilizations.
    Write32(bytes, 16, 9404);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.size() == 1 && units.size() == 1 && abilities.size() == 1);
    bytes = toggleBytes;
    Write32(bytes, 52, 0x80000002u);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.empty() && abilities.size() == 1 &&
        !abilities[0].enabled && abilities[0].caption == "Scout vision" && abilities[0].tooltip.find("Disabled:") == 0);
    bytes.insert(bytes.end(), toggleBytes.begin() + 16, toggleBytes.end());
    Write32(bytes, 12, 2);
    Write32(bytes, toggleBytes.size() + 48, 10001);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Disabled identities remain reserved.
    bytes[toggleBytes.size()] = 2;
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.size() == 2);
    bytes = Version3(false, true);
    Write32(bytes, 52, 0x80000001u);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Only command bindings can use the disabled flag.
    bytes = Version4();
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units[0].amount == 3.0f &&
        units[0].stat == edmw::UnitEffectStat::LineOfSight);
    bytes = Version4(true);
    Write32(bytes, 72, 7);
    WriteFloat(bytes, 44, 0.125f);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units[0].amount == 0.125f &&
        units[0].stat == edmw::UnitEffectStat::MovementSpeed);
    bytes = Version4();
    Write32(bytes, 72, 7);
    WriteFloat(bytes, 44, -0.125f);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) &&
        abilities[0].tooltip.find("-0.125 movement speed") != std::string::npos);
    const auto speedBytes = bytes;
    for (const auto invalidAmount : { 0.0f, 1001.0f, (std::numeric_limits<float>::infinity)(),
        (std::numeric_limits<float>::quiet_NaN)() }) {
        bytes = speedBytes;
        WriteFloat(bytes, 44, invalidAmount);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.empty() && abilities.empty());
    }
    for (const auto stat : { 51u, UINT32_MAX }) {
        bytes = speedBytes;
        Write32(bytes, 72, stat);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // Unverified/native-unknown outputs fail closed.
    }
    bytes = Version4();
    WriteFloat(bytes, 44, 32.125f);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    for (size_t length = 0; length < speedBytes.size(); ++length) {
        bytes.assign(speedBytes.begin(), speedBytes.begin() + length);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.empty() && abilities.empty());
    }
    bytes = Version5();
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.size() == 1 &&
        units[0].outputs.size() == 2 && units[0].outputs[0].stat == edmw::UnitEffectStat::LineOfSight &&
        units[0].outputs[1].stat == edmw::UnitEffectStat::MovementSpeed && units[0].outputs[1].amount == 0.125f &&
        abilities[0].tooltip.find("+3 line of sight; +0.125 movement speed") != std::string::npos);
    const auto multipleBytes = bytes;
    for (size_t length = 0; length < multipleBytes.size(); ++length) {
        bytes.assign(multipleBytes.begin(), multipleBytes.begin() + length);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.empty() && abilities.empty());
    }
    bytes = multipleBytes; Write32(bytes, 44, 1);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = multipleBytes; Write32(bytes, 72, 0);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = multipleBytes; Write32(bytes, 72, 52);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    bytes = multipleBytes; Write32(bytes, 84, 0);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities)); // No duplicate stat within one lifecycle.
    bytes = multipleBytes; WriteFloat(bytes, 88, (std::numeric_limits<float>::quiet_NaN)());
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    Require(edmw::ValidUnitEffectAmount(edmw::UnitEffectStat::ProductionCoefficient, 10000.0f) &&
        edmw::ValidUnitEffectAmount(edmw::UnitEffectStat::ProductionCoefficient, -10000.0f) &&
        !edmw::ValidUnitEffectAmount(edmw::UnitEffectStat::ProductionCoefficient, 10001.0f) &&
        !edmw::ValidUnitEffectAmount(edmw::UnitEffectStat::ProductionCoefficient, 1.5f));
    for (int argument = 1; argument < argc; ++argument) {
        std::ifstream file(argv[argument], std::ios::binary);
        Require(file.good());
        const std::vector<uint8_t> fixture((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        Require(edmw::ReadRuntimeRules(fixture, rules, units, abilities));
        const bool disabled = !abilities.empty() && std::all_of(abilities.begin(), abilities.end(), [](const auto& binding) { return !binding.enabled; });
        const bool movementOnly = rules.empty() && abilities.empty() && units.size() == 1 &&
            units[0].stat == edmw::UnitEffectStat::MovementSpeed;
        const bool allStats = rules.empty() && abilities.size() <= 1 && units.size() == 1 &&
            units[0].outputs.size() == 51;
        const bool multiTargetAura = std::string(argv[argument]).find("multi-target-aura.edmwe") != std::string::npos;
        if (multiTargetAura) {
            Require(fixture.size() == 168 && rules.empty() && abilities.empty() && units.size() == 2);
            std::set<uint32_t> targets;
            for (const auto& effect : units) {
                Require(effect.civilization == 12 && effect.sourceObject == 749 && effect.radiusTiles == 5 &&
                    effect.trigger == edmw::UnitEffectTrigger::Proximity && effect.activation == edmw::UnitEffectActivation::Toggle &&
                    effect.durationMilliseconds == 0 && effect.outputs.size() == 2 &&
                    effect.outputs[0].stat == edmw::UnitEffectStat::Health && effect.outputs[0].amount == 1000 &&
                    effect.outputs[1].stat == edmw::UnitEffectStat::LineOfSight && effect.outputs[1].amount == 30);
                targets.insert(effect.targetObject);
            }
            Require((targets == std::set<uint32_t>{777, 878} && units[0].id != units[1].id));
        } else if (allStats) {
            std::set<uint32_t> statIds;
            for (const auto& output : units[0].outputs) {
                Require(edmw::ValidUnitEffectAmount(output.stat, output.amount));
                statIds.insert(static_cast<uint32_t>(output.stat));
            }
            Require(statIds.size() == 51 && *statIds.begin() == 0 && *statIds.rbegin() == 50);
            if (!abilities.empty()) {
                const auto& effect = units[0];
                const auto& binding = abilities[0];
                Require(binding.enabled && binding.effect == effect.id && binding.technology >= 10000 && binding.technology < 15000 &&
                    effect.trigger == edmw::UnitEffectTrigger::Activated && effect.activation == edmw::UnitEffectActivation::Timed &&
                    effect.durationMilliseconds == 15000 && effect.radiusTiles == 0 && effect.sourceObject == effect.targetObject);
                Require(binding.tooltip.size() > 512 && binding.tooltip.size() < 8192);
                for (const auto& output : effect.outputs)
                    Require(binding.tooltip.find(edmw::UnitEffectStatName(output.stat)) != std::string::npos);
                Require(binding.tooltip.find("maximum 5 queue capacity") != std::string::npos);
            }
        } else if (movementOnly) {
            const auto& movementRule = units[0];
            Require(movementRule.amount == 0.125f && movementRule.radiusTiles == 5 &&
                movementRule.sourceObject == 749 && movementRule.targetObject == 777 && movementRule.civilization == 12 &&
                movementRule.trigger == edmw::UnitEffectTrigger::Proximity);
            if (movementRule.outputs.size() > 1) Require(movementRule.outputs.size() == 2 &&
                movementRule.outputs[1].stat == edmw::UnitEffectStat::LineOfSight && movementRule.outputs[1].amount == 2);
        } else if (disabled) Require(rules.empty() && units.empty() && abilities.size() == 2);
        else {
            Require(rules.size() == 1 && rules[0].resource == 4 && rules[0].amount == 123 && rules[0].civilization == 11);
            Require(units.size() == 1 + abilities.size());
            const auto aura = std::find_if(units.begin(), units.end(), [](const auto& item) { return item.trigger == edmw::UnitEffectTrigger::Proximity; });
            Require(aura != units.end() && aura->sourceObject == 749 && aura->targetObject == 777 && aura->civilization == 12 &&
                aura->radiusTiles == 5 && aura->amount == 3);
        }
        if (!abilities.empty() && !allStats) {
            Require(abilities.size() == 2);
            std::set<uint32_t> technologyIds;
            std::set<std::string> captions;
            for (const auto& binding : abilities) {
                Require(binding.civilization == 12 && binding.sourceObject == 749 &&
                    (binding.technology == 10000 || binding.technology == 10001));
                technologyIds.insert(binding.technology);
                captions.insert(binding.caption);
                Require(binding.caption == "Recon toggle" || binding.caption == "Recon burst");
                if (!disabled) {
                    const auto effect = std::find_if(units.begin(), units.end(), [&](const auto& item) { return item.id == binding.effect; });
                    Require(binding.enabled && effect != units.end() && effect->trigger == edmw::UnitEffectTrigger::Activated &&
                        effect->sourceObject == 749 && effect->targetObject == 749 && effect->amount == 3 && !effect->radiusTiles);
                    Require(binding.caption == "Recon toggle" ?
                        effect->activation == edmw::UnitEffectActivation::Toggle && effect->durationMilliseconds == 0 :
                        effect->activation == edmw::UnitEffectActivation::Timed && effect->durationMilliseconds == 15000);
                } else Require(binding.tooltip.find("Disabled:") == 0);
            }
            Require(technologyIds.size() == 2 && captions.size() == 2);
        }
        printf("Managed-to-native fixture passed: %s\n", argv[argument]);
    }
    puts("Native formats 01/02/03/04/05, multiple outputs per lifecycle, fractional stat outputs, unsupported-stat rejection, activation bounds, duplicate IDs, malformed payloads and research/load filtering passed.");
}
