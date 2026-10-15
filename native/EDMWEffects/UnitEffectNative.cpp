#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <memory>
#include <mutex>
#include "UnitEffectNative.h"

namespace {
constexpr uintptr_t complexUnitVtable = 0x007F6C48;
constexpr uintptr_t getterAddress = 0x006B6B35;
constexpr uintptr_t constructorAddress = 0x006AFB2B;
constexpr uintptr_t destructorAddress = 0x006AFD9A;
constexpr uintptr_t poolCheckoutAddress = 0x006C0DE9;
constexpr uintptr_t tickCallAddress = 0x005B1DF8;
constexpr uintptr_t tickFunctionAddress = 0x005B2BD2;
constexpr uint8_t getterBytes[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x3C };
constexpr uint8_t constructorBytes[] = { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0x53 };
constexpr uint8_t destructorBytes[] = { 0x53, 0x56, 0x8B, 0xF1, 0x57 };
constexpr uint8_t poolCheckoutBytes[] = { 0x53, 0x55, 0x56, 0x57, 0x8B, 0xF1 };
constexpr uint8_t tickBytes[] = { 0xE8, 0xD5, 0x0D, 0x00, 0x00 };

using SightGetter = float(__thiscall*)(void*);
using Constructor = void*(__thiscall*)(void*);
using Destructor = void(__thiscall*)(void*);
using PhysicsUpdate = int(__thiscall*)(void*);
using SightRefresh = void(__thiscall*)(void*);
SightGetter originalGetter = nullptr;
Constructor originalConstructor = nullptr;
Constructor originalPoolCheckout = nullptr;
Destructor originalDestructor = nullptr;
std::unique_ptr<edmw::UnitEffectRuntime> runtime;

struct CachedSight { uint32_t unitId; int32_t bonus; };
std::map<void*, CachedSight> cachedSight;
std::map<void*, uint64_t> generations;
uint64_t nextGeneration = 1;
uint32_t lastSimulationMilliseconds = 0;
std::set<uint32_t> relevantObjects;
std::recursive_mutex cacheMutex;

template<class T> T Read(const void* pointer, size_t offset) {
    T value;
    memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value));
    return value;
}

void Forget(void* unit) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    cachedSight.erase(unit);
    generations.erase(unit);
}

uint64_t Generation(void* unit) {
    const auto found = generations.find(unit);
    if (found != generations.end()) return found->second;
    return generations.emplace(unit, nextGeneration++).first->second;
}

void* __fastcall ConstructUnit(void* unit, void*) {
    // A new object can reuse both an old pointer and a native unit ID. Clear
    // before even the base constructor can expose this object to native code.
    Forget(unit);
    return originalConstructor(unit);
}

void __fastcall DestroyUnit(void* unit, void*) {
    // Native teardown must still see its previous sight radius when it removes
    // vision. Never clear the entry before the original destructor has run.
    originalDestructor(unit);
    Forget(unit);
}

void* __fastcall CheckoutUnit(void* pool, void*) {
    auto unit = originalPoolCheckout(pool);
    // The pool usually recycles an already constructed EEComplexUnit. Its
    // constructor is not called again, so every checkout is a new generation.
    if (unit) Forget(unit);
    return unit;
}

float __fastcall ReadSight(void* unit, void*) {
    const float base = originalGetter(unit);
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    const auto found = cachedSight.find(unit);
    if (base <= 0.0f || found == cachedSight.end() || found->second.unitId != Read<uint32_t>(unit, 0x24) ||
        Read<uint8_t>(unit, 0x627)) return base;
    // The native EELineOfSightTable bounds its radius at 32 (0x007F7C60).
    return (std::max)(1.0f, (std::min)(32.0f, base + static_cast<float>(found->second.bonus)));
}

