#pragma once
#include <array>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include "UnitEffectStats.h"

namespace edmw {
class ProportionalHealth {
public:
    int32_t Resize(int32_t health, int32_t previousMaximum, int32_t maximum) {
        if (health <= 0 || previousMaximum <= 0 || maximum <= 0) { remainder_ = 0; return health; }
        if (maximum == previousMaximum) return health;
        if (health >= previousMaximum) remainder_ = 0;
        const auto exact = (static_cast<double>(health) + remainder_) * maximum / previousMaximum;
        const auto next = static_cast<int32_t>((std::max)(1.0, (std::min)(static_cast<double>(maximum), std::floor(exact + 1e-9))));
        remainder_ = next >= maximum ? 0 : exact - next;
        return next;
    }
private:
    double remainder_ = 0;
};

class ReplacedFloatOverlay {
public:
    float Update(float observed, float amount) {
        if (!amount) {
            const auto result = active_ && observed == written_ ? baseline_ : observed;
            active_ = false;
            return result;
        }
        if (!active_ || observed != written_) baseline_ = observed;
        active_ = true;
        written_ = (std::max)(0.001f, baseline_ + amount);
        return written_;
    }
private:
    bool active_ = false;
    float baseline_ = 0.0f, written_ = 0.0f;
};

class ConsumedIntegerOverlay {
public:
    int32_t Update(int32_t observed, float desiredAmount) {
        const auto desired = std::round(static_cast<double>(desiredAmount));
        const auto adjusted = (std::max)(0.0, (std::min)(2147483647.0, static_cast<double>(observed) + desired - applied_));
        const auto next = static_cast<int32_t>(adjusted);
        applied_ += static_cast<double>(next) - observed;
        if (!desiredAmount) applied_ = 0;
        return next;
    }
    int32_t Suspend(int32_t observed) {
        suspendedVisible_ = observed;
        suspendedCanonical_ = static_cast<int32_t>((std::max)(0.0, (std::min)(2147483647.0, static_cast<double>(observed) - applied_)));
        suspended_ = true;
        return suspendedCanonical_;
    }
    int32_t Resume(int32_t observed, float desiredAmount) {
        if (!suspended_) return Update(observed, desiredAmount);
        const auto restored = (std::max)(int64_t{ 0 }, (std::min)(int64_t{ INT32_MAX },
            static_cast<int64_t>(suspendedVisible_) + observed - suspendedCanonical_));
        suspended_ = false;
        return Update(static_cast<int32_t>(restored), desiredAmount);
    }
private:
    double applied_ = 0;
    int32_t suspendedVisible_ = 0, suspendedCanonical_ = 0;
    bool suspended_ = false;
};

// Sizes come from native allocations 0x007089D8 and 0x005CD74C.
// These are borrowed snapshots: never call a native constructor/destructor on
// them, and never put their addresses into a player/global prototype registry.
struct UnitStatProjection {
    std::array<uint8_t, 0x348> prototype{};
    std::array<uint8_t, 0x1C0> statistics{};

    void Refresh(const void* sourcePrototype, const void* sourceStatistics) {
        memcpy(prototype.data(), sourcePrototype, prototype.size());
        memcpy(statistics.data(), sourceStatistics, statistics.size());
    }

