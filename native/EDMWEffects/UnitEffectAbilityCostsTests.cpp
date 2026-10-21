#include <cassert>
#include <cstdint>
#include <iostream>
#include <limits>
#include "UnitEffectAbilityCosts.h"

int main() {
    using namespace edmw;
    EffectAbilityCostTotals totals{};
    assert(ComputeEffectAbilityCostTotals({ 10, 20, 30, 40 }, 3, totals));
    assert((totals == EffectAbilityCostTotals{ 30, 60, 90, 120 }));
    assert(CanAffordEffectAbilityCost({ 30, 60, 90, 120 }, totals));
    assert(!CanAffordEffectAbilityCost({ 30, 60, 89, 120 }, totals));
    // One short resource rejects the entire group before any deduction.
    EffectAbilityCostTotals balances{ 30, 60, 89, 120 };
    const auto before = balances;
    if (CanAffordEffectAbilityCost(balances, totals))
        for (size_t i = 0; i < totals.size(); ++i) balances[i] -= totals[i];
    assert(balances == before);

    assert(ComputeEffectAbilityCostTotals({ 100000, 100000, 100000, 100000 }, 4096, totals));
    assert(totals[0] == 409600000);
    assert(!ComputeEffectAbilityCostTotals({ UINT32_MAX, 0, 0, 0 }, 4096, totals));
    assert((totals == EffectAbilityCostTotals{}));
    assert(!ComputeEffectAbilityCostTotals({ 1, 0, 0, 0 }, UINT32_MAX, totals));
    assert(!ComputeEffectAbilityCostTotals({ 1, 0, 0, 0 }, 0, totals));
    assert(!ComputeEffectAbilityCostTotals({ 100001, 0, 0, 0 }, 1, totals));
    assert(ComputeEffectAbilityCostTotals({ 0, 0, 0, 0 }, 1, totals));
    assert(CanAffordEffectAbilityCost({ -1, -2, -3, -4 }, totals));
    assert(!CanAffordEffectAbilityCost({ -1, 10, 10, 10 }, { 1, 0, 0, 0 }));
    assert(!CanAffordEffectAbilityCost({ 10, 10, 10, 10 }, { -1, 0, 0, 0 }));

    assert(ComputeEffectAbilityCostTotals({ 10, 20, 30, 40 }, 3, totals));
    balances = { 100, 200, 300, 400 };
    const auto original = balances;
    assert(CanAffordEffectAbilityCost(balances, totals));
    for (size_t i = 0; i < totals.size(); ++i) balances[i] -= totals[i];
    const auto refund = ClampEffectAbilityCostRefund(balances, totals);
    for (size_t i = 0; i < totals.size(); ++i) balances[i] += refund[i];
    assert(balances == original);
    // A delayed refund cannot overflow after unrelated income reaches the cap.
    const auto maximum = (std::numeric_limits<int32_t>::max)();
    assert((ClampEffectAbilityCostRefund({ maximum, maximum - 3, -100, 0 }, { 10, 20, 30, 40 }) ==
        EffectAbilityCostTotals{ 0, 3, 30, 40 }));
    std::cout << "Unit effect ability cost tests passed.\n";
}
