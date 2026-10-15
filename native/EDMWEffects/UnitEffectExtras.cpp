#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <map>
#include <mutex>
#include "UnitEffectExtras.h"
#include "UnitEffectNative.h"

namespace {
std::recursive_mutex extrasMutex;
struct RegenerationState { uint32_t id; edmw::UnitEffectRegeneration clock; };
std::map<void*, RegenerationState> regeneration;
std::map<void*, uint32_t> accuracySequence;
struct OwnerContribution { uint32_t id; void* owner; int64_t cost, capacity; };
struct OwnerTotals { int64_t cost = 0, capacity = 0; edmw::UnitEffectCounterContribution counter; };
std::map<void*, OwnerContribution> ownerContributions;
std::map<void*, OwnerTotals> ownerTotals;
std::set<void*> visitedUnits;
uint32_t lastSimulationMilliseconds = 0;
thread_local uint32_t damageDepth = 0;
using HealthGetter = int32_t(__thiscall*)(void*);
using HealthSetter = void(__thiscall*)(void*, int32_t, bool);
using DamageHandler = void(__thiscall*)(void*, void*, int32_t, uint32_t, bool, bool);
DamageHandler originalDamage = nullptr;
constexpr uintptr_t damageAddress = 0x006F4043;
constexpr uint8_t damageBytes[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x2C };
using PopulationCapacityGetter = int32_t(__thiscall*)(void*, bool);
PopulationCapacityGetter originalPopulationCapacity = nullptr;
constexpr uintptr_t populationCapacityAddress = 0x005CCC94;
constexpr uint8_t populationCapacityBytes[] = { 0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x08, 0x0D, 0x00, 0x00 };
template<class T> T Read(const void* pointer, size_t offset) {
    T value{};
    memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value));
    return value;
}

template<class T> void Put(void* pointer, size_t offset, T value) {
    memcpy(static_cast<uint8_t*>(pointer) + offset, &value, sizeof(value));
}

bool CurrentPlayer(void* player) {
    if (!player) return false;
    const auto count = *reinterpret_cast<const uint32_t*>(0x0099EF30);
    if (count > 16) return false;
    for (uint32_t index = 0; index < count; ++index)
        if (reinterpret_cast<void* const*>(0x0099EED8)[index] == player && Read<uint32_t>(player, 0x478) == index) return true;
    return false;
}

void SyncOwnerCost(void* owner) {
    const auto found = ownerTotals.find(owner);
    if (found == ownerTotals.end()) return;
    if (CurrentPlayer(owner)) {
        const auto previous = Read<int32_t>(owner, 0xF14);
        const auto next = found->second.counter.Update(previous, found->second.cost);
        if (next != previous) Put(owner, 0xF14, next);
    }
    if (!found->second.cost && !found->second.capacity) ownerTotals.erase(found);
}

void ForgetOwnerContribution(void* unit) {
    const auto found = ownerContributions.find(unit);
    if (found == ownerContributions.end()) return;
    const auto owner = found->second.owner;
    const auto totals = ownerTotals.find(owner);
    if (totals != ownerTotals.end()) {
        totals->second.cost -= found->second.cost;
        totals->second.capacity -= found->second.capacity;
    }
    ownerContributions.erase(found);
    SyncOwnerCost(owner);
}

void UpdateOwnerContribution(void* unit, uint32_t id, const edmw::UnitEffectRuntime& runtime) {
    const auto owner = Read<void*>(unit, 0x18);
    const auto stats = Read<void*>(unit, 0x6C);
    if (!CurrentPlayer(owner) || !stats) { ForgetOwnerContribution(unit); return; }
    const auto baseCost = (std::max)(0, Read<int32_t>(stats, 0x19C));
    const auto desiredCost = (std::max)(0.0, static_cast<double>(baseCost) + runtime.Bonus(id, edmw::UnitEffectStat::Population));
    const auto cost = static_cast<int64_t>(std::round(desiredCost)) - baseCost;
    const auto capacity = static_cast<int64_t>(std::round(runtime.Bonus(id, edmw::UnitEffectStat::PopulationProvided)));
    const auto found = ownerContributions.find(unit);
    if (found != ownerContributions.end() && found->second.id == id && found->second.owner == owner &&
        found->second.cost == cost && found->second.capacity == capacity) return;
    ForgetOwnerContribution(unit);
    if (!cost && !capacity) return;
    ownerContributions[unit] = { id, owner, cost, capacity };
    auto& totals = ownerTotals[owner];
    totals.cost += cost;
    totals.capacity += capacity;
    SyncOwnerCost(owner);
}

