#pragma once
#include <cmath>
#include <cstdint>
#include <string>

namespace edmw {
// Stable IDs shared with CivilizationUnitEffectStat. Unknown IDs fail closed.
enum class UnitEffectStat : uint32_t {
    LineOfSight = 0, Health = 1, AttackDamage = 2, AttackRange = 3,
    MinimumRange = 4, AttackInterval = 5, SplashRadius = 6, MovementSpeed = 7,
    Acceleration = 8, TurretTurnSpeed = 9, ArmorShock = 10, ArmorArrows = 11,
    ArmorPierce = 12, ArmorGuns = 13, FuelCapacity = 14, Population = 15,
    TransportCapacity = 16, BuildCoefficient = 17, GatherFieldFood = 18,
    GatherGold = 19, GatherStone = 20, GatherWood = 21, GatherAnimalFood = 22,
    GatherTreeFood = 23, ModelScale = 24, RequiredSpace = 25, ResourceAmount = 26,
    SteeringRotation = 27, Altitude = 28, Accuracy = 29, Armor = 30,
    HealthRegeneration = 31, TurnRate = 32, GatherRate = 33, CarryCapacity = 34,
    RepairRate = 35, FoodCost = 36, WoodCost = 37, GoldCost = 38, StoneCost = 39,
    TrainingTime = 40, ProductionCoefficient = 41, ConstructionTime = 42,
    GarrisonCapacity = 43, PopulationProvided = 44, FootprintWidth = 45,
    FootprintDepth = 46, BuildLimit = 47, QueueCapacity = 48, ProductionRate = 49,
    BuildRate = 50
};
inline bool KnownUnitEffectStat(UnitEffectStat stat) { return static_cast<uint32_t>(stat) <= 50; }
inline bool UnitEffectIsLimit(UnitEffectStat stat) {
    return stat == UnitEffectStat::BuildLimit || stat == UnitEffectStat::QueueCapacity;
}
inline bool SupportedUnitEffectStat(UnitEffectStat stat) {
    return KnownUnitEffectStat(stat);
}
inline float UnitEffectAmountLimit(UnitEffectStat stat) {
    switch (stat) {
    case UnitEffectStat::LineOfSight: return 32.0f;
    case UnitEffectStat::AttackRange: case UnitEffectStat::MinimumRange:
    case UnitEffectStat::SplashRadius: case UnitEffectStat::FootprintWidth:
    case UnitEffectStat::FootprintDepth: case UnitEffectStat::RequiredSpace: return 64.0f;
    case UnitEffectStat::AttackInterval: case UnitEffectStat::TrainingTime:
    case UnitEffectStat::ConstructionTime: return 3600.0f;
    case UnitEffectStat::MovementSpeed: case UnitEffectStat::Acceleration:
    case UnitEffectStat::TurretTurnSpeed: case UnitEffectStat::SteeringRotation:
    case UnitEffectStat::TurnRate: return 1000.0f;
    case UnitEffectStat::ModelScale: case UnitEffectStat::Accuracy:
    case UnitEffectStat::ProductionRate: return 100.0f;
    case UnitEffectStat::BuildCoefficient: case UnitEffectStat::GatherFieldFood:
    case UnitEffectStat::GatherGold: case UnitEffectStat::GatherStone:
    case UnitEffectStat::GatherWood: case UnitEffectStat::GatherAnimalFood:
    case UnitEffectStat::GatherTreeFood:
    case UnitEffectStat::Altitude: case UnitEffectStat::HealthRegeneration:
    case UnitEffectStat::GatherRate: case UnitEffectStat::RepairRate:
    case UnitEffectStat::BuildRate: case UnitEffectStat::ProductionCoefficient: return 10000.0f;
    default: return 100000.0f;
    }
}
inline bool UnitEffectRequiresWhole(UnitEffectStat stat) {
    switch (stat) {
    case UnitEffectStat::Health: case UnitEffectStat::AttackDamage:
    case UnitEffectStat::ArmorShock: case UnitEffectStat::ArmorArrows:
    case UnitEffectStat::ArmorPierce: case UnitEffectStat::ArmorGuns:
    case UnitEffectStat::FuelCapacity: case UnitEffectStat::Population:
    case UnitEffectStat::TransportCapacity: case UnitEffectStat::BuildCoefficient:
    case UnitEffectStat::GatherFieldFood: case UnitEffectStat::GatherGold:
    case UnitEffectStat::GatherStone: case UnitEffectStat::GatherWood:
    case UnitEffectStat::GatherAnimalFood: case UnitEffectStat::GatherTreeFood:
    case UnitEffectStat::RequiredSpace: case UnitEffectStat::ResourceAmount:
    case UnitEffectStat::Armor: case UnitEffectStat::GatherRate:
    case UnitEffectStat::CarryCapacity: case UnitEffectStat::RepairRate:
    case UnitEffectStat::FoodCost: case UnitEffectStat::WoodCost:
    case UnitEffectStat::GoldCost: case UnitEffectStat::StoneCost:
    case UnitEffectStat::ProductionCoefficient:
    case UnitEffectStat::GarrisonCapacity: case UnitEffectStat::PopulationProvided:
    case UnitEffectStat::FootprintWidth: case UnitEffectStat::FootprintDepth:
    case UnitEffectStat::BuildLimit: case UnitEffectStat::QueueCapacity:
    case UnitEffectStat::BuildRate: return true;
    default: return false;
    }
}
inline bool ValidUnitEffectAmount(UnitEffectStat stat, float amount) {
    return KnownUnitEffectStat(stat) && std::isfinite(amount) && amount != 0.0f &&
        std::abs(amount) <= UnitEffectAmountLimit(stat) &&
        (!UnitEffectRequiresWhole(stat) || amount == std::trunc(amount)) &&
        (!UnitEffectIsLimit(stat) || (amount >= 1.0f && amount == std::trunc(amount)));
}
inline float ApplyMovementAmount(float nativeSpeed, float amount) {
    if (nativeSpeed <= 0.0f) return nativeSpeed;
    const auto adjusted = nativeSpeed + amount;
    return adjusted > 0.0f ? adjusted : 0.0f;
}
inline const char* UnitEffectStatName(UnitEffectStat stat) {
    constexpr const char* names[] = {
        "line of sight", "health", "attack damage", "attack range", "minimum attack range",
        "attack interval", "splash radius", "movement speed", "acceleration", "turret turn speed",
        "armor against shock", "armor against arrows", "armor against piercing", "armor against guns",
        "fuel capacity", "population cost", "transport capacity", "construction interval",
        "field food gather interval", "gold gather interval", "stone gather interval", "wood gather interval",
        "animal food gather interval", "tree food gather interval", "model scale", "required space",
        "resource amount", "steering rotation", "altitude", "accuracy", "all armor", "health regeneration",
        "steering rotation", "all gather intervals", "carry capacity", "repair parameter", "food cost",
        "wood cost", "gold cost", "stone cost", "training time", "production parameter", "construction time",
        "garrison capacity", "population provided", "footprint width", "footprint depth", "build limit",
        "queue capacity", "production rate", "construction interval"
    };
    return KnownUnitEffectStat(stat) ? names[static_cast<uint32_t>(stat)] : "unknown stat";
}
inline std::string UnitEffectAmountText(float amount) {
    auto value = std::to_string(amount);
    if (value.find('.') != std::string::npos) {
        while (!value.empty() && value.back() == '0') value.pop_back();
        if (!value.empty() && value.back() == '.') value.pop_back();
    }
    return (amount > 0.0f ? "+" : "") + value;
}
}
