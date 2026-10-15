using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationInstallChange(string CivilizationName, string? EntityName,
    string Key, string Label, string Before, string After, string Scope, string TargetFile)
{
    public string Description => $"{CivilizationName}{(EntityName is null ? "" : " / " + EntityName)}: {Label}: {Before} → {After} · {Scope}";
}

public sealed record CivilizationInstallResult(bool Success, string Message, string? BackupPath = null, int FileCount = 0);

/// <summary>An immutable review snapshot. Patched bytes and originals are kept private.</summary>
public sealed class CivilizationInstallPlan
{
    public string GamePath { get; }
    public string ProjectName { get; }
    public IReadOnlyList<CivilizationInstallChange> Changes { get; }
    public IReadOnlyList<string> Issues { get; }
    public IReadOnlyDictionary<string, string?> OriginalHashes { get; }
    public bool CanInstall => Issues.Count == 0 && FileCount > 0;
    public int FileCount => Files.Count;
    internal IReadOnlyList<CivilizationPlannedFile> Files { get; }
    internal IReadOnlyDictionary<string, string?> Dependencies { get; }
    internal IReadOnlyList<CivilizationModelDependency> Models { get; }
    internal CivilizationEditProject PreparedProject { get; }

    internal CivilizationInstallPlan(string gamePath, string projectName,
        List<CivilizationInstallChange> changes, List<string> issues,
        List<CivilizationPlannedFile> files, Dictionary<string, string?> dependencies,
        List<CivilizationModelDependency> models, CivilizationEditProject preparedProject)
    {
        GamePath = gamePath; ProjectName = projectName;
        PreparedProject = preparedProject;
        Changes = changes.AsReadOnly(); Issues = issues.AsReadOnly(); Files = files.AsReadOnly(); Models = models.AsReadOnly();
        Dependencies = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(dependencies, StringComparer.OrdinalIgnoreCase));
        var hashes = new Dictionary<string, string?>(dependencies, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) hashes[file.RelativePath] = file.OriginalHash;
        OriginalHashes = new ReadOnlyDictionary<string, string?>(hashes);
    }
}

internal sealed record CivilizationPlannedFile(string RelativePath, byte[]? Original, byte[] Patched)
{
    public string? OriginalHash { get; } = Original is null ? null : CivilizationGameInstaller.Hash(Original);
    public string InstalledHash { get; } = CivilizationGameInstaller.Hash(Patched);
}
internal sealed record CivilizationModelDependency(int GraphicsId, string ModelName, string Hash);

/// <summary>Reviewed, hash-guarded installation of verified existing records.</summary>
public static class CivilizationGameInstaller
{
    private const string StorageName = ".edmw-civilization-editor";
    private const string ObjectPath = "Data\\db\\dbobjects.dat";
    private const string CivilizationPath = "Data\\db\\dbphoenixcivtable.dat";
    private const string GraphicsPath = "Data\\db\\dbgraphics.dat";
    private const string TechnologyPath = "Data\\db\\dbtechtree.dat";
    private const string EffectsPath = "Data\\db\\dbeffects.dat";
    private const string EventsPath = "Data\\db\\dbevents.dat";
    private const string ButtonsPath = "Data\\db\\dbbuttons.dat";
    private const string ExecutablePath = "Empires_DMW.exe";
    public const string SupportedExecutableSha256 = "9A713D5034D0CFFEDADD2D50266F15769F905E298BDC4F87CB90DDF6CAAF4AE3";
    private const int ObjectStride = 0x790;
    private const int CivilizationStride = 344;
    private const int MaximumFileBytes = 32 * 1024 * 1024;

