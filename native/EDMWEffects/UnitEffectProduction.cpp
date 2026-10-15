#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <array>
#include <cstring>
#include <map>
#include <mutex>
#include "UnitEffectProduction.h"
#include "UnitEffectNative.h"
#include "NativeAbilityBindings.h"

namespace {
constexpr uintptr_t queueAddress = 0x006B0595;
constexpr uintptr_t costAddress = 0x0067154E;
constexpr uintptr_t durationAddress = 0x006B6EB0;
constexpr uintptr_t costTooltipAddress = 0x0078382D;
constexpr uintptr_t nativeQueueDurationReturn = 0x006BBAB3;
constexpr uintptr_t buildCommandSlot = 0x007E4D44; // EEMBuildUnit vtable +0x28.
constexpr uintptr_t buildCommandAddress = 0x004FC922;
constexpr uintptr_t researchCommandAddress = 0x005072D1;
using Queue = void(__thiscall*)(void*, uint32_t, bool, bool);
using Cost = int32_t(__thiscall*)(void*, uint32_t, uint32_t, float);
using Duration = int32_t(__thiscall*)(void*, void*);
using CostTooltip = bool(__thiscall*)(void*, void*);
using SelectionCount = uint32_t(__thiscall*)(void*);
using SelectionUnit = void*(__thiscall*)(void*, uint32_t);
using BuildCommand = void(__thiscall*)(void*);
using FirstSelectionUnit = void*(__thiscall*)(void*);
using FindPlayer = void*(__thiscall*)(void*, uint32_t);
using FindUnit = void*(__thiscall*)(void*, uint32_t);
using IsBuilding = bool(__thiscall*)(void*);
using FindTechnology = void*(__thiscall*)(void*, uint32_t);
Queue originalQueue = nullptr;
Cost originalCost = nullptr;
Duration originalDuration = nullptr;
CostTooltip originalCostTooltip = nullptr;
thread_local void* purchasingActor = nullptr;
struct DurationState { uint32_t id, technology; int32_t adjusted; };
std::map<void*, DurationState> durations;
std::mutex durationMutex;
bool buildCommandInstalled = false;
BuildCommand originalResearchCommand = nullptr;

template<class T> T Read(const void* pointer, size_t offset) {
    T value{}; memcpy(&value, static_cast<const uint8_t*>(pointer) + offset, sizeof(value)); return value;
}
template<class T> void Put(void* pointer, size_t offset, T value) {
    memcpy(static_cast<uint8_t*>(pointer) + offset, &value, sizeof(value));
}
bool ProductionNode(void* node) {
    if (!node) return false;
    const auto definition = Read<void*>(node, 8);
    if (!definition) return false;
    const auto kind = Read<uint32_t>(definition, 0xA8);
    return kind == 0 || kind == 1 || kind == 2 || kind == 7 || kind == 8;
}
bool QueueRange(void* actor, uintptr_t& begin, uint32_t& count) {
    begin = Read<uintptr_t>(actor, 0xB8);
    const auto end = Read<uintptr_t>(actor, 0xBC);
    const auto capacity = Read<uintptr_t>(actor, 0xC0);
    if (!begin) { count = 0; return end == 0; }
    if (end < begin || capacity < end || (end - begin) % 0x1C || (end - begin) / 0x1C > 65536) return false;
    count = static_cast<uint32_t>((end - begin) / 0x1C);
    return true;
}
uint32_t ProducedBuilding(void* node) {
    if (!ProductionNode(node)) return UINT32_MAX;
    const auto definition = Read<void*>(node, 8);
    const auto object = Read<int32_t>(definition, 0x11C);
    if (object < 0 || object >= 2049) return UINT32_MAX;
    auto prototype = reinterpret_cast<void* const*>(0x009D1BF8)[object];
    if (!prototype) return UINT32_MAX;
    const auto table = Read<uintptr_t>(prototype, 0);
    // Native placement dispatch at 0x00758EFA uses this exact virtual predicate
    // to choose building placement instead of an immediate train/research action.
    return reinterpret_cast<IsBuilding>(Read<uintptr_t>(reinterpret_cast<void*>(table), 0x7C))(prototype)
        ? static_cast<uint32_t>(object) : UINT32_MAX;
}
bool BuildingLimitAllows(void* actor, void* node) {
    const auto limit = edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::BuildLimit);
    if (limit <= 0) return true;
    const auto object = ProducedBuilding(node);
    if (object == UINT32_MAX) return true;
    const auto owner = Read<void*>(actor, 0x18);
    const auto tree = owner ? Read<void*>(owner, 0xD58) : nullptr;
    if (!owner || !tree) return false;
    uint64_t count = 0;
    size_t visited = 0;
    for (auto unit = Read<void*>(owner, 0x464); unit; unit = Read<void*>(unit, 0x0C)) {
        if (++visited > 65536) return false;
        if (Read<uintptr_t>(unit, 0) != 0x007F6C48 || Read<void*>(unit, 0x18) != owner || Read<int32_t>(unit, 0x90) <= 0) continue;
        const auto prototype = Read<void*>(unit, 0x64);
        if (prototype && Read<uint32_t>(prototype, 0x1AC) == object) ++count;
        // Activation removes the first B4 entry (0x006BBAF1 -> 0x004B5241),
        // so reserve the separate active order as well as the waiting vector.
        const auto activeTechnology = Read<uint32_t>(unit, 0x4E4);
        if (activeTechnology != 100000 && Read<int32_t>(unit, 0x508) > 0) {
            const auto activeNode = reinterpret_cast<FindTechnology>(0x00401D64)(tree, activeTechnology);
            if (activeNode && ProducedBuilding(activeNode) == object) ++count;
        }
        uintptr_t begin = 0; uint32_t queued = 0;
        if (!QueueRange(unit, begin, queued)) return false;
        for (uint32_t index = 0; index < queued; ++index) {
            const auto technology = Read<uint32_t>(reinterpret_cast<void*>(begin + index * 0x1C), 0);
            const auto queuedNode = reinterpret_cast<FindTechnology>(0x00401D64)(tree, technology);
            if (queuedNode && ProducedBuilding(queuedNode) == object) ++count;
        }
        if (!edmw::ProductionLimitAllows(count, limit)) return false;
    }
    return edmw::ProductionLimitAllows(count, limit);
}
int32_t AdjustDuration(void* actor, void* node, int32_t native) {
    if (!ProductionNode(node)) return native;
    const auto definition = Read<void*>(node, 8);
    return edmw::AdjustProductionDuration(native, Read<int32_t>(definition, 0x108),
        edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::TrainingTime),
        edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::ConstructionTime),
        edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::ProductionCoefficient),
        edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::ProductionRate));
}

