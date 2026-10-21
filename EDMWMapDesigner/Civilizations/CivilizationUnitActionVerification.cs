using System.Buffers.Binary;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationUnitActionVerification
{
    internal static void Verify(string fixture, string output)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException(message); }
        var project = CivilizationEditProject.CreateDefault();
        project.Name = "Permanent action outputs";
        var civilization = project.Civilizations.Single(civ => civ.SourceCivilizationId == 12);
        var unit = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        civilization.Entities.Add(unit);
        var transfer = new CivilizationUnitEffectOutput(CivilizationUnitEffectStat.LineOfSight, 0)
            { Kind = CivilizationUnitEffectOutputKind.OwnershipTransfer, Owner = CivilizationUnitEffectOwner.SpecificPlayer, PlayerSlot = 2 };
        var transform = transfer with { Kind = CivilizationUnitEffectOutputKind.Transform, ObjectId = 878, Owner = CivilizationUnitEffectOwner.SourcePlayer, PlayerSlot = 1 };
        var spawn = transform with { Kind = CivilizationUnitEffectOutputKind.Spawn, ObjectId = 777, Count = 3 };
        var effect = new CivilizationUnitEffect { Name = "Reinforcements", TargetObjectId = 749,
            Trigger = CivilizationUnitEffectTrigger.Activated, ActivationMode = CivilizationUnitEffectActivationMode.Instant,
            DurationSeconds = 0, Outputs = [transfer, transform, spawn], ButtonCaption = "Reinforce", ButtonIconId = 1350 };
        CivilizationUnitEffects.Write(unit, [effect]);
        var persisted = CivilizationUnitEffects.Read(unit).Single();
        Require(CivilizationUnitEffects.GetOutputs(persisted).SequenceEqual([transfer, transform, spawn]) &&
            CivilizationUnitEffects.GetStatOutputs(persisted).Count == 0 && persisted.Amount == 0, "Action-only authoring must preserve structured outputs without inventing a stat bonus.");
        var document = project.Serialize();
        Require(CivilizationEditProject.Deserialize(document).Serialize() == document, "Actions persist in saved projects.");
        var history = new CivilizationEditHistory(project);
        history.Change(next => CivilizationUnitEffects.Write(next.Civilizations.Single(civ => civ.SourceCivilizationId == 12).Entities.Single(),
            [effect with { Outputs = [spawn with { Count = 7 }] }]));
        Require(history.Undo() && history.Current.Serialize() == document && history.Redo(), "Action edits preserve undo and redo.");
        var package = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        var file = package.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath));
        var bytes = file.Replacement!;
        var configuration = CivilizationRuntimeConfiguration.Decode(bytes);
        var rule = configuration.Units.Single();
        Require(bytes.AsSpan(0, 8).SequenceEqual("EDMWEF08"u8) && CivilizationRuntimeConfiguration.GetOutputs(rule).Count == 0 &&
            CivilizationRuntimeConfiguration.GetActions(rule).Count == 3 && rule.ActivationMode == CivilizationUnitEffectActivationMode.Instant && rule.DurationMilliseconds == 0,
            "V8 action-only instant ability must retain all three actions and no stat adjustment.");
        var named = CivilizationRuntimeDescriptions.ForLaunch(package, [rule], new Dictionary<int, string> { [777] = "Sherman Tank", [878] = "Commoner" }).Single();
        Require(CivilizationRuntimeConfiguration.GetActions(named)[1].ObjectName == "Commoner" &&
            CivilizationRuntimeConfiguration.GetActions(named)[2].ObjectName == "Sherman Tank", "Action objects use player-facing launch names.");
        var namedBytes = CivilizationRuntimeConfiguration.Encode([], [named]);
        Require(CivilizationRuntimeConfiguration.Equivalent(named, CivilizationRuntimeConfiguration.Decode(namedBytes).Units.Single()), "Named action wire round trip.");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "permanent-actions.edmwe"), namedBytes);
        for (var length = 0; length < namedBytes.Length; length++)
        {
            var truncated = namedBytes[..length];
            Reject(() => CivilizationRuntimeConfiguration.Decode(truncated), "A truncated V8 action must be rejected.");
        }
        var legacyHeader = namedBytes.ToArray(); "EDMWEF07"u8.CopyTo(legacyHeader);
        Reject(() => CivilizationRuntimeConfiguration.Decode(legacyHeader), "Older runtime headers cannot reinterpret action framing.");
        foreach (var invalid in new[] { spawn with { Count = 0 }, spawn with { Count = 65 }, spawn with { ObjectId = -1 },
            transfer with { PlayerSlot = 9 }, transform with { ObjectId = null } })
            Reject(() => CivilizationUnitEffects.Write(unit, [effect with { Outputs = [invalid] }]), "Invalid permanent action settings must be rejected.");
        Reject(() => CivilizationUnitEffects.Write(unit, [effect with { Outputs = [transfer, transfer] }]), "Duplicate ownership actions are ambiguous.");
        Reject(() => CivilizationUnitEffects.Write(unit, [effect with { Outputs = [spawn, new(CivilizationUnitEffectStat.Health, 100)] }]), "Instant stat modifiers are invalid.");
        var outcome = CivilizationPackageManager.Install(package, fixture);
        Require(outcome.Success, outcome.Message);
        byte[]? launched = null;
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launched = value);
        Require(CivilizationRuntimeConfiguration.Equivalent(CivilizationRuntimeConfiguration.Decode(launched!).Units.Single(), rule), "Installed action commands retain their settings at launch.");
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, package.PackageId, false);
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launched = value);
        Require(!CivilizationRuntimeConfiguration.Decode(launched!).Units.Single().Enabled, "Disabled installed actions keep only inert command bindings.");
        var shared = CivilizationPackageManager.CreateSharePackage(fixture, package.PackageId);
        Require(CivilizationInstallerPackageCodec.Encode(shared).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(package)), "Sharing preserves action settings.");
        outcome = CivilizationPackageManager.Uninstall(fixture, package.PackageId);
        Require(outcome.Success, outcome.Message);
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launched = value);
        Require(CivilizationRuntimeConfiguration.Decode(launched!).Count == 0, "Uninstall removes action rules and their native command bindings.");
        var aura = effect with { Trigger = CivilizationUnitEffectTrigger.Proximity, ActivationMode = CivilizationUnitEffectActivationMode.Toggle,
            DurationSeconds = 30, TargetObjectId = 777, TargetObjectIds = [777, 878], TargetPlayers = CivilizationUnitEffectTargetPlayers.Other,
            Outputs = [transfer with { Owner = CivilizationUnitEffectOwner.SourcePlayer, PlayerSlot = 1 }, spawn, new(CivilizationUnitEffectStat.Health, 100)] };
        CivilizationUnitEffects.Write(unit, [aura]);
        var auraPackage = CivilizationInstallerPackage.Create(project, fixture);
        var auraRules = CivilizationRuntimeConfiguration.Decode(auraPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!).Units;
        Require(auraRules.Count == 2 && auraRules.All(rule => rule.TargetPlayers == CivilizationUnitEffectTargetPlayers.Other &&
            CivilizationRuntimeConfiguration.GetActions(rule).Count == 2 && CivilizationRuntimeConfiguration.GetOutputs(rule).Count == 1), "Mixed proximity actions preserve other-player filtering and multiple targets.");
        var delayed = effect with { Name = "Timed transformation grant", Trigger = CivilizationUnitEffectTrigger.ProximityAbility,
            TargetObjectId = 777, TargetObjectIds = [777, 878], ProximityDelaySeconds = 60, MaxConcurrentTargets = 2,
            MaxAffectedTargets = 5, GoldCost = 50, Outputs = [transform], ActivationMode = CivilizationUnitEffectActivationMode.Instant };
        CivilizationUnitEffects.Write(unit, [delayed]);
        var delayedDocument = project.Serialize();
        Require(CivilizationEditProject.Deserialize(delayedDocument).Serialize() == delayedDocument, "Delay, both limits and all costs persist generically in a project.");
        var delayedPackage = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        var delayedBytes = delayedPackage.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Replacement!;
        var delayedRules = CivilizationRuntimeConfiguration.Decode(delayedBytes).Units;
        Require(delayedRules.Count == 2 && delayedRules.All(rule => rule.DelayMilliseconds == 60000 && rule.MaxConcurrentTargets == 2 &&
            rule.MaxAffectedTargets == 5 && rule.GoldCost == 50 && rule.FamilyId == Guid.Parse(delayed.Id) &&
            rule.ActivationMode == CivilizationUnitEffectActivationMode.Instant && CivilizationRuntimeConfiguration.GetActions(rule).Single().ObjectId == 878),
            "Generic multi-target grants share a continuous delay, source-family limits and per-recipient activation cost.");
        File.WriteAllBytes(Path.Combine(output, "delayed-action-grants.edmwe"), delayedBytes);
        outcome = CivilizationPackageManager.Install(delayedPackage, fixture); Require(outcome.Success, outcome.Message);
        CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, value => launched = value);
        Require(CivilizationRuntimeConfiguration.Decode(launched!).Units.All(rule => rule.GoldCost == 50 && rule.DelayMilliseconds == 60000), "Installed delayed grants preserve resource costs at launch.");
        outcome = CivilizationPackageManager.Uninstall(fixture, delayedPackage.PackageId); Require(outcome.Success, outcome.Message);
        foreach (var invalid in new[] { delayed with { ProximityDelaySeconds = -1 }, delayed with { ProximityDelaySeconds = 3601 },
            delayed with { MaxConcurrentTargets = -1 }, delayed with { MaxAffectedTargets = -1 }, delayed with { GoldCost = -1 }, delayed with { WoodCost = 100001 } })
            Reject(() => CivilizationUnitEffects.Write(unit, [invalid]), "Invalid generic delays, limits and resource costs must be rejected.");
        var tampered = bytes.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(tampered.AsSpan(16 + 56), 52);
        Reject(() => CivilizationRuntimeConfiguration.Decode(tampered), "Actions do not increase the stable stat ID catalog.");
        CivilizationBuildingActionVerification.Verify(fixture, output);
        Console.WriteLine("UNIT ACTIONS PASS: ownership/transform/spawn authoring, instant and mixed outputs, V8 framing, malformed settings, project/history, package/share/install/disable/launch/uninstall.");
    }
}
