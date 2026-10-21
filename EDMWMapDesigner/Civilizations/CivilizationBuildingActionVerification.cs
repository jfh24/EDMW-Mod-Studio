using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Headless coverage for building emitters and building action results.</summary>
internal static class CivilizationBuildingActionVerification
{
    internal static void Verify(string fixture, string output)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var databasePath = Path.Combine(fixture, "Data", "db");
        var originals = new[] { "dbobjects.dat", "dbtechtree.dat", "dbbuttons.dat", "dbevents.dat", "dbphoenixcivtable.dat" }
            .ToDictionary(name => name, name => SHA256.HashData(File.ReadAllBytes(Path.Combine(databasePath, name))), StringComparer.Ordinal);
        var executablePath = Path.Combine(fixture, "Empires_DMW.exe");
        var executableHash = SHA256.HashData(File.ReadAllBytes(executablePath));
        var objects = File.ReadAllBytes(Path.Combine(databasePath, "dbobjects.dat"));
        var technologies = File.ReadAllBytes(Path.Combine(databasePath, "dbtechtree.dat"));
        var buttons = File.ReadAllBytes(Path.Combine(databasePath, "dbbuttons.dat"));
        var catalog = CivilizationGameCatalog.Load(fixture);
        var buildingIds = CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, technologies, 11, CivilizationCategory.Buildings).ToHashSet();
        var unitIds = CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, technologies, 11, CivilizationCategory.Units).ToHashSet();
        var buildings = catalog.Items.Where(item => item.Category == CivilizationCategory.Buildings && buildingIds.Contains(item.SourceId)).ToArray();
        var candidates = buildings.Select(item =>
        {
            var entity = new CivilizationEntityDraft { Name = item.Name, Category = CivilizationCategory.Buildings, SourceObjectId = item.SourceId };
            return (Entity: entity, Slots: CivilizationUnitEffectBindings.AvailableButtonSlots(objects, technologies, buttons, entity, 11));
        }).Where(candidate => candidate.Slots.Count > 0).ToArray();
        Require(candidates.Length > 0 && buildings.Length > 1 && unitIds.Count > 0, "The fixture needs existing building emitters and unit/building action results.");
        var source = candidates[0].Entity;
        var targetBuilding = buildings.First(item => item.SourceId != source.SourceObjectId);
        var spawnedUnit = catalog.Items.First(item => item.Category == CivilizationCategory.Units && unitIds.Contains(item.SourceId));
        var project = CivilizationEditProject.CreateDefault();
        project.Name = "Generic building action verification";
        project.Civilizations.Single(civilization => civilization.SourceRowIndex == 11).Entities.Add(source);
        var transform = new CivilizationUnitEffectOutput(CivilizationUnitEffectStat.LineOfSight, 0)
        {
            Kind = CivilizationUnitEffectOutputKind.Transform, ObjectId = targetBuilding.SourceId
        };
        var spawn = new CivilizationUnitEffectOutput(CivilizationUnitEffectStat.LineOfSight, 0)
        {
            Kind = CivilizationUnitEffectOutputKind.Spawn, ObjectId = spawnedUnit.SourceId, Count = 2
        };
        var proximity = new CivilizationUnitEffect
        {
            Name = "Building support", TargetObjectId = targetBuilding.SourceId,
            TargetObjectIds = [targetBuilding.SourceId, spawnedUnit.SourceId],
            Trigger = CivilizationUnitEffectTrigger.Proximity, ActivationMode = CivilizationUnitEffectActivationMode.Toggle,
            ProximityDelaySeconds = 30, MaxConcurrentTargets = 2, MaxAffectedTargets = 4, Outputs = [transform, spawn],
            ButtonIconId = CivilizationUnitEffectBindings.ReadButtonChoices(buttons).First().Id
        };
        CivilizationUnitEffects.Write(source, [proximity]);
        var document = project.Serialize();
        Require(CivilizationEditProject.Deserialize(document).Serialize() == document, "Building action authoring persists without a unit-specific schema.");
        var proximityPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        var proximityRules = ReadRules(proximityPackage);
        Require(proximityRules.Count == 2 && proximityRules.All(rule => rule.SourceObjectId == source.SourceObjectId &&
            rule.DelayMilliseconds == 30000 && rule.MaxConcurrentTargets == 2 && rule.MaxAffectedTargets == 4 &&
            rule.FamilyId == Guid.Parse(proximity.Id) && CivilizationRuntimeConfiguration.GetOutputs(rule).Count == 0 &&
            CivilizationRuntimeConfiguration.GetActions(rule).Count == 2 &&
            CivilizationRuntimeConfiguration.GetActions(rule)[0].ObjectId == targetBuilding.SourceId &&
            CivilizationRuntimeConfiguration.GetActions(rule)[1].ObjectId == spawnedUnit.SourceId),
            "A building emits delayed, limited actions to unit and building recipients with building transformation and unit spawning outputs.");
        Require(proximityPackage.Files.All(file => file.Patches.Count == 0), "Automatic building effects require no native button database edits.");
        var activated = proximity with
        {
            Id = Guid.NewGuid().ToString("D"), Name = "Building reinforcements", Trigger = CivilizationUnitEffectTrigger.Activated,
            TargetObjectId = source.SourceObjectId!.Value, TargetObjectIds = [source.SourceObjectId.Value],
            ActivationMode = CivilizationUnitEffectActivationMode.Instant, DurationSeconds = 0,
            ProximityDelaySeconds = 0, MaxConcurrentTargets = 0, MaxAffectedTargets = 0,
            FoodCost = 10, WoodCost = 20, GoldCost = 30, StoneCost = 40,
            ButtonCaption = "Reinforce", ButtonSlot = candidates[0].Slots[0]
        };
        CivilizationUnitEffects.Write(source, [activated]);
        var package = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        var rule = ReadRules(package).Single();
        Require(rule.SourceObjectId == source.SourceObjectId && rule.TargetObjectId == source.SourceObjectId &&
            rule.Trigger == CivilizationUnitEffectTrigger.Activated && rule.ActivationMode == CivilizationUnitEffectActivationMode.Instant &&
            rule.FoodCost == 10 && rule.WoodCost == 20 && rule.GoldCost == 30 && rule.StoneCost == 40 &&
            rule.DelayMilliseconds == 0 && rule.MaxConcurrentTargets == 0 && rule.MaxAffectedTargets == 0,
            "Building action buttons preserve all four per-recipient costs and source-only instant activation.");
        var names = new Dictionary<int, string> { [targetBuilding.SourceId] = "Converted building", [spawnedUnit.SourceId] = "Reinforcement unit" };
        var named = CivilizationRuntimeDescriptions.ForLaunch(package, [rule], names).Single();
        Require(CivilizationRuntimeConfiguration.GetActions(named)[0].ObjectName == "Converted building" &&
            CivilizationRuntimeConfiguration.GetActions(named)[1].ObjectName == "Reinforcement unit",
            "Building and unit action results resolve independently to player-facing names.");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "building-actions.edmwe"), CivilizationRuntimeConfiguration.Encode([], [named]));
        var installation = CivilizationPackageManager.Install(package, fixture);
        Require(installation.Success, installation.Message);
        byte[]? launched = null;
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => launched = bytes);
        Require(CivilizationRuntimeConfiguration.Equivalent(CivilizationRuntimeConfiguration.Decode(launched!).Units.Single(), rule),
            "Installed building effects retain actions and all resource costs at launch.");
        var shared = CivilizationPackageManager.CreateSharePackage(fixture, package.PackageId);
        Require(CivilizationInstallerPackageCodec.Encode(shared).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(package)),
            "Sharing preserves a building's paid action configuration.");
        var removal = CivilizationPackageManager.Uninstall(fixture, package.PackageId);
        Require(removal.Success, removal.Message);
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => launched = bytes);
        Require(CivilizationRuntimeConfiguration.Decode(launched!).Count == 0 &&
            SHA256.HashData(File.ReadAllBytes(executablePath)).AsSpan().SequenceEqual(executableHash) &&
            originals.All(pair => SHA256.HashData(File.ReadAllBytes(Path.Combine(databasePath, pair.Key))).AsSpan().SequenceEqual(pair.Value)),
            "Uninstall removes building action commands and restores the original six game files.");
        Console.WriteLine("BUILDING ACTIONS PASS: generic building emitters and recipients, building transformation, unit spawning, delays, both limits, all resource costs, package/share/install/launch/uninstall and original game file restoration.");
    }

    private static IReadOnlyList<CivilizationRuntimeUnitRule> ReadRules(CivilizationInstallerPackage package) =>
        CivilizationRuntimeConfiguration.Decode(package.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!).Units;
}