    public static CivilizationInstallPlan BuildPlan(CivilizationEditProject project, string gamePath, bool forExport = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        project = project.Clone();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var changes = new List<CivilizationInstallChange>();
        var issues = new List<string>();
        var files = new List<CivilizationPlannedFile>();
        var dependencies = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var modelDependencies = new List<CivilizationModelDependency>();
        try
        {
            project.Validate();
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected game folder does not exist.");
            if (!forExport && GameIsRunning(root)) issues.Add("Close Empires: Dawn of the Modern World before installing this project.");
            var executableHash = FileHash(ResolveTarget(root, ExecutablePath, true));
            dependencies[ExecutablePath] = executableHash;
            if (!EqualHash(executableHash, SupportedExecutableSha256)) issues.Add("This executable version has not been verified for civilization installation. Choose the supported EDMW installation; the game executable will not be modified.");
            if (File.Exists(CurrentManifestPath(root))) issues.Add("A civilization package is already installed. Restore it before installing another project.");
            var originalObjects = ReadBounded(ResolveTarget(root, ObjectPath));
            var originalCivilizations = ReadBounded(ResolveTarget(root, CivilizationPath));
            var objects = (byte[])originalObjects.Clone();
            var civilizations = (byte[])originalCivilizations.Clone();
            byte[]? originalTechnologies = null;
            byte[]? technologies = null;
            byte[]? originalEffects = null;
            byte[]? effects = null;
            CivilizationAbilityData? effectData = null;
            Dictionary<int, CivilizationTechnologyRecord>? technologyRecords = null;
            CivilizationGameAssets? referenceAssets = null;
            dependencies[ObjectPath] = Hash(originalObjects);
            dependencies[CivilizationPath] = Hash(originalCivilizations);
            ValidateCountedTable(originalObjects, ObjectStride, "object");
            ValidateCountedTable(originalCivilizations, CivilizationStride, "civilization");
            var sharedWrites = new Dictionary<string, (string Value, string Owner)>(StringComparer.Ordinal);
            var producerAdditions = new Dictionary<int, IReadOnlyList<int>>();
            var progressionMenus = new Dictionary<(int TechnologyId, int CivilizationRow), HashSet<int>>();
            var replacements = new List<(CivilizationDraft Civilization, CivilizationEntityDraft Entity, CivilizationModelAsset Asset)>();
            var unitAttachments = new List<(CivilizationDraft Civilization, CivilizationEntityDraft Entity)>();

            void EnsureTechnologyData()
            {
                if (originalTechnologies is not null) return;
                originalTechnologies = ReadBounded(ResolveTarget(root, TechnologyPath));
                technologies = (byte[])originalTechnologies.Clone();
                technologyRecords = CivilizationTechnologyBindings.ReadRecords(originalTechnologies).ToDictionary(record => record.TechId);
                dependencies[TechnologyPath] = Hash(originalTechnologies);
            }

            void EnsureEffectData()
            {
                if (effectData is not null) return;
                EnsureTechnologyData();
                originalEffects = ReadBounded(ResolveTarget(root, EffectsPath));
                effects = (byte[])originalEffects.Clone();
                var events = ReadBounded(ResolveTarget(root, EventsPath, true));
                referenceAssets ??= new CivilizationGameAssets(root);
                byte[]? powers = null;
                try { powers = referenceAssets.ReadAssetFile("db\\dbcivpowers.dat"); }
                catch (FileNotFoundException) { }
                effectData = new(originalObjects, technologyRecords!.Values.ToArray(), events, originalEffects, civilizationsDatabase: originalCivilizations, powersDatabase: powers);
                dependencies[EffectsPath] = Hash(originalEffects);
                dependencies[EventsPath] = Hash(events);
            }

            CivilizationTechnologyRecord RequireOwnedTechnology(int techId, int civilizationRow, CivilizationCategory? nodeCategory,
                bool allowProgressionCandidate = false)
            {
                EnsureTechnologyData();
                if (!technologyRecords!.TryGetValue(techId, out var technology)) throw new InvalidDataException("The source technology ID is not present in the selected game's table.");
                // Authorize source identities against their original roster even
                // when the project also changes the civilization's age support.
                var ages = CivilizationCivilizationBindings.ReadValues(originalCivilizations, civilizationRow);
                var lower = int.Parse(ages["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture);
                var upper = int.Parse(ages["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture);
                var available = nodeCategory switch
                {
                    CivilizationCategory.Technologies => CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(originalObjects, technologyRecords!.Values.ToArray(), civilizationRow, lower, upper)
                        .Concat(CivilizationTechnologyBindings.GetCivilizationAgeAdvanceRecords(originalObjects, technologyRecords!.Values.ToArray(), civilizationRow, lower, upper))
                        .Any(record => record.TechId == techId),
                    CivilizationCategory.Abilities => CivilizationTechnologyBindings.GetCivilizationAbilityRecords(originalObjects, technologyRecords!.Values.ToArray(), civilizationRow, lower, upper).Any(record => record.TechId == techId),
                    _ => CivilizationTechnologyBindings.IsTechnologyForCivilization(originalObjects, originalTechnologies!, techId, civilizationRow, lower, upper)
                };
                if (!available && allowProgressionCandidate)
                {
                    var effectiveAges = CivilizationCivilizationBindings.ReadValues(civilizations, civilizationRow);
                    lower = int.Parse(effectiveAges["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture);
                    upper = int.Parse(effectiveAges["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture);
                    available = nodeCategory == CivilizationCategory.Technologies
                        ? CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(originalObjects,
                            technologyRecords.Values.ToArray(), civilizationRow, lower, upper, true).Any(record => record.TechId == techId)
                        : nodeCategory is null && new[] { CivilizationCategory.Units, CivilizationCategory.Buildings }
                            .SelectMany(category => CivilizationTechnologyBindings.GetCivilizationProgressionObjectIds(originalObjects,
                                technologyRecords.Values.ToArray(), civilizationRow, category, lower, upper, true)
                                .Select(objectId => (ObjectId: objectId, Category: category)))
                            .Any(item => CivilizationObjectBindings.ReadTechnologyId(originalObjects, item.ObjectId, item.Category) == techId);
                }
                if (!available) throw new InvalidDataException("The source technology is not available to this civilization in its supported ages. Choose a source from this civilization's verified roster.");
                return technology;
            }

            bool HasProgressionMembership(CivilizationEntityDraft entity) => entity.Properties.Any(pair =>
                CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, pair.Key) &&
                bool.TryParse(pair.Value, out _));

            void WriteProgressionMembership(CivilizationDraft civilization, CivilizationEntityDraft entity,
                int technologyId, string key, string value)
            {
                if (!bool.TryParse(value, out var included))
                    throw new InvalidDataException("Progression inclusion must be True or False.");
                var row = civilization.SourceRowIndex!.Value;
                var normalized = included.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var owner = civilization.Name + " / " + entity.Name;
                if (!RegisterWrite(sharedWrites, $"technology:{technologyId}:membership:{row}", normalized, owner, issues)) return;
                var before = CivilizationTechnologyBindings.ReadProgressionIncluded(technologyRecords![technologyId], row)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                CivilizationTechnologyBindings.WriteProgressionIncluded(technologies!, technologyId, row, included);
                changes.Add(new(civilization.Name, entity.Name, key, "Included in progression", before, normalized,
                    $"{civilization.Name} only; production or research technology {technologyId}", TechnologyPath));
            }

            void QueueProgressionProducers(CivilizationDraft civilization, CivilizationEntityDraft entity,
                int technologyId, string key, string value)
            {
                var row = civilization.SourceRowIndex!.Value;
                var ages = CivilizationCivilizationBindings.ReadValues(civilizations, row);
                var lower = int.Parse(ages["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture);
                var upper = int.Parse(ages["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture);
                var requested = CivilizationTechnologyBindings.ParseProgressionProducerIds(value);
                var choices = CivilizationTechnologyBindings.GetProgressionProducerChoices(originalObjects,
                    technologyRecords!.Values.ToArray(), technologyId, row, lower, upper).ToHashSet();
                if (requested.Any(id => !choices.Contains(id)))
                    throw new InvalidDataException("Choose an existing producer from this civilization with the original producer's object category, family and definition type.");
                var normalized = string.Join(", ", requested);
                if (!RegisterWrite(sharedWrites, $"technology:{technologyId}:producers:{row}", normalized,
                    civilization.Name + " / " + entity.Name, issues)) return;
                var prior = producerAdditions.GetValueOrDefault(technologyId, []);
                producerAdditions[technologyId] = prior.Concat(requested).Distinct().ToArray();
                foreach (var producerId in requested)
                    CivilizationTechnologyBindings.WriteProgressionProducerAction(objects, producerId, technologyId);
                var menuKey = (technologyId, row);
                if (!progressionMenus.TryGetValue(menuKey, out var menu)) progressionMenus.Add(menuKey, menu = []);
                menu.UnionWith(requested);
                changes.Add(new(civilization.Name, entity.Name, key, "Additional producer", "", normalized,
                    "Existing producers are preserved; the chosen civilization's producer can offer this item", TechnologyPath));
            }

            foreach (var civilization in project.Civilizations)
            {
                if (civilization.SourceCivilizationId is not int sourceId || civilization.SourceRowIndex is not int row)
                {
                    issues.Add($"{civilization.Name}: adding a new civilization record is not mapped. Use an existing source civilization.");
                    continue;
                }
                VerifyCivilizationIdentity(originalCivilizations, row, sourceId);
                var originalValues = CivilizationCivilizationBindings.ReadValues(originalCivilizations, row);
                foreach (var property in civilization.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && !IsDraftNote(pair.Key)))
                {
                    if (!CivilizationCivilizationBindings.CanWrite(property.Key))
                    {
                        issues.Add($"{civilization.Name}: '{FieldLabel(property.Key)}' is not mapped for game installation. Clear that override or keep this project as a draft.");
                        continue;
                    }
                    var owner = civilization.Name;
                    if (!RegisterWrite(sharedWrites, $"civ:{row}:{property.Key}", property.Value, owner, issues)) continue;
                    try
                    {
                        CivilizationCivilizationBindings.WriteValue(civilizations, row, property.Key, property.Value);
                        originalValues.TryGetValue(property.Key, out var before);
                        changes.Add(new(civilization.Name, null, property.Key, FieldLabel(property.Key), before ?? "", property.Value, "Existing civilization record", CivilizationPath));
                    }
                    catch (Exception exception) when (Expected(exception)) { issues.Add($"{owner}: {exception.Message}"); }
                }
                try
                {
                    CivilizationCivilizationBindings.ValidateRow(civilizations, row);
                    foreach (var property in civilization.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && CivilizationCivilizationBindings.CanWrite(pair.Key)))
                    {
                        var expected = (byte[])civilizations.Clone();
                        CivilizationCivilizationBindings.WriteValue(expected, row, property.Key, property.Value);
                        if (!expected.AsSpan().SequenceEqual(civilizations)) issues.Add($"{civilization.Name}: overlapping civilization or age overrides disagree. Make the supported-age range and individual age bounds consistent.");
                    }
                }
                catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name}: {exception.Message}"); }

                foreach (var entity in civilization.Entities)
                {
                    var authoredProperties = entity.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && !IsDraftNote(pair.Key)).ToArray();
                    if ((entity.SourceObjectId is not null || entity.SourceTechnologyId is not null || entity.SourcePowerId is not null) && authoredProperties.Length == 0) continue;
                    if (entity.Category is CivilizationCategory.Technologies or CivilizationCategory.Abilities)
                    {
                        if (entity.SourceTechnologyId is not int technologyId)
                        {
                            issues.Add(entity.SourcePowerId is not null
                                ? $"{civilization.Name} / {entity.Name}: this power uses native behavior without a linked editable technology. Its source parameters are read-only."
                                : $"{civilization.Name} / {entity.Name}: creating a technology record is not mapped. Choose an existing technology from this civilization's roster.");
                            continue;
                        }
                        try
                        {
                            CivilizationTechnologyRecord node;
                            if (entity.SourcePowerId is int powerId)
                            {
                                EnsureEffectData();
                                var assignedPower = effectData!.GetPowerRecords(row).SingleOrDefault(power => power.Index == powerId);
                                if (assignedPower?.ActivationTechnologyId != technologyId || !technologyRecords!.TryGetValue(technologyId, out node!))
                                    throw new InvalidDataException("The source power and activation technology are not assigned to this civilization.");
                            }
                            else node = RequireOwnedTechnology(technologyId, row, entity.Category, HasProgressionMembership(entity));
                            var runtimeRules = CivilizationRuntimeEffects.RulesFor(civilization, entity);
                            if (runtimeRules.Count > 0)
                            {
                                if (!CivilizationTechnologyBindings.IsResearchRecord(node))
                                    throw new InvalidDataException("Runtime bonuses support nonrepeatable research technologies only.");
                                var target = CivilizationRuntimeEffects.PathFor(entity);
                                if (File.Exists(ResolveTarget(root, target)))
                                    throw new InvalidDataException("These runtime effects are already installed. Uninstall their mod before replacing it.");
                                files.Add(new(target, null, CivilizationRuntimeEffects.Encode(runtimeRules)));
                                foreach (var rule in runtimeRules)
                                {
                                    var field = CivilizationRuntimeEffects.Fields.Single(field => field.Resource == rule.Resource);
                                    changes.Add(new(civilization.Name, entity.Name, field.Key, field.Name + " on research completion", "0",
                                        rule.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        "Runtime prototype; this civilization only. Launch through Mod Studio. Uninstall removes future bonuses, not resources already awarded in saves.", target));
                                }
                            }
                            var originalTechnologyValues = CivilizationTechnologyBindings.ReadObjectValues(originalTechnologies!, technologyId, entity.Category);
                            foreach (var property in authoredProperties)
                            {
                                if (CivilizationRuntimeEffects.IsKey(property.Key)) continue;
                                if (CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, property.Key))
                                {
                                    if (entity.SourcePowerId is not null)
                                        throw new InvalidDataException("Civilization powers cannot be added to or removed from age progression.");
                                    WriteProgressionMembership(civilization, entity, technologyId, property.Key, property.Value);
                                    continue;
                                }
                                if (CivilizationTechnologyBindings.IsProgressionProducerKey(entity.Category, property.Key))
                                {
                                    if (entity.SourcePowerId is not null)
                                        throw new InvalidDataException("Civilization powers do not use progression producers.");
                                    QueueProgressionProducers(civilization, entity, technologyId, property.Key, property.Value);
                                    continue;
                                }
                                if (property.Key.Contains(".effect.", StringComparison.Ordinal))
                                {
                                    EnsureEffectData();
                                    if (!effectData!.CanWriteEffect(technologyId, entity.Category, property.Key))
                                        throw new InvalidDataException($"'{property.Key}' is not a verified effect linked to this definition.");
                                    var identity = property.Key[(property.Key.IndexOf('.') + 1)..];
                                    if (!RegisterWrite(sharedWrites, identity, property.Value, civilization.Name + " / " + entity.Name, issues)) continue;
                                    var beforeEffect = effectData.GetValues(node, entity.Category).GetValueOrDefault(property.Key, "");
                                    effectData.WriteEffectValue(effects!, technologyId, entity.Category, property.Key, property.Value);
                                    changes.Add(new(civilization.Name, entity.Name, property.Key, FieldLabel(property.Key), beforeEffect, property.Value,
                                        "Shared modifier effect; affects every linked technology and target", EffectsPath));
                                    continue;
                                }
                                if (entity.SourcePowerId is not null || !CivilizationTechnologyBindings.CanWriteObject(entity.Category, property.Key))
                                {
                                    issues.Add($"{civilization.Name} / {entity.Name}: '{FieldLabel(property.Key)}' is not mapped for technology installation. Clear that override or keep this project as a draft.");
                                    continue;
                                }
                                var field = property.Key[(property.Key.IndexOf('.') + 1)..];
                                if (!RegisterWrite(sharedWrites, $"technology:{technologyId}:{field}", property.Value, civilization.Name + " / " + entity.Name, issues)) continue;
                                CivilizationTechnologyBindings.WriteObjectValue(technologies!, technologyId, entity.Category, property.Key, property.Value);
                                changes.Add(new(civilization.Name, entity.Name, property.Key, FieldLabel(property.Key), originalTechnologyValues.GetValueOrDefault(property.Key, ""), property.Value,
                                    $"Shared technology {technologyId}; affects every object or research action using this record", TechnologyPath));
                            }
                        }
                        catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name} / {entity.Name}: {exception.Message}"); }
                        continue;
                    }
                    if (entity.SourceObjectId is not int objectRow)
                    {
                        issues.Add($"{civilization.Name} / {entity.Name}: creating a new {CategoryName(entity.Category)} record is not mapped. Choose an existing catalog entry.");
                        continue;
                    }
                    if (entity.Category is not (CivilizationCategory.Units or CivilizationCategory.Buildings))
                    {
                        if (entity.Properties.Any(pair => !string.IsNullOrWhiteSpace(pair.Value) && !IsDraftNote(pair.Key)))
                            issues.Add($"{civilization.Name} / {entity.Name}: existing {entity.Category} records are not mapped for installation.");
                        continue;
                    }
                    var values = CivilizationObjectBindings.ReadValues(originalObjects, objectRow, entity.Category);
                    EnsureTechnologyData();
                    var civilizationAges = CivilizationCivilizationBindings.ReadValues(originalCivilizations, row);
                    var initialAge = int.Parse(civilizationAges["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture);
                    var finalAge = int.Parse(civilizationAges["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture);
                    var originalMember = CivilizationTechnologyBindings.GetCivilizationObjectIds(originalObjects,
                        technologyRecords!.Values.ToArray(), row, entity.Category, initialAge, finalAge).Contains(objectRow);
                    var effectiveAges = CivilizationCivilizationBindings.ReadValues(civilizations, row);
                    var effectiveInitialAge = int.Parse(effectiveAges["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture);
                    var effectiveFinalAge = int.Parse(effectiveAges["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture);
                    if (!originalMember && !(HasProgressionMembership(entity) &&
                        CivilizationTechnologyBindings.GetCivilizationProgressionObjectIds(originalObjects,
                            technologyRecords.Values.ToArray(), row, entity.Category, effectiveInitialAge, effectiveFinalAge, true).Contains(objectRow)))
                    {
                        issues.Add($"{civilization.Name} / {entity.Name}: the source object is not in this civilization's verified roster for its supported ages.");
                        continue;
                    }
                    foreach (var property in entity.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && !IsDraftNote(pair.Key)))
                    {
                        if (property.Key == CivilizationUnitEffects.PropertyKey)
                        {
                            try
                            {
                                var unitEffects = CivilizationUnitEffects.Read(entity).Where(effect => effect.Enabled).ToArray();
                                if (unitEffects.Length == 0) continue;
                                var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(originalObjects);
                                foreach (var effect in unitEffects)
                                    if (effect.TargetObjectId >= count || originalObjects[4 + effect.TargetObjectId * 1936 + 232] == 0)
                                        throw new InvalidDataException("An effect target is absent from the game catalog.");
                                var target = CivilizationRuntimeEffects.PathFor(entity);
                                if (File.Exists(ResolveTarget(root, target)))
                                    throw new InvalidDataException("These runtime effects are already installed. Uninstall their mod before replacing it.");
                                unitAttachments.Add((civilization, entity));
                                foreach (var effect in unitEffects)
                                    changes.Add(new(civilization.Name, entity.Name, property.Key, effect.Name, "None",
                                        CivilizationUnitEffects.Describe(effect),
                                        effect.Trigger == CivilizationUnitEffectTrigger.Proximity
                                            ? "Runtime prototype; same-player units, once per effect regardless of nearby source count. Launch through Mod Studio. Live gameplay, save/load and multiplayer unverified."
                                            : "Session-only runtime ability with an in-game button. Active bonuses reset on load. Launch through Mod Studio. Live gameplay and multiplayer unverified.", target));
                            }
                            catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name} / {entity.Name}: {exception.Message}"); }
                            continue;
                        }
                        if (CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, property.Key))
                        {
                            try
                            {
                                var rawTechId = CivilizationObjectBindings.ReadTechnologyId(originalObjects, objectRow, entity.Category);
                                if (rawTechId > int.MaxValue) throw new InvalidDataException("The object's production technology cannot be resolved.");
                                _ = RequireOwnedTechnology((int)rawTechId, row, null, HasProgressionMembership(entity));
                                WriteProgressionMembership(civilization, entity, (int)rawTechId, property.Key, property.Value);
                            }
                            catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name} / {entity.Name}: {exception.Message}"); }
                            continue;
                        }
                        if (CivilizationTechnologyBindings.IsProgressionProducerKey(entity.Category, property.Key))
                        {
                            try
                            {
                                var rawTechId = CivilizationObjectBindings.ReadTechnologyId(originalObjects, objectRow, entity.Category);
                                if (rawTechId > int.MaxValue) throw new InvalidDataException("The object's production technology cannot be resolved.");
                                _ = RequireOwnedTechnology((int)rawTechId, row, null, HasProgressionMembership(entity));
                                QueueProgressionProducers(civilization, entity, (int)rawTechId, property.Key, property.Value);
                            }
                            catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name} / {entity.Name}: {exception.Message}"); }
                            continue;
                        }
                        if (property.Key is "unit.modelAsset" or "building.modelAsset")
                        {
                            var asset = project.GetModelAsset(property.Value);
                            if (asset is null) { issues.Add($"{entity.Name}: replacement model asset is missing."); continue; }
                            if (asset.Format != "cem") { issues.Add($"{entity.Name}: OBJ replacements require conversion to the game's CEM format before installation."); continue; }
                            try { asset.Validate(); _ = CivilizationModelReader.Read(asset.Data, asset.OriginalFileName); replacements.Add((civilization, entity, asset)); }
                            catch (Exception exception) when (Expected(exception)) { issues.Add($"{entity.Name}: replacement CEM is not valid: {exception.Message}"); }
                            continue;
                        }
                        if (CivilizationTechnologyBindings.CanWriteObject(entity.Category, property.Key))
                        {
                            try
                            {
                                var rawTechId = CivilizationObjectBindings.ReadTechnologyId(originalObjects, objectRow, entity.Category);
                                if (rawTechId > int.MaxValue) throw new InvalidDataException("The object's production technology cannot be resolved.");
                                _ = RequireOwnedTechnology((int)rawTechId, row, null, HasProgressionMembership(entity));
                                var technologyOwner = civilization.Name + " / " + entity.Name;
                                var field = property.Key[(property.Key.IndexOf('.') + 1)..];
                                if (!RegisterWrite(sharedWrites, $"technology:{rawTechId}:{field}", property.Value, technologyOwner, issues)) continue;
                                var before = CivilizationTechnologyBindings.ReadObjectValues(originalTechnologies!, (int)rawTechId, entity.Category).GetValueOrDefault(property.Key, "");
                                CivilizationTechnologyBindings.WriteObjectValue(technologies!, (int)rawTechId, entity.Category, property.Key, property.Value);
                                changes.Add(new(civilization.Name, entity.Name, property.Key, FieldLabel(property.Key), before, property.Value,
                                    $"Shared production technology {rawTechId}; affects every object using this technology", TechnologyPath));
                            }
                            catch (Exception exception) when (Expected(exception)) { issues.Add($"{civilization.Name} / {entity.Name}: {exception.Message}"); }
                            continue;
                        }
                        if (!CivilizationObjectBindings.CanWrite(entity.Category, property.Key))
                        {
                            issues.Add($"{civilization.Name} / {entity.Name}: '{FieldLabel(property.Key)}' is not mapped for installation. Clear that override or keep this project as a draft.");
                            continue;
                        }
                        var owner = civilization.Name + " / " + entity.Name;
                        if (!RegisterWrite(sharedWrites, $"object:{objectRow}:{property.Key}", property.Value, owner, issues)) continue;
                        try
                        {
                            if (property.Key.EndsWith(".graphicsId", StringComparison.Ordinal))
                            {
                                referenceAssets ??= new CivilizationGameAssets(root);
                                var graphicsBytes = ReadBounded(ResolveTarget(root, GraphicsPath, true));
                                if (!uint.TryParse(property.Value, out var graphics) || graphicsBytes.Length < 4 || graphics >= BinaryPrimitives.ReadUInt32LittleEndian(graphicsBytes)) throw new InvalidDataException("The graphics reference points outside the selected game's graphics table.");
                                var model = referenceAssets.FindModelName((int)graphics) ?? throw new InvalidDataException("This graphics reference has no verified native model association.");
                                dependencies[GraphicsPath] = Hash(graphicsBytes);
                                modelDependencies.Add(new((int)graphics, model, Hash(referenceAssets.ReadModel(model))));
                            }
                            if (property.Key.EndsWith(".icon", StringComparison.Ordinal))
                            {
                                var buttons = ReadBounded(ResolveTarget(root, ButtonsPath, true));
                                if (!uint.TryParse(property.Value, out var icon) || buttons.Length < 4 || icon >= BinaryPrimitives.ReadUInt32LittleEndian(buttons)) throw new InvalidDataException("The icon reference points outside the selected game's button table.");
                                dependencies[ButtonsPath] = Hash(buttons);
                            }
                            CivilizationObjectBindings.WriteValue(objects, objectRow, entity.Category, property.Key, property.Value);
                            values.TryGetValue(property.Key, out var before);
                            changes.Add(new(civilization.Name, entity.Name, property.Key, FieldLabel(property.Key), before ?? "", property.Value,
                                $"Global object record {objectRow}; affects every civilization using this record", ObjectPath));
                        }
                        catch (Exception exception) when (Expected(exception)) { issues.Add($"{owner}: {exception.Message}"); }
                    }
                    if (entity.Category is CivilizationCategory.Units or CivilizationCategory.Buildings)
                    {
                        var prefix = entity.Category == CivilizationCategory.Units ? "unit" : "building";
                        var finalValues = CivilizationObjectBindings.ReadValues(objects, objectRow, entity.Category);
                        if (finalValues.TryGetValue(prefix + ".minimumRange", out var minimum) && finalValues.TryGetValue(prefix + ".attackRange", out var maximum) &&
                            double.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture) > double.Parse(maximum, System.Globalization.CultureInfo.InvariantCulture))
                            issues.Add($"{entity.Name}: the minimum attack range exceeds the resulting maximum attack range, including inherited game values.");
                    }
                }
            }

            // Age range aliases overlap the same bytes. Check all requested
            // values against the final buffer, including repeated source slots.
            foreach (var civilization in project.Civilizations.Where(civilization => civilization.SourceRowIndex is not null && civilization.SourceCivilizationId is not null))
                foreach (var property in civilization.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && CivilizationCivilizationBindings.CanWrite(pair.Key)))
                {
                    var expected = (byte[])civilizations.Clone();
                    CivilizationCivilizationBindings.WriteValue(expected, civilization.SourceRowIndex!.Value, property.Key, property.Value);
                    if (!expected.AsSpan().SequenceEqual(civilizations)) issues.Add($"{civilization.Name}: shared or overlapping civilization overrides disagree. Make every override for this source slot consistent.");
                }

            if (technologies is not null)
            {
                try
                {
                    CivilizationTechnologyBindings.ValidateAgeIntervals(technologies);
                    var resultingRecords = CivilizationTechnologyBindings.ReadRecords(technologies);
                    foreach (var civilization in project.Civilizations.Where(civilization => civilization.SourceRowIndex is not null))
                    {
                        var row = civilization.SourceRowIndex!.Value;
                        foreach (var entity in civilization.Entities.Where(entity => entity.Properties.Any(pair =>
                            CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, pair.Key) &&
                            bool.TryParse(pair.Value, out var included) && included)))
                        {
                            var technologyId = entity.SourceTechnologyId ?? (entity.SourceObjectId is int sourceObjectId
                                ? checked((int)CivilizationObjectBindings.ReadTechnologyId(originalObjects, sourceObjectId, entity.Category)) : -1);
                            if (technologyId >= 0 && technologyRecords!.TryGetValue(technologyId, out var originalTechnology))
                            {
                                var originalAges = CivilizationCivilizationBindings.ReadValues(civilizations, row);
                                var choices = CivilizationTechnologyBindings.GetProgressionProducerChoices(originalObjects,
                                    technologyRecords.Values.ToArray(), technologyId, row,
                                    int.Parse(originalAges["age.initialAge"], System.Globalization.CultureInfo.InvariantCulture),
                                    int.Parse(originalAges["age.finalAge"], System.Globalization.CultureInfo.InvariantCulture));
                                foreach (var producerId in choices.Where(originalTechnology.ProducerObjectIds.Contains))
                                {
                                    CivilizationTechnologyBindings.WriteProgressionProducerAction(objects, producerId, technologyId);
                                    var originalMember = entity.SourceObjectId is int objectId
                                        ? CivilizationTechnologyBindings.GetCivilizationObjectIds(originalObjects, technologyRecords.Values.ToArray(),
                                            row, entity.Category, 0, 100).Contains(objectId)
                                        : CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(originalObjects,
                                            technologyRecords.Values.ToArray(), row, 0, 100, false).Any(record => record.TechId == technologyId);
                                    if (!originalMember)
                                    {
                                        var menuKey = (technologyId, row);
                                        if (!progressionMenus.TryGetValue(menuKey, out var menu)) progressionMenus.Add(menuKey, menu = []);
                                        menu.Add(producerId);
                                    }
                                }
                            }
                        }
                    }
                    if (progressionMenus.Count > 0)
                    {
                        var buttons = ReadBounded(ResolveTarget(root, ButtonsPath, true));
                        dependencies[ButtonsPath] = Hash(buttons);
                        foreach (var assignment in progressionMenus.OrderBy(pair => pair.Key.TechnologyId).ThenBy(pair => pair.Key.CivilizationRow))
                        {
                            resultingRecords = CivilizationTechnologyBindings.ReadRecords(technologies);
                            var technology = resultingRecords.Single(record => record.TechId == assignment.Key.TechnologyId);
                            var occupied = new HashSet<int>();
                            foreach (var producerId in assignment.Value)
                            {
                                var rawName = System.Text.Encoding.ASCII.GetString(objects, 4 + producerId * ObjectStride, 100);
                                var category = rawName.StartsWith("b ", StringComparison.OrdinalIgnoreCase)
                                    ? CivilizationCategory.Buildings : CivilizationCategory.Units;
                                var actorActions = CivilizationObjectDetails.ReadActionTechnologyIds(objects, producerId, category).ToHashSet();
                                foreach (var other in resultingRecords.Where(record => record.TechId != technology.TechId &&
                                    actorActions.Contains((uint)record.TechId) && record.AllowsCivilizationRow(assignment.Key.CivilizationRow) &&
                                    !record.EditorOnly && !record.IsEpochDefinition && record.StartAge <= technology.EndAge && record.EndAge >= technology.StartAge))
                                    occupied.Add(CivilizationTechnologyBindings.ReadProgressionButtonSlot(other, assignment.Key.CivilizationRow, buttons));
                            }
                            var slot = CivilizationTechnologyBindings.ReadProgressionButtonSlot(technology, assignment.Key.CivilizationRow, buttons);
                            if (slot is < 1 or > 41 || occupied.Contains(slot))
                                slot = Enumerable.Range(1, 41).FirstOrDefault(candidate => !occupied.Contains(candidate));
                            if (slot == 0) throw new InvalidDataException("The selected producer has no free progression menu position. Choose another compatible producer.");
                            CivilizationTechnologyBindings.WriteProgressionButtonSlot(technologies, technology.TechId, assignment.Key.CivilizationRow, slot);
                        }
                    }
                    if (producerAdditions.Count > 0)
                        technologies = CivilizationTechnologyBindings.WriteProgressionProducers(technologies, producerAdditions);
                    resultingRecords = CivilizationTechnologyBindings.ReadRecords(technologies);
                    foreach (var civilization in project.Civilizations.Where(civilization => civilization.SourceRowIndex is not null))
                        foreach (var entity in civilization.Entities.Where(entity => entity.Properties.Any(pair =>
                            CivilizationTechnologyBindings.IsProgressionMembershipKey(entity.Category, pair.Key) &&
                            bool.TryParse(pair.Value, out var included) && included)))
                        {
                            var row = civilization.SourceRowIndex!.Value;
                            var available = entity.SourceObjectId is int objectId
                                ? CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, resultingRecords, row, entity.Category, 0, 100).Contains(objectId)
                                : entity.SourceTechnologyId is int id && CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(objects,
                                    resultingRecords, row, 0, 100, false).Any(record => record.TechId == id);
                            if (!available) issues.Add($"{civilization.Name} / {entity.Name}: adding this item requires an enabled producer belonging to this civilization. Choose a compatible production or research location.");
                        }
                }
                catch (Exception exception) when (Expected(exception))
                { issues.Add("The resulting progression values cannot be installed. " + exception.Message); }
            }
            if (unitAttachments.Count > 0)
            {
                var requests = unitAttachments.SelectMany(item => CivilizationUnitEffects.Read(item.Entity)
                    .Where(effect => effect.Enabled && effect.Trigger == CivilizationUnitEffectTrigger.Activated)
                    .Select(effect => new CivilizationActivatedAbilityRequest(item.Civilization.SourceRowIndex!.Value, item.Entity, effect))).ToArray();
                if (requests.Length > 0)
                {
                    EnsureTechnologyData();
                    var buttons = ReadBounded(ResolveTarget(root, ButtonsPath, true));
                    var events = ReadBounded(ResolveTarget(root, EventsPath, true));
                    dependencies[ButtonsPath] = Hash(buttons);
                    dependencies[EventsPath] = Hash(events);
                    var built = CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies!, buttons, events, requests);
                    objects = built.Objects;
                    technologies = built.Technologies;
                    var registrations = built.Registrations.ToDictionary(registration => Guid.Parse(registration.EffectId));
                    foreach (var item in unitAttachments)
                        CivilizationUnitEffects.Write(item.Entity, CivilizationUnitEffects.Read(item.Entity).Select(effect =>
                            registrations.TryGetValue(Guid.Parse(effect.Id), out var registration)
                                ? effect with { NativeTechnologyId = registration.TechnologyId } : effect));
                }
                foreach (var item in unitAttachments)
                    files.Add(new(CivilizationRuntimeEffects.PathFor(item.Entity), null,
                        CivilizationRuntimeConfiguration.DataFor(item.Civilization, item.Entity)!));
            }
            if (!objects.AsSpan().SequenceEqual(originalObjects)) files.Add(new(ObjectPath, originalObjects, objects));
            if (!civilizations.AsSpan().SequenceEqual(originalCivilizations)) files.Add(new(CivilizationPath, originalCivilizations, civilizations));
            if (originalTechnologies is not null && technologies is not null && !technologies.AsSpan().SequenceEqual(originalTechnologies)) files.Add(new(TechnologyPath, originalTechnologies, technologies));
            if (originalEffects is not null && effects is not null && !effects.AsSpan().SequenceEqual(originalEffects)) files.Add(new(EffectsPath, originalEffects, effects));
            if (replacements.Count > 0)
            {
                dependencies[GraphicsPath] = FileHash(ResolveTarget(root, GraphicsPath, true));
                var assets = new CivilizationGameAssets(root);
                var scopes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var objectCount = BinaryPrimitives.ReadInt32LittleEndian(objects);
                for (var row = 0; row < objectCount; row++)
                {
                    var graphics = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(4 + row * ObjectStride + 0x130, 4));
                    var model = assets.FindModelName(graphics);
                    if (model is not null) scopes[model] = scopes.GetValueOrDefault(model) + 1;
                }
                foreach (var (civilization, entity, asset) in replacements)
                {
                    var graphics = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(4 + entity.SourceObjectId!.Value * ObjectStride + 0x130, 4));
                    var model = assets.FindModelName(graphics);
                    if (model is null) { issues.Add($"{entity.Name}: the source CEM model cannot be resolved from this graphics record. Choose a verified source model before installing."); continue; }
                    var target = "Data\\" + model;
                    var current = assets.ReadModel(model);
                    modelDependencies.Add(new(graphics, model, Hash(current)));
                    var path = ResolveTarget(root, target);
                    var existingFile = files.FirstOrDefault(file => file.RelativePath.Equals(target, StringComparison.OrdinalIgnoreCase));
                    if (existingFile is not null && existingFile.InstalledHash != asset.Sha256.ToUpperInvariant())
                    { issues.Add($"{entity.Name}: two different replacement models target '{target}'. Choose one replacement for this shared model."); continue; }
                    if (existingFile is null)
                    {
                        var original = File.Exists(path) ? ReadBounded(path) : null;
                        if (original is null || !original.AsSpan().SequenceEqual(asset.Data)) files.Add(new(target, original, (byte[])asset.Data.Clone()));
                    }
                    changes.Add(new(civilization.Name, entity.Name, entity.Category == CivilizationCategory.Units ? "unit.modelAsset" : "building.modelAsset", "Replacement model", model, asset.Name,
                        $"Global model replacement; referenced by {scopes.GetValueOrDefault(model)} object records", target));
                }
            }
            if (files.Count == 0 && issues.Count == 0) issues.Add("This project has no changed, mapped game values or model replacements to install.");
        }
        catch (Exception exception) when (Expected(exception)) { issues.Add(exception.Message); }
        return new(root, project.Name, changes, issues.Distinct().ToList(), files, dependencies, modelDependencies, project);
    }

    public static CivilizationInstallResult Install(CivilizationInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanInstall) return new(false, string.Join(Environment.NewLine, plan.Issues));
        string? backupPath = null;
        CivilizationInstallationManifest? manifest = null;
        FileStream? operationLock = null;
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnsureGameClosed(plan.GamePath);
            var storage = StoragePath(plan.GamePath);
            Directory.CreateDirectory(storage);
            operationLock = AcquireLock(storage);
            if (File.Exists(CurrentManifestPath(plan.GamePath))) throw new InvalidDataException("Restore the existing civilization installation before installing another project.");
            VerifyPlanCurrent(plan);
            var installId = Guid.NewGuid().ToString("N");
            backupPath = BackupDirectory(plan.GamePath, installId);
            Directory.CreateDirectory(backupPath);
            manifest = new() { InstallId = installId, GamePath = plan.GamePath, ProjectName = plan.ProjectName, Status = "Installing", CreatedUtc = DateTimeOffset.UtcNow };
            for (var i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                if (file.Original is not null) File.WriteAllBytes(Path.Combine(backupPath, i + ".original"), file.Original);
                File.WriteAllBytes(Path.Combine(backupPath, i + ".patched"), file.Patched);
                manifest.Files.Add(new() { RelativePath = file.RelativePath, OriginalHash = file.OriginalHash, InstalledHash = file.InstalledHash, BackupName = i + ".original" });
            }
            WriteManifest(plan.GamePath, manifest);
            for (var i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                var target = ResolveTarget(plan.GamePath, file.RelativePath);
                RequireHash(target, file.OriginalHash, "A game file changed after review; installation was cancelled.");
                RequireHash(Path.Combine(backupPath, i + ".patched"), file.InstalledHash, "A staged installation file changed; installation was cancelled.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(backupPath, i + ".patched"), target, true);
                applied.Add(file.RelativePath);
            }
            manifest.Status = "Installed";
            WriteManifest(plan.GamePath, manifest);
            File.WriteAllText(Path.Combine(backupPath, "manifest.json"), JsonSerializer.Serialize(manifest, CivilizationInstallerJsonContext.Default.CivilizationInstallationManifest));
            return new(true, $"Installed {plan.Files.Count} game files. Backups are available for Restore.", backupPath, plan.Files.Count);
        }
        catch (Exception exception) when (Expected(exception))
        {
            var recovery = manifest is null ? "" : RollBackInstallation(plan.GamePath, manifest, backupPath!, applied);
            return new(false, exception.Message + recovery, backupPath);
        }
        finally { operationLock?.Dispose(); }
    }

