using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

/// <summary>A portable replacement model stored inside the draft project, never installed into the game.</summary>
public sealed class CivilizationModelAsset
{
    public const int MaximumDataBytes = 16 * 1024 * 1024;

    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Replacement model";
    public string OriginalFileName { get; set; } = "";
    public string Format { get; set; } = "";
    public string MimeType { get; set; } = "";
    public int FileLength { get; set; }
    // System.Text.Json encodes byte arrays as base64, keeping the project self-contained.
    public byte[] Data { get; set; } = [];
    public string Sha256 { get; set; } = "";

    /// <summary>Reads a user-selected file. Mesh parsing and preview validation belong to the renderer.</summary>
    public static CivilizationModelAsset Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);
        var extension = Path.GetExtension(resolved).ToLowerInvariant();
        if (extension is not (".cem" or ".obj"))
            throw new InvalidDataException("Choose an EDMW .cem model or a Wavefront .obj model.");
        using var stream = File.Open(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumDataBytes)
            throw new InvalidDataException("A replacement model must contain 1 byte to 16 MB.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        var name = Path.GetFileNameWithoutExtension(resolved);
        if (name.Length > 200) name = name[..200];
        var asset = new CivilizationModelAsset
        {
            Name = name,
            OriginalFileName = Path.GetFileName(resolved),
            Format = extension[1..],
            MimeType = extension == ".obj" ? "text/plain" : "application/octet-stream",
            FileLength = data.Length,
            Data = data,
            Sha256 = Convert.ToHexString(SHA256.HashData(data))
        };
        asset.Validate();
        return asset;
    }

    public CivilizationModelAsset Clone() => new()
    {
        Id = Id, Name = Name, OriginalFileName = OriginalFileName,
        Format = Format, MimeType = MimeType, FileLength = FileLength,
        Data = (byte[])Data.Clone(), Sha256 = Sha256
    };

    public void Validate()
    {
        if (!Guid.TryParse(Id, out var guid) || guid == Guid.Empty)
            throw new InvalidDataException("A model asset must have a nonempty GUID identity.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
            throw new InvalidDataException("A model asset name must contain 1 to 200 characters.");
        if (string.IsNullOrWhiteSpace(OriginalFileName) || OriginalFileName.Length > 255 ||
            OriginalFileName.IndexOfAny(['/', '\\']) >= 0 ||
            OriginalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"{Name}: the original model filename is invalid.");
        var extension = Path.GetExtension(OriginalFileName).ToLowerInvariant();
        if (extension is not (".cem" or ".obj") ||
            !string.Equals(Format, extension[1..], StringComparison.Ordinal))
            throw new InvalidDataException($"{Name}: the model format must match its .cem or .obj filename.");
        var expectedMimeType = extension == ".obj" ? "text/plain" : "application/octet-stream";
        if (MimeType != expectedMimeType)
            throw new InvalidDataException($"{Name}: the model content type does not match its format.");
        if (Data is null || Data.Length is <= 0 or > MaximumDataBytes || FileLength != Data.Length)
            throw new InvalidDataException($"{Name}: model data must contain 1 byte to 16 MB and match its recorded length.");
        var expectedHash = Convert.ToHexString(SHA256.HashData(Data));
        if (!string.Equals(Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Name}: model data does not match its SHA-256 fingerprint.");
    }
}
