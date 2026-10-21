#pragma once
#include <cstdint>

namespace edmw {
// These adapters use the supported game's complete conversion/replacement
// routines. Call only from synchronized simulation after restoring all custom
// projections on the original actor. A successful call can retire that actor;
// use the returned actor for every subsequent operation.
bool ValidateNativeUnitActions();
void* TransferEffectUnit(void* actor, void* newOwner);
void* TransformEffectUnit(void* actor, uint32_t object);
}
