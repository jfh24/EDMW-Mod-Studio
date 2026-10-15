#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <memory>
#include <mutex>
#include "UnitEffectNative.h"
#include "UnitStatProjection.h"
#include "UnitEffectExtras.h"
#include "UnitEffectProduction.h"
#include "EffectTrace.h"

namespace {
constexpr uintptr_t complexUnitVtable = 0x007F6C48;
constexpr uintptr_t getterAddress = 0x006B6B35;
constexpr uintptr_t movementGetterAddress = 0x006B6B80;
constexpr uintptr_t displayMovementGetterAddress = 0x006B6CCE;
constexpr uintptr_t constructorAddress = 0x006AFB2B;
constexpr uintptr_t destructorAddress = 0x006AFD9A;
constexpr uintptr_t poolCheckoutAddress = 0x006C0DE9;
constexpr uintptr_t tickCallAddress = 0x005B1DF8;
constexpr uintptr_t tickFunctionAddress = 0x005B2BD2;
constexpr uintptr_t scalarModifierAddress = 0x006676F9;
constexpr uintptr_t sharedModifierAddress = 0x0066A5B9;
constexpr uint8_t getterBytes[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x3C };
constexpr uint8_t movementGetterBytes[] = { 0x55, 0x8B, 0xEC, 0x51, 0x53 };
constexpr uint8_t displayMovementGetterBytes[] = { 0x55, 0x8B, 0xEC, 0x51, 0xD9, 0x81, 0xB8, 0x04, 0x00, 0x00 };
constexpr uint8_t constructorBytes[] = { 0x55, 0x8B, 0xEC, 0x51, 0x51, 0x53 };
constexpr uint8_t destructorBytes[] = { 0x53, 0x56, 0x8B, 0xF1, 0x57 };
constexpr uint8_t poolCheckoutBytes[] = { 0x53, 0x55, 0x56, 0x57, 0x8B, 0xF1 };
constexpr uint8_t tickBytes[] = { 0xE8, 0xD5, 0x0D, 0x00, 0x00 };
constexpr uint8_t scalarModifierBytes[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x3C };
constexpr uint8_t sharedModifierBytes[] = { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0xA8, 0x00, 0x00, 0x00 };

using SightGetter = float(__thiscall*)(void*);
using Constructor = void*(__thiscall*)(void*);
using Destructor = void(__thiscall*)(void*);
using PhysicsUpdate = int(__thiscall*)(void*);
using SightRefresh = void(__thiscall*)(void*);
using ScalarModifier = void(__cdecl*)(int, int, void*, int, int, int, int);
using SharedModifier = int(__cdecl*)(void*, int, void*, void*, void*);
SightGetter originalGetter = nullptr;
SightGetter originalMovementGetter = nullptr;
SightGetter originalDisplayMovementGetter = nullptr;
Constructor originalConstructor = nullptr;
Constructor originalPoolCheckout = nullptr;
Destructor originalDestructor = nullptr;
ScalarModifier originalScalarModifier = nullptr;
SharedModifier originalSharedModifier = nullptr;
uint32_t sharedModifierDepth = 0;
std::unique_ptr<edmw::UnitEffectRuntime> runtime;

struct CachedSight { uint32_t unitId; float bonus; };
std::map<void*, CachedSight> cachedSight;
std::map<void*, CachedSight> cachedMovement;
struct ProjectedUnit {
    void* nativePrototype = nullptr;
    void* nativeStatistics = nullptr;
    edmw::UnitStatProjection values;
};
std::map<void*, std::unique_ptr<ProjectedUnit>> projectedUnits;
std::map<void*, edmw::ReplacedFloatOverlay> modelScales;
std::map<void*, edmw::ConsumedIntegerOverlay> resourceAmounts;
std::map<void*, edmw::ProportionalHealth> healthFractions;
std::map<void*, uint64_t> generations;
std::map<void*, uint32_t> unitIdentities;
uint64_t nextGeneration = 1;
uint32_t lastSimulationMilliseconds = 0;
std::set<uint32_t> relevantObjects;
std::recursive_mutex cacheMutex;

template<class T> T Read(const void* pointer, size_t offset) {
    T value;
    memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value));
    return value;
}
template<class T> void Put(void* pointer, size_t offset, T value) {
    memcpy(static_cast<uint8_t*>(pointer) + offset, &value, sizeof(value));
}
int32_t MaximumHealth(void* unit) {
    using Getter = int32_t(__thiscall*)(void*);
    return reinterpret_cast<Getter>(0x006C2948)(unit);
}
void RefreshHealth(void* unit, int32_t previousMaximum) {
    const auto health = Read<int32_t>(unit, 0x90);
    if (health <= 0 || previousMaximum <= 0) return;
    const auto maximum = MaximumHealth(unit);
    if (maximum <= 0 || maximum == previousMaximum) return;
    const auto next = healthFractions[unit].Resize(health, previousMaximum, maximum);
    using Setter = void(__thiscall*)(void*, int32_t, bool);
    if (next != health) reinterpret_cast<Setter>(0x006F3A00)(unit, next, true);
}