void __fastcall QueueProduction(void* actor, void*, uint32_t technology, bool prepend, bool charge) {
    // Native affordability, spending, retained refund costs, and queue insertion
    // all run with the same actor. No player/global technology row is changed.
    const edmw::ProductionActorScope scope(purchasingActor, actor);
    const auto capacity = edmw::UnitEffectBonus(actor, edmw::UnitEffectStat::QueueCapacity);
    if (capacity > 0) {
        uintptr_t begin = 0; uint32_t queued = 0;
        if (!QueueRange(actor, begin, queued)) return;
        const auto active = Read<uint32_t>(actor, 0x4E4) != 100000 && Read<int32_t>(actor, 0x508) > 0;
        if (!edmw::ProductionLimitAllows(static_cast<uint64_t>(queued) + (active ? 1u : 0u), capacity)) return;
    }
    const auto owner = Read<void*>(actor, 0x18);
    const auto tree = owner ? Read<void*>(owner, 0xD58) : nullptr;
    const auto node = tree ? reinterpret_cast<FindTechnology>(0x00401D64)(tree, technology) : nullptr;
    if (!BuildingLimitAllows(actor, node)) return;
    originalQueue(actor, technology, prepend, charge);
}

void* ProductionCommandPlayer(void* command) {
    // Native commands carry the network identity at player +0x480; effects use
    // the separately assigned world index at +0x478. Invalid context falls
    // through to the original command without applying custom purchase rules.
    const uint32_t networkIdentity = Read<uint8_t>(command, 0x0A);
    if (networkIdentity == 255) return nullptr;
    const auto world = reinterpret_cast<void*>(0x0099EE58);
    const auto owner = reinterpret_cast<FindPlayer>(0x005926D2)(world, networkIdentity);
    if (!owner) return nullptr;
    const auto index = edmw::NativeAbilityCommandPlayerIndex(networkIdentity, Read<uint8_t>(owner, 0x480),
        Read<uint32_t>(owner, 0x478), Read<uint32_t>(world, 0xD8));
    return index != UINT32_MAX && reinterpret_cast<void* const*>(0x0099EED8)[index] == owner ? owner : nullptr;
}

