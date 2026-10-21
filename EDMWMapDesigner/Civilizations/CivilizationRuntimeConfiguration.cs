using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public readonly record struct CivilizationRuntimeUnitOutput(CivilizationUnitEffectStat Stat, float Amount);
public readonly record struct CivilizationRuntimeUnitAction(CivilizationUnitEffectOutputKind Kind, int ObjectId = -1,
    int Count = 1, CivilizationUnitEffectOwner Owner = CivilizationUnitEffectOwner.SourcePlayer, int PlayerSlot = 1, string ObjectName = "");

public readonly record struct CivilizationRuntimeUnitRule(Guid Id, int CivilizationId, int SourceObjectId,
    int TargetObjectId, float Amount, int RadiusTiles,
    CivilizationUnitEffectTrigger Trigger = CivilizationUnitEffectTrigger.Proximity,
    CivilizationUnitEffectActivationMode ActivationMode = CivilizationUnitEffectActivationMode.Toggle,
    int DurationMilliseconds = 0, int TechnologyId = 0, string Caption = "", bool Enabled = true,
    CivilizationUnitEffectStat Stat = CivilizationUnitEffectStat.LineOfSight,
    IReadOnlyList<CivilizationRuntimeUnitOutput>? Outputs = null, int GrantFamilyTechnologyId = 0, string Name = "", string TargetName = "",
    IReadOnlyList<CivilizationRuntimeUnitAction>? Actions = null, CivilizationUnitEffectTargetPlayers TargetPlayers = CivilizationUnitEffectTargetPlayers.Own,
    Guid FamilyId = default, int DelayMilliseconds = 0, int MaxAffectedTargets = 0, int MaxConcurrentTargets = 0,
    int FoodCost = 0, int WoodCost = 0, int GoldCost = 0, int StoneCost = 0);

