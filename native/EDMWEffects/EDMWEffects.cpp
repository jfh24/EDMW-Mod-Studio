#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <algorithm>
#include "EffectRules.h"
#include "UnitEffectNative.h"
#include "NativeAbilityBindings.h"

namespace {
std::vector<edmw::EffectRule> rules;
HANDLE trace = INVALID_HANDLE_VALUE;
bool initialized = false;
using Event = int(__cdecl*)(int, int, int, void*, void*);
using Grant = void(__thiscall*)(void*, uint32_t, uint32_t);
constexpr uintptr_t eventAddress = 0x0066987F;
constexpr uintptr_t grantAddress = 0x005C8694;
constexpr uintptr_t hookAddress = 0x006736FB;
constexpr uint8_t callBytes[] = { 0xE8, 0x7F, 0x61, 0xFF, 0xFF };

template<class T> T Read(const void* p, size_t offset) {
    T value;
    memcpy(&value, static_cast<const uint8_t*>(p) + offset, sizeof(value));
    return value;
}

int __cdecl ResearchEvent(int eventId, int playerIndex, int technology, void* player, void* actor, void* node) {
    const int result = reinterpret_cast<Event>(eventAddress)(eventId, playerIndex, technology, player, actor);
    // This call site is reached after the native completed flag is set. The
    // native early-out prevents another call for already completed research.
    const auto definition = Read<void*>(node, 8);
    const auto tree = Read<void*>(node, 0x24);
    if (!definition || !tree || !Read<uint8_t>(node, 4)) return result;
    const auto kind = Read<uint32_t>(definition, 0xA8);
    const auto civilization = Read<uint32_t>(tree, 0x16D4);
    const bool restoring = *reinterpret_cast<const uint8_t*>(0x0099FC7C) != 0;
    if (!player) player = Read<void*>(tree, 0x16D0);
    if (!player || playerIndex < 0 || playerIndex > 15 || Read<int>(player, 0x478) != playerIndex) return result;
    for (const auto& rule : rules) {
        if (!edmw::Matches(rule, static_cast<uint32_t>(technology), civilization, kind, restoring)) continue;
        const auto current = Read<uint32_t>(player, 0xEBC + rule.resource * 4);
        if (current > INT32_MAX) continue;
        const auto amount = (std::min)(rule.amount, static_cast<uint32_t>(INT32_MAX) - current);
        reinterpret_cast<Grant>(grantAddress)(player, rule.resource, amount);
        char line[160];
        const int length = sprintf_s(line, "grant player=%d civilization=%u technology=%d resource=%u amount=%u before=%u after=%u\r\n",
            playerIndex, civilization, technology, rule.resource, amount, current, Read<uint32_t>(player, 0xEBC + rule.resource * 4));
        DWORD written;
        if (trace != INVALID_HANDLE_VALUE && length > 0) WriteFile(trace, line, static_cast<DWORD>(length), &written, nullptr);
    }
    return result;
}

__declspec(naked) void ResearchHook() {
    __asm {
        push ebp
        mov ebp, esp
        push esi
        push dword ptr [ebp + 24]
        push dword ptr [ebp + 20]
        push dword ptr [ebp + 16]
        push dword ptr [ebp + 12]
        push dword ptr [ebp + 8]
        call ResearchEvent
        add esp, 24
        pop ebp
        ret
    }
}
}

// Invoked by the owned game's launcher thread, outside DllMain/loader lock.
extern "C" DWORD WINAPI InitializeEffects(void* configuration) {
    if (initialized || reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)) != 0x00400000 || !configuration) return 0;
    if (memcmp(reinterpret_cast<void*>(hookAddress), callBytes, sizeof(callBytes))) return 0;
    constexpr uint8_t grantBytes[] = { 0x8B,0x54,0x24,0x08,0x8B,0x44,0x24,0x04,0x01,0x94,0x81,0xBC,0x0E,0x00,0x00 };
    if (memcmp(reinterpret_cast<void*>(grantAddress), grantBytes, sizeof(grantBytes))) return 0;
    const auto path = static_cast<const wchar_t*>(configuration);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 0;
    const DWORD size = GetFileSize(file, nullptr);
    if (size < 12 || size > 16 + 1024 * 96) { CloseHandle(file); return 0; }
    std::vector<uint8_t> bytes(size);
    DWORD read = 0;
    const bool loaded = ReadFile(file, bytes.data(), size, &read, nullptr) && read == size;
    CloseHandle(file);
    std::vector<edmw::UnitEffectRule> unitRules;
    std::vector<edmw::NativeAbilityBinding> abilities;
    if (!loaded || !edmw::ReadRuntimeRules(bytes, rules, unitRules, abilities)) return 0;
    wchar_t logPath[32768];
    if (swprintf_s(logPath, L"%s.log", path) > 0)
        trace = CreateFileW(logPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    DWORD oldProtect;
    if (!VirtualProtect(reinterpret_cast<void*>(hookAddress), 5, PAGE_EXECUTE_READWRITE, &oldProtect)) return 0;
    uint8_t patch[5] = { 0xE8 };
    const auto displacement = static_cast<int32_t>(reinterpret_cast<uintptr_t>(&ResearchHook) - hookAddress - 5);
    memcpy(patch + 1, &displacement, 4);
    memcpy(reinterpret_cast<void*>(hookAddress), patch, 5);
    DWORD ignored;
    if (!VirtualProtect(reinterpret_cast<void*>(hookAddress), 5, oldProtect, &ignored) ||
        !FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(hookAddress), 5)) return 0;
    if (!edmw::InstallUnitEffects(unitRules) || !edmw::InstallNativeAbilities(abilities)) return 0;
    initialized = true;
    if (trace != INVALID_HANDLE_VALUE) {
        constexpr char message[] = "EDMW runtime effects initialized (prototype)\r\n";
        DWORD written;
        WriteFile(trace, message, sizeof(message) - 1, &written, nullptr);
    }
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
