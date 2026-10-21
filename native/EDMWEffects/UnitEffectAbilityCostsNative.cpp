#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include "UnitEffectAbilityCostsNative.h"
#include "UnitEffectAbilityCosts.h"
#include "EffectTrace.h"

namespace {
constexpr uintptr_t basePlayerVtable = 0x007EFA78;
constexpr uintptr_t humanPlayerVtable = 0x007E9FDC;
constexpr uintptr_t computerPlayerVtable = 0x007E770C;
constexpr uintptr_t chargeAddress = 0x005D44BD;
constexpr uintptr_t computerChargeAddress = 0x00444724;
constexpr uintptr_t refundAddress = 0x005C8694;
constexpr std::array<uint32_t, 4> resourceIds{ 2, 3, 5, 4 };
constexpr std::array<size_t, 4> balanceIndices{ 0, 1, 3, 2 };
using Charge = void(__thiscall*)(void*, uint32_t, int32_t);
using Grant = void(__thiscall*)(void*, uint32_t, uint32_t);

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

bool Player(void* owner, Charge& charge) {
    const auto count = Read<uint32_t>(reinterpret_cast<const void*>(0x0099EF30), 0);
    if (!owner || !count || count > 16) return false;
    const auto index = Read<uint32_t>(owner, 0x478);
    if (index >= count || Read<void*>(reinterpret_cast<const void*>(0x0099EED8), index * 4) != owner)
        return false;
    const auto tree = Read<void*>(owner, 0xD58);
    if (!tree || Read<void*>(tree, 0x16D0) != owner) return false;
    const auto table = Read<uintptr_t>(owner, 0);
    if (table != basePlayerVtable && table != humanPlayerVtable && table != computerPlayerVtable)
        return false;
    const auto expectedCharge = table == computerPlayerVtable ? computerChargeAddress : chargeAddress;
    const auto actualCharge = Read<uintptr_t>(reinterpret_cast<const void*>(table), 0xD8);
    if (actualCharge != expectedCharge) return false;
    charge = reinterpret_cast<Charge>(actualCharge);
    return true;
}

bool Balances(void* owner, edmw::EffectAbilityCostTotals& balances) {
    std::array<int32_t, 4> nativeBalances{};
    if (!Copy(static_cast<const uint8_t*>(owner) + 0xEC4, nativeBalances.data(), sizeof(nativeBalances)))
        return false;
    for (size_t i = 0; i < balances.size(); ++i) balances[i] = nativeBalances[balanceIndices[i]];
    return true;
}

bool Prepare(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count,
    std::array<int32_t, 4>& totals, Charge& charge, edmw::EffectAbilityCostTotals* available = nullptr) {
    edmw::EffectAbilityCostTotals balances{};
    const bool valid = edmw::ComputeEffectAbilityCostTotals(costs, count, totals) &&
        edmw::ValidateNativeEffectCosts() && Player(owner, charge) &&
        Balances(owner, balances) && edmw::CanAffordEffectAbilityCost(balances, totals);
    if (valid && available) *available = balances;
    return valid;
}

void ChargeAll(void* owner, const std::array<int32_t, 4>& totals, Charge charge) {
    // The supported virtuals are bounded, non-allocating integer deductions and
    // do not call game code between resources. With the simulation thread held
    // by the caller, the complete preflight and this loop form one purchase.
    for (size_t i = 0; i < totals.size(); ++i)
        if (totals[i]) charge(owner, resourceIds[i], totals[i]);
}
}

bool edmw::ValidateNativeEffectCosts() {
    constexpr uint8_t charge[] = { 0x8B, 0x44, 0x24, 0x04, 0x8D, 0x84, 0x81,
        0xBC, 0x0E, 0x00, 0x00, 0x8B, 0x4C, 0x24, 0x08, 0x29, 0x08, 0xC2, 0x08, 0x00 };
    constexpr uint8_t computerCharge[] = { 0x83, 0x7C, 0x24, 0x08, 0x00, 0x7E, 0x0D,
        0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08, 0xE8, 0x85, 0xFD, 0x18, 0x00,
        0xC2, 0x08, 0x00 };
    constexpr uint8_t affordability[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C };
    constexpr uint8_t purchaseCall[] = { 0xFF, 0x92, 0xD8, 0x00, 0x00, 0x00 };
    constexpr uint8_t refund[] = { 0x8B, 0x54, 0x24, 0x08, 0x8B, 0x44, 0x24, 0x04,
        0x01, 0x94, 0x81, 0xBC, 0x0E, 0x00, 0x00, 0x52, 0x50, 0x51,
        0xE8, 0x5B, 0x57, 0x16, 0x00, 0x83, 0xC4, 0x0C, 0xC2, 0x08, 0x00 };
    constexpr uint8_t refundCaller[] = { 0xE8, 0x71, 0xEC, 0x18, 0x00 };
    return Matches(chargeAddress, charge) && Matches(computerChargeAddress, computerCharge) &&
        Matches(0x005CCCF4, affordability) && Matches(0x005D4608, purchaseCall) &&
        Matches(refundAddress, refund) && Matches(0x00439A1E, refundCaller);
}

bool edmw::CanPayEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count) {
    std::array<int32_t, 4> totals{}; Charge charge = nullptr;
    return Prepare(owner, costs, count, totals, charge);
}

bool edmw::PayEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count) {
    std::array<int32_t, 4> totals{}; Charge charge = nullptr;
    EffectAbilityCostTotals before{}, after{};
    if (!Prepare(owner, costs, count, totals, charge, &before)) {
        TraceEffect("ability-cost rejected targets=%u food=%u wood=%u gold=%u stone=%u", count, costs[0], costs[1], costs[2], costs[3]);
        return false;
    }
    ChargeAll(owner, totals, charge);
    if (Balances(owner, after))
        TraceEffect("ability-cost paid player=%u targets=%u food=%d->%d wood=%d->%d gold=%d->%d stone=%d->%d",
            Read<uint32_t>(owner, 0x478), count, before[0], after[0], before[1], after[1], before[2], after[2], before[3], after[3]);
    return true;
}

bool edmw::RefundEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count) {
    EffectAbilityCostTotals totals{}, balances{}; Charge charge = nullptr;
    if (!ComputeEffectAbilityCostTotals(costs, count, totals) || !ValidateNativeEffectCosts() ||
        !Player(owner, charge) || !Balances(owner, balances)) return false;
    const auto refund = ClampEffectAbilityCostRefund(balances, totals);
    for (size_t i = 0; i < refund.size(); ++i)
        if (refund[i]) reinterpret_cast<Grant>(refundAddress)(owner, resourceIds[i], static_cast<uint32_t>(refund[i]));
    TraceEffect("ability-cost refunded player=%u food=%d wood=%d gold=%d stone=%d", Read<uint32_t>(owner, 0x478),
        refund[0], refund[1], refund[2], refund[3]);
    return true;
}
