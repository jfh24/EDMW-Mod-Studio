using System.Buffers.Binary;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationAbilityButtonChoice(int Id, string Name, int Slot)
{
    public override string ToString() => $"{Name} (button {Id})";
}

public sealed record CivilizationActivatedAbilityRequest(int CivilizationRow,
    CivilizationEntityDraft Source, CivilizationUnitEffect Effect);
public sealed record CivilizationActivatedAbilityRegistration(string EffectId, int CivilizationId,
    int SourceObjectId, int TechnologyId, string Caption, string Tooltip);
public sealed record CivilizationAbilityButtonBuild(byte[] Objects, byte[] Technologies,
    IReadOnlyList<CivilizationActivatedAbilityRegistration> Registrations);

/// <summary>Checked native button sources and in-memory registration of custom immediate abilities.</summary>
public static class CivilizationUnitEffectBindings
{
    // The dynamic 3x14 grid overlaps the fixed nine command controls and the
    // twenty middle action controls. Reserve those entire regions, including
    // contextual actions not represented by a technology's button metadata.
    // HUD form 110 + slot aliases forms 78..86 and 404..423 at these cells.
    internal static IReadOnlyList<int> CustomAbilityButtonSlots { get; } =
        Array.AsReadOnly(new[] { 13, 27, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41 });

