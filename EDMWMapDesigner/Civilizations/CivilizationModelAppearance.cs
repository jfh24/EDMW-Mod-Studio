using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record ModelPlayerColor(string Name, int Argb);

/// <summary>A decoded game texture. Pixels are top-left-origin, straight-alpha ARGB.</summary>
public sealed class ModelTexture
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public ModelTexture(string name, int width, int height, int[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || (long)width * height != pixels.Length)
            throw new ArgumentException("Texture dimensions do not match its pixels.");
        Name = name;
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>Verified UDF frame ranges are inclusive and use milliseconds per frame.</summary>
public sealed record ModelAnimationClip(string Name, int StartFrame, int EndFrame, float FramesPerSecond)
{
    public double DurationSeconds => (EndFrame - StartFrame + 1) / (double)FramesPerSecond;
    public int Slot { get; init; } = -1;
    public string? ModelName { get; init; }
    public ModelTexture? Texture { get; init; }
}

/// <summary>The UDF selected by a graphics row supplies civilization-specific appearance.</summary>
public sealed class ModelAppearance
{
    public IReadOnlyDictionary<uint, ModelTexture> Textures { get; }
    public IReadOnlyList<ModelAnimationClip> Animations { get; }
    public string? UnitDefinitionName { get; }
    private readonly IReadOnlyDictionary<string, ModelTexture> _namedTextures;

    public ModelAppearance(IReadOnlyDictionary<uint, ModelTexture> textures,
        IReadOnlyList<ModelAnimationClip>? animations = null, string? unitDefinitionName = null,
        IReadOnlyDictionary<string, ModelTexture>? namedTextures = null)
    {
        Textures = textures;
        Animations = animations ?? Array.Empty<ModelAnimationClip>();
        UnitDefinitionName = unitDefinitionName;
        _namedTextures = namedTextures ?? new Dictionary<string, ModelTexture>(StringComparer.OrdinalIgnoreCase);
    }

    public ModelTexture? FindTexture(ModelMaterial material)
    {
        if (material.Name.Equals("player color", StringComparison.OrdinalIgnoreCase)) return null;
        if (Textures.TryGetValue(material.TextureId, out var texture)) return texture;
        if (_namedTextures.TryGetValue(material.TextureName, out texture)) return texture;
        return _namedTextures.GetValueOrDefault(material.Name);
    }

    public ModelAppearance ForAnimation(ModelAnimationClip animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (animation.Texture is null) return this;
        var textures = new Dictionary<uint, ModelTexture>(Textures) { [0] = animation.Texture };
        return new ModelAppearance(textures, Animations, UnitDefinitionName, _namedTextures);
    }
}

internal sealed record ModelUnitVariant(string ModelStem, string DiffuseTexture, string ShadowTexture);
internal sealed record ModelUnitAnimation(int StartFrame, int EndFrame, int FrameMilliseconds);

/// <summary>
/// USTR 20/21/22 framing follows the native reader at 0x0071D37E/0x0071DA7D/0x0071E196.
/// Six inheritable asset strings precede each of the 34 animation slots. Effects
/// and sound records precede the matching 24-byte animation range records.
/// </summary>
internal sealed class ModelUnitDefinition
{
    public IReadOnlyList<ModelUnitVariant> Variants { get; }
    public IReadOnlyList<ModelUnitAnimation> Animations { get; }

    private ModelUnitDefinition(ModelUnitVariant[] variants, ModelUnitAnimation[] animations)
    { Variants = Array.AsReadOnly(variants); Animations = Array.AsReadOnly(animations); }

    public static ModelUnitDefinition Read(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("USTR"u8))
            throw new InvalidDataException("The unit definition header is invalid.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        if (version is not (20 or 21 or 22)) throw new InvalidDataException("This unit definition version is not supported.");
        var cursor = 8;
        _ = ReadString(data, ref cursor);
        var count = ReadCount(data, ref cursor, 34);
        if (count != 34) throw new InvalidDataException("The unit definition animation asset table has an unsupported length.");
        var variants = new ModelUnitVariant[count];
        for (var slot = 0; slot < count; slot++)
        {
            var inherited = slot == 0 ? new string[6] : new[] { variants[0].ModelStem, variants[0].DiffuseTexture, variants[0].ShadowTexture, "", "", "" };
            for (var field = 0; field < 6; field++)
                if (ReadFlag(data, ref cursor)) inherited[field] = ReadString(data, ref cursor);
            _ = ReadFlag(data, ref cursor);
            variants[slot] = new(inherited[0] ?? "", inherited[1] ?? "", inherited[2] ?? "");
        }
        var effectCount = ReadCount(data, ref cursor, 8192);
        for (var effect = 0; effect < effectCount; effect++)
        {
            _ = ReadString(data, ref cursor);
            Skip(data, ref cursor, 12);
            _ = ReadString(data, ref cursor);
            Skip(data, ref cursor, 16);
        }
        var soundCount = ReadCount(data, ref cursor, 8192);
        for (var sound = 0; sound < soundCount; sound++)
        {
            _ = ReadString(data, ref cursor);
            Skip(data, ref cursor, 12);
        }
        var animationCount = ReadCount(data, ref cursor, 34);
        if (animationCount != count) throw new InvalidDataException("The unit definition's frame table does not match its asset table.");
        var animations = new ModelUnitAnimation[animationCount];
        for (var slot = 0; slot < animationCount; slot++)
        {
            var start = ReadCount(data, ref cursor, 100_000);
            var end = ReadCount(data, ref cursor, 100_000);
            var milliseconds = ReadCount(data, ref cursor, 1_000_000);
            _ = ReadCount(data, ref cursor, int.MaxValue); // Delay before fidgets.
            for (var flag = 0; flag < 4; flag++) _ = ReadFlag(data, ref cursor);
            _ = ReadCount(data, ref cursor, 1_000_000); // Transition blending time.
            // Stock menu assets occasionally carry a reversed unused range.
            // ReadModelAppearance excludes it without losing valid materials.
            animations[slot] = new(start, end, milliseconds);
        }
        // Later versions append physics and motion modifiers. They are retained
        // by the game; preview parsing does not reinterpret their payloads.
        if (data.Length - cursor < 50) throw new InvalidDataException("The unit definition trailer is truncated.");
        return new ModelUnitDefinition(variants, animations);
    }

    public static string AnimationLabel(string dataPath, int slot)
    {
        string[] labels = ["Attack", "Build", "Carry/Tread Water", "Chop", "Death", "Dead", "Farm/Swim", "Fidget", "Fidget2/Convert", "Mine", "Forage", "Hunt", "Transition", "Walk", "Alternate Attack 1", "Alternate Attack 2", "Alternate Death 1", "Alternate Death 2", "Alternate Dead 1", "Alternate Dead 2", "Cinematic 1", "Cinematic 2", "Cinematic 3", "Cinematic 4"];
        var fallback = slot < labels.Length ? labels[slot] : $"Animation {slot + 1}";
        var libraryPath = Path.Combine(Path.GetDirectoryName(dataPath)!, "Language.dll");
        if (!File.Exists(libraryPath)) return fallback;
        var module = LoadLibraryExW(libraryPath, IntPtr.Zero, 2); // Resource data only; no DLL code executes.
        if (module == IntPtr.Zero) return fallback;
        try
        {
            var value = new StringBuilder(512);
            // The native animation UI at 0x0060E6D0 uses 29825 + slot.
            return LoadStringW(module, checked((uint)(29825 + slot)), value, value.Capacity) > 0 && !string.IsNullOrWhiteSpace(value.ToString())
                ? value.ToString() : fallback;
        }
        finally { _ = FreeLibrary(module); }
    }

    private static int ReadCount(byte[] data, ref int cursor, int maximum)
    {
        if (cursor < 0 || data.Length - cursor < 4) throw new InvalidDataException("The unit definition is truncated.");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        if (value > maximum) throw new InvalidDataException("A unit definition count exceeds its limit.");
        return (int)value;
    }

    private static string ReadString(byte[] data, ref int cursor)
    {
        var count = ReadCount(data, ref cursor, 4096);
        if (count < 1 || data.Length - cursor < count) throw new InvalidDataException("A unit definition string is truncated.");
        var field = data.AsSpan(cursor, count);
        cursor += count;
        var terminator = field.IndexOf((byte)0);
        if (terminator < 0 || field[terminator..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("A unit definition string is not terminated.");
        foreach (var value in field[..terminator]) if (value < 32 || value > 126) throw new InvalidDataException("A unit definition string is not ASCII.");
        return Encoding.ASCII.GetString(field[..terminator]);
    }

    private static bool ReadFlag(byte[] data, ref int cursor)
    {
        if (cursor < 0 || cursor >= data.Length || data[cursor] > 1) throw new InvalidDataException("A unit definition flag is invalid.");
        return data[cursor++] == 1;
    }

    private static void Skip(byte[] data, ref int cursor, int length)
    {
        if (cursor < 0 || data.Length - cursor < length) throw new InvalidDataException("The unit definition is truncated.");
        cursor += length;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadStringW(IntPtr module, uint identifier, StringBuilder buffer, int capacity);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
