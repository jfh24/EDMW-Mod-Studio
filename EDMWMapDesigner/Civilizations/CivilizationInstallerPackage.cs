using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

public sealed class CivilizationInstallerPackage
{
    public int FormatVersion { get; set; } = 1;
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = "";
    public string ProjectJson { get; set; } = "";
    public string ExecutableHash { get; set; } = CivilizationGameInstaller.SupportedExecutableSha256;
    public List<CivilizationInstallChange> Changes { get; set; } = [];
    public List<CivilizationPackageFile> Files { get; set; } = [];
    public Dictionary<string, string?> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> AssetDependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CivilizationPackageModelReference> ModelReferences { get; set; } = [];
    public List<string> OwnerTargets { get; set; } = [];

    public static CivilizationInstallerPackage Create(CivilizationEditProject project, string gamePath)
    {
        var plan = CivilizationGameInstaller.BuildPlan(project, gamePath, forExport: true);
        if (!plan.CanInstall)
            throw new InvalidDataException(plan.Issues.Count > 0 ? string.Join(Environment.NewLine, plan.Issues) : "There are no changes to export.");
        var portableProject = plan.PreparedProject.Clone();
        portableProject.GamePath = "";
        var package = new CivilizationInstallerPackage
        {
            ProjectName = project.Name, ProjectJson = portableProject.Serialize(),
            Changes = plan.Changes.ToList(),
            ModelReferences = plan.Models.Select(model => new CivilizationPackageModelReference(model.GraphicsId, model.ModelName, model.Hash)).ToList()
        };
        foreach (var file in plan.Files)
        {
            if (IsModelPath(file.RelativePath) || CivilizationRuntimeEffects.IsPath(file.RelativePath))
            {
                package.Files.Add(new() { RelativePath = file.RelativePath, WholeFile = true,
                    ExpectedHash = file.OriginalHash, Replacement = file.Patched.ToArray() });
                continue;
            }
            // Adding an existing foreign item can append a verified producer
            // reference. Variable record offsets then move, so this narrowly
            // supported operation owns the complete technology table instead
            // of pretending its offsets can merge with independent packages.
            if (IsTechnologyPath(file.RelativePath) && file.Original is not null &&
                (file.Original.Length != file.Patched.Length || TechnologyButtonsDiffer(file.Original, file.Patched)))
            {
                package.Files.Add(new() { RelativePath = file.RelativePath, WholeFile = true,
                    ExpectedHash = file.OriginalHash, Replacement = file.Patched.ToArray() });
                continue;
            }
            if (IsObjectPath(file.RelativePath) && file.Original is not null && ObjectActionListsDiffer(file.Original, file.Patched))
            {
                package.Files.Add(new() { RelativePath = file.RelativePath, WholeFile = true,
                    ExpectedHash = file.OriginalHash, Replacement = file.Patched.ToArray() });
                continue;
            }
            if (file.Original is null || file.Original.Length != file.Patched.Length)
                throw new InvalidDataException("A database package must preserve file and record lengths.");
            var slots = SemanticSlots(file.RelativePath, file.Original).Distinct().OrderBy(slot => slot.Offset).ToArray();
            var patches = new List<CivilizationPackagePatch>();
            var covered = new bool[file.Original.Length];
            var expandedMasks = IsTechnologyPath(file.RelativePath)
                ? CivilizationTechnologyBindings.ReadRecords(file.Original)
                    .Where(record => file.Original[record.Offset + 312] != file.Patched[record.Offset + 312])
                    .SelectMany(record => ProgressionMaskOffsets.Select(offset => record.Offset + offset)).ToHashSet()
                : [];
            foreach (var slot in slots)
            {
                var before = file.Original.AsSpan(slot.Offset, slot.Length);
                var after = file.Patched.AsSpan(slot.Offset, slot.Length);
                // Universal membership is expanded into all nine civilization
                // flags. Retain unchanged flags as owned ranges, too: otherwise
                // another package could change an unowned flag and removal of
                // this expansion would silently enable that civilization again.
                if (before.SequenceEqual(after) && !expandedMasks.Contains(slot.Offset)) continue;
                var guard = IdentityGuard(file.RelativePath, file.Original, slot.Offset);
                patches.Add(new() { Offset = slot.Offset, Length = slot.Length,
                    SemanticTarget = $"{file.RelativePath.ToLowerInvariant()}:{slot.Offset}:{slot.Length}",
                    Expected = before.ToArray(), Replacement = after.ToArray(),
                    IdentityOffset = guard.Offset, IdentityBytes = guard.Bytes });
                Array.Fill(covered, true, slot.Offset, slot.Length);
            }
            for (var index = 0; index < file.Original.Length; index++)
                if (file.Original[index] != file.Patched[index] && !covered[index])
                    throw new InvalidDataException("The proposed changes contain bytes outside a verified writable field.");
            if (patches.Count > 0)
                package.Files.Add(new() { RelativePath = file.RelativePath, ExpectedLength = file.Original.Length,
                    StructuralHash = StructuralHash(file.RelativePath, file.Original), Patches = patches });
        }
        // Mutable tables are checked through structure/identity fingerprints and
        // field-sized expected bytes, allowing unrelated installed packages.
        foreach (var dependency in plan.Dependencies)
        {
            if (IsDatabasePath(dependency.Key))
            {
                if (package.Files.Any(file => file.RelativePath.Equals(dependency.Key, StringComparison.OrdinalIgnoreCase))) continue;
                var data = File.ReadAllBytes(Path.Combine(plan.GamePath, dependency.Key));
                package.Files.Add(new() { RelativePath = dependency.Key, ExpectedLength = data.Length,
                    StructuralHash = StructuralHash(dependency.Key, data) });
            }
            else package.Dependencies[dependency.Key] = dependency.Value;
        }
        // A model upload and the same object's graphics reference are coupled
        // logical overrides even when they touch different physical files.
        var objectsFile = package.Files.FirstOrDefault(file => file.RelativePath.EndsWith("\\dbobjects.dat", StringComparison.OrdinalIgnoreCase));
        foreach (var entity in project.Civilizations.SelectMany(civ => civ.Entities))
            if (entity.SourceObjectId is int source &&
                ((objectsFile?.Patches.Any(patch => patch.Offset == 4 + source * 1936 + 304) ?? false) ||
                 entity.Properties.Any(pair => pair.Key.EndsWith(".modelAsset", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value))))
                package.OwnerTargets.Add($"object:{source}:model");
        package.OwnerTargets = package.OwnerTargets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (project.Civilizations.SelectMany(civ => civ.Entities).Any(entity => entity.SourcePowerId is not null &&
            entity.Properties.Any(pair => pair.Key.Contains(".effect.", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value))))
        {
            var assets = new CivilizationGameAssets(gamePath);
            package.AssetDependencies["db\\dbcivpowers.dat"] = Hash(assets.ReadAssetFile("db\\dbcivpowers.dat"));
        }
        Validate(package);
        return package;
    }

