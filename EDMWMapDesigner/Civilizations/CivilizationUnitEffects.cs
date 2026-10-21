using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Civilizations;

public enum CivilizationUnitEffectTrigger { Proximity, Activated, ProximityAbility }
// These identities are persisted in projects and version 4+ runtime files. Never reorder or reuse them.
public enum CivilizationUnitEffectStat
{
    LineOfSight = 0, Health = 1, AttackDamage = 2, AttackRange = 3, MinimumRange = 4,
    AttackInterval = 5, SplashRadius = 6, MovementSpeed = 7, Acceleration = 8, TurretTurnSpeed = 9,
    ArmorShock = 10, ArmorArrows = 11, ArmorPierce = 12, ArmorGuns = 13, FuelCapacity = 14,
    Population = 15, TransportCapacity = 16, BuildCoefficient = 17, GatherFieldFood = 18,
    GatherGold = 19, GatherStone = 20, GatherWood = 21, GatherAnimalFood = 22, GatherTreeFood = 23,
    ModelScale = 24, RequiredSpace = 25, ResourceAmount = 26, SteeringRotation = 27, Altitude = 28,
    Accuracy = 29, Armor = 30, HealthRegeneration = 31, TurnRate = 32, GatherRate = 33,
    CarryCapacity = 34, RepairRate = 35, FoodCost = 36, WoodCost = 37, GoldCost = 38,
    StoneCost = 39, TrainingTime = 40, ProductionCoefficient = 41, ConstructionTime = 42,
    GarrisonCapacity = 43, PopulationProvided = 44, FootprintWidth = 45, FootprintDepth = 46,
    BuildLimit = 47, QueueCapacity = 48, ProductionRate = 49, BuildRate = 50
}
public enum CivilizationUnitEffectActivationMode { Toggle, Timed, Instant }
public enum CivilizationUnitEffectOutputKind { StatModifier, OwnershipTransfer, Transform, Spawn }
public enum CivilizationUnitEffectOwner { SourcePlayer, AffectedPlayer, SpecificPlayer }
public enum CivilizationUnitEffectTargetPlayers { Own, Other, Any }

public sealed record CivilizationUnitEffectOutput(CivilizationUnitEffectStat Stat, decimal Amount)
{
    public CivilizationUnitEffectOutputKind Kind { get; init; }
    public int? ObjectId { get; init; }
    public int Count { get; init; } = 1;
    public CivilizationUnitEffectOwner Owner { get; init; }
    public int PlayerSlot { get; init; } = 1;
}

public sealed record CivilizationUnitEffectStatDefinition(CivilizationUnitEffectStat Stat, string Name,
    string Unit, decimal MinimumAmount, decimal MaximumAmount, int DecimalPlaces, decimal DefaultAmount,
    string Help, bool RuntimeSupported, string PropertySuffix)
{
    public override string ToString() => Name;
}

/// <summary>An authored attachment; the source object and civilization belong to its enclosing entity.</summary>
public sealed record CivilizationUnitEffect
{
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Name { get; init; } = "New effect";
    [JsonRequired] public bool Enabled { get; init; } = true;
    [JsonRequired] public CivilizationUnitEffectTrigger Trigger { get; init; } = CivilizationUnitEffectTrigger.Proximity;
    public CivilizationUnitEffectStat Stat { get; init; } = CivilizationUnitEffectStat.LineOfSight;
    [JsonRequired] public int TargetObjectId { get; init; }
    // The scalar remains the first-target mirror for older project readers.
    public IReadOnlyList<int>? TargetObjectIds { get; init; }
    public decimal Amount { get; init; } = 3;
    // Stat/Amount remain the legacy first-output mirror for existing project readers.
    // All current consumers use GetOutputs; a trigger and button belong to the whole output list.
    public IReadOnlyList<CivilizationUnitEffectOutput>? Outputs { get; init; }
    public CivilizationUnitEffectTargetPlayers TargetPlayers { get; init; }
    public int ProximityDelaySeconds { get; init; }
    public int MaxAffectedTargets { get; init; }
    public int MaxConcurrentTargets { get; init; }
    public int FoodCost { get; init; }
    public int WoodCost { get; init; }
    public int GoldCost { get; init; }
    public int StoneCost { get; init; }
    [JsonRequired] public int RadiusTiles { get; init; } = 5;
    [JsonRequired] public CivilizationUnitEffectActivationMode ActivationMode { get; init; } = CivilizationUnitEffectActivationMode.Timed;
    [JsonRequired] public int DurationSeconds { get; init; } = 30;
    [JsonRequired] public string ButtonCaption { get; init; } = "Activate ability";
    [JsonRequired] public int ButtonIconId { get; init; }
    [JsonRequired] public int ButtonSlot { get; init; } = 13;
    // Assigned only to a portable export after reserving an unused native command.
    public int? NativeTechnologyId { get; init; }
    // Per-recipient commands for an exported proximity-granted ability.
    public IReadOnlyDictionary<int, int>? NativeTargetTechnologyIds { get; init; }
}