public sealed record CivilizationRuntimeConfiguration(IReadOnlyList<CivilizationRuntimeRule> Research,
    IReadOnlyList<CivilizationRuntimeUnitRule> Units)
{
    internal const int MaximumNameBytes = 400;
    private static readonly UTF8Encoding NameEncoding = new(false, true);
    public int Count => Research.Count + Units.Count;

    public static IReadOnlyList<CivilizationRuntimeUnitOutput> GetOutputs(CivilizationRuntimeUnitRule rule) =>
        rule.Outputs ?? (rule.Actions is { Count: > 0 } && rule.Amount == 0 ? [] : [new(rule.Stat, rule.Amount)]);
    public static IReadOnlyList<CivilizationRuntimeUnitAction> GetActions(CivilizationRuntimeUnitRule rule) => rule.Actions ?? [];
    public static bool EquivalentActions(CivilizationRuntimeUnitRule left, CivilizationRuntimeUnitRule right) =>
        GetActions(left).Select(action => action with { ObjectName = "" }).SequenceEqual(GetActions(right).Select(action => action with { ObjectName = "" }));

    public static bool Equivalent(CivilizationRuntimeUnitRule left, CivilizationRuntimeUnitRule right) =>
        left with { Amount = 0, Stat = CivilizationUnitEffectStat.LineOfSight, Outputs = null, Name = "", TargetName = "", Actions = null } ==
        right with { Amount = 0, Stat = CivilizationUnitEffectStat.LineOfSight, Outputs = null, Name = "", TargetName = "", Actions = null } &&
        (left.Name.Length == 0 || right.Name.Length == 0 || left.Name == right.Name) &&
        GetOutputs(left).SequenceEqual(GetOutputs(right)) && EquivalentActions(left, right);

    public static IReadOnlyList<CivilizationRuntimeUnitRule> UnitRulesFor(CivilizationDraft civilization, CivilizationEntityDraft entity)
    {
        var effects = CivilizationUnitEffects.Read(entity).Where(effect => effect.Enabled).ToArray();
        if (effects.Length == 0) return [];
        if (civilization.SourceRowIndex is not (>= 3 and <= 11) ||
            civilization.SourceCivilizationId != civilization.SourceRowIndex + 1 || entity.SourceObjectId is null)
            throw new InvalidDataException("Runtime unit effects require an existing playable civilization and source object.");
        if (effects.Any(effect => effect.Trigger == CivilizationUnitEffectTrigger.Activated && effect.NativeTechnologyId is null ||
            effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility && effect.NativeTargetTechnologyIds is null))
            throw new InvalidDataException($"{entity.Name}: activated abilities need a generated in-game command before export.");
        foreach (var effect in effects)
            foreach (var output in CivilizationUnitEffects.GetStatOutputs(effect))
                if (!CivilizationUnitEffects.GetStatDefinition(output.Stat).RuntimeSupported)
                    throw new InvalidDataException($"{effect.Name}: {CivilizationUnitEffects.GetStatDefinition(output.Stat).Name} can be saved in this project, but its live runtime adapter is not available yet. Disable this effect before exporting.");
        if (effects.Sum(effect => (long)CivilizationUnitEffects.GetTargetObjectIds(effect).Count) > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException($"{entity.Name}: the selected targets exceed the {CivilizationRuntimeEffects.MaximumRules} runtime rule limit.");
        var expanded = effects.SelectMany(effect =>
        {
            var outputs = CivilizationUnitEffects.GetStatOutputs(effect).Select(output =>
                new CivilizationRuntimeUnitOutput(output.Stat, (float)output.Amount)).ToArray();
            var actions = CivilizationUnitEffects.GetOutputs(effect).Where(output => output.Kind != CivilizationUnitEffectOutputKind.StatModifier)
                .Select(output => new CivilizationRuntimeUnitAction(output.Kind, output.ObjectId ?? -1, output.Count, output.Owner, output.PlayerSlot)).ToArray();
            var targets = CivilizationUnitEffects.GetTargetObjectIds(effect);
            return targets.Select(target => new CivilizationRuntimeUnitRule(
            RuleIdForTarget(effect, target), civilization.SourceCivilizationId!.Value,
            entity.SourceObjectId.Value, target, outputs.Length > 0 ? outputs[0].Amount : 0,
            effect.Trigger != CivilizationUnitEffectTrigger.Activated ? effect.RadiusTiles : 0,
            effect.Trigger,
            effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? CivilizationUnitEffectActivationMode.Toggle : effect.ActivationMode,
            effect.Trigger == CivilizationUnitEffectTrigger.Activated && effect.ActivationMode == CivilizationUnitEffectActivationMode.Timed ? checked(effect.DurationSeconds * 1000) : 0,
            effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility ? effect.NativeTargetTechnologyIds![target] :
                effect.Trigger == CivilizationUnitEffectTrigger.Activated ? effect.NativeTechnologyId!.Value : 0,
            effect.Trigger != CivilizationUnitEffectTrigger.Proximity ? effect.ButtonCaption : "", Stat: outputs.Length > 0 ? outputs[0].Stat : CivilizationUnitEffectStat.LineOfSight,
            Outputs: outputs.Length == 1 ? null : outputs,
            GrantFamilyTechnologyId: effect.Trigger == CivilizationUnitEffectTrigger.ProximityAbility ? effect.NativeTargetTechnologyIds!.Values.Min() : 0,
            Name: ExportName(effect.Name), Actions: actions.Length == 0 ? null : actions, TargetPlayers: effect.TargetPlayers,
            FamilyId: actions.Length > 0 || effect.TargetPlayers != CivilizationUnitEffectTargetPlayers.Own || effect.ProximityDelaySeconds != 0 ||
                effect.MaxAffectedTargets != 0 || effect.MaxConcurrentTargets != 0 || effect.FoodCost != 0 || effect.WoodCost != 0 || effect.GoldCost != 0 || effect.StoneCost != 0 ? Guid.Parse(effect.Id) : Guid.Empty,
            DelayMilliseconds: checked(effect.ProximityDelaySeconds * 1000), MaxAffectedTargets: effect.MaxAffectedTargets, MaxConcurrentTargets: effect.MaxConcurrentTargets,
            FoodCost: effect.FoodCost, WoodCost: effect.WoodCost, GoldCost: effect.GoldCost, StoneCost: effect.StoneCost));
        }).ToArray();
        if (expanded.Length > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException($"{entity.Name}: the selected targets exceed the {CivilizationRuntimeEffects.MaximumRules} runtime rule limit.");
        return expanded;
    }

    internal static Guid RuleIdForTarget(CivilizationUnitEffect effect, int target) =>
        CivilizationUnitEffects.GetTargetObjectIds(effect).Count == 1 ? Guid.Parse(effect.Id) : TargetRuleId(Guid.Parse(effect.Id), target);

    private static string ExportName(string name) => string.Concat(name.Select(character =>
        char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character)).Trim();

    private static Guid TargetRuleId(Guid effect, int target)
    {
        // Fixed namespace + authored identity + target: independent of selection order,
        // other targets, output values, source roster order and export command IDs.
        Span<byte> input = stackalloc byte[36];
        new Guid("f9e8fd43-d726-4c22-965c-9d21fc7b7d27").TryWriteBytes(input);
        effect.TryWriteBytes(input[16..]);
        BinaryPrimitives.WriteInt32LittleEndian(input[32..], target);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    internal static byte[]? DataFor(CivilizationDraft civilization, CivilizationEntityDraft entity)
    {
        var research = CivilizationRuntimeEffects.RulesFor(civilization, entity);
        var units = UnitRulesFor(civilization, entity);
        if (research.Count + units.Count == 0) return null;
        return units.Count == 0 ? CivilizationRuntimeEffects.Encode(research) : Encode(research, units);
    }

    public static byte[] Encode(IEnumerable<CivilizationRuntimeRule> research, IEnumerable<CivilizationRuntimeUnitRule> unitRules)
    {
        var researchBytes = CivilizationRuntimeEffects.Encode(research);
        var units = unitRules.OrderBy(rule => rule.Id).ToArray();
        var researchCount = (researchBytes.Length - 12) / 16;
        if (researchCount + units.Length > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException("Too many runtime effect rules.");
        foreach (var rule in units) Validate(rule);
        var version8 = units.Any(rule => GetActions(rule).Count > 0 || rule.TargetPlayers != CivilizationUnitEffectTargetPlayers.Own || rule.ActivationMode == CivilizationUnitEffectActivationMode.Instant ||
            rule.FamilyId != Guid.Empty || rule.DelayMilliseconds != 0 || rule.MaxAffectedTargets != 0 || rule.MaxConcurrentTargets != 0 ||
            rule.FoodCost != 0 || rule.WoodCost != 0 || rule.GoldCost != 0 || rule.StoneCost != 0);
        var version7 = version8 || units.Any(rule => rule.Name.Length != 0 || rule.TargetName.Length != 0);
        var bytes = new byte[16 + researchCount * 16 + units.Sum(rule => 60 + GetOutputs(rule).Count * 8 + rule.Caption.Length +
            (version7 ? 8 + NameEncoding.GetByteCount(rule.Name) + NameEncoding.GetByteCount(rule.TargetName) : 0) +
            (version8 ? 52 + GetActions(rule).Sum(action => 24 + NameEncoding.GetByteCount(action.ObjectName)) : 0))];
        (version8 ? "EDMWEF08"u8 : version7 ? "EDMWEF07"u8 : units.Any(rule => rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility) ? "EDMWEF06"u8 : "EDMWEF05"u8).CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), researchCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), units.Length);
        researchBytes.AsSpan(12).CopyTo(bytes.AsSpan(16));
        var offset = 16 + researchCount * 16;
        foreach (var rule in units)
        {
            var outputs = GetOutputs(rule);
            var nameLength = version7 ? NameEncoding.GetByteCount(rule.Name) : 0;
            var targetNameLength = version7 ? NameEncoding.GetByteCount(rule.TargetName) : 0;
            var record = bytes.AsSpan(offset, 60 + outputs.Count * 8 + rule.Caption.Length + (version7 ? 8 + nameLength + targetNameLength : 0) +
                (version8 ? 52 + GetActions(rule).Sum(action => 24 + NameEncoding.GetByteCount(action.ObjectName)) : 0));
            rule.Id.TryWriteBytes(record);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], rule.CivilizationId);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], rule.SourceObjectId);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], rule.TargetObjectId);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], rule.GrantFamilyTechnologyId); // V6 grant family; otherwise reserved zero.
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], rule.RadiusTiles);
            BinaryPrimitives.WriteUInt32LittleEndian(record[36..], (uint)rule.Trigger + 1u | (rule.Enabled ? 0u : 0x80000000u));
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], (int)rule.ActivationMode + 1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], rule.DurationMilliseconds);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], rule.TechnologyId);
            BinaryPrimitives.WriteInt32LittleEndian(record[52..], rule.Caption.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[56..], outputs.Count);
            for (var index = 0; index < outputs.Count; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record[(60 + index * 8)..], (int)outputs[index].Stat);
                BinaryPrimitives.WriteSingleLittleEndian(record[(64 + index * 8)..], outputs[index].Amount);
            }
            Encoding.ASCII.GetBytes(rule.Caption, record[(60 + outputs.Count * 8)..]);
            if (version7)
            {
                var nameOffset = 60 + outputs.Count * 8 + rule.Caption.Length;
                BinaryPrimitives.WriteInt32LittleEndian(record[nameOffset..], nameLength);
                NameEncoding.GetBytes(rule.Name, record[(nameOffset + 4)..]);
                var targetNameOffset = nameOffset + 4 + nameLength;
                BinaryPrimitives.WriteInt32LittleEndian(record[targetNameOffset..], targetNameLength);
                NameEncoding.GetBytes(rule.TargetName, record[(targetNameOffset + 4)..]);
            }
            if (version8)
            {
                var actionOffset = 60 + outputs.Count * 8 + rule.Caption.Length + 8 + nameLength + targetNameLength;
                BinaryPrimitives.WriteInt32LittleEndian(record[actionOffset..], (int)rule.TargetPlayers);
                BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 4)..], GetActions(rule).Count);
                actionOffset += 8;
                foreach (var action in GetActions(rule))
                {
                    BinaryPrimitives.WriteInt32LittleEndian(record[actionOffset..], (int)action.Kind);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 4)..], action.ObjectId);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 8)..], action.Count);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 12)..], (int)action.Owner);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 16)..], action.PlayerSlot);
                    var length = NameEncoding.GetByteCount(action.ObjectName);
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 20)..], length);
                    NameEncoding.GetBytes(action.ObjectName, record[(actionOffset + 24)..]);
                    actionOffset += 24 + length;
                }
                rule.FamilyId.TryWriteBytes(record[actionOffset..]);
                var conditions = new[] { rule.DelayMilliseconds, rule.MaxAffectedTargets, rule.MaxConcurrentTargets, rule.FoodCost, rule.WoodCost, rule.GoldCost, rule.StoneCost };
                for (var condition = 0; condition < conditions.Length; condition++)
                    BinaryPrimitives.WriteInt32LittleEndian(record[(actionOffset + 16 + condition * 4)..], conditions[condition]);
            }
            offset += record.Length;
        }
        _ = Decode(bytes);
        return bytes;
    }

    public static CivilizationRuntimeConfiguration Decode(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF01"u8))
            return new(CivilizationRuntimeEffects.Decode(bytes), []);
        var version8 = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF08"u8);
        var version7 = version8 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF07"u8);
        var version6 = version7 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF06"u8);
        var version5 = version6 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF05"u8);
        var version4 = version5 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF04"u8);
        var version3 = version4 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF03"u8);
        if (bytes.Length < 16 || (!version3 && !bytes.AsSpan(0, 8).SequenceEqual("EDMWEF02"u8)))
            throw new InvalidDataException("The runtime effect configuration header is invalid.");
        var researchCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var unitCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (researchCount < 0 || unitCount < 0 || (long)researchCount + unitCount > CivilizationRuntimeEffects.MaximumRules ||
            bytes.Length < 16L + researchCount * 16L + unitCount * (version4 ? 60L : version3 ? 56L : 36L))
            throw new InvalidDataException("The runtime effect configuration framing is invalid.");
        var researchBytes = new byte[12 + researchCount * 16];
        "EDMWEF01"u8.CopyTo(researchBytes);
        BinaryPrimitives.WriteInt32LittleEndian(researchBytes.AsSpan(8), researchCount);
        bytes.AsSpan(16, researchCount * 16).CopyTo(researchBytes.AsSpan(12));
        var research = CivilizationRuntimeEffects.Decode(researchBytes);
        var units = new List<CivilizationRuntimeUnitRule>();
        var ids = new HashSet<Guid>();
        var technologies = new HashSet<int>();
        var offset = 16 + researchCount * 16;
        for (var i = 0; i < unitCount; i++)
        {
            var size = version4 ? 60 : version3 ? 56 : 36;
            if (bytes.Length - offset < size) throw new InvalidDataException("A runtime unit rule is truncated.");
            var record = bytes.AsSpan(offset, size);
            var rule = new CivilizationRuntimeUnitRule(new Guid(record[..16]),
                BinaryPrimitives.ReadInt32LittleEndian(record[16..]), BinaryPrimitives.ReadInt32LittleEndian(record[20..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[24..]), version5 ? 0 : version4 ? BinaryPrimitives.ReadSingleLittleEndian(record[28..]) : BinaryPrimitives.ReadInt32LittleEndian(record[28..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[32..]));
            if (version3)
            {
                var captionLength = BinaryPrimitives.ReadInt32LittleEndian(record[52..]);
                var outputCount = version5 ? BinaryPrimitives.ReadInt32LittleEndian(record[56..]) : 0;
                var triggerCode = BinaryPrimitives.ReadUInt32LittleEndian(record[36..]) & 0x7FFFFFFFu;
                if (version5 && (outputCount < (version8 ? 0 : 1) || outputCount > 51 ||
                    BinaryPrimitives.ReadInt32LittleEndian(record[28..]) != 0 && (!version6 || triggerCode != 3)))
                    throw new InvalidDataException("A runtime effect output list is invalid.");
                var outputBytes = outputCount * 8;
                if (captionLength is < 0 or > 40 || bytes.Length - offset - size < outputBytes + captionLength ||
                    bytes.AsSpan(offset + size + outputBytes, captionLength).ContainsAnyExceptInRange((byte)32, (byte)126))
                    throw new InvalidDataException("A runtime ability caption is invalid.");
                var outputs = new CivilizationRuntimeUnitOutput[outputCount];
                for (var index = 0; index < outputCount; index++)
                    outputs[index] = new((CivilizationUnitEffectStat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + size + index * 8)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + size + index * 8 + 4)));
                rule = rule with
                {
                    Trigger = (CivilizationUnitEffectTrigger)(triggerCode - 1),
                    Enabled = (BinaryPrimitives.ReadUInt32LittleEndian(record[36..]) & 0x80000000u) == 0,
                    ActivationMode = (CivilizationUnitEffectActivationMode)(BinaryPrimitives.ReadInt32LittleEndian(record[40..]) - 1),
                    DurationMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(record[44..]),
                    TechnologyId = BinaryPrimitives.ReadInt32LittleEndian(record[48..]),
                    Stat = version5 ? outputs.Length > 0 ? outputs[0].Stat : CivilizationUnitEffectStat.LineOfSight : version4 ? (CivilizationUnitEffectStat)BinaryPrimitives.ReadInt32LittleEndian(record[56..]) : CivilizationUnitEffectStat.LineOfSight,
                    Amount = version5 ? outputs.Length > 0 ? outputs[0].Amount : 0 : rule.Amount,
                    Outputs = outputCount > 1 ? outputs : null,
                    GrantFamilyTechnologyId = version6 ? BinaryPrimitives.ReadInt32LittleEndian(record[28..]) : 0,
                    Caption = Encoding.ASCII.GetString(bytes, offset + size + outputBytes, captionLength)
                };
                size += outputBytes + captionLength;
            }
            if (version7)
            {
                for (var field = 0; field < 2; field++)
                {
                    if (bytes.Length - offset - size < 4) throw new InvalidDataException("Runtime effect display metadata is truncated.");
                    var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + size));
                    if (nameLength is < 0 or > MaximumNameBytes || bytes.Length - offset - size - 4 < nameLength)
                        throw new InvalidDataException("Runtime effect display metadata has invalid framing.");
                    string name;
                    try { name = NameEncoding.GetString(bytes, offset + size + 4, nameLength); }
                    catch (DecoderFallbackException error) { throw new InvalidDataException("Runtime effect display metadata is not valid UTF-8.", error); }
                    rule = field == 0 ? rule with { Name = name } : rule with { TargetName = name };
                    size += 4 + nameLength;
                }
            }
            if (version8)
            {
                if (bytes.Length - offset - size < 8) throw new InvalidDataException("Runtime actions are truncated.");
                var scope = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + size));
                var actionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + size + 4));
                if (actionCount is < 0 or > CivilizationUnitEffects.MaximumOutputs) throw new InvalidDataException("Runtime action count is invalid.");
                size += 8;
                var actions = new List<CivilizationRuntimeUnitAction>();
                for (var actionIndex = 0; actionIndex < actionCount; actionIndex++)
                {
                    if (bytes.Length - offset - size < 24) throw new InvalidDataException("A runtime action is truncated.");
                    var actionBytes = bytes.AsSpan(offset + size, 24);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(actionBytes[20..]);
                    if (length is < 0 or > MaximumNameBytes || bytes.Length - offset - size - 24 < length) throw new InvalidDataException("A runtime action name is truncated.");
                    string name;
                    try { name = NameEncoding.GetString(bytes, offset + size + 24, length); }
                    catch (DecoderFallbackException error) { throw new InvalidDataException("A runtime action name is not UTF-8.", error); }
                    actions.Add(new((CivilizationUnitEffectOutputKind)BinaryPrimitives.ReadInt32LittleEndian(actionBytes),
                        BinaryPrimitives.ReadInt32LittleEndian(actionBytes[4..]), BinaryPrimitives.ReadInt32LittleEndian(actionBytes[8..]),
                        (CivilizationUnitEffectOwner)BinaryPrimitives.ReadInt32LittleEndian(actionBytes[12..]), BinaryPrimitives.ReadInt32LittleEndian(actionBytes[16..]), name));
                    size += 24 + length;
                }
                if (bytes.Length - offset - size < 44) throw new InvalidDataException("Runtime ability conditions are truncated.");
                var conditions = bytes.AsSpan(offset + size, 44);
                rule = rule with { Actions = actions.Count == 0 ? null : actions.ToArray(), TargetPlayers = (CivilizationUnitEffectTargetPlayers)scope,
                    FamilyId = new Guid(conditions[..16]), DelayMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(conditions[16..]),
                    MaxAffectedTargets = BinaryPrimitives.ReadInt32LittleEndian(conditions[20..]), MaxConcurrentTargets = BinaryPrimitives.ReadInt32LittleEndian(conditions[24..]),
                    FoodCost = BinaryPrimitives.ReadInt32LittleEndian(conditions[28..]), WoodCost = BinaryPrimitives.ReadInt32LittleEndian(conditions[32..]),
                    GoldCost = BinaryPrimitives.ReadInt32LittleEndian(conditions[36..]), StoneCost = BinaryPrimitives.ReadInt32LittleEndian(conditions[40..]) };
                size += 44;
            }
            Validate(rule);
            if (!version6 && rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility)
                throw new InvalidDataException("Proximity-granted abilities require runtime format version 6.");
            if (!ids.Add(rule.Id) || (rule.Trigger != CivilizationUnitEffectTrigger.Proximity && !technologies.Add(rule.TechnologyId)))
                throw new InvalidDataException("Runtime rules have duplicate effect or command identities.");
            units.Add(rule);
            offset += size;
        }
        if (offset != bytes.Length) throw new InvalidDataException("The runtime effect configuration has trailing data.");
        if (research.Any(rule => technologies.Contains(rule.TechnologyId)))
            throw new InvalidDataException("An ability command cannot also be used as a research bonus trigger.");
        foreach (var family in units.Where(rule => rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility)
            .GroupBy(rule => rule.GrantFamilyTechnologyId))
        {
            var anchor = family.SingleOrDefault(rule => rule.TechnologyId == family.Key);
            if (anchor.Id == Guid.Empty || family.Min(rule => rule.TechnologyId) != family.Key ||
                family.Select(rule => rule.TargetObjectId).Distinct().Count() != family.Count() ||
                family.Any(rule => rule.CivilizationId != anchor.CivilizationId || rule.SourceObjectId != anchor.SourceObjectId ||
                    rule.RadiusTiles != anchor.RadiusTiles || rule.Caption != anchor.Caption || rule.Name != anchor.Name || rule.Enabled != anchor.Enabled || rule.ActivationMode != anchor.ActivationMode ||
                    rule.FamilyId != anchor.FamilyId || rule.DelayMilliseconds != anchor.DelayMilliseconds || rule.MaxAffectedTargets != anchor.MaxAffectedTargets || rule.MaxConcurrentTargets != anchor.MaxConcurrentTargets ||
                    rule.FoodCost != anchor.FoodCost || rule.WoodCost != anchor.WoodCost || rule.GoldCost != anchor.GoldCost || rule.StoneCost != anchor.StoneCost ||
                    !GetOutputs(rule).SequenceEqual(GetOutputs(anchor)) || !EquivalentActions(rule, anchor)))
                throw new InvalidDataException("A proximity-granted ability family is missing its command or contains incompatible rules.");
        }
        return new(research, units);
    }

    private static void Validate(CivilizationRuntimeUnitRule rule)
    {
        if (rule.Id == Guid.Empty || rule.CivilizationId is < 4 or > 12 ||
            rule.SourceObjectId is < 0 or >= 100_000 || rule.TargetObjectId is < 0 or >= 100_000 ||
            !Enum.IsDefined(rule.Trigger) || !Enum.IsDefined(rule.ActivationMode) || !Enum.IsDefined(rule.TargetPlayers) || rule.Caption is null || rule.Name is null || rule.TargetName is null)
            throw new InvalidDataException("A runtime unit rule is invalid.");
        foreach (var name in new[] { rule.Name, rule.TargetName }.Concat(GetActions(rule).Select(action => action.ObjectName)))
        {
            if (name is null || name.Length > 100 || name != name.Trim() || name.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
                throw new InvalidDataException("Runtime effect display names must be trimmed, contain at most 100 characters and have no control characters.");
            try
            {
                if (NameEncoding.GetByteCount(name) > MaximumNameBytes)
                    throw new InvalidDataException("A runtime effect display name is too long.");
            }
            catch (EncoderFallbackException error) { throw new InvalidDataException("A runtime effect display name contains invalid Unicode.", error); }
        }
        var outputs = GetOutputs(rule);
        var actions = GetActions(rule);
        if (rule.DelayMilliseconds is < 0 or > 3600000 || rule.MaxAffectedTargets is < 0 or > 100000 || rule.MaxConcurrentTargets is < 0 or > 100000 ||
            new[] { rule.FoodCost, rule.WoodCost, rule.GoldCost, rule.StoneCost }.Any(cost => cost is < 0 or > 100000) ||
            (rule.Trigger == CivilizationUnitEffectTrigger.Proximity && (rule.FoodCost != 0 || rule.WoodCost != 0 || rule.GoldCost != 0 || rule.StoneCost != 0)) ||
            (rule.Trigger == CivilizationUnitEffectTrigger.Activated && (rule.DelayMilliseconds != 0 || rule.MaxAffectedTargets != 0 || rule.MaxConcurrentTargets != 0)))
            throw new InvalidDataException("Runtime ability delay, recipient limits or costs are invalid.");
        if (outputs.Count + actions.Count is < 1 or > CivilizationUnitEffects.MaximumOutputs || outputs.Count > 51)
            throw new InvalidDataException("A runtime unit effect must contain at least one output.");
        var stats = new HashSet<CivilizationUnitEffectStat>();
        foreach (var output in outputs)
        {
            if (!float.IsFinite(output.Amount) || !Enum.IsDefined(output.Stat) || !stats.Add(output.Stat))
                throw new InvalidDataException("A runtime unit effect has an invalid or duplicate stat output.");
            var stat = CivilizationUnitEffects.GetStatDefinition(output.Stat);
            if (!stat.RuntimeSupported)
                throw new InvalidDataException($"{stat.Name} does not yet have a supported runtime adapter.");
            if (output.Amount == 0 || output.Amount < (float)stat.MinimumAmount || output.Amount > (float)stat.MaximumAmount)
                throw new InvalidDataException($"The runtime adjustment for {stat.Name} is outside its supported range.");
            CivilizationUnitEffects.ValidateAmount(output.Stat, (decimal)output.Amount);
        }
        var actionKinds = new HashSet<CivilizationUnitEffectOutputKind>();
        foreach (var action in actions)
            if (action.Kind is not (CivilizationUnitEffectOutputKind.OwnershipTransfer or CivilizationUnitEffectOutputKind.Transform or CivilizationUnitEffectOutputKind.Spawn) ||
                !Enum.IsDefined(action.Owner) || action.PlayerSlot is < 1 or > 8 || action.Count is < 1 or > 64 ||
                (action.Kind is CivilizationUnitEffectOutputKind.Transform or CivilizationUnitEffectOutputKind.Spawn ? action.ObjectId is < 0 or >= 100_000 : action.ObjectId != -1) ||
                action.Kind != CivilizationUnitEffectOutputKind.Spawn && (action.Count != 1 || !actionKinds.Add(action.Kind)))
                throw new InvalidDataException("A permanent effect action is invalid.");
        if (actions.Where(action => action.Kind == CivilizationUnitEffectOutputKind.Spawn).Sum(action => action.Count) > 64)
            throw new InvalidDataException("An effect can spawn at most 64 objects per activation.");
        if (rule.ActivationMode == CivilizationUnitEffectActivationMode.Instant && (outputs.Count != 0 || actions.Count == 0) ||
            rule.Trigger != CivilizationUnitEffectTrigger.Proximity && rule.TargetPlayers != CivilizationUnitEffectTargetPlayers.Own)
            throw new InvalidDataException("Instant abilities require permanent actions only; player filtering belongs to proximity.");
        if (rule.Trigger == CivilizationUnitEffectTrigger.Proximity)
        {
            if (!rule.Enabled || rule.GrantFamilyTechnologyId != 0 || rule.RadiusTiles is < 1 or > 64 || rule.ActivationMode != CivilizationUnitEffectActivationMode.Toggle ||
                rule.DurationMilliseconds != 0 || rule.TechnologyId != 0 || rule.Caption.Length != 0)
                throw new InvalidDataException("A runtime proximity rule is invalid.");
        }
        else if ((rule.Trigger == CivilizationUnitEffectTrigger.ProximityAbility
                ? rule.GrantFamilyTechnologyId is < 10000 or > 14999 || rule.RadiusTiles is < 1 or > 64 || rule.ActivationMode == CivilizationUnitEffectActivationMode.Timed || rule.DurationMilliseconds != 0
                : rule.GrantFamilyTechnologyId != 0 || rule.RadiusTiles != 0 || rule.TargetObjectId != rule.SourceObjectId) || rule.TechnologyId is < 10000 or > 14999 ||
            string.IsNullOrWhiteSpace(rule.Caption) || rule.Caption.Length > 40 || rule.Caption.Any(character => character is < ' ' or > '~') ||
            (rule.ActivationMode != CivilizationUnitEffectActivationMode.Timed ? rule.DurationMilliseconds != 0 : rule.DurationMilliseconds is < 1000 or > 3_600_000))
            throw new InvalidDataException("A runtime activated ability is invalid.");
    }
}
