#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <algorithm>
#include <cstring>
#include <map>
#include <mutex>
#include "NativeAbilityBindings.h"
#include "UnitEffectNative.h"
#include "EffectTrace.h"

namespace {
constexpr uintptr_t commandSlot = 0x007EC1BC; // EEMResearchTechnology vtable +0x28
constexpr uintptr_t commandFunction = 0x005072D1;
constexpr uintptr_t resourceStringSlot = 0x007E4894;
constexpr uintptr_t assignStringSlot = 0x007E30EC;
constexpr uintptr_t findTechnologyAddress = 0x00401D64;
constexpr uintptr_t initializeTreeAddress = 0x0066D3CD;
constexpr uintptr_t actionKeyAddress = 0x006714D6;
constexpr uintptr_t groupHudKeyReturn = 0x0076D231;
constexpr uint8_t findTechnologyBytes[] = { 0x56, 0x8B, 0x74, 0x24, 0x08 };
constexpr uint8_t initializeTreeBytes[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x18 };
constexpr uint8_t actionKeyBytes[] = { 0x8B, 0x41, 0x08, 0x8B, 0x90, 0xA8, 0x00, 0x00, 0x00 };
constexpr uint8_t groupHudKeyCallBytes[] = { 0xE8, 0xA5, 0x42, 0xF0, 0xFF };
constexpr uint32_t captionBase = 1000000;
constexpr uint32_t tooltipBase = 2000000;
using Command = void(__thiscall*)(void*);
using ResourceString = long(__thiscall*)(void*, long, void*);
using AssignString = void*(__thiscall*)(void*, const char*);
using FindPlayer = void*(__thiscall*)(void*, uint32_t);
using FindUnit = void*(__thiscall*)(void*, uint32_t);
using FindTechnology = void*(__thiscall*)(void*, uint32_t);
using InitializeTree = uintptr_t(__thiscall*)(void*, uint32_t, uint32_t, uint32_t, uint32_t);
using EvaluatePrerequisites = void(__thiscall*)(void*, uint32_t);
using ReadActionKey = uintptr_t(__thiscall*)(void*, edmw::NativeAbilityActionKey*);
using SelectionCount = uint32_t(__thiscall*)(void*);
using SelectionUnit = void*(__thiscall*)(void*, uint32_t);
ResourceString originalResourceString = nullptr;
AssignString assignString = nullptr;
FindTechnology originalFindTechnology = nullptr;
InitializeTree originalInitializeTree = nullptr;
ReadActionKey originalActionKey = nullptr;
std::map<uint32_t, edmw::NativeAbilityBinding> abilities;
std::map<uint32_t, int32_t> authoredSlots;
std::mutex placementMutex;

template<class T> T Read(const void* pointer, size_t offset) {
    T value;
    memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value));
    return value;
}

edmw::NativeAbilityNodeIdentity ReadAbilityIdentity(const void* definition) {
    return { Read<uint32_t>(definition, 0xFC), Read<uint32_t>(definition, 0xA8),
        Read<uint32_t>(definition, 0xAC), Read<uint32_t>(definition, 0x100) };
}

std::vector<void*> AbilityActors(const edmw::NativeAbilityBinding& binding, void* player, void* anchor,
    uint32_t playerIndex, uint32_t civilization) {
    const edmw::UnitEffectSnapshot anchorSnapshot{ Read<uint32_t>(anchor, 0x24), playerIndex,
        civilization, binding.sourceObject, 0, 0, true, 0 };
    std::map<uint32_t, void*> pointers{ { anchorSnapshot.id, anchor } };
    std::vector<edmw::UnitEffectSnapshot> selected;
    uint32_t count = 0;
    // Native research execution 0x00507302 saves owner +0xD44. Its group loop
    // 0x0050776A/+0x00507788 calls these exact count/item virtuals. Never use
    // the local HUD selection at +0x488 for synchronized effect commands.
    const auto selection = Read<void*>(player, 0xD44);
    const auto table = selection ? Read<uintptr_t*>(selection, 0) : nullptr;
    if (table && table[0x24 / 4] && table[0x20 / 4]) {
        count = reinterpret_cast<SelectionCount>(table[0x24 / 4])(selection);
        if (count <= 4096) for (uint32_t index = 0; index < count; ++index) {
            const auto unit = reinterpret_cast<SelectionUnit>(table[0x20 / 4])(selection, index);
            if (!unit || Read<uintptr_t>(unit, 0) != 0x007F6C48 || Read<void*>(unit, 0x18) != player) continue;
            const auto prototype = Read<void*>(unit, 0x64);
            if (!prototype) continue;
            const auto id = Read<uint32_t>(unit, 0x24);
            selected.push_back({ id, playerIndex, civilization, Read<uint32_t>(prototype, 0x1AC),
                0, 0, Read<int32_t>(unit, 0x90) > 0, 0 });
            pointers.emplace(id, unit);
        }
    }
    std::vector<void*> actors;
    for (const auto id : edmw::NativeAbilityActivationTargets(binding, anchorSnapshot, selected))
        actors.push_back(pointers.at(id));
    edmw::TraceEffect("Ability targets: technology=%u anchor=%u synchronized-selection=%u eligible=%u",
        binding.technology, anchorSnapshot.id, count, static_cast<uint32_t>(actors.size()));
    return actors;
}

