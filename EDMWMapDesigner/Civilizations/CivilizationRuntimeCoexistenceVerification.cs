using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Exercises independent runtime packages against an already isolated, clean game fixture.</summary>
internal static class CivilizationRuntimeCoexistenceVerification
{
    internal static void Run(string fixture)
    {
        const string objectsPath = "Data\\db\\dbobjects.dat";
        const string technologiesPath = "Data\\db\\dbtechtree.dat";
        var originals = new[] { objectsPath, technologiesPath, "Data\\db\\dbphoenixcivtable.dat", "Data\\db\\dbbuttons.dat", "Data\\db\\dbevents.dat", "Empires_DMW.exe" }
            .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0, "Coexistence verification requires a clean fixture.");
        var source = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var slots = CivilizationUnitEffectBindings.AvailableButtonSlots(originals[objectsPath], originals[technologiesPath],
            originals["Data\\db\\dbbuttons.dat"], source, 11).Take(2).ToArray();
        Require(slots.Length == 2, "Coexistence fixture has two available Ranger ability slots.");
        CivilizationEditProject AbilityProject(string name, int slot, decimal amount)
        {
            var project = CivilizationEditProject.CreateDefault();
            project.Name = name;
            var ranger = new CivilizationEntityDraft { Name = source.Name, Category = source.Category, SourceObjectId = source.SourceObjectId };
            CivilizationUnitEffects.Write(ranger, [new()
            {
                Name = name, TargetObjectId = 749, Trigger = CivilizationUnitEffectTrigger.Activated,
                ActivationMode = CivilizationUnitEffectActivationMode.Toggle, ButtonCaption = name,
                ButtonIconId = 1350, ButtonSlot = slot, Amount = amount
            }]);
            project.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(ranger);
            return project;
        }
        var firstProject = AbilityProject("Independent recon A", slots[0], 3);
        var secondProject = AbilityProject("Independent recon B", slots[1], 7);
        var first = Package(firstProject);
        var second = Package(secondProject);
        var firstRule = Rules(first).Single();
        var secondRule = Rules(second).Single();
        Require(firstRule.TechnologyId == secondRule.TechnologyId,
            "Independently built ability exports begin with colliding native command IDs.");
        var portableFirst = CivilizationInstallerPackageCodec.Encode(first);
        var portableSecond = CivilizationInstallerPackageCodec.Encode(second);

