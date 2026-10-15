#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include "UnitEffectRuntime.h"

namespace edmw {
class UnitEffectCounterContribution {
public:
    int32_t Update(int32_t nativeCounter, int64_t desired) {
        const auto baseline = static_cast<int64_t>(nativeCounter) - applied_;
        const auto next = (std::max)(int64_t{ 0 }, (std::min)(int64_t{ 2147483647 }, baseline + desired));
        applied_ = next - baseline;
        return static_cast<int32_t>(next);
    }
private:
    int64_t applied_ = 0;
};
inline bool UnitEffectAccuracyHit(float adjustment, uint32_t attacker, uint32_t target,
    uint32_t simulationMilliseconds, uint32_t sequence) {
    const auto chance = (std::max)(0.0f, (std::min)(100.0f, 100.0f + adjustment));
    if (chance >= 100.0f) return true;
    if (chance <= 0.0f) return false;
    // A separate deterministic stream leaves the game's random sequence intact.
    // Unit identities, simulation time and hit order are shared by every peer.
    uint32_t value = attacker * 0x9E3779B9u ^ target * 0x85EBCA6Bu ^ simulationMilliseconds ^ sequence * 0xC2B2AE35u;
    value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15;
    value *= 0x846CA68Bu; value ^= value >> 16;
    return static_cast<double>(value % 1'000'000u) < static_cast<double>(chance) * 10'000.0;
}
// Keep fractional health between simulation ticks. The caller owns lifecycle
// invalidation, and feeds the native health after combat has updated it.
class UnitEffectRegeneration {
public:
    void Reset() { initialized_ = false; remainder_ = 0; previousRate_ = 0; }
    int32_t Update(uint32_t now, int32_t health, int32_t maximum, float rate) {
        if (!initialized_ || now < previousTime_ || health <= 0 || maximum <= 0) {
            initialized_ = true;
            previousTime_ = now;
            previousRate_ = rate;
            remainder_ = 0;
            return health;
        }
        // The previous evaluated bonus was active over this simulation interval.
        const auto elapsed = now - previousTime_;
        remainder_ += static_cast<double>(previousRate_) * elapsed / 1000.0;
        previousTime_ = now;
        previousRate_ = rate;
        if (!std::isfinite(remainder_)) { remainder_ = 0; return health; }
        const auto whole = std::trunc(remainder_);
        const auto next = static_cast<int32_t>((std::max)(0.0,
            (std::min)(static_cast<double>(maximum), static_cast<double>(health) + whole)));
        remainder_ -= whole;
        // Do not bank healing while full, or damage after a unit has died.
        if ((next == maximum && remainder_ > 0) || next == 0) remainder_ = 0;
        return next;
    }
private:
    bool initialized_ = false;
    uint32_t previousTime_ = 0;
    float previousRate_ = 0;
    double remainder_ = 0;
};

void UpdateAdditionalUnitEffects(void* unit, uint32_t id, uint32_t simulationMilliseconds,
    const UnitEffectRuntime& runtime);
void ForgetAdditionalUnitEffects(void* unit);
bool ValidateAdditionalUnitEffectBindings();
bool InstallAdditionalUnitEffects(const std::vector<UnitEffectRule>& rules);
void BeginAdditionalUnitEffectFrame();
void EndAdditionalUnitEffectFrame();
}