bool LivePlayerTree(const void* tree) {
    const auto owner = Read<void*>(tree, 0x16D0);
    const auto playerIndex = Read<uint32_t>(tree, 4);
    const auto playerCount = *reinterpret_cast<const uint32_t*>(0x0099EF30);
    return owner && playerCount && playerCount <= 16 && playerIndex < playerCount &&
        reinterpret_cast<void* const*>(0x0099EED8)[playerIndex] == owner &&
        Read<uint32_t>(owner, 0x478) == playerIndex;
}

bool ReadVector(const void* object, size_t offset, size_t elementSize, size_t maximumCount,
    uintptr_t& begin, size_t& count) {
    begin = Read<uintptr_t>(object, offset + 4);
    return edmw::NativeAbilityVectorCount(begin, Read<uintptr_t>(object, offset + 8),
        Read<uintptr_t>(object, offset + 12), elementSize, maximumCount, count);
}

void* CivilizationSlot(void* definition, uint32_t nativeCivilization) {
    uintptr_t begin = 0; size_t count = 0;
    if (!nativeCivilization || !ReadVector(definition, 0x54, 8, 32, begin, count) ||
        nativeCivilization - 1 >= count) return nullptr;
    return reinterpret_cast<void*>(begin + (nativeCivilization - 1) * 8);
}

bool SourceActions(uint32_t sourceObject, std::vector<uint32_t>& actions) {
    // This is the canonical registry, never a unit's temporary private shadow.
    if (sourceObject >= 2049) return false;
    const auto prototype = reinterpret_cast<void* const*>(0x009D1BF8)[sourceObject];
    if (!prototype || Read<uint32_t>(prototype, 0x1AC) != sourceObject) return false;
    uintptr_t begin = 0; size_t count = 0;
    if (!ReadVector(prototype, 0x34, 4, 4096, begin, count)) return false;
    actions.resize(count);
    if (count) memcpy(actions.data(), reinterpret_cast<const void*>(begin), count * 4);
    return true;
}

bool MatchesAbilitySource(void* definition, const edmw::NativeAbilityBinding& binding) {
    uintptr_t begin = 0; size_t count = 0;
    if (!ReadVector(definition, 0x0C, 4, 4096, begin, count) || count != 1 ||
        Read<uint32_t>(reinterpret_cast<const void*>(begin), 0) != binding.sourceObject) return false;
    std::vector<uint32_t> actions;
    return SourceActions(binding.sourceObject, actions) &&
        std::find(actions.begin(), actions.end(), binding.technology) != actions.end();
}

uintptr_t __fastcall ReadAbilityGroupKey(void* node, void*, edmw::NativeAbilityActionKey* destination) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress());
    const auto result = originalActionKey(node, destination);
    // Only the multi-selection HUD's producer-action deduplication key changes.
    // Keep native conversion and its EAX result for every other call site.
    if (caller != groupHudKeyReturn || !node || !destination) return result;
    const auto tree = Read<void*>(node, 0x24);
    if (!tree || !LivePlayerTree(tree)) return result;
    const auto definition = Read<void*>(node, 8);
    if (!definition) return result;
    const auto identity = ReadAbilityIdentity(definition);
    const auto binding = abilities.find(identity.technology);
    if (binding == abilities.end() || !MatchesAbilitySource(definition, binding->second)) return result;
    const auto key = edmw::NativeAbilityGroupActionKey(&binding->second, identity.technology,
        Read<uint32_t>(tree, 0x16D4), identity, *destination, true);
    memcpy(destination, &key, sizeof(key));
    return result;
}