void RefreshGeometry(void* unit, bool remove) {
    if (Read<int32_t>(unit, 0x90) <= 0 || Read<int32_t>(unit, 0x1C) == -10000 || Read<int32_t>(unit, 0x20) == -10000) return;
    const auto prototype = Read<void*>(unit, 0x64);
    if (!prototype || !Read<uint32_t>(prototype, 0x284)) return;
    const auto table = Read<uintptr_t*>(prototype, 0);
    using Callback = void(__thiscall*)(void*, void*);
    if (remove) {
        reinterpret_cast<Callback>(table[0xC4 / 4])(prototype, unit);
        reinterpret_cast<Callback>(table[0xC8 / 4])(prototype, unit);
    } else {
        reinterpret_cast<Callback>(table[0xA8 / 4])(prototype, unit);
        reinterpret_cast<Callback>(table[0xA4 / 4])(prototype, unit);
    }
}

void RestoreProjection(void* unit, bool refreshGeometry = true, bool refreshHealth = true) {
    const auto found = projectedUnits.find(unit);
    if (found == projectedUnits.end()) return;
    const auto& projection = *found->second;
    const bool prototypeOwned = Read<void*>(unit, 0x64) == projection.values.prototype.data();
    const bool statisticsOwned = Read<void*>(unit, 0x6C) == projection.values.statistics.data();
    if (!prototypeOwned && !statisticsOwned) return;
    const auto previousMaximum = refreshHealth && Read<int32_t>(unit, 0x90) > 0 ? MaximumHealth(unit) : 0;
    const bool geometry = refreshGeometry && prototypeOwned &&
        Read<uint32_t>(projection.values.prototype.data(), 0x284) != Read<uint32_t>(projection.nativePrototype, 0x284);
    if (geometry) RefreshGeometry(unit, true);
    if (prototypeOwned) Put(unit, 0x64, projection.nativePrototype);
    if (statisticsOwned) Put(unit, 0x6C, projection.nativeStatistics);
    if (geometry) RefreshGeometry(unit, false);
    if (previousMaximum) RefreshHealth(unit, previousMaximum);
}

