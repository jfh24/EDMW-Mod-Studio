#include <cassert>
#include <iostream>
#include "UnitEffectProduction.h"

int main() {
    using namespace edmw;
    assert(AdjustProductionCost(100, -25) == 75);
    assert(AdjustProductionCost(100, -200) == 0);
    assert(AdjustProductionCost(0, 25) == 25);
    assert(ProductionLimitAllows(3, 4));
    assert(!ProductionLimitAllows(4, 4));
    assert(ProductionLimitAllows(10000, 0));
    assert(AdjustProductionDuration(30000, 60000, -5, 0, 0, 0) == 25000);
    assert(AdjustProductionDuration(30000, 60000, 0, 0, 1000, 0) == 30500);
    assert(AdjustProductionDuration(30000, 60000, 0, 0, 0, 1) == 15000);
    assert(AdjustProductionDuration(30000, 60000, -100, 0, 0, 0) == 1);
    assert(AdjustProductionDuration(0, 0, 5, 5, 5, 1) == 0);
    assert(ScaleProductionRemaining(7500, 30000, 15000) == 3750);
    assert(ScaleProductionRemaining(3750, 15000, 30000) == 7500);
    assert(ScaleProductionRemaining(7500, 30000, 30000) == 7500);
    void* slot = nullptr;
    int first = 1, second = 2;
    { ProductionActorScope outer(slot, &first);
        assert(slot == &first);
        { ProductionActorScope inner(slot, &second); assert(slot == &second); }
        assert(slot == &first);
    }
    assert(slot == nullptr);
    std::cout << "UNIT PRODUCTION PASS: fixed time, raw parameters, rate multiplier, current progress, nested producer scope.\n";
}