void RemapAbilityButtons(void* tree, uint32_t sourceObject) {
    std::lock_guard<std::mutex> lock(placementMutex);
    const auto nativeCivilization = Read<uint32_t>(tree, 0x16D4);
    const auto wireCivilization = edmw::NativeCivilizationToWireId(nativeCivilization);
    std::vector<uint32_t> actions;
    const auto validActions = SourceActions(sourceObject, actions);
    std::map<uint32_t, void*> destinations;
    std::vector<edmw::NativeAbilityPlacement> requested;
    for (const auto& entry : abilities) {
        const auto& binding = entry.second;
        if (binding.civilization != wireCivilization || binding.sourceObject != sourceObject ||
            (validActions && std::find(actions.begin(), actions.end(), binding.technology) == actions.end())) continue;
        const auto node = originalFindTechnology(tree, binding.technology);
        const auto definition = node ? Read<void*>(node, 8) : nullptr;
        if (!definition || !edmw::MatchesNativeAbilityNode(&binding, binding.technology,
            ReadAbilityIdentity(definition))) continue;
        const auto slot = CivilizationSlot(definition, nativeCivilization);
        if (!slot) {
            // A malformed/missing override cannot safely suppress the shared
            // fallback icon. Keep the native lookup pointer valid for callers,
            // but hide only this registered node through its local age check.
            const int32_t unavailableAge = INT32_MAX;
            memcpy(static_cast<uint8_t*>(node) + 0x2C, &unavailableAge, sizeof(unavailableAge));
            continue;
        }
        const auto authored = authoredSlots.emplace(binding.technology, Read<int32_t>(slot, 0)).first;
        requested.push_back({ binding.technology, authored->second });
        destinations.emplace(binding.technology, slot);
    }
    std::vector<int32_t> occupied;
    if (!validActions) {
        // Incomplete native metadata cannot establish a free position. Hide only
        // our registered buttons rather than falling back onto a stock control.
        occupied.assign(edmw::NativeAbilityHudSlots.begin(), edmw::NativeAbilityHudSlots.end());
    } else for (const auto technology : actions) {
        if (destinations.find(technology) != destinations.end()) continue;
        // Use the unwrapped lookup; reserving other actions must not recurse,
        // evaluate prerequisites or depend on their current age/availability.
        const auto node = originalFindTechnology(tree, technology);
        if (!node) continue;
        const auto definition = Read<void*>(node, 8);
        const auto slot = definition ? CivilizationSlot(definition, nativeCivilization) : nullptr;
        const auto button = Read<void*>(node, 0x28);
        occupied.push_back(edmw::ResolveNativeAbilityHudSlot(slot ? Read<int32_t>(slot, 0) : -1,
            button ? Read<int32_t>(button, 0x3C) : -1));
    }
    for (const auto& placement : edmw::AllocateNativeAbilityHudSlots(std::move(requested), occupied)) {
        // Definitions are shared, but only this registered technology's one
        // matching-civilization slot is changed. Its shared icon is untouched.
        memcpy(destinations.at(placement.technology), &placement.slot, sizeof(placement.slot));
    }
}

