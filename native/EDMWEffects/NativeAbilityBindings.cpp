#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <cstring>
#include <map>
#include "NativeAbilityBindings.h"
#include "UnitEffectNative.h"

namespace {
constexpr uintptr_t commandSlot = 0x007EC1BC; // EEMResearchTechnology vtable +0x28
constexpr uintptr_t commandFunction = 0x005072D1;
constexpr uintptr_t resourceStringSlot = 0x007E4894;
constexpr uintptr_t assignStringSlot = 0x007E30EC;
constexpr uint32_t captionBase = 1000000;
constexpr uint32_t tooltipBase = 2000000;
using Command = void(__thiscall*)(void*);
using ResourceString = long(__thiscall*)(void*, long, void*);
using AssignString = void*(__thiscall*)(void*, const char*);
using FindPlayer = void*(__thiscall*)(void*, uint32_t);
using FindUnit = void*(__thiscall*)(void*, uint32_t);
using FindTechnology = void*(__thiscall*)(void*, uint32_t);
ResourceString originalResourceString = nullptr;
AssignString assignString = nullptr;
std::map<uint32_t, edmw::NativeAbilityBinding> abilities;

template<class T> T Read(const void* pointer, size_t offset) {
    T value;
    memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value));
    return value;
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
    if (!found->second.enabled) return;
    const auto playerIndex = Read<uint8_t>(command, 0x0A);
    if (playerIndex >= 16) return;
    auto world = reinterpret_cast<void*>(0x0099EE58);
    auto player = reinterpret_cast<FindPlayer>(0x005926D2)(world, playerIndex);
    auto actor = reinterpret_cast<FindUnit>(0x005A3D5C)(world, Read<uint32_t>(command, 0x10));
    if (!player || !actor || Read<uint32_t>(player, 0x478) != playerIndex ||
        Read<void*>(actor, 0x18) != player || Read<int32_t>(actor, 0x90) <= 0) return;
    const auto tree = Read<void*>(player, 0xD58);
    const auto prototype = Read<void*>(actor, 0x64);
    if (!tree || !prototype || Read<uint32_t>(tree, 0x16D4) != found->second.civilization ||
        Read<uint32_t>(prototype, 0x1AC) != found->second.sourceObject) return;
    const auto node = reinterpret_cast<FindTechnology>(0x00401D64)(tree, technology);
    if (!node || !Read<uint8_t>(node, 0x3D)) return;
    const auto definition = Read<void*>(node, 8);
    if (!definition || Read<uint32_t>(definition, 0xA8) != 3 ||
        Read<uint32_t>(definition, 0xFC) != technology) return;
    edmw::ActivateUnitEffect(found->second.effect, actor, playerIndex);
}

bool WritePointer(uintptr_t address, const void* value) {
    DWORD protection;
    if (!VirtualProtect(reinterpret_cast<void*>(address), sizeof(void*), PAGE_READWRITE, &protection)) return false;
    memcpy(reinterpret_cast<void*>(address), &value, sizeof(value));
    DWORD ignored;
    return VirtualProtect(reinterpret_cast<void*>(address), sizeof(void*), protection, &ignored) != FALSE;
}

bool Printable(const std::string& value, size_t limit) {
    return !value.empty() && value.size() <= limit && std::all_of(value.begin(), value.end(),
        [](unsigned char character) { return character >= 32 && character <= 126; }) &&
        std::any_of(value.begin(), value.end(), [](unsigned char character) { return character != ' '; });
}
}

bool edmw::InstallNativeAbilities(const std::vector<NativeAbilityBinding>& bindings) {
    if (bindings.empty()) return true;
    if (!abilities.empty() || bindings.size() > 1024 ||
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
            !Printable(binding.tooltip, 512) || !checked.emplace(binding.technology, binding).second) return false;
    }
    abilities.swap(checked);
    // The launcher terminates the still-suspended owned process if either
    // protected write fails, so a partial binding can never enter gameplay.
    return WritePointer(resourceStringSlot, reinterpret_cast<const void*>(&GetResourceString)) &&
        WritePointer(commandSlot, reinterpret_cast<const void*>(&ExecuteAbility));
}
