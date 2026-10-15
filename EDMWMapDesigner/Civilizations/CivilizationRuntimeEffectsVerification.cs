using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EDMWMapDesigner.UI;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationRuntimeEffectsVerification
{
    public static void Run(string source, string output)
    {
        VerifyStatCatalog();
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) || output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a verification directory outside the installed game.");
        var fixture = Path.Combine(output, "effects-" + Guid.NewGuid().ToString("N"));
        string[] files = ["Empires_DMW.exe", "Data\\db\\dbobjects.dat", "Data\\db\\dbtechtree.dat", "Data\\db\\dbphoenixcivtable.dat",
            "Data\\db\\dbbuttons.dat", "Data\\db\\dbevents.dat"];
        var hashes = files.ToDictionary(file => file, file => SHA256.HashData(File.ReadAllBytes(Path.Combine(source, file))));
        foreach (var file in files)
        {
            var target = Path.Combine(fixture, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, file), target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        var records = CivilizationTechnologyBindings.ReadRecords(File.ReadAllBytes(Path.Combine(fixture, files[2])));
        var phoenixCivilizations = File.ReadAllBytes(Path.Combine(fixture, files[3]));
        foreach (var civilization in CivilizationEditProject.CreateDefault().Civilizations)
        {
            var row = civilization.SourceRowIndex!.Value;
            var recordOffset = 4 + row * 344;
            var storedIdentity = BinaryPrimitives.ReadInt32LittleEndian(phoenixCivilizations.AsSpan(recordOffset));
            var nativeIdentity = BinaryPrimitives.ReadInt32LittleEndian(phoenixCivilizations.AsSpan(recordOffset + 4));
            Require(storedIdentity == civilization.SourceCivilizationId && nativeIdentity == row && storedIdentity == nativeIdentity + 1,
                "Persisted civilization identity maps to the native zero-based Phoenix row without changing wire IDs");
        }
        var objects = File.ReadAllBytes(Path.Combine(fixture, files[1]));
        var nativeButtonChecks = CivilizationAbilityButtonsVerification.Verify(fixture);
        var nativeSlotChecks = VerifyAbilityButtonSlots(objects, File.ReadAllBytes(Path.Combine(fixture, files[2])),
            File.ReadAllBytes(Path.Combine(fixture, files[4])), File.ReadAllBytes(Path.Combine(fixture, files[5])));
        var technology = CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(objects, records, 10, 0, 100)
            .First(CivilizationTechnologyBindings.IsResearchRecord);
        CivilizationEditProject Project(string name, string amount)
        {
            var project = CivilizationEditProject.CreateDefault();
            project.Name = name;
            var civilization = project.Civilizations.Single(civ => civ.SourceRowIndex == 10);
            civilization.Entities.Add(new() { Name = technology.Identifier, Category = CivilizationCategory.Technologies,
                SourceTechnologyId = technology.TechId, Properties = new() { ["technology.bonusFood"] = amount } });
            return project;
        }
        var projectA = Project("Research bonus A", "123");
        var projectB = Project("Research bonus B", "37");
        var packageA = CivilizationInstallerPackage.Create(projectA, fixture);
        var packageB = CivilizationInstallerPackage.Create(projectB, fixture);
        Require(CivilizationEditProject.Deserialize(projectA.Serialize()).Serialize() == projectA.Serialize(), "Project round trip");
        var history = new CivilizationEditHistory(projectA);
        history.Change(project => project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities[0].Properties["technology.bonusFood"] = "250");
        history.Undo();
        Require(history.Current.Serialize() == projectA.Serialize(), "Runtime authoring undo");
        Require(packageA.Files.Count(file => file.WholeFile) == 1 && packageA.Files.All(file => file.Patches.Count == 0), "Effects-only package changes no database bytes");
        packageA = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(packageA));
        var sidecarA = packageA.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
        var sidecarB = packageB.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
        var rule = CivilizationRuntimeEffects.Decode(sidecarA.Replacement!).Single();
        Require(rule == new CivilizationRuntimeRule(technology.TechId, 11, 4, 123), "Rule identity and food resource mapping");
        var corrupt = sidecarA.Replacement!.ToArray(); corrupt[20] = 3;
        Reject(() => CivilizationRuntimeEffects.Decode(corrupt), "Unsupported resource");
        Reject(() => CivilizationRuntimeEffects.Decode(sidecarA.Replacement![..^1]), "Truncated rules");
        Reject(() => Project("Invalid amount", "1.5").Validate(), "Fractional amount");
        Reject(() => Project("Invalid amount", "100001").Validate(), "Oversized amount");
        Reject(() => CivilizationPackageManager.ResolveTarget(fixture, "Data\\EDMWEffects\\..\\Empires_DMW.exe"), "Path traversal");
        var forged = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(packageA));
        forged.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement![24] ^= 1;
        Reject(() => CivilizationInstallerPackageCodec.Encode(forged), "Project/sidecar mismatch");
        var missing = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(packageA));
        missing.Files.RemoveAll(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
        Reject(() => CivilizationInstallerPackageCodec.Encode(missing), "Missing runtime sidecar");
        foreach (var first in new[] { packageA, packageB })
        {
            Success(CivilizationPackageManager.Install(packageA, fixture));
            Success(CivilizationPackageManager.Install(packageB, fixture));
            Require(Snapshot().Count == 2, "Both mods active");
            CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, packageA.PackageId, false);
            Require(Snapshot().Single().Amount == 37, "Disable only selected mod");
            var installed = CivilizationPackageManager.ReadInstalled(fixture).Single(item => item.PackageId == packageA.PackageId);
            Require(installed.HasRuntimeEffects && !installed.RuntimeEffectsEnabled, "Persisted effect state");
            Require(CivilizationInstalledEditsPanel.BuildDetails(installed).Contains("Runtime effects: Disabled", StringComparison.Ordinal), "Details expose disabled effects");
            var shared = CivilizationPackageManager.CreateSharePackage(fixture, packageA.PackageId);
            Require(CivilizationInstallerPackageCodec.Encode(shared).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(packageA)), "Sharing preserves original rules while locally disabled");
            CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, packageA.PackageId, true);
            Require(Snapshot().Count == 2, "Re-enable restores selected rules");
            var bytes = File.ReadAllBytes(Path.Combine(fixture, sidecarA.RelativePath));
            File.WriteAllBytes(Path.Combine(fixture, sidecarA.RelativePath), [0]);
            Reject(() => Snapshot(), "Externally modified sidecar blocks launch");
            File.WriteAllBytes(Path.Combine(fixture, sidecarA.RelativePath), bytes);
            Success(CivilizationPackageManager.Uninstall(fixture, first.PackageId));
            var remaining = first == packageA ? packageB : packageA;
            Require(Snapshot().Count == 1 && CivilizationPackageManager.ReadInstalled(fixture).Single().PackageId == remaining.PackageId, "Selective uninstall");
            var preservedSidecar = remaining.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
            Require(File.ReadAllBytes(Path.Combine(fixture, preservedSidecar.RelativePath)).AsSpan().SequenceEqual(preservedSidecar.Replacement!),
                "Selective uninstall preserves the other mod's runtime sidecar byte for byte");
            Success(CivilizationPackageManager.Uninstall(fixture, remaining.PackageId));
            Require(Snapshot().Count == 0 && !File.Exists(Path.Combine(fixture, sidecarA.RelativePath)) && !File.Exists(Path.Combine(fixture, sidecarB.RelativePath)), "Uninstall removes owned sidecars");
        }
        var auraProject = CivilizationEditProject.CreateDefault();
        auraProject.Name = "Ranger reconnaissance";
        var ranger = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var aura = new CivilizationUnitEffect { Name = "Tank reconnaissance", TargetObjectId = 777, Amount = 3, RadiusTiles = 5 };
        CivilizationUnitEffects.Write(ranger, [aura]);
        auraProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(ranger);
        var auraPackage = CivilizationInstallerPackage.Create(auraProject, fixture);
        var auraFile = auraPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
        var auraRule = CivilizationRuntimeConfiguration.Decode(auraFile.Replacement!).Units.Single();
        Require(auraRule == new CivilizationRuntimeUnitRule(Guid.Parse(aura.Id), 12, 749, 777, 3, 5), "Aura source/civilization/target/radius/sight mapping");
        Require(auraPackage.Files.All(file => file.Patches.Count == 0), "Proximity-only package changes no native database fields");
        var auraRoundTrip = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(auraPackage));
        Require(CivilizationRuntimeConfiguration.Decode(auraRoundTrip.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!).Units.Single() == auraRule,
            "Proximity package round trip");
        var malformedAura = auraFile.Replacement!.ToArray(); malformedAura.AsSpan(80, 4).Clear();
        Reject(() => CivilizationRuntimeConfiguration.Decode(malformedAura), "Zero sight adjustment");
        var unknownStat = auraFile.Replacement!.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(unknownStat.AsSpan(76), int.MaxValue);
        Reject(() => CivilizationRuntimeConfiguration.Decode(unknownStat), "Unknown stat output");
        var nonfinite = auraFile.Replacement!.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(nonfinite.AsSpan(80), float.NaN);
        Reject(() => CivilizationRuntimeConfiguration.Decode(nonfinite), "Nonfinite effect adjustment");
        var legacyVersion4 = auraFile.Replacement![..^8].ToArray();
        "EDMWEF04"u8.CopyTo(legacyVersion4);
        BinaryPrimitives.WriteSingleLittleEndian(legacyVersion4.AsSpan(44), 3);
        BinaryPrimitives.WriteInt32LittleEndian(legacyVersion4.AsSpan(72), 0);
        Require(CivilizationRuntimeConfiguration.Decode(legacyVersion4).Units.Single() == auraRule, "Legacy version 4 single-output import");
        var legacyAura = legacyVersion4[..^4].ToArray();
        "EDMWEF03"u8.CopyTo(legacyAura);
        BinaryPrimitives.WriteInt32LittleEndian(legacyAura.AsSpan(44), 3);
        Require(CivilizationRuntimeConfiguration.Decode(legacyAura).Units.Single() == auraRule, "Legacy version 3 sight rule import");
        var legacyProximity = legacyAura[..52].ToArray();
        "EDMWEF02"u8.CopyTo(legacyProximity);
        Require(CivilizationRuntimeConfiguration.Decode(legacyProximity).Units.Single() == auraRule, "Legacy version 2 proximity rule import");
        var legacyPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(auraPackage));
        legacyPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement = legacyAura;
        legacyPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacyPackage));
        Require(CivilizationRuntimeConfiguration.Decode(legacyPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!).Units.Single() == auraRule,
            "Legacy version 3 exported package remains readable");
        var speed = auraRule with { Id = Guid.NewGuid(), Stat = CivilizationUnitEffectStat.MovementSpeed, Amount = 0.125f };
        Require(CivilizationRuntimeConfiguration.Decode(CivilizationRuntimeConfiguration.Encode([], [speed])).Units.Single() == speed,
            "Fractional movement modifier wire mapping");
        var unboundStat = CivilizationUnitEffects.StatChoices.FirstOrDefault(choice => !choice.RuntimeSupported);
        if (unboundStat is not null)
            Reject(() => CivilizationRuntimeConfiguration.Encode([], [auraRule with { Stat = unboundStat.Stat }]),
                "Unsupported native stat cannot silently become sight");
        Reject(() => CivilizationRuntimeConfiguration.Decode(auraFile.Replacement![..^1]), "Truncated aura record");
        Reject(() => CivilizationRuntimeConfiguration.Encode([], [auraRule, auraRule]), "Duplicate effect identity");
        Success(CivilizationPackageManager.Install(auraPackage, fixture));
        Success(CivilizationPackageManager.Install(packageA, fixture));
        var combined = Configuration();
        Require(combined.Research.Count == 1 && combined.Units.Single() == auraRule, "Research and proximity mods coexist");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "combined-rules.edmwe"), CivilizationRuntimeConfiguration.Encode(combined.Research, combined.Units));
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, auraPackage.PackageId, false);
        Require(Configuration().Units.Count == 0 && Configuration().Research.Count == 1, "Disable proximity without changing research mod");
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, auraPackage.PackageId, true);
        Require(Configuration().Units.Count == 1, "Re-enable proximity mod");
        var sharedAura = CivilizationPackageManager.CreateSharePackage(fixture, auraPackage.PackageId);
        Require(CivilizationInstallerPackageCodec.Encode(sharedAura).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(auraPackage)), "Shared aura preserves project and rule identity");
        Success(CivilizationPackageManager.Uninstall(fixture, auraPackage.PackageId));
        Require(Configuration().Units.Count == 0 && Configuration().Research.Count == 1, "Undo aura leaves independent research mod");
        Success(CivilizationPackageManager.Uninstall(fixture, packageA.PackageId));
        Require(Configuration().Count == 0, "All runtime mods removed");
        var movementProject = auraProject.Clone();
        movementProject.Name = "Ranger mobility";
        var movementRanger = movementProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single();
        var movement = aura with { Id = Guid.NewGuid().ToString(), Name = "Movement and sight aura", Outputs =
            [new(CivilizationUnitEffectStat.MovementSpeed, 0.125m), new(CivilizationUnitEffectStat.LineOfSight, 2)] };
        CivilizationUnitEffects.Write(movementRanger, [movement]);
        var movementPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(movementProject, fixture)));
        var movementRule = CivilizationRuntimeConfiguration.Decode(movementPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!).Units.Single();
        Require(CivilizationRuntimeConfiguration.GetOutputs(movementRule).SequenceEqual(
            [new(CivilizationUnitEffectStat.MovementSpeed, 0.125f), new(CivilizationUnitEffectStat.LineOfSight, 2)]),
            "Multiple outputs and fractional adjustment survive package creation and codec as one effect");
        Success(CivilizationPackageManager.Install(movementPackage, fixture));
        Require(CivilizationRuntimeConfiguration.Equivalent(Configuration().Units.Single(), movementRule), "Multiple outputs survive installation and launch aggregation");
        File.WriteAllBytes(Path.Combine(output, "movement-rules.edmwe"), CivilizationRuntimeConfiguration.Encode([], [movementRule]));
        Success(CivilizationPackageManager.Uninstall(fixture, movementPackage.PackageId));
        Require(Configuration().Count == 0, "Movement uninstall removes its runtime output");
        VerifyMultipleTargets(auraProject, aura, fixture, output);
        {
            var allStatsProject = auraProject.Clone();
            allStatsProject.Name = "All stat output integration";
            var allStatsEntity = allStatsProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single();
            var allStats = aura with { Id = Guid.NewGuid().ToString(), Name = "Every stat output", Outputs =
                CivilizationUnitEffects.StatChoices.Select(choice => new CivilizationUnitEffectOutput(choice.Stat, choice.DefaultAmount)).ToArray() };
            CivilizationUnitEffects.Write(allStatsEntity, [allStats]);
            var allStatsPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(
                CivilizationInstallerPackage.Create(allStatsProject, fixture)));
            var allStatsBytes = allStatsPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!;
            var allStatsRule = CivilizationRuntimeConfiguration.Decode(allStatsBytes).Units.Single();
            Require(CivilizationRuntimeConfiguration.GetOutputs(allStatsRule).Count == 51 &&
                CivilizationRuntimeConfiguration.GetOutputs(allStatsRule).Select(item => item.Stat).Distinct().Count() == 51,
                "All 51 runtime outputs survive portable package creation and codec as one effect");
            Success(CivilizationPackageManager.Install(allStatsPackage, fixture));
            Require(CivilizationRuntimeConfiguration.Equivalent(Configuration().Units.Single(), allStatsRule),
                "All 51 runtime outputs survive installation and launch aggregation");
            File.WriteAllBytes(Path.Combine(output, "all-stat-outputs.edmwe"), allStatsBytes);
            Success(CivilizationPackageManager.Uninstall(fixture, allStatsPackage.PackageId));
            Require(Configuration().Count == 0, "Uninstall removes every output together");
        }
        var activatedProject = auraProject.Clone();
        activatedProject.Name = "Reconnaissance abilities";
        var activatedRanger = activatedProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single();
        var buttons = File.ReadAllBytes(Path.Combine(fixture, "Data\\db\\dbbuttons.dat"));
        var freeSlots = CivilizationUnitEffectBindings.AvailableButtonSlots(objects,
            File.ReadAllBytes(Path.Combine(fixture, files[2])), buttons, activatedRanger, 11).Take(2).ToArray();
        Require(freeSlots.Length == 2, "Two free ability button slots");
        var toggle = aura with { Id = Guid.NewGuid().ToString(), Name = "Recon toggle", Trigger = CivilizationUnitEffectTrigger.Activated,
            TargetObjectId = 749, ActivationMode = CivilizationUnitEffectActivationMode.Toggle, ButtonCaption = "Recon toggle", ButtonIconId = 1350, ButtonSlot = freeSlots[0],
            Outputs = [new(CivilizationUnitEffectStat.LineOfSight, 3), new(CivilizationUnitEffectStat.MovementSpeed, 0.125m)] };
        var timed = toggle with { Id = Guid.NewGuid().ToString(), Name = "Recon burst", ActivationMode = CivilizationUnitEffectActivationMode.Timed,
            DurationSeconds = 15, ButtonCaption = "Recon burst", ButtonSlot = freeSlots[1] };
        var everyOutputAbility = timed with { Id = Guid.NewGuid().ToString(), Name = "Every output ability", Outputs =
            CivilizationUnitEffects.StatChoices.Select(choice => new CivilizationUnitEffectOutput(choice.Stat, choice.DefaultAmount)).ToArray() };
        var allAbilityProject = auraProject.Clone();
        allAbilityProject.Name = "All stat ability integration";
        CivilizationUnitEffects.Write(allAbilityProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single(), [everyOutputAbility]);
        var allAbilityPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(
            CivilizationInstallerPackage.Create(allAbilityProject, fixture)));
        var allAbilityBytes = allAbilityPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!;
        var allAbilityRules = CivilizationRuntimeConfiguration.Decode(allAbilityBytes);
        Require(allAbilityRules.Units.Count == 1 && allAbilityRules.Units.Single() is { TechnologyId: >= 10000, DurationMilliseconds: 15000 } &&
            CivilizationRuntimeConfiguration.GetOutputs(allAbilityRules.Units.Single()).Count == 51,
            "All 51 outputs use one ability command, one activation timer and one complete tooltip");
        var allAbilityButtons = CivilizationUnitEffectBindings.BuildAbilityButtons(objects,
            File.ReadAllBytes(Path.Combine(fixture, files[2])), buttons, File.ReadAllBytes(Path.Combine(fixture, files[5])),
            [new(11, activatedRanger, everyOutputAbility)]);
        Require(allAbilityButtons.Registrations.Single().Tooltip.Contains("Limit 5 production queue capacity", StringComparison.Ordinal),
            "Activated limit output tooltip describes SET semantics");
        VerifyAbilityEpochGroups(objects, File.ReadAllBytes(Path.Combine(fixture, files[2])), buttons,
            File.ReadAllBytes(Path.Combine(fixture, files[5])), activatedRanger, everyOutputAbility);
        Success(CivilizationPackageManager.Install(allAbilityPackage, fixture));
        Require(CivilizationRuntimeConfiguration.Equivalent(Configuration().Units.Single(), allAbilityRules.Units.Single()),
            "All 51 ability outputs survive installation and launch aggregation");
        File.WriteAllBytes(Path.Combine(output, "all-stat-abilities.edmwe"), allAbilityBytes);
        File.WriteAllBytes(Path.Combine(output, "all-stat-abilities-dbobjects.dat"),
            allAbilityPackage.Files.Single(file => CivilizationInstallerPackage.IsObjectPath(file.RelativePath)).Replacement!);
        File.WriteAllBytes(Path.Combine(output, "all-stat-abilities-dbtechtree.dat"),
            allAbilityPackage.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath)).Replacement!);
        Success(CivilizationPackageManager.Uninstall(fixture, allAbilityPackage.PackageId));
        Require(Configuration().Count == 0, "All-stat ability uninstall restores command tables");
        CivilizationUnitEffects.Write(activatedRanger, [aura, toggle, timed]);
        var originalDraft = activatedProject.Serialize();
        var abilityPackage = CivilizationInstallerPackage.Create(activatedProject, fixture);
        Require(activatedProject.Serialize() == originalDraft && CivilizationUnitEffects.Read(activatedRanger).All(effect => effect.NativeTechnologyId is null),
            "Generated command IDs do not mutate the authoring draft");
        abilityPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(abilityPackage));
        var abilityRules = abilityPackage.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))
            .SelectMany(file => CivilizationRuntimeConfiguration.Decode(file.Replacement!).Units).ToArray();
        Require(abilityRules.Length == 3 && abilityRules.Single(rule => rule.Id == Guid.Parse(toggle.Id)) is { RadiusTiles: 0, DurationMilliseconds: 0, Caption: "Recon toggle" } &&
            abilityRules.Single(rule => rule.Id == Guid.Parse(timed.Id)) is { RadiusTiles: 0, DurationMilliseconds: 15000, Caption: "Recon burst" }, "Toggle and timed ability wire mapping");
        Require(abilityRules.Where(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated).All(rule => CivilizationRuntimeConfiguration.GetOutputs(rule).Count == 2) &&
            abilityRules.Where(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated).Select(rule => rule.TechnologyId).Distinct().Count() == 2,
            "Multiple activated outputs share one command and timer per effect");
        VerifyLegacyAbilityInstall(abilityPackage, fixture, files);
        VerifyLegacyAbilitySlot(abilityPackage, fixture, files);
        File.WriteAllBytes(Path.Combine(output, "combined-abilities.edmwe"), CivilizationRuntimeConfiguration.Encode([rule], abilityRules));
        var invalidButtonPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(abilityPackage));
        invalidButtonPackage.Files.RemoveAll(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        Reject(() => CivilizationInstallerPackageCodec.Encode(invalidButtonPackage), "Ability package requires native button records");
        foreach (var fieldOffset in new[] { 136, 232, 236, 240, 324 + 10 * 8 })
        {
            var tamperedButton = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(abilityPackage));
            var table = tamperedButton.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath)).Replacement!;
            var command = CivilizationTechnologyBindings.ReadRecords(table).Single(record => record.TechId == abilityRules.First(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated).TechnologyId);
            table[command.Offset + fieldOffset] ^= 1;
            Reject(() => CivilizationInstallerPackageCodec.Encode(tamperedButton), "Ability slot, tooltip and zero-cost registration guards");
        }
        var duplicateProject = auraProject.Clone();
        var duplicateEntity = new CivilizationEntityDraft { Name = "Another attachment", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        CivilizationUnitEffects.Write(duplicateEntity, [aura]);
        duplicateProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(duplicateEntity);
        Reject(() => CivilizationInstallerPackage.Create(duplicateProject, fixture), "Duplicate effect IDs across entities");
        Success(CivilizationPackageManager.Install(abilityPackage, fixture));
        Require(Configuration().Units.Count == 3, "Installed proximity and two activated abilities");
        Require(CivilizationPackageManager.ReadInstalledExport(activatedProject, fixture)?.PackageId == abilityPackage.PackageId,
            "Unchanged original draft matches its export with generated command IDs");
        Require(CivilizationPackageManager.CreateSharePackage(fixture, abilityPackage.PackageId).PackageId == abilityPackage.PackageId,
            "Installed ability package can be shared");
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, abilityPackage.PackageId, false);
        var disabled = Configuration();
        Require(disabled.Research.Count == 0 && disabled.Units.Count == 2 && disabled.Units.All(rule => !rule.Enabled && rule.Trigger == CivilizationUnitEffectTrigger.Activated),
            "Disabled abilities retain only inactive button bindings in the launch snapshot");
        File.WriteAllBytes(Path.Combine(output, "disabled-abilities.edmwe"), CivilizationRuntimeConfiguration.Encode(disabled.Research, disabled.Units));
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, abilityPackage.PackageId, true);
        Require(Configuration().Units.Count == 3, "Re-enabled abilities retain their command identities");
        Require(!CivilizationPackageManager.Install(auraPackage, fixture).Success, "Conflicting complete-table ownership is refused");
        var activeAbilityFiles = abilityPackage.Files.Where(file => file.WholeFile || file.Patches.Count > 0)
            .ToDictionary(file => file.RelativePath, file => SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file.RelativePath))));
        Require(!CivilizationPackageManager.Install(packageA, fixture).Success,
            "Exclusive ability tables reject another package that depends on those tables");
        foreach (var file in activeAbilityFiles)
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file.Key))).AsSpan().SequenceEqual(file.Value),
                "Rejected conflicting install preserves the existing ability package byte for byte");
        Require(Configuration().Research.Count == 0 && Configuration().Units.Count == 3 &&
            CivilizationPackageManager.ReadInstalled(fixture).Single().PackageId == abilityPackage.PackageId,
            "Rejected conflicting install preserves the installed ability configuration and inventory");
        Success(CivilizationPackageManager.Uninstall(fixture, abilityPackage.PackageId));
        foreach (var file in files)
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(hashes[file]),
                "Ability uninstall restores all six original game files");
        Require(Configuration().Count == 0, "Last mod uninstall leaves an empty configuration for the plain game launch path");
        foreach (var file in files)
        {
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(hashes[file]), "Fixture database/executable unchanged");
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, file))).AsSpan().SequenceEqual(hashes[file]), "Source game unchanged");
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "runtime-effects-result.txt"),
            "PASS: all 51 stat outputs author/save/describe and package/install/launch aggregation/uninstall as one effect; range and precision validation; multiple outputs share one effect identity, trigger, timer and command; multiple targets preserve project/history/codec/install/share/launch/uninstall, canonical deduplication, stable per-target identities and aggregate capacity; v2/v3/v4 compatibility; v5 output-list codec; project/history, research/proximity/toggle/timed package codec, command registration, concurrent mods, persisted toggles, sharing, tamper detection; selective uninstall preserves other supported mods byte for byte; rejected conflicting installs preserve ability files and inventory; final uninstall restores all six original game files and leaves an empty plain-launch configuration.\n" +
            string.Join("\n", nativeButtonChecks) + "\n" +
            nativeSlotChecks + "\n" +
            "PASS: retained slot-1 ability package codec, install, launch configuration and sharing preserve original on-disk records; uninstall restores all six original files and removes its runtime configuration.\n" +
            "UNVERIFIED: live buttons/effects, fog refresh, save/load, two-player match. Activated state is session-only; ability tables require exclusive package ownership.\n");
        Console.WriteLine("RUNTIME EFFECTS PASS: 51 stat outputs, multiple outputs per effect, v5 output-list codec, v2/v3/v4 compatibility, multi-output package/install/launch/uninstall, research/proximity/activated authoring, native button registration, toggle, share, tamper detection, unchanged source files.");

        CivilizationRuntimeConfiguration Configuration()
        {
            CivilizationRuntimeConfiguration? snapshot = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => snapshot = CivilizationRuntimeConfiguration.Decode(bytes));
            return snapshot!;
        }

        IReadOnlyList<CivilizationRuntimeRule> Snapshot()
        {
            IReadOnlyList<CivilizationRuntimeRule>? snapshot = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => snapshot = CivilizationRuntimeEffects.Decode(bytes));
            return snapshot!;
        }
    }

    private static void VerifyMultipleTargets(CivilizationEditProject template, CivilizationUnitEffect original,
        string fixture, string output)
    {
        var project = template.Clone();
        project.Name = "Multiple target aura";
        var civilization = project.Civilizations.Single(civ => civ.SourceRowIndex == 11);
        var entity = civilization.Entities.Single();
        var multi = original with { Id = "72f8c984-4154-4fdb-b2f4-39684d261d8b", Name = "Shared health and sight",
            TargetObjectId = 878, TargetObjectIds = [878, 777, 878],
            Outputs = [new(CivilizationUnitEffectStat.Health, 1000), new(CivilizationUnitEffectStat.LineOfSight, 30)] };
        CivilizationUnitEffects.Write(entity, [multi]);
        var saved = CivilizationUnitEffects.Read(entity).Single();
        Require(saved.TargetObjectId == 777 && CivilizationUnitEffects.GetTargetObjectIds(saved).SequenceEqual([777, 878]),
            "Multiple targets are sorted and deduplicated with a legacy first-target mirror");
        Require(CivilizationUnitEffects.Describe(saved, id => id == 777 ? "Sherman" : "Citizen").Contains("Sherman, Citizen", StringComparison.Ordinal),
            "Effect descriptions include every affected target");
        var reopened = CivilizationEditProject.Deserialize(project.Serialize());
        Require(CivilizationUnitEffects.GetTargetObjectIds(CivilizationUnitEffects.Read(reopened.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single()).Single())
            .SequenceEqual([777, 878]), "Multiple target project save and reopen");
        var history = new CivilizationEditHistory(project);
        history.Change(next => CivilizationUnitEffects.Write(next.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single(),
            [saved with { TargetObjectIds = [749, 777, 878] }]));
        Require(history.Undo() && history.Current.Serialize() == project.Serialize(), "Multiple target undo restores the complete target set");
        Require(history.Redo() && CivilizationUnitEffects.GetTargetObjectIds(CivilizationUnitEffects.Read(history.Current.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single()).Single())
            .SequenceEqual([749, 777, 878]), "Multiple target redo restores the complete target set");
        var rules = CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity);
        Require(rules.Count == 2 && rules.Select(rule => rule.TargetObjectId).SequenceEqual([777, 878]) &&
            rules.All(rule => rule.Id != Guid.Parse(multi.Id) && rule.SourceObjectId == 749 && rule.CivilizationId == 12 &&
                rule.Trigger == CivilizationUnitEffectTrigger.Proximity && rule.RadiusTiles == 5 &&
                CivilizationRuntimeConfiguration.GetOutputs(rule).SequenceEqual([new(CivilizationUnitEffectStat.Health, 1000), new(CivilizationUnitEffectStat.LineOfSight, 30)])) &&
            rules.Select(rule => rule.Id).Distinct().Count() == 2, "One multi-target attachment expands to two independent native rules with complete outputs");
        var bytes = CivilizationRuntimeConfiguration.Encode([], rules);
        File.WriteAllBytes(Path.Combine(output, "multi-target-aura.edmwe"), bytes);
        CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = [777, 878] }]);
        Require(CivilizationRuntimeConfiguration.Encode([], CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity)).AsSpan().SequenceEqual(bytes),
            "Target order and duplicate selections cannot change runtime bytes or multiply output");
        CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = [878, 749, 777] }]);
        var extended = CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity);
        Require(rules.All(rule => extended.Single(item => item.TargetObjectId == rule.TargetObjectId).Id == rule.Id),
            "Adding another target preserves existing target identities");
        CivilizationUnitEffects.Write(entity, [original with { TargetObjectIds = null }]);
        Require(CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity).Single().Id == Guid.Parse(original.Id) &&
            CivilizationUnitEffects.Read(entity).Single().TargetObjectIds is null, "Legacy scalar targets retain their original effect identity");
        Reject(() => CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = [] }]), "Explicit empty target set");
        Reject(() => CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = [777, -1] }]), "Negative target identity");
        Reject(() => CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = [777, 100000] }]), "Out-of-range target identity");
        Reject(() => CivilizationUnitEffects.Write(entity, [multi with { Trigger = CivilizationUnitEffectTrigger.Activated,
            TargetObjectIds = [749, 777], NativeTechnologyId = 10000 }]), "Activated abilities cannot target other object types");
        CivilizationUnitEffects.Write(entity, [multi]);
        var duplicate = new CivilizationEntityDraft { Name = "Duplicate author identity", Category = CivilizationCategory.Units, SourceObjectId = 777 };
        CivilizationUnitEffects.Write(duplicate, [multi with { TargetObjectIds = [749, 750] }]);
        civilization.Entities.Add(duplicate);
        Reject(() => CivilizationInstallerPackage.Create(project, fixture), "Duplicate authored identity with different expanded target sets");
        civilization.Entities.Remove(duplicate);
        var huge = multi with { TargetObjectIds = Enumerable.Range(0, 1025).ToArray() };
        CivilizationUnitEffects.Write(entity, [huge]);
        Reject(() => CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity), "Per-entity expanded runtime capacity");
        CivilizationUnitEffects.Write(entity, [multi with { TargetObjectIds = Enumerable.Range(0, 513).ToArray() }]);
        var overflow = new CivilizationEntityDraft { Name = "Aggregate overflow", Category = CivilizationCategory.Units, SourceObjectId = 777 };
        CivilizationUnitEffects.Write(overflow, [multi with { Id = Guid.NewGuid().ToString(), TargetObjectIds = Enumerable.Range(0, 513).ToArray() }]);
        civilization.Entities.Add(overflow);
        Reject(() => CivilizationInstallerPackage.Create(project, fixture), "Aggregate expanded package capacity before installation");
        civilization.Entities.Remove(overflow);
        CivilizationUnitEffects.Write(entity, [multi]);
        var package = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        var payload = package.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!;
        Require(payload.AsSpan().SequenceEqual(bytes), "Multiple target package codec preserves canonical runtime expansion");
        var tampered = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(package));
        tampered.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement![40] ^= 1;
        Reject(() => CivilizationInstallerPackageCodec.Encode(tampered), "Multi-target sidecar must match its authored target set");
        Success(CivilizationPackageManager.Install(package, fixture));
        byte[]? launch = null;
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launch = value);
        Require(launch!.AsSpan().SequenceEqual(bytes), "Multiple targets survive installation and launch aggregation");
        var shared = CivilizationPackageManager.CreateSharePackage(fixture, package.PackageId);
        Require(CivilizationInstallerPackageCodec.Encode(shared).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(package)),
            "Sharing preserves authored targets and stable runtime identities");
        Success(CivilizationPackageManager.Uninstall(fixture, package.PackageId));
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launch = value);
        Require(CivilizationRuntimeConfiguration.Decode(launch!).Count == 0, "Multi-target uninstall removes every expanded rule");
    }

    private static string VerifyAbilityButtonSlots(byte[] objects, byte[] technologies, byte[] buttons, byte[] events)
    {
        // Native explicit command widgets Action1..9 overlap these zero-based
        // Train/Research grid indices, independently of technology/icon metadata.
        // The authoring API exposes slots 1..41, so command index zero is outside it.
        int[] explicitCommandSlots = [0, 1, 2, 14, 15, 16, 28, 29, 30];
        int[] nativeContextSlots = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26];
        var records = CivilizationTechnologyBindings.ReadRecords(technologies);
        var inputHashes = new[] { objects, technologies, buttons, events }.Select(SHA256.HashData).ToArray();
        var unitContexts = 0;
        var buildingContexts = 0;
        for (var row = 3; row <= 11; row++)
        foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
        foreach (var sourceId in CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, records, row, category))
        {
            var source = new CivilizationEntityDraft { Name = "HUD slot regression", Category = category, SourceObjectId = sourceId };
            var offset = CivilizationObjectBindings.ValidateRecord(objects, sourceId, category);
            var free = CivilizationUnitEffectBindings.AvailableButtonSlots(objects, technologies, buttons, source, row).ToHashSet();
            Require(free.All(slot => slot is >= 1 and <= 41), "Only valid native grid slots are offered");
            Require(!free.Overlaps(explicitCommandSlots) && !free.Overlaps(nativeContextSlots),
                "Normal commands and native context controls retain their physical cells in every source/civilization context");
            for (var command = 0; command < explicitCommandSlots.Length; command++)
            {
                var technologyId = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(offset +
                    CivilizationObjectDetails.CommandTechnologyIdsOffset + command * 4));
                if (technologyId >= 0 && explicitCommandSlots[command] > 0)
                    Require(!free.Contains(explicitCommandSlots[command]),
                        $"Civilization {row} source {sourceId} preserves native command {command} at physical slot {explicitCommandSlots[command]}");
            }
            var actionIds = CivilizationObjectDetails.ReadActionTechnologyIds(objects, sourceId, category)
                .Select(id => checked((int)id)).ToHashSet();
            foreach (var record in records.Where(record => !record.EditorOnly && record.AllowsCivilizationRow(row) &&
                (actionIds.Contains(record.TechId) || record.ProducerObjectIds.Contains(sourceId))))
            {
                var slot = CivilizationTechnologyBindings.ReadProgressionButtonSlot(record, row, buttons);
                if (slot > 0)
                    Require(!free.Contains(slot), $"Civilization {row} source {sourceId} preserves native production/research slot {slot}");
            }
            if (category == CivilizationCategory.Units) unitContexts++; else buildingContexts++;
        }
        Require(unitContexts > 0 && buildingContexts > 0, "Slot regression covers playable units and buildings");

        var citizen = new CivilizationEntityDraft { Name = "Citizen (China)", Category = CivilizationCategory.Units, SourceObjectId = 878 };
        var commands = CivilizationObjectDetails.ReadCommandTechnologyIds(objects, 878, CivilizationCategory.Units);
        Require(commands.Take(4).SequenceEqual([1730, 1729, 1719, 1720]), "Citizen regression retains Stop, Explore, Garrison and Back to Work commands");
        var citizenFree = CivilizationUnitEffectBindings.AvailableButtonSlots(objects, technologies, buttons, citizen, 3);
        var effect = new CivilizationUnitEffect { Name = "Command preservation", TargetObjectId = 878,
            Trigger = CivilizationUnitEffectTrigger.Activated, ButtonCaption = "Command preservation", ButtonIconId = 1350 };
        foreach (var slot in new[] { 1, 2, 14 })
        {
            Require(!citizenFree.Contains(slot), "Chinese Citizen native Explore/Garrison/Back to Work slots are unavailable");
            Reject(() => CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events,
                [new(3, citizen, effect with { ButtonSlot = slot })]), "An ability cannot replace a Chinese Citizen native command");
        }
        Require(citizenFree.Count > 0, "The Chinese Citizen retains a safe custom ability cell");
        var citizenBuilt = CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events,
            [new(3, citizen, effect with { ButtonSlot = citizenFree[0] })]);
        var commandOffset = CivilizationObjectBindings.ValidateRecord(objects, 878, CivilizationCategory.Units) +
            CivilizationObjectDetails.CommandTechnologyIdsOffset;
        Require(objects.AsSpan(commandOffset, CivilizationObjectDetails.CommandTechnologyCount * 4).SequenceEqual(
            citizenBuilt.Objects.AsSpan(commandOffset, CivilizationObjectDetails.CommandTechnologyCount * 4)),
            "Adding a safe Chinese Citizen ability preserves all nine raw native command references");
        var citizenRecords = CivilizationTechnologyBindings.ReadRecords(citizenBuilt.Technologies).ToDictionary(record => record.TechId);
        foreach (var record in records)
            Require(technologies.AsSpan(record.Offset, record.Length).SequenceEqual(
                citizenBuilt.Technologies.AsSpan(citizenRecords[record.TechId].Offset, record.Length)),
                "A Chinese Citizen ability preserves every original technology record byte for byte");

        var ranger = new CivilizationEntityDraft { Name = "Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var freeSlots = CivilizationUnitEffectBindings.AvailableButtonSlots(objects, technologies, buttons, ranger, 11);
        Require(freeSlots.Count > 0, "Ranger has a safe slot for command exhaustion regression");
        var requests = freeSlots.Select(slot => new CivilizationActivatedAbilityRequest(11, ranger,
            effect with { Id = Guid.NewGuid().ToString(), TargetObjectId = 749, ButtonSlot = slot })).ToArray();
        var filled = CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events, requests);
        Require(filled.Registrations.Count == freeSlots.Count &&
            CivilizationUnitEffectBindings.AvailableButtonSlots(filled.Objects, filled.Technologies, buttons, ranger, 11).Count == 0,
            "Every safe slot can be allocated once and exhaustion returns an empty set");
        var duplicate = requests[0] with { Effect = requests[0].Effect with { Id = Guid.NewGuid().ToString() } };
        Reject(() => CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events, [requests[0], duplicate]),
            "Two custom abilities cannot claim one physical HUD slot");
        Reject(() => CivilizationUnitEffectBindings.BuildAbilityButtons(filled.Objects, filled.Technologies, buttons, events, [duplicate]),
            "An exhausted HUD cannot fall back onto an occupied slot");
        var inputs = new[] { objects, technologies, buttons, events };
        for (var index = 0; index < inputs.Length; index++)
            Require(SHA256.HashData(inputs[index]).AsSpan().SequenceEqual(inputHashes[index]),
                "Native command slot checks and rejected builds preserve original database arrays");
        return $"PASS: native fixed-command and production slots preserved across {unitContexts} playable unit and {buildingContexts} building contexts; Citizen Explore/Garrison/Back to Work collisions rejected; unique custom slots and complete-slot exhaustion; immutable input databases.";
    }

    private static void VerifyStatCatalog()
    {
        var choices = CivilizationUnitEffects.StatChoices;
        Require(choices.Select(choice => choice.Stat).Distinct().Count() == Enum.GetValues<CivilizationUnitEffectStat>().Length,
            "Every stat output has exactly one descriptor");
        Require(choices.All(choice => choice.RuntimeSupported), "Every catalog output has a native runtime adapter");
        var excluded = new HashSet<string>(StringComparer.Ordinal)
            { "graphicsId", "icon", "attackType", "projectile", "targetFlags", "resourceTypeId", "initialAge", "finalAge" };
        var numericStats = new[] { CivilizationCategory.Units, CivilizationCategory.Buildings }
            .SelectMany(category => CivilizationSchema.GetFields(category))
            .Where(field => field.Kind == CivilizationFieldKind.Number)
            .Select(field => field.Key[(field.Key.IndexOf('.') + 1)..]).Where(suffix => !excluded.Contains(suffix)).Distinct();
        Require(numericStats.All(suffix => choices.Any(choice => choice.PropertySuffix == suffix)),
            "All numeric unit/building stats appear as effect outputs");
        var entity = new CivilizationEntityDraft { Name = "Output catalog", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var effects = choices.Select(choice => new CivilizationUnitEffect
            { Name = choice.Name, TargetObjectId = 749, Stat = choice.Stat, Amount = choice.DefaultAmount }).ToArray();
        CivilizationUnitEffects.Write(entity, effects);
        var persisted = CivilizationUnitEffects.Read(entity);
        Require(persisted.Zip(effects).All(pair => CivilizationUnitEffects.GetOutputs(pair.First).SequenceEqual(CivilizationUnitEffects.GetOutputs(pair.Second))),
            "All stat outputs persist without losing their amounts");
        var combined = effects[0] with { Outputs = choices.Select(choice => new CivilizationUnitEffectOutput(choice.Stat, choice.DefaultAmount)).ToArray() };
        CivilizationUnitEffects.Write(entity, [combined]);
        Require(CivilizationUnitEffects.GetOutputs(CivilizationUnitEffects.Read(entity).Single()).SequenceEqual(CivilizationUnitEffects.GetOutputs(combined)),
            "All 51 outputs can be attached to one shared effect");
        Reject(() => CivilizationUnitEffects.Write(entity, [combined with { Outputs = [] }]), "Empty output list");
        Reject(() => CivilizationUnitEffects.Write(entity, [combined with { Outputs = [new(CivilizationUnitEffectStat.LineOfSight, 1), new(CivilizationUnitEffectStat.LineOfSight, 2)] }]),
            "Duplicate output stat");
        CivilizationUnitEffects.Write(entity, [effects[0]]);
        var legacyJson = JsonNode.Parse(entity.Properties[CivilizationUnitEffects.PropertyKey])!.AsArray();
        legacyJson[0]!.AsObject().Remove("outputs");
        entity.Properties[CivilizationUnitEffects.PropertyKey] = legacyJson.ToJsonString();
        Require(CivilizationUnitEffects.Read(entity).Single().Outputs is { Count: 1 }, "Legacy stat/amount project migrates to one output");
        legacyJson[0]!.AsObject()["outputs"] = new JsonArray(new JsonObject { ["stat"] = "LineOfSight", ["amount"] = 3 });
        legacyJson[0]!.AsObject().Remove("stat");
        legacyJson[0]!.AsObject().Remove("amount");
        entity.Properties[CivilizationUnitEffects.PropertyKey] = legacyJson.ToJsonString();
        Require(CivilizationUnitEffects.GetOutputs(CivilizationUnitEffects.Read(entity).Single()).Single().Amount == 3,
            "Current output-list projects do not require legacy mirrors");
        foreach (var choice in choices)
        {
            Reject(() => CivilizationUnitEffects.ValidateAmount(choice.Stat, 0), "Zero " + choice.Name);
            Reject(() => CivilizationUnitEffects.ValidateAmount(choice.Stat, choice.MaximumAmount + 1), "Oversized " + choice.Name);
            Reject(() => CivilizationUnitEffects.ValidateAmount(choice.Stat, choice.DefaultAmount + 1m / (decimal)Math.Pow(10, choice.DecimalPlaces + 1)),
                "Excess precision " + choice.Name);
            Require(CivilizationUnitEffects.Describe(effects.Single(effect => effect.Stat == choice.Stat)).Contains(choice.Name, StringComparison.OrdinalIgnoreCase),
                "Descriptions name the selected output");
            if (CivilizationUnitEffects.IsLimit(choice.Stat))
            {
                Reject(() => CivilizationUnitEffects.ValidateAmount(choice.Stat, -1), "Positive set limit " + choice.Name);
                Require(CivilizationUnitEffects.DescribeOutput(new(choice.Stat, choice.DefaultAmount)).StartsWith("Limit ", StringComparison.Ordinal),
                    "Custom limits describe SET behavior without additive sign");
            }
            else
            {
                foreach (var amount in new[] { choice.MinimumAmount, choice.MaximumAmount })
                {
                    var roundTrip = (decimal)(float)amount;
                    Require(roundTrip == amount, "Catalog bounds survive native float precision: " + choice.Name);
                }
                var nearMaximum = choice.MaximumAmount - 1m / (decimal)Math.Pow(10, choice.DecimalPlaces);
                Require((decimal)(float)nearMaximum == nearMaximum, "Catalog precision survives native float near its bound: " + choice.Name);
            }
        }
        var civilization = CivilizationEditProject.CreateDefault().Civilizations.Single(civ => civ.SourceRowIndex == 11);
        var unsupported = choices.FirstOrDefault(choice => !choice.RuntimeSupported);
        if (unsupported is not null)
        {
            CivilizationUnitEffects.Write(entity, [effects.Single(effect => effect.Stat == unsupported.Stat)]);
            Reject(() => CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity), "Unsupported enabled output export");
            CivilizationUnitEffects.Write(entity, [CivilizationUnitEffects.Read(entity).Single() with { Enabled = false }]);
            Require(CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity).Count == 0, "Disabled draft outputs do not block supported exports");
        }
    }

    private static void VerifyAbilityEpochGroups(byte[] objects, byte[] technologies, byte[] buttons, byte[] events,
        CivilizationEntityDraft source, CivilizationUnitEffect effect)
    {
        var originalRecords = CivilizationTechnologyBindings.ReadRecords(technologies);
        var originalEpochs = originalRecords.Where(record => record.IsEpochDefinition).ToArray();
        CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(originalRecords);
        foreach (var technologyId in new[] { 10000, 11000, 12000, 13000, 14000 })
        {
            var build = CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events,
                [new(11, source, effect with { NativeTechnologyId = technologyId })]);
            var records = CivilizationTechnologyBindings.ReadRecords(build.Technologies);
            var epochs = records.Where(record => record.IsEpochDefinition).ToArray();
            var expectedEpoch = technologyId / 1000 - 1;
            var created = records.Single(record => record.TechId == technologyId);
            Require(created.StartAge == 0 && created.EndAge == 14 && expectedEpoch >= 9,
                "Reserved future-group ability IDs retain all-age disk availability for the scoped native age adapter");
            Require(created.NodeKind == 3 && created.CommandId == 51 && created.EventId == 0 &&
                !created.EditorOnly && created.PrerequisiteTechnologyIds.Count == 0,
                "Every reserved ability is a visible immediate node with no research prerequisites");
            var start = epochs[expectedEpoch].Offset;
            var end = expectedEpoch + 1 < epochs.Length ? epochs[expectedEpoch + 1].Offset - 4 : build.Technologies.Length;
            Require(created.Offset > start && created.Offset + created.Length <= end,
                "Native technology lookup finds the ability within its computed epoch group");
            for (var epoch = 0; epoch < epochs.Length; epoch++)
            {
                var originalCount = BinaryPrimitives.ReadInt32LittleEndian(technologies.AsSpan(originalEpochs[epoch].Offset - 4));
                var count = BinaryPrimitives.ReadInt32LittleEndian(build.Technologies.AsSpan(epochs[epoch].Offset - 4));
                Require(count == originalCount + (epoch == expectedEpoch ? 1 : 0), "Only the matching epoch's native child count changes");
            }
            if (technologyId != 10000) continue;
            // Recreate the former flattened-parser false positive: an otherwise
            // valid ability record placed in the first group rather than group 9.
            var firstGroupEnd = originalEpochs[1].Offset - 4;
            using var misplaced = new MemoryStream();
            misplaced.Write(technologies.AsSpan(0, firstGroupEnd));
            misplaced.Write(build.Technologies.AsSpan(created.Offset, created.Length));
            misplaced.Write(technologies.AsSpan(firstGroupEnd));
            var malformed = misplaced.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(malformed, BinaryPrimitives.ReadInt32LittleEndian(technologies) + 1);
            var flattened = CivilizationTechnologyBindings.ReadRecords(malformed);
            Require(flattened.Any(record => record.TechId == 10000), "Malformed grouping remains syntactically readable");
            Reject(() => CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(flattened),
                "An ability in the wrong native epoch group is rejected");
            Reject(() => CivilizationUnitEffectBindings.RepairAbilityTechnologyGroups(malformed, []),
                "Unverified records cannot be moved between native groups");
            var repaired = CivilizationUnitEffectBindings.RepairAbilityTechnologyGroups(malformed, [10000]);
            Require(repaired.AsSpan().SequenceEqual(build.Technologies),
                "Repair of a verified earlier export preserves every original record and restores native lookup");
            Require(CivilizationUnitEffectBindings.RepairAbilityTechnologyGroups(repaired, [10000]).AsSpan().SequenceEqual(repaired),
                "Native ability group repair is idempotent");
        }
    }

    private static void VerifyLegacyAbilitySlot(CivilizationInstallerPackage correctedPackage, string fixture, string[] sourceFiles)
    {
        var legacy = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(correctedPackage));
        var project = CivilizationEditProject.Deserialize(legacy.ProjectJson);
        var entity = project.Civilizations.SelectMany(civilization => civilization.Entities)
            .First(candidate => CivilizationUnitEffects.Read(candidate).Any(effect => effect.Trigger == CivilizationUnitEffectTrigger.Activated));
        var effects = CivilizationUnitEffects.Read(entity);
        var previous = effects.First(effect => effect.Trigger == CivilizationUnitEffectTrigger.Activated);
        CivilizationUnitEffects.Write(entity, effects.Select(effect => effect.Id == previous.Id ? effect with { ButtonSlot = 1 } : effect));
        legacy.ProjectJson = project.Serialize();
        var technologyFile = legacy.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        var record = CivilizationTechnologyBindings.ReadRecords(technologyFile.Replacement!)
            .Single(candidate => candidate.TechId == previous.NativeTechnologyId);
        BinaryPrimitives.WriteInt32LittleEndian(technologyFile.Replacement!.AsSpan(record.Offset + 276), 1);
        for (var row = 0; row < 11; row++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(technologyFile.Replacement.AsSpan(record.Offset + 324 + row * 8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(technologyFile.Replacement.AsSpan(record.Offset + 328 + row * 8), 1);
        }
        legacy = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacy));
        technologyFile = legacy.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        var legacyTable = technologyFile.Replacement!.ToArray();
        var before = sourceFiles.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, path))));
        Success(CivilizationPackageManager.Install(legacy, fixture));
        Require(File.ReadAllBytes(Path.Combine(fixture, technologyFile.RelativePath)).AsSpan().SequenceEqual(legacyTable),
            "A retained slot-1 package installs its original on-disk registration without remapping game files");
        var expectedRules = legacy.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))
            .SelectMany(file => CivilizationRuntimeConfiguration.Decode(file.Replacement!).Units).ToDictionary(rule => rule.Id);
        CivilizationRuntimeConfiguration? snapshot = null;
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => snapshot = CivilizationRuntimeConfiguration.Decode(bytes));
        Require(snapshot is not null && snapshot.Units.Count == expectedRules.Count && snapshot.Units.All(rule =>
            expectedRules.TryGetValue(rule.Id, out var expected) && CivilizationRuntimeConfiguration.Equivalent(rule, expected)),
            "A retained slot-1 export keeps its native command and effect identity in the launch configuration");
        Require(CivilizationInstallerPackageCodec.Encode(CivilizationPackageManager.CreateSharePackage(fixture, legacy.PackageId)).AsSpan()
            .SequenceEqual(CivilizationInstallerPackageCodec.Encode(legacy)), "Sharing retains the old slot-1 export byte for byte");
        Require(File.ReadAllBytes(Path.Combine(fixture, technologyFile.RelativePath)).AsSpan().SequenceEqual(legacyTable),
            "Launch configuration and sharing do not persist the native runtime's process-only button remapping");
        Success(CivilizationPackageManager.Uninstall(fixture, legacy.PackageId));
        foreach (var file in sourceFiles)
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(before[file]),
                "Uninstall of an old overlapping-button export restores all six original game files");
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => snapshot = CivilizationRuntimeConfiguration.Decode(bytes));
        Require(snapshot!.Count == 0, "Uninstall of a retained slot-1 export restores the plain game launch configuration");
    }

    private static void VerifyLegacyAbilityInstall(CivilizationInstallerPackage correctedPackage, string fixture, string[] sourceFiles)
    {
        var legacy = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(correctedPackage));
        var technologyFile = legacy.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        var records = CivilizationTechnologyBindings.ReadRecords(technologyFile.Replacement!);
        var epochs = records.Where(record => record.IsEpochDefinition).ToArray();
        var moved = records.First(record => record.Identifier.StartsWith("EDMW ability ", StringComparison.Ordinal));
        using var misplaced = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        for (var epoch = 0; epoch < epochs.Length; epoch++)
        {
            var end = epoch + 1 < epochs.Length ? epochs[epoch + 1].Offset - 4 : technologyFile.Replacement!.Length;
            var children = records.Where(record => !record.IsEpochDefinition && record.Offset > epochs[epoch].Offset &&
                record.Offset < end && record.TechId != moved.TechId).ToList();
            if (epoch == 0) children.Add(moved);
            BinaryPrimitives.WriteInt32LittleEndian(header, children.Count);
            misplaced.Write(header);
            misplaced.Write(technologyFile.Replacement!.AsSpan(epochs[epoch].Offset, epochs[epoch].Length));
            foreach (var child in children) misplaced.Write(technologyFile.Replacement!.AsSpan(child.Offset, child.Length));
        }
        technologyFile.Replacement = misplaced.ToArray();
        // Earlier exports must remain readable as retained manifests and journals.
        legacy = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacy));
        var before = sourceFiles.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, path))));
        var rejection = CivilizationPackageManager.Install(legacy, fixture);
        Require(!rejection.Success && rejection.Message.Contains("Re-export and reinstall", StringComparison.Ordinal),
            "Incoming legacy ability grouping is rejected with recovery guidance");
        Require(CivilizationPackageManager.Review(legacy, fixture).Any(message => message.Contains("Re-export and reinstall", StringComparison.Ordinal)),
            "Read-only install review rejects legacy ability grouping");
        foreach (var file in sourceFiles)
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(before[file]),
                "Rejected legacy ability install changes no game files");

        // Model an already installed export from the earlier version, including
        // its retained journal and backup index, only inside this disposable fixture.
        var installation = CivilizationPackageManager.Install(correctedPackage, fixture);
        Success(installation);
        var storage = Path.Combine(fixture, ".edmw-civilization-packages");
        var manifestPath = Path.Combine(storage, "installed", legacy.PackageId + ".json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["package"] = System.Text.Json.JsonSerializer.SerializeToNode(legacy, CivilizationInstallerPackageCodec.JsonOptions);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var badTable = legacy.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath)).Replacement!;
        File.WriteAllBytes(Path.Combine(fixture, technologyFile.RelativePath), badTable);
        foreach (var indexPath in new[] { Path.Combine(storage, "backups", legacy.PackageId + ".json"), Path.Combine(installation.BackupPath!, "transaction.json") })
        {
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            if (index.ContainsKey("package"))
                index["package"] = System.Text.Json.JsonSerializer.SerializeToNode(legacy, CivilizationInstallerPackageCodec.JsonOptions);
            foreach (var file in index["files"]!.AsArray())
                if (file!["relativePath"]!.GetValue<string>() == technologyFile.RelativePath)
                    file["afterHash"] = CivilizationInstallerPackage.Hash(badTable);
            File.WriteAllText(indexPath, index.ToJsonString());
        }
        Require(CivilizationPackageManager.ReadInstalled(fixture).Single().PackageId == legacy.PackageId,
            "Retained legacy ability manifests and journals remain readable");
        Success(CivilizationPackageManager.Recover(fixture));
        Success(CivilizationPackageManager.Uninstall(fixture, legacy.PackageId));
        foreach (var file in sourceFiles)
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(before[file]),
                "Legacy ability uninstall restores the original game files");
    }
    private static void Require(bool value, string check) { if (!value) throw new InvalidOperationException(check); }
    private static void Success(CivilizationInstallResult result) => Require(result.Success, result.Message);
    private static void Reject(Action action, string check)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Accepted invalid input: " + check);
    }
}