void ProjectStatistics(void* unit, uint32_t id, bool traceApplication = false) {
    // Unit +0x64 is borrowed. Only resolve the shared stats from the canonical
    // owner table; a previous per-instance projection must never become a base.
    auto prototype = Read<void*>(unit, 0x64);
    const auto existing = projectedUnits.find(unit);
    if (existing != projectedUnits.end() && prototype == existing->second->values.prototype.data())
        prototype = existing->second->nativePrototype;
    const auto owner = Read<void*>(unit, 0x18);
    if (!prototype || !owner) {
        if (traceApplication) edmw::TraceEffect("effect-project unit=%u skipped=missing-owner-or-prototype", id);
        return;
    }
    const auto object = Read<uint32_t>(prototype, 0x1AC);
    const auto first = Read<void**>(owner, 0xAC8);
    const auto end = Read<void**>(owner, 0xACC);
    if (!first || !end || end < first || static_cast<size_t>(end - first) > 100000 || object >= static_cast<size_t>(end - first)) {
        if (traceApplication) edmw::TraceEffect("effect-project unit=%u object=%u skipped=invalid-canonical-table", id, object);
        return;
    }
    auto statistics = first[object];
    if (!statistics) {
        if (traceApplication) edmw::TraceEffect("effect-project unit=%u object=%u skipped=missing-canonical-statistics", id, object);
        return;
    }
    const auto previousMaximum = MaximumHealth(unit);
    auto candidate = std::make_unique<ProjectedUnit>();
    candidate->nativePrototype = prototype;
    candidate->nativeStatistics = statistics;
    candidate->values.Refresh(prototype, statistics);
    bool changed = false;
    for (uint32_t value = 0; value <= 50; ++value) {
        const auto stat = static_cast<edmw::UnitEffectStat>(value);
        const auto amount = runtime->Bonus(id, stat);
        if (amount && candidate->values.Apply(stat, amount)) changed = true;
    }
    if (!changed) {
        RestoreProjection(unit);
        projectedUnits.erase(unit);
        return;
    }
    // Native destructors own the canonical table, never these snapshots. All
    // nested pointers stay borrowed and no native destructor runs on our bytes.
    const bool geometry = Read<uint32_t>(Read<void*>(unit, 0x64), 0x284) !=
        Read<uint32_t>(candidate->values.prototype.data(), 0x284);
    if (geometry) RefreshGeometry(unit, true);
    RestoreProjection(unit, false, false);
    auto& slot = projectedUnits[unit];
    slot = std::move(candidate);
    Put(slot->values.statistics.data(), 0xB4, static_cast<void*>(slot->values.prototype.data()));
    Put(unit, 0x64, static_cast<void*>(slot->values.prototype.data()));
    Put(unit, 0x6C, static_cast<void*>(slot->values.statistics.data()));
    if (geometry) RefreshGeometry(unit, false);
    RefreshHealth(unit, previousMaximum);
    if (traceApplication)
        edmw::TraceEffect("effect-project unit=%u object=%u health-bonus=%.6g previous-max=%d actual-max=%d current-health=%d prototype-health=%d statistics-health=%d",
            id, object, runtime->Bonus(id, edmw::UnitEffectStat::Health), previousMaximum, MaximumHealth(unit),
            Read<int32_t>(unit, 0x90), Read<int32_t>(Read<void*>(unit, 0x64), 0x254), Read<int32_t>(Read<void*>(unit, 0x6C), 0x184));
}

void UpdateModelScale(void* unit, uint32_t id) {
    const auto amount = runtime->Bonus(id, edmw::UnitEffectStat::ModelScale);
    auto found = modelScales.find(unit);
    if (!amount && found == modelScales.end()) return;
    const bool starting = found == modelScales.end();
    if (found == modelScales.end()) found = modelScales.emplace(unit, edmw::ReplacedFloatOverlay{}).first;
    const auto previous = Read<float>(unit, 0x348);
    const auto next = found->second.Update(previous, amount);
    Put(unit, 0x348, next);
    if (starting || !amount)
        edmw::TraceEffect("effect-model-scale unit=%u amount=%.6g before=%.6g after=%.6g",
            id, amount, previous, next);
    if (!amount) modelScales.erase(found);
}

void UpdateResourceAmount(void* unit, uint32_t id) {
    const auto amount = runtime->Bonus(id, edmw::UnitEffectStat::ResourceAmount);
    auto found = resourceAmounts.find(unit);
    if (!amount && found == resourceAmounts.end()) return;
    if (found == resourceAmounts.end()) found = resourceAmounts.emplace(unit, edmw::ConsumedIntegerOverlay{}).first;
    Put(unit, 0x94, found->second.Update(Read<int32_t>(unit, 0x94), amount));
    if (!amount) resourceAmounts.erase(found);
}