void* __fastcall FindAbilityTechnology(void* tree, void*, uint32_t technology) {
    auto node = originalFindTechnology(tree, technology);
    const auto binding = abilities.find(technology);
    if (!node || binding == abilities.end() || !LivePlayerTree(tree)) return node;
    const auto definition = Read<void*>(node, 8);
    if (!definition || !edmw::MatchesNativeAbilityNode(&binding->second, technology, ReadAbilityIdentity(definition)) ||
        binding->second.civilization != edmw::NativeCivilizationToWireId(Read<uint32_t>(tree, 0x16D4))) return node;
    // Loader 0x00672772 derives this minimum from the technology ID, making
    // reserved IDs start at age 9. Only registered empty custom commands are
    // age-neutral; native civilization, prerequisites and availability remain.
    const auto previous = Read<int32_t>(node, 0x2C);
    const auto desired = edmw::NativeAbilityMinimumAge(&binding->second, technology, Read<uint32_t>(tree, 0x16D4),
        ReadAbilityIdentity(definition), previous);
    if (desired != previous) memcpy(static_cast<uint8_t*>(node) + 0x2C, &desired, sizeof(desired));
    // Native multi-selection collection requires definition flag 9. Its owned
    // bitset stores a bit count at +0x70 and allocation byte count at +0x71.
    // Enable only this registered source's HUD flag; command/event stay 51/0.
    const auto flags = Read<uint8_t*>(definition, 0x6C);
    if (flags && Read<uint8_t>(definition, 0x70) >= 10 && Read<uint8_t>(definition, 0x71) >= 2 &&
        MatchesAbilitySource(definition, binding->second)) {
        flags[1] = edmw::NativeAbilityGroupVisibilityByte(&binding->second, technology,
            Read<uint32_t>(tree, 0x16D4), ReadAbilityIdentity(definition), flags[1]);
    }
    RemapAbilityButtons(tree, binding->second.sourceObject);
    return node;
}

uintptr_t __fastcall InitializeAbilityTree(void* tree, void*, uint32_t reset, uint32_t restore,
    uint32_t civilization, uint32_t units) {
    const auto result = originalInitializeTree(tree, reset, restore, civilization, units);
    // The native prerequisite evaluator dereferences the owning player. Global
    // template/menu trees are also initialized here and must remain read-only.
    if (!LivePlayerTree(tree)) return result;
    const auto wireCivilization = edmw::NativeCivilizationToWireId(Read<uint32_t>(tree, 0x16D4));
    for (const auto& entry : abilities) {
        const auto& binding = entry.second;
        if (binding.civilization != wireCivilization) continue;
        auto node = FindAbilityTechnology(tree, nullptr, binding.technology);
        const auto definition = node ? Read<void*>(node, 8) : nullptr;
        if (!definition || !edmw::MatchesNativeAbilityNode(&binding, binding.technology,
            ReadAbilityIdentity(definition))) continue;
        // Native epoch initialization skips group 9 when a civilization's final
        // age is below 9. Complete its intended age-zero prerequisite through
        // the normal evaluator after deterministic civilization setup, never
        // from a HUD lookup and never by forcing availability/civilization bits.
        reinterpret_cast<EvaluatePrerequisites>(0x0067345C)(node, 0);
    }
    return result;
}

long __fastcall GetResourceString(void* resource, void*, long id, void* destination) {
    // The game's name getter 0x00672ECE uses Language2; its HUD help reader
    // 0x0076E677 uses Language. Do not replace a same-numbered unrelated lookup.
    const auto nameResource = *reinterpret_cast<void* const*>(0x0099FC94);
    const auto helpResource = *reinterpret_cast<void* const*>(0x0099FC90);
    const uint32_t value = static_cast<uint32_t>(id);
    const uint32_t base = resource == nameResource ? captionBase : resource == helpResource ? tooltipBase : 0;
    if (base && value >= base) {
        const auto found = abilities.find(value - base);
        if (found != abilities.end() && destination) {
            const auto& text = base == captionBase ? found->second.caption : found->second.tooltip;
            assignString(destination, text.c_str());
            // Low-Level Engine.dll UResourceFile::GetString returns zero on success.
            return 0;
        }
    }
    return originalResourceString(resource, id, destination);
}

