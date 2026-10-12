using System.Globalization;

namespace EDMWMapDesigner.Civilizations;

public enum CivilizationCategory
{
    Overview, Units, Buildings, Technologies, Abilities, Economy, Ages, Assets, AI
}

public enum CivilizationFieldKind { Text, Number, Boolean, Choice, Multiline }

public sealed record CivilizationFieldDefinition(
    string Key, string Label, string Section, CivilizationFieldKind Kind,
    string[]? Options = null, string? Help = null);

/// <summary>Draft editing vocabulary. These keys are not confirmed game database offsets.</summary>
public static class CivilizationSchema
{
    private static CivilizationFieldDefinition T(string key, string label, string section,
        string? help = null) => new(key, label, section, CivilizationFieldKind.Text, Help: help);
    private static CivilizationFieldDefinition N(string key, string label, string section,
        string? help = null) => new(key, label, section, CivilizationFieldKind.Number, Help: help);
    private static CivilizationFieldDefinition B(string key, string label, string section,
        string? help = null) => new(key, label, section, CivilizationFieldKind.Boolean, Help: help);
    private static CivilizationFieldDefinition M(string key, string label, string section,
        string? help = null) => new(key, label, section, CivilizationFieldKind.Multiline, Help: help);
    private static CivilizationFieldDefinition C(string key, string label, string section,
        params string[] options) => new(key, label, section, CivilizationFieldKind.Choice, options);