void __cdecl ModifyNativeScalar(int selector, int percent, void* actor, int amount, int randomAmount, int random, int operation) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    auto model = modelScales.find(actor);
    if (model != modelScales.end()) Put(actor, 0x348, model->second.Update(Read<float>(actor, 0x348), 0));
    originalScalarModifier(selector, percent, actor, amount, randomAmount, random, operation);
    if (modelScales.find(actor) != modelScales.end()) UpdateModelScale(actor, Read<uint32_t>(actor, 0x24));
}

int __cdecl ModifySharedStatistics(void* definition, int player, void* owner, void* selection, void* actor) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    if (sharedModifierDepth++) {
        const auto result = originalSharedModifier(definition, player, owner, selection, actor);
        --sharedModifierDepth;
        return result;
    }
    std::vector<void*> projections, models, resources;
    for (const auto& entry : projectedUnits) projections.push_back(entry.first);
    for (const auto unit : projections) RestoreProjection(unit);
    for (auto& entry : modelScales) {
        models.push_back(entry.first);
        Put(entry.first, 0x348, entry.second.Update(Read<float>(entry.first, 0x348), 0));
    }
    for (auto& entry : resourceAmounts) {
        resources.push_back(entry.first);
        Put(entry.first, 0x94, entry.second.Suspend(Read<int32_t>(entry.first, 0x94)));
    }
    // Native research and availability bookkeeping must always see the owned
    // canonical records, including native per-unit health and geometry refresh.
    // 0x0066B2FA returns a status in EAX; caller 0x006699F0 forwards it.
    // Reapplying overlays must never replace that native result.
    const auto result = originalSharedModifier(definition, player, owner, selection, actor);
    for (const auto unit : projections)
        if (projectedUnits.find(unit) != projectedUnits.end() && Read<int32_t>(unit, 0x90) > 0)
            ProjectStatistics(unit, Read<uint32_t>(unit, 0x24));
    for (const auto unit : models)
        if (modelScales.find(unit) != modelScales.end()) UpdateModelScale(unit, Read<uint32_t>(unit, 0x24));
    for (const auto unit : resources)
        if (const auto resource = resourceAmounts.find(unit); resource != resourceAmounts.end())
            Put(unit, 0x94, resource->second.Resume(Read<int32_t>(unit, 0x94),
                runtime->Bonus(Read<uint32_t>(unit, 0x24), edmw::UnitEffectStat::ResourceAmount)));
    --sharedModifierDepth;
    return result;
}

void Forget(void* unit) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    const auto identity = unitIdentities.find(unit);
    if (identity != unitIdentities.end()) {
        if (runtime) runtime->ForgetUnit(identity->second);
        unitIdentities.erase(identity);
    }
    RestoreProjection(unit);
    projectedUnits.erase(unit);
    const auto model = modelScales.find(unit);
    if (model != modelScales.end()) {
        Put(unit, 0x348, model->second.Update(Read<float>(unit, 0x348), 0));
        modelScales.erase(model);
    }
    if (const auto resource = resourceAmounts.find(unit); resource != resourceAmounts.end()) {
        if (Read<int32_t>(unit, 0x90) > 0) Put(unit, 0x94, resource->second.Update(Read<int32_t>(unit, 0x94), 0));
        resourceAmounts.erase(resource);
    }
    healthFractions.erase(unit);
    edmw::ForgetAdditionalUnitEffects(unit);
    edmw::ForgetProductionEffects(unit);
    cachedSight.erase(unit);
    cachedMovement.erase(unit);
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
    {
        const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
        // Re-register canonical geometry before native teardown so callbacks
        // release its owned records; cached sight still retains the old radius.
        RestoreProjection(unit);
    }
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

float AdjustMovement(void* unit, float base) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    const auto found = cachedMovement.find(unit);
    // Original getters incorporate movement status, transports and native speed
    // multipliers. An immobile/disabled native unit must remain immobile.
    if (base <= 0.0f || found == cachedMovement.end() ||
        found->second.unitId != Read<uint32_t>(unit, 0x24) || Read<int32_t>(unit, 0x90) <= 0) return base;
    return edmw::ApplyMovementAmount(base, found->second.bonus);
}