    internal static bool IsDatabasePath(string path) => path.Equals("Data\\db\\dbobjects.dat", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("Data\\db\\dbphoenixcivtable.dat", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("Data\\db\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("Data\\db\\dbeffects.dat", StringComparison.OrdinalIgnoreCase);
    internal static bool IsModelPath(string path) => path.StartsWith("Data\\models\\", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".cem", StringComparison.OrdinalIgnoreCase);

    internal static string StructuralHash(string path, byte[] data)
    {
        var masked = data.ToArray();
        foreach (var slot in SemanticSlots(path, data)) masked.AsSpan(slot.Offset, slot.Length).Clear();
        return Hash(masked);
    }

    // Earlier exports fingerprinted masks, and the oldest exports fingerprinted
    // age bounds as well. Match those exact layouts without authorizing any new
    // field under their stricter fingerprints.
    internal static string AgeEditingStructuralHash(string path, byte[] data)
    {
        if (!IsTechnologyPath(path)) return StructuralHash(path, data);
        var masks = TechnologyMaskOffsets(data);
        var masked = data.ToArray();
        foreach (var slot in SemanticSlots(path, data))
            if (!masks.Contains(slot.Offset)) masked.AsSpan(slot.Offset, slot.Length).Clear();
        return Hash(masked);
    }

    internal static string LegacyStructuralHash(string path, byte[] data)
    {
        if (!IsTechnologyPath(path)) return StructuralHash(path, data);
        var ages = TechnologyAgeOffsets(data);
        var masks = TechnologyMaskOffsets(data);
        var masked = data.ToArray();
        foreach (var slot in SemanticSlots(path, data))
            if (!ages.Contains(slot.Offset) && !masks.Contains(slot.Offset)) masked.AsSpan(slot.Offset, slot.Length).Clear();
        return Hash(masked);
    }

    internal static bool MatchesStructuralHash(CivilizationPackageFile file, byte[] data)
    {
        if (file.WholeFile || file.ExpectedLength != data.Length) return false;
        if (string.Equals(StructuralHash(file.RelativePath, data), file.StructuralHash, StringComparison.OrdinalIgnoreCase)) return true;
        if (!IsTechnologyPath(file.RelativePath) || HasTechnologyMembershipPatches(file, data)) return false;
        if (string.Equals(AgeEditingStructuralHash(file.RelativePath, data), file.StructuralHash, StringComparison.OrdinalIgnoreCase)) return true;
        return !HasTechnologyAgePatches(file, data) &&
            string.Equals(LegacyStructuralHash(file.RelativePath, data), file.StructuralHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasTechnologyAgePatches(CivilizationPackageFile file, byte[] data)
    {
        if (!IsTechnologyPath(file.RelativePath)) return false;
        var ages = TechnologyAgeOffsets(data);
        return file.Patches.Any(patch => ages.Any(offset =>
            patch.Offset < offset + 4 && (long)patch.Offset + patch.Length > offset));
    }

    internal static bool IsTechnologyPath(string path) => path.Equals("Data\\db\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase);
    private static bool TechnologyButtonsDiffer(byte[] original, byte[] replacement)
    {
        var records = CivilizationTechnologyBindings.ReadRecords(replacement).ToDictionary(record => record.TechId);
        foreach (var record in CivilizationTechnologyBindings.ReadRecords(original))
        {
            if (!records.TryGetValue(record.TechId, out var replaced)) throw new InvalidDataException("Progression additions must preserve existing technology identities.");
            if (!original.AsSpan(record.Offset + 324, 88).SequenceEqual(replacement.AsSpan(replaced.Offset + 324, 88))) return true;
        }
        return false;
    }
    internal static bool IsObjectPath(string path) => path.Equals("Data\\db\\dbobjects.dat", StringComparison.OrdinalIgnoreCase);
    private static bool ObjectActionListsDiffer(byte[] original, byte[] replacement)
    {
        var count = Count(original, 1936);
        if (Count(replacement, 1936) != count) throw new InvalidDataException("Progression additions must preserve existing object records.");
        for (var row = 0; row < count; row++)
            if (!original.AsSpan(4 + row * 1936 + 1136, 800).SequenceEqual(replacement.AsSpan(4 + row * 1936 + 1136, 800))) return true;
        return false;
    }
    internal static void ValidateWholeDatabase(string path, byte[] data)
    {
        if (IsTechnologyPath(path)) _ = CivilizationTechnologyBindings.ReadRecords(data);
        else if (IsObjectPath(path)) _ = Count(data, 1936);
        else throw new InvalidDataException("The whole-file database operation is unsupported.");
    }
    private static HashSet<int> TechnologyAgeOffsets(byte[] data) => CivilizationTechnologyBindings.ReadRecords(data)
        .SelectMany(record => new[] { record.Offset + 108, record.Offset + 112 }).ToHashSet();
    internal static readonly int[] ProgressionMaskOffsets = [312, 315, 316, 317, 318, 319, 320, 321, 322, 323];
    internal static HashSet<int> TechnologyMaskOffsets(byte[] data) => CivilizationTechnologyBindings.ReadRecords(data)
        .SelectMany(record => ProgressionMaskOffsets.Select(offset => record.Offset + offset)).ToHashSet();
    internal static bool HasTechnologyMembershipPatches(CivilizationPackageFile file, byte[] data)
    {
        if (!IsTechnologyPath(file.RelativePath)) return false;
        var masks = TechnologyMaskOffsets(data);
        return file.Patches.Any(patch => masks.Any(offset => patch.Offset <= offset && (long)patch.Offset + patch.Length > offset));
    }

    internal static (int Offset, byte[] Bytes) IdentityGuard(string path, byte[] data, int fieldOffset)
    {
        var offset = path.EndsWith("\\dbobjects.dat", StringComparison.OrdinalIgnoreCase)
            ? 4 + ((fieldOffset - 4) / 1936) * 1936 + 116
            : path.EndsWith("\\dbphoenixcivtable.dat", StringComparison.OrdinalIgnoreCase)
                ? 4 + ((fieldOffset - 4) / 344) * 344
                : path.EndsWith("\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase)
                    ? CivilizationTechnologyBindings.ReadRecords(data).Single(record => fieldOffset >= record.Offset && fieldOffset < record.Offset + 436).Offset + 104
                    : (fieldOffset / 88) * 88;
        var length = path.EndsWith("\\dbphoenixcivtable.dat", StringComparison.OrdinalIgnoreCase) ? 8 :
            path.EndsWith("\\dbeffects.dat", StringComparison.OrdinalIgnoreCase) ? 88 : 4;
        var bytes = data.AsSpan(offset, length).ToArray();
        if (length == 88) bytes.AsSpan(8, 12).Clear(); // Preserve effect identity/opcode/selector/targets, allow independent operands.
        return (offset, bytes);
    }

    internal static IEnumerable<(int Offset, int Length)> SemanticSlots(string path, byte[] data)
    {
        if (path.EndsWith("\\dbobjects.dat", StringComparison.OrdinalIgnoreCase))
        {
            var count = Count(data, 1936);
            var fields = CivilizationObjectBindings.Fields(CivilizationCategory.Units)
                .Concat(CivilizationObjectBindings.Fields(CivilizationCategory.Buildings))
                .Select(field => (field.Offset, Length(field.Type))).Distinct().ToArray();
            for (var row = 0; row < count; row++)
            {
                var offset = 4 + row * 1936;
                if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 116, 4)) != row)
                    continue; // Deleted/legacy slots are not editable source records.
                foreach (var field in fields) yield return (offset + field.Offset, field.Item2);
            }
        }
        else if (path.EndsWith("\\dbphoenixcivtable.dat", StringComparison.OrdinalIgnoreCase))
        {
            var count = Count(data, 344);
            for (var row = 0; row < count; row++)
            {
                var offset = 4 + row * 344;
                if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4)) != row)
                    throw new InvalidDataException("A civilization record identity does not match its row.");
                foreach (var field in CivilizationCivilizationBindings.Fields)
                    yield return (offset + field.Offset, Length(field.Type));
            }
        }
        else if (path.EndsWith("\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase))
        {
            var fields = new[] { CivilizationCategory.Units, CivilizationCategory.Buildings,
                CivilizationCategory.Technologies, CivilizationCategory.Abilities }
                .SelectMany(CivilizationTechnologyBindings.GetFields)
                .Select(field => (field.Offset, Length(field.Type))).Distinct().ToArray();
            foreach (var record in CivilizationTechnologyBindings.ReadRecords(data))
            {
                foreach (var field in fields) yield return (record.Offset + field.Offset, field.Item2);
                foreach (var offset in ProgressionMaskOffsets) yield return (record.Offset + offset, 1);
            }
        }
        else if (path.EndsWith("\\dbeffects.dat", StringComparison.OrdinalIgnoreCase))
        {
            if (data.Length == 0 || data.Length % 88 != 0 || data.Length / 88 > 100_000)
                throw new InvalidDataException("The effect table has an unsupported layout.");
            foreach (var record in CivilizationAbilityData.ReadEffects(data))
                if (CivilizationAbilityData.HasVerifiedScalarModifier(record))
                    foreach (var offset in new[] { 8, 12, 16 }) yield return (record.Index * 88 + offset, 4);
        }
        else throw new InvalidDataException("The package refers to an unsupported mutable table.");
    }

    private static int Count(byte[] data, int stride)
    {
        if (data.Length < 4) throw new InvalidDataException("The database header is truncated.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count is < 1 or > 100_000 || data.LongLength != 4L + (long)count * stride)
            throw new InvalidDataException("The database record framing is unsupported.");
        return count;
    }
    private static int Length(CivilizationBinaryType type) => type is CivilizationBinaryType.Byte or CivilizationBinaryType.BooleanByte ? 1 :
        type == CivilizationBinaryType.String100 ? 100 : 4;
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    internal static void Validate(CivilizationInstallerPackage package)
    {
        if (package is null) throw new InvalidDataException("The civilization package manifest is empty.");
        if (package.FormatVersion != 1 || !Guid.TryParseExact(package.PackageId, "N", out _) ||
            string.IsNullOrWhiteSpace(package.ProjectName) || package.ProjectName.Length > 120 ||
            package.ExecutableHash != CivilizationGameInstaller.SupportedExecutableSha256 ||
            package.Files is null || package.Files.Count is < 1 or > 10_000 || package.Changes is null ||
            package.Changes.Count is < 1 or > 100_000 || package.Dependencies is null || package.AssetDependencies is null || package.AssetDependencies.Count > 100 ||
            package.ModelReferences is null || package.OwnerTargets is null ||
            package.OwnerTargets.Count > 100_000 || package.OwnerTargets.Any(target => string.IsNullOrEmpty(target) || target.Length > 200))
            throw new InvalidDataException("The civilization package manifest is invalid.");
        var project = CivilizationEditProject.Deserialize(package.ProjectJson);
        if (project.Name != package.ProjectName) throw new InvalidDataException("The embedded project name differs from its package.");
        var effectIds = new HashSet<Guid>();
        foreach (var effect in project.Civilizations.SelectMany(civ => civ.Entities).SelectMany(CivilizationUnitEffects.Read))
            if (!effectIds.Add(Guid.Parse(effect.Id)))
                throw new InvalidDataException("The package contains duplicate unit effect identities.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var changed = false;
        foreach (var file in package.Files)
        {
            if (file is null) throw new InvalidDataException("A package file entry is empty.");
            _ = CivilizationPackageManager.ResolveTarget(Path.GetFullPath("."), file.RelativePath);
            if (!paths.Add(file.RelativePath) || file.Patches is null) throw new InvalidDataException("Duplicate or missing package file entries.");
            if (file.Patches.Any(patch => patch is null)) throw new InvalidDataException("A package field entry is empty.");
            if (file.WholeFile)
            {
                var databaseTable = IsTechnologyPath(file.RelativePath) || IsObjectPath(file.RelativePath);
                if ((!IsModelPath(file.RelativePath) && !databaseTable && !CivilizationRuntimeEffects.IsPath(file.RelativePath)) || file.Replacement is null ||
                    file.Replacement.Length < 1 || file.Replacement.Length > (databaseTable ? 32 : 16) * 1024 * 1024 ||
                    file.Patches.Count != 0 || (file.ExpectedHash is not null && !ValidHash(file.ExpectedHash)) ||
                    (databaseTable && file.ExpectedHash is null))
                    throw new InvalidDataException("A whole-file replacement entry is invalid.");
                if (CivilizationRuntimeEffects.IsPath(file.RelativePath)) CivilizationRuntimeEffects.ValidatePackageFile(project, file);
                else if (databaseTable) ValidateWholeDatabase(file.RelativePath, file.Replacement);
                else _ = CivilizationModelReader.Read(file.Replacement, file.RelativePath);
                bytes += file.Replacement.Length;
                changed = true;
            }
            else
            {
                if (!IsDatabasePath(file.RelativePath) || file.ExpectedLength is < 4 or > 32 * 1024 * 1024 || !ValidHash(file.StructuralHash))
                    throw new InvalidDataException("A database package entry is invalid.");
                var end = 0;
                foreach (var patch in file.Patches.OrderBy(patch => patch.Offset))
                {
                    if (patch is null) throw new InvalidDataException("A package field entry is empty.");
                    if (patch.Offset < end || patch.Length is not (1 or 4 or 100) || patch.Offset > file.ExpectedLength - patch.Length ||
                        patch.Expected is null || patch.Replacement is null || patch.Expected.Length != patch.Length || patch.Replacement.Length != patch.Length ||
                        patch.IdentityBytes is null || patch.IdentityBytes.Length is not (4 or 8 or 88) || patch.IdentityOffset < 0 || patch.IdentityOffset > file.ExpectedLength - patch.IdentityBytes.Length ||
                        patch.SemanticTarget != $"{file.RelativePath.ToLowerInvariant()}:{patch.Offset}:{patch.Length}")
                        throw new InvalidDataException("A semantic field patch has invalid bounds or identity.");
                    end = patch.Offset + patch.Length;
                    bytes += patch.Length * 2;
                    changed = true;
                }
            }
        }
        var expectedRuntimeFiles = project.Civilizations.SelectMany(civ => civ.Entities
            .Where(entity => CivilizationRuntimeConfiguration.DataFor(civ, entity) is not null)
            .Select(CivilizationRuntimeEffects.PathFor)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedRuntimeFiles.SetEquals(package.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)).Select(file => file.RelativePath)))
            throw new InvalidDataException("The package is missing a runtime effect attachment from its source project.");
        var configurations = package.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))
            .Select(file => CivilizationRuntimeConfiguration.Decode(file.Replacement!)).ToArray();
        if (configurations.Sum(configuration => configuration.Count) > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException("The package contains too many runtime effect rules.");
        var unitRules = configurations.SelectMany(configuration => configuration.Units).ToArray();
        if (unitRules.Select(rule => rule.Id).Distinct().Count() != unitRules.Length)
            throw new InvalidDataException("The package contains duplicate unit effect identities.");
        var activated = unitRules.Where(rule => rule.Trigger == CivilizationUnitEffectTrigger.Activated).ToArray();
        if (activated.Length > 0)
        {
            if (activated.Select(rule => rule.TechnologyId).Distinct().Count() != activated.Length)
                throw new InvalidDataException("The package contains duplicate ability command identities.");
            var technologyFile = package.Files.SingleOrDefault(file => IsTechnologyPath(file.RelativePath));
            var objectsFile = package.Files.SingleOrDefault(file => IsObjectPath(file.RelativePath));
            if (technologyFile is not { WholeFile: true, Replacement: not null } || objectsFile is not { WholeFile: true, Replacement: not null })
                throw new InvalidDataException("An activated ability package is missing its in-game button tables.");
            var records = CivilizationTechnologyBindings.ReadRecords(technologyFile.Replacement).ToDictionary(record => record.TechId);
            foreach (var rule in activated)
            {
                var entity = project.Civilizations.Where(civ => civ.SourceCivilizationId == rule.CivilizationId).SelectMany(civ => civ.Entities)
                    .Single(entity => CivilizationUnitEffects.Read(entity).Any(effect => Guid.Parse(effect.Id) == rule.Id));
                var attachment = CivilizationUnitEffects.Read(entity).Single(effect => Guid.Parse(effect.Id) == rule.Id);
                if (!records.TryGetValue(rule.TechnologyId, out var record) || record.NodeKind != 3 || record.CreatesObject || record.EditorOnly ||
                    record.CommandId != 51 || record.EventId != 0 || record.WoodCost != 0 || record.StoneCost != 0 || record.GoldCost != 0 || record.FoodCost != 0 ||
                    BinaryPrimitives.ReadInt32LittleEndian(technologyFile.Replacement.AsSpan(record.Offset + 136)) != 0 ||
                    record.CreatedObjectId != 0 || record.PrerequisiteTechnologyIds.Count != 0 || record.AdditionalTechnologyIds.Count != 0 ||
                    !record.ProducerObjectIds.SequenceEqual([rule.SourceObjectId]) || !record.AllowedCivilizationRows.SequenceEqual([rule.CivilizationId - 1]) ||
                    !CivilizationObjectDetails.ReadActionTechnologyIds(objectsFile.Replacement, rule.SourceObjectId, entity.Category).Contains((uint)rule.TechnologyId) ||
                    BinaryPrimitives.ReadInt32LittleEndian(technologyFile.Replacement.AsSpan(record.Offset + 168)) != attachment.ButtonIconId ||
                    BinaryPrimitives.ReadInt32LittleEndian(technologyFile.Replacement.AsSpan(record.Offset + 176)) != 1_000_000 + rule.TechnologyId ||
                    record.CommonButtonSlot != attachment.ButtonSlot || record.CivilizationButtonSlots[rule.CivilizationId - 2] != attachment.ButtonSlot ||
                    record.ToolTipStringId != 2_000_000 + rule.TechnologyId || record.PrerequisiteStringId1 != 2_000_000 + rule.TechnologyId ||
                    record.PrerequisiteStringId2 != 2_000_000 + rule.TechnologyId)
                    throw new InvalidDataException("An activated ability does not match its in-game button registration.");
            }
        }
        if (!changed || bytes > 128 * 1024 * 1024) throw new InvalidDataException("The package has no changes or exceeds its payload limit.");
        foreach (var dependency in package.Dependencies)
        {
            _ = CivilizationPackageManager.ResolveTarget(Path.GetFullPath("."), dependency.Key, true);
            if (dependency.Value is not null && !ValidHash(dependency.Value)) throw new InvalidDataException("An immutable dependency hash is invalid.");
        }
        foreach (var model in package.ModelReferences)
            if (model is null || model.GraphicsId < 0 || !ValidHash(model.Hash) || !IsModelPath("Data\\" + model.ModelName))
                throw new InvalidDataException("A source model reference is invalid.");
        foreach (var dependency in package.AssetDependencies)
        {
            _ = CivilizationPackageManager.ResolveTarget(Path.GetFullPath("."), "Data\\" + dependency.Key, true);
            if (!ValidHash(dependency.Value)) throw new InvalidDataException("A packed asset dependency hash is invalid.");
        }
    }
    internal static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed class CivilizationPackageFile
{
    public string RelativePath { get; set; } = "";
    public bool WholeFile { get; set; }
    public int ExpectedLength { get; set; }
    public string? StructuralHash { get; set; }
    public string? ExpectedHash { get; set; }
    public byte[]? Replacement { get; set; }
    public List<CivilizationPackagePatch> Patches { get; set; } = [];
}
public sealed class CivilizationPackagePatch
{
    public string SemanticTarget { get; set; } = "";
    public int Offset { get; set; }
    public int Length { get; set; }
    public byte[] Expected { get; set; } = [];
    public byte[] Replacement { get; set; } = [];
    public int IdentityOffset { get; set; }
    public byte[] IdentityBytes { get; set; } = [];
}
public sealed record CivilizationPackageModelReference(int GraphicsId, string ModelName, string Hash);

public static class CivilizationInstallerPackageCodec
{
    public const int FooterSize = 64;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EDMWCIVINSTALL01");
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 64 };

    public static byte[] Encode(CivilizationInstallerPackage package)
    {
        CivilizationInstallerPackage.Validate(package);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("civilization-package.json", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, package, JsonOptions);
        }
        if (output.Length > 128 * 1024 * 1024) throw new InvalidDataException("The compressed package exceeds 128 MB.");
        return output.ToArray();
    }
    public static CivilizationInstallerPackage Decode(byte[] payload)
    {
        if (payload.Length is < 1 or > 128 * 1024 * 1024) throw new InvalidDataException("The package size is invalid.");
        using var stream = new MemoryStream(payload, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "civilization-package.json" || archive.Entries[0].Length > 384L * 1024 * 1024)
            throw new InvalidDataException("The package contents are invalid.");
        using var manifest = archive.Entries[0].Open();
        var package = JsonSerializer.Deserialize<CivilizationInstallerPackage>(manifest, JsonOptions) ?? throw new InvalidDataException("The package manifest is empty.");
        CivilizationInstallerPackage.Validate(package);
        return package;
    }
    public static CivilizationInstallerPackage ReadExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < FooterSize) throw new InvalidDataException("This installer does not contain a civilization package.");
        stream.Position = stream.Length - FooterSize;
        Span<byte> footer = stackalloc byte[FooterSize];
        stream.ReadExactly(footer);
        if (!footer[..16].SequenceEqual(Magic)) throw new InvalidDataException("The civilization package marker is missing.");
        var offset = BinaryPrimitives.ReadInt64LittleEndian(footer[16..24]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(footer[24..32]);
        if (offset < 0 || length is < 1 or > 128 * 1024 * 1024 || length != stream.Length - FooterSize - offset)
            throw new InvalidDataException("The embedded package boundaries are invalid.");
        var bytes = new byte[(int)length];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(footer[32..64])) throw new InvalidDataException("The embedded package failed integrity verification.");
        return Decode(bytes);
    }
    internal static void Append(Stream destination, byte[] payload)
    {
        var offset = destination.Position;
        destination.Write(payload);
        Span<byte> footer = stackalloc byte[FooterSize];
        Magic.CopyTo(footer);
        BinaryPrimitives.WriteInt64LittleEndian(footer[16..24], offset);
        BinaryPrimitives.WriteInt64LittleEndian(footer[24..32], payload.Length);
        SHA256.HashData(payload).CopyTo(footer[32..64]);
        destination.Write(footer);
    }
}
