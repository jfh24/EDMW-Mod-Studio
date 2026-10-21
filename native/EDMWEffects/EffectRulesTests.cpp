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
std::vector<uint8_t> Version7(const std::string& name, bool proximity = false, const std::string& targetName = "") {
    auto bytes = Version5(proximity);
    memcpy(bytes.data(), "EDMWEF07", 8);
    const auto nameOffset = bytes.size();
    bytes.resize(nameOffset + 8 + name.size() + targetName.size());
    Write32(bytes, nameOffset, static_cast<uint32_t>(name.size()));
    if (!name.empty()) memcpy(bytes.data() + nameOffset + 4, name.data(), name.size());
    Write32(bytes, nameOffset + 4 + name.size(), static_cast<uint32_t>(targetName.size()));
    if (!targetName.empty()) memcpy(bytes.data() + nameOffset + 8 + name.size(), targetName.data(), targetName.size());
    return bytes;
}
uint32_t Read32(const std::vector<uint8_t>& bytes, size_t offset) {
    uint32_t value = 0; memcpy(&value, bytes.data() + offset, 4); return value;
}
size_t Version8ActionOffset(const std::vector<uint8_t>& bytes) {
    size_t offset = 76u + Read32(bytes, 72) * 8u + Read32(bytes, 68);
    for (unsigned name = 0; name < 2; ++name) offset += 4u + Read32(bytes, offset);
    return offset + 8u;
}
size_t Version8ConditionOffset(const std::vector<uint8_t>& bytes) {
    auto offset = Version8ActionOffset(bytes);
    const auto count = Read32(bytes, offset - 4);
    for (uint32_t index = 0; index < count; ++index) offset += 24u + Read32(bytes, offset + 20);
    return offset;
}
std::vector<uint8_t> Version8(const std::vector<edmw::UnitEffectAction>& actions, bool proximity = false,
    bool stats = false, edmw::UnitEffectTargetPlayers scope = edmw::UnitEffectTargetPlayers::Own) {
    auto bytes = Version7("Support", proximity, "Sherman V");
    memcpy(bytes.data(), "EDMWEF08", 8);
    if (!stats) { bytes.erase(bytes.begin() + 76, bytes.begin() + 92); Write32(bytes, 72, 0); }
    Write32(bytes, 56, proximity || stats ? 1u : 3u);
    const auto scopeOffset = bytes.size();
    bytes.resize(bytes.size() + 8);
    Write32(bytes, scopeOffset, static_cast<uint32_t>(scope));
    Write32(bytes, scopeOffset + 4, static_cast<uint32_t>(actions.size()));
    for (const auto& action : actions) {
        const auto offset = bytes.size();
        bytes.resize(offset + 24 + action.objectName.size());
        Write32(bytes, offset, static_cast<uint32_t>(action.kind));
        Write32(bytes, offset + 4, action.object);
        Write32(bytes, offset + 8, action.count);
        Write32(bytes, offset + 12, static_cast<uint32_t>(action.owner));
        Write32(bytes, offset + 16, action.playerSlot);
        Write32(bytes, offset + 20, static_cast<uint32_t>(action.objectName.size()));
        if (!action.objectName.empty()) memcpy(bytes.data() + offset + 24, action.objectName.data(), action.objectName.size());
    }
    const auto conditions = bytes.size();
    bytes.resize(conditions + 44);
    bytes[conditions] = 1; // One authored effect family across expanded recipient records.
    return bytes;
}
void VerifyActionRuntimeRules() {
    using namespace edmw;
    std::vector<EffectRule> research;
    std::vector<UnitEffectRule> units;
    std::vector<NativeAbilityBinding> abilities;
    const UnitEffectAction transfer{ UnitEffectActionKind::OwnershipTransfer, UINT32_MAX, 1, UnitEffectOwner::SourcePlayer, 1, "" };
    const UnitEffectAction transform{ UnitEffectActionKind::Transform, 777, 1, UnitEffectOwner::SourcePlayer, 1, "Sherman V" };
    const UnitEffectAction spawn{ UnitEffectActionKind::Spawn, 878, 3, UnitEffectOwner::SpecificPlayer, 4, "Citizen" };
    const auto activated = Version8({ transfer, transform, spawn });
    Require(ReadRuntimeRules(activated, research, units, abilities) && units.size() == 1 && abilities.size() == 1 &&
        UnitEffectOutputs(units[0]).empty() && units[0].actions.size() == 3 && units[0].activation == UnitEffectActivation::Instant &&
        units[0].actions[0].object == UINT32_MAX && units[0].actions[1].object == 777 && units[0].actions[2].count == 3 &&
        units[0].actions[2].owner == UnitEffectOwner::SpecificPlayer && units[0].actions[2].playerSlot == 4 &&
        abilities[0].tooltip.find("Change into Sherman V") != std::string::npos &&
        abilities[0].tooltip.find("Spawn 3 x Citizen for Player 4") != std::string::npos &&
        abilities[0].tooltip.find("Applies once per click.") != std::string::npos &&
        abilities[0].tooltip.find("Actions are permanent.") != std::string::npos &&
        abilities[0].tooltip.find("line of sight") == std::string::npos);
    for (size_t length = 0; length < activated.size(); ++length) {
        const std::vector<uint8_t> truncated(activated.begin(), activated.begin() + length);
        Require(!ReadRuntimeRules(truncated, research, units, abilities) && research.empty() && units.empty() && abilities.empty());
    }
    auto bytes = Version8({ transfer, transform, spawn }, true, true, UnitEffectTargetPlayers::Other);
    const auto proximityConditions = Version8ConditionOffset(bytes);
    Write32(bytes, proximityConditions + 16, 60000);
    Write32(bytes, proximityConditions + 20, 5);
    Write32(bytes, proximityConditions + 24, 2);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && abilities.empty() && units[0].outputs.size() == 2 &&
        units[0].actions.size() == 3 && units[0].targetPlayers == UnitEffectTargetPlayers::Other &&
        units[0].familyId[0] == 1 && units[0].delayMilliseconds == 60000 &&
        units[0].maxAffectedTargets == 5 && units[0].maxConcurrentTargets == 2);
    const auto delayedProximity = bytes;
    for (const auto invalid : { std::pair<size_t, uint32_t>{proximityConditions + 16,3600001},
        {proximityConditions + 20,100001}, {proximityConditions + 24,100001},
        {proximityConditions + 28,1}, {proximityConditions + 32,1}, {proximityConditions + 36,1}, {proximityConditions + 40,1} }) {
        bytes = delayedProximity; Write32(bytes, invalid.first, invalid.second);
        Require(!ReadRuntimeRules(bytes, research, units, abilities));
    }
    bytes = Version8({ spawn }, true, false, UnitEffectTargetPlayers::Any);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && units[0].outputs.empty() &&
        units[0].targetPlayers == UnitEffectTargetPlayers::Any && units[0].activation == UnitEffectActivation::Toggle);
    bytes = Version8({ spawn }, false, true);
    Write32(bytes, 56, 2); Write32(bytes, 60, 1500);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && abilities[0].tooltip.find("Lasts 1.5 seconds.") != std::string::npos &&
        abilities[0].tooltip.find("Actions are permanent.") != std::string::npos);
    Write32(bytes, 56, 3); Write32(bytes, 60, 0);
    Require(!ReadRuntimeRules(bytes, research, units, abilities)); // Instant actions cannot discard maintained stat modifiers.
    Require(!ReadRuntimeRules(Version8({}, true), research, units, abilities));
    const auto firstAction = Version8ActionOffset(activated);
    const auto conditions = Version8ConditionOffset(activated);
    bytes = activated;
    Write32(bytes, conditions + 36, 50);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && units[0].resourceCosts[2] == 50 &&
        abilities[0].resourceCosts[2] == 50 && abilities[0].tooltip.find("Costs 50 gold per activation.") != std::string::npos);
    for (const auto invalid : { std::pair<size_t, uint32_t>{conditions + 16,1}, {conditions + 20,1}, {conditions + 24,1},
        {conditions + 28,100001}, {conditions + 32,100001}, {conditions + 36,100001}, {conditions + 40,100001} }) {
        bytes = activated; Write32(bytes, invalid.first, invalid.second);
        Require(!ReadRuntimeRules(bytes, research, units, abilities));
    }
    bytes = activated; std::fill(bytes.begin() + conditions, bytes.begin() + conditions + 16, uint8_t{});
    Require(ReadRuntimeRules(bytes, research, units, abilities) && units[0].familyId == units[0].id);
    for (const auto invalid : { std::pair<size_t, uint32_t>{firstAction - 8,3}, {firstAction - 8,UINT32_MAX},
        {firstAction - 4,65}, {firstAction,0}, {firstAction,4}, {firstAction + 4,777},
        {firstAction + 8,0}, {firstAction + 8,2}, {firstAction + 12,3}, {firstAction + 16,0},
        {firstAction + 16,9}, {firstAction + 20,401}, {firstAction + 24 + 4,100000} }) {
        bytes = activated; Write32(bytes, invalid.first, invalid.second);
        Require(!ReadRuntimeRules(bytes, research, units, abilities));
    }
    bytes = activated; bytes.push_back(0); Require(!ReadRuntimeRules(bytes, research, units, abilities));
    bytes = activated; Write32(bytes, 60, 1); Require(!ReadRuntimeRules(bytes, research, units, abilities));
    Require(!ReadRuntimeRules(Version8({ transfer, transfer }, true), research, units, abilities));
    Require(!ReadRuntimeRules(Version8({ transform, transform }, true), research, units, abilities));
    auto bad = spawn;
    for (const auto count : { 0u,65u,UINT32_MAX }) {
        bad.count = count; Require(!ReadRuntimeRules(Version8({ bad }, true), research, units, abilities));
    }
    bad = spawn; bad.count = 64;
    Require(ReadRuntimeRules(Version8({ bad }, true), research, units, abilities));
    for (const auto& name : { std::string(101, 'X'), std::string(" Leading"), std::string("Bad\nname"),
        std::string("\xC0\xAF"), std::string("\xED\xA0\x80"), std::string("\xF4\x90\x80\x80") }) {
        bad = spawn; bad.objectName = name;
        Require(!ReadRuntimeRules(Version8({ bad }, true), research, units, abilities));
    }
    bad = spawn; bad.objectName = "R\xC3\xA9" "serve";
    Require(ReadRuntimeRules(Version8({ bad }, true), research, units, abilities) && units[0].actions[0].objectName == bad.objectName);
    auto oneSpawn = spawn; oneSpawn.count = 1;
    Require(ReadRuntimeRules(Version8(std::vector<UnitEffectAction>(64, oneSpawn), true), research, units, abilities) && units[0].actions.size() == 64);
    Require(!ReadRuntimeRules(Version8(std::vector<UnitEffectAction>(65, oneSpawn), true), research, units, abilities));
    Require(ReadRuntimeRules(Version8(std::vector<UnitEffectAction>(62, oneSpawn), true, true), research, units, abilities));
    Require(!ReadRuntimeRules(Version8(std::vector<UnitEffectAction>(63, oneSpawn), true, true), research, units, abilities));
    auto maximumSpawn = spawn; maximumSpawn.count = 64;
    Require(!ReadRuntimeRules(Version8({ maximumSpawn, oneSpawn }, true), research, units, abilities));
    bytes = Version8({ spawn });
    Write32(bytes, 40, 777); Write32(bytes, 44, 10000); Write32(bytes, 48, 5); Write32(bytes, 52, 3);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && abilities[0].proximityGranted &&
        abilities[0].tooltip.find("Leaving range removes the ability.") != std::string::npos &&
        abilities[0].tooltip.find("ends its effects") == std::string::npos);
    const auto grant = bytes;
    bytes.insert(bytes.end(), grant.begin() + 16, grant.end()); Write32(bytes, 12, 2);
    const auto second = grant.size();
    bytes[second] = 2; Write32(bytes, second + 24, 879); Write32(bytes, second + 48, 10001);
    Require(ReadRuntimeRules(bytes, research, units, abilities) && units.size() == 2 && abilities.size() == 2);
    auto malformed = bytes;
    const auto relativeAction = Version8ActionOffset(grant) - 16;
    Write32(malformed, second + relativeAction + 8, 2);
    Require(!ReadRuntimeRules(malformed, research, units, abilities)); // Grant family action payloads must match.
    malformed = bytes; Write32(malformed, second + 40, 1);
    Require(!ReadRuntimeRules(malformed, research, units, abilities)); // A family has one activation behavior.
    malformed = bytes; malformed[second + relativeAction + 24] = 'X';
    Require(ReadRuntimeRules(malformed, research, units, abilities)); // Display names never change action identity.
    const auto relativeConditions = Version8ConditionOffset(grant) - 16;
    for (const auto field : { 16u,20u,24u,28u,32u,36u,40u }) {
        malformed = bytes; Write32(malformed, second + relativeConditions + field, 1);
        Require(!ReadRuntimeRules(malformed, research, units, abilities)); // One family shares waits, recipient limits and activation costs.
    }
    malformed = bytes; malformed[second + relativeConditions] = 2;
    Require(!ReadRuntimeRules(malformed, research, units, abilities));
}
void VerifyNamedRuntimeRules() {
    std::vector<edmw::EffectRule> research;
    std::vector<edmw::UnitEffectRule> units;
    std::vector<edmw::NativeAbilityBinding> abilities;
    for (const auto& name : { std::string("Sherman support"), std::string("R\xC3\xA9" "serve"), std::string(100, 'X'), std::string{} }) {
        for (const auto proximity : { false, true }) {
            const auto bytes = Version7(name, proximity, "Sherman V");
            Require(edmw::ReadRuntimeRules(bytes, research, units, abilities) && units.size() == 1 && units[0].name == name && units[0].targetName == "Sherman V");
            Require(proximity ? abilities.empty() : abilities.size() == 1 && abilities[0].caption == "Scout vision");
            for (size_t length = 0; length < bytes.size(); ++length) {
                const std::vector<uint8_t> truncated(bytes.begin(), bytes.begin() + length);
                Require(!edmw::ReadRuntimeRules(truncated, research, units, abilities));
            }
        }
    }
    for (const auto& invalid : { std::string(101, 'X'), std::string("\nBroken"), std::string("Broken\rname"),
        std::string(" Leading"), std::string("Trailing "), std::string("\xC0\xAF"), std::string("\xED\xA0\x80"),
        std::string("\xF4\x90\x80\x80"), std::string("\xE2\x82"), std::string("\xC2\x85"), std::string("\xE2\x80\xA8") })
    {
        Require(!edmw::ReadRuntimeRules(Version7(invalid, true), research, units, abilities));
        Require(!edmw::ReadRuntimeRules(Version7("Support", true, invalid), research, units, abilities));
    }
    std::string maximumEmoji;
    for (unsigned index = 0; index < 50; ++index) maximumEmoji += "\xF0\x9F\x9B\xA1";
    Require(edmw::ValidUnitEffectName(maximumEmoji));
    Require(!edmw::ValidUnitEffectName(maximumEmoji + "\xF0\x9F\x9B\xA1"));
    auto malformed = Version7("Support", true);
    Write32(malformed, 92, 401);
    Require(!edmw::ReadRuntimeRules(malformed, research, units, abilities));
    malformed = Version7("Support", true); malformed.push_back(0);
    Require(!edmw::ReadRuntimeRules(malformed, research, units, abilities));
    malformed = Version7(std::string("A\0B", 3), true);
    Require(!edmw::ReadRuntimeRules(malformed, research, units, abilities));
    Require(edmw::ReadRuntimeRules(Version5(true), research, units, abilities) && units[0].name.empty());
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
    VerifyNamedRuntimeRules();
    VerifyActionRuntimeRules();
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
    bytes = Version5();
    memcpy(bytes.data(), "EDMWEF06", 8);
    Write32(bytes, 40, 777); Write32(bytes, 44, 10000); Write32(bytes, 48, 5); Write32(bytes, 52, 3);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.size() == 1 && abilities.size() == 1 &&
        units[0].trigger == edmw::UnitEffectTrigger::ProximityAbility && units[0].sourceObject == 749 &&
        units[0].targetObject == 777 && abilities[0].sourceObject == 777 && abilities[0].proximityGranted &&
        abilities[0].grantFamilyTechnology == 10000 && units[0].grantFamilyTechnology == 10000 &&
        abilities[0].tooltip.find("Leaving range ends its effects.") != std::string::npos);
    const auto grantBytes = bytes;
    for (size_t length = 0; length < grantBytes.size(); ++length) {
        bytes.assign(grantBytes.begin(), grantBytes.begin() + length);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities) && rules.empty() && units.empty() && abilities.empty());
    }
    bytes = grantBytes; memcpy(bytes.data(), "EDMWEF05", 8);
    Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    for (const auto invalid : { std::pair<size_t, uint32_t>{48,0}, {48,65}, {56,2}, {60,1000}, {64,0}, {68,0}, {44,0}, {44,9999}, {44,15000}, {44,10001} }) {
        bytes = grantBytes; Write32(bytes, invalid.first, invalid.second);
        Require(!edmw::ReadRuntimeRules(bytes, rules, units, abilities));
    }
    bytes = grantBytes; Write32(bytes, 52, 0x80000003u);
    Require(edmw::ReadRuntimeRules(bytes, rules, units, abilities) && units.empty() && abilities.size() == 1 &&
        !abilities[0].enabled && abilities[0].proximityGranted);
    Require(edmw::NativeAbilityMinimumAge(&abilities[0], 10000, 11, {10000,3,51,0}, 9) == INT32_MAX);
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
        const bool grantedFixture = std::string(argv[argument]).find("proximity-granted-abilities.edmwe") != std::string::npos;
        const bool actionFixture = std::string(argv[argument]).find("permanent-actions.edmwe") != std::string::npos;
        const bool delayedFixture = std::string(argv[argument]).find("delayed-action-grants.edmwe") != std::string::npos;
        const bool buildingFixture = std::string(argv[argument]).find("building-actions.edmwe") != std::string::npos;
        const bool version8Fixture = fixture.size() >= 8 && !memcmp(fixture.data(), "EDMWEF08", 8);
        const bool namedFixture = version8Fixture || (fixture.size() >= 8 && !memcmp(fixture.data(), "EDMWEF07", 8));
        if (delayedFixture) {
            Require(version8Fixture && rules.empty() && units.size() == 2 && abilities.size() == 2 && units[0].familyId == units[1].familyId);
            for (const auto& delayed : units)
                Require(delayed.trigger == edmw::UnitEffectTrigger::ProximityAbility && delayed.activation == edmw::UnitEffectActivation::Instant &&
                    delayed.delayMilliseconds == 60000 && delayed.maxConcurrentTargets == 2 && delayed.maxAffectedTargets == 5 &&
                    delayed.resourceCosts[2] == 50 && edmw::UnitEffectOutputs(delayed).empty() && delayed.actions.size() == 1 &&
                    delayed.actions[0].kind == edmw::UnitEffectActionKind::Transform && delayed.actions[0].object == 878);
            for (const auto& binding : abilities)
                Require(binding.proximityGranted && binding.resourceCosts[2] == 50 && binding.tooltip.find("Costs 50 gold") != std::string::npos);
        } else if (buildingFixture) {
            Require(version8Fixture && rules.empty() && units.size() == 1 && abilities.size() == 1);
            const auto& building = units[0];
            Require(building.name == "Building reinforcements" && building.sourceObject == building.targetObject &&
                building.trigger == edmw::UnitEffectTrigger::Activated && building.activation == edmw::UnitEffectActivation::Instant &&
                edmw::UnitEffectOutputs(building).empty() && building.actions.size() == 2 &&
                building.actions[0].kind == edmw::UnitEffectActionKind::Transform && building.actions[0].objectName == "Converted building" &&
                building.actions[1].kind == edmw::UnitEffectActionKind::Spawn && building.actions[1].count == 2 &&
                building.actions[1].objectName == "Reinforcement unit" && building.resourceCosts[0] == 10 &&
                building.resourceCosts[1] == 20 && building.resourceCosts[2] == 30 && building.resourceCosts[3] == 40 &&
                abilities[0].resourceCosts == building.resourceCosts &&
                abilities[0].tooltip.find("Costs 10 food, 20 wood, 30 gold, 40 stone") != std::string::npos);
        } else if (actionFixture) {
            Require(version8Fixture && rules.empty() && units.size() == 1 && abilities.size() == 1);
            const auto& actionRule = units[0];
            Require(actionRule.civilization == 12 && actionRule.sourceObject == 749 && actionRule.targetObject == 749 &&
                actionRule.activation == edmw::UnitEffectActivation::Instant && actionRule.durationMilliseconds == 0 &&
                actionRule.trigger == edmw::UnitEffectTrigger::Activated && edmw::UnitEffectOutputs(actionRule).empty() &&
                actionRule.actions.size() == 3 && actionRule.name == "Reinforcements");
            const auto& transfer = actionRule.actions[0];
            const auto& transform = actionRule.actions[1];
            const auto& spawn = actionRule.actions[2];
            Require(transfer.kind == edmw::UnitEffectActionKind::OwnershipTransfer && transfer.object == UINT32_MAX &&
                transfer.count == 1 && transfer.owner == edmw::UnitEffectOwner::SpecificPlayer && transfer.playerSlot == 2 &&
                transform.kind == edmw::UnitEffectActionKind::Transform && transform.object == 878 && transform.count == 1 &&
                transform.objectName == "Commoner" && spawn.kind == edmw::UnitEffectActionKind::Spawn && spawn.object == 777 &&
                spawn.count == 3 && spawn.owner == edmw::UnitEffectOwner::SourcePlayer && spawn.objectName == "Sherman Tank");
            Require(abilities[0].caption == "Reinforce" && abilities[0].tooltip.find("Change into Commoner") != std::string::npos &&
                abilities[0].tooltip.find("Spawn 3 x Sherman Tank") != std::string::npos &&
                abilities[0].tooltip.find("Applies once per click.") != std::string::npos &&
                abilities[0].tooltip.find("Actions are permanent.") != std::string::npos);
        } else if (grantedFixture) {
            Require(rules.empty() && units.size() == 2 && abilities.size() == 2);
            const auto nameBytes = (namedFixture ? 8 + units[0].name.size() + units[0].targetName.size() : 0) +
                (version8Fixture ? 52u : 0u);
            const auto allNameBytes = (namedFixture ? nameBytes + 8 + units[1].name.size() + units[1].targetName.size() : 0) +
                (version8Fixture ? 52u : 0u);
            Require(fixture.size() == 192 + allNameBytes);
            std::set<uint32_t> targets;
            for (const auto& effect : units) {
                Require(effect.trigger == edmw::UnitEffectTrigger::ProximityAbility && effect.civilization == 12 && effect.sourceObject == 749 &&
                    effect.radiusTiles == 5 && effect.activation == edmw::UnitEffectActivation::Toggle && effect.durationMilliseconds == 0 &&
                    effect.grantFamilyTechnology == 10000 && effect.outputs.size() == 2 &&
                    effect.outputs[0].stat == edmw::UnitEffectStat::Health && effect.outputs[0].amount == 100 &&
                    effect.outputs[1].stat == edmw::UnitEffectStat::LineOfSight && effect.outputs[1].amount == 3);
                targets.insert(effect.targetObject);
            }
            const std::set<uint32_t> expectedTargets{777,878};
            Require(targets == expectedTargets && abilities[0].technology != abilities[1].technology);
            for (const auto& binding : abilities)
                Require(binding.enabled && binding.proximityGranted && binding.caption == "Nearby boost" &&
                    binding.grantFamilyTechnology == 10000 && expectedTargets.count(binding.sourceObject) == 1);
            auto malformed = fixture;
            Write32(malformed, 16 + 88 + nameBytes + 24, units[0].targetObject);
            Require(!edmw::ReadRuntimeRules(malformed, rules, units, abilities));
            malformed = fixture; malformed[180 + nameBytes] = 'X'; // Different second-family caption.
            Require(!edmw::ReadRuntimeRules(malformed, rules, units, abilities));
            malformed = fixture; Write32(malformed, 16 + 88 + nameBytes + 32, 6);
            Require(!edmw::ReadRuntimeRules(malformed, rules, units, abilities));
            malformed = fixture; Write32(malformed, 16 + 88 + nameBytes + 36, 0x80000003u);
            Require(!edmw::ReadRuntimeRules(malformed, rules, units, abilities));
            Require(edmw::ReadRuntimeRules(fixture, rules, units, abilities));
        } else if (multiTargetAura) {
            Require(rules.empty() && abilities.empty() && units.size() == 2);
            const auto nameBytes = (namedFixture ? 16 + units[0].name.size() + units[1].name.size() + units[0].targetName.size() + units[1].targetName.size() : 0) +
                (version8Fixture ? 104u : 0u);
            Require(fixture.size() == 168 + nameBytes);
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
        if (!abilities.empty() && !allStats && !grantedFixture && !actionFixture && !delayedFixture && !buildingFixture) {
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
    puts("Native formats 01/02/03/04/05/06/07/08, permanent actions, authored UTF-8 names, proximity-granted abilities, multiple outputs, activation bounds, duplicate IDs, malformed payloads and research/load filtering passed.");
}