float __fastcall ReadMovement(void* unit, void*) {
    return AdjustMovement(unit, originalMovementGetter(unit));
}

float __fastcall ReadDisplayMovement(void* unit, void*) {
    return AdjustMovement(unit, originalDisplayMovementGetter(unit));
}

void EvaluateUnits(void* physics) {
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    edmw::BeginAdditionalUnitEffectFrame();
    std::vector<edmw::UnitEffectSnapshot> units;
    std::map<uint32_t, void*> pointers;
    const uint32_t playerCount = *reinterpret_cast<const uint32_t*>(0x0099EF30);
    if (!playerCount || playerCount > 16) { edmw::EndAdditionalUnitEffectFrame(); return; }
    for (uint32_t playerIndex = 0; playerIndex < playerCount; ++playerIndex) {
        auto player = reinterpret_cast<void* const*>(0x0099EED8)[playerIndex];
        if (!player || Read<uint32_t>(player, 0x478) != playerIndex) continue;
        const auto tree = Read<void*>(player, 0xD58);
        if (!tree) continue;
        const auto civilization = edmw::NativeCivilizationToWireId(Read<uint32_t>(tree, 0x16D4));
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
            if (relevantObjects.find(object) == relevantObjects.end() && cachedSight.find(unit) == cachedSight.end() &&
                cachedMovement.find(unit) == cachedMovement.end() && projectedUnits.find(unit) == projectedUnits.end() &&
                modelScales.find(unit) == modelScales.end() && resourceAmounts.find(unit) == resourceAmounts.end() &&
                !edmw::HasProductionEffects(unit)) continue;
            const auto id = Read<uint32_t>(unit, 0x24);
            if (id == UINT32_MAX || pointers.find(id) != pointers.end()) continue;
            const bool alive = Read<int32_t>(unit, 0x90) > 0;
            unitIdentities[unit] = id;
            if (!alive) { Forget(unit); continue; }
            units.push_back({ id, playerIndex, civilization, object,
                Read<int32_t>(unit, 0x1C), Read<int32_t>(unit, 0x20), alive, Generation(unit) });
            if (alive) pointers.emplace(id, unit);
        }
    }
    const auto time = Read<uint32_t>(physics, 0xE8);
    lastSimulationMilliseconds = time;
    const auto changes = runtime->Evaluate(units, time);
    std::set<uint32_t> changedUnits;
    for (const auto& change : changes) {
        changedUnits.insert(change.unit);
        edmw::TraceEffect("effect-change unit=%u stat=%u before=%.6g after=%.6g simulation-ms=%u",
            change.unit, static_cast<uint32_t>(change.stat), change.previous, change.next, time);
    }
    // Dropped units were removed by the native update or destructor. Do not
    // dereference those pointers; constructors also invalidate reused storage.
    for (auto it = cachedSight.begin(); it != cachedSight.end();) {
        const auto live = pointers.find(it->second.unitId);
        if (live == pointers.end() || live->second != it->first) it = cachedSight.erase(it);
        else ++it;
    }
    for (auto it = cachedMovement.begin(); it != cachedMovement.end();) {
        const auto live = pointers.find(it->second.unitId);
        if (live == pointers.end() || live->second != it->first) it = cachedMovement.erase(it);
        else ++it;
    }
    for (const auto& entry : pointers) {
        auto unit = entry.second;
        const auto identity = unitIdentities.find(unit);
        if (identity == unitIdentities.end() || identity->second != entry.first) continue;
        const bool traceApplication = changedUnits.find(entry.first) != changedUnits.end();
        ProjectStatistics(unit, entry.first, traceApplication);
        UpdateModelScale(unit, entry.first);
        UpdateResourceAmount(unit, entry.first);
        const auto movement = runtime->Bonus(entry.first, edmw::UnitEffectStat::MovementSpeed);
        if (movement) cachedMovement[unit] = { entry.first, movement };
        else cachedMovement.erase(unit);
        const auto desired = runtime->Bonus(entry.first);
        const auto cached = cachedSight.find(unit);
        const auto previous = cached == cachedSight.end() ? 0 : cached->second.bonus;
        if (previous != desired) {
            // Preserve the old sight radius until native footprint removal.
            reinterpret_cast<SightRefresh>(0x006C29E3)(unit);
            if (desired) cachedSight[unit] = { entry.first, desired };
            else cachedSight.erase(unit);
            reinterpret_cast<SightRefresh>(0x006C2891)(unit);
        }
        edmw::UpdateProductionEffects(unit, entry.first);
        if (traceApplication)
            edmw::TraceEffect("effect-applied unit=%u health=%d max-health=%d sight-bonus=%.6g actual-sight=%.6g simulation-ms=%u",
                entry.first, Read<int32_t>(unit, 0x90), MaximumHealth(unit), desired, ReadSight(unit, nullptr), time);
        // Negative regeneration can invoke native death callbacks. Complete all
        // other work first, and revalidate later snapshot entries by identity.
        edmw::UpdateAdditionalUnitEffects(unit, entry.first, time, *runtime);
    }
    edmw::EndAdditionalUnitEffectFrame();
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
            !ValidUnitEffectOutputs(rule) ||
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
        memcmp(reinterpret_cast<const void*>(scalarModifierAddress), scalarModifierBytes, sizeof(scalarModifierBytes)) ||
        memcmp(reinterpret_cast<const void*>(sharedModifierAddress), sharedModifierBytes, sizeof(sharedModifierBytes)) ||
        memcmp(reinterpret_cast<const void*>(movementGetterAddress), movementGetterBytes, sizeof(movementGetterBytes)) ||
        memcmp(reinterpret_cast<const void*>(displayMovementGetterAddress), displayMovementGetterBytes, sizeof(displayMovementGetterBytes)) ||
        memcmp(reinterpret_cast<const void*>(constructorAddress), constructorBytes, sizeof(constructorBytes)) ||
        memcmp(reinterpret_cast<const void*>(destructorAddress), destructorBytes, sizeof(destructorBytes)) ||
        memcmp(reinterpret_cast<const void*>(poolCheckoutAddress), poolCheckoutBytes, sizeof(poolCheckoutBytes)) ||
        memcmp(reinterpret_cast<const void*>(tickCallAddress), tickBytes, sizeof(tickBytes)) ||
        !ValidateAdditionalUnitEffectBindings()) return false;
    originalGetter = reinterpret_cast<SightGetter>(MakeTrampoline(getterAddress, getterBytes, sizeof(getterBytes)));
    originalScalarModifier = reinterpret_cast<ScalarModifier>(MakeTrampoline(scalarModifierAddress, scalarModifierBytes, sizeof(scalarModifierBytes)));
    originalSharedModifier = reinterpret_cast<SharedModifier>(MakeTrampoline(sharedModifierAddress, sharedModifierBytes, sizeof(sharedModifierBytes)));
    originalMovementGetter = reinterpret_cast<SightGetter>(MakeTrampoline(movementGetterAddress, movementGetterBytes, sizeof(movementGetterBytes)));
    originalDisplayMovementGetter = reinterpret_cast<SightGetter>(MakeTrampoline(displayMovementGetterAddress, displayMovementGetterBytes, sizeof(displayMovementGetterBytes)));
    originalConstructor = reinterpret_cast<Constructor>(MakeTrampoline(constructorAddress, constructorBytes, sizeof(constructorBytes)));
    originalDestructor = reinterpret_cast<Destructor>(MakeTrampoline(destructorAddress, destructorBytes, sizeof(destructorBytes)));
    originalPoolCheckout = reinterpret_cast<Constructor>(MakeTrampoline(poolCheckoutAddress, poolCheckoutBytes, sizeof(poolCheckoutBytes)));
    if (!originalGetter || !originalScalarModifier || !originalSharedModifier || !originalMovementGetter || !originalDisplayMovementGetter ||
        !originalConstructor || !originalDestructor || !originalPoolCheckout) return false;
    runtime = std::make_unique<edmw::UnitEffectRuntime>(rules);
    for (const auto& rule : rules) {
        relevantObjects.insert(rule.sourceObject);
        relevantObjects.insert(rule.targetObject);
    }
    // Initialization failure terminates the still-suspended owned game. Thus a
    // partial patch set can never be resumed by the launcher.
    return InstallProductionEffects(rules) && InstallAdditionalUnitEffects(rules) &&
        WriteBranch(constructorAddress, reinterpret_cast<const void*>(&ConstructUnit), sizeof(constructorBytes), 0xE9) &&
        WriteBranch(destructorAddress, reinterpret_cast<const void*>(&DestroyUnit), sizeof(destructorBytes), 0xE9) &&
        WriteBranch(poolCheckoutAddress, reinterpret_cast<const void*>(&CheckoutUnit), sizeof(poolCheckoutBytes), 0xE9) &&
        WriteBranch(getterAddress, reinterpret_cast<const void*>(&ReadSight), sizeof(getterBytes), 0xE9) &&
        WriteBranch(scalarModifierAddress, reinterpret_cast<const void*>(&ModifyNativeScalar), sizeof(scalarModifierBytes), 0xE9) &&
        WriteBranch(sharedModifierAddress, reinterpret_cast<const void*>(&ModifySharedStatistics), sizeof(sharedModifierBytes), 0xE9) &&
        WriteBranch(movementGetterAddress, reinterpret_cast<const void*>(&ReadMovement), sizeof(movementGetterBytes), 0xE9) &&
        WriteBranch(displayMovementGetterAddress, reinterpret_cast<const void*>(&ReadDisplayMovement), sizeof(displayMovementGetterBytes), 0xE9) &&
        WriteBranch(tickCallAddress, reinterpret_cast<const void*>(&UpdateUnits), sizeof(tickBytes), 0xE8);
}