int32_t __fastcall ReadPopulationCapacity(void* owner, void*, bool all) {
    const auto original = originalPopulationCapacity(owner, all);
    std::lock_guard<std::recursive_mutex> lock(extrasMutex);
    const auto found = ownerTotals.find(owner);
    if (found == ownerTotals.end()) return original;
    return static_cast<int32_t>((std::max)(int64_t{ 0 }, (std::min)(int64_t{ 2147483647 },
        static_cast<int64_t>(original) + found->second.capacity)));
}

void* ComplexUnit(void* object) {
    if (!object) return nullptr;
    constexpr uintptr_t complexVtable = 0x007F6C48;
    if (Read<uintptr_t>(object, 0) == complexVtable) return object;
    const auto complex = Read<void*>(object, 0x68);
    return complex && Read<uintptr_t>(complex, 0) == complexVtable ? complex : nullptr;
}

void __fastcall ReceiveDamage(void* target, void*, void* attacker, int32_t amount,
    uint32_t player, bool flag1, bool flag2) {
    struct DamageScope {
        DamageScope() { ++damageDepth; }
        ~DamageScope() { --damageDepth; }
    } scope;
    if (damageDepth == 1 && attacker && amount > 0) {
        auto actor = ComplexUnit(attacker);
        auto accuracy = edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::Accuracy);
        if (accuracy && actor) {
            // Query the core before taking the extras lock. Simulation updates
            // hold the core lock first; retain that lock order on damage calls.
            std::lock_guard<std::recursive_mutex> lock(extrasMutex);
            if (!edmw::UnitEffectAccuracyHit(accuracy, Read<uint32_t>(actor, 0x24),
                Read<uint32_t>(target, 0x24), lastSimulationMilliseconds, ++accuracySequence[actor])) return;
        }
    }
    originalDamage(target, attacker, amount, player, flag1, flag2);
}

void* InstallHook(uintptr_t address, const uint8_t* expected, size_t size, const void* handler) {
    if (memcmp(reinterpret_cast<const void*>(address), expected, size)) return nullptr;
    const auto trampoline = static_cast<uint8_t*>(VirtualAlloc(nullptr, size + 5,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!trampoline) return nullptr;
    memcpy(trampoline, expected, size);
    trampoline[size] = 0xE9;
    const auto back = static_cast<int32_t>(address + size - reinterpret_cast<uintptr_t>(trampoline + size) - 5);
    memcpy(trampoline + size + 1, &back, sizeof(back));
    DWORD previous = 0;
    if (!VirtualProtect(trampoline, size + 5, PAGE_EXECUTE_READ, &previous) ||
        !FlushInstructionCache(GetCurrentProcess(), trampoline, size + 5)) {
        VirtualFree(trampoline, 0, MEM_RELEASE);
        return nullptr;
    }
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, PAGE_EXECUTE_READWRITE, &previous)) return nullptr;
    std::vector<uint8_t> patch(size, 0x90);
    patch[0] = 0xE9;
    const auto branch = static_cast<int32_t>(reinterpret_cast<uintptr_t>(handler) - address - 5);
    memcpy(patch.data() + 1, &branch, sizeof(branch));
    memcpy(reinterpret_cast<void*>(address), patch.data(), size);
    DWORD ignored = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), size, previous, &ignored) ||
        !FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), size)) return nullptr;
    return trampoline;
}
}

