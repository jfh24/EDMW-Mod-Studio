using System.Buffers.Binary;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Assigns installation-local native command identities without changing a portable export.</summary>
internal static class CivilizationRuntimePackageRebaser
{
    internal static CivilizationInstallerPackage Rebase(CivilizationInstallerPackage package, byte[] currentTechnologies)
    {
        CivilizationInstallerPackage.Validate(package);
        var ownRules = package.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))
            .SelectMany(file => CivilizationRuntimeConfiguration.Decode(file.Replacement!).Units)
            .Where(rule => rule.Trigger != CivilizationUnitEffectTrigger.Proximity).OrderBy(rule => rule.TechnologyId).ToArray();
        if (ownRules.Length == 0) return package;
        var ownIds = ownRules.Select(rule => rule.TechnologyId).ToHashSet();
        var sourceTable = package.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        var sourceRecords = CivilizationTechnologyBindings.ReadRecords(sourceTable.Replacement!);
        var currentRecords = CivilizationTechnologyBindings.ReadRecords(currentTechnologies);
        CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(sourceRecords);
        CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(currentRecords);
        if (sourceTable.Original is { } baseline && CivilizationTechnologyBindings.ReadRecords(baseline).Any(record => ownIds.Contains(record.TechId)))
            throw new InvalidDataException("A custom ability command already exists in its exported source table.");

        // Reserve all incoming identities before allocating replacements. This
        // prevents a collision on 10000 from stealing the incoming 10001 node.
        var liveIds = currentRecords.Select(record => record.TechId).ToHashSet();
        var reserved = liveIds.Concat(sourceRecords.Select(record => record.TechId)).ToHashSet();
        var mapping = new Dictionary<int, int>();
        foreach (var rule in ownRules)
        {
            var assigned = rule.TechnologyId;
            if (liveIds.Contains(assigned))
            {
                assigned = Enumerable.Range(10000, 5000).FirstOrDefault(id => !reserved.Contains(id), -1);
                if (assigned < 0) throw new InvalidDataException("The installed game has no free native command identities for another ability package.");
            }
            mapping.Add(rule.TechnologyId, assigned);
            reserved.Add(assigned);
        }
        var storedIds = currentRecords.Select(record => ReadStoredId(currentTechnologies, record))
            .Concat(sourceRecords.Where(record => !ownIds.Contains(record.TechId)).Select(record => ReadStoredId(sourceTable.Replacement!, record)));
        var nextStoredId = checked(storedIds.Max() + 1);
        _ = checked(nextStoredId + ownRules.Length);
        var clone = JsonSerializer.Deserialize<CivilizationInstallerPackage>(
            JsonSerializer.SerializeToUtf8Bytes(package, CivilizationInstallerPackageCodec.JsonOptions), CivilizationInstallerPackageCodec.JsonOptions)
            ?? throw new InvalidDataException("The portable ability package could not be copied.");
        var table = clone.Files.Single(file => CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath));
        var ownRecords = new Dictionary<int, byte[]>();
        foreach (var record in sourceRecords.Where(record => ownIds.Contains(record.TechId)))
        {
            var bytes = sourceTable.Replacement!.AsSpan(record.Offset, record.Length).ToArray();
            var assigned = mapping[record.TechId];
            Put(bytes, 100, nextStoredId++);
            Put(bytes, 104, assigned);
            Put(bytes, 176, checked(1_000_000 + assigned));
            foreach (var offset in new[] { 232, 236, 240 }) Put(bytes, offset, checked(2_000_000 + assigned));
            ownRecords.Add(record.TechId, bytes);
        }
        var epochs = sourceRecords.Where(record => record.IsEpochDefinition).ToArray();
        using (var output = new MemoryStream())
        {
            Span<byte> header = stackalloc byte[4];
            for (var epoch = 0; epoch < epochs.Length; epoch++)
            {
                var groupEnd = epoch + 1 < epochs.Length ? epochs[epoch + 1].Offset - 4 : sourceTable.Replacement!.Length;
                var originalChildren = sourceRecords.Where(record => !record.IsEpochDefinition &&
                    record.Offset > epochs[epoch].Offset && record.Offset < groupEnd && !ownIds.Contains(record.TechId)).ToArray();
                var additions = sourceRecords.Where(record => ownIds.Contains(record.TechId) && mapping[record.TechId] / 1000 - 1 == epoch).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(header, checked(originalChildren.Length + additions.Length));
                output.Write(header);
                output.Write(sourceTable.Replacement!.AsSpan(epochs[epoch].Offset, epochs[epoch].Length));
                foreach (var record in originalChildren) output.Write(sourceTable.Replacement.AsSpan(record.Offset, record.Length));
                foreach (var record in additions) output.Write(ownRecords[record.TechId]);
            }
            table.Replacement = output.ToArray();
        }
        CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(table.Replacement);

        var objects = clone.Files.Single(file => CivilizationInstallerPackage.IsObjectPath(file.RelativePath)).Replacement!;
        foreach (var rule in ownRules)
        {
            var commandSource = rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility ? rule.TargetObjectId : rule.SourceObjectId;
            var category = CivilizationUnitEffectBindings.CommandSourceCategory(objects, commandSource);
            var recordOffset = CivilizationObjectBindings.ValidateRecord(objects, commandSource, category);
            var count = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(recordOffset + CivilizationObjectDetails.ActionTechnologyCountOffset));
            if (count is < 0 or > CivilizationObjectDetails.ActionTechnologyCount)
                throw new InvalidDataException("An ability source has an invalid native action count.");
            var found = false;
            for (var slot = 0; slot < count; slot++)
            {
                var offset = recordOffset + CivilizationObjectDetails.ActionTechnologyIdsOffset + slot * 4;
                if (BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(offset)) != rule.TechnologyId) continue;
                Put(objects, offset, mapping[rule.TechnologyId]);
                found = true;
            }
            if (!found) throw new InvalidDataException("An ability's exported source action is missing.");
        }
        var project = CivilizationEditProject.Deserialize(clone.ProjectJson);
        foreach (var entity in project.Civilizations.SelectMany(civ => civ.Entities))
        {
            if (!entity.Properties.ContainsKey(CivilizationUnitEffects.PropertyKey)) continue;
            CivilizationUnitEffects.Write(entity, CivilizationUnitEffects.Read(entity).Select(effect =>
                !effect.Enabled || effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? effect : effect with
                {
                    NativeTechnologyId = effect.NativeTechnologyId is int id && mapping.TryGetValue(id, out var assigned) ? assigned : effect.NativeTechnologyId,
                    NativeTargetTechnologyIds = effect.NativeTargetTechnologyIds?.ToDictionary(pair => pair.Key,
                        pair => mapping.GetValueOrDefault(pair.Value, pair.Value))
                }));
        }
        clone.ProjectJson = project.Serialize();
        foreach (var file in clone.Files.Where(file => CivilizationRuntimeEffects.IsPath(file.RelativePath)))
        {
            var configuration = CivilizationRuntimeConfiguration.Decode(file.Replacement!);
            if (!configuration.Units.Any(rule => rule.Trigger != CivilizationUnitEffectTrigger.Proximity)) continue;
            var remapped = configuration.Units.Select(rule => rule.Trigger == CivilizationUnitEffectTrigger.Proximity ? rule : rule with
            {
                TechnologyId = mapping[rule.TechnologyId],
                GrantFamilyTechnologyId = rule.GrantFamilyTechnologyId == 0 ? 0 : FamilyId(rule, configuration.Units, mapping)
            });
            file.Replacement = CivilizationRuntimeConfiguration.Encode(configuration.Research, remapped);
        }
        CivilizationInstallerPackage.Validate(clone);
        return clone;
    }

    private static int FamilyId(CivilizationRuntimeUnitRule rule, IReadOnlyList<CivilizationRuntimeUnitRule> rules, IReadOnlyDictionary<int, int> mapping) =>
        rules.Where(candidate => candidate.Trigger == CivilizationUnitEffectTrigger.ProximityAbility &&
            candidate.GrantFamilyTechnologyId == rule.GrantFamilyTechnologyId)
            .Select(candidate => mapping[candidate.TechnologyId]).Min();

    private static int ReadStoredId(byte[] table, CivilizationTechnologyRecord record) =>
        BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(record.Offset + 100));

    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
}