    private static readonly IReadOnlyDictionary<CivilizationCategory, IReadOnlyList<CivilizationFieldDefinition>> Fields =
        new Dictionary<CivilizationCategory, IReadOnlyList<CivilizationFieldDefinition>>
        {
            [CivilizationCategory.Overview] = Array.AsReadOnly(new[]
            {
                T("overview.displayName", "Display name", "Identity"),
                T("overview.shortName", "Short name", "Identity"),
                T("overview.internalName", "Internal name", "Identity"),
                T("overview.baseCivilization", "Base civilization", "Identity"),
                M("overview.description", "Description", "Identity"),
                M("overview.history", "Historical background", "Identity"),
                B("overview.selectable", "Selectable in new games", "Availability"),
                B("overview.randomEligible", "Eligible for random selection", "Availability"),
                T("overview.allowedAges", "Supported ages", "Availability"),
                M("overview.bonuses", "Civilization bonuses", "Bonuses"),
                M("overview.teamBonuses", "Team bonuses", "Bonuses"),
                M("overview.uniqueFeatures", "Unique units and buildings", "Bonuses"),
                M("overview.notes", "Design notes", "Notes")
            }),
            [CivilizationCategory.Units] = Array.AsReadOnly(new[]
            {
                B("available", "Available to civilization", "Identity"),
                T("unit.internalName", "Internal name", "Identity"),
                T("unit.displayName", "Game display name", "Identity"),
                M("unit.description", "Description", "Identity"),
                C("unit.role", "Role", "Identity", "Infantry", "Archer", "Cavalry", "Siege", "Ship", "Aircraft", "Worker", "Support", "Hero", "Other"),
                T("unit.age", "Available age", "Availability"),
                T("unit.prerequisites", "Prerequisites", "Availability"),
                T("unit.upgradeFrom", "Upgrades from", "Availability"),
                T("unit.upgradeTo", "Upgrades to", "Availability"),
                N("unit.health", "Health", "Combat", "Must be at least 1."),
                N("unit.attack", "Attack damage", "Combat"),
                T("unit.attackType", "Weapon type ID", "Combat"),
                N("unit.attackRange", "Attack range", "Combat"),
                N("unit.minimumRange", "Minimum range", "Combat"),
                N("unit.attackInterval", "Attack interval (seconds)", "Combat"),
                N("unit.accuracy", "Accuracy (%)", "Combat", "Enter a percentage from 0 to 100."),
                N("unit.armor", "Armor", "Combat"),
                N("unit.armorShock", "Armor against shock", "Combat"),
                N("unit.armorArrows", "Armor against arrows", "Combat"),
                N("unit.armorPierce", "Armor against piercing", "Combat"),
                N("unit.armorGuns", "Armor against guns", "Combat"),
                T("unit.armorClass", "Armor class", "Combat"),
                M("unit.damageModifiers", "Damage modifiers", "Combat"),
                M("unit.attackProfiles", "Attack profiles and alternate attacks", "Combat", "Draft each attack's damage, range, rate, targets and projectile references."),
                M("unit.damageVsClass", "Damage against armor / target classes", "Combat"),
                M("unit.resistances", "Armor and damage resistances", "Combat"),
                T("unit.projectile", "Projectile reference", "Combat"),
                T("unit.targetClasses", "Allowed target classes", "Combat"),
                N("unit.splashRadius", "Splash radius", "Combat"),
                N("unit.sight", "Line of sight", "Combat"),
                N("unit.regeneration", "Health regeneration per second", "Combat"),
                B("unit.progressionIncluded", "Included in civilization progression", "Availability"),
                T("unit.progressionProducers", "Progression producer IDs", "Availability", "Existing compatible producers chosen in Ages progression."),
                N("unit.foodCost", "Food cost", "Cost & production"),
                N("unit.woodCost", "Wood cost", "Cost & production"),
                N("unit.goldCost", "Gold cost", "Cost & production"),
                N("unit.stoneCost", "Stone cost", "Cost & production"),
                N("unit.population", "Population cost", "Cost & production"),
                N("unit.trainTime", "Training time (seconds)", "Cost & production"),
                N("unit.productionCoefficient", "Production parameter", "Cost & production", "Raw technology-table production parameter; its conversion to seconds is not verified."),
                T("unit.trainedAt", "Produced at", "Cost & production"),
                M("unit.productionQueues", "Production queue and menu assignments", "Cost & production"),
                N("unit.speed", "Movement coefficient", "Movement"),
                N("unit.acceleration", "Acceleration coefficient", "Movement"),
                N("unit.turretTurnSpeed", "Turret turn coefficient", "Movement"),
                N("unit.fuelCapacity", "Fuel capacity", "Movement"),
                N("unit.turnRate", "Turn rate", "Movement"),
                C("unit.movementDomain", "Movement domain", "Movement", "Land", "Water", "Air", "Amphibious"),
                B("unit.canGarrison", "Can garrison", "Movement"),
                N("unit.transportCapacity", "Transport capacity", "Movement"),
                M("unit.transportRules", "Transport passenger rules", "Movement"),
                M("unit.garrisonRules", "Garrison eligibility and effects", "Movement"),
                M("unit.terrainRules", "Terrain and pathfinding rules", "Movement"),
                M("unit.abilities", "Abilities", "Behavior"),
                M("unit.commands", "Commands", "Behavior"),
                N("unit.gatherRate", "Gather rate", "Economy"),
                N("unit.carryCapacity", "Carry capacity", "Economy"),
                N("unit.buildRate", "Build rate", "Economy"),
                N("unit.repairRate", "Repair rate", "Economy"),
                T("unit.model", "Model / graphics reference", "Presentation"),
                N("unit.graphicsId", "Graphics reference ID", "Presentation"),
                N("unit.modelScaleFactor", "Model scale", "Presentation"),
                T("unit.modelAsset", "Replacement model asset", "Presentation", "References the stable ID of a .cem or .obj model embedded in this project. Use the model import control to choose a file."),
                T("unit.icon", "Icon reference", "Presentation"),
                T("unit.soundSet", "Sound set", "Presentation"),
                T("unit.voiceSet", "Voice reference", "Presentation"),
                M("unit.animations", "Animation references by action", "Presentation"),
                T("unit.hotkey", "Hotkey", "Presentation"),
                M("unit.notes", "Design notes", "Notes")
            }),
            [CivilizationCategory.Buildings] = Array.AsReadOnly(new[]
            {
                B("available", "Available to civilization", "Identity"),
                T("building.internalName", "Internal name", "Identity"),
                T("building.displayName", "Game display name", "Identity"),
                M("building.description", "Description", "Identity"),
                C("building.role", "Role", "Identity", "Economy", "Military", "Defense", "Research", "Housing", "Wonder", "Other"),
                T("building.age", "Available age", "Availability"),
                T("building.prerequisites", "Prerequisites", "Availability"),
                N("building.health", "Health", "Stats", "Must be at least 1."),
                N("building.armor", "Armor", "Stats"),
                N("building.armorShock", "Armor against shock", "Stats"),
                N("building.armorArrows", "Armor against arrows", "Stats"),
                N("building.armorPierce", "Armor against piercing", "Stats"),
                N("building.armorGuns", "Armor against guns", "Stats"),
                N("building.sight", "Line of sight", "Stats"),
                N("building.attack", "Attack damage", "Stats"),
                N("building.attackRange", "Attack range", "Stats"),
                N("building.attackInterval", "Attack interval (seconds)", "Stats"),
                M("building.attackProfiles", "Attack profiles and target classes", "Stats"),
                M("building.resistances", "Armor and damage resistances", "Stats"),
                N("building.garrisonCapacity", "Garrison capacity", "Stats"),
                M("building.garrisonRules", "Garrison eligibility and effects", "Stats"),
                N("building.populationProvided", "Population provided", "Stats"),
                B("building.progressionIncluded", "Included in civilization progression", "Availability"),
                T("building.progressionProducers", "Progression producer IDs", "Availability", "Existing compatible producers chosen in Ages progression."),
                N("building.foodCost", "Food cost", "Cost & construction"),
                N("building.woodCost", "Wood cost", "Cost & construction"),
                N("building.goldCost", "Gold cost", "Cost & construction"),
                N("building.stoneCost", "Stone cost", "Cost & construction"),
                N("building.buildTime", "Build time (seconds)", "Cost & construction"),
                N("building.productionCoefficient", "Production parameter", "Cost & construction", "Raw technology-table production parameter; its conversion to seconds is not verified."),
                N("building.footprintWidth", "Footprint width", "Placement"),
                N("building.footprintDepth", "Footprint depth", "Placement"),
                C("building.placement", "Placement domain", "Placement", "Land", "Water", "Shore", "Anywhere"),
                N("building.buildLimit", "Build limit", "Placement"),
                B("building.requiresFlatGround", "Requires flat ground", "Placement"),
                M("building.placementRules", "Terrain, slope and adjacency rules", "Placement"),
                M("building.footprint", "Footprint and collision rules", "Placement"),
                M("building.producedUnits", "Produced units", "Production"),
                M("building.research", "Available research", "Production"),
                N("building.queueCapacity", "Production queue capacity", "Production"),
                N("building.productionRate", "Production speed multiplier", "Production"),
                M("building.productionQueues", "Production queues, menus and slots", "Production"),
                M("building.productionPrerequisites", "Production prerequisites by item", "Production"),
                T("building.upgradeFrom", "Upgrades from", "Production"),
                T("building.upgradeTo", "Upgrades to", "Production"),
                M("building.resourceGeneration", "Resource generation", "Economy"),
                T("building.model", "Model / graphics reference", "Presentation"),
                N("building.graphicsId", "Graphics reference ID", "Presentation"),
                N("building.modelScaleFactor", "Model scale", "Presentation"),
                T("building.modelAsset", "Replacement model asset", "Presentation", "References the stable ID of a .cem or .obj model embedded in this project. Use the model import control to choose a file."),
                T("building.icon", "Icon reference", "Presentation"),
                T("building.soundSet", "Sound set", "Presentation"),
                T("building.voiceSet", "Voice reference", "Presentation"),
                M("building.animations", "Construction and action animations", "Presentation"),
                T("building.hotkey", "Hotkey", "Presentation"),
                M("building.notes", "Design notes", "Notes")
            }),
            [CivilizationCategory.Technologies] = Array.AsReadOnly(new[]
            {
                B("available", "Available to civilization", "Identity"),
                T("technology.internalName", "Internal name", "Identity"),
                T("technology.displayName", "Game display name", "Identity"),
                M("technology.description", "Description", "Identity"),
                T("technology.age", "Available age", "Requirements"),
                T("technology.prerequisites", "Prerequisite technologies", "Requirements"),
                T("technology.requiredBuildings", "Required buildings", "Requirements"),
                T("technology.researchedAt", "Researched at", "Requirements"),
                B("technology.progressionIncluded", "Included in civilization progression", "Requirements"),
                T("technology.progressionProducers", "Progression producer IDs", "Requirements", "Existing compatible producers chosen in Ages progression."),
                N("technology.foodCost", "Food cost", "Cost & research"),
                N("technology.woodCost", "Wood cost", "Cost & research"),
                N("technology.goldCost", "Gold cost", "Cost & research"),
                N("technology.stoneCost", "Stone cost", "Cost & research"),
                N("technology.researchTime", "Research time (seconds)", "Cost & research"),
                N("technology.productionCoefficient", "Research parameter", "Cost & research", "Raw technology-table research parameter; its conversion to seconds is not verified."),
                B("technology.repeatable", "Repeatable", "Effects"),
                N("technology.maximumLevel", "Maximum research level", "Effects"),
                T("technology.affectedEntities", "Affected units and buildings", "Effects"),
                M("technology.effects", "Effects", "Effects", "Describe intended changes. Game field mappings are pending."),
                M("technology.effectOperations", "Effect targets, operators and values", "Effects", "Draft additive, multiplicative, replacement, unlock and upgrade effects with their targets."),
                M("technology.prerequisiteRules", "Prerequisite and exclusion rules", "Requirements"),
                T("technology.unlocks", "Unlocks", "Effects"),
                T("technology.icon", "Icon reference", "Presentation"),
                T("technology.hotkey", "Hotkey", "Presentation"),
                M("technology.notes", "Design notes", "Notes")
            }),
            [CivilizationCategory.Abilities] = Array.AsReadOnly(new[]
            {
                B("available", "Available to civilization", "Identity"),
                T("ability.internalName", "Internal name", "Identity"),
                M("ability.description", "Description", "Identity"),
                C("ability.activation", "Activation", "Behavior", "Active", "Passive", "Triggered", "Toggle"),
                T("ability.owners", "Assigned units and buildings", "Behavior"),
                T("ability.prerequisites", "Prerequisites", "Behavior"),
                N("ability.cooldown", "Cooldown (seconds)", "Behavior"),
                N("ability.duration", "Duration (seconds)", "Behavior"),
                N("ability.castTime", "Cast time (seconds)", "Behavior"),
                N("ability.range", "Range", "Targeting"),
                N("ability.radius", "Area radius", "Targeting"),
                C("ability.target", "Target type", "Targeting", "Self", "Unit", "Building", "Ground", "Area", "Global"),
                C("ability.allegiance", "Target allegiance", "Targeting", "Friendly", "Enemy", "Any"),
                N("ability.triggerChance", "Trigger chance (%)", "Targeting", "Enter a percentage from 0 to 100."),
                N("ability.energyCost", "Energy cost", "Cost"),
                N("ability.foodCost", "Food cost", "Cost"),
                N("ability.goldCost", "Gold cost", "Cost"),
                M("ability.effects", "Effects", "Effects"),
                M("ability.effectOperations", "Effect targets, operators and values", "Effects"),
                M("ability.triggerRules", "Trigger conditions", "Behavior"),
                M("ability.targetRules", "Target filters and exclusions", "Targeting"),
                T("ability.animation", "Animation reference", "Presentation"),
                T("ability.effectAsset", "Visual effect reference", "Presentation"),
                T("ability.sound", "Sound reference", "Presentation"),
                T("ability.icon", "Icon reference", "Presentation"),
                T("ability.hotkey", "Hotkey", "Presentation"),
                M("ability.notes", "Design notes", "Notes")
            }),
            [CivilizationCategory.Economy] = Array.AsReadOnly(new[]
            {
                N("economy.startFood", "Starting food", "Starting resources"),
                N("economy.startWood", "Starting wood", "Starting resources"),
                N("economy.startGold", "Starting gold", "Starting resources"),
                N("economy.startStone", "Starting stone", "Starting resources"),
                N("economy.startPopulation", "Starting population capacity", "Population"),
                N("economy.populationLimit", "Population limit", "Population"),
                N("economy.foodGatherRate", "Food gather multiplier", "Gathering"),
                N("economy.woodGatherRate", "Wood gather multiplier", "Gathering"),
                N("economy.goldGatherRate", "Gold gather multiplier", "Gathering"),
                N("economy.stoneGatherRate", "Stone gather multiplier", "Gathering"),
                N("economy.workerSpeed", "Worker speed multiplier", "Gathering"),
                N("economy.carryCapacity", "Worker carry multiplier", "Gathering"),
                N("economy.buildSpeed", "Construction speed multiplier", "Production"),
                N("economy.researchSpeed", "Research speed multiplier", "Production"),
                N("economy.trainSpeed", "Training speed multiplier", "Production"),
                N("economy.tradeRate", "Trade rate multiplier", "Trade"),
                M("economy.startingUnits", "Starting units", "Starting roster"),
                M("economy.startingBuildings", "Starting buildings", "Starting roster"),
                M("economy.startingRosterByAge", "Starting units and buildings by age", "Starting roster"),
                M("economy.notes", "Economy design notes", "Notes")
            }),
            [CivilizationCategory.Ages] = Array.AsReadOnly(new[]
            {
                T("age.initialAge", "Initial age", "Age support"),
                T("age.finalAge", "Final age", "Age support"),
                T("age.supportedAges", "Supported ages", "Age support"),
                T("age.transitionCivilization", "Civilization after transition", "Transitions"),
                T("age.transitionTechnology", "Age advance technology", "Transitions"),
                T("age.transitionBuilding", "Age advance building", "Transitions"),
                T("age.transitionPrerequisites", "Transition prerequisites", "Transitions"),
                N("age.foodCost", "Age advance food cost", "Advance cost"),
                N("age.woodCost", "Age advance wood cost", "Advance cost"),
                N("age.goldCost", "Age advance gold cost", "Advance cost"),
                N("age.stoneCost", "Age advance stone cost", "Advance cost"),
                N("age.advanceTime", "Age advance time (seconds)", "Advance cost"),
                M("age.unitReplacements", "Unit replacements by age", "Progression"),
                M("age.buildingReplacements", "Building replacements by age", "Progression"),
                M("age.technologyUnlocks", "Technology unlocks by age", "Progression"),
                M("age.bonusChanges", "Bonus changes by age", "Progression"),
                M("age.transitionMappings", "Civilization and technology mappings by age", "Transitions"),
                M("age.rostersByAge", "Unit and building availability by age", "Progression"),
                M("age.notes", "Age progression notes", "Notes")
            }),
            [CivilizationCategory.Assets] = Array.AsReadOnly(new[]
            {
                T("assets.flag", "Flag reference", "Civilization presentation"),
                T("assets.emblem", "Emblem reference", "Civilization presentation"),
                T("assets.portrait", "Portrait reference", "Civilization presentation"),
                T("assets.color", "Preferred player color", "Civilization presentation"),
                T("assets.architecture", "Architecture set", "Models"),
                T("assets.unitStyle", "Unit visual set", "Models"),
                M("assets.modelMappings", "Model and graphics references by entity", "Models"),
                M("assets.animationMappings", "Animation references by entity and action", "Models"),
                T("assets.voiceSet", "Voice set", "Audio"),
                T("assets.music", "Music reference", "Audio"),
                T("assets.ambientSound", "Ambient sound reference", "Audio"),
                M("assets.voiceMappings", "Voice and sound references by entity", "Audio"),
                T("assets.nameStringId", "Name localization ID", "Localization"),
                T("assets.descriptionStringId", "Description localization ID", "Localization"),
                M("assets.localization", "Custom localized text", "Localization"),
                M("assets.replacements", "Asset replacement notes", "References"),
                M("assets.iconMappings", "Icon and button references by entity", "References"),
                M("assets.notes", "Presentation notes", "Notes")
            }),
            [CivilizationCategory.AI] = Array.AsReadOnly(new[]
            {
                B("ai.playable", "Available to computer players", "Availability"),
                T("ai.personality", "AI personality", "Strategy"),
                T("ai.script", "AI script reference", "Strategy"),
                M("ai.buildOrder", "Build order", "Strategy"),
                M("ai.unitPriorities", "Unit priorities", "Strategy"),
                M("ai.researchPriorities", "Research priorities", "Strategy"),
                M("ai.economyPriorities", "Economy priorities", "Economy"),
                N("ai.workerTarget", "Target worker count", "Economy"),
                N("ai.attackThreshold", "Attack force threshold", "Military"),
                C("ai.stance", "Preferred stance", "Military", "Aggressive", "Balanced", "Defensive"),
                M("ai.armyComposition", "Army composition", "Military"),
                M("ai.agePriorities", "Age advance priorities", "Progression"),
                M("ai.allowedRostersByAge", "AI roster and substitutions by age", "Progression"),
                M("ai.difficultyAdjustments", "Difficulty adjustments", "Progression"),
                M("ai.notes", "AI design notes", "Notes")
            })
        };