void __fastcall PlaceProducedBuilding(void* command, void*) {
    const auto owner = ProductionCommandPlayer(command);
    // This is the synchronized command selection (owner +0xD44), not the
    // local HUD selection. Native EEMBuildUnit uses its first unit at 0x004FC96B.
    const auto selection = owner ? Read<void*>(owner, 0xD44) : nullptr;
    const auto candidate = selection ? reinterpret_cast<FirstSelectionUnit>(0x0040462B)(selection) : nullptr;
    const auto actor = candidate && Read<void*>(candidate, 0x18) == owner && Read<int32_t>(candidate, 0x90) > 0 ? candidate : nullptr;
    const auto tree = owner ? Read<void*>(owner, 0xD58) : nullptr;
    const auto node = tree ? reinterpret_cast<FindTechnology>(0x00401D64)(tree, Read<uint32_t>(command, 0x1C)) : nullptr;
    if (actor && !BuildingLimitAllows(actor, node)) return;
    const edmw::ProductionActorScope scope(purchasingActor, actor);
    reinterpret_cast<BuildCommand>(buildCommandAddress)(command);
}

void __fastcall IssueProductionResearch(void* command, void*) {
    const auto world = reinterpret_cast<void*>(0x0099EE58);
    const auto owner = ProductionCommandPlayer(command);
    const auto candidate = owner ? reinterpret_cast<FindUnit>(0x005A3D5C)(world, Read<uint32_t>(command, 0x10)) : nullptr;
    const auto actor = candidate && Read<void*>(candidate, 0x18) == owner && Read<int32_t>(candidate, 0x90) > 0 ? candidate : nullptr;
    // Initial native purchase eligibility can query prices before queue entry.
    // The queue's own nested actor scope still overrides this for group orders.
    const edmw::ProductionActorScope scope(purchasingActor, actor);
    originalResearchCommand(command);
}
int32_t __fastcall ReadProductionCost(void* node, void*, uint32_t resource, uint32_t epoch, float multiplier) {
    const auto native = originalCost(node, resource, epoch, multiplier);
    if (!purchasingActor || !ProductionNode(node)) return native;
    auto stat = edmw::UnitEffectStat::FoodCost;
    switch (resource) {
    case 2: stat = edmw::UnitEffectStat::FoodCost; break;
    case 3: stat = edmw::UnitEffectStat::WoodCost; break;
    case 4: stat = edmw::UnitEffectStat::StoneCost; break;
    case 5: stat = edmw::UnitEffectStat::GoldCost; break;
    default: return native;
    }
    const auto bonus = edmw::UnitEffectBonus(purchasingActor, stat);
    if (!bonus) return native;
    return edmw::AdjustProductionCost(native, bonus);
}
int32_t __fastcall ReadProductionDuration(void* actor, void*, void* node) {
    const auto native = originalDuration(actor, node);
    const auto adjusted = AdjustDuration(actor, node, native);
    // Queue initialization stores this result immediately after the call. Other
    // callers include HUD progress queries, which must not mutate simulation state.
    if (reinterpret_cast<uintptr_t>(_ReturnAddress()) == nativeQueueDurationReturn && actor && node) {
        const auto definition = Read<void*>(node, 8);
        if (definition) {
            const std::lock_guard<std::mutex> guard(durationMutex);
            durations[actor] = { Read<uint32_t>(actor, 0x24), Read<uint32_t>(definition, 0xFC), adjusted };
        }
    }
    return adjusted;
}

