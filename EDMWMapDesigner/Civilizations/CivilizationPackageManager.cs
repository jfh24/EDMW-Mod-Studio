using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationInstalledPackage(string PackageId, string ProjectName,
    DateTimeOffset InstalledAt, IReadOnlyList<CivilizationInstallChange> Changes, string Status = "Installed");

/// <summary>
/// Owns semantic fields, never whole database snapshots. Independent packages
/// may coexist and be removed in any order; overlapping fields/models conflict.
/// </summary>
public static class CivilizationPackageManager
{
    private const string Storage = ".edmw-civilization-packages";
    private const int MaximumFileBytes = 32 * 1024 * 1024;

    public static IReadOnlyList<CivilizationInstalledPackage> ReadInstalled(string gamePath)
    {
        var root = Root(gamePath);
        var installed = LoadInstalled(root).Select(manifest => new CivilizationInstalledPackage(
            manifest.Package.PackageId, manifest.Package.ProjectName, manifest.InstalledAt,
            manifest.Package.Changes.AsReadOnly())).ToList();
        var legacy = Path.Combine(root, ".edmw-civilization-editor", "current.json");
        if (File.Exists(legacy))
        {
            var manifest = JsonSerializer.Deserialize<CivilizationInstallationManifest>(ReadBounded(legacy),
                CivilizationInstallerJsonContext.Default.CivilizationInstallationManifest)
                ?? throw new InvalidDataException("The earlier installation manifest is empty.");
            installed.Add(new("legacy", manifest.ProjectName, manifest.CreatedUtc, [], manifest.Status));
        }
        foreach (var transaction in LoadTransactions(root))
            if (transaction.State != "Complete")
            {
                var index = installed.FindIndex(entry => entry.PackageId == transaction.Package.PackageId);
                const string status = "Interrupted package transaction — recovery required";
                if (index >= 0) installed[index] = installed[index] with { Status = status };
                else installed.Add(new(transaction.Package.PackageId, transaction.Package.ProjectName, transaction.CreatedAt,
                    transaction.Package.Changes.AsReadOnly(), status));
            }
        return installed.OrderByDescending(entry => entry.InstalledAt).ToArray();
    }

