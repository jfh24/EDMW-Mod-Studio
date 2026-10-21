#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include "UnitEffectActionsNative.h"

namespace {
constexpr uintptr_t transferAddress = 0x0073A285;
constexpr uintptr_t transformAddress = 0x0072CF16;
constexpr uintptr_t replacementAddress = 0x0072D6F0;
constexpr uintptr_t transferCategoryPredicate = 0x0051A29A;
constexpr uintptr_t transferCategoryRegistry = 0x00877678;
constexpr uintptr_t complexUnitVtable = 0x007F6C48;
constexpr uintptr_t prototypeRegistry = 0x009D1BF8;
constexpr uint32_t prototypeCount = 2049;

bool Copy(const void* from, void* to, size_t size) {
    if (!from || !to) return false;
    __try { memcpy(to, from, size); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template<class T> T Read(const void* pointer, size_t offset) {
    T result{};
    if (pointer) Copy(static_cast<const uint8_t*>(pointer) + offset, &result, sizeof(result));
    return result;
}
template<size_t N> bool Matches(uintptr_t address, const uint8_t (&expected)[N]) {
    uint8_t actual[N]{};
    return Copy(reinterpret_cast<const void*>(address), actual, N) && !memcmp(actual, expected, N);
}

bool Player(void* owner, uint32_t& index) {
    const auto count = Read<uint32_t>(reinterpret_cast<const void*>(0x0099EF30), 0);
    if (!owner || !count || count > 16) return false;
    index = Read<uint32_t>(owner, 0x478);
    if (index >= count || Read<void*>(reinterpret_cast<const void*>(0x0099EED8), index * 4) != owner)
        return false;
    const auto tree = Read<void*>(owner, 0xD58);
    return tree && Read<void*>(tree, 0x16D0) == owner;
}

void* Prototype(uint32_t object) {
    if (object >= prototypeCount) return nullptr;
    const auto prototype = Read<void*>(reinterpret_cast<const void*>(prototypeRegistry), object * 4);
    if (!prototype || Read<uint32_t>(prototype, 0x1AC) != object || Read<uint32_t>(prototype, 0x1A4) != 1)
        return nullptr;
    const auto table = Read<uintptr_t>(prototype, 0);
    if (table < 0x007E3000 || table > 0x0083CF00) return nullptr;
    // All supported EEUC unit/building subclasses dispatch native grid creation
    // through one of these verified virtual +0x48 implementations. No arbitrary
    // registered world-object class is invoked as an EEComplexUnit factory.
    switch (Read<uintptr_t>(reinterpret_cast<const void*>(table), 0x48)) {
    case 0x00703C3F: // Ordinary land, naval, air, citizen and transport classes.
    case 0x006D7686: // Building.
    case 0x006D9500: // Gate.
    case 0x006DE5E5: // Mobile building.
    case 0x006DD49A: // Mining building.
    case 0x006DF0E8: // Naval building.
        return prototype;
    default:
        return nullptr;
    }
}

bool Statistics(void* owner, uint32_t object, void* prototype) {
    const auto begin = Read<uintptr_t>(owner, 0xAC8);
    const auto end = Read<uintptr_t>(owner, 0xACC);
    const auto capacity = Read<uintptr_t>(owner, 0xAD0);
    if (!begin || end < begin || capacity < end || (end - begin) % 4 ||
        (capacity - begin) % 4 || (capacity - begin) / 4 > 100000 || object >= (end - begin) / 4)
        return false;
    const auto stats = Read<void*>(reinterpret_cast<const void*>(begin), object * 4);
    return stats && Read<void*>(stats, 0xB4) == prototype;
}

bool Actor(void* actor, void*& owner, uint32_t& player, void*& prototype, uint32_t& object) {
    if (!actor || Read<uintptr_t>(actor, 0) != complexUnitVtable ||
        Read<void*>(actor, 0x68) != actor || Read<int32_t>(actor, 0x90) <= 0 ||
        Read<uint32_t>(actor, 0x24) == UINT32_MAX) return false;
    owner = Read<void*>(actor, 0x18);
    if (!Player(owner, player)) return false;
    prototype = Read<void*>(actor, 0x64);
    if (!prototype) return false;
    object = Read<uint32_t>(prototype, 0x1AC);
    if (Prototype(object) != prototype || !Statistics(owner, object, prototype)) return false;
    // A garrisoned/removed actor uses -10000 grid coordinates. Native grid
    // replacement assumes an actor currently present on the map.
    const auto map = Read<void*>(reinterpret_cast<const void*>(0x0099EF20), 0);
    const auto width = Read<uint32_t>(map, 0x150);
    const auto x = Read<int32_t>(actor, 0x1C), y = Read<int32_t>(actor, 0x20);
    return width && width <= 8192 && x >= 0 && y >= 0 &&
        static_cast<uint32_t>(x) < width && static_cast<uint32_t>(y) < width;
}

bool ExactTypeHandoff(uint32_t object) {
    // The fifth argument to the stock ownership lifecycle selects tribute
    // handoff. Its only conversion exception is native category 34 when that
    // category both contains this object and enables civilization remapping.
    // Test the same predicate before the lifecycle removes the old actor.
    const auto registry = reinterpret_cast<const void*>(transferCategoryRegistry);
    const auto begin = Read<uintptr_t>(registry, 4);
    const auto end = Read<uintptr_t>(registry, 8);
    const auto capacity = Read<uintptr_t>(registry, 12);
    constexpr uint32_t exceptionCategory = 34;
    if (!begin || end < begin || capacity < end || (end - begin) % 4 ||
        (capacity - begin) % 4 || (capacity - begin) / 4 > 100000 ||
        exceptionCategory >= (end - begin) / 4) return false;
    void* category = nullptr;
    if (!Copy(reinterpret_cast<const void*>(begin + exceptionCategory * 4), &category, sizeof(category)))
        return false;
    if (!category) return true;
    uint8_t remap = 0;
    if (!Copy(static_cast<const uint8_t*>(category) + 0x1A5, &remap, sizeof(remap))) return false;
    if (!remap) return true;
    using Contains = bool(__thiscall*)(void*, uint32_t, uint32_t);
    __try { return !reinterpret_cast<Contains>(transferCategoryPredicate)(category, object, 0); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void* Transfer(void* actor, uint32_t player) {
    using Convert = void*(__cdecl*)(void*, uint32_t, bool, bool, bool);
    // Match stock tribute's ownership handoff at 0x0050C97B, preserving type
    // and native selection/registration updates. Tribute purchase checks and
    // payment belong to its command handler, outside this lifecycle routine.
    __try { return reinterpret_cast<Convert>(transferAddress)(actor, player, false, true, true); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* Transform(void* actor, uint32_t player, void* prototype) {
    using Replace = void*(__cdecl*)(void*, uint32_t, void*, bool);
    __try { return reinterpret_cast<Replace>(transformAddress)(actor, player, prototype, true); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
}

bool edmw::ValidateNativeUnitActions() {
    constexpr uint8_t transfer[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x74 };
    constexpr uint8_t transform[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10 };
    constexpr uint8_t replacement[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x7C };
    constexpr uint8_t transformCaller[] = { 0xE8, 0xA3, 0x19, 0x0D, 0x00 };
    constexpr uint8_t convertCaller[] = { 0xE8, 0x76, 0x74, 0x03, 0x00 };
    constexpr uint8_t tributeCaller[] = { 0xE8, 0x05, 0xD9, 0x22, 0x00 };
    constexpr uint8_t categoryPredicate[] = { 0x53, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x10, 0x32, 0xC0 };
    constexpr uint8_t registryBounds[] = { 0xB9, 0x01, 0x08, 0x00, 0x00,
        0xBF, 0xF8, 0x1B, 0x9D, 0x00, 0xF3, 0xAB };
    return Matches(transferAddress, transfer) && Matches(transformAddress, transform) &&
        Matches(replacementAddress, replacement) && Matches(0x0065B56E, transformCaller) &&
        Matches(0x00702E0A, convertCaller) && Matches(0x0050C97B, tributeCaller) &&
        Matches(transferCategoryPredicate, categoryPredicate) && Matches(0x0070899C, registryBounds);
}

void* edmw::TransferEffectUnit(void* actor, void* newOwner) {
    if (!ValidateNativeUnitActions()) return nullptr;
    void* oldOwner = nullptr; void* prototype = nullptr;
    uint32_t player = 0, object = 0, destination = 0;
    if (!Actor(actor, oldOwner, player, prototype, object) || !Player(newOwner, destination)) return nullptr;
    if (oldOwner == newOwner) return actor;
    if (!Statistics(newOwner, object, prototype) || !ExactTypeHandoff(object)) return nullptr;
    return Transfer(actor, destination);
}

void* edmw::TransformEffectUnit(void* actor, uint32_t object) {
    if (!ValidateNativeUnitActions()) return nullptr;
    void* owner = nullptr; void* previous = nullptr;
    uint32_t player = 0, previousObject = 0;
    if (!Actor(actor, owner, player, previous, previousObject)) return nullptr;
    if (previousObject == object) return actor;
    const auto prototype = Prototype(object);
    if (!prototype || !Statistics(owner, object, prototype)) return nullptr;
    return Transform(actor, player, prototype);
}
