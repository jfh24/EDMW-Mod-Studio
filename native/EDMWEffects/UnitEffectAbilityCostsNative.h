#pragma once
#include <array>
#include <cstdint>

namespace edmw {
// Resource order is food, wood, gold, stone. Costs are charged once for each
// eligible recipient. Call Pay only at synchronized activation, before applying
// any output. It rechecks the complete group before making native deductions.
bool ValidateNativeEffectCosts();
bool CanPayEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count);
bool PayEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count);
// Refunds a previously paid activation that could not apply any output. Native
// resource headroom is respected if income arrived while a warmup was pending.
bool RefundEffectCost(void* owner, const std::array<uint32_t, 4>& costs, uint32_t count);
}