void edmw::UpdateAdditionalUnitEffects(void* unit, uint32_t id, uint32_t simulationMilliseconds,
    const UnitEffectRuntime& runtime) {
    std::lock_guard<std::recursive_mutex> lock(extrasMutex);
    visitedUnits.insert(unit);
    lastSimulationMilliseconds = simulationMilliseconds;
    UpdateOwnerContribution(unit, id, runtime);
    // Called under the adapter's simulation/cache lock, for a validated live unit.
    const auto rate = runtime.Bonus(id, UnitEffectStat::HealthRegeneration);
    auto found = regeneration.find(unit);
    if (!rate && found == regeneration.end()) return;
    if (found == regeneration.end() || found->second.id != id) {
        regeneration[unit] = { id, {} };
        found = regeneration.find(unit);
    }
    const auto current = Read<int32_t>(unit, 0x90);
    // Native maximum includes research and the unit's own maximum-health bonus.
    const auto maximum = reinterpret_cast<HealthGetter>(0x006C2948)(unit);
    const auto next = found->second.clock.Update(simulationMilliseconds, current, maximum, rate);
    if (next != current) reinterpret_cast<HealthSetter>(0x006F3A00)(unit, next, true);
    if (!rate) regeneration.erase(unit);
}

void edmw::BeginAdditionalUnitEffectFrame() {
    std::lock_guard<std::recursive_mutex> lock(extrasMutex);
    visitedUnits.clear();
}

void edmw::EndAdditionalUnitEffectFrame() {
    std::lock_guard<std::recursive_mutex> lock(extrasMutex);
    // Corpses can remain allocated for a long time. Remove their contributions
    // when they leave the live snapshot, without waiting for their destructor.
    for (auto it = ownerContributions.begin(); it != ownerContributions.end();) {
        const auto unit = it->first;
        ++it;
        if (visitedUnits.find(unit) == visitedUnits.end()) ForgetOwnerContribution(unit);
    }
    for (auto it = regeneration.begin(); it != regeneration.end();)
        if (visitedUnits.find(it->first) == visitedUnits.end()) it = regeneration.erase(it); else ++it;
    for (auto it = accuracySequence.begin(); it != accuracySequence.end();)
        if (visitedUnits.find(it->first) == visitedUnits.end()) it = accuracySequence.erase(it); else ++it;
}

void edmw::ForgetAdditionalUnitEffects(void* unit) {
    std::lock_guard<std::recursive_mutex> lock(extrasMutex);
    ForgetOwnerContribution(unit);
    regeneration.erase(unit);
    accuracySequence.erase(unit);
}

bool edmw::ValidateAdditionalUnitEffectBindings() {
    constexpr uint8_t maximumHealthBytes[] = { 0x8B, 0x91, 0xA0, 0x00, 0x00, 0x00 };
    constexpr uint8_t setHealthBytes[] = { 0x55, 0x8B, 0xEC, 0x51, 0x53 };
    return !memcmp(reinterpret_cast<const void*>(0x006C2948), maximumHealthBytes, sizeof(maximumHealthBytes)) &&
        !memcmp(reinterpret_cast<const void*>(0x006F3A00), setHealthBytes, sizeof(setHealthBytes));
}

bool edmw::InstallAdditionalUnitEffects(const std::vector<UnitEffectRule>& rules) {
    const auto contains = [&](UnitEffectStat stat) {
        return std::any_of(rules.begin(), rules.end(), [&](const auto& rule) {
            const auto outputs = UnitEffectOutputs(rule);
            return std::any_of(outputs.begin(), outputs.end(), [&](const auto& output) { return output.stat == stat; });
        });
    };
    if (contains(UnitEffectStat::Accuracy)) {
        if (originalDamage) return false;
        originalDamage = reinterpret_cast<DamageHandler>(InstallHook(damageAddress, damageBytes, sizeof(damageBytes), reinterpret_cast<const void*>(&ReceiveDamage)));
        if (!originalDamage) return false;
    }
    if (contains(UnitEffectStat::PopulationProvided)) {
        if (originalPopulationCapacity) return false;
        originalPopulationCapacity = reinterpret_cast<PopulationCapacityGetter>(InstallHook(populationCapacityAddress,
            populationCapacityBytes, sizeof(populationCapacityBytes), reinterpret_cast<const void*>(&ReadPopulationCapacity)));
        if (!originalPopulationCapacity) return false;
    }
    return true;
}