/// <summary>Custom attachments for experimental proximity effects and session-only activated abilities.</summary>
public static class CivilizationUnitEffects
{
    public const string PropertyKey = "runtime.unitEffects";
    public const int MaximumAttachments = 64;
    public const int MaximumOutputs = 64;
    public static IReadOnlyList<CivilizationUnitEffectStatDefinition> StatChoices { get; } = CreateStatChoices();

    public static IReadOnlyList<CivilizationUnitEffectOutput> GetOutputs(CivilizationUnitEffect effect) =>
        effect.Outputs ?? [new(effect.Stat, effect.Amount)];

    public static IReadOnlyList<CivilizationUnitEffectOutput> GetStatOutputs(CivilizationUnitEffect effect) =>
        GetOutputs(effect).Where(output => output.Kind == CivilizationUnitEffectOutputKind.StatModifier).ToArray();

    public static string DescribeOwner(CivilizationUnitEffectOutput output) => output.Owner switch
    {
        CivilizationUnitEffectOwner.AffectedPlayer => "the affected player's control",
        CivilizationUnitEffectOwner.SpecificPlayer => $"Player {output.PlayerSlot}",
        _ => "the source player's control"
    };

    public static IReadOnlyList<int> GetTargetObjectIds(CivilizationUnitEffect effect) =>
        effect.TargetObjectIds is null ? [effect.TargetObjectId] : effect.TargetObjectIds.Distinct().Order().ToArray();

    public static bool IsLimit(CivilizationUnitEffectStat stat) =>
        stat is CivilizationUnitEffectStat.BuildLimit or CivilizationUnitEffectStat.QueueCapacity;

    public static string DescribeOutput(CivilizationUnitEffectOutput output, Func<int, string>? objectName = null)
    {
        var target = output.ObjectId is int id ? objectName?.Invoke(id) ?? "the chosen unit or building" : "a unit or building";
        if (output.Kind != CivilizationUnitEffectOutputKind.StatModifier) return output.Kind switch
        {
            CivilizationUnitEffectOutputKind.OwnershipTransfer => "Transfer ownership to " + DescribeOwner(output),
            CivilizationUnitEffectOutputKind.Transform => "Change into " + target,
            CivilizationUnitEffectOutputKind.Spawn => $"Spawn {output.Count} × {target} for " + DescribeOwner(output),
            _ => throw new InvalidDataException("Select a supported effect output.")
        };
        var stat = GetStatDefinition(output.Stat);
        return IsLimit(output.Stat)
            ? $"Limit {output.Amount.ToString("0", CultureInfo.InvariantCulture)} {stat.Name.ToLowerInvariant()}"
            : output.Amount.ToString("+0.####;-0.####", CultureInfo.InvariantCulture) + " " + stat.Name.ToLowerInvariant();
    }