void EvaluateUnits(void* physics) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    std::vector<edmw::UnitEffectSnapshot> units;
    std::map<uint32_t, void*> pointers;
    const uint32_t playerCount = *reinterpret_cast<const uint32_t*>(0x0099EF30);
    if (!playerCount || playerCount > 16) return;
    for (uint32_t playerIndex = 0; playerIndex < playerCount; ++playerIndex) {
        auto player = reinterpret_cast<void* const*>(0x0099EED8)[playerIndex];
        if (!player || Read<uint32_t>(player, 0x478) != playerIndex) continue;
        const auto tree = Read<void*>(player, 0xD58);
        if (!tree) continue;
        const auto civilization = Read<uint32_t>(tree, 0x16D4);
        size_t remaining = 65536;
        for (auto unit = Read<void*>(player, 0x464); unit && remaining--; unit = Read<void*>(unit, 0x0C)) {
            // Restrict this adapter to the verified EEComplexUnit instance
            // layout. Units and buildings use it; other world object classes
            // may have different layouts and are deliberately ignored.
            if (Read<uintptr_t>(unit, 0) != complexUnitVtable || Read<void*>(unit, 0x18) != player) continue;
            const auto prototype = Read<void*>(unit, 0x64);
            if (!prototype) continue;
            const auto object = Read<uint32_t>(prototype, 0x1AC);
            // Native transformations can retain the instance while changing its
            // prototype. Keep an already affected target in this snapshot so a
            // now-unrelated type removes its old radius before clearing the
            // cached bonus, rather than silently dropping its vision state.
            if (relevantObjects.find(object) == relevantObjects.end() && cachedSight.find(unit) == cachedSight.end()) continue;
            const auto id = Read<uint32_t>(unit, 0x24);
            if (id == UINT32_MAX || pointers.find(id) != pointers.end()) continue;
            const bool alive = Read<int32_t>(unit, 0x90) > 0;
            units.push_back({ id, playerIndex, civilization, object,
                Read<int32_t>(unit, 0x1C), Read<int32_t>(unit, 0x20), alive, Generation(unit) });
            if (alive) pointers.emplace(id, unit);
        }
    }
    const auto time = Read<uint32_t>(physics, 0xE8);
    lastSimulationMilliseconds = time;
    runtime->Evaluate(units, time);
    // Dropped units were removed by the native update or destructor. Do not
    // dereference those pointers; constructors also invalidate reused storage.
    for (auto it = cachedSight.begin(); it != cachedSight.end();) {
        const auto live = pointers.find(it->second.unitId);
        if (live == pointers.end() || live->second != it->first) it = cachedSight.erase(it);
        else ++it;
    }
    for (const auto& entry : pointers) {
        auto unit = entry.second;
        const auto desired = runtime->Bonus(entry.first);
        const auto cached = cachedSight.find(unit);
        const auto previous = cached == cachedSight.end() ? 0 : cached->second.bonus;
        if (previous == desired) continue;
        // This sequence is the same one used by the game's native sight stat
        // modifier (selector 4). Calls into ReadSight are reentrant on this lock.
        reinterpret_cast<SightRefresh>(0x006C29E3)(unit);
        if (desired) cachedSight[unit] = { entry.first, desired };
        else cachedSight.erase(unit);
        reinterpret_cast<SightRefresh>(0x006C2891)(unit);
    }
}

int __fastcall UpdateUnits(void* physics, void*) {
    const int result = reinterpret_cast<PhysicsUpdate>(tickFunctionAddress)(physics);
    EvaluateUnits(physics);
    return result;
}

bool WriteBranch(uintptr_t address, const void* destination, size_t size, uint8_t opcode) {
    DWORD protection;
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, PAGE_EXECUTE_READWRITE, &protection)) return false;
    std::vector<uint8_t> patch(size, 0x90);
    patch[0] = opcode;
    const auto relative = static_cast<int32_t>(reinterpret_cast<uintptr_t>(destination) - address - 5);
    memcpy(patch.data() + 1, &relative, sizeof(relative));
    memcpy(reinterpret_cast<void*>(address), patch.data(), size);
    DWORD ignored;
    const bool restored = VirtualProtect(reinterpret_cast<void*>(address), size, protection, &ignored) != FALSE;
    const bool flushed = FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size) != FALSE;
    return restored && flushed;
}

void* MakeTrampoline(uintptr_t address, const uint8_t* expected, size_t size) {
    if (memcmp(reinterpret_cast<const void*>(address), expected, size)) return nullptr;
    // All copied prologue instructions above are position independent. No
    // relative CALL, branch, or partially copied instruction is relocated.
    auto code = static_cast<uint8_t*>(VirtualAlloc(nullptr, size + 5, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!code) return nullptr;
    memcpy(code, expected, size);
    code[size] = 0xE9;
    const auto relative = static_cast<int32_t>(address + size - reinterpret_cast<uintptr_t>(code + size) - 5);
    memcpy(code + size + 1, &relative, sizeof(relative));
    DWORD ignored;
    if (!VirtualProtect(code, size + 5, PAGE_EXECUTE_READ, &ignored) ||
        !FlushInstructionCache(GetCurrentProcess(), code, size + 5)) {
        VirtualFree(code, 0, MEM_RELEASE);
        return nullptr;
    }
    return code;
}
}