    public static IReadOnlyList<CivilizationFieldDefinition> GetFields(CivilizationCategory category) =>
        GetFields(category, null);

    public static IReadOnlyList<CivilizationFieldDefinition> GetFields(CivilizationCategory category,
        IReadOnlyDictionary<string, string>? sourceValues)
    {
        var fields = Fields.TryGetValue(category, out var definitions) ? definitions.ToList() : [];
        var keys = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in CivilizationObjectBindings.Fields(category).Concat(CivilizationTechnologyBindings.GetFields(category)))
        {
            if (!keys.Add(binding.Key)) continue;
            var suffix = binding.Key[(binding.Key.IndexOf('.') + 1)..];
            var section = suffix == "internalName" ? "Identity" :
                suffix is "initialAge" or "finalAge" ? category is CivilizationCategory.Technologies or CivilizationCategory.Abilities ? "Requirements" : "Availability" :
                suffix.EndsWith("Cost", StringComparison.Ordinal) || suffix == "productionCoefficient" ? "Cost & production" :
                suffix.StartsWith("hud", StringComparison.Ordinal) ? "HUD display" :
                suffix.StartsWith("gather", StringComparison.Ordinal) || suffix is "buildCoefficient" or "resourceTypeId" or "resourceAmount" ? "Economy" :
                suffix is "steeringRotation" or "altitude" ? "Movement" :
                suffix is "requiredSpace" ? "Placement" : "Combat";
            fields.Add(new(binding.Key, SourceLabel(suffix), section,
                binding.Type == CivilizationBinaryType.BooleanByte ? CivilizationFieldKind.Boolean :
                binding.Type == CivilizationBinaryType.String100 ? CivilizationFieldKind.Text : CivilizationFieldKind.Number,
                Help: binding.Evidence));
        }
        if (sourceValues is not null)
        {
            var prefix = category == CivilizationCategory.Abilities ? "ability." :
                category == CivilizationCategory.Technologies ? "technology." :
                category == CivilizationCategory.Units ? "unit." :
                category == CivilizationCategory.Buildings ? "building." :
                category == CivilizationCategory.Ages ? "age." : category.ToString().ToLowerInvariant() + ".";
            foreach (var (key, value) in sourceValues)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && keys.Add(key))
                    fields.Add(new(key, SourceLabel(key[prefix.Length..]), SourceSection(category, key),
                        value.Contains('\n') || value.Length > 180 ? CivilizationFieldKind.Multiline : CivilizationFieldKind.Text));
        }
        return fields.AsReadOnly();
    }

    private static string SourceSection(CivilizationCategory category, string key) => category switch
    {
        CivilizationCategory.Overview => key.Contains("Technolog", StringComparison.OrdinalIgnoreCase) ? "Bonuses" : "Identity",
        CivilizationCategory.Economy => key.Contains("worker", StringComparison.OrdinalIgnoreCase) ? "Gathering" : "Starting resources",
        CivilizationCategory.Ages => key.Contains("transition", StringComparison.OrdinalIgnoreCase) || key.Contains("advance", StringComparison.OrdinalIgnoreCase) ? "Transitions" : "Progression",
        CivilizationCategory.AI => "Strategy",
        CivilizationCategory.Assets => key.Contains("localization", StringComparison.OrdinalIgnoreCase) || key.Contains("leader", StringComparison.OrdinalIgnoreCase) ? "Localization" : "References",
        _ => key.Contains("effect", StringComparison.OrdinalIgnoreCase) ? "Effects" :
            key.Contains("StringId", StringComparison.Ordinal) ? "Localization" :
            key.Contains("Technology", StringComparison.Ordinal) ? "Production" : "Identity"
    };

    private static string SourceLabel(string suffix)
    {
        if (suffix == "initialAge") return "Available from age";
        if (suffix == "finalAge") return "Available through age";
        if (suffix.Length == 0) return "Value";
        if (suffix.StartsWith("hud", StringComparison.Ordinal)) suffix = "HUD" + suffix[3..];
        var words = System.Text.RegularExpressions.Regex.Replace(suffix,
            "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        words = words.Replace(" Ids", " IDs", StringComparison.Ordinal).Replace(" Id", " ID", StringComparison.Ordinal);
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static bool IsEntityCategory(CivilizationCategory category) => category is
        CivilizationCategory.Units or CivilizationCategory.Buildings or
        CivilizationCategory.Technologies or CivilizationCategory.Abilities;

    public static string? ValidateValue(CivilizationFieldDefinition field, string? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 1_000_000) return $"{field.Label} is too long.";
        if (field.Key.EndsWith(".progressionProducers", StringComparison.Ordinal))
        {
            try { _ = CivilizationTechnologyBindings.ParseProgressionProducerIds(value); return null; }
            catch (InvalidDataException error) { return error.Message; }
        }
        if (field.Key.EndsWith(".initialAge", StringComparison.Ordinal) || field.Key.EndsWith(".finalAge", StringComparison.Ordinal))
            return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var age) && age is >= 0 and <= 100
                ? null : $"{field.Label} requires a whole-number age from 0 to 100.";
        if (field.Kind == CivilizationFieldKind.Boolean &&
            !bool.TryParse(value, out _)) return $"{field.Label} must be true, false, or blank to inherit.";
        if (field.Kind == CivilizationFieldKind.Choice &&
            !(field.Options?.Contains(value, StringComparer.Ordinal) ?? false))
            return $"Choose a listed value for {field.Label}, or leave it blank to inherit.";
        if (field.Kind != CivilizationFieldKind.Number) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number)) return $"{field.Label} requires a finite number (use a decimal point).";
        var minimum = field.Key.EndsWith(".health", StringComparison.Ordinal) ? 1d : 0d;
        if (field.Key.EndsWith(".armor", StringComparison.Ordinal) || field.Key.Contains(".effect.", StringComparison.Ordinal)) minimum = double.NegativeInfinity;
        if (number < minimum) return $"{field.Label} must be at least {minimum.ToString(CultureInfo.InvariantCulture)}.";
        if (field.Key is "unit.accuracy" or "ability.triggerChance" && number > 100)
            return $"{field.Label} must be between 0 and 100.";
        if (RequiresWholeNumber(field.Key) && number != Math.Truncate(number))
            return $"{field.Label} requires a whole number.";
        return null;
    }

    public static bool TryValidateValue(CivilizationFieldDefinition field, string? value, out string? error)
    {
        error = ValidateValue(field, value);
        return error is null;
    }

    private static bool RequiresWholeNumber(string key) => key is
        "unit.transportCapacity" or "building.garrisonCapacity" or "building.populationProvided" or
        "building.buildLimit" or "building.queueCapacity" or "technology.maximumLevel" or
        "economy.startPopulation" or "economy.populationLimit" or "ai.workerTarget";
}