    private static CivilizationUnitEffect Normalize(CivilizationUnitEffect effect)
    {
        var outputs = GetOutputs(effect).ToArray();
        var firstStat = outputs.FirstOrDefault(output => output.Kind == CivilizationUnitEffectOutputKind.StatModifier);
        var targets = GetTargetObjectIds(effect);
        return effect with { Outputs = Array.AsReadOnly(outputs), Stat = firstStat?.Stat ?? CivilizationUnitEffectStat.LineOfSight, Amount = firstStat?.Amount ?? 0,
            TargetObjectId = targets[0], TargetObjectIds = effect.TargetObjectIds is null ? null : Array.AsReadOnly(targets.ToArray()),
            DurationSeconds = effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility || effect.ActivationMode == CivilizationUnitEffectActivationMode.Instant ? 0 : effect.DurationSeconds,
            NativeTargetTechnologyIds = effect.NativeTargetTechnologyIds is null ? null :
                new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(effect.NativeTargetTechnologyIds.OrderBy(pair => pair.Key)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)) };
    }

    public static CivilizationUnitEffectStatDefinition GetStatDefinition(CivilizationUnitEffectStat stat) =>
        StatChoices.FirstOrDefault(choice => choice.Stat == stat) ??
        throw new InvalidDataException("Select a supported effect output stat.");

    private static IReadOnlyList<CivilizationUnitEffectStatDefinition> CreateStatChoices()
    {
        CivilizationUnitEffectStatDefinition Stat(CivilizationUnitEffectStat stat, string name, string suffix,
            string unit = "points", decimal limit = 100_000, int decimals = 0, decimal initial = 1,
            string? help = null) => new(stat, name, unit, IsLimit(stat) ? 1 : -Math.Min(limit, decimals == 4 ? 1000 : limit), Math.Min(limit, decimals == 4 ? 1000 : limit), decimals, initial,
                help ?? $"Adds to {name.ToLowerInvariant()} while the effect is active. Negative values subtract.",
                true, suffix);
        return Array.AsReadOnly(new[]
        {
            Stat(CivilizationUnitEffectStat.LineOfSight, "Line of sight", "sight", "tiles", 32, 2, 3),
            Stat(CivilizationUnitEffectStat.Health, "Health", "health"),
            Stat(CivilizationUnitEffectStat.AttackDamage, "Attack damage", "attack"),
            Stat(CivilizationUnitEffectStat.AttackRange, "Attack range", "attackRange", "tiles", 64, 2),
            Stat(CivilizationUnitEffectStat.MinimumRange, "Minimum attack range", "minimumRange", "tiles", 64, 2),
            Stat(CivilizationUnitEffectStat.AttackInterval, "Attack interval", "attackInterval", "seconds", 3600, 3, 0.1m,
                "Adds seconds between attacks. A negative adjustment makes attacks faster."),
            Stat(CivilizationUnitEffectStat.SplashRadius, "Splash radius", "splashRadius", "tiles", 64, 2),
            Stat(CivilizationUnitEffectStat.Accuracy, "Accuracy", "accuracy", "percentage points", 100, 2, -20,
                "Adjusts outgoing damage hit chance from a 100% baseline. Negative values create misses; chance is clamped to 0–100%. Projectile flight stays native."),
            Stat(CivilizationUnitEffectStat.Armor, "Armor", "armor"),
            Stat(CivilizationUnitEffectStat.ArmorShock, "Armor against shock", "armorShock"),
            Stat(CivilizationUnitEffectStat.ArmorArrows, "Armor against arrows", "armorArrows"),
            Stat(CivilizationUnitEffectStat.ArmorPierce, "Armor against piercing", "armorPierce"),
            Stat(CivilizationUnitEffectStat.ArmorGuns, "Armor against guns", "armorGuns"),
            Stat(CivilizationUnitEffectStat.HealthRegeneration, "Health regeneration", "regeneration", "health / second", 10000, 3),
            // Keep four decimal places within the precision of the native float32 amount.
            Stat(CivilizationUnitEffectStat.MovementSpeed, "Movement speed", "speed", "internal units", 1000, 4, 0.1m,
                "Adds a fixed amount to current movement speed: 1.0 + 0.2 becomes 1.2. Conversion to tiles per second is unverified. Immobilized units remain stopped."),
            Stat(CivilizationUnitEffectStat.Acceleration, "Acceleration coefficient", "acceleration", "coefficient", 10000, 4, 0.1m),
            Stat(CivilizationUnitEffectStat.TurretTurnSpeed, "Turret turn coefficient", "turretTurnSpeed", "coefficient", 10000, 4, 0.1m),
            Stat(CivilizationUnitEffectStat.TurnRate, "Turn rate", "turnRate", "coefficient", 10000, 4, 0.1m),
            Stat(CivilizationUnitEffectStat.SteeringRotation, "Steering rotation", "steeringRotation", "coefficient", 10000, 4, 0.1m),
            Stat(CivilizationUnitEffectStat.Altitude, "Altitude", "altitude", "raw units", 10000, 3),
            Stat(CivilizationUnitEffectStat.FuelCapacity, "Fuel capacity", "fuelCapacity"),
            Stat(CivilizationUnitEffectStat.TransportCapacity, "Transport capacity", "transportCapacity", "passengers"),
            Stat(CivilizationUnitEffectStat.GarrisonCapacity, "Garrison capacity", "garrisonCapacity", "occupants", help: "Changes the same native container capacity as Transport capacity. Garrison and transport occupants share this capacity."),
            Stat(CivilizationUnitEffectStat.Population, "Population cost", "population", "population"),
            Stat(CivilizationUnitEffectStat.PopulationProvided, "Population provided", "populationProvided", "population"),
            Stat(CivilizationUnitEffectStat.FoodCost, "Food cost", "foodCost", "food", help: "Adds to the food cost of units and research produced by the affected unit or building. Negative values discount each queued purchase."),
            Stat(CivilizationUnitEffectStat.WoodCost, "Wood cost", "woodCost", "wood", help: "Adds to the wood cost of units and research produced by the affected unit or building."),
            Stat(CivilizationUnitEffectStat.GoldCost, "Gold cost", "goldCost", "gold", help: "Adds to the gold cost of units and research produced by the affected unit or building."),
            Stat(CivilizationUnitEffectStat.StoneCost, "Stone cost", "stoneCost", "stone", help: "Adds to the stone cost of units and research produced by the affected unit or building."),
            Stat(CivilizationUnitEffectStat.TrainingTime, "Training time", "trainTime", "seconds", 3600, 3, help: "Adds seconds to units and research produced by the affected unit or building. Negative values shorten production."),
            Stat(CivilizationUnitEffectStat.ConstructionTime, "Build time", "buildTime", "seconds", 3600, 3, help: "Adds seconds to the affected producer's queue, including its units and research. Combines with training time if both are selected."),
            Stat(CivilizationUnitEffectStat.ProductionCoefficient, "Production parameter", "productionCoefficient", "native milliseconds", 10000, 0, 1000, "Adds to the native production duration parameter for this producer's units and research, preserving the game's existing duration multipliers."),
            Stat(CivilizationUnitEffectStat.ProductionRate, "Production speed multiplier", "productionRate", "multiplier", 100, 4, 0.1m, "Adds to a production speed multiplier of 1. For example, +0.5 produces units and research at 1.5 times the native speed."),
            Stat(CivilizationUnitEffectStat.QueueCapacity, "Production queue capacity", "queueCapacity", "maximum queued orders", initial: 5,
                help: "Sets a custom maximum for this producer's active and waiting production orders. The smallest active limit wins; removing the effect lifts this limit. Existing orders are kept."),
            Stat(CivilizationUnitEffectStat.BuildLimit, "Build limit", "buildLimit", "maximum buildings per type", initial: 5,
                help: "Sets a custom maximum for each building type produced by the affected producer. Counts the owner's existing buildings and reserved queued orders across all producers. The smallest active limit wins; removing the effect lifts this limit."),
            Stat(CivilizationUnitEffectStat.GatherRate, "Gather interval", "gatherRate", "milliseconds", 10000, 0, 100,
                "Adds milliseconds to all six native gathering intervals. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.CarryCapacity, "Carry capacity", "carryCapacity", "resources"),
            Stat(CivilizationUnitEffectStat.BuildRate, "Build work interval", "buildRate", "milliseconds", 10000, 0, 100,
                "Adds milliseconds to the same native construction work interval as Construction parameter. Positive values slow work; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.RepairRate, "Repair parameter", "repairRate", "raw units", 10000, 0, 1,
                "Adds to the game's native integer repair parameter. Its conversion to repair throughput is unverified."),
            Stat(CivilizationUnitEffectStat.BuildCoefficient, "Construction parameter", "buildCoefficient", "milliseconds", 10000, 0, 100,
                "Adds milliseconds to the native construction work interval. Positive values slow work; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherFieldFood, "Field food gather interval", "gatherFieldFoodCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native field food gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherGold, "Gold gather interval", "gatherGoldCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native gold gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherStone, "Stone gather interval", "gatherStoneCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native stone gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherWood, "Wood gather interval", "gatherWoodCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native wood gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherAnimalFood, "Animal / fish food gather interval", "gatherAnimalFoodCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native animal and fish food gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.GatherTreeFood, "Tree food gather interval", "gatherTreeFoodCoefficient", "milliseconds", 10000, 0, 100, "Adds to the native tree food gathering interval. Positive values slow gathering; negative values speed it up."),
            Stat(CivilizationUnitEffectStat.ResourceAmount, "Resource amount", "resourceAmount", "resources", 100000, 0),
            Stat(CivilizationUnitEffectStat.RequiredSpace, "Required space", "requiredSpace", "tiles", 64, 0, 1,
                "Changes the native square footprint on both axes. The resulting extent is clamped between 0 and 64 tiles."),
            Stat(CivilizationUnitEffectStat.FootprintWidth, "Square footprint (width)", "footprintWidth", "tiles", 64, 0, 1,
                "Changes the native square footprint on both axes. Width and depth use the same square extent; they cannot create independent rectangular footprints."),
            Stat(CivilizationUnitEffectStat.FootprintDepth, "Square footprint (depth)", "footprintDepth", "tiles", 64, 0, 1,
                "Changes the native square footprint on both axes. Depth and width use the same square extent; they cannot create independent rectangular footprints."),
            Stat(CivilizationUnitEffectStat.ModelScale, "Model scale", "modelScaleFactor", "scale", 100, 4, 0.1m)
        });
    }

    public static void ValidateAmount(CivilizationUnitEffectStat stat, decimal amount)
    {
        var definition = GetStatDefinition(stat);
        if (amount == 0 || amount < definition.MinimumAmount || amount > definition.MaximumAmount ||
            amount != decimal.Round(amount, definition.DecimalPlaces))
            throw new InvalidDataException($"{definition.Name}: enter a {(IsLimit(stat) ? "limit" : "nonzero adjustment")} from {definition.MinimumAmount.ToString(CultureInfo.InvariantCulture)} to {definition.MaximumAmount.ToString(CultureInfo.InvariantCulture)} {definition.Unit}" +
                (definition.DecimalPlaces == 0 ? " using whole numbers." : $" with at most {definition.DecimalPlaces} decimal places."));
    }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static bool Supports(CivilizationEntityDraft entity) =>
        entity.Category is CivilizationCategory.Units or CivilizationCategory.Buildings;

    public static bool HasAttachments(CivilizationEntityDraft entity) => Read(entity).Count > 0;

    public static IReadOnlyList<CivilizationUnitEffect> Read(CivilizationEntityDraft entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.Properties.TryGetValue(PropertyKey, out var value)) return [];
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{entity.Name}: effect attachments are empty or invalid.");
        try
        {
            var effects = JsonSerializer.Deserialize<List<CivilizationUnitEffect>>(value, Options)
                ?? throw new InvalidDataException($"{entity.Name}: effect attachments must be a list.");
            using var document = JsonDocument.Parse(value);
            foreach (var attachment in document.RootElement.EnumerateArray())
                if (attachment.ValueKind == JsonValueKind.Object &&
                    (!attachment.TryGetProperty("outputs", out var outputs) || outputs.ValueKind == JsonValueKind.Null) &&
                    (!attachment.TryGetProperty("stat", out _) || !attachment.TryGetProperty("amount", out _)))
                    throw new InvalidDataException($"{entity.Name}: a legacy effect must contain both stat and amount, or an output list.");
            Validate(entity, effects);
            return effects.Select(Normalize).ToArray();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{entity.Name}: effect attachments contain invalid data.", error);
        }
    }

    public static void Write(CivilizationEntityDraft entity, IEnumerable<CivilizationUnitEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(effects);
        var list = effects.ToList();
        Validate(entity, list);
        list = list.Select(Normalize).ToList();
        if (list.Count == 0) entity.Properties.Remove(PropertyKey);
        else entity.Properties[PropertyKey] = JsonSerializer.Serialize(list, Options);
    }

    public static void Validate(CivilizationEntityDraft entity) => _ = Read(entity);

    public static void Validate(CivilizationEntityDraft entity, IReadOnlyList<CivilizationUnitEffect> effects)
    {
        if (effects.Count == 0) return;
        if (!Supports(entity) || entity.SourceObjectId is not (>= 0 and < 100_000))
            throw new InvalidDataException($"{entity.Name}: custom unit effects require a unit or building from the game catalog.");
        if (effects.Count > MaximumAttachments)
            throw new InvalidDataException($"{entity.Name}: at most {MaximumAttachments} effects can be attached.");
        var ids = new HashSet<Guid>();
        var slots = new HashSet<int>();
        foreach (var effect in effects)
        {
            if (effect is null || !Guid.TryParse(effect.Id, out var id) || id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException($"{entity.Name}: every effect needs a unique identifier.");
            if (string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 100)
                throw new InvalidDataException("An effect name must contain 1 to 100 characters.");
            if (!Enum.IsDefined(effect.Trigger) || !Enum.IsDefined(effect.ActivationMode) || !Enum.IsDefined(effect.TargetPlayers))
                throw new InvalidDataException($"{effect.Name}: unsupported trigger or activation mode.");
            if (effect.ProximityDelaySeconds is < 0 or > 3600 || effect.MaxAffectedTargets is < 0 or > 100000 ||
                effect.MaxConcurrentTargets is < 0 or > 100000 || new[] { effect.FoodCost, effect.WoodCost, effect.GoldCost, effect.StoneCost }.Any(cost => cost is < 0 or > 100000) ||
                (effect.Trigger == CivilizationUnitEffectTrigger.Proximity && (effect.FoodCost != 0 || effect.WoodCost != 0 || effect.GoldCost != 0 || effect.StoneCost != 0)) ||
                (effect.Trigger == CivilizationUnitEffectTrigger.Activated && (effect.ProximityDelaySeconds != 0 || effect.MaxAffectedTargets != 0 || effect.MaxConcurrentTargets != 0)))
                throw new InvalidDataException($"{effect.Name}: proximity delay must be 0–3,600 seconds; limits and costs must be 0–100,000. Costs belong to activated abilities and limits belong to proximity effects.");
            var targets = GetTargetObjectIds(effect);
            if (targets.Count == 0 || targets.Any(target => target is < 0 or >= 100_000) || effect.ButtonIconId is < 0 or >= 100_000)
                throw new InvalidDataException($"{effect.Name}: select a valid target and button icon source.");
            var outputs = GetOutputs(effect);
            if (outputs.Count is < 1 or > MaximumOutputs)
                throw new InvalidDataException($"{effect.Name}: choose from 1 to {MaximumOutputs} outputs.");
            var stats = new HashSet<CivilizationUnitEffectStat>();
            var actions = new HashSet<CivilizationUnitEffectOutputKind>();
            foreach (var output in outputs)
            {
                if (output is null || !Enum.IsDefined(output.Kind))
                    throw new InvalidDataException($"{effect.Name}: select a supported output type.");
                if (output.Kind != CivilizationUnitEffectOutputKind.StatModifier)
                {
                    if (!Enum.IsDefined(output.Owner) || output.PlayerSlot is < 1 or > 8 ||
                        output.Count is < 1 or > 64 || output.Kind != CivilizationUnitEffectOutputKind.Spawn && output.Count != 1 ||
                        (output.Kind is CivilizationUnitEffectOutputKind.Transform or CivilizationUnitEffectOutputKind.Spawn
                            ? output.ObjectId is not (>= 0 and < 100_000) : output.ObjectId is not null) ||
                        output.Kind != CivilizationUnitEffectOutputKind.Spawn && !actions.Add(output.Kind))
                        throw new InvalidDataException($"{effect.Name}: choose a valid action, object, owner and count (1–64).");
                    continue;
                }
                if (!Enum.IsDefined(output.Stat) || !stats.Add(output.Stat) || output.ObjectId is not null || output.Count != 1 ||
                    output.Owner != CivilizationUnitEffectOwner.SourcePlayer || output.PlayerSlot != 1)
                    throw new InvalidDataException($"{effect.Name}: each stat output must be valid and appear only once.");
                ValidateAmount(output.Stat, output.Amount);
            }
            if (outputs.Where(output => output.Kind == CivilizationUnitEffectOutputKind.Spawn).Sum(output => output.Count) > 64)
                throw new InvalidDataException($"{effect.Name}: spawn outputs can create at most 64 objects in one activation.");
            if (effect.ActivationMode == CivilizationUnitEffectActivationMode.Instant && stats.Count != 0)
                throw new InvalidDataException($"{effect.Name}: instant abilities contain permanent actions only. Choose a toggle or duration for stat modifiers.");
            if (effect.Trigger != CivilizationUnitEffectTrigger.Proximity && effect.TargetPlayers != CivilizationUnitEffectTargetPlayers.Own)
                throw new InvalidDataException($"{effect.Name}: target-player filtering belongs to proximity effects.");
            if (effect.RadiusTiles is < 1 or > 64)
                throw new InvalidDataException($"{effect.Name}: proximity radius must be between 1 and 64 tiles.");
            if (effect.DurationSeconds > 3600 || effect.DurationSeconds < (effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility || effect.ActivationMode == CivilizationUnitEffectActivationMode.Instant ? 0 : 1))
                throw new InvalidDataException($"{effect.Name}: duration must be greater than zero and at most 3,600 seconds.");
            if (effect.ButtonSlot is < 1 or > 41 || string.IsNullOrWhiteSpace(effect.ButtonCaption) || effect.ButtonCaption.Length > 40)
                throw new InvalidDataException($"{effect.Name}: choose a button slot from 1 to 41 and a caption of 1 to 40 characters.");
            if (effect.NativeTechnologyId is not null and not (>= 10000 and < 15000))
                throw new InvalidDataException($"{effect.Name}: the exported native command identity is invalid.");
            if (effect.NativeTargetTechnologyIds is { } commands &&
                (effect.Trigger != CivilizationUnitEffectTrigger.ProximityAbility || commands.Count != targets.Count ||
                 !commands.Keys.Order().SequenceEqual(targets) || commands.Values.Any(value => value is < 10000 or >= 15000) ||
                 commands.Values.Distinct().Count() != commands.Count ||
                 (targets.Count == 1 ? effect.NativeTechnologyId != commands[targets[0]] : effect.NativeTechnologyId is not null)))
                throw new InvalidDataException($"{effect.Name}: the exported recipient command identities are invalid.");
            if (effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility &&
                (effect.ActivationMode == CivilizationUnitEffectActivationMode.Timed ||
                 effect.NativeTechnologyId is not null && (targets.Count != 1 || effect.NativeTargetTechnologyIds is null)))
                throw new InvalidDataException($"{effect.Name}: proximity-granted abilities must use a toggle and per-target command identities.");
            if (effect.Trigger == CivilizationUnitEffectTrigger.Activated)
            {
                if (targets.Count != 1 || targets[0] != entity.SourceObjectId)
                    throw new InvalidDataException($"{effect.Name}: activated abilities affect selected units or buildings of the source type.");
                if (effect.Enabled && !slots.Add(effect.ButtonSlot))
                    throw new InvalidDataException($"{effect.Name}: enabled activated abilities must use different button slots.");
            }
        }
    }

    public static string Describe(CivilizationUnitEffect effect, Func<int, string>? objectName = null)
    {
        var adjustments = string.Join(", ", GetOutputs(effect).Select(output => DescribeOutput(output, objectName)));
        var target = string.Join(", ", GetTargetObjectIds(effect).Select(id => objectName?.Invoke(id) ?? $"object {id}"));
        var behavior = effect.Trigger switch
        {
            CivilizationUnitEffectTrigger.Proximity => $"other nearby {target}: {adjustments} within {effect.RadiusTiles.ToString("0.##", CultureInfo.InvariantCulture)} tiles",
            CivilizationUnitEffectTrigger.ProximityAbility => $"other nearby {target} gain a toggle: {adjustments} within {effect.RadiusTiles.ToString("0.##", CultureInfo.InvariantCulture)} tiles",
            _ => $"selected: {adjustments} · {(effect.ActivationMode == CivilizationUnitEffectActivationMode.Instant ? "instant" : effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle ? "toggle" : effect.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " seconds")}"
        };
        return $"{effect.Name} · {(effect.Enabled ? "Enabled" : "Disabled")} · {behavior}";
    }
}
