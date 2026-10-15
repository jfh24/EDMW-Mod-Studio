using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationGameIconReference(int ButtonId, string Name, string TexturePath);

/// <summary>Owns small, decoded icons from the selected game's button references and SSA/loose textures.</summary>
public sealed class CivilizationGameIcons : IDisposable
{
    private readonly Lazy<CivilizationGameAssets> _assets;
    private readonly Lazy<byte[]> _objects;
    private readonly Lazy<IReadOnlyDictionary<int, CivilizationTechnologyRecord>> _technologies;
    private readonly Lazy<IReadOnlyDictionary<int, CivilizationGameIconReference>> _buttons;
    private readonly Dictionary<string, Bitmap?> _images = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public CivilizationGameIcons(string gamePath)
    {
        var path = Path.GetFullPath(gamePath);
        _assets = new(() => new CivilizationGameAssets(path));
        _objects = new(() => ReadDatabase(path, "dbobjects.dat"));
        _technologies = new(() => CivilizationTechnologyBindings.ReadRecords(ReadDatabase(path, "dbtechtree.dat"))
            .ToDictionary(record => record.TechId));
        _buttons = new(() => ReadButtons(ReadDatabase(path, "dbbuttons.dat")));
    }

    // Returned images are owned by this provider; callers must not dispose them.
    public Image? ForEntity(CivilizationEntityDraft entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return TryRead(() =>
        {
            if (entity.SourceObjectId is int objectId)
            {
                var key = entity.Category == CivilizationCategory.Buildings ? "building.icon" : "unit.icon";
                if (entity.Properties.TryGetValue(key, out var value) && int.TryParse(value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var buttonId)) return ButtonReference(buttonId);
                return ObjectReference(objectId);
            }
            return entity.SourceTechnologyId is int technologyId ? TechnologyReference(technologyId) : null;
        });
    }

    public Image? ForObject(int objectId) => TryRead(() => ObjectReference(objectId));
    public Image? ForTechnology(int technologyId) => TryRead(() => TechnologyReference(technologyId));
    public Image? ForAge(int ageId) => ForTechnology(ageId);
    public Image? ForButton(int buttonId) => TryRead(() => ButtonReference(buttonId));

    public CivilizationGameIconReference? ObjectReference(int objectId)
    {
        var data = _objects.Value;
        if (data.Length < 4) throw new InvalidDataException("The object table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count is <= 0 or > 100_000 || data.LongLength != 4L + count * 1936L)
            throw new InvalidDataException("The object icon source has an unsupported layout.");
        if (objectId < 0 || objectId >= count) return null;
        var offset = 4 + objectId * 1936;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 116)) != objectId || data[offset + 232] == 0)
            return null;
        // Verified dbobjects buttonId binding, shared with the editable icon field.
        return ButtonReference(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 300)));
    }

    public CivilizationGameIconReference? TechnologyReference(int technologyId) =>
        _technologies.Value.TryGetValue(technologyId, out var record) && record.ButtonId <= int.MaxValue
            ? ButtonReference((int)record.ButtonId) : null;

    public CivilizationGameIconReference? ButtonReference(int buttonId) => _buttons.Value.GetValueOrDefault(buttonId);

    private Image? TryRead(Func<CivilizationGameIconReference?> source)
    {
        if (_disposed) return null;
        try
        {
            var reference = source();
            if (reference is null) return null;
            if (_images.TryGetValue(reference.TexturePath, out var existing)) return existing;
            // Keep images alive while visible cards borrow them, without unbounded native handles.
            if (_images.Count >= 4096) return null;
            Bitmap? image = null;
            try
            {
                var texture = _assets.Value.ReadTexture(reference.TexturePath);
                image = CreateThumbnail(texture);
            }
            catch (Exception error) when (MissingAsset(error)) { }
            _images.Add(reference.TexturePath, image);
            return image;
        }
        catch (Exception error) when (MissingAsset(error)) { return null; }
    }

    private static Bitmap CreateThumbnail(ModelTexture texture)
    {
        using var source = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        var pixels = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < source.Height; y++)
                Marshal.Copy(texture.Pixels, y * source.Width, IntPtr.Add(pixels.Scan0, y * pixels.Stride), source.Width);
        }
        finally { source.UnlockBits(pixels); }
        const int size = 64;
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var ratio = Math.Min(size / (float)source.Width, size / (float)source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        graphics.DrawImage(source, new Rectangle((size - width) / 2, (size - height) / 2, width, height));
        return output;
    }

    private static IReadOnlyDictionary<int, CivilizationGameIconReference> ReadButtons(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("The button table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count is <= 0 or > 100_000 || data.LongLength != 4L + count * 228L)
            throw new InvalidDataException("The button icon source has an unsupported layout.");
        var result = new Dictionary<int, CivilizationGameIconReference>();
        for (var row = 0; row < count; row++)
        {
            var record = data.AsSpan(4 + row * 228, 228);
            // Unused rows contain stale memory, not valid button records. Both identity and paths must match.
            if (BinaryPrimitives.ReadInt32LittleEndian(record[204..]) != row) continue;
            var name = ReadAscii(record[..100]);
            var texture = ReadAscii(record.Slice(100, 100))?.Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Whoops", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(texture) || !texture.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ||
                texture.Contains("..", StringComparison.Ordinal) || texture.Contains(':')) continue;
            result.Add(row, new(row, name, texture));
        }
        return result;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0) return null;
        foreach (var value in field[..end]) if (value is < 32 or > 126) return null;
        return Encoding.ASCII.GetString(field[..end]);
    }

    private static byte[] ReadDatabase(string path, string name)
    {
        using var stream = File.OpenRead(Path.Combine(path, "Data", "db", name));
        if (stream.Length > CivilizationGameAssets.MaximumModelBytes)
            throw new InvalidDataException("The icon source exceeds the input limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool MissingAsset(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or ExternalException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }
}
