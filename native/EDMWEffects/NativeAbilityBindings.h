#pragma once
#include "UnitEffectRuntime.h"
#include <array>
#include <algorithm>
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

struct NativeAbilityNodeIdentity {
    uint32_t technology, kind, command, event;
};
inline bool MatchesNativeAbilityNode(const NativeAbilityBinding* binding, uint32_t requestedTechnology,
    const NativeAbilityNodeIdentity& identity) {
    return binding && binding->technology >= 10000 && binding->technology <= 14999 &&
        requestedTechnology == binding->technology && identity.technology == binding->technology &&
        identity.kind == 3 && identity.command == 51 && identity.event == 0;
}
inline int32_t NativeAbilityMinimumAge(const NativeAbilityBinding* binding, uint32_t requestedTechnology,
    uint32_t nativeCivilization, const NativeAbilityNodeIdentity& identity, int32_t nativeMinimumAge) {
    return MatchesNativeAbilityNode(binding, requestedTechnology, identity) &&
        binding->civilization == NativeCivilizationToWireId(nativeCivilization) ? 0 : nativeMinimumAge;
}
inline uint8_t NativeAbilityGroupVisibilityByte(const NativeAbilityBinding* binding, uint32_t requestedTechnology,
    uint32_t nativeCivilization, const NativeAbilityNodeIdentity& identity, uint8_t nativeFlags) {
    return MatchesNativeAbilityNode(binding, requestedTechnology, identity) &&
        binding->civilization == NativeCivilizationToWireId(nativeCivilization)
        ? static_cast<uint8_t>(nativeFlags | 2u) : nativeFlags;
}
struct NativeAbilityActionKey { uint32_t command, parameter; };
inline NativeAbilityActionKey NativeAbilityGroupActionKey(const NativeAbilityBinding* binding,
    uint32_t requestedTechnology, uint32_t nativeCivilization, const NativeAbilityNodeIdentity& identity,
    NativeAbilityActionKey nativeKey, bool groupHud) {
    if (groupHud && nativeKey.command == 51 && nativeKey.parameter == 0 &&
        MatchesNativeAbilityNode(binding, requestedTechnology, identity) &&
        binding->civilization == NativeCivilizationToWireId(nativeCivilization))
        nativeKey.parameter = binding->technology;
    return nativeKey;
}

// Command +0x0A names the native network identity (player byte +0x480).
// Effect snapshots use the independently assigned world index (+0x478).
inline uint32_t NativeAbilityCommandPlayerIndex(uint32_t commandIdentity, uint32_t resolvedIdentity,
    uint32_t resolvedIndex, uint32_t worldPlayerCount) {
    return commandIdentity < 255 && commandIdentity == resolvedIdentity &&
        worldPlayerCount > 0 && worldPlayerCount <= 16 && resolvedIndex < 16 && resolvedIndex < worldPlayerCount
        ? resolvedIndex : UINT32_MAX;
}

inline std::vector<uint32_t> NativeAbilityActivationTargets(const NativeAbilityBinding& binding,
    const UnitEffectSnapshot& anchor, const std::vector<UnitEffectSnapshot>& selection) {
    const auto eligible = [&](const UnitEffectSnapshot& unit) {
        return unit.id != UINT32_MAX && unit.alive && unit.player == anchor.player &&
            unit.civilization == binding.civilization && unit.object == binding.sourceObject;
    };
    if (!binding.enabled || !eligible(anchor)) return {};
    std::set<uint32_t> targets{ anchor.id };
    // The command retains its actor identity even for a group. Expand only if
    // that actor remains in the synchronized command selection; queued commands
    // must not accidentally affect an unrelated later selection.
    const auto selectedAnchor = std::find_if(selection.begin(), selection.end(), [&](const auto& unit) {
        return unit.id == anchor.id && eligible(unit);
    });
    if (selectedAnchor != selection.end())
        for (const auto& unit : selection) if (eligible(unit)) targets.insert(unit.id);
    return { targets.begin(), targets.end() };
}

// The fixed command and middle-action widgets share rectangles with the other
// train/research slots. Reserve those entire regions, even for an idle actor.
inline constexpr std::array<int32_t, 13> NativeAbilityHudSlots{ 13, 27, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41 };
inline constexpr int32_t NativeAbilityHiddenHudSlot = 10000;
inline bool IsNativeAbilityHudSlot(int32_t slot) {
    return std::find(NativeAbilityHudSlots.begin(), NativeAbilityHudSlots.end(), slot) != NativeAbilityHudSlots.end();
}
inline int32_t ResolveNativeAbilityHudSlot(int32_t civilizationSlot, int32_t buttonFallback) {
    const auto slot = civilizationSlot > 0 ? civilizationSlot : buttonFallback;
    return slot < 0 || slot >= NativeAbilityHiddenHudSlot ? -1 : (std::min)(slot, 41);
}
struct NativeAbilityPlacement {
    uint32_t technology;
    int32_t slot;
};
inline std::vector<NativeAbilityPlacement> AllocateNativeAbilityHudSlots(
    std::vector<NativeAbilityPlacement> requested, const std::vector<int32_t>& occupied) {
    std::array<bool, 42> used{};
    for (const auto slot : occupied) if (slot >= 0 && slot < static_cast<int32_t>(used.size())) used[slot] = true;
    std::sort(requested.begin(), requested.end(), [](const auto& left, const auto& right) {
        return left.technology < right.technology;
    });
    for (auto& placement : requested) {
        if (!IsNativeAbilityHudSlot(placement.slot) || used[placement.slot]) {
            const auto available = std::find_if(NativeAbilityHudSlots.begin(), NativeAbilityHudSlots.end(),
                [&](int32_t slot) { return !used[slot]; });
            placement.slot = available == NativeAbilityHudSlots.end() ? NativeAbilityHiddenHudSlot : *available;
        }
        if (placement.slot != NativeAbilityHiddenHudSlot) used[placement.slot] = true;
    }
    return requested;
}
inline bool NativeAbilityVectorCount(uintptr_t begin, uintptr_t end, uintptr_t capacity,
    size_t elementSize, size_t maximumCount, size_t& count) {
    count = 0;
    if (!elementSize) return false;
    if (!begin) return !end && !capacity;
    if (end < begin || capacity < end || (end - begin) % elementSize ||
        (capacity - begin) % elementSize || (end - begin) / elementSize > maximumCount) return false;
    count = static_cast<size_t>((end - begin) / elementSize);
    return true;
}

// Installs native HUD localization, registered-node age/prerequisite handling
// and synchronized command bindings in this process only.
// The caller must first install matching enabled activated UnitEffectRules. Disabled
// bindings retain their caption/help and consume activation without applying it. Activated
// state is session-only until the save integration has been implemented.
bool InstallNativeAbilities(const std::vector<NativeAbilityBinding>& bindings);
}
