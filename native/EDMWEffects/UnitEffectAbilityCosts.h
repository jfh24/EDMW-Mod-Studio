#pragma once
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace edmw {
using EffectAbilityCosts = std::array<uint32_t, 4>; // Food, wood, gold, stone.
using EffectAbilityCostTotals = std::array<int32_t, 4>;

inline bool ComputeEffectAbilityCostTotals(const EffectAbilityCosts& costs, uint32_t count,
    EffectAbilityCostTotals& totals) {
    totals.fill(0);
    if (!count || count > 4096) return false;
    EffectAbilityCostTotals candidate{};
    for (size_t i = 0; i < costs.size(); ++i) {
        if (costs[i] > 100000) return false;
        const auto total = static_cast<uint64_t>(costs[i]) * count;
        if (total > static_cast<uint64_t>((std::numeric_limits<int32_t>::max)())) return false;
        candidate[i] = static_cast<int32_t>(total);
    }
    totals = candidate;
    return true;
}

inline bool CanAffordEffectAbilityCost(const EffectAbilityCostTotals& balances,
    const EffectAbilityCostTotals& totals) {
    for (size_t i = 0; i < totals.size(); ++i)
        if (totals[i] < 0 || (totals[i] && balances[i] < totals[i])) return false;
    return true;
}

inline EffectAbilityCostTotals ClampEffectAbilityCostRefund(const EffectAbilityCostTotals& balances,
    const EffectAbilityCostTotals& totals) {
    EffectAbilityCostTotals refund{};
    for (size_t i = 0; i < totals.size(); ++i) {
        if (totals[i] <= 0) continue;
        const auto headroom = static_cast<int64_t>((std::numeric_limits<int32_t>::max)()) - balances[i];
        refund[i] = static_cast<int32_t>((std::min)(static_cast<int64_t>(totals[i]), headroom));
    }
    return refund;
}
}
