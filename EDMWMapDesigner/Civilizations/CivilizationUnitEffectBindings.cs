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
        var byId = records.ToDictionary(record => record.TechId);
        var template = records.SingleOrDefault(record => record.TechId == 1700 && record.NodeKind == 3 &&
            record.CommandId == 3 && !record.CreatesObject && !record.EditorOnly)
            ?? throw new InvalidDataException("The verified immediate-command template is unavailable.");
        var validButtons = ReadButtonChoices(buttons).Select(choice => choice.Id).ToHashSet();
        var usedTechnologyIds = records.Select(record => record.TechId).ToHashSet();
        var claimedSlots = new HashSet<(int Object, int Civilization, int Slot)>();
        var resultObjects = (byte[])objects.Clone();
        var additions = new List<byte[]>();
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
            if (!claimedSlots.Add((sourceId, request.CivilizationRow, effect.ButtonSlot)))
                throw new InvalidDataException($"{effect.Name}: another custom ability already occupies button slot {effect.ButtonSlot}.");
            var existing = CivilizationObjectDetails.ReadCommandTechnologyIds(objects, sourceId, request.Source.Category)
                .Concat(CivilizationObjectDetails.ReadActionTechnologyIds(objects, sourceId, request.Source.Category).Select(id => checked((int)id)))
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
            additions.Add(recordBytes);
            var adjustment = effect.Amount > 0 ? "+" + effect.Amount : effect.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ending = effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle
                ? "Click again to turn off." : $"Lasts {effect.DurationSeconds} seconds; click again to restart.";
            registrations.Add(new(effect.Id, request.CivilizationRow + 1, sourceId, technologyId, effect.ButtonCaption,
                $"{effect.ButtonCaption}: {adjustment} line of sight. {ending} Active bonuses reset when a save is loaded."));
        }
        var firstGroupEnd = records.Skip(1).FirstOrDefault(record => record.IsEpochDefinition)?.Offset - 4 ?? technologies.Length;
        using var output = new MemoryStream();
        output.Write(technologies.AsSpan(0, firstGroupEnd));
        foreach (var addition in additions) output.Write(addition);
        output.Write(technologies.AsSpan(firstGroupEnd));
        var resultTechnologies = output.ToArray();
        Put(resultTechnologies, 0, checked(BinaryPrimitives.ReadInt32LittleEndian(technologies) + additions.Count));
        var parsed = CivilizationTechnologyBindings.ReadRecords(resultTechnologies);
        foreach (var registration in registrations)
        {
            var created = parsed.Single(record => record.TechId == registration.TechnologyId);
            if (created.NodeKind != 3 || !created.ProducerObjectIds.SequenceEqual([registration.SourceObjectId]) ||
                !created.AllowedCivilizationRows.SequenceEqual([registration.CivilizationId - 1]))
                throw new InvalidDataException("A custom ability did not retain its native identity and civilization ownership.");
        }
        return new(resultObjects, resultTechnologies, registrations.AsReadOnly());
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
        var ids = CivilizationObjectDetails.ReadCommandTechnologyIds(objects, sourceId, source.Category)
            .Concat(CivilizationObjectDetails.ReadActionTechnologyIds(objects, sourceId, source.Category).Select(id => checked((int)id)))
            .ToHashSet();
        var occupied = records.Where(record => (ids.Contains(record.TechId) || record.ProducerObjectIds.Contains(sourceId)) &&
                record.AllowsCivilizationRow(civilizationRow) && !record.EditorOnly)
            .Select(record => CivilizationTechnologyBindings.ReadProgressionButtonSlot(record, civilizationRow, buttons)).ToHashSet();
        return Enumerable.Range(1, 41).Where(slot => !occupied.Contains(slot)).ToArray();
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
