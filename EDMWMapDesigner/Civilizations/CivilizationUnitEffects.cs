using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Civilizations;

public enum CivilizationUnitEffectTrigger { Proximity, Activated }
public enum CivilizationUnitEffectStat { LineOfSight }
public enum CivilizationUnitEffectActivationMode { Toggle, Timed }

/// <summary>An authored attachment; the source object and civilization belong to its enclosing entity.</summary>
public sealed record CivilizationUnitEffect
{
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Name { get; init; } = "New effect";
    [JsonRequired] public bool Enabled { get; init; } = true;
    [JsonRequired] public CivilizationUnitEffectTrigger Trigger { get; init; } = CivilizationUnitEffectTrigger.Proximity;
    [JsonRequired] public CivilizationUnitEffectStat Stat { get; init; } = CivilizationUnitEffectStat.LineOfSight;
    [JsonRequired] public int TargetObjectId { get; init; }
    [JsonRequired] public int Amount { get; init; } = 3;
    [JsonRequired] public int RadiusTiles { get; init; } = 5;
    [JsonRequired] public CivilizationUnitEffectActivationMode ActivationMode { get; init; } = CivilizationUnitEffectActivationMode.Timed;
    [JsonRequired] public int DurationSeconds { get; init; } = 30;
    [JsonRequired] public string ButtonCaption { get; init; } = "Activate ability";
    [JsonRequired] public int ButtonIconId { get; init; }
    [JsonRequired] public int ButtonSlot { get; init; } = 1;
    // Assigned only to a portable export after reserving an unused native command.
    public int? NativeTechnologyId { get; init; }
}

/// <summary>Custom attachments for experimental proximity effects and session-only activated abilities.</summary>
public static class CivilizationUnitEffects
{
    public const string PropertyKey = "runtime.unitEffects";
    public const int MaximumAttachments = 64;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static bool Supports(CivilizationEntityDraft entity) =>
        entity.Category is CivilizationCategory.Units or CivilizationCategory.Buildings;

    public static bool HasAttachments(CivilizationEntityDraft entity) => Read(entity).Count > 0;

    public static IReadOnlyList<CivilizationUnitEffect> Read(CivilizationEntityDraft entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!entity.Properties.TryGetValue(PropertyKey, out var value)) return [];
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{entity.Name}: effect attachments are empty or invalid.");
        try
        {
            var effects = JsonSerializer.Deserialize<List<CivilizationUnitEffect>>(value, Options)
                ?? throw new InvalidDataException($"{entity.Name}: effect attachments must be a list.");
            Validate(entity, effects);
            return effects.AsReadOnly();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{entity.Name}: effect attachments contain invalid data.", error);
        }
    }

    public static void Write(CivilizationEntityDraft entity, IEnumerable<CivilizationUnitEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(effects);
        var list = effects.ToList();
        Validate(entity, list);
        if (list.Count == 0) entity.Properties.Remove(PropertyKey);
        else entity.Properties[PropertyKey] = JsonSerializer.Serialize(list, Options);
    }

    public static void Validate(CivilizationEntityDraft entity) => _ = Read(entity);

    public static void Validate(CivilizationEntityDraft entity, IReadOnlyList<CivilizationUnitEffect> effects)
    {
        if (effects.Count == 0) return;
        if (!Supports(entity) || entity.SourceObjectId is not (>= 0 and < 100_000))
            throw new InvalidDataException($"{entity.Name}: custom unit effects require a unit or building from the game catalog.");
        if (effects.Count > MaximumAttachments)
            throw new InvalidDataException($"{entity.Name}: at most {MaximumAttachments} effects can be attached.");
        var ids = new HashSet<Guid>();
        var slots = new HashSet<int>();
        foreach (var effect in effects)
        {
            if (effect is null || !Guid.TryParse(effect.Id, out var id) || id == Guid.Empty || !ids.Add(id))
                throw new InvalidDataException($"{entity.Name}: every effect needs a unique identifier.");
            if (string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 100)
                throw new InvalidDataException("An effect name must contain 1 to 100 characters.");
            if (!Enum.IsDefined(effect.Trigger) || !Enum.IsDefined(effect.Stat) || !Enum.IsDefined(effect.ActivationMode))
                throw new InvalidDataException($"{effect.Name}: unsupported trigger, stat, or activation mode.");
            if (effect.TargetObjectId is < 0 or >= 100_000 || effect.ButtonIconId is < 0 or >= 100_000)
                throw new InvalidDataException($"{effect.Name}: select a valid target and button icon source.");
            if (effect.Amount is < -32 or > 32 || effect.Amount == 0)
                throw new InvalidDataException($"{effect.Name}: the sight adjustment must be nonzero and between -32 and 32 tiles.");
            if (effect.RadiusTiles is < 1 or > 64)
                throw new InvalidDataException($"{effect.Name}: proximity radius must be between 1 and 64 tiles.");
            if (effect.DurationSeconds is <= 0 or > 3600)
                throw new InvalidDataException($"{effect.Name}: duration must be greater than zero and at most 3,600 seconds.");
            if (effect.ButtonSlot is < 1 or > 41 || string.IsNullOrWhiteSpace(effect.ButtonCaption) || effect.ButtonCaption.Length > 40)
                throw new InvalidDataException($"{effect.Name}: choose a button slot from 1 to 41 and a caption of 1 to 40 characters.");
            if (effect.NativeTechnologyId is not null and not (>= 10000 and < 15000))
                throw new InvalidDataException($"{effect.Name}: the exported native command identity is invalid.");
            if (effect.Trigger == CivilizationUnitEffectTrigger.Activated)
            {
                if (effect.TargetObjectId != entity.SourceObjectId)
                    throw new InvalidDataException($"{effect.Name}: activated stat abilities currently affect the activating unit or building itself.");
                if (effect.Enabled && !slots.Add(effect.ButtonSlot))
                    throw new InvalidDataException($"{effect.Name}: enabled activated abilities must use different button slots.");
            }
        }
    }

    public static string Describe(CivilizationUnitEffect effect, Func<int, string>? objectName = null)
    {
        var adjustment = effect.Amount.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
        var target = objectName?.Invoke(effect.TargetObjectId) ?? $"object {effect.TargetObjectId}";
        var behavior = effect.Trigger == CivilizationUnitEffectTrigger.Proximity
            ? $"{target}: {adjustment} sight within {effect.RadiusTiles.ToString("0.##", CultureInfo.InvariantCulture)} tiles"
            : $"self: {adjustment} sight · {(effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle ? "toggle" : effect.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " seconds")}";
        return $"{effect.Name} · {(effect.Enabled ? "Enabled" : "Disabled")} · {behavior}";
    }
}