    public static CivilizationInstallResult Restore(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        string? backupPath = null;
        CivilizationInstallationManifest? manifest = null;
        FileStream? operationLock = null;
        var restored = new List<(string Target, byte[]? Prior, string? WrittenHash)>();
        var restoreStarted = false;
        var previousStatus = "";
        try
        {
            EnsureGameClosed(root);
            if (!File.Exists(CurrentManifestPath(root))) return new(false, "There is no civilization installation to restore.");
            operationLock = AcquireLock(StoragePath(root));
            manifest = ReadManifest(root);
            backupPath = BackupDirectory(root, manifest.InstallId);
            var restoreFiles = new List<(string Target, byte[]? Original, byte[]? Prior)>();
            foreach (var file in manifest.Files)
            {
                var target = ResolveTarget(root, file.RelativePath);
                var currentHash = FileHash(target);
                var incomplete = manifest.Status != "Installed";
                if (!EqualHash(currentHash, file.InstalledHash) && !(incomplete && EqualHash(currentHash, file.OriginalHash)))
                    throw new InvalidDataException($"'{file.RelativePath}' changed outside this installer. Restore will not overwrite those changes.");
                var original = file.OriginalHash is null ? null : ReadBounded(Path.Combine(backupPath, file.BackupName));
                if (!EqualHash(original is null ? null : Hash(original), file.OriginalHash)) throw new InvalidDataException("A backup file is missing or has changed. Restore was cancelled.");
                restoreFiles.Add((target, original, File.Exists(target) ? ReadBounded(target) : null));
            }
            previousStatus = manifest.Status;
            manifest.Status = "Restoring";
            WriteManifest(root, manifest);
            restoreStarted = true;
            foreach (var (target, original, prior) in restoreFiles)
            {
                RequireHash(target, prior is null ? null : Hash(prior), "A game file changed during Restore; restoration was cancelled.");
                WriteOrDelete(target, original);
                restored.Add((target, prior, original is null ? null : Hash(original)));
            }
            File.Delete(CurrentManifestPath(root));
            return new(true, $"Restored {restoreFiles.Count} original game files.", backupPath, restoreFiles.Count);
        }
        catch (Exception exception) when (Expected(exception))
        {
            var recovery = "";
            if (manifest is not null && restoreStarted)
            {
                try
                {
                    foreach (var (target, data, writtenHash) in restored.AsEnumerable().Reverse())
                    {
                        RequireHash(target, writtenHash, "A restored file changed externally and cannot be safely rolled back.");
                        WriteOrDelete(target, data);
                    }
                    manifest.Status = previousStatus; WriteManifest(root, manifest); recovery = " The previous files were recovered.";
                }
                catch (Exception rollback) when (Expected(rollback)) { manifest.Status = "RecoveryRequired"; TryWriteManifest(root, manifest); recovery = $" Recovery is incomplete: {rollback.Message}. Backups remain in '{backupPath}'."; }
            }
            return new(false, exception.Message + recovery, backupPath);
        }
        finally { operationLock?.Dispose(); }
    }