    public static CivilizationInstallResult Recover(string gamePath)
    {
        try
        {
            var root = Root(gamePath);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            var recovered = RecoverTransactions(root);
            return new(true, recovered.Count == 0 ? "There are no interrupted transactions to recover." :
                $"Recovered {recovered.Count} interrupted package transaction(s) from protected backups.");
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    public static IReadOnlyList<string> Review(CivilizationInstallerPackage package, string gamePath)
    {
        try
        {
            CivilizationInstallerPackage.Validate(package);
            var root = Root(gamePath);
            EnsureGameClosed(root);
            CheckCompatibility(package, root, LoadInstalled(root), false);
            if (LoadTransactions(root).Any(transaction => transaction.State != "Complete"))
                return ["An interrupted package transaction needs recovery. Use Recover interrupted changes before installing this package."];
            _ = PrepareInstall(package, root); // Read-only expected field/model baseline check.
            return [];
        }
        catch (Exception error) when (Expected(error)) { return [error.Message]; }
    }

    public static CivilizationInstallResult Install(CivilizationInstallerPackage package, string gamePath)
        => InstallCore(package, gamePath, allowAlreadyInstalled: false);

    internal static CivilizationInstallResult InstallOrVerifyInstalled(CivilizationInstallerPackage package, string gamePath)
        => InstallCore(package, gamePath, allowAlreadyInstalled: true);

    private static CivilizationInstallResult InstallCore(CivilizationInstallerPackage package, string gamePath, bool allowAlreadyInstalled)
    {
        try
        {
            CivilizationInstallerPackage.Validate(package);
            var root = Root(gamePath);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            if (allowAlreadyInstalled)
            {
                var installed = LoadInstalled(root);
                var existing = installed.SingleOrDefault(item => item.Package.PackageId == package.PackageId);
                if (existing is not null)
                {
                    if (!SamePackage(existing.Package, package)) throw new InvalidDataException("The installed package identity belongs to different export contents.");
                    CheckInstalledState(existing.Package, root, installed);
                    return new(true, "Already installed on this computer.", FileCount: package.Files.Count(file => file.WholeFile || file.Patches.Count > 0));
                }
            }
            RecoverTransactions(root);
            CheckCompatibility(package, root, LoadInstalled(root), false);
            var edits = PrepareInstall(package, root);
            var manifest = new PackageInstallation { Package = package, InstalledAt = DateTimeOffset.UtcNow };
            return Transact(root, package, "Install", edits, () => WriteJson(ManifestPath(root, package.PackageId), manifest));
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    /// <summary>Read-only reuse of a persisted sender package, including its original field baselines.</summary>
    internal static CivilizationInstallerPackage? ReadInstalledExport(CivilizationEditProject project, string gamePath)
    {
        var root = Root(gamePath);
        var document = PortableProjectJson(project);
        var installed = LoadInstalled(root);
        var matching = installed.Where(item => PortableProjectJson(CivilizationEditProject.Deserialize(item.Package.ProjectJson)) == document).ToArray();
        if (matching.Length > 1) throw new InvalidDataException("More than one installed package matches this project. Resolve the package inventory before exporting.");
        if (matching.Length == 0) return null;
        CheckInstalledState(matching[0].Package, root, installed);
        return matching[0].Package;
    }

    /// <summary>Read-only acknowledgement of an exact, fully verified prior installation.</summary>
    internal static bool IsInstalledAndVerified(CivilizationInstallerPackage package, string gamePath)
    {
        CivilizationInstallerPackage.Validate(package);
        var root = Root(gamePath);
        var installed = LoadInstalled(root);
        var existing = installed.SingleOrDefault(item => item.Package.PackageId == package.PackageId);
        if (existing is null) return false;
        if (!CivilizationPackageContentComparer.SameInstalledContent(package, existing.Package))
            throw new InvalidDataException("The installed package identity belongs to different export contents.");
        CheckInstalledState(existing.Package, root, installed);
        return true;
    }

    private static string PortableProjectJson(CivilizationEditProject project)
    {
        var portable = project.Clone(); portable.GamePath = ""; return portable.Serialize();
    }

    private static bool SamePackage(CivilizationInstallerPackage first, CivilizationInstallerPackage second) =>
        JsonSerializer.SerializeToUtf8Bytes(first, CivilizationInstallerPackageCodec.JsonOptions).AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(second, CivilizationInstallerPackageCodec.JsonOptions));

    private static void CheckInstalledState(CivilizationInstallerPackage package, string root, IReadOnlyList<PackageInstallation> installed)
    {
        if (LoadTransactions(root).Any(transaction => transaction.State != "Complete"))
            throw new InvalidDataException("An interrupted package transaction needs recovery before this project can be exported again.");
        CheckCompatibility(package, root, installed, uninstall: false, alreadyInstalled: true);
        foreach (var file in package.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
        {
            var current = ReadBounded(ResolveTarget(root, file.RelativePath));
            if (file.WholeFile)
            {
                if (!current.AsSpan().SequenceEqual(file.Replacement))
                    throw new InvalidDataException("An installed export file changed externally. This project cannot be exported as installed: " + file.RelativePath);
            }
            else
            {
                foreach (var patch in file.Patches)
                    if (!current.AsSpan(patch.Offset, patch.Length).SequenceEqual(patch.Replacement))
                        throw new InvalidDataException("An installed field changed externally. This project cannot be exported as installed: " + patch.SemanticTarget);
                ValidateMergedTables(file, current, current, allowUniversalRestoration: true);
            }
        }
        ValidateProgressionAvailability(root, [], installed.Select(item => item.Package));
        // An idempotent result also requires complete, intact protected originals so
        // the existing tracked installation remains removable after a re-export.
        var backup = InstalledBackups(root, package.PackageId);
        var changed = package.Files.Where(file => file.WholeFile || file.Patches.Count > 0).ToArray();
        if (backup.Files.Count != changed.Length || changed.Any(file => backup.Files.Count(entry => entry.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new InvalidDataException("The installed package's protected backup index is incomplete.");
        foreach (var file in backup.Files)
        {
            if (file.BeforeHash is null) continue;
            var original = ReadBounded(Path.Combine(backup.Directory, file.BackupName));
            if (!SameHash(CivilizationInstallerPackage.Hash(original), file.BeforeHash))
                throw new InvalidDataException("An installed package's protected original backup changed.");
        }
    }

    public static CivilizationInstallResult Uninstall(string gamePath, string packageId)
    {
        if (packageId == "legacy") return CivilizationGameInstaller.Restore(gamePath);
        try
        {
            RequireId(packageId);
            var root = Root(gamePath);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            var recovered = RecoverTransactions(root);
            var manifests = LoadInstalled(root);
            var manifest = manifests.SingleOrDefault(item => item.Package.PackageId == packageId);
            if (manifest is null)
                return recovered.Contains(packageId) ? new(true, "The interrupted installation was recovered.") : new(false, "This package is not installed.");
            CheckCompatibility(manifest.Package, root, manifests, true);
            var edits = PrepareUninstall(manifest.Package, root);
            return Transact(root, manifest.Package, "Uninstall", edits, () => File.Delete(ManifestPath(root, packageId)));
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    private static void CheckCompatibility(CivilizationInstallerPackage package, string root,
        IReadOnlyList<PackageInstallation> installed, bool uninstall, bool alreadyInstalled = false)
    {
        var executable = ResolveTarget(root, "Empires_DMW.exe", true);
        if (!SameHash(FileHash(executable), package.ExecutableHash))
            throw new InvalidDataException("Choose the supported Empires: Dawn of the Modern World installation. This game executable has not been verified.");
        if (!uninstall && File.Exists(Path.Combine(root, ".edmw-civilization-editor", "current.json")))
            throw new InvalidDataException("Uninstall the earlier civilization edit before installing a shared package.");
        if (!uninstall && !alreadyInstalled && installed.Any(item => item.Package.PackageId == package.PackageId))
            throw new InvalidDataException("This civilization package is already installed.");
        foreach (var other in installed.Where(item => item.Package.PackageId != package.PackageId))
        {
            foreach (var table in package.Files.Where(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath) || CivilizationInstallerPackage.IsObjectPath(file.RelativePath)))
            {
                var owned = other.Package.Files.FirstOrDefault(file => file.RelativePath.Equals(table.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (owned is not null && (table.WholeFile || owned.WholeFile))
                    throw new InvalidDataException($"'{other.Package.ProjectName}' already depends on {table.RelativePath}. A progression producer addition owns that complete table; uninstall the other package first.");
            }
            if (package.OwnerTargets.Intersect(other.Package.OwnerTargets, StringComparer.Ordinal).Any())
                throw new InvalidDataException($"'{other.Package.ProjectName}' already owns a logical model override changed by this package. Uninstall it first.");
            foreach (var file in package.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
            {
                var existing = other.Package.Files.FirstOrDefault(candidate => candidate.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (existing is null || (!existing.WholeFile && existing.Patches.Count == 0)) continue;
                if (file.WholeFile || existing.WholeFile || file.Patches.Any(patch => existing.Patches.Any(owned => Overlap(patch, owned))))
                    throw new InvalidDataException($"'{other.Package.ProjectName}' already owns a field or model changed by this package: {file.RelativePath}. Uninstall that package first.");
            }
        }
        foreach (var file in package.Files.Where(file => !file.WholeFile))
        {
            var bytes = ReadBounded(ResolveTarget(root, file.RelativePath));
            if (bytes.Length != file.ExpectedLength || (!uninstall && !MatchesStructuralHash(file, bytes, installed)))
                throw new InvalidDataException($"The table layout, record identities or unmapped game data differ from this package: {file.RelativePath}.");
            var validSlots = CivilizationInstallerPackage.SemanticSlots(file.RelativePath, bytes).ToHashSet();
            foreach (var patch in file.Patches)
            {
                if (!validSlots.Contains((patch.Offset, patch.Length)))
                    throw new InvalidDataException("The package contains a patch outside a verified semantic field.");
                var guard = CivilizationInstallerPackage.IdentityGuard(file.RelativePath, bytes, patch.Offset);
                if (guard.Offset != patch.IdentityOffset || !guard.Bytes.AsSpan().SequenceEqual(patch.IdentityBytes))
                    throw new InvalidDataException("The source record identity differs from this package.");
            }
        }
        if (uninstall) return; // Restoration checks only owned current ranges; unrelated immutable assets may evolve.
        foreach (var dependency in package.Dependencies)
            if (!SameHash(FileHash(ResolveTarget(root, dependency.Key, true)), dependency.Value))
                throw new InvalidDataException("A required game asset differs from the package source: " + dependency.Key);
        if (package.AssetDependencies.Count > 0)
        {
            var assets = new CivilizationGameAssets(root);
            foreach (var dependency in package.AssetDependencies)
                if (!SameHash(CivilizationInstallerPackage.Hash(assets.ReadAssetFile(dependency.Key)), dependency.Value))
                    throw new InvalidDataException("A required packed or loose game definition differs from the package source: " + dependency.Key);
        }
        if (package.ModelReferences.Count > 0)
        {
            var assets = new CivilizationGameAssets(root);
            foreach (var reference in package.ModelReferences)
            {
                var ownReplacement = alreadyInstalled ? package.Files.FirstOrDefault(file => file.WholeFile &&
                    file.RelativePath.Equals("Data\\" + reference.ModelName, StringComparison.OrdinalIgnoreCase)) : null;
                var expectedHash = ownReplacement is null ? reference.Hash : CivilizationInstallerPackage.Hash(ownReplacement.Replacement!);
                if (!string.Equals(assets.FindModelName(reference.GraphicsId), reference.ModelName, StringComparison.OrdinalIgnoreCase) ||
                    !SameHash(CivilizationInstallerPackage.Hash(assets.ReadModel(reference.ModelName)), expectedHash))
                    throw new InvalidDataException("A source model mapping or asset differs from this package: " + reference.ModelName);
            }
        }
    }

    private static bool MatchesStructuralHash(CivilizationPackageFile file, byte[] bytes,
        IReadOnlyList<PackageInstallation> installed)
    {
        if (CivilizationInstallerPackage.MatchesStructuralHash(file, bytes)) return true;
        if (!file.RelativePath.EndsWith("\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase)) return false;
        var ageSlots = CivilizationTechnologyBindings.ReadRecords(bytes)
            .SelectMany(record => new[] { record.Offset + 108, record.Offset + 112 }).ToHashSet();
        var maskSlots = CivilizationInstallerPackage.TechnologyMaskOffsets(bytes);
        if (CivilizationInstallerPackage.HasTechnologyMembershipPatches(file, bytes)) return false;
        // Reconstruct older fingerprints only through intact independently
        // tracked fields. Unowned external age/mask edits still fail the hash.
        var maskBaseline = bytes.ToArray();
        var legacyBaseline = bytes.ToArray();
        foreach (var package in installed)
        {
            var owned = package.Package.Files.FirstOrDefault(candidate =>
                !candidate.WholeFile && candidate.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (owned is null) continue;
            foreach (var patch in owned.Patches.Where(patch =>
                (patch.Length == 4 && ageSlots.Contains(patch.Offset)) || (patch.Length == 1 && maskSlots.Contains(patch.Offset))))
                if (bytes.AsSpan(patch.Offset, patch.Length).SequenceEqual(patch.Replacement))
                {
                    patch.Expected.CopyTo(legacyBaseline, patch.Offset);
                    if (maskSlots.Contains(patch.Offset)) patch.Expected.CopyTo(maskBaseline, patch.Offset);
                }
        }
        if (SameHash(CivilizationInstallerPackage.AgeEditingStructuralHash(file.RelativePath, maskBaseline), file.StructuralHash)) return true;
        return !CivilizationInstallerPackage.HasTechnologyAgePatches(file, bytes) &&
            SameHash(CivilizationInstallerPackage.LegacyStructuralHash(file.RelativePath, legacyBaseline), file.StructuralHash);
    }

    private static List<PendingFile> PrepareInstall(CivilizationInstallerPackage package, string root)
    {
        var pending = new List<PendingFile>();
        foreach (var file in package.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
        {
            var target = ResolveTarget(root, file.RelativePath);
            var original = File.Exists(target) ? ReadBounded(target) : null;
            if (file.WholeFile)
            {
                if (!SameHash(original is null ? null : CivilizationInstallerPackage.Hash(original), file.ExpectedHash))
                    throw new InvalidDataException("A whole-file replacement's original file differs: " + file.RelativePath);
                // Original loose models are embedded in the local installed
                // manifest, so arbitrary removal restores the real prior asset.
                pending.Add(new(file.RelativePath, original, file.Replacement!.ToArray()));
            }
            else
            {
                if (original is null) throw new FileNotFoundException("A target database is missing: " + file.RelativePath);
                var patched = original.ToArray();
                foreach (var patch in file.Patches)
                {
                    if (!original.AsSpan(patch.Offset, patch.Length).SequenceEqual(patch.Expected))
                        throw new InvalidDataException("A field's current value differs from the reviewed package baseline: " + patch.SemanticTarget);
                    patch.Replacement.CopyTo(patched, patch.Offset);
                }
                ValidateMergedTables(file, original, patched);
                pending.Add(new(file.RelativePath, original, patched));
            }
        }
        ValidateProgressionAvailability(root, pending, LoadInstalled(root).Select(item => item.Package).Append(package));
        return pending;
    }

    private static List<PendingFile> PrepareUninstall(CivilizationInstallerPackage package, string root)
    {
        var backup = InstalledBackups(root, package.PackageId);
        var pending = new List<PendingFile>();
        foreach (var file in package.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
        {
            var target = ResolveTarget(root, file.RelativePath);
            var current = File.Exists(target) ? ReadBounded(target) : null;
            if (file.WholeFile)
            {
                if (current is null || !SameHash(CivilizationInstallerPackage.Hash(current), CivilizationInstallerPackage.Hash(file.Replacement!)))
                    throw new InvalidDataException("An installed export file changed externally; removal was cancelled: " + file.RelativePath);
                var entry = backup.Files.Single(item => item.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
                var original = entry.BeforeHash is null ? null : ReadBounded(Path.Combine(backup.Directory, entry.BackupName));
                if (!SameHash(original is null ? null : CivilizationInstallerPackage.Hash(original), entry.BeforeHash))
                    throw new InvalidDataException("An export file's protected original backup is missing or has changed.");
                if (CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath) || CivilizationInstallerPackage.IsObjectPath(file.RelativePath))
                    CivilizationInstallerPackage.ValidateWholeDatabase(file.RelativePath,
                        original ?? throw new InvalidDataException("The original progression table backup is missing."));
                pending.Add(new(file.RelativePath, current, original));
            }
            else
            {
                if (current is null) throw new InvalidDataException("An installed database is missing.");
                var restored = current.ToArray();
                foreach (var patch in file.Patches)
                {
                    if (!current.AsSpan(patch.Offset, patch.Length).SequenceEqual(patch.Replacement))
                        throw new InvalidDataException("An installed field changed externally; removal was cancelled: " + patch.SemanticTarget);
                    patch.Expected.CopyTo(restored, patch.Offset);
                }
                ValidateMergedTables(file, current, restored, allowUniversalRestoration: true);
                pending.Add(new(file.RelativePath, current, restored));
            }
        }
        ValidateProgressionAvailability(root, pending, LoadInstalled(root).Where(item => item.Package.PackageId != package.PackageId).Select(item => item.Package));
        return pending;
    }

    private static void ValidateProgressionAvailability(string root, IReadOnlyList<PendingFile> pending,
        IEnumerable<CivilizationInstallerPackage> packages)
    {
        var authored = packages.SelectMany(package => CivilizationEditProject.Deserialize(package.ProjectJson).Civilizations)
            .SelectMany(civilization => civilization.Entities.Where(entity => entity.Properties.Any(pair =>
                CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, pair.Key) &&
                bool.TryParse(pair.Value, out var included) && included)).Select(entity => (Civilization: civilization, Entity: entity)))
            .ToArray();
        if (authored.Length == 0) return;
        byte[] ReadResult(string relative) => pending.FirstOrDefault(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase))?.After
            ?? ReadBounded(ResolveTarget(root, relative));
        var objects = ReadResult("Data\\db\\dbobjects.dat");
        var records = CivilizationTechnologyBindings.ReadRecords(ReadResult("Data\\db\\dbtechtree.dat"));
        foreach (var item in authored)
        {
            if (item.Civilization.SourceRowIndex is not int row) throw new InvalidDataException("A progression civilization source is missing.");
            var available = item.Entity.Category switch
            {
                CivilizationCategory.Units or CivilizationCategory.Buildings => item.Entity.SourceObjectId is int objectId &&
                    CivilizationTechnologyBindings.GetCivilizationProgressionObjectIds(objects, records, row, item.Entity.Category, 0, 100, includeCandidates: false).Contains(objectId),
                CivilizationCategory.Technologies => item.Entity.SourceTechnologyId is int techId &&
                    CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(objects, records, row, 0, 100, includeCandidates: false).Any(record => record.TechId == techId),
                _ => false
            };
            if (!available) throw new InvalidDataException($"The combined packages would leave '{item.Entity.Name}' without civilization membership or an available producer. Remove its progression addition before removing its producer.");
        }
    }

    private static void ValidateMergedTables(CivilizationPackageFile file, byte[] before, byte[] bytes,
        bool allowUniversalRestoration = false)
    {
        var validation = bytes.ToArray();
        if (file.RelativePath.EndsWith("\\dbobjects.dat", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var patch in file.Patches)
            {
                var row = (patch.Offset - 4) / 1936;
                var start = 4 + row * 1936;
                var category = Encoding.ASCII.GetString(before, start, 100).StartsWith("b ", StringComparison.OrdinalIgnoreCase)
                    ? CivilizationCategory.Buildings : CivilizationCategory.Units;
                var field = CivilizationObjectBindings.Fields(category).SingleOrDefault(field => field.Offset == patch.Offset - start)
                    ?? throw new InvalidDataException("This object category does not support the package field.");
                CivilizationObjectBindings.WriteValue(validation, row, category, field.Key, FieldValue(field, bytes, patch.Offset));
            }
            foreach (var row in file.Patches.Where(patch => (patch.Offset - 4) % 1936 is 128 or 132)
                .Select(patch => (patch.Offset - 4) / 1936).Distinct())
            {
                var start = 4 + row * 1936;
                var minimum = BitConverter.ToSingle(bytes, start + 128);
                var maximum = BitConverter.ToSingle(bytes, start + 132);
                if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || minimum < 0 || maximum < 0 || minimum > maximum)
                    throw new InvalidDataException($"The combined packages would create an invalid minimum/maximum attack range for object {row}.");
            }
        }
        else if (file.RelativePath.EndsWith("\\dbphoenixcivtable.dat", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var patch in file.Patches)
            {
                var row = (patch.Offset - 4) / 344;
                var field = CivilizationCivilizationBindings.Fields.Single(field => field.Offset == (patch.Offset - 4) % 344);
                CivilizationCivilizationBindings.WriteValue(validation, row, field.Key, FieldValue(field, bytes, patch.Offset));
            }
            foreach (var row in file.Patches.Select(patch => (patch.Offset - 4) / 344).Distinct())
                CivilizationCivilizationBindings.ValidateRow(bytes, row);
        }
        else if (file.RelativePath.EndsWith("\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase))
        {
            var records = CivilizationTechnologyBindings.ReadRecords(bytes);
            var fields = CivilizationTechnologyBindings.GetFields(CivilizationCategory.Technologies);
            foreach (var patch in file.Patches)
            {
                var record = records.Single(record => patch.Offset >= record.Offset && patch.Offset < record.Offset + 436);
                if (patch.Length == 1 && CivilizationInstallerPackage.ProgressionMaskOffsets.Contains(patch.Offset - record.Offset))
                {
                    if (bytes[patch.Offset] > 1) throw new InvalidDataException("A progression membership flag must contain 0 or 1.");
                    continue;
                }
                var field = fields.Single(field => field.Offset == patch.Offset - record.Offset);
                CivilizationTechnologyBindings.WriteObjectValue(validation, record.TechId, CivilizationCategory.Technologies,
                    field.Key, FieldValue(field, bytes, patch.Offset));
            }
            // Disjoint lower/upper endpoint packages still share one interval.
            // Validate the complete result before writing, including uninstall.
            CivilizationTechnologyBindings.ValidateAgeIntervals(bytes);
            ValidateMembershipRanges(file, before, bytes, records, allowUniversalRestoration);
        }
        else if (file.RelativePath.EndsWith("\\dbeffects.dat", StringComparison.OrdinalIgnoreCase) &&
            file.Patches.Any(patch => !float.IsFinite(BitConverter.ToSingle(bytes, patch.Offset))))
            throw new InvalidDataException("Effect operands require finite numbers.");
    }

    private static void ValidateMembershipRanges(CivilizationPackageFile file, byte[] before, byte[] bytes,
        IReadOnlyList<CivilizationTechnologyRecord> records, bool allowUniversalRestoration)
    {
        foreach (var record in records)
        {
            var maskPatches = file.Patches.Where(patch => patch.Length == 1 &&
                CivilizationInstallerPackage.ProgressionMaskOffsets.Contains(patch.Offset - record.Offset)).ToArray();
            if (maskPatches.Length == 0) continue;
            var universal = record.Offset + 312;
            if (before[universal] > 1 || bytes[universal] > 1 || CivilizationInstallerPackage.ProgressionMaskOffsets.Any(offset => bytes[record.Offset + offset] > 1))
                throw new InvalidDataException("A progression membership flag must contain 0 or 1.");
            if (before[universal] != bytes[universal])
            {
                if (maskPatches.Length != CivilizationInstallerPackage.ProgressionMaskOffsets.Length ||
                    CivilizationInstallerPackage.ProgressionMaskOffsets.Any(offset => !maskPatches.Any(patch => patch.Offset == record.Offset + offset)))
                    throw new InvalidDataException("A universal progression membership change must own all civilization flags atomically.");
                if (bytes[universal] != 0 && !allowUniversalRestoration)
                    throw new InvalidDataException("An export cannot grant universal progression membership.");
            }
            else if (before[universal] != 0 && !allowUniversalRestoration)
                throw new InvalidDataException("Individual progression membership flags require an explicit civilization mask.");
        }
    }
    private static string FieldValue(CivilizationBinaryField field, byte[] bytes, int offset)
    {
        if (field.Type == CivilizationBinaryType.String100)
        {
            var value = bytes.AsSpan(offset, 100);
            var end = value.IndexOf((byte)0);
            if (end < 0 || value[..end].IndexOfAnyExceptInRange((byte)32, (byte)126) >= 0)
                throw new InvalidDataException("A package internal name is not terminated printable ASCII.");
            return Encoding.ASCII.GetString(value[..end]);
        }
        if (field.Type == CivilizationBinaryType.BooleanByte)
            return bytes[offset] switch { 0 => "false", 1 => "true", _ => throw new InvalidDataException("A package Boolean field must contain 0 or 1.") };
        var number = field.Type switch
        {
            CivilizationBinaryType.UInt32 => (double)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)),
            CivilizationBinaryType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)),
            CivilizationBinaryType.Float32 => BitConverter.ToSingle(bytes, offset),
            CivilizationBinaryType.Byte => bytes[offset],
            _ => throw new InvalidDataException("A package field type is unsupported.")
        };
        return (number / field.Scale).ToString("G17", CultureInfo.InvariantCulture);
    }

    private static CivilizationInstallResult Transact(string root, CivilizationInstallerPackage package,
        string operation, IReadOnlyList<PendingFile> edits, Action commitManifest)
    {
        var folder = SafeStorage(root, "transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var journal = new PackageTransaction { Package = package, Operation = operation, CreatedAt = DateTimeOffset.UtcNow };
        if (File.Exists(ManifestPath(root, package.PackageId)))
        {
            var manifestBefore = ReadBounded(ManifestPath(root, package.PackageId), 384 * 1024 * 1024);
            WriteAtomic(Path.Combine(folder, "manifest.original"), manifestBefore);
            journal.ManifestBeforeHash = CivilizationInstallerPackage.Hash(manifestBefore);
        }
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var backupName = index + ".original";
            if (edit.Before is not null) WriteAtomic(Path.Combine(folder, backupName), edit.Before);
            if (edit.After is not null) WriteAtomic(Path.Combine(folder, index + ".staged"), edit.After);
            journal.Files.Add(new() { RelativePath = edit.RelativePath, BackupName = backupName,
                BeforeHash = edit.Before is null ? null : CivilizationInstallerPackage.Hash(edit.Before),
                AfterHash = edit.After is null ? null : CivilizationInstallerPackage.Hash(edit.After) });
        }
        WriteJson(Path.Combine(folder, "transaction.json"), journal);
        try
        {
            for (var index = 0; index < edits.Count; index++)
            {
                EnsureGameClosed(root);
                var file = journal.Files[index];
                var target = ResolveTarget(root, file.RelativePath);
                RequireHash(target, file.BeforeHash, "A game file changed during the package transaction.");
                if (edits[index].After is not null)
                {
                    var staged = Path.Combine(folder, index + ".staged");
                    RequireHash(staged, file.AfterHash, "A staged package file changed.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(staged, target, true);
                }
                else File.Delete(target);
            }
            if (operation == "Install")
            {
                journal.InstallBackup = true;
                // This journal becomes the retained backup index only after
                // every file has applied. It is referenced by package ID.
                WriteJson(SafeStorage(root, "backups", package.PackageId + ".json"), new PackageBackup
                    { Directory = folder, Files = journal.Files });
            }
            commitManifest();
            journal.State = "Complete";
            WriteJson(Path.Combine(folder, "transaction.json"), journal);
            return new(true, operation == "Install" ? "Civilization package installed." : "Civilization package uninstalled; other packages were preserved.", folder, edits.Count);
        }
        catch (Exception error) when (Expected(error))
        {
            try
            {
                Rollback(root, folder, journal);
                journal.State = "Complete";
                WriteJson(Path.Combine(folder, "transaction.json"), journal);
                return new(false, error.Message + " The transaction was rolled back.", folder);
            }
            catch (Exception rollbackError) when (Expected(rollbackError))
            {
                return new(false, error.Message + " Recovery is required: " + rollbackError.Message + " Protected backups: " + folder, folder);
            }
        }
    }

    private static HashSet<string> RecoverTransactions(string root)
    {
        var recovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transaction in LoadTransactions(root).Where(transaction => transaction.State != "Complete"))
        {
            Rollback(root, transaction.Directory, transaction);
            transaction.State = "Complete";
            WriteJson(Path.Combine(transaction.Directory, "transaction.json"), transaction);
            recovered.Add(transaction.Package.PackageId);
        }
        return recovered;
    }
    private static void Rollback(string root, string directory, PackageTransaction journal)
    {
        // Validate the entire recovery first. A corrupt earlier backup must not
        // cause later files to be restored before the error is discovered.
        var manifestBefore = journal.ManifestBeforeHash is null ? journal.ManifestBefore :
            ReadBounded(Path.Combine(directory, "manifest.original"), 384 * 1024 * 1024);
        if (journal.ManifestBeforeHash is not null && !SameHash(CivilizationInstallerPackage.Hash(manifestBefore!), journal.ManifestBeforeHash))
            throw new InvalidDataException("The protected prior package manifest backup has changed.");
        var pending = new List<PendingFile>();
        foreach (var file in journal.Files.AsEnumerable().Reverse())
        {
            var target = ResolveTarget(root, file.RelativePath);
            var current = ReadBoundedOrNull(target);
            var currentHash = current is null ? null : CivilizationInstallerPackage.Hash(current);
            if (SameHash(currentHash, file.BeforeHash)) continue;
            if (!SameHash(currentHash, file.AfterHash)) throw new InvalidDataException("A transaction file changed externally and cannot be recovered: " + file.RelativePath);
            var original = file.BeforeHash is null ? null : ReadBounded(Path.Combine(directory, file.BackupName));
            if (!SameHash(original is null ? null : CivilizationInstallerPackage.Hash(original), file.BeforeHash))
                throw new InvalidDataException("A protected transaction backup has changed.");
            pending.Add(new(file.RelativePath, current, original));
        }
        var manifestPath = ManifestPath(root, journal.Package.PackageId);
        var currentManifest = File.Exists(manifestPath) ? ReadBounded(manifestPath, 384 * 1024 * 1024) : null;
        if (currentManifest is not null && (manifestBefore is null || !currentManifest.AsSpan().SequenceEqual(manifestBefore)))
        {
            var active = ReadJsonBytes<PackageInstallation>(currentManifest);
            if (active.Package.PackageId != journal.Package.PackageId ||
                !JsonSerializer.SerializeToUtf8Bytes(active.Package, CivilizationInstallerPackageCodec.JsonOptions).AsSpan().SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(journal.Package, CivilizationInstallerPackageCodec.JsonOptions)))
                throw new InvalidDataException("A package manifest changed externally and cannot be recovered.");
        }
        foreach (var file in pending)
        {
            var target = ResolveTarget(root, file.RelativePath);
            RequireHash(target, file.Before is null ? null : CivilizationInstallerPackage.Hash(file.Before), "A transaction file changed during recovery.");
            WriteAtomic(target, file.After);
        }
        WriteAtomic(ManifestPath(root, journal.Package.PackageId), manifestBefore);
        if (journal.Operation == "Install") File.Delete(SafeStorage(root, "backups", journal.Package.PackageId + ".json"));
    }

    private static IReadOnlyList<PackageInstallation> LoadInstalled(string root)
    {
        var directory = SafeStorage(root, "installed");
        if (!Directory.Exists(directory)) return [];
        var entries = new List<PackageInstallation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            RejectReparse(path);
            var manifest = ReadJson<PackageInstallation>(path, 384 * 1024 * 1024);
            CivilizationInstallerPackage.Validate(manifest.Package);
            if (Path.GetFileNameWithoutExtension(path) != manifest.Package.PackageId)
                throw new InvalidDataException("An installed package manifest identity differs from its filename.");
            entries.Add(manifest);
        }
        return entries;
    }
    private static IReadOnlyList<PackageTransaction> LoadTransactions(string root)
    {
        var directory = SafeStorage(root, "transactions");
        if (!Directory.Exists(directory)) return [];
        var entries = new List<PackageTransaction>();
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            RejectReparse(folder);
            var path = Path.Combine(folder, "transaction.json");
            if (!File.Exists(path)) continue;
            var journal = ReadJson<PackageTransaction>(path, 384 * 1024 * 1024);
            if (journal.State is not ("Prepared" or "Complete") || journal.Operation is not ("Install" or "Uninstall"))
                throw new InvalidDataException("A package transaction status is invalid.");
            if (journal.ManifestBeforeHash is not null && !CivilizationInstallerPackage.ValidHash(journal.ManifestBeforeHash))
                throw new InvalidDataException("A prior manifest backup hash is invalid.");
            journal.Directory = folder;
            CivilizationInstallerPackage.Validate(journal.Package);
            ValidateBackups(root, journal.Files);
            entries.Add(journal);
        }
        return entries;
    }
    private static PackageBackup InstalledBackups(string root, string id)
    {
        var backup = ReadJson<PackageBackup>(SafeStorage(root, "backups", id + ".json"));
        var directory = Path.GetFullPath(backup.Directory);
        var storage = SafeStorage(root, "transactions") + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(storage, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A package backup directory is outside protected storage.");
        RejectReparse(directory);
        ValidateBackups(root, backup.Files);
        return backup;
    }
    private static void ValidateBackups(string root, IReadOnlyList<TransactionFile> files)
    {
        if (files is null || files.Count is < 1 or > 10_000) throw new InvalidDataException("A transaction backup index is invalid.");
        foreach (var file in files)
        {
            if (file is null) throw new InvalidDataException("A transaction backup entry is empty.");
            _ = ResolveTarget(root, file.RelativePath);
            if (Path.GetFileName(file.BackupName) != file.BackupName || !file.BackupName.EndsWith(".original", StringComparison.Ordinal) ||
                !int.TryParse(file.BackupName[..^9], out var number) || number < 0 ||
                (file.BeforeHash is not null && !CivilizationInstallerPackage.ValidHash(file.BeforeHash)) ||
                (file.AfterHash is not null && !CivilizationInstallerPackage.ValidHash(file.AfterHash)))
                throw new InvalidDataException("A transaction backup contains an invalid file or hash.");
        }
    }

    internal static string ResolveTarget(string root, string relative, bool dependency = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('/') ||
            relative.Split('\\').Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("A package file path is invalid.");
        var allowed = CivilizationInstallerPackage.IsDatabasePath(relative) || CivilizationInstallerPackage.IsModelPath(relative);
        if (dependency) allowed |= relative.Equals("Empires_DMW.exe", StringComparison.OrdinalIgnoreCase) ||
            (relative.StartsWith("Data\\db\\", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
        if (!allowed) throw new InvalidDataException("A package file is outside supported game data.");
        return BoundedPath(root, relative);
    }
    private static string Root(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected game folder does not exist.");
        RejectReparse(root);
        return root;
    }
    private static string SafeStorage(string root, params string[] parts) => BoundedPath(root, Path.Combine([Storage, .. parts]));
    private static string BoundedPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A package path leaves the game folder.");
        var check = path;
        while (check.Length > root.Length) { RejectReparse(check); check = Path.GetDirectoryName(check)!; }
        return path;
    }
    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package files and protected storage cannot use redirected paths.");
    }
    private static FileStream AcquireLock(string root)
    {
        var path = SafeStorage(root, "transaction.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static string ManifestPath(string root, string id) { RequireId(id); return SafeStorage(root, "installed", id + ".json"); }
    private static void RequireId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("The package identity is invalid."); }
    private static bool Overlap(CivilizationPackagePatch first, CivilizationPackagePatch second) => first.Offset < second.Offset + second.Length && second.Offset < first.Offset + first.Length;
    private static string? FileHash(string path) => File.Exists(path) ? CivilizationInstallerPackage.Hash(ReadBounded(path)) : null;
    private static byte[]? ReadBoundedOrNull(string path) => File.Exists(path) ? ReadBounded(path) : null;
    private static bool SameHash(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static void RequireHash(string path, string? expected, string message) { if (!SameHash(FileHash(path), expected)) throw new InvalidDataException(message + " " + Path.GetFileName(path)); }
    private static byte[] ReadBounded(string path, int maximum = MaximumFileBytes)
    {
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("A game/package file exceeds its supported size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static T ReadJson<T>(string path, int maximum = MaximumFileBytes) =>
        ReadJsonBytes<T>(ReadBounded(path, maximum));
    private static T ReadJsonBytes<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, CivilizationInstallerPackageCodec.JsonOptions) ?? throw new InvalidDataException("A package manifest is empty."); }
        catch (JsonException error) { throw new InvalidDataException("A protected package manifest is corrupt.", error); }
    }
    private static void WriteJson<T>(string path, T value) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, CivilizationInstallerPackageCodec.JsonOptions));
    private static void WriteAtomic(string path, byte[]? bytes)
    {
        RejectReparse(path);
        if (bytes is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureGameClosed(string root)
    {
        foreach (var process in Process.GetProcessesByName("Empires_DMW"))
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null || Path.GetDirectoryName(Path.GetFullPath(executable))!.Equals(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close Empires: Dawn of the Modern World before changing civilization packages.");
                }
                catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Close Empires: Dawn of the Modern World before changing civilization packages."); }
            }
    }
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception;
    private sealed record PendingFile(string RelativePath, byte[]? Before, byte[]? After);
    private sealed class PackageInstallation
    {
        public PackageInstallation() { }
        public CivilizationInstallerPackage Package { get; set; } = new();
        public DateTimeOffset InstalledAt { get; set; }
    }
    private sealed class PackageTransaction
    {
        public PackageTransaction() { }
        public string State { get; set; } = "Prepared";
        public string Operation { get; set; } = "";
        public CivilizationInstallerPackage Package { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; }
        public List<TransactionFile> Files { get; set; } = [];
        public byte[]? ManifestBefore { get; set; }
        public string? ManifestBeforeHash { get; set; }
        public bool InstallBackup { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Directory { get; set; } = "";
    }
    private sealed class PackageBackup
    {
        public PackageBackup() { }
        public string Directory { get; set; } = "";
        public List<TransactionFile> Files { get; set; } = [];
    }
    private sealed class TransactionFile
    {
        public TransactionFile() { }
        public string RelativePath { get; set; } = "";
        public string BackupName { get; set; } = "";
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
    }
}