        // Each export can arrive first, and the first installed package can be
        // removed before the second. An earlier command must never be restored
        // by a later package's complete-table backup.
        foreach (var reverseInstall in new[] { false, true })
        foreach (var removeEarlierFirst in new[] { false, true })
        {
            var earlier = reverseInstall ? second : first;
            var later = reverseInstall ? first : second;
            Success(CivilizationPackageManager.Install(earlier, fixture));
            CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, earlier.PackageId, false);
            Require(CivilizationPackageManager.Review(later, fixture).Count == 0,
                "A second independently exported ability is reviewable while the first is disabled.");
            var lanReview = CivilizationLanPackageReview.Prepare(later, fixture);
            Require(lanReview.Issues.Count == 0 && lanReview.Package is not null,
                "LAN recipient review rebuilds an independent ability despite an installed command-ID collision: " + string.Join("; ", lanReview.Issues));
            var reviewedLater = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(lanReview.Package!));
            Require(reviewedLater.PackageId == later.PackageId && Rules(reviewedLater).Select(rule => rule.Id).SequenceEqual(Rules(later).Select(rule => rule.Id)),
                "LAN canonical review preserves the incoming package and authored effect identities before installation-local rebasing.");
            Success(CivilizationPackageManager.Install(later, fixture));
            var snapshot = Snapshot();
            Require(snapshot.Units.Count == 2 && snapshot.Units.Select(rule => rule.TechnologyId).Distinct().Count() == 2 &&
                snapshot.Units.Select(rule => rule.Id).ToHashSet().SetEquals([firstRule.Id, secondRule.Id]),
                "Two independent ability packages retain distinct authored identities and receive distinct native commands.");
            Require(snapshot.Units.Count(rule => rule.Enabled) == 1,
                "A disabled ability keeps its inactive command binding while another ability runs.");
            var commandIds = snapshot.Units.ToDictionary(rule => rule.Id, rule => rule.TechnologyId);
            CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, earlier.PackageId, true);
            Require(Snapshot().Units.All(rule => rule.Enabled && commandIds[rule.Id] == rule.TechnologyId),
                "Re-enabling an ability preserves both installed command identities.");
            VerifyRegistrations(Snapshot());
            Success(CivilizationPackageManager.InstallOrVerifyInstalled(later, fixture));
            var shared = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(
                CivilizationPackageManager.CreateSharePackage(fixture, later.PackageId)));
            Require(shared.PackageId == later.PackageId && Rules(shared).Select(rule => rule.Id).SequenceEqual(Rules(later).Select(rule => rule.Id)),
                "Sharing an installed rebased ability preserves its package and authored identities.");
            Success(CivilizationPackageManager.InstallOrVerifyInstalled(shared, fixture));
            Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 2,
                "Importing original and shared copies is idempotent after command rebasing.");

            var remove = removeEarlierFirst ? earlier : later;
            var keep = removeEarlierFirst ? later : earlier;
            Success(CivilizationPackageManager.Uninstall(fixture, remove.PackageId));
            Require(Snapshot().Units.Count == 1 && Snapshot().Units.Single().Id == Rules(keep).Single().Id &&
                Snapshot().Units.Single().TechnologyId == commandIds[Rules(keep).Single().Id],
                "Selective removal preserves the other ability and its installed command ID.");
            VerifyRegistrations(Snapshot());
            _ = CivilizationPackageManager.CreateSharePackage(fixture, keep.PackageId);
            Success(CivilizationPackageManager.Uninstall(fixture, keep.PackageId));
            Restored();
        }
        Console.WriteLine("RUNTIME COEXISTENCE: independent exports pass LAN review, command rebasing, sharing and both install/removal orders.");
        Require(CivilizationInstallerPackageCodec.Encode(first).AsSpan().SequenceEqual(portableFirst) &&
            CivilizationInstallerPackageCodec.Encode(second).AsSpan().SequenceEqual(portableSecond),
            "Review, rebasing, installation and sharing leave the caller's portable exports unchanged.");

        // Authoring a second export from a game that already contains the first
        // edit must not make that export depend on retaining the first edit.
        var secondDraft = secondProject.Serialize();
        Success(CivilizationPackageManager.Install(first, fixture));
        var builtAfterFirst = Package(secondProject);
        Require(secondProject.Serialize() == secondDraft && builtAfterFirst.BaselineAbilityTechnologyIds.Contains(firstRule.TechnologyId) &&
            Rules(builtAfterFirst).Single().TechnologyId != firstRule.TechnologyId,
            "Export reserves an installed command while preserving the authored draft and recording the source's tracked registrations.");
        Success(CivilizationPackageManager.Install(builtAfterFirst, fixture));
        Require(Snapshot().Units.Select(rule => rule.Id).ToHashSet().SetEquals([firstRule.Id, secondRule.Id]),
            "An export authored against cumulative installed tables contributes only its own effect.");
        Success(CivilizationPackageManager.Uninstall(fixture, first.PackageId));
        Require(Snapshot().Units.Single().Id == secondRule.Id, "Removing the source game's earlier edit leaves the later authored export active.");
        VerifyRegistrations(Snapshot());
        var cumulativeShare = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(
            CivilizationPackageManager.CreateSharePackage(fixture, builtAfterFirst.PackageId)));
        Require(Rules(cumulativeShare).Single().Id == secondRule.Id,
            "An edit remains independently shareable after another edit recorded in its source layout is removed.");
        Success(CivilizationPackageManager.Uninstall(fixture, builtAfterFirst.PackageId));
        Restored();
        var cumulativeReview = CivilizationLanPackageReview.Prepare(cumulativeShare, fixture);
        Require(cumulativeReview.Issues.Count == 0 && cumulativeReview.Package is not null,
            "A clean recipient can review an export authored against another installed edit: " + string.Join("; ", cumulativeReview.Issues));
        Success(CivilizationPackageManager.Install(cumulativeShare, fixture));
        Require(Snapshot().Units.Single().Id == secondRule.Id,
            "A clean recipient installs only the later export's effect, without importing its author's earlier edit.");
        VerifyRegistrations(Snapshot());
        Success(CivilizationPackageManager.Uninstall(fixture, cumulativeShare.PackageId));
        Restored();

        // Earlier portable exports contain only whole-table hashes and patched
        // bytes. Pure command additions can recover their exact source baseline.
        var legacyFirst = CivilizationInstallerPackageCodec.Decode(portableFirst);
        var legacySecond = CivilizationInstallerPackageCodec.Decode(portableSecond);
        foreach (var legacy in new[] { legacyFirst, legacySecond })
            foreach (var file in legacy.Files) file.Original = null;
        legacyFirst = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacyFirst));
        legacySecond = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacySecond));
        foreach (var removeEarlierFirst in new[] { false, true })
        {
            Success(CivilizationPackageManager.Install(legacyFirst, fixture));
            Success(CivilizationPackageManager.Install(legacySecond, fixture));
            Require(Snapshot().Units.Count == 2 && Snapshot().Units.Select(rule => rule.TechnologyId).Distinct().Count() == 2,
                "Legacy pure ability exports reconstruct their baselines and receive distinct installed command identities.");
            var remove = removeEarlierFirst ? legacyFirst : legacySecond;
            var keep = removeEarlierFirst ? legacySecond : legacyFirst;
            Success(CivilizationPackageManager.Uninstall(fixture, remove.PackageId));
            Require(Snapshot().Units.Single().Id == Rules(keep).Single().Id,
                "Either legacy pure ability export can be removed while preserving the other.");
            _ = CivilizationPackageManager.CreateSharePackage(fixture, keep.PackageId);
            VerifyRegistrations(Snapshot());
            Success(CivilizationPackageManager.Uninstall(fixture, keep.PackageId));
            Restored();
        }
        // An older installed export may combine a custom command and ordinary
        // scalar changes in one whole-table snapshot. Its protected originals
        // supply the exact baseline while its portable export remains intact.
        var legacyMixedProject = firstProject.Clone();
        legacyMixedProject.Name = "Legacy recon with health";
        var originalMixedHealth = BinaryPrimitives.ReadUInt32LittleEndian(originals[objectsPath].AsSpan(4 + 749 * 1936 + 120));
        legacyMixedProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single()
            .Properties["unit.health"] = checked(originalMixedHealth + 17).ToString(CultureInfo.InvariantCulture);
        var legacyMixed = Package(legacyMixedProject);
        foreach (var file in legacyMixed.Files) file.Original = null;
        legacyMixed = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(legacyMixed));
        var portableLegacyMixed = CivilizationInstallerPackageCodec.Encode(legacyMixed);
        Success(CivilizationPackageManager.Install(legacyMixed, fixture));
        Require(BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(fixture, objectsPath)).AsSpan(4 + 749 * 1936 + 120)) ==
            originalMixedHealth + 17, "A retained whole-table ability export installs its ordinary health override.");
        Success(CivilizationPackageManager.Install(second, fixture));
        Require(Snapshot().Units.Select(rule => rule.Id).ToHashSet().SetEquals([firstRule.Id, secondRule.Id]),
            "A new ability can coexist with an older installed whole-table ability and scalar override.");
        Require(CivilizationInstallerPackageCodec.Encode(CivilizationPackageManager.CreateSharePackage(fixture, legacyMixed.PackageId))
            .AsSpan().SequenceEqual(portableLegacyMixed),
            "Recovering an installed legacy baseline from protected originals leaves its portable share export byte for byte intact.");
        Success(CivilizationPackageManager.Uninstall(fixture, legacyMixed.PackageId));
        Require(Snapshot().Units.Single().Id == secondRule.Id &&
            BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(fixture, objectsPath)).AsSpan(4 + 749 * 1936 + 120)) == originalMixedHealth,
            "Removing an older ability and scalar export restores its owned health field while preserving the newer ability.");
        VerifyRegistrations(Snapshot());
        _ = CivilizationPackageManager.CreateSharePackage(fixture, second.PackageId);
        Success(CivilizationPackageManager.Uninstall(fixture, second.PackageId));
        Restored();
        Console.WriteLine("RUNTIME COEXISTENCE: exports authored with earlier edits and legacy pure/mixed exports remain independent, shareable and removable.");
        VerifyProtectedBackups(first);
        VerifyPortableManifest(first, second);

        var grantProject = CivilizationEditProject.CreateDefault();
        grantProject.Name = "Independent recipient grants";
        var emitter = new CivilizationEntityDraft { Name = source.Name, Category = source.Category, SourceObjectId = source.SourceObjectId };
        CivilizationUnitEffects.Write(emitter, [new()
        {
            Name = "Recipient recon", Trigger = CivilizationUnitEffectTrigger.ProximityAbility,
            TargetObjectId = 777, TargetObjectIds = [777, 878], Amount = 2,
            ActivationMode = CivilizationUnitEffectActivationMode.Toggle, DurationSeconds = 0,
            ButtonCaption = "Recipient recon", ButtonIconId = 1350, ButtonSlot = slots[0]
        }]);
        grantProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(emitter);
        var grant = Package(grantProject);
        Require(Rules(grant).Count == 2 && Rules(grant).Any(rule => rule.TechnologyId == firstRule.TechnologyId),
            "Recipient grant exports include multiple commands and collide with an independent ability.");
        Success(CivilizationPackageManager.Install(first, fixture));
        Success(CivilizationPackageManager.Install(grant, fixture));
        var granted = Snapshot().Units.Where(rule => rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility).ToArray();
        Require(granted.Length == 2 && Snapshot().Units.Select(rule => rule.TechnologyId).Distinct().Count() == 3 &&
            granted.Select(rule => rule.GrantFamilyTechnologyId).Distinct().Count() == 1 &&
            granted.Any(rule => rule.TechnologyId == granted[0].GrantFamilyTechnologyId),
            "Rebasing recipient grants preserves a family identity naming one of the family's remapped commands.");
        VerifyRegistrations(Snapshot());
        var sharedGrant = CivilizationPackageManager.CreateSharePackage(fixture, grant.PackageId);
        _ = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(sharedGrant));
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, grant.PackageId, false);
        Require(Snapshot().Units.Count == 3 && Snapshot().Units.Count(rule => !rule.Enabled) == 2,
            "Disabling a remapped recipient family preserves every inactive recipient binding.");
        Success(CivilizationPackageManager.Uninstall(fixture, first.PackageId));
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, grant.PackageId, true);
        VerifyRegistrations(Snapshot());
        Success(CivilizationPackageManager.Uninstall(fixture, grant.PackageId));
        Restored();

        var auraProject = CivilizationEditProject.CreateDefault();
        auraProject.Name = "Independent aura";
        var auraSource = new CivilizationEntityDraft { Name = source.Name, Category = source.Category, SourceObjectId = source.SourceObjectId };
        CivilizationUnitEffects.Write(auraSource, [new() { Name = "Independent aura", TargetObjectId = 777, Amount = 2 }]);
        auraProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(auraSource);
        var aura = Package(auraProject);
        var technology = CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(originals[objectsPath],
            CivilizationTechnologyBindings.ReadRecords(originals[technologiesPath]), 10, 0, 100).First(CivilizationTechnologyBindings.IsResearchRecord);
        var researchProject = CivilizationEditProject.CreateDefault();
        researchProject.Name = "Independent research";
        researchProject.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
        {
            Name = technology.Identifier, Category = CivilizationCategory.Technologies, SourceTechnologyId = technology.TechId,
            Properties = new() { ["technology.bonusFood"] = "23" }
        });
        var research = Package(researchProject);
        var health = BinaryPrimitives.ReadUInt32LittleEndian(originals[objectsPath].AsSpan(4 + 749 * 1936 + 120));
        CivilizationEditProject HealthProject(string name, uint increment)
        {
            var project = CivilizationEditProject.CreateDefault();
            project.Name = name;
            project.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(new()
            {
                Name = source.Name, Category = source.Category, SourceObjectId = source.SourceObjectId,
                Properties = new() { ["unit.health"] = checked(health + increment).ToString(CultureInfo.InvariantCulture) }
            });
            return project;
        }
        var scalar = Package(HealthProject("Independent health", 13));
        // A group-nine custom command shifts the later epoch records even though
        // ordinary playable research stays in the preceding groups. Exercise an
        // ordinary verified field on that shifted record using its stable ID.
        var shiftedRecord = CivilizationTechnologyBindings.ReadRecords(originals[technologiesPath]).Single(record => record.IsEpochDefinition && record.TechId == 10);
        var shiftedCost = BinaryPrimitives.ReadUInt32LittleEndian(originals[technologiesPath].AsSpan(shiftedRecord.Offset + 124));
        AddTechnologyCostPatch(scalar, originals[technologiesPath], shiftedRecord, checked(shiftedCost + 17));
        scalar = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(scalar));
        var conflict = Package(HealthProject("Conflicting health", 27));
        Success(CivilizationPackageManager.Install(first, fixture));
        Success(CivilizationPackageManager.Install(aura, fixture));
        Success(CivilizationPackageManager.Install(research, fixture));
        Success(CivilizationPackageManager.Install(scalar, fixture));
        Require(Snapshot().Research.Single().Amount == 23 && Snapshot().Units.Count == 2,
            "Ability, aura, research and ordinary scalar packages coexist.");
        var mergedTechnologies = File.ReadAllBytes(Path.Combine(fixture, technologiesPath));
        var shiftedInstalled = CivilizationTechnologyBindings.ReadRecords(mergedTechnologies).Single(record => record.TechId == shiftedRecord.TechId);
        Require(shiftedInstalled.Offset != shiftedRecord.Offset &&
            BinaryPrimitives.ReadUInt32LittleEndian(mergedTechnologies.AsSpan(shiftedInstalled.Offset + 124)) == shiftedCost + 17,
            "Mapped technology fields follow their stable record identity after an earlier group's command additions shift their offsets.");
        _ = CivilizationPackageManager.CreateSharePackage(fixture, scalar.PackageId);
        var installedTables = new[] { objectsPath, technologiesPath }.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
        Require(CivilizationPackageManager.Review(conflict, fixture).Count > 0 && !CivilizationPackageManager.Install(conflict, fixture).Success,
            "Packages that edit the same ordinary field still conflict.");
        foreach (var table in installedTables)
            Require(File.ReadAllBytes(Path.Combine(fixture, table.Key)).AsSpan().SequenceEqual(table.Value),
                "Rejecting a real field conflict leaves the installed tables intact.");

        var currentTechnologies = installedTables[technologiesPath];
        var activeTechnologyId = Snapshot().Units.Single(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated).TechnologyId;
        var activeCommand = CivilizationTechnologyBindings.ReadRecords(currentTechnologies).Single(record => record.TechId == activeTechnologyId);
        var commandConflictProject = CivilizationEditProject.CreateDefault();
        commandConflictProject.Name = "Conflicting ability command field";
        var commandConflict = new CivilizationInstallerPackage
        {
            ProjectName = commandConflictProject.Name, ProjectJson = commandConflictProject.Serialize(),
            Changes = [new("American", "Custom command", "technology.woodCost", "Wood cost", "0", "1", "Verification", technologiesPath)],
            Files = [new()
            {
                RelativePath = technologiesPath, Original = currentTechnologies.ToArray(), ExpectedLength = currentTechnologies.Length,
                StructuralHash = CivilizationInstallerPackage.StructuralHash(technologiesPath, currentTechnologies)
            }]
        };
        AddTechnologyCostPatch(commandConflict, currentTechnologies, activeCommand, 1);
        commandConflict = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(commandConflict));
        Require(CivilizationPackageManager.Review(commandConflict, fixture).Count > 0 &&
            !CivilizationPackageManager.Install(commandConflict, fixture).Success &&
            File.ReadAllBytes(Path.Combine(fixture, technologiesPath)).AsSpan().SequenceEqual(currentTechnologies),
            "An ordinary scalar package cannot overwrite another package's exact owned command record.");
        var altered = currentTechnologies.ToArray();
        altered[activeCommand.Offset + 168] ^= 1;
        File.WriteAllBytes(Path.Combine(fixture, technologiesPath), altered);
        Require(!CivilizationPackageManager.Uninstall(fixture, first.PackageId).Success &&
            File.ReadAllBytes(Path.Combine(fixture, technologiesPath)).AsSpan().SequenceEqual(altered),
            "External changes to an owned ability record block removal without overwriting them.");
        File.WriteAllBytes(Path.Combine(fixture, technologiesPath), currentTechnologies);
        Success(CivilizationPackageManager.Uninstall(fixture, first.PackageId));
        Require(Snapshot().Research.Count == 1 && Snapshot().Units.Single().Trigger == CivilizationUnitEffectTrigger.Proximity &&
            BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(fixture, objectsPath)).AsSpan(4 + 749 * 1936 + 120)) == health + 13,
            "Removing an ability preserves unrelated aura, research and scalar edits.");
        var remainingTechnologies = File.ReadAllBytes(Path.Combine(fixture, technologiesPath));
        var shiftedRemaining = CivilizationTechnologyBindings.ReadRecords(remainingTechnologies).Single(record => record.TechId == shiftedRecord.TechId);
        Require(shiftedRemaining.Offset == shiftedRecord.Offset &&
            BinaryPrimitives.ReadUInt32LittleEndian(remainingTechnologies.AsSpan(shiftedRemaining.Offset + 124)) == shiftedCost + 17,
            "Removing an earlier command preserves a later ordinary field while its record returns to the original offset.");
        Success(CivilizationPackageManager.Uninstall(fixture, scalar.PackageId));
        Success(CivilizationPackageManager.Uninstall(fixture, research.PackageId));
        Success(CivilizationPackageManager.Uninstall(fixture, aura.PackageId));
        Restored();

        CivilizationInstallerPackage Package(CivilizationEditProject project) => CivilizationInstallerPackageCodec.Decode(
            CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        CivilizationRuntimeConfiguration Snapshot()
        {
            byte[]? bytes = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, data => bytes = data);
            return CivilizationRuntimeConfiguration.Decode(bytes!);
        }
        void VerifyRegistrations(CivilizationRuntimeConfiguration snapshot)
        {
            var objects = File.ReadAllBytes(Path.Combine(fixture, objectsPath));
            var technologies = File.ReadAllBytes(Path.Combine(fixture, technologiesPath));
            CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(technologies);
            var records = CivilizationTechnologyBindings.ReadRecords(technologies);
            foreach (var rule in snapshot.Units.Where(rule => rule.Trigger != CivilizationUnitEffectTrigger.Proximity))
            {
                var command = records.Single(record => record.TechId == rule.TechnologyId);
                var commandSource = rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility ? rule.TargetObjectId : rule.SourceObjectId;
                var commandCategory = CivilizationUnitEffectBindings.CommandSourceCategory(objects, commandSource);
                Require(command.Identifier == "EDMW ability " + rule.Id.ToString("N") && command.CommandId == 51 && command.EventId == 0 &&
                    command.ProducerObjectIds.SequenceEqual([commandSource]) && CivilizationObjectDetails.ReadActionTechnologyIds(objects, commandSource, commandCategory)
                        .Contains((uint)rule.TechnologyId),
                    "Each aggregated ability retains its matching native command and source action.");
            }
            Require(records.Count(record => record.Identifier.StartsWith("EDMW ability ", StringComparison.Ordinal)) ==
                snapshot.Units.Count(rule => rule.Trigger != CivilizationUnitEffectTrigger.Proximity),
                "Removed packages leave no orphaned native ability records.");
        }
        void Restored()
        {
            Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0 && Snapshot().Count == 0,
                "Removing all packages clears the inventory and plain-game launch configuration.");
            foreach (var original in originals)
                Require(File.ReadAllBytes(Path.Combine(fixture, original.Key)).AsSpan().SequenceEqual(original.Value),
                    "Arbitrary package removal order restores every original fixture byte: " + original.Key);
        }

        void VerifyProtectedBackups(CivilizationInstallerPackage package)
        {
            Success(CivilizationPackageManager.Install(package, fixture));
            var manifestPath = Path.Combine(fixture, ".edmw-civilization-packages", "installed", package.PackageId + ".json");
            var manifest = File.ReadAllBytes(manifestPath);
            var installedFiles = originals.Keys.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
            var indexPath = Path.Combine(fixture, ".edmw-civilization-packages", "backups", package.PackageId + ".json");
            var index = JsonNode.Parse(File.ReadAllBytes(indexPath))!;
            foreach (var tablePath in new[] { objectsPath, technologiesPath })
            {
                var entry = index["files"]!.AsArray().Single(node => node!["relativePath"]!.GetValue<string>() == tablePath)!;
                var backupPath = Path.Combine(index["directory"]!.GetValue<string>(), entry["backupName"]!.GetValue<string>());
                Require(Path.GetFullPath(backupPath).StartsWith(Path.GetFullPath(fixture).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "The protected backup fixture remains inside its isolated game directory.");
                var protectedBytes = File.ReadAllBytes(backupPath);
                foreach (var missing in new[] { false, true })
                {
                    try
                    {
                        if (missing) File.Delete(backupPath);
                        else
                        {
                            var corrupt = protectedBytes.ToArray();
                            corrupt[0] ^= 1;
                            File.WriteAllBytes(backupPath, corrupt);
                        }
                        Require(!CivilizationPackageManager.Uninstall(fixture, package.PackageId).Success,
                            "A missing or corrupt protected composition backup blocks uninstall.");
                        Reject(() => CivilizationPackageManager.CreateSharePackage(fixture, package.PackageId),
                            "A missing or corrupt protected composition backup blocks installed sharing.");
                        Require(File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifest),
                            "Rejected backup recovery leaves the installed manifest intact.");
                        foreach (var installedFile in installedFiles)
                            Require(File.ReadAllBytes(Path.Combine(fixture, installedFile.Key)).AsSpan().SequenceEqual(installedFile.Value),
                                "A rejected uninstall with damaged protected originals never changes live game files.");
                    }
                    finally { File.WriteAllBytes(backupPath, protectedBytes); }
                }
            }
            _ = CivilizationPackageManager.CreateSharePackage(fixture, package.PackageId);
            Success(CivilizationPackageManager.Uninstall(fixture, package.PackageId));
            Restored();
        }

        void VerifyPortableManifest(CivilizationInstallerPackage installedPackage, CivilizationInstallerPackage differentPackage)
        {
            Success(CivilizationPackageManager.Install(installedPackage, fixture));
            var manifestPath = Path.Combine(fixture, ".edmw-civilization-packages", "installed", installedPackage.PackageId + ".json");
            var originalManifest = File.ReadAllBytes(manifestPath);
            var installedFiles = originals.Keys.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
            try
            {
                var changed = JsonNode.Parse(originalManifest)!;
                var differentExport = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(differentPackage));
                // Keep the package identity valid: correspondence with installed
                // registrations must be checked in addition to basic validation.
                differentExport.PackageId = installedPackage.PackageId;
                changed["exportPackage"] = JsonSerializer.SerializeToNode(differentExport, CivilizationInstallerPackageCodec.JsonOptions);
                var alteredManifest = JsonSerializer.SerializeToUtf8Bytes(changed, CivilizationInstallerPackageCodec.JsonOptions);
                File.WriteAllBytes(manifestPath, alteredManifest);
                Reject(() => CivilizationPackageManager.CreateSharePackage(fixture, installedPackage.PackageId),
                    "A valid portable export with mismatched authored effects cannot replace the installed package's saved export.");
                Require(!CivilizationPackageManager.InstallOrVerifyInstalled(installedPackage, fixture).Success,
                    "A mismatched portable export cannot receive an idempotent installed acknowledgement.");
                Require(File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(alteredManifest),
                    "Rejected mismatched-export checks preserve the manifest for recovery.");
                foreach (var installedFile in installedFiles)
                    Require(File.ReadAllBytes(Path.Combine(fixture, installedFile.Key)).AsSpan().SequenceEqual(installedFile.Value),
                        "Rejected portable-export mismatch never changes live game files.");
            }
            finally { File.WriteAllBytes(manifestPath, originalManifest); }
            Success(CivilizationPackageManager.Uninstall(fixture, installedPackage.PackageId));
            Restored();
        }

        void AddTechnologyCostPatch(CivilizationInstallerPackage package, byte[] original, CivilizationTechnologyRecord record, uint value)
        {
            var file = package.Files.Single(entry => CivilizationInstallerPackage.IsTechnologyPath(entry.RelativePath));
            var offset = record.Offset + 124;
            var replacement = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(replacement, value);
            file.Patches.Add(new()
            {
                Offset = offset, Length = 4, Expected = original.AsSpan(offset, 4).ToArray(), Replacement = replacement,
                IdentityOffset = record.Offset + 104, IdentityBytes = original.AsSpan(record.Offset + 104, 4).ToArray(),
                SemanticTarget = $"{technologiesPath.ToLowerInvariant()}:{offset}:4"
            });
        }
    }

    private static IReadOnlyList<CivilizationRuntimeUnitRule> Rules(CivilizationInstallerPackage package) =>
        package.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))
            .SelectMany(file => CivilizationRuntimeConfiguration.Decode(file.Replacement!).Units).OrderBy(rule => rule.Id).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Success(CivilizationInstallResult result) => Require(result.Success, result.Message);

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
}