bool __fastcall ReadProductionCostTooltip(void* action, void*, void* destination) {
    void* actor = nullptr;
    const auto owner = Read<void*>(action, 0x10);
    if (owner) {
        const auto currentPlayer = *reinterpret_cast<const uint32_t*>(0x0099FC68);
        const auto activeOwner = currentPlayer < 16 ? reinterpret_cast<void* const*>(0x0099EED8)[currentPlayer] : nullptr;
        const auto selection = Read<void*>(owner, owner == activeOwner ? 0x488 : 0xD44);
        if (selection) {
            const auto table = Read<uintptr_t>(selection, 0);
            if (reinterpret_cast<SelectionCount>(Read<uintptr_t>(reinterpret_cast<const void*>(table), 0x24))(selection)) {
                const auto candidate = reinterpret_cast<SelectionUnit>(Read<uintptr_t>(reinterpret_cast<const void*>(table), 0x20))(selection, 0);
                if (candidate && Read<uintptr_t>(candidate, 0) == 0x007F6C48 && Read<void*>(candidate, 0x18) == owner &&
                    Read<int32_t>(candidate, 0x90) > 0) actor = candidate;
            }
        }
    }
    // This exact UI helper already chooses the first selected producer. Limit
    // the display scope to that same helper; simulation queries never infer an
    // actor from the local user's selection.
    const edmw::ProductionActorScope scope(purchasingActor, actor);
    return originalCostTooltip(action, destination);
}

template<size_t N> bool InstallHook(uintptr_t address, const std::array<uint8_t, N>& expected,
    const void* callback, void** original) {
    if (memcmp(reinterpret_cast<const void*>(address), expected.data(), N)) return false;
    auto trampoline = static_cast<uint8_t*>(VirtualAlloc(nullptr, N + 5, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!trampoline) return false;
    memcpy(trampoline, expected.data(), N);
    trampoline[N] = 0xE9;
    const auto back = static_cast<int32_t>(address + N - reinterpret_cast<uintptr_t>(trampoline + N + 5));
    memcpy(trampoline + N + 1, &back, 4);
    DWORD previous = 0;
    if (!VirtualProtect(trampoline, N + 5, PAGE_EXECUTE_READ, &previous)) return false;
    FlushInstructionCache(GetCurrentProcess(), trampoline, N + 5);
    *original = trampoline;
    if (!VirtualProtect(reinterpret_cast<void*>(address), N, PAGE_EXECUTE_READWRITE, &previous)) return false;
    std::array<uint8_t, N> branch{}; branch.fill(0x90); branch[0] = 0xE9;
    const auto to = static_cast<int32_t>(reinterpret_cast<uintptr_t>(callback) - address - 5);
    memcpy(branch.data() + 1, &to, 4);
    memcpy(reinterpret_cast<void*>(address), branch.data(), N);
    DWORD ignored = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), N, previous, &ignored)) return false;
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), N);
    return true;
}
}