void __fastcall ExecuteAbility(void* command, void*) {
    const auto technology = Read<uint32_t>(command, 0x14);
    const auto found = abilities.find(technology);
    if (found == abilities.end()) {
        reinterpret_cast<Command>(commandFunction)(command);
        return;
    }
    // Registered custom commands are always consumed, even if invalid. They
    // must never fall through to a native production/research side effect.
    const uint32_t networkIdentity = Read<uint8_t>(command, 0x0A);
    const auto actorId = Read<uint32_t>(command, 0x10);
    edmw::TraceEffect("Ability command received: technology=%u networkIdentity=%u actor=%u", technology, networkIdentity, actorId);
    if (!found->second.enabled) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=disabled", technology); return;
    }
    if (networkIdentity == 255) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=unassigned-network-identity", technology); return;
    }
    auto world = reinterpret_cast<void*>(0x0099EE58);
    auto player = reinterpret_cast<FindPlayer>(0x005926D2)(world, networkIdentity);
    if (!player) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=network-player-not-found", technology); return;
    }
    // The native resolver compares player +0x480, not the world-array index at
    // +0x478. These identities commonly differ (e.g. human identity 0/index 1).
    const auto playerIndex = edmw::NativeAbilityCommandPlayerIndex(networkIdentity, Read<uint8_t>(player, 0x480),
        Read<uint32_t>(player, 0x478), Read<uint32_t>(world, 0xD8));
    if (playerIndex == UINT32_MAX) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=invalid-player-mapping", technology); return;
    }
    if (reinterpret_cast<void* const*>(0x0099EED8)[playerIndex] != player) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=player-registry-mismatch", technology); return;
    }
    auto actor = reinterpret_cast<FindUnit>(0x005A3D5C)(world, actorId);
    if (!actor) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=actor-not-found", technology); return;
    }
    if (Read<void*>(actor, 0x18) != player) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=actor-owner-mismatch", technology); return;
    }
    if (Read<int32_t>(actor, 0x90) <= 0) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=actor-dead", technology); return;
    }
    const auto tree = Read<void*>(player, 0xD58);
    const auto prototype = Read<void*>(actor, 0x64);
    if (!tree || !prototype) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=actor-data-missing", technology); return;
    }
    const auto civilization = edmw::NativeCivilizationToWireId(Read<uint32_t>(tree, 0x16D4));
    if (civilization != found->second.civilization) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=civilization-mismatch", technology); return;
    }
    if (Read<uint32_t>(prototype, 0x1AC) != found->second.sourceObject) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=source-object-mismatch", technology); return;
    }
    const auto node = reinterpret_cast<FindTechnology>(0x00401D64)(tree, technology);
    if (!node || !Read<uint8_t>(node, 0x3D)) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=node-unavailable", technology); return;
    }
    const auto definition = Read<void*>(node, 8);
    if (!definition || !edmw::MatchesNativeAbilityNode(&found->second, technology, ReadAbilityIdentity(definition))) {
        edmw::TraceEffect("Ability command rejected: technology=%u reason=registered-node-identity-mismatch", technology); return;
    }
    const auto actors = AbilityActors(found->second, player, actor, playerIndex, civilization);
    const auto activated = edmw::ActivateUnitEffects(found->second.effect, actors, playerIndex);
    edmw::TraceEffect("Ability activation %s: technology=%u playerIndex=%u actor=%u civilization=%u targets=%u",
        activated ? "accepted" : "rejected", technology, playerIndex, Read<uint32_t>(actor, 0x24), civilization,
        static_cast<uint32_t>(actors.size()));
}

bool WritePointer(uintptr_t address, const void* value) {
    DWORD protection;
    if (!VirtualProtect(reinterpret_cast<void*>(address), sizeof(void*), PAGE_READWRITE, &protection)) return false;
    memcpy(reinterpret_cast<void*>(address), &value, sizeof(value));
    DWORD ignored;
    return VirtualProtect(reinterpret_cast<void*>(address), sizeof(void*), protection, &ignored) != FALSE;
}

void* InstallFunction(uintptr_t address, const uint8_t* expected, size_t size, const void* callback) {
    if (memcmp(reinterpret_cast<const void*>(address), expected, size)) return nullptr;
    auto trampoline = static_cast<uint8_t*>(VirtualAlloc(nullptr, size + 5, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!trampoline) return nullptr;
    // These copied prologues contain complete position-independent instructions.
    memcpy(trampoline, expected, size);
    trampoline[size] = 0xE9;
    const auto back = static_cast<int32_t>(address + size - reinterpret_cast<uintptr_t>(trampoline + size) - 5);
    memcpy(trampoline + size + 1, &back, sizeof(back));
    DWORD protection = 0;
    if (!VirtualProtect(trampoline, size + 5, PAGE_EXECUTE_READ, &protection) ||
        !FlushInstructionCache(GetCurrentProcess(), trampoline, size + 5)) {
        VirtualFree(trampoline, 0, MEM_RELEASE);
        return nullptr;
    }
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, PAGE_EXECUTE_READWRITE, &protection)) return nullptr;
    std::vector<uint8_t> patch(size, 0x90);
    patch[0] = 0xE9;
    const auto branch = static_cast<int32_t>(reinterpret_cast<uintptr_t>(callback) - address - 5);
    memcpy(patch.data() + 1, &branch, sizeof(branch));
    memcpy(reinterpret_cast<void*>(address), patch.data(), size);
    DWORD ignored = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, protection, &ignored) ||
        !FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size)) return nullptr;
    return trampoline;
}