    private static void VerifyPlanCurrent(CivilizationInstallPlan plan)
    {
        foreach (var dependency in plan.Dependencies) RequireHash(ResolveTarget(plan.GamePath, dependency.Key, true), dependency.Value, "Game data changed after this installation was reviewed. Review the project again.");
        foreach (var file in plan.Files) RequireHash(ResolveTarget(plan.GamePath, file.RelativePath), file.OriginalHash, "A target game file changed after review. Review the project again.");
        if (plan.Models.Count == 0) return;
        var assets = new CivilizationGameAssets(plan.GamePath);
        foreach (var model in plan.Models)
            if (!string.Equals(assets.FindModelName(model.GraphicsId), model.ModelName, StringComparison.OrdinalIgnoreCase) || Hash(assets.ReadModel(model.ModelName)) != model.Hash)
                throw new InvalidDataException("A source model or graphics association changed after review. Review the replacement again.");
    }

    private static string RollBackInstallation(string root, CivilizationInstallationManifest manifest, string backupPath, HashSet<string> applied)
    {
        try
        {
            foreach (var file in manifest.Files.Where(file => applied.Contains(file.RelativePath)))
            {
                var target = ResolveTarget(root, file.RelativePath);
                var hash = FileHash(target);
                if (EqualHash(hash, file.OriginalHash)) continue;
                if (!EqualHash(hash, file.InstalledHash)) throw new InvalidDataException($"'{file.RelativePath}' changed during installation and cannot be safely rolled back.");
                var original = file.OriginalHash is null ? null : ReadBounded(Path.Combine(backupPath, file.BackupName));
                if (!EqualHash(original is null ? null : Hash(original), file.OriginalHash)) throw new InvalidDataException("A rollback backup has changed.");
                WriteOrDelete(target, original);
            }
            if (File.Exists(CurrentManifestPath(root))) File.Delete(CurrentManifestPath(root));
            return " Original game files were restored.";
        }
        catch (Exception rollback) when (Expected(rollback))
        {
            manifest.Status = "RecoveryRequired";
            TryWriteManifest(root, manifest);
            return $" Rollback is incomplete: {rollback.Message}. Backups remain in '{backupPath}'.";
        }
    }