bool edmw::InstallUnitEffects(const std::vector<UnitEffectRule>& rules) {
    if (rules.empty()) return true;
    if (runtime || rules.size() > 1024) return false;
    std::set<UnitEffectId> identities;
    for (const auto& rule : rules) {
        if (rule.sourceObject >= 100000 || rule.targetObject >= 100000 || rule.civilization < 4 || rule.civilization > 12 ||
            !rule.sightAmount || rule.sightAmount < -32 || rule.sightAmount > 32 ||
            std::all_of(rule.id.begin(), rule.id.end(), [](uint8_t value) { return value == 0; }) ||
            !identities.insert(rule.id).second) return false;
        if (rule.trigger == UnitEffectTrigger::Proximity) {
            if (rule.radiusTiles < 1 || rule.radiusTiles > 64) return false;
        } else if (rule.trigger == UnitEffectTrigger::Activated) {
            if (rule.radiusTiles || rule.sourceObject != rule.targetObject) return false;
            if (rule.activation == UnitEffectActivation::Toggle) {
                if (rule.durationMilliseconds) return false;
            } else if (rule.activation == UnitEffectActivation::Timed) {
                if (rule.durationMilliseconds < 1000 || rule.durationMilliseconds > 3600000) return false;
            } else return false;
        } else return false;
    }
    if (memcmp(reinterpret_cast<const void*>(getterAddress), getterBytes, sizeof(getterBytes)) ||
        memcmp(reinterpret_cast<const void*>(constructorAddress), constructorBytes, sizeof(constructorBytes)) ||
        memcmp(reinterpret_cast<const void*>(destructorAddress), destructorBytes, sizeof(destructorBytes)) ||
        memcmp(reinterpret_cast<const void*>(poolCheckoutAddress), poolCheckoutBytes, sizeof(poolCheckoutBytes)) ||
        memcmp(reinterpret_cast<const void*>(tickCallAddress), tickBytes, sizeof(tickBytes))) return false;
    originalGetter = reinterpret_cast<SightGetter>(MakeTrampoline(getterAddress, getterBytes, sizeof(getterBytes)));
    originalConstructor = reinterpret_cast<Constructor>(MakeTrampoline(constructorAddress, constructorBytes, sizeof(constructorBytes)));
    originalDestructor = reinterpret_cast<Destructor>(MakeTrampoline(destructorAddress, destructorBytes, sizeof(destructorBytes)));
    originalPoolCheckout = reinterpret_cast<Constructor>(MakeTrampoline(poolCheckoutAddress, poolCheckoutBytes, sizeof(poolCheckoutBytes)));
    if (!originalGetter || !originalConstructor || !originalDestructor || !originalPoolCheckout) return false;
    runtime = std::make_unique<edmw::UnitEffectRuntime>(rules);
    for (const auto& rule : rules) {
        relevantObjects.insert(rule.sourceObject);
        relevantObjects.insert(rule.targetObject);
    }
    // Initialization failure terminates the still-suspended owned game. Thus a
    // partial patch set can never be resumed by the launcher.
    return WriteBranch(constructorAddress, reinterpret_cast<const void*>(&ConstructUnit), sizeof(constructorBytes), 0xE9) &&
        WriteBranch(destructorAddress, reinterpret_cast<const void*>(&DestroyUnit), sizeof(destructorBytes), 0xE9) &&
        WriteBranch(poolCheckoutAddress, reinterpret_cast<const void*>(&CheckoutUnit), sizeof(poolCheckoutBytes), 0xE9) &&
        WriteBranch(getterAddress, reinterpret_cast<const void*>(&ReadSight), sizeof(getterBytes), 0xE9) &&
        WriteBranch(tickCallAddress, reinterpret_cast<const void*>(&UpdateUnits), sizeof(tickBytes), 0xE8);
}

bool edmw::ActivateUnitEffect(const UnitEffectId& effect, void* actor, uint32_t expectedPlayer) {
    if (!runtime || !actor || expectedPlayer > 15 || Read<uintptr_t>(actor, 0) != complexUnitVtable ||
        Read<int32_t>(actor, 0x90) <= 0) return false;
    const auto owner = Read<void*>(actor, 0x18);
    const auto prototype = Read<void*>(actor, 0x64);
    if (!owner || !prototype || Read<uint32_t>(owner, 0x478) != expectedPlayer) return false;
    const auto tree = Read<void*>(owner, 0xD58);
    if (!tree) return false;
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    const UnitEffectSnapshot snapshot{ Read<uint32_t>(actor, 0x24), expectedPlayer, Read<uint32_t>(tree, 0x16D4),
        Read<uint32_t>(prototype, 0x1AC), Read<int32_t>(actor, 0x1C), Read<int32_t>(actor, 0x20), true, Generation(actor) };
    return runtime->Activate(effect, snapshot, lastSimulationMilliseconds);
}