bool Printable(const std::string& value, size_t limit) {
    return !value.empty() && value.size() <= limit && std::all_of(value.begin(), value.end(),
        [](unsigned char character) { return character >= 32 && character <= 126; }) &&
        std::any_of(value.begin(), value.end(), [](unsigned char character) { return character != ' '; });
}
}

bool edmw::InstallNativeAbilities(const std::vector<NativeAbilityBinding>& bindings) {
    if (bindings.empty()) return true;
    if (!abilities.empty() || bindings.size() > 1024 || originalFindTechnology || originalInitializeTree || originalActionKey ||
        memcmp(reinterpret_cast<const void*>(findTechnologyAddress), findTechnologyBytes, sizeof(findTechnologyBytes)) ||
        memcmp(reinterpret_cast<const void*>(initializeTreeAddress), initializeTreeBytes, sizeof(initializeTreeBytes)) ||
        memcmp(reinterpret_cast<const void*>(actionKeyAddress), actionKeyBytes, sizeof(actionKeyBytes)) ||
        memcmp(reinterpret_cast<const void*>(groupHudKeyReturn - 5), groupHudKeyCallBytes, sizeof(groupHudKeyCallBytes)) ||
        *reinterpret_cast<const uintptr_t*>(commandSlot) != commandFunction) return false;
    const auto engine = GetModuleHandleW(L"Low-Level Engine.dll");
    if (!engine) return false;
    originalResourceString = reinterpret_cast<ResourceString>(GetProcAddress(engine, "?GetString@UResourceFile@@QAEJJAAVUWideString@@@Z"));
    assignString = reinterpret_cast<AssignString>(GetProcAddress(engine, "??4UWideString@@QAEAAV0@PBD@Z"));
    if (!originalResourceString || !assignString ||
        *reinterpret_cast<const ResourceString*>(resourceStringSlot) != originalResourceString ||
        *reinterpret_cast<const AssignString*>(assignStringSlot) != assignString) return false;
    std::map<uint32_t, NativeAbilityBinding> checked;
    for (const auto& binding : bindings) {
        if (binding.technology < 10000 || binding.technology > 14999 || binding.civilization < 4 ||
            binding.civilization > 12 || binding.sourceObject >= 100000 || !Printable(binding.caption, 40) ||
            !Printable(binding.tooltip, 8192) || !checked.emplace(binding.technology, binding).second) return false;
    }
    abilities.swap(checked);
    originalFindTechnology = reinterpret_cast<FindTechnology>(InstallFunction(findTechnologyAddress,
        findTechnologyBytes, sizeof(findTechnologyBytes), reinterpret_cast<const void*>(&FindAbilityTechnology)));
    originalInitializeTree = reinterpret_cast<InitializeTree>(InstallFunction(initializeTreeAddress,
        initializeTreeBytes, sizeof(initializeTreeBytes), reinterpret_cast<const void*>(&InitializeAbilityTree)));
    originalActionKey = reinterpret_cast<ReadActionKey>(InstallFunction(actionKeyAddress,
        actionKeyBytes, sizeof(actionKeyBytes), reinterpret_cast<const void*>(&ReadAbilityGroupKey)));
    if (!originalFindTechnology || !originalInitializeTree || !originalActionKey) return false;
    // The launcher terminates the still-suspended owned process if either
    // protected write fails, so a partial binding can never enter gameplay.
    return WritePointer(resourceStringSlot, reinterpret_cast<const void*>(&GetResourceString)) &&
        WritePointer(commandSlot, reinterpret_cast<const void*>(&ExecuteAbility));
}