    public static CivilizationAbilityButtonBuild BuildAbilityButtons(byte[] objects, byte[] technologies,
        byte[] buttons, byte[] events, IReadOnlyList<CivilizationActivatedAbilityRequest> requests)
    {
        var active = requests.Where(request => request.Effect.Enabled &&
            request.Effect.Trigger == CivilizationUnitEffectTrigger.Activated)
            .OrderBy(request => request.Effect.Id, StringComparer.Ordinal).ToArray();
        if (active.Length == 0) return new(objects, technologies, []);
        foreach (var request in active) CivilizationUnitEffects.Validate(request.Source, [request.Effect]);
        if (active.Length > 1024 || active.Select(request => Guid.Parse(request.Effect.Id)).Distinct().Count() != active.Length)
            throw new InvalidDataException("Activated ability identifiers must be unique and within the 1,024-rule limit.");
        // Custom commands are consumed by the runtime. If launched without it,
        // the native fallback must remain an empty, zero-cost event.
        if (CivilizationAbilityData.ReadEvents(events)[0].EffectIds.Count != 0)
            throw new InvalidDataException("Custom ability buttons require the game's empty event zero to remain unchanged.");
        var records = CivilizationTechnologyBindings.ReadRecords(technologies);
        ValidateNativeTechnologyGroups(records);
        var byId = records.ToDictionary(record => record.TechId);
        var template = records.SingleOrDefault(record => record.TechId == 1700 && record.NodeKind == 3 &&
            record.CommandId == 3 && !record.CreatesObject && !record.EditorOnly)
            ?? throw new InvalidDataException("The verified immediate-command template is unavailable.");
        var validButtons = ReadButtonChoices(buttons).Select(choice => choice.Id).ToHashSet();
        var usedTechnologyIds = records.Select(record => record.TechId).ToHashSet();
        var claimedSlots = new HashSet<(int Object, int Civilization, int Slot)>();
        var resultObjects = (byte[])objects.Clone();
        var additions = new List<(int TechnologyId, byte[] Bytes)>();
        var registrations = new List<CivilizationActivatedAbilityRegistration>();
        var nextStoredId = checked(records.Max(record => BinaryPrimitives.ReadInt32LittleEndian(
            technologies.AsSpan(record.Offset + 100, 4))) + 1);
        foreach (var request in active)
        {
            CivilizationUnitEffects.Validate(request.Source, [request.Effect]);
            var effect = request.Effect;
            var sourceId = request.Source.SourceObjectId!.Value;
            if (request.CivilizationRow is < 3 or > 11)
                throw new InvalidDataException("Custom ability buttons require a playable source civilization.");
            var sourceOffset = CivilizationObjectBindings.ValidateRecord(objects, sourceId, request.Source.Category);
            if (request.Source.SourceTechnologyId is int identity && identity != BinaryPrimitives.ReadInt32LittleEndian(
                objects.AsSpan(sourceOffset + CivilizationObjectBindings.TechnologyIdOffset, 4)))
                throw new InvalidDataException("The ability's source technology does not match its unit or building.");
            if (!validButtons.Contains(effect.ButtonIconId))
                throw new InvalidDataException($"{effect.Name}: the selected button appearance is not an existing named game button.");
            if (effect.ButtonCaption.Any(character => character is < ' ' or > '~'))
                throw new InvalidDataException($"{effect.Name}: this runtime prototype supports printable English button captions.");
            if (!CustomAbilityButtonSlots.Contains(effect.ButtonSlot))
                throw new InvalidDataException($"{effect.Name}: button slot {effect.ButtonSlot} overlaps the game's standard action controls. Open Custom effects and choose a free button position.");
            if (!claimedSlots.Add((sourceId, request.CivilizationRow, effect.ButtonSlot)))
                throw new InvalidDataException($"{effect.Name}: another custom ability already occupies button slot {effect.ButtonSlot}.");
            var existing = CivilizationObjectDetails.ReadActionTechnologyIds(objects, sourceId, request.Source.Category).Select(id => checked((int)id))
                .Concat(records.Where(record => record.ProducerObjectIds.Contains(sourceId)).Select(record => record.TechId)).Distinct();
            foreach (var id in existing)
                if (byId.TryGetValue(id, out var other) && other.AllowsCivilizationRow(request.CivilizationRow) && !other.EditorOnly &&
                    CivilizationTechnologyBindings.ReadProgressionButtonSlot(other, request.CivilizationRow, buttons) == effect.ButtonSlot)
                    throw new InvalidDataException($"{effect.Name}: button slot {effect.ButtonSlot} is occupied by {other.Identifier}. Choose a free slot.");
            var technologyId = effect.NativeTechnologyId ?? Enumerable.Range(10000, 5000).FirstOrDefault(id => !usedTechnologyIds.Contains(id), -1);
            if (technologyId is < 10000 or > 14999 || !usedTechnologyIds.Add(technologyId))
                throw new InvalidDataException($"{effect.Name}: its native technology ID is unavailable. Remove the prior installed version before rebuilding it.");
            var recordBytes = new byte[448]; // 436 fixed, one producer, no additional technologies.
            technologies.AsSpan(template.Offset, 436).CopyTo(recordBytes);
            recordBytes.AsSpan(0, 100).Clear();
            Encoding.ASCII.GetBytes("EDMW ability " + Guid.Parse(effect.Id).ToString("N")).CopyTo(recordBytes, 0);
            Put(recordBytes, 100, nextStoredId++);
            Put(recordBytes, 104, technologyId);
            // Authored availability spans every age. Native loader 0x00672772
            // nevertheless derives node +0x2C from the reserved technology ID;
            // NativeAbilityBindings neutralizes that age only for registered abilities.
            Put(recordBytes, 108, 0); Put(recordBytes, 112, 14);
            Put(recordBytes, 116, 0);
            recordBytes.AsSpan(124, 20).Clear();
            Put(recordBytes, 144, 1);
            recordBytes.AsSpan(148, 20).Clear(); // No prerequisites or created object.
            Put(recordBytes, 168, effect.ButtonIconId);
            Put(recordBytes, 176, 1000000 + technologyId);
            Put(recordBytes, 180, 3);
            Put(recordBytes, 208, 51); // 0x006714FE extracts event zero for native command 51.
            Put(recordBytes, 216, 0);
            foreach (var offset in new[] { 232, 236, 240 }) Put(recordBytes, offset, 2000000 + technologyId);
            recordBytes[261] = 0;
            recordBytes.AsSpan(312, 12).Clear();
            recordBytes[312 + request.CivilizationRow] = 1;
            Put(recordBytes, 276, effect.ButtonSlot);
            for (var row = 0; row < 11; row++)
            {
                Put(recordBytes, 324 + row * 8, effect.ButtonSlot);
                Put(recordBytes, 328 + row * 8, effect.ButtonSlot);
            }
            Put(recordBytes, 436, 1); Put(recordBytes, 440, sourceId); Put(recordBytes, 444, 0);
            CivilizationTechnologyBindings.WriteProgressionProducerAction(resultObjects, sourceId, technologyId);
            additions.Add((technologyId, recordBytes));
            var adjustments = string.Join(", ", CivilizationUnitEffects.GetOutputs(effect).Select(output =>
                CivilizationUnitEffects.DescribeOutput(output) + " (" +
                CivilizationUnitEffects.GetStatDefinition(output.Stat).Unit + ")"));
            var ending = effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle
                ? "Click again to turn off." : $"Lasts {effect.DurationSeconds} {(effect.DurationSeconds == 1 ? "second" : "seconds")}.";
            registrations.Add(new(effect.Id, request.CivilizationRow + 1, sourceId, technologyId, effect.ButtonCaption,
                $"{effect.ButtonCaption}: {adjustments}. {ending}"));
        }
        // Native lookup 0x00401D64 computes the epoch from the technology ID:
        // IDs 10000..14999 belong to existing groups 9..13. Each group's map
        // is searched independently by 0x0067081E, so appending to group zero
        // creates an unreachable node even though a flattened parser finds it.
        var epochs = records.Where(record => record.IsEpochDefinition).ToArray();
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        for (var epoch = 0; epoch < epochs.Length; epoch++)
        {
            var start = epochs[epoch].Offset - 4;
            var end = epoch + 1 < epochs.Length ? epochs[epoch + 1].Offset - 4 : technologies.Length;
            var groupAdditions = additions.Where(addition => addition.TechnologyId / 1000 - 1 == epoch).ToArray();
            var childCount = checked(BinaryPrimitives.ReadInt32LittleEndian(technologies.AsSpan(start)) + groupAdditions.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header, childCount);
            output.Write(header);
            output.Write(technologies.AsSpan(start + 4, end - start - 4));
            foreach (var addition in groupAdditions) output.Write(addition.Bytes);
        }
        var resultTechnologies = output.ToArray();
        var parsed = CivilizationTechnologyBindings.ReadRecords(resultTechnologies);
        ValidateNativeTechnologyGroups(parsed);
        foreach (var registration in registrations)
        {
            var created = parsed.Single(record => record.TechId == registration.TechnologyId);
            if (created.NodeKind != 3 || !created.ProducerObjectIds.SequenceEqual([registration.SourceObjectId]) ||
                !created.AllowedCivilizationRows.SequenceEqual([registration.CivilizationId - 1]))
                throw new InvalidDataException("A custom ability did not retain its native identity and civilization ownership.");
        }
        return new(resultObjects, resultTechnologies, registrations.AsReadOnly());
    }