    template<size_t N, class T> static T Read(const std::array<uint8_t, N>& bytes, size_t offset) {
        T value{}; memcpy(&value, bytes.data() + offset, sizeof(value)); return value;
    }
    template<size_t N, class T> static void Put(std::array<uint8_t, N>& bytes, size_t offset, T value) {
        memcpy(bytes.data() + offset, &value, sizeof(value));
    }
    template<size_t N> static void AddFloat(std::array<uint8_t, N>& bytes, size_t offset, float amount, float minimum = 0.0f) {
        const auto base = Read<N, float>(bytes, offset);
        if (!std::isfinite(base)) return;
        Put(bytes, offset, (std::max)(minimum, base + amount));
    }
    template<size_t N> static void AddInteger(std::array<uint8_t, N>& bytes, size_t offset, float amount, int32_t minimum = 0, int32_t maximum = INT32_MAX) {
        const auto base = Read<N, int32_t>(bytes, offset);
        const auto value = (std::max)(static_cast<double>(minimum), (std::min)(static_cast<double>(maximum), static_cast<double>(base) + amount));
        Put(bytes, offset, static_cast<int32_t>(std::round(value)));
    }
    void FloatPair(size_t prototypeOffset, size_t statisticsOffset, float amount, float minimum = 0.0f) {
        AddFloat(prototype, prototypeOffset, amount, minimum);
        AddFloat(statistics, statisticsOffset, amount, minimum);
    }
    void IntegerPair(size_t prototypeOffset, size_t statisticsOffset, float amount, int32_t minimum = 0) {
        AddInteger(prototype, prototypeOffset, amount, minimum);
        AddInteger(statistics, statisticsOffset, amount, minimum);
    }
    void IntegerFloatPair(size_t prototypeOffset, size_t statisticsOffset, float amount) {
        AddInteger(prototype, prototypeOffset, amount);
        AddFloat(statistics, statisticsOffset, amount);
    }
    void Reciprocal(size_t source, size_t destination) {
        const auto value = Read<0x1C0, float>(statistics, source);
        Put(statistics, destination, value > 0.0f ? 1.0f / value : 0.0f);
    }
    bool Apply(UnitEffectStat stat, float amount) {
        switch (stat) {
        case UnitEffectStat::Health: IntegerPair(0x254, 0x184, amount, 1); break;
        case UnitEffectStat::AttackDamage: IntegerFloatPair(0x208, 0x11C, amount); break;
        case UnitEffectStat::AttackRange: AddFloat(prototype, 0xEC, amount); break;
        case UnitEffectStat::MinimumRange: AddFloat(prototype, 0xE4, amount); break;
        case UnitEffectStat::AttackInterval: IntegerPair(0x204, 0x178, amount * 1000.0f, 1); break;
        case UnitEffectStat::SplashRadius: FloatPair(0xF0, 0xE4, amount); break;
        case UnitEffectStat::Acceleration:
            FloatPair(0x104, 0xEC, amount); Reciprocal(0xEC, 0x108); break;
        case UnitEffectStat::TurretTurnSpeed:
            FloatPair(0x114, 0xFC, amount); Reciprocal(0xFC, 0x118); break;
        case UnitEffectStat::ArmorShock: IntegerFloatPair(0x1E4, 0x124, amount); break;
        case UnitEffectStat::ArmorArrows: IntegerFloatPair(0x1E8, 0x128, amount); break;
        case UnitEffectStat::ArmorPierce: IntegerFloatPair(0x1EC, 0x12C, amount); break;
        case UnitEffectStat::ArmorGuns: IntegerFloatPair(0x1F0, 0x130, amount); break;
        case UnitEffectStat::Armor:
            for (size_t index = 0; index < 6; ++index) IntegerFloatPair(0x1E4 + index * 4, 0x124 + index * 4, amount);
            break;
        case UnitEffectStat::FuelCapacity: {
            const auto base = Read<0x348, int32_t>(prototype, 0x22C);
            const auto adjusted = Read<0x1C0, int32_t>(statistics, 0x170);
            const auto nativeScale = base > 0 ? static_cast<float>(adjusted) / base : 1.0f;
            AddInteger(prototype, 0x22C, amount * 1000.0f);
            AddInteger(statistics, 0x170, amount * 1000.0f * nativeScale);
            break;
        }
        case UnitEffectStat::TransportCapacity:
        case UnitEffectStat::GarrisonCapacity: AddInteger(prototype, 0x240, amount); break;
        case UnitEffectStat::BuildCoefficient:
        case UnitEffectStat::BuildRate: IntegerPair(0x20C, 0x174, amount); break;
        case UnitEffectStat::GatherFieldFood: IntegerPair(0x228, 0x168, amount); break;
        case UnitEffectStat::GatherGold: IntegerPair(0x218, 0x158, amount); break;
        case UnitEffectStat::GatherStone: IntegerPair(0x214, 0x154, amount); break;
        case UnitEffectStat::GatherWood: IntegerPair(0x210, 0x150, amount); break;
        case UnitEffectStat::GatherAnimalFood: IntegerPair(0x220, 0x160, amount); break;
        case UnitEffectStat::GatherTreeFood: IntegerPair(0x224, 0x164, amount); break;
        case UnitEffectStat::GatherRate:
            for (const auto offset : { 0x210u, 0x214u, 0x218u, 0x220u, 0x224u, 0x228u })
                IntegerPair(offset, offset - 0xC0u, amount);
            break;
        case UnitEffectStat::CarryCapacity: IntegerPair(0x268, 0x18C, amount); break;
        case UnitEffectStat::RepairRate: IntegerPair(0x248, 0x180, amount); break;
        case UnitEffectStat::RequiredSpace:
        case UnitEffectStat::FootprintWidth:
        case UnitEffectStat::FootprintDepth: AddInteger(prototype, 0x284, amount, 0, 64); break;
        case UnitEffectStat::ResourceAmount: AddInteger(prototype, 0x264, amount); break;
        case UnitEffectStat::SteeringRotation:
        case UnitEffectStat::TurnRate: AddFloat(prototype, 0x120, amount); break;
        case UnitEffectStat::Altitude: AddFloat(prototype, 0xE0, amount); break;
        default: return false;
        }
        return true;
    }
};
}
