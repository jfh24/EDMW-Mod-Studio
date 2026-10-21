using System.Buffers.Binary;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// The independently owned portion of an ability export's database snapshot.
/// Only registered command additions and the ordinary mapped fields compose;
/// unrelated variable-record edits retain exclusive whole-file ownership.
/// </summary>
internal sealed class CivilizationAbilityTableComposition
{
    private readonly string path;
    private readonly IReadOnlyList<(int Id, int Epoch, byte[] Bytes)> commands;
    private readonly IReadOnlyDictionary<int, int[]> actions;

    private CivilizationAbilityTableComposition(string path, byte[] original,
        IReadOnlySet<int> commandIds, IReadOnlyList<CivilizationPackagePatch> patches,
        IReadOnlyList<(int Id, int Epoch, byte[] Bytes)> commands,
        IReadOnlyDictionary<int, int[]> actions)
    {
        this.path = path;
        Original = original;
        CommandIds = commandIds;
        Patches = patches;
        this.commands = commands;
        this.actions = actions;
    }

    internal byte[] Original { get; }
    internal IReadOnlySet<int> CommandIds { get; }
    internal IReadOnlyList<CivilizationPackagePatch> Patches { get; }

    internal static bool TryCreate(CivilizationInstallerPackage package, CivilizationPackageFile file,
        out CivilizationAbilityTableComposition composition)
    {
        composition = null!;
        if (!file.WholeFile || file.Replacement is null ||
            !(CivilizationInstallerPackage.IsObjectPath(file.RelativePath) || CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath)))
            return false;
        try
        {
            var rules = package.Files.Where(entry => CivilizationRuntimeEffects.IsPath(entry.RelativePath))
                .SelectMany(entry => CivilizationRuntimeConfiguration.Decode(entry.Replacement!).Units)
                .Where(rule => rule.Trigger != CivilizationUnitEffectTrigger.Proximity).ToArray();
            if (rules.Length == 0) return false;
            var ids = rules.Select(rule => rule.TechnologyId).ToHashSet();
            if (ids.Count != rules.Length) return false;
            var original = file.Original?.ToArray() ?? WithoutCommands(file.RelativePath, file.Replacement, ids);
            if (!string.Equals(CivilizationInstallerPackage.Hash(original), file.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                return false;
            var stripped = WithoutCommands(file.RelativePath, file.Replacement, ids);
            var patches = ScalarPatches(file.RelativePath, original, stripped);
            var additions = new List<(int Id, int Epoch, byte[] Bytes)>();
            var actionAdditions = new Dictionary<int, int[]>();
            if (CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath))
            {
                var before = CivilizationTechnologyBindings.ReadRecords(original);
                var after = CivilizationTechnologyBindings.ReadRecords(file.Replacement);
                // Malformed retained exports remain on their original recovery path.
                CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(before);
                CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(after);
                if (before.Any(record => ids.Contains(record.TechId))) return false;
                var byId = after.ToDictionary(record => record.TechId);
                foreach (var rule in rules)
                {
                    if (!byId.TryGetValue(rule.TechnologyId, out var record) ||
                        record.Identifier != "EDMW ability " + rule.Id.ToString("N")) return false;
                    ValidateCommand(record, file.Replacement);
                    additions.Add((record.TechId, record.TechId / 1000 - 1,
                        file.Replacement.AsSpan(record.Offset, record.Length).ToArray()));
                }
                // Preserve export order within each epoch, including stored identities.
                additions = additions.OrderBy(item => byId[item.Id].Offset).ToList();
            }
            else
            {
                var count = ObjectCount(original);
                if (ObjectCount(file.Replacement) != count) return false;
                var expectedSources = rules.ToDictionary(rule => rule.TechnologyId,
                    rule => rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility ? rule.TargetObjectId : rule.SourceObjectId);
                var found = new HashSet<int>();
                for (var row = 0; row < count; row++)
                {
                    var before = ReadActions(original, row);
                    var after = ReadActions(file.Replacement, row);
                    if (before.Any(value => value <= int.MaxValue && ids.Contains((int)value))) return false;
                    var added = after.Where(value => value <= int.MaxValue && ids.Contains((int)value)).Select(value => (int)value).ToArray();
                    if (added.Length == 0) continue;
                    if (after.Length != before.Length + added.Length || !after.Take(before.Length).SequenceEqual(before) ||
                        !after.Skip(before.Length).SequenceEqual(added.Select(value => (uint)value))) return false;
                    _ = CivilizationUnitEffectBindings.CommandSourceCategory(original, row);
                    foreach (var id in added)
                        if (expectedSources[id] != row || !found.Add(id)) return false;
                    actionAdditions.Add(row, added);
                }
                if (!found.SetEquals(ids)) return false;
            }
            composition = new(file.RelativePath, original, ids, patches, additions, actionAdditions);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    internal byte[] Apply(byte[] current)
    {
        VerifyOwned(current, installed: false);
        var result = ApplyScalarPatches(current, installing: true);
        if (CivilizationInstallerPackage.IsTechnologyPath(path))
        {
            var groups = TechnologyGroups(result);
            foreach (var command in commands)
            {
                if (command.Epoch < 0 || command.Epoch >= groups.Count)
                    throw new InvalidDataException("The ability's native epoch group is absent.");
                groups[command.Epoch].Add(command.Bytes.ToArray());
            }
            result = WriteTechnologyGroups(groups);
            CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(result);
        }
        else
            foreach (var row in actions)
                foreach (var id in row.Value)
                    CivilizationTechnologyBindings.WriteProgressionProducerAction(result, row.Key, id);
        return result;
    }

    internal byte[] Remove(byte[] current)
    {
        VerifyOwned(current, installed: true);
        return WithoutCommands(path, ApplyScalarPatches(current, installing: false), CommandIds);
    }

    internal bool MatchesInstalled(byte[] current)
    {
        try
        {
            VerifyOwned(current, installed: true);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private void VerifyOwned(byte[] current, bool installed)
    {
        foreach (var patch in CurrentPatches(current))
            if (!current.AsSpan(patch.Offset, patch.Length).SequenceEqual(installed ? patch.Replacement : patch.Expected))
                throw new InvalidDataException("An ability package's owned field differs from its reviewed value: " + patch.SemanticTarget);
        if (CivilizationInstallerPackage.IsTechnologyPath(path))
        {
            var parsed = CivilizationTechnologyBindings.ReadRecords(current);
            CivilizationUnitEffectBindings.ValidateNativeTechnologyGroups(parsed);
            var records = parsed.ToDictionary(record => record.TechId);
            foreach (var command in commands)
            {
                var present = records.TryGetValue(command.Id, out var record);
                if (installed != present || installed &&
                    !current.AsSpan(record!.Offset, record.Length).SequenceEqual(command.Bytes))
                    throw new InvalidDataException("An ability command is missing, changed, or already owned by another package.");
            }
        }
        else
        {
            var count = ObjectCount(current);
            foreach (var row in actions.Keys)
            {
                if (row >= count) throw new InvalidDataException("An ability source object is absent.");
                var identity = 4 + row * CivilizationObjectBindings.RecordSize + CivilizationObjectBindings.RecordIdentityOffset;
                if (!current.AsSpan(identity, 4).SequenceEqual(Original.AsSpan(identity, 4)))
                    throw new InvalidDataException("An ability source object's identity changed.");
            }
            for (var row = 0; row < count; row++)
            {
                var found = ReadActions(current, row).Where(id => id <= int.MaxValue && CommandIds.Contains((int)id)).Select(id => (int)id);
                var expected = installed && actions.TryGetValue(row, out var assigned) ? assigned : [];
                if (!found.SequenceEqual(expected))
                    throw new InvalidDataException("An ability command's object action registration changed or is already installed.");
            }
        }
    }

    private IReadOnlyList<CivilizationPackagePatch> CurrentPatches(byte[] current) =>
        TranslatePatches(new() { RelativePath = path, Patches = Patches.ToList() }, current);

    private byte[] ApplyScalarPatches(byte[] current, bool installing)
    {
        var result = current.ToArray();
        foreach (var patch in CurrentPatches(current))
            (installing ? patch.Replacement : patch.Expected).CopyTo(result, patch.Offset);
        return result;
    }

    /// <summary>Moves existing semantic technology patches by stable technology identity.</summary>
    internal static IReadOnlyList<CivilizationPackagePatch> TranslatePatches(CivilizationPackageFile file, byte[] current)
    {
        if (file.Patches.Count == 0) return [];
        var records = CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath)
            ? CivilizationTechnologyBindings.ReadRecords(current).ToDictionary(record => record.TechId) : null;
        var result = new List<CivilizationPackagePatch>();
        foreach (var patch in file.Patches)
        {
            var offset = patch.Offset;
            var identityOffset = patch.IdentityOffset;
            if (records is not null)
            {
                if (patch.IdentityBytes.Length != 4 || !records.TryGetValue(BinaryPrimitives.ReadInt32LittleEndian(patch.IdentityBytes), out var record))
                    throw new InvalidDataException("A patched technology's stable identity is absent from the combined table.");
                var withinRecord = checked(patch.Offset - patch.IdentityOffset + 104);
                if (withinRecord < 0 || withinRecord > 436 - patch.Length)
                    throw new InvalidDataException("A technology patch is outside its verified fixed record.");
                offset = checked(record.Offset + withinRecord);
                identityOffset = record.Offset + 104;
            }
            if (offset < 0 || offset > current.Length - patch.Length || identityOffset < 0 ||
                identityOffset > current.Length - patch.IdentityBytes.Length)
                throw new InvalidDataException("A patched database record's identity differs from the exported source.");
            // Effect-record identities deliberately mask their writable operand
            // bytes. Recompute that guard instead of comparing raw record bytes.
            var guard = records is null
                ? CivilizationInstallerPackage.IdentityGuard(file.RelativePath, current, offset)
                : (Offset: identityOffset, Bytes: current.AsSpan(identityOffset, 4).ToArray());
            if (guard.Offset != identityOffset || !guard.Bytes.AsSpan().SequenceEqual(patch.IdentityBytes))
                throw new InvalidDataException("A patched database record's identity differs from the exported source.");
            result.Add(new()
            {
                Offset = offset, Length = patch.Length, Expected = patch.Expected, Replacement = patch.Replacement,
                IdentityOffset = identityOffset, IdentityBytes = patch.IdentityBytes,
                SemanticTarget = $"{file.RelativePath.ToLowerInvariant()}:{offset}:{patch.Length}"
            });
        }
        return result;
    }

    /// <summary>Removes only supplied registered commands, ignoring identities absent from the table.</summary>
    internal static byte[] WithoutCommands(string path, byte[] bytes, IReadOnlySet<int> commandIds)
    {
        if (commandIds.Count == 0) return bytes.ToArray();
        if (CivilizationInstallerPackage.IsTechnologyPath(path))
        {
            var records = CivilizationTechnologyBindings.ReadRecords(bytes);
            foreach (var record in records.Where(record => commandIds.Contains(record.TechId))) ValidateCommand(record, bytes);
            return WriteTechnologyGroups(TechnologyGroups(bytes).Select(group => group.Where(record =>
                !commandIds.Contains(BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(104)))).ToList()).ToList());
        }
        if (!CivilizationInstallerPackage.IsObjectPath(path)) return bytes.ToArray();
        var result = bytes.ToArray();
        var count = ObjectCount(bytes);
        for (var row = 0; row < count; row++)
        {
            var before = ReadActions(bytes, row);
            var after = before.Where(id => id > int.MaxValue || !commandIds.Contains((int)id)).ToArray();
            if (before.Length == after.Length) continue;
            var start = 4 + row * CivilizationObjectBindings.RecordSize;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(start + CivilizationObjectDetails.ActionTechnologyCountOffset), after.Length);
            for (var index = 0; index < before.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(start + CivilizationObjectDetails.ActionTechnologyIdsOffset + index * 4),
                    index < after.Length ? after[index] : uint.MaxValue);
        }
        return result;
    }

    private static IReadOnlyList<CivilizationPackagePatch> ScalarPatches(string path, byte[] before, byte[] after)
    {
        if (before.Length != after.Length) throw new InvalidDataException("Ability composition cannot change unrelated database framing.");
        var remainder = after.ToArray();
        var result = new List<CivilizationPackagePatch>();
        var slots = CivilizationInstallerPackage.SemanticSlots(path, before).Distinct().OrderBy(slot => slot.Offset).ToArray();
        var expandedMasks = CivilizationInstallerPackage.IsTechnologyPath(path)
            ? CivilizationTechnologyBindings.ReadRecords(before).Where(record => before[record.Offset + 312] != after[record.Offset + 312])
                .SelectMany(record => CivilizationInstallerPackage.ProgressionMaskOffsets.Select(offset => record.Offset + offset)).ToHashSet()
            : [];
        foreach (var slot in slots)
        {
            var expected = before.AsSpan(slot.Offset, slot.Length);
            var replacement = after.AsSpan(slot.Offset, slot.Length);
            expected.CopyTo(remainder.AsSpan(slot.Offset));
            if (expected.SequenceEqual(replacement) && !expandedMasks.Contains(slot.Offset)) continue;
            var identity = CivilizationInstallerPackage.IdentityGuard(path, before, slot.Offset);
            result.Add(new()
            {
                Offset = slot.Offset, Length = slot.Length, Expected = expected.ToArray(), Replacement = replacement.ToArray(),
                IdentityOffset = identity.Offset, IdentityBytes = identity.Bytes,
                SemanticTarget = $"{path.ToLowerInvariant()}:{slot.Offset}:{slot.Length}"
            });
        }
        if (!remainder.AsSpan().SequenceEqual(before))
            throw new InvalidDataException("Ability composition contains an unrelated structural database change.");
        // The parser checks complete technology intervals and boolean masks. Object
        // scalar ranges are checked by the package manager's normal merged-field validation.
        CivilizationInstallerPackage.ValidateWholeDatabase(path, after);
        return result;
    }

    private static int ObjectCount(byte[] bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("The object table is truncated.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count is < 1 or > 100000 || bytes.LongLength != 4L + count * (long)CivilizationObjectBindings.RecordSize)
            throw new InvalidDataException("The object table framing is unsupported.");
        return count;
    }

    private static uint[] ReadActions(byte[] bytes, int row)
    {
        var start = 4 + row * CivilizationObjectBindings.RecordSize;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start + CivilizationObjectDetails.ActionTechnologyCountOffset));
        if (count is < 0 or > CivilizationObjectDetails.ActionTechnologyCount)
            throw new InvalidDataException("An object action list exceeds its native capacity.");
        return Enumerable.Range(0, count).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(start + CivilizationObjectDetails.ActionTechnologyIdsOffset + index * 4))).ToArray();
    }

    private static List<List<byte[]>> TechnologyGroups(byte[] bytes)
    {
        var groups = new List<List<byte[]>>();
        foreach (var record in CivilizationTechnologyBindings.ReadRecords(bytes))
        {
            if (record.IsEpochDefinition) groups.Add([]);
            groups[^1].Add(bytes.AsSpan(record.Offset, record.Length).ToArray());
        }
        return groups;
    }

    private static byte[] WriteTechnologyGroups(List<List<byte[]>> groups)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        foreach (var group in groups)
        {
            if (group.Count == 0) throw new InvalidDataException("An ability operation cannot remove an epoch definition.");
            BinaryPrimitives.WriteInt32LittleEndian(header, group.Count - 1);
            output.Write(header);
            foreach (var record in group) output.Write(record);
        }
        var result = output.ToArray();
        _ = CivilizationTechnologyBindings.ReadRecords(result);
        return result;
    }

    private static void ValidateCommand(CivilizationTechnologyRecord record, byte[] bytes)
    {
        const string prefix = "EDMW ability ";
        if (record.IsEpochDefinition || record.TechId is < 10000 or > 14999 ||
            !record.Identifier.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(record.Identifier[prefix.Length..], "N", out _) ||
            record.Length != 448 || record.NodeKind != 3 || record.CommandId != 51 || record.EventId != 0 ||
            record.CreatesObject || record.EditorOnly || record.CreatedObjectId != 0 || record.CommandParameter != 0 ||
            record.StartAge != 0 || record.EndAge != 14 || record.WoodCost != 0 || record.StoneCost != 0 || record.GoldCost != 0 || record.FoodCost != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(record.Offset + 136)) != 0 ||
            record.PrerequisiteTechnologyIds.Count != 0 || record.AdditionalTechnologyIds.Count != 0 ||
            record.ProducerObjectIds.Count != 1 || record.AllowedCivilizationRows.Count != 1 ||
            record.NameStringId != 1000000 + record.TechId || record.ToolTipStringId != 2000000 + record.TechId ||
            record.PrerequisiteStringId1 != 2000000 + record.TechId || record.PrerequisiteStringId2 != 2000000 + record.TechId)
            throw new InvalidDataException("A supplied command identity does not refer to a verified custom ability record.");
    }
}
