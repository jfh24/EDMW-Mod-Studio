#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include "UnitEffectRuntime.h"

namespace edmw {
inline int32_t AdjustProductionCost(int32_t nativeCost, float amount) {
    if (!amount || !std::isfinite(amount)) return nativeCost;
    return static_cast<int32_t>(std::round((std::max)(0.0,
        (std::min)(2147483647.0, static_cast<double>(nativeCost) + amount))));
}

inline bool ProductionLimitAllows(uint64_t existingAndReserved, float limit) {
    return limit <= 0.0f || existingAndReserved < static_cast<uint64_t>(limit);
}

// The native getter returns the same millisecond countdown consumed by the
// producer update. Keep fixed time adjustments distinct from a speed multiplier.
inline int32_t AdjustProductionDuration(int32_t nativeMilliseconds, int32_t rawMilliseconds,
    float trainingSeconds, float constructionSeconds, float parameterAmount, float rateAmount) {
    if (nativeMilliseconds <= 0) return nativeMilliseconds;
    const double factor = rawMilliseconds > 0 ? static_cast<double>(nativeMilliseconds) / rawMilliseconds : 1.0;
    const double additive = static_cast<double>(trainingSeconds + constructionSeconds) * 1000.0 + parameterAmount * factor;
    const double rate = (std::max)(0.01, 1.0 + rateAmount);
    const double value = (static_cast<double>(nativeMilliseconds) + additive) / rate;
    if (!std::isfinite(value)) return nativeMilliseconds;
    return static_cast<int32_t>(std::round((std::max)(1.0, (std::min)(2147483647.0, value))));
}

inline int32_t ScaleProductionRemaining(int32_t remaining, int32_t previousDuration, int32_t nextDuration) {
    if (remaining <= 0 || previousDuration <= 0 || nextDuration <= 0 || previousDuration == nextDuration) return remaining;
    const auto value = std::ceil(static_cast<double>(remaining) * nextDuration / previousDuration);
    return static_cast<int32_t>((std::max)(1.0, (std::min)(2147483647.0, value)));
}

// An inner purchase or native reentrant callback must restore the outer actor.
class ProductionActorScope {
public:
    ProductionActorScope(void*& slot, void* actor) : slot_(slot), previous_(slot) { slot_ = actor; }
    ~ProductionActorScope() { slot_ = previous_; }
    ProductionActorScope(const ProductionActorScope&) = delete;
    ProductionActorScope& operator=(const ProductionActorScope&) = delete;
private:
    void*& slot_;
    void* previous_;
};

bool InstallProductionEffects(const std::vector<UnitEffectRule>& rules);
bool HasProductionEffects(void* unit);
void UpdateProductionEffects(void* unit, uint32_t id);
void ForgetProductionEffects(void* unit);
}
