using System.Security.Cryptography;
using EDMWMapDesigner.UI;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationRuntimeEffectsVerification
{
    public static void Run(string source, string output)
    {
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
        var objects = File.ReadAllBytes(Path.Combine(fixture, files[1]));
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
        var malformedAura = auraFile.Replacement!.ToArray(); malformedAura[44] = 0;
        Reject(() => CivilizationRuntimeConfiguration.Decode(malformedAura), "Zero sight adjustment");
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
        var activatedProject = auraProject.Clone();
        activatedProject.Name = "Reconnaissance abilities";
        var activatedRanger = activatedProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single();
        var buttons = File.ReadAllBytes(Path.Combine(fixture, "Data\\db\\dbbuttons.dat"));
        var existingActions = CivilizationObjectDetails.ReadCommandTechnologyIds(objects, 749, CivilizationCategory.Units)
            .Concat(CivilizationObjectDetails.ReadActionTechnologyIds(objects, 749, CivilizationCategory.Units).Select(id => checked((int)id))).ToHashSet();
        var usedSlots = records.Where(record => (existingActions.Contains(record.TechId) || record.ProducerObjectIds.Contains(749)) &&
            record.AllowsCivilizationRow(11) && !record.EditorOnly)
            .Select(record => CivilizationTechnologyBindings.ReadProgressionButtonSlot(record, 11, buttons)).ToHashSet();
        var freeSlots = Enumerable.Range(1, 41).Where(slot => !usedSlots.Contains(slot)).Take(2).ToArray();
        Require(freeSlots.Length == 2, "Two free ability button slots");
        var toggle = aura with { Id = Guid.NewGuid().ToString(), Name = "Recon toggle", Trigger = CivilizationUnitEffectTrigger.Activated,
            TargetObjectId = 749, ActivationMode = CivilizationUnitEffectActivationMode.Toggle, ButtonCaption = "Recon toggle", ButtonIconId = 1350, ButtonSlot = freeSlots[0] };
        var timed = toggle with { Id = Guid.NewGuid().ToString(), Name = "Recon burst", ActivationMode = CivilizationUnitEffectActivationMode.Timed,
            DurationSeconds = 15, ButtonCaption = "Recon burst", ButtonSlot = freeSlots[1] };
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
        Success(CivilizationPackageManager.Uninstall(fixture, abilityPackage.PackageId));
        Require(Configuration().Count == 0, "Ability uninstall removes runtime registrations");
        foreach (var file in files)
        {
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, file))).AsSpan().SequenceEqual(hashes[file]), "Fixture database/executable unchanged");
            Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, file))).AsSpan().SequenceEqual(hashes[file]), "Source game unchanged");
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "runtime-effects-result.txt"),
            "PASS: project/history, research/proximity/toggle/timed package codec, command registration, validation, concurrent research/proximity mods, persisted toggles, details, sharing, tamper detection, selective uninstall, original file hashes.\n" +
            "UNVERIFIED: live buttons/effects, fog refresh, save/load, two-player match. Activated state is session-only; ability tables require exclusive package ownership.\n");
        Console.WriteLine("RUNTIME EFFECTS PASS: research/proximity/activated authoring, codec, native button registration, toggle, share, tamper detection, selective uninstall, unchanged source files.");

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
    private static void Require(bool value, string check) { if (!value) throw new InvalidOperationException(check); }
    private static void Success(CivilizationInstallResult result) => Require(result.Success, result.Message);
    private static void Reject(Action action, string check)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Accepted invalid input: " + check);
    }
}