    internal static void ValidateNativeTechnologyGroups(IReadOnlyList<CivilizationTechnologyRecord> records)
    {
        var epoch = -1;
        foreach (var record in records)
        {
            if (record.IsEpochDefinition)
            {
                epoch++;
                if (record.TechId != epoch)
                    throw new InvalidDataException("Native ability tables require the game's ordered epoch groups 0 through 13.");
            }
            else if (record.TechId / 1000 - 1 != epoch)
                throw new InvalidDataException($"Technology {record.TechId} is stored in epoch {epoch}, but native lookup requires epoch {record.TechId / 1000 - 1}. Re-export and reinstall the custom ability package with this version of Mod Studio.");
        }
        if (epoch != 13)
            throw new InvalidDataException("Native ability tables require all 14 existing epoch groups.");
    }

    internal static void ValidateNativeTechnologyGroups(byte[] technologies) =>
        ValidateNativeTechnologyGroups(CivilizationTechnologyBindings.ReadRecords(technologies));

    // Returns a repaired copy for verified old exports or isolated diagnostics.
    // Callers retain ownership of installation transactions; this never writes a game file.
    internal static byte[] RepairAbilityTechnologyGroups(byte[] technologies, IReadOnlyCollection<int> abilityIds)
    {
        var records = CivilizationTechnologyBindings.ReadRecords(technologies);
        var epochs = records.Where(record => record.IsEpochDefinition).ToArray();
        if (epochs.Length != 14 || epochs.Where((record, epoch) => record.TechId != epoch).Any())
            throw new InvalidDataException("Native ability tables require the game's ordered 14 epoch groups.");
        var currentEpoch = -1;
        var changed = false;
        foreach (var record in records)
        {
            if (record.IsEpochDefinition) { currentEpoch++; continue; }
            if (record.TechId / 1000 - 1 == currentEpoch) continue;
            if (!abilityIds.Contains(record.TechId) || record.TechId is < 10000 or > 14999 ||
                !record.Identifier.StartsWith("EDMW ability ", StringComparison.Ordinal) || record.NodeKind != 3 ||
                record.CommandId != 51 || record.EventId != 0 || record.CreatesObject)
                throw new InvalidDataException("Only a verified custom ability record can be moved between native epoch groups.");
            changed = true;
        }
        if (!changed) return technologies;
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        for (var epoch = 0; epoch < epochs.Length; epoch++)
        {
            var originalEnd = epoch + 1 < epochs.Length ? epochs[epoch + 1].Offset - 4 : technologies.Length;
            var children = records.Where(record => !record.IsEpochDefinition && record.TechId / 1000 - 1 == epoch)
                .OrderBy(record => record.Offset > epochs[epoch].Offset && record.Offset < originalEnd ? 0 : 1).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(header, children.Length);
            output.Write(header);
            output.Write(technologies.AsSpan(epochs[epoch].Offset, epochs[epoch].Length));
            foreach (var child in children) output.Write(technologies.AsSpan(child.Offset, child.Length));
        }
        var repaired = output.ToArray();
        ValidateNativeTechnologyGroups(repaired);
        return repaired;
    }

    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

