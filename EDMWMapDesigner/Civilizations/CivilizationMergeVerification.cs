using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Checks reversible installed collections and their portable envelopes in an isolated game fixture.</summary>
internal static class CivilizationMergeVerification
{
    internal static void RunIsolated(string source, string output, bool runtimeOnly = false)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Merge verification output must be outside the source game installation.");
        string[] dependencies = ["Data\\db\\dbobjects.dat", "Data\\db\\dbtechtree.dat", "Data\\db\\dbphoenixcivtable.dat",
            "Data\\db\\dbbuttons.dat", "Data\\db\\dbevents.dat", "Data\\db\\dbeffects.dat", "Data\\db\\dbgraphics.dat", "Empires_DMW.exe"];
        var sourceHashes = dependencies.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(Path.Combine(source, path))));
        var fixture = Path.Combine(output, "merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Isolated merge verification. No live game installation is changed.\n");
        foreach (var relative in dependencies)
        {
            var target = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        Console.WriteLine("CIVILIZATION MERGE: isolated fixture prepared; verifying custom names, sharing and reversible grouping.");
        try
        {
            if (runtimeOnly) CivilizationRuntimeCoexistenceVerification.Run(fixture);
            else
            {
                Run(fixture);
                CivilizationMergedRecoveryVerification.Run(fixture);
            }
        }
        finally
        {
            foreach (var original in sourceHashes)
                Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, original.Key))).AsSpan().SequenceEqual(original.Value),
                    "Merge verification changed a source game file: " + original.Key);
        }
        if (runtimeOnly)
        {
            File.WriteAllText(Path.Combine(output, "runtime-coexistence-result.txt"), "PASS: concurrent abilities, command rebasing, legacy installs, shared recipient review, toggles, selective uninstall and exact restoration.\n");
            Console.WriteLine("RUNTIME COEXISTENCE PASS: independent, cumulative and legacy edits, sharing, toggles, backup integrity and exact restoration.");
            return;
        }
        File.WriteAllText(Path.Combine(output, "civilization-merges-result.txt"),
            "PASS: custom names, reversible installed collections, portable format-2 codec and LAN review, recipient installation/unmerge, " +
            "aggregate toggles, flat constituent identities, grouped uninstall, rejected invalid envelopes and selections, " +
            "conflict rollback preserving preexisting edits, exact game file restoration and unchanged source installation.\n");
    }

    internal static void Run(string fixture)
    {
        const string objectsPath = "Data\\db\\dbobjects.dat";
        const string technologiesPath = "Data\\db\\dbtechtree.dat";
        string[] gameFiles = [objectsPath, technologiesPath, "Data\\db\\dbphoenixcivtable.dat",
            "Data\\db\\dbbuttons.dat", "Data\\db\\dbevents.dat", "Empires_DMW.exe"];
        var originals = ReadGameFiles();
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0,
            "Merge verification starts with an isolated clean installation.");
        var source = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var slot = CivilizationUnitEffectBindings.AvailableButtonSlots(originals[objectsPath], originals[technologiesPath],
            originals["Data\\db\\dbbuttons.dat"], source, 11).First();
        var abilityProject = CivilizationEditProject.CreateDefault();
        abilityProject.Name = "Merge fixture recon";
        CivilizationUnitEffects.Write(source, [new()
        {
            Name = "Merge fixture recon", Trigger = CivilizationUnitEffectTrigger.Activated, TargetObjectId = 749,
            ActivationMode = CivilizationUnitEffectActivationMode.Toggle, ButtonCaption = "Recon",
            ButtonIconId = 1350, ButtonSlot = slot, Amount = 3
        }]);
        abilityProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(source);
        var ability = Package(abilityProject);
        var technology = CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(originals[objectsPath],
            CivilizationTechnologyBindings.ReadRecords(originals[technologiesPath]), 10, 0, 100)
            .First(CivilizationTechnologyBindings.IsResearchRecord);
        var researchProject = CivilizationEditProject.CreateDefault();
        researchProject.Name = "Merge fixture research";
        researchProject.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
        {
            Name = technology.Identifier, Category = CivilizationCategory.Technologies, SourceTechnologyId = technology.TechId,
            Properties = new() { ["technology.bonusFood"] = "17" }
        });
        var research = Package(researchProject);
        var auraProject = CivilizationEditProject.CreateDefault();
        auraProject.Name = "Merge fixture aura";
        var emitter = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        CivilizationUnitEffects.Write(emitter, [new() { Name = "Merge fixture aura", TargetObjectId = 777, Amount = 2 }]);
        auraProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(emitter);
        var aura = Package(auraProject);
        var inconsistent = Envelope("Inconsistent collection", ability, research);
        inconsistent.Changes.RemoveAt(0);
        Reject(() => CivilizationInstallerPackageCodec.Encode(inconsistent), "A collection cannot omit a constituent's reviewed changes.");
        var recursive = Envelope("Recursive collection", ability, research);
        recursive.Components[0] = recursive;
        Reject(() => CivilizationInstallerPackageCodec.Encode(recursive), "Recursive collections are rejected before serialization.");
        var nested = Envelope("Nested collection", Envelope("Inner collection", ability, research), aura);
        Reject(() => CivilizationInstallerPackageCodec.Encode(nested), "Portable collections require flattened constituent packages.");
        var duplicate = Envelope("Duplicate collection", ability, ability);
        Reject(() => CivilizationInstallerPackageCodec.Encode(duplicate), "Portable collections cannot repeat a constituent identity.");

        Success(CivilizationPackageManager.Install(ability, fixture));
        Success(CivilizationPackageManager.Install(research, fixture));
        var beforeMerge = ReadGameFiles();
        var beforeRules = SnapshotBytes();
        var individualAbility = CivilizationInstallerPackageCodec.Encode(CivilizationPackageManager.CreateSharePackage(fixture, ability.PackageId));
        var individualResearch = CivilizationInstallerPackageCodec.Encode(CivilizationPackageManager.CreateSharePackage(fixture, research.PackageId));
        RejectMerge([ability.PackageId], "Single edit");
        RejectMerge([ability.PackageId, ability.PackageId], "Duplicate edit");
        RejectMerge([ability.PackageId, research.PackageId], "  ");
        RejectMerge([ability.PackageId, "legacy"], "Legacy edit");
        Unchanged(beforeMerge, "Rejected merge leaves all installed game bytes unchanged.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 2,
            "Rejected merge preserves both individual inventory entries.");

        Success(CivilizationPackageManager.MergeInstalled(fixture, [ability.PackageId, research.PackageId], "  Desert Alliance  "));
        var merged = CivilizationPackageManager.ReadInstalled(fixture).Single();
        Require(merged.ProjectName == "Desert Alliance" && merged.ComponentCount == 2 &&
            merged.PackageId != ability.PackageId && merged.PackageId != research.PackageId,
            "A named merged entry replaces its two constituent Home entries.");
        Unchanged(beforeMerge, "Merging preserves installed native ability tables, sidecar files and other game bytes.");
        Require(SnapshotBytes().AsSpan().SequenceEqual(beforeRules), "Merging preserves the complete runtime launch configuration.");
        var shared = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(
            CivilizationPackageManager.CreateSharePackage(fixture, merged.PackageId)));
        Require(shared.FormatVersion == 2 && shared.PackageId == merged.PackageId && shared.ProjectName == "Desert Alliance" &&
            shared.Components.Count == 2 && shared.Components.Select(component => component.PackageId).ToHashSet()
                .SetEquals([ability.PackageId, research.PackageId]),
            "The merged portable envelope retains its custom name, stable collection identity and original component identities.");
        Require(shared.Changes.Count == ability.Changes.Count + research.Changes.Count,
            "The merged share includes every constituent change for recipient review.");
        Require(CivilizationInstallerPackageCodec.Encode(shared.Components.Single(component => component.PackageId == ability.PackageId))
                .AsSpan().SequenceEqual(individualAbility) &&
            CivilizationInstallerPackageCodec.Encode(shared.Components.Single(component => component.PackageId == research.PackageId))
                .AsSpan().SequenceEqual(individualResearch),
            "Merging retains each original independently shareable export byte for byte.");
        Require(CivilizationPackageManager.CreateSharePackage(fixture, merged.PackageId).PackageId == shared.PackageId,
            "Repeated sharing uses the same merged identity.");
        var groupCollision = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(aura));
        groupCollision.PackageId = merged.PackageId;
        Require(CivilizationPackageManager.Review(groupCollision, fixture).Count > 0 &&
            !CivilizationPackageManager.Install(groupCollision, fixture).Success,
            "An individual package cannot reuse an installed merged entry's identity.");
        Unchanged(beforeMerge, "Rejecting an individual package with a merged identity changes no game bytes.");
        Require(ReadGameFiles().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(beforeMerge.Keys) &&
            CivilizationPackageManager.ReadInstalled(fixture).Single().PackageId == merged.PackageId &&
            SnapshotBytes().AsSpan().SequenceEqual(beforeRules),
            "Rejected merged-identity collision preserves sidecar inventory, the group entry and runtime rules.");

        var misleadingSummary = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(shared));
        var summaryProject = CivilizationEditProject.Deserialize(misleadingSummary.ProjectJson);
        summaryProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(new()
        {
            Name = "Unedited summary entity", Category = CivilizationCategory.Units, SourceObjectId = 749
        });
        Require(summaryProject.GetChanges().Count == 0,
            "An unedited source-backed summary entity has no authored changes and exercises the strict summary schema.");
        misleadingSummary.ProjectJson = summaryProject.Serialize();
        Reject(() => CivilizationInstallerPackageCodec.Encode(misleadingSummary),
            "Merged summaries reject entity content even when GetChanges reports no edits.");

        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, merged.PackageId, false);
        var disabled = Snapshot();
        Require(disabled.Research.Count == 0 && disabled.Units.Count == 1 && !disabled.Units.Single().Enabled,
            "Disabling a merged edit disables every member while retaining its inactive native ability binding.");
        Require(!CivilizationPackageManager.ReadInstalled(fixture).Single().RuntimeEffectsEnabled,
            "The merged runtime toggle state persists in Home inventory.");
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, merged.PackageId, true);
        Require(SnapshotBytes().AsSpan().SequenceEqual(beforeRules), "Re-enabling a merged edit restores both members without changing command identities.");
        Success(CivilizationPackageManager.Unmerge(fixture, merged.PackageId));
        Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId).ToHashSet()
            .SetEquals([ability.PackageId, research.PackageId]), "Unmerge restores the original independent Home entries.");
        Unchanged(beforeMerge, "Unmerge leaves all current game values unchanged.");
        Require(SnapshotBytes().AsSpan().SequenceEqual(beforeRules), "Unmerge preserves the complete runtime launch configuration.");
        Success(CivilizationPackageManager.Uninstall(fixture, ability.PackageId));
        Success(CivilizationPackageManager.Uninstall(fixture, research.PackageId));
        Restored();
        Console.WriteLine("CIVILIZATION MERGE: named local merge, sharing, aggregate toggles and unmerge preserve all game bytes.");

        Require(CivilizationPackageManager.Review(shared, fixture).Count == 0,
            "A merged exported package can be reviewed against a clean recipient installation.");
        var reviewedShare = CivilizationLanPackageReview.Prepare(shared, fixture);
        Require(reviewedShare.Issues.Count == 0 && reviewedShare.Package is { Components.Count: 2, ProjectName: "Desert Alliance" },
            "LAN recipient review rebuilds all merged component edits and preserves the collection name: " + string.Join("; ", reviewedShare.Issues));
        const string forgedText = "FORGED MERGED CHANGE METADATA";
        var forgedDescriptions = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(shared));
        foreach (var component in forgedDescriptions.Components)
            component.Changes = component.Changes.Select(change => change with
            {
                Label = forgedText + " label", After = forgedText + " after"
            }).ToList();
        forgedDescriptions.Changes = forgedDescriptions.Components.SelectMany(component => component.Changes).ToList();
        forgedDescriptions = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(forgedDescriptions));
        var canonicalDescriptions = CivilizationLanPackageReview.Prepare(forgedDescriptions, fixture);
        Require(canonicalDescriptions.Issues.Count == 0 && canonicalDescriptions.Package is { } canonical &&
            canonical.Changes.SequenceEqual(reviewedShare.Package!.Changes) &&
            canonical.Components.All(component => component.Changes.All(change => !change.Description.Contains(forgedText, StringComparison.Ordinal))) &&
            canonical.Changes.All(change => !change.Description.Contains(forgedText, StringComparison.Ordinal)),
            "LAN review rebuilds every child and aggregate description from actual edits instead of trusting matching forged metadata: " +
                string.Join("; ", canonicalDescriptions.Issues));
        Unchanged(originals, "Reviewing forged merged descriptions changes no native game data.");

        var incomingAbilityProject = abilityProject.Clone();
        incomingAbilityProject.Name = "Merge fixture incoming recon";
        var incomingAbilitySource = incomingAbilityProject.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Single();
        incomingAbilitySource.Id = Guid.NewGuid().ToString("D");
        var incomingSlot = CivilizationUnitEffectBindings.AvailableButtonSlots(originals[objectsPath], originals[technologiesPath],
            originals["Data\\db\\dbbuttons.dat"], incomingAbilitySource, 11).Skip(1).First();
        CivilizationUnitEffects.Write(incomingAbilitySource, CivilizationUnitEffects.Read(incomingAbilitySource).Select(effect => effect with
        {
            Id = Guid.NewGuid().ToString("D"), Name = "Incoming recon", ButtonCaption = "Incoming recon", ButtonSlot = incomingSlot
        }));
        var incomingAbility = Package(incomingAbilityProject);
        foreach (var partial in new[]
        {
            (Existing: research, Envelope: shared),
            (Existing: ability, Envelope: Envelope("Partial ability recipient", incomingAbility, ability))
        })
        {
            Success(CivilizationPackageManager.Install(partial.Existing, fixture));
            CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, partial.Existing.PackageId, false);
            var partialBefore = ReadGameFiles();
            var manifestPath = Path.Combine(fixture, ".edmw-civilization-packages", "installed", partial.Existing.PackageId + ".json");
            var retainedManifest = File.ReadAllBytes(manifestPath);
            var partialReview = CivilizationPackageManager.Review(partial.Envelope, fixture);
            Require(partialReview.Count == 0,
                "A partial recipient reviews a new ability followed by an already installed disabled member: " + string.Join("; ", partialReview));
            var partialLan = CivilizationLanPackageReview.Prepare(partial.Envelope, fixture);
            Require(partialLan.Issues.Count == 0 && partialLan.Package is { Components.Count: 2 },
                "LAN review accepts a partial recipient with an existing disabled ability or research member: " + string.Join("; ", partialLan.Issues));
            Require(CivilizationPackageManager.ReadInstalled(fixture).Single() is { RuntimeEffectsEnabled: false } existing &&
                existing.PackageId == partial.Existing.PackageId && File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(retainedManifest),
                "Partial recipient reviews preserve the existing package, protected manifest and disabled runtime state.");
            Unchanged(partialBefore, "Partial recipient reviews change no installed native tables or sidecars.");
            Require(ReadGameFiles().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(partialBefore.Keys),
                "Partial recipient reviews create no new effect sidecars.");
            Success(CivilizationPackageManager.Uninstall(fixture, partial.Existing.PackageId));
            Restored();
        }
        Success(CivilizationPackageManager.Install(shared, fixture));
        Require(CivilizationPackageManager.ReadInstalled(fixture).Single() is { ProjectName: "Desert Alliance", ComponentCount: 2 },
            "Installing a shared merged package preserves its custom Home name and member count.");
        Require(Snapshot().Research.Single().Amount == 17 && Snapshot().Units.Single().Enabled,
            "The recipient launches all merged runtime effects together.");
        Success(CivilizationPackageManager.InstallOrVerifyInstalled(shared, fixture));
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 1,
            "Re-importing the same merged package is idempotent.");
        var recipientBeforeUnmerge = ReadGameFiles();
        Success(CivilizationPackageManager.Unmerge(fixture, shared.PackageId));
        Unchanged(recipientBeforeUnmerge, "A recipient can unmerge the shared package without rewriting game files.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId).ToHashSet()
            .SetEquals([ability.PackageId, research.PackageId]), "Recipient unmerge preserves original component identities.");
        Success(CivilizationPackageManager.Uninstall(fixture, research.PackageId));
        Success(CivilizationPackageManager.Uninstall(fixture, ability.PackageId));
        Restored();
        Console.WriteLine("CIVILIZATION MERGE: shared codec/LAN review, recipient install, idempotent import and recipient unmerge passed.");

        // A later merge flattens an existing collection, retaining a single
        // reversible set rather than hiding another layer of collections.
        Success(CivilizationPackageManager.Install(ability, fixture));
        Success(CivilizationPackageManager.Install(research, fixture));
        Success(CivilizationPackageManager.Install(aura, fixture));
        Success(CivilizationPackageManager.MergeInstalled(fixture, [ability.PackageId, research.PackageId], "First collection"));
        var firstGroup = CivilizationPackageManager.ReadInstalled(fixture).Single(item => item.ComponentCount == 2);
        var beforeNested = ReadGameFiles();
        RejectMerge([ability.PackageId, aura.PackageId], "Overlapping collection");
        Unchanged(beforeNested, "An overlapping hidden-member selection changes no game values.");
        Success(CivilizationPackageManager.MergeInstalled(fixture, [firstGroup.PackageId, aura.PackageId], "Three civ edits"));
        var three = CivilizationPackageManager.ReadInstalled(fixture).Single();
        var threeShare = CivilizationPackageManager.CreateSharePackage(fixture, three.PackageId);
        Require(three.ProjectName == "Three civ edits" && three.ComponentCount == 3 && threeShare.Components.Count == 3 &&
            threeShare.Components.All(component => component.Components.Count == 0),
            "Merging an existing collection flattens and names its three original edits.");
        Unchanged(beforeNested, "Merging an existing collection does not alter gameplay data.");
        Success(CivilizationPackageManager.Uninstall(fixture, three.PackageId));
        Restored();
        Console.WriteLine("CIVILIZATION MERGE: existing collections flatten to original edits and grouped uninstall restores originals.");

        // Failed grouped imports must not leave the first newly installed child
        // behind, and must never roll back a child that existed before import.
        var health = BinaryPrimitives.ReadUInt32LittleEndian(originals[objectsPath].AsSpan(4 + 749 * 1936 + 120));
        CivilizationInstallerPackage HealthPackage(string name, uint increment)
        {
            var project = CivilizationEditProject.CreateDefault();
            project.Name = name;
            project.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(new()
            {
                Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749,
                Properties = new() { ["unit.health"] = checked(health + increment).ToString(CultureInfo.InvariantCulture) }
            });
            return Package(project);
        }
        var healthA = HealthPackage("Merge rollback health A", 5);
        var healthB = HealthPackage("Merge rollback health B", 9);
        var conflicting = Envelope("Conflicting collection", healthA, healthB);
        Require(!CivilizationPackageManager.Install(conflicting, fixture).Success,
            "A merged import with genuinely conflicting constituent fields is rejected.");
        Restored();
        Success(CivilizationPackageManager.Install(healthA, fixture));
        var existingBefore = ReadGameFiles();
        Require(!CivilizationPackageManager.Install(conflicting, fixture).Success,
            "A conflicting collection cannot replace an already installed constituent.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Single().PackageId == healthA.PackageId,
            "Failed merged import preserves a preexisting constituent and its inventory entry.");
        Unchanged(existingBefore, "Failed merged import does not roll back preexisting game changes.");
        Success(CivilizationPackageManager.Uninstall(fixture, healthA.PackageId));
        Restored();
        Console.WriteLine("CIVILIZATION MERGE PASS: custom names, reversible Home grouping, portable collection sharing and recipient unmerge, aggregate toggles, flattening, grouped uninstall, conflict rollback and exact game-file restoration.");

        CivilizationInstallerPackage Package(CivilizationEditProject project) =>
            CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        Dictionary<string, byte[]> ReadGameFiles()
        {
            var sidecars = Path.Combine(fixture, "Data", "EDMWEffects");
            var paths = gameFiles.Concat(Directory.Exists(sidecars)
                ? Directory.EnumerateFiles(sidecars, "*.edmwe").Select(path => Path.GetRelativePath(fixture, path)) : []);
            return paths.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
        }
        CivilizationRuntimeConfiguration Snapshot()
        {
            CivilizationRuntimeConfiguration? result = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => result = CivilizationRuntimeConfiguration.Decode(bytes));
            return result!;
        }
        byte[] SnapshotBytes()
        {
            byte[]? result = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => result = bytes);
            return result!;
        }
        void Unchanged(IReadOnlyDictionary<string, byte[]> expected, string check)
        {
            foreach (var entry in expected)
                Require(File.ReadAllBytes(Path.Combine(fixture, entry.Key)).AsSpan().SequenceEqual(entry.Value), check + " " + entry.Key);
        }
        void Restored()
        {
            Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0, "No merged or constituent inventory remains after removal or rollback.");
            Require(Snapshot().Count == 0, "Removal or rollback leaves no runtime effects.");
            var sidecars = Path.Combine(fixture, "Data", "EDMWEffects");
            Require(!Directory.Exists(sidecars) || !Directory.EnumerateFiles(sidecars, "*.edmwe").Any(),
                "Removal or rollback leaves no orphan runtime effect sidecars.");
            Unchanged(originals, "Final removal restores exact original game bytes.");
        }
        void RejectMerge(IReadOnlyList<string> ids, string name) =>
            Require(!CivilizationPackageManager.MergeInstalled(fixture, ids, name).Success, "Invalid merge selection/name is rejected: " + name);
    }

    private static CivilizationInstallerPackage Envelope(string name, params CivilizationInstallerPackage[] components)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        return new()
        {
            FormatVersion = 2, ProjectName = name, ProjectJson = project.Serialize(),
            Components = components.ToList(), Changes = components.SelectMany(component => component.Changes).ToList()
        };
    }

    private static void Success(CivilizationInstallResult result) => Require(result.Success, result.Message);
    private static void Reject(Action action, string check)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(check);
    }
    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException(check);
    }
}