    private static bool RegisterWrite(Dictionary<string, (string Value, string Owner)> writes, string key, string value, string owner, List<string> issues)
    {
        if (!writes.TryGetValue(key, out var previous)) { writes.Add(key, (value, owner)); return true; }
        if (previous.Value == value || EquivalentNumber(key, previous.Value, value)) return true;
        issues.Add($"Conflicting shared-record overrides: {previous.Owner} and {owner} request different values for the same game field. Make the overrides agree.");
        return false;
    }

    private static bool IsDraftNote(string key) => key.EndsWith(".notes", StringComparison.Ordinal);
    private static bool EquivalentNumber(string recordKey, string first, string second)
    {
        var key = recordKey[(recordKey.LastIndexOf(':') + 1)..];
        var field = Enum.GetValues<CivilizationCategory>().SelectMany(CivilizationSchema.GetFields).FirstOrDefault(definition => definition.Key == key || definition.Key.EndsWith("." + key, StringComparison.Ordinal));
        return field?.Kind == CivilizationFieldKind.Number &&
            decimal.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
            decimal.TryParse(second, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && a == b;
    }
    private static string FieldLabel(string key) => Enum.GetValues<CivilizationCategory>().SelectMany(CivilizationSchema.GetFields).FirstOrDefault(field => field.Key == key)?.Label ?? key;
    private static string CategoryName(CivilizationCategory category) => category.ToString().TrimEnd('s').ToLowerInvariant();
    private static void ValidateCountedTable(byte[] data, int stride, string name)
    {
        if (data.Length < 4) throw new InvalidDataException($"The {name} database header is missing.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count is <= 0 or > 100_000 || data.Length != 4L + (long)count * stride) throw new InvalidDataException($"The {name} database uses an unsupported record layout.");
    }
    private static void VerifyCivilizationIdentity(byte[] data, int row, int sourceId)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (row < 0 || row >= count) throw new InvalidDataException("A civilization source row is out of range.");
        var record = data.AsSpan(4 + row * CivilizationStride, CivilizationStride);
        if (BinaryPrimitives.ReadInt32LittleEndian(record) != sourceId || BinaryPrimitives.ReadInt32LittleEndian(record[4..]) != row)
            throw new InvalidDataException("A civilization source identity does not match the selected game's database.");
    }

    private static string ResolveTarget(string root, string relative, bool dependency = false)
    {
        var normalized = relative.Replace('/', '\\');
        var allowed = CivilizationRuntimeEffects.IsPath(normalized) || normalized.Equals(ObjectPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(CivilizationPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(TechnologyPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(EffectsPath, StringComparison.OrdinalIgnoreCase) ||
            (dependency && (normalized.Equals(GraphicsPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(ButtonsPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(EventsPath, StringComparison.OrdinalIgnoreCase) || normalized.Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase))) ||
            (normalized.StartsWith("Data\\models\\", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".cem", StringComparison.OrdinalIgnoreCase));
        if (!allowed || Path.IsPathRooted(normalized) || normalized.Contains(':') || normalized.Split('\\').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("The installation contains an invalid target path.");
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installation target leaves the selected game folder.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(target)!); parent.FullName.Length > root.Length; parent = parent.Parent!)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Installation into linked Data folders is unsupported.");
        return target;
    }
    private static string StoragePath(string root)
    {
        var path = Path.Combine(root, StorageName);
        CheckLinkedDirectories(root, path);
        return path;
    }
    private static string BackupDirectory(string root, string installId)
    {
        var path = Path.Combine(StoragePath(root), "transactions", installId);
        CheckLinkedDirectories(root, path);
        return path;
    }
    private static void CheckLinkedDirectories(string root, string path)
    {
        for (var directory = new DirectoryInfo(path); directory.FullName.Length > root.Length; directory = directory.Parent!)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Installation backups cannot use linked folders.");
    }
    private static string CurrentManifestPath(string root) => Path.Combine(StoragePath(root), "current.json");
    private static FileStream AcquireLock(string storage) => File.Open(Path.Combine(storage, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static bool GameIsRunning(string root)
    {
        var processes = Process.GetProcessesByName("Empires_DMW");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null || string.Equals(Path.GetFullPath(executable), Path.Combine(root, ExecutablePath), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void EnsureGameClosed(string root) { if (GameIsRunning(root)) throw new InvalidOperationException("Close Empires: Dawn of the Modern World before installing or restoring game files."); }
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string? FileHash(string path) { if (!File.Exists(path)) return null; using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool EqualHash(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static void RequireHash(string path, string? expected, string message) { if (!EqualHash(FileHash(path), expected)) throw new InvalidDataException(message + " File: " + Path.GetFileName(path)); }
    private static byte[] ReadBounded(string path) { using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); if (stream.Length > MaximumFileBytes) throw new InvalidDataException("A game file exceeds the supported installation size."); var data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data); return data; }
    private static void WriteOrDelete(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".edmw-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteManifest(string root, CivilizationInstallationManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CivilizationInstallerJsonContext.Default.CivilizationInstallationManifest);
        WriteOrDelete(CurrentManifestPath(root), bytes);
    }
    private static void TryWriteManifest(string root, CivilizationInstallationManifest manifest) { try { WriteManifest(root, manifest); } catch (Exception exception) when (Expected(exception)) { } }
    private static CivilizationInstallationManifest ReadManifest(string root)
    {
        var data = ReadBounded(CurrentManifestPath(root));
        var manifest = JsonSerializer.Deserialize(data, CivilizationInstallerJsonContext.Default.CivilizationInstallationManifest) ?? throw new InvalidDataException("The installation manifest is empty.");
        if (manifest.FormatVersion != 1 || !Guid.TryParseExact(manifest.InstallId, "N", out _) || !string.Equals(manifest.GamePath, root, StringComparison.OrdinalIgnoreCase) || manifest.Files is null || manifest.Files.Count is < 1 or > 10_000 ||
            manifest.Status is not ("Installing" or "Installed" or "Restoring" or "RecoveryRequired"))
            throw new InvalidDataException("The installation manifest is invalid or belongs to another game folder.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.RelativePath) || string.IsNullOrEmpty(file.BackupName)) throw new InvalidDataException("The installation manifest contains an empty file entry.");
            _ = ResolveTarget(root, file.RelativePath);
            if (!paths.Add(file.RelativePath) || !ValidHash(file.InstalledHash) || (file.OriginalHash is not null && !ValidHash(file.OriginalHash)) ||
                Path.GetFileName(file.BackupName) != file.BackupName || !file.BackupName.EndsWith(".original", StringComparison.Ordinal) || !int.TryParse(file.BackupName[..^9], out var index) || index < 0)
                throw new InvalidDataException("The installation manifest contains an invalid file or backup entry.");
        }
        return manifest;
    }
    private static bool ValidHash(string? hash) => hash is not null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception;
}

internal sealed class CivilizationInstallationManifest
{
    public int FormatVersion { get; set; } = 1;
    public string InstallId { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Status { get; set; } = "Installing";
    public DateTimeOffset CreatedUtc { get; set; }
    public List<CivilizationInstalledFile> Files { get; set; } = [];
}
internal sealed class CivilizationInstalledFile
{
    public string RelativePath { get; set; } = "";
    public string? OriginalHash { get; set; }
    public string InstalledHash { get; set; } = "";
    public string BackupName { get; set; } = "";
}
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CivilizationInstallationManifest))]
internal partial class CivilizationInstallerJsonContext : JsonSerializerContext;