bool edmw::ActivateUnitEffect(const UnitEffectId& effect, void* actor, uint32_t expectedPlayer) {
    return ActivateUnitEffects(effect, { actor }, expectedPlayer);
}

bool edmw::ActivateUnitEffects(const UnitEffectId& effect, const std::vector<void*>& actors, uint32_t expectedPlayer) {
    if (!runtime || expectedPlayer > 15 || actors.empty() || actors.size() > 4096) return false;
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    std::vector<UnitEffectSnapshot> snapshots;
    for (const auto actor : actors) {
        if (!actor || Read<uintptr_t>(actor, 0) != complexUnitVtable || Read<int32_t>(actor, 0x90) <= 0) continue;
        const auto owner = Read<void*>(actor, 0x18);
        const auto prototype = Read<void*>(actor, 0x64);
        if (!owner || !prototype || Read<uint32_t>(owner, 0x478) != expectedPlayer ||
            reinterpret_cast<void* const*>(0x0099EED8)[expectedPlayer] != owner) continue;
        const auto tree = Read<void*>(owner, 0xD58);
        if (!tree) continue;
        snapshots.push_back({ Read<uint32_t>(actor, 0x24), expectedPlayer, NativeCivilizationToWireId(Read<uint32_t>(tree, 0x16D4)),
            Read<uint32_t>(prototype, 0x1AC), Read<int32_t>(actor, 0x1C), Read<int32_t>(actor, 0x20), true, Generation(actor) });
    }
    const auto activated = runtime->ActivateGroup(effect, snapshots, expectedPlayer, lastSimulationMilliseconds);
    edmw::TraceEffect("effect-group eligible=%u activated=%u player=%u simulation-ms=%u",
        static_cast<uint32_t>(snapshots.size()), static_cast<uint32_t>(activated), expectedPlayer, lastSimulationMilliseconds);
    return activated != 0;
}

float edmw::UnitEffectBonus(void* actor, UnitEffectStat stat) {
    if (!runtime || !actor || Read<uintptr_t>(actor, 0) != complexUnitVtable || Read<int32_t>(actor, 0x90) <= 0) return 0.0f;
    const std::lock_guard<std::recursive_mutex> guard(cacheMutex);
    return runtime->Bonus(Read<uint32_t>(actor, 0x24), stat);
}
