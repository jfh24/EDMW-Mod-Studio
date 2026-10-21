using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Leaves a real committed child transaction inside a prepared group journal, then exercises recovery.</summary>
internal static class CivilizationMergedRecoveryVerification
{
    internal static void Run(string fixture)
    {
        const string storage = ".edmw-civilization-packages";
        const string objectsPath = "Data\\db\\dbobjects.dat";
        const string technologiesPath = "Data\\db\\dbtechtree.dat";
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0, "Merged recovery requires a clean isolated fixture.");
        string[] gameFiles = [objectsPath, technologiesPath, "Data\\db\\dbphoenixcivtable.dat", "Data\\db\\dbbuttons.dat", "Data\\db\\dbevents.dat", "Empires_DMW.exe"];
        var originals = gameFiles.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
        var ranger = new CivilizationEntityDraft { Name = "Army Ranger", Category = CivilizationCategory.Units, SourceObjectId = 749 };
        var slot = CivilizationUnitEffectBindings.AvailableButtonSlots(originals[objectsPath], originals[technologiesPath],
            originals["Data\\db\\dbbuttons.dat"], ranger, 11).First();
        CivilizationInstallerPackage Package(string name, bool ability)
        {
            var project = CivilizationEditProject.CreateDefault();
            project.Name = name;
            var source = ranger.Clone();
            source.Id = Guid.NewGuid().ToString("D");
            CivilizationUnitEffects.Write(source, [new()
            {
                Name = name, TargetObjectId = ability ? 749 : 777, Amount = ability ? 7 : 3,
                Trigger = ability ? CivilizationUnitEffectTrigger.Activated : CivilizationUnitEffectTrigger.Proximity,
                ActivationMode = CivilizationUnitEffectActivationMode.Toggle, ButtonCaption = name,
                ButtonIconId = 1350, ButtonSlot = slot
            }]);
            project.Civilizations.Single(civ => civ.SourceRowIndex == 11).Entities.Add(source);
            return CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(CivilizationInstallerPackage.Create(project, fixture)));
        }
        var prior = Package("Recovery prior aura", false);
        var child = Package("Recovery child ability", true);
        Success(CivilizationPackageManager.Install(prior, fixture));
        CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, prior.PackageId, false);
        var priorSidecar = prior.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).RelativePath;
        var childSidecar = child.Files.Single(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).RelativePath;
        var priorManifest = Path.Combine(storage, "installed", prior.PackageId + ".json");
        var priorBackup = Path.Combine(storage, "backups", prior.PackageId + ".json");
        var retained = gameFiles.Concat([priorSidecar, priorManifest, priorBackup]).ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));

        foreach (var externalChange in new[] { false, true })
        {
            Require(CivilizationPackageManager.Review(child, fixture).Count == 0, "Recovery child passes read-only preflight.");
            var groupId = Guid.NewGuid().ToString("N");
            var targets = child.Files.Where(file => file.WholeFile || file.Patches.Count > 0).Select(file => file.RelativePath).ToArray();
            var journal = Invoke("BeginMergedTransaction", fixture, "Install", new[] { child.PackageId }, new[] { groupId }, targets)!;
            var effective = CivilizationRuntimePackageRebaser.Rebase(child, File.ReadAllBytes(Path.Combine(fixture, technologiesPath)));
            var pending = Invoke("PrepareInstall", effective, fixture)!;
            Invoke("RegisterMergedChanges", fixture, journal, pending);
            Action commit = () => File.WriteAllBytes(Path.Combine(fixture, storage, "installed", child.PackageId + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Package = effective, ExportPackage = child, InstalledAt = DateTimeOffset.UtcNow, RuntimeEffectsEnabled = true
                }, CivilizationInstallerPackageCodec.JsonOptions));
            var result = (CivilizationInstallResult)Invoke("Transact", fixture, effective, "Install", pending, commit)!;
            Success(result);
            var groupPath = Path.Combine(fixture, storage, "groups", groupId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(groupPath)!);
            File.WriteAllBytes(groupPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Id = groupId, Name = "Interrupted merge fixture", CreatedAt = DateTimeOffset.UtcNow,
                MemberIds = new[] { prior.PackageId, child.PackageId }
            }, CivilizationInstallerPackageCodec.JsonOptions));

            // The child is fully committed; the outer merge has not committed.
            // A process interruption at this point must hide no partial state
            // behind a successful launch, share, or runtime toggle.
            var callback = false;
            Reject(() => CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, _ => callback = true), "Prepared group blocks launch.");
            Require(!callback, "Prepared group invokes no launch callback.");
            Reject(() => CivilizationPackageManager.CreateSharePackage(fixture, prior.PackageId), "Prepared group blocks sharing a preexisting member.");
            Reject(() => CivilizationPackageManager.CreateSharePackage(fixture, groupId), "Prepared group blocks sharing its incomplete aggregate.");
            Reject(() => CivilizationPackageManager.SetRuntimeEffectsEnabled(fixture, prior.PackageId, true), "Prepared group blocks runtime toggles.");
            Require(CivilizationPackageManager.ReadInstalled(fixture).All(item => item.Status.Contains("recovery", StringComparison.OrdinalIgnoreCase)),
                "Partial group inventory explicitly requires recovery.");

            if (externalChange)
            {
                var committed = File.ReadAllBytes(Path.Combine(fixture, objectsPath));
                var altered = committed.ToArray();
                altered[4 + 749 * 1936 + 120] ^= 1;
                File.WriteAllBytes(Path.Combine(fixture, objectsPath), altered);
                var current = targets.Concat([priorManifest, priorBackup,
                    Path.Combine(storage, "installed", child.PackageId + ".json"),
                    Path.Combine(storage, "backups", child.PackageId + ".json"),
                    Path.Combine(storage, "groups", groupId + ".json")]).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(fixture, path)));
                var refused = CivilizationPackageManager.Recover(fixture);
                Require(!refused.Success && refused.Message.Contains("outside", StringComparison.OrdinalIgnoreCase),
                    "Unknown external game changes refuse interrupted group recovery.");
                foreach (var entry in current)
                    Require(File.ReadAllBytes(Path.Combine(fixture, entry.Key)).AsSpan().SequenceEqual(entry.Value),
                        "Refused recovery checks every game target before restoring any game file or protected metadata.");
                Reject(() => CivilizationPackageManager.CreateSharePackage(fixture, prior.PackageId), "Refused recovery leaves the merge guard active.");
                File.WriteAllBytes(Path.Combine(fixture, objectsPath), committed);
            }
            Success(CivilizationPackageManager.Recover(fixture));
            var inventory = CivilizationPackageManager.ReadInstalled(fixture);
            Require(inventory.Count == 1 && inventory.Single().PackageId == prior.PackageId && !inventory.Single().RuntimeEffectsEnabled,
                "Recovery retains the preexisting package and its disabled state.");
            foreach (var entry in retained)
                Require(File.ReadAllBytes(Path.Combine(fixture, entry.Key)).AsSpan().SequenceEqual(entry.Value),
                    "Recovery preserves original native files and preexisting sidecar, manifest and protected backup index exactly.");
            Require(!File.Exists(Path.Combine(fixture, childSidecar)) && !File.Exists(Path.Combine(fixture, storage, "installed", child.PackageId + ".json")) &&
                !File.Exists(Path.Combine(fixture, storage, "backups", child.PackageId + ".json")) && !File.Exists(groupPath),
                "Recovery removes newly committed child files, its installation metadata and incomplete group entry.");
            Require(CivilizationInstallerPackageCodec.Encode(CivilizationPackageManager.CreateSharePackage(fixture, prior.PackageId)).AsSpan()
                .SequenceEqual(CivilizationInstallerPackageCodec.Encode(prior)), "The retained package remains shareable after outer rollback.");
            byte[]? snapshot = null;
            CivilizationPackageManager.WithRuntimeEffectsForLaunch(fixture, bytes => snapshot = bytes);
            Require(CivilizationRuntimeConfiguration.Decode(snapshot!).Count == 0,
                "Recovered launch snapshot contains neither the removed child nor the disabled preexisting aura.");
            Success(CivilizationPackageManager.Recover(fixture));
        }
        Success(CivilizationPackageManager.Uninstall(fixture, prior.PackageId));
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0, "Recovery fixture removes its retained installation.");
        foreach (var entry in originals)
            Require(File.ReadAllBytes(Path.Combine(fixture, entry.Key)).AsSpan().SequenceEqual(entry.Value),
                "Recovery fixture leaves every original game byte intact.");
    }

    private static object? Invoke(string name, params object?[] arguments)
    {
        var method = typeof(CivilizationPackageManager).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Recovery fixture helper is unavailable: " + name);
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Success(CivilizationInstallResult result) => Require(result.Success, result.Message);
}