bool edmw::InstallProductionEffects(const std::vector<UnitEffectRule>& rules) {
    bool required = false;
    for (const auto& rule : rules)
        for (const auto& output : UnitEffectOutputs(rule)) {
            const auto stat = static_cast<uint32_t>(output.stat);
            if ((stat >= 36 && stat <= 42) || stat == 47 || stat == 48 || stat == 49) required = true;
        }
    if (!required) return true;
    if (originalQueue || originalCost || originalDuration || originalCostTooltip || originalResearchCommand || buildCommandInstalled) return false;
    // All copied prologues comprise complete, position-independent instructions.
    constexpr std::array<uint8_t, 9> queueBytes{ 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x94, 0x00, 0x00, 0x00 };
    constexpr std::array<uint8_t, 6> costBytes{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C };
    constexpr std::array<uint8_t, 6> durationBytes{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x40 };
    constexpr std::array<uint8_t, 6> tooltipBytes{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x68 };
    constexpr std::array<uint8_t, 6> researchBytes{ 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x78 };
    if (memcmp(reinterpret_cast<const void*>(queueAddress), queueBytes.data(), queueBytes.size()) ||
        memcmp(reinterpret_cast<const void*>(costAddress), costBytes.data(), costBytes.size()) ||
        memcmp(reinterpret_cast<const void*>(durationAddress), durationBytes.data(), durationBytes.size()) ||
        memcmp(reinterpret_cast<const void*>(costTooltipAddress), tooltipBytes.data(), tooltipBytes.size()) ||
        memcmp(reinterpret_cast<const void*>(researchCommandAddress), researchBytes.data(), researchBytes.size()) ||
        *reinterpret_cast<const uintptr_t*>(buildCommandSlot) != buildCommandAddress) return false;
    // The launcher terminates its suspended process after any installation error.
    if (!(InstallHook(queueAddress, queueBytes, reinterpret_cast<const void*>(&QueueProduction), reinterpret_cast<void**>(&originalQueue)) &&
        InstallHook(costAddress, costBytes, reinterpret_cast<const void*>(&ReadProductionCost), reinterpret_cast<void**>(&originalCost)) &&
        InstallHook(durationAddress, durationBytes, reinterpret_cast<const void*>(&ReadProductionDuration), reinterpret_cast<void**>(&originalDuration)) &&
        InstallHook(costTooltipAddress, tooltipBytes, reinterpret_cast<const void*>(&ReadProductionCostTooltip), reinterpret_cast<void**>(&originalCostTooltip)) &&
        InstallHook(researchCommandAddress, researchBytes, reinterpret_cast<const void*>(&IssueProductionResearch), reinterpret_cast<void**>(&originalResearchCommand)))) return false;
    DWORD protection = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(buildCommandSlot), sizeof(void*), PAGE_READWRITE, &protection)) return false;
    const auto callback = reinterpret_cast<const void*>(&PlaceProducedBuilding);
    memcpy(reinterpret_cast<void*>(buildCommandSlot), &callback, sizeof(callback));
    DWORD ignored = 0;
    buildCommandInstalled = VirtualProtect(reinterpret_cast<void*>(buildCommandSlot), sizeof(void*), protection, &ignored) != FALSE;
    return buildCommandInstalled;
}

bool edmw::HasProductionEffects(void* unit) {
    const std::lock_guard<std::mutex> guard(durationMutex);
    return durations.find(unit) != durations.end();
}

void edmw::UpdateProductionEffects(void* unit, uint32_t id) {
    if (!originalDuration || !unit) return;
    const auto technology = Read<uint32_t>(unit, 0x4E4);
    const auto remaining = Read<int32_t>(unit, 0x508);
    if (technology == 100000 || remaining <= 0) { ForgetProductionEffects(unit); return; }
    const auto owner = Read<void*>(unit, 0x18);
    if (owner && Read<uint8_t>(owner, 0xF45)) { ForgetProductionEffects(unit); return; }
    const auto tree = owner ? Read<void*>(owner, 0xD58) : nullptr;
    const auto node = tree ? reinterpret_cast<FindTechnology>(0x00401D64)(tree, technology) : nullptr;
    if (!ProductionNode(node)) { ForgetProductionEffects(unit); return; }
    const auto native = originalDuration(unit, node);
    const auto adjusted = AdjustDuration(unit, node, native);
    const std::lock_guard<std::mutex> guard(durationMutex);
    auto found = durations.find(unit);
    const bool matching = found != durations.end() && found->second.id == id && found->second.technology == technology;
    const auto previous = matching ? found->second.adjusted : native;
    const auto nextRemaining = ScaleProductionRemaining(remaining, previous, adjusted);
    if (nextRemaining != remaining) Put(unit, 0x508, nextRemaining);
    if (adjusted == native) durations.erase(unit);
    else durations[unit] = { id, technology, adjusted };
}

void edmw::ForgetProductionEffects(void* unit) {
    const std::lock_guard<std::mutex> guard(durationMutex);
    durations.erase(unit);
}