    /// <summary>Free native HUD slots, before accounting for the editor's other custom attachments.</summary>
    public static IReadOnlyList<int> AvailableButtonSlots(byte[] objects, byte[] technologies,
        byte[] buttons, CivilizationEntityDraft source, int civilizationRow)
    {
        if (civilizationRow is < 3 or > 11 || source.SourceObjectId is not int sourceId)
            throw new InvalidDataException("Choose an existing unit or building in a playable civilization.");
        _ = CivilizationObjectBindings.ValidateRecord(objects, sourceId, source.Category);
        var records = CivilizationTechnologyBindings.ReadRecords(technologies);
        // Explicit commands use fixed controls, not their dbbuttons fallback
        // slot (usually 41). Their cells are already excluded above.
        var ids = CivilizationObjectDetails.ReadActionTechnologyIds(objects, sourceId, source.Category).Select(id => checked((int)id))
            .ToHashSet();
        var occupied = records.Where(record => (ids.Contains(record.TechId) || record.ProducerObjectIds.Contains(sourceId)) &&
                record.AllowsCivilizationRow(civilizationRow) && !record.EditorOnly)
            .Select(record => CivilizationTechnologyBindings.ReadProgressionButtonSlot(record, civilizationRow, buttons)).ToHashSet();
        return CustomAbilityButtonSlots.Where(slot => !occupied.Contains(slot)).ToArray();
    }

    public static IReadOnlyList<CivilizationAbilityButtonChoice> ReadButtonChoices(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Length < 4) throw new InvalidDataException("The button table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(database);
        if (count is <= 0 or > 100_000 || database.LongLength != 4L + count * 228L)
            throw new InvalidDataException("The button table has an unsupported layout.");
        var result = new List<CivilizationAbilityButtonChoice>();
        for (var row = 0; row < count; row++)
        {
            var record = database.AsSpan(4 + row * 228, 228);
            if (BinaryPrimitives.ReadInt32LittleEndian(record[204..]) != row) continue;
            var terminator = record[..100].IndexOf((byte)0);
            if (terminator <= 0) continue;
            var nameBytes = record[..terminator];
            if (nameBytes.ContainsAnyExceptInRange((byte)32, (byte)126)) continue;
            var name = Encoding.ASCII.GetString(nameBytes).Trim();
            if (name.Length == 0 || name.StartsWith("Whoops", StringComparison.OrdinalIgnoreCase)) continue;
            var textureEnd = record.Slice(100, 100).IndexOf((byte)0);
            if (textureEnd <= 0 || record.Slice(100, textureEnd).ContainsAnyExceptInRange((byte)32, (byte)126)) continue;
            var texture = Encoding.ASCII.GetString(record.Slice(100, textureEnd)).Trim().Replace('/', '\\');
            if (texture.Length == 0 || texture.Contains(':') || texture.StartsWith('\\') ||
                texture.Split('\\').Any(part => part is "." or "..")) continue;
            var slot = BinaryPrimitives.ReadInt32LittleEndian(record[216..]);
            result.Add(new(row, name, slot));
        }
        return result.AsReadOnly();
    }
}
