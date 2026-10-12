using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Read-only access to bounded loose and SSA model assets.</summary>
public sealed class CivilizationGameAssets
{
    public const int MaximumModelBytes = 16 * 1024 * 1024;
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private readonly string _dataPath;
    private readonly Dictionary<string, ArchiveEntry> _archiveAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<Dictionary<int, string>> _graphicsModels;
    private readonly Dictionary<int, string> _graphicsUnitDefinitions = [];
    private readonly Dictionary<string, ModelTexture> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _textureOrder = [];
    private int _cachedTextureBytes;
    private readonly Lazy<Dictionary<string, string>> _textureNames;
    private readonly Lazy<IReadOnlyList<ModelPlayerColor>> _playerColors;

    public IReadOnlyList<string> ModelNames { get; }
    public IReadOnlyList<ModelPlayerColor> PlayerColors => _playerColors.Value;
    internal IReadOnlyList<string> AssetNames { get; }

    public CivilizationGameAssets(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        _dataPath = Path.Combine(Path.GetFullPath(gamePath), "Data");
        // A supported moredata archive has precedence over the base archive.
        ReadArchiveIndex(Path.Combine(_dataPath, "moredata.ssa"));
        ReadArchiveIndex(Path.Combine(_dataPath, "data.ssa"));
        var assets = new HashSet<string>(_archiveAssets.Keys, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_dataPath))
            foreach (var path in Directory.EnumerateFiles(_dataPath, "*", SearchOption.AllDirectories))
                if (!path.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase))
                    assets.Add(NormalizeRelativePath(Path.GetRelativePath(_dataPath, path)));
        AssetNames = Array.AsReadOnly(assets.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        var names = new HashSet<string>(_archiveAssets.Keys.Where(name => name.StartsWith("models\\", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".cem", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        var looseFolder = Path.Combine(_dataPath, "Models");
        if (Directory.Exists(looseFolder))
            foreach (var path in Directory.EnumerateFiles(looseFolder, "*.cem", SearchOption.AllDirectories))
                names.Add(NormalizeModelPath(Path.GetRelativePath(_dataPath, path)));
        ModelNames = Array.AsReadOnly(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        _graphicsModels = new Lazy<Dictionary<int, string>>(ReadGraphicsModels);
        _textureNames = new Lazy<Dictionary<string, string>>(() => AssetNames
            .Where(name => name.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(name).ToLowerInvariant() is ".sst" or ".tga" or ".dds")
            .GroupBy(name => Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase));
        _playerColors = new Lazy<IReadOnlyList<ModelPlayerColor>>(ReadPlayerColors);
    }

    public byte[] ReadModel(string relativePath)
    {
        var name = NormalizeModelPath(relativePath);
        return ReadAssetFile(name);
    }

    internal byte[] ReadAssetFile(string relativePath)
    {
        var name = NormalizeRelativePath(relativePath);
        var loose = Path.GetFullPath(Path.Combine(_dataPath, name));
        if (!loose.StartsWith(_dataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The model path must remain inside the game's Data folder.", nameof(relativePath));
        if (File.Exists(loose)) return ReadBoundedFile(loose);
        if (!_archiveAssets.TryGetValue(name, out var entry))
            throw new FileNotFoundException($"The game model was not found: {name}");
        if (entry.Size > MaximumModelBytes)
            throw new InvalidDataException("The compressed model exceeds the 16 MB input limit.");
        using var stream = File.Open(entry.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (entry.Start < 0 || entry.Size < 0 || entry.Start > stream.Length - entry.Size)
            throw new InvalidDataException("The model archive changed or has an invalid asset range.");
        stream.Position = entry.Start;
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data.AsSpan().StartsWith("PK01"u8) ? DecompressPk01(data) : data;
    }

    /// <summary>
    /// Finds only an unambiguous model field in a graphics record. In this game
    /// build, dbobjects graphics references use the graphics table's row index,
    /// which is distinct from the graphics record's stored ID.
    /// </summary>
    public string? FindModelName(int graphicsId) => graphicsId >= 0 && _graphicsModels.Value.TryGetValue(graphicsId, out var name) ? name : null;

    /// <summary>Reads the selected unit's actual texture overrides and animation ranges.</summary>
    public ModelAppearance ReadModelAppearance(ModelMesh mesh, int? graphicsId = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ModelUnitDefinition? definition = null;
        string? unitPath = null;
        if (graphicsId is int id)
        {
            _ = _graphicsModels.Value;
            if (_graphicsUnitDefinitions.TryGetValue(id, out var stem)) unitPath = "units\\" + stem + ".udf";
        }
        else
        {
            var candidate = "units\\" + Path.GetFileNameWithoutExtension(mesh.Name) + ".udf";
            if (_archiveAssets.ContainsKey(candidate) || File.Exists(Path.Combine(_dataPath, candidate))) unitPath = candidate;
        }
        if (unitPath is not null)
        {
            try { definition = ModelUnitDefinition.Read(ReadAssetFile(unitPath)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { unitPath = null; }
        }
        var textures = new Dictionary<uint, ModelTexture>();
        var named = new Dictionary<string, ModelTexture>(StringComparer.OrdinalIgnoreCase);
        var diffuse = TryReadTexture(definition?.Variants.FirstOrDefault()?.DiffuseTexture);
        if (diffuse is not null) textures[0] = diffuse;
        foreach (var material in mesh.Materials)
        {
            if (material.Name.Equals("player color", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { material.TextureName, material.Name })
            {
                var texture = TryReadTexture(name);
                if (texture is not null) named.TryAdd(name, texture);
            }
        }
        var clips = new List<ModelAnimationClip>();
        if (definition is not null)
        {
            for (var slot = 0; slot < definition.Animations.Count; slot++)
            {
                var animation = definition.Animations[slot];
                var variant = definition.Variants[slot];
                var alternate = !string.Equals(variant.ModelStem, definition.Variants[0].ModelStem, StringComparison.OrdinalIgnoreCase);
                var textureChanges = !string.Equals(variant.DiffuseTexture, definition.Variants[0].DiffuseTexture, StringComparison.OrdinalIgnoreCase);
                if (animation.EndFrame < animation.StartFrame ||
                    (animation.EndFrame == animation.StartFrame && animation.StartFrame == 0 && !alternate && !textureChanges)) continue;
                if (animation.EndFrame > animation.StartFrame && animation.FrameMilliseconds <= 0) continue;
                var modelName = alternate ? ResolveModelAssetName(variant.ModelStem) : null;
                var frameCount = mesh.FrameCount;
                if (alternate)
                {
                    if (modelName is null) continue;
                    try { frameCount = CivilizationModelReader.Read(ReadModel(modelName), modelName).FrameCount; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    { continue; }
                }
                if (animation.EndFrame >= frameCount) continue;
                clips.Add(new ModelAnimationClip(ModelUnitDefinition.AnimationLabel(_dataPath, slot), animation.StartFrame,
                    animation.EndFrame, animation.FrameMilliseconds > 0 ? 1000f / animation.FrameMilliseconds : 1f)
                {
                    Slot = slot, ModelName = modelName,
                    Texture = string.Equals(variant.DiffuseTexture, definition.Variants[0].DiffuseTexture, StringComparison.OrdinalIgnoreCase)
                        ? diffuse : TryReadTexture(variant.DiffuseTexture)
                });
            }
        }
        return new ModelAppearance(textures, clips.AsReadOnly(), unitPath, named);
    }

    public ModelTexture ReadTexture(string name) => TryReadTexture(name) ?? throw new FileNotFoundException($"The game texture was not found: {name}");

    private ModelTexture? TryReadTexture(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var fileName = Path.GetFileName(stem.Replace('/', '\\'));
        var fileStem = _textureNames.Value.ContainsKey(fileName) ? fileName : Path.GetFileNameWithoutExtension(fileName);
        if (!_textureNames.Value.TryGetValue(fileStem, out var assetName)) return null;
        lock (_textureCache)
        {
            if (_textureCache.TryGetValue(assetName, out var cached)) return cached;
            try
            {
                var texture = CivilizationTextureReader.Read(ReadAssetFile(assetName), assetName);
                var bytes = checked(texture.Pixels.Length * sizeof(int));
                while (_cachedTextureBytes + bytes > 64 * 1024 * 1024 && _textureOrder.TryDequeue(out var oldest))
                    if (_textureCache.Remove(oldest, out var removed)) _cachedTextureBytes -= removed.Pixels.Length * sizeof(int);
                _textureCache[assetName] = texture;
                _textureOrder.Enqueue(assetName);
                _cachedTextureBytes += bytes;
                return texture;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            { return null; }
        }
    }

    private string? ResolveModelAssetName(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var candidates = ModelNames.Where(name => Path.GetFileNameWithoutExtension(name).Equals(stem, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private IReadOnlyList<ModelPlayerColor> ReadPlayerColors()
    {
        try
        {
            var data = ReadAssetFile("db\\dbcolortable.dat");
            if (data.Length < 4) return Array.Empty<ModelPlayerColor>();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (count < 16 || count > 100_000 || data.Length != 4L + count * 168L) return Array.Empty<ModelPlayerColor>();
            var colors = new List<ModelPlayerColor>();
            for (var index = 0; index < 16; index++)
            {
                var row = data.AsSpan(4 + index * 168, 168);
                var name = ReadAsciiField(row[..100]);
                if (name is null || !name.StartsWith("Player ", StringComparison.OrdinalIgnoreCase) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(row[104..]) != index) return Array.Empty<ModelPlayerColor>();
                var rgb = new int[3];
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(row[(108 + channel * 4)..]);
                    if (!float.IsFinite(value) || value is < 0 or > 1) return Array.Empty<ModelPlayerColor>();
                    rgb[channel] = (int)MathF.Round(value * 255);
                }
                var opening = name.IndexOf('(');
                var closing = name.LastIndexOf(')');
                if (opening >= 0 && closing > opening) name = name[(opening + 1)..closing];
                colors.Add(new ModelPlayerColor(name, unchecked((int)(0xff000000u | (uint)rgb[0] << 16 | (uint)rgb[1] << 8 | (uint)rgb[2]))));
            }
            return colors.AsReadOnly();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return Array.Empty<ModelPlayerColor>(); }
    }

    private void ReadArchiveIndex(string path)
    {
        if (!File.Exists(path)) return;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 16) throw new InvalidDataException($"The archive header is truncated: {Path.GetFileName(path)}");
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        // Some distributions store moredata.ssa as compound storage rather than
        // an SSA. Do not interpret that distinct container as an SSA or guess.
        if (header[..8].SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 })) return;
        if (!header[..4].SequenceEqual("rass"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 0)
            throw new InvalidDataException($"The archive has an unsupported SSA version: {Path.GetFileName(path)}");
        var indexLength = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (indexLength > MaximumIndexBytes || indexLength > stream.Length - 16)
            throw new InvalidDataException("The SSA index length is invalid.");
        var index = new byte[(int)indexLength];
        stream.ReadExactly(index);
        var cursor = 0;
        var entryCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<KeyValuePair<string, ArchiveEntry>>();
        while (cursor < index.Length)
        {
            if (++entryCount > 100_000 || index.Length - cursor < 4)
                throw new InvalidDataException("The SSA index is truncated or contains too many entries.");
            var pathLength = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor, 4));
            cursor += 4;
            if (pathLength is < 2 or > 4096 || pathLength > index.Length - cursor - 12)
                throw new InvalidDataException("An SSA entry has an invalid path length.");
            var pathBytes = index.AsSpan(cursor, (int)pathLength);
            cursor += (int)pathLength;
            var terminator = pathBytes.IndexOf((byte)0);
            if (terminator <= 0 || pathBytes[(terminator + 1)..].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("An SSA entry path is not properly terminated.");
            foreach (var value in pathBytes[..terminator])
                if (value < 32 || value > 126) throw new InvalidDataException("An SSA entry has a non-ASCII path.");
            var entryName = NormalizeRelativePath(Encoding.ASCII.GetString(pathBytes[..terminator]));
            if (!seen.Add(entryName)) throw new InvalidDataException("The SSA index contains duplicate asset paths.");
            var start = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor, 4));
            var end = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor + 4, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(cursor + 8, 4));
            cursor += 12;
            // SSA ends are inclusive. Index metadata is followed by other
            // archive metadata, so the first payload can begin after indexEnd.
            if (start < 16L + indexLength || end < start || end >= stream.Length || (long)end - start + 1 != size)
                throw new InvalidDataException("An SSA entry has an invalid payload range.");
            if (size <= MaximumModelBytes)
            {
                found.Add(new(entryName, new ArchiveEntry(path, start, checked((int)size))));
            }
        }
        foreach (var entry in found) _archiveAssets.TryAdd(entry.Key, entry.Value);
    }

    private Dictionary<int, string> ReadGraphicsModels()
    {
        var path = Path.Combine(_dataPath, "db", "dbgraphics.dat");
        if (!File.Exists(path)) return [];
        var data = ReadBoundedFile(path);
        if (data.Length < 4) return [];
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count is <= 0 or > 100_000) return [];
        var byStem = ModelNames.GroupBy(name => Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var resolvedStems = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<GraphicsAnchor>();
        // Graphics records have variable framing. These fixed name/model and
        // identity fields are accepted only when the model exists, both strings
        // terminate within their 100-byte field, and both IDs are in bounds.
        // Padding after a string's NUL contains engine data, not necessarily zero.
        var text = Encoding.Latin1.GetString(data);
        foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9_][A-Za-z0-9_ .()\-]{0,98}\x00", RegexOptions.CultureInvariant))
        {
            var offset = match.Index;
            if (offset < 104 || offset > data.Length - 108) continue;
            var model = ReadAsciiField(data.AsSpan(offset, 100));
            var display = ReadAsciiField(data.AsSpan(offset - 100, 100));
            if (string.IsNullOrWhiteSpace(model) || display is null) continue;
            var storedId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 100, 4));
            var row = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 104, 4));
            if (storedId is <= 0 or > 1_000_000 || row < 0 || row >= count) continue;
            if (!resolvedStems.TryGetValue(model, out var fullName))
            {
                fullName = ResolveModelStem(model, byStem);
                resolvedStems.Add(model, fullName);
            }
            if (fullName is null) continue;
            candidates.Add(new(row, storedId, offset, fullName, model));
        }
        var unique = candidates.GroupBy(candidate => candidate.Row).Where(group => group.Count() == 1).Select(group => group.Single()).OrderBy(candidate => candidate.Offset).ToArray();
        var storedIds = unique.GroupBy(candidate => candidate.StoredId).ToDictionary(group => group.Key, group => group.Count());
        var result = new Dictionary<int, string>();
        for (var i = 0; i < unique.Length; i++)
        {
            var anchor = unique[i];
            if (storedIds[anchor.StoredId] != 1) continue;
            // Require a neighboring named record with consecutive row identity
            // and ordered byte position. Isolated candidates remain unresolved.
            var previousMatches = i > 0 && unique[i - 1].Row + 1 == anchor.Row;
            var nextMatches = i + 1 < unique.Length && anchor.Row + 1 == unique[i + 1].Row;
            if (previousMatches || nextMatches)
            {
                result.Add(anchor.Row, anchor.Model);
                _graphicsUnitDefinitions[anchor.Row] = anchor.UnitStem;
            }
        }
        return result;
    }

    private string? ResolveModelStem(string stem, Dictionary<string, string> modelNames)
    {
        // The graphics model field frequently names a UDF rather than a CEM.
        // Its verified USTR 20/21/22 header supplies the primary mesh name.
        // Citizen and Knight share mesh files with other civilizations, so
        // deriving filenames from country names would give the wrong result.
        var directModel = modelNames.GetValueOrDefault(stem);
        var unitPath = "units\\" + stem + ".udf";
        if (_archiveAssets.ContainsKey(unitPath) || File.Exists(Path.Combine(_dataPath, unitPath)))
        {
            try
            {
                var data = ReadAssetFile(unitPath);
                if (data.Length < 17 || !data.AsSpan(0, 4).SequenceEqual("USTR"u8)) return directModel;
                var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
                if (version is not (20 or 21 or 22)) return directModel;
                var cursor = 8;
                _ = ReadUdfString(data, ref cursor); // Human-readable unit name.
                if (data.Length - cursor < 5 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)) != 34 || data[cursor + 4] != 1)
                    return directModel;
                cursor += 5;
                var meshStem = ReadUdfString(data, ref cursor);
                if (modelNames.TryGetValue(meshStem, out var model)) return model;
                // A medium LOD is accepted only for the explicitly named mesh.
                return modelNames.GetValueOrDefault(meshStem + "_med") ?? directModel;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                return directModel;
            }
        }
        return directModel ?? modelNames.GetValueOrDefault(stem + "_med");
    }

    private static string ReadUdfString(byte[] data, ref int cursor)
    {
        if (data.Length - cursor < 4) throw new InvalidDataException("The UDF string length is truncated.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        if (length is < 2 or > 1024 || length > data.Length - cursor) throw new InvalidDataException("The UDF string length is invalid.");
        var field = data.AsSpan(cursor, (int)length);
        cursor += (int)length;
        var terminator = field.IndexOf((byte)0);
        if (terminator < 0 || field[terminator..].IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("The UDF string is not terminated.");
        foreach (var value in field[..terminator]) if (value < 32 || value > 126) throw new InvalidDataException("The UDF string is not ASCII.");
        return Encoding.ASCII.GetString(field[..terminator]);
    }

    private static string? ReadAsciiField(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0) return null;
        foreach (var value in field[..end]) if (value < 32 || value > 126) return null;
        return Encoding.ASCII.GetString(field[..end]);
    }

    private static byte[] ReadBoundedFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumModelBytes) throw new InvalidDataException("The model or graphics database exceeds the 16 MB input limit.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static string NormalizeModelPath(string path)
    {
        var name = NormalizeRelativePath(path);
        if (!name.StartsWith("models\\", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".cem", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a relative models\\name.cem game asset.", nameof(path));
        return name;
    }

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = path.Replace('/', '\\');
        if (Path.IsPathRooted(name) || name.Contains(':')) throw new ArgumentException("Asset paths must be relative.", nameof(path));
        var segments = name.Split('\\');
        foreach (var segment in segments)
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The asset path contains an invalid segment.", nameof(path));
        return string.Join('\\', segments);
    }

    internal static byte[] DecompressPk01(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14 || !data[..4].SequenceEqual("PK01"u8)) throw new InvalidDataException("The PK01 compressed model header is invalid.");
        var outputSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (outputSize > MaximumModelBytes) throw new InvalidDataException("The decompressed model exceeds the 16 MB output limit.");
        return DclBlast.Decode(data[12..], (int)outputSize);
    }

    private sealed record ArchiveEntry(string ArchivePath, long Start, int Size);
    private sealed record GraphicsAnchor(int Row, int StoredId, int Offset, string Model, string UnitStem);

    /* Altered C# implementation of Mark Adler's blast 1.3 (24 Aug 2013).
     * Source: https://github.com/madler/zlib/tree/master/contrib/blast
     * The implementation below uses bounded buffers and managed exceptions
     * instead of callbacks, a circular output window, and setjmp/longjmp.
     *
     * Copyright (C) 2003, 2012, 2013 Mark Adler
     *
     * This software is provided 'as-is', without any express or implied
     * warranty.  In no event will the author be held liable for any damages
     * arising from the use of this software.
     *
     * Permission is granted to anyone to use this software for any purpose,
     * including commercial applications, and to alter it and redistribute it
     * freely, subject to the following restrictions:
     * 1. The origin of this software must not be misrepresented; you must not
     *    claim that you wrote the original software. If you use this software
     *    in a product, an acknowledgment in the product documentation would be
     *    appreciated but is not required.
     * 2. Altered source versions must be plainly marked as such, and must not be
     *    misrepresented as being the original software.
     * 3. This notice may not be removed or altered from any source distribution.
     * Mark Adler    madler@alumni.caltech.edu
     */
    private static class DclBlast
    {
        private static readonly Huffman Literals = new([11,124,8,7,28,7,188,13,76,4,10,8,12,10,12,10,8,23,8,9,7,6,7,8,7,6,55,8,23,24,12,11,7,9,11,12,6,7,22,5,7,24,6,11,9,6,7,22,7,11,38,7,9,8,25,11,8,11,9,12,8,12,5,38,5,38,5,11,7,5,6,21,6,10,53,8,7,24,10,27,44,253,253,253,252,252,252,13,12,45,12,45,12,61,12,45,44,173]);
        private static readonly Huffman Lengths = new([2,35,36,53,38,23]);
        private static readonly Huffman Distances = new([2,20,53,230,247,151,248]);
        private static readonly int[] LengthBase = [3,2,4,5,6,7,8,9,10,12,16,24,40,72,136,264];
        private static readonly int[] LengthExtra = [0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8];

        public static byte[] Decode(ReadOnlySpan<byte> compressed, int outputSize)
        {
            var bits = new BitReader(compressed);
            var codedLiterals = bits.Read(8);
            var dictionaryBits = bits.Read(8);
            if (codedLiterals > 1 || dictionaryBits is < 4 or > 6) throw new InvalidDataException("The DCL stream header is invalid.");
            var output = new byte[outputSize];
            var position = 0;
            while (true)
            {
                if (bits.Read(1) == 0)
                {
                    var literal = codedLiterals == 1 ? Literals.Decode(ref bits) : bits.Read(8);
                    if (position >= output.Length) throw new InvalidDataException("The DCL stream exceeds its declared output size.");
                    output[position++] = (byte)literal;
                    continue;
                }
                var symbol = Lengths.Decode(ref bits);
                var length = LengthBase[symbol] + bits.Read(LengthExtra[symbol]);
                if (length == 519) break;
                var distanceBits = length == 2 ? 2 : dictionaryBits;
                var distance = (Distances.Decode(ref bits) << distanceBits) + bits.Read(distanceBits) + 1;
                if (distance > position || length > output.Length - position) throw new InvalidDataException("The DCL stream has an invalid back-reference or output length.");
                for (var copied = 0; copied < length; copied++) { output[position] = output[position - distance]; position++; }
            }
            if (position != output.Length) throw new InvalidDataException("The DCL output does not match its declared size.");
            return output;
        }

        private sealed class Huffman
        {
            private readonly int[] _counts = new int[14];
            private readonly int[] _symbols;

            public Huffman(byte[] repeatedLengths)
            {
                var lengths = new List<int>();
                foreach (var item in repeatedLengths)
                    for (var repeat = 0; repeat < (item >> 4) + 1; repeat++) lengths.Add(item & 15);
                foreach (var length in lengths)
                {
                    if (length > 13) throw new InvalidOperationException("Invalid built-in DCL code table.");
                    _counts[length]++;
                }
                var remaining = 1;
                for (var length = 1; length <= 13; length++)
                {
                    remaining = (remaining << 1) - _counts[length];
                    if (remaining < 0) throw new InvalidOperationException("Oversubscribed built-in DCL code table.");
                }
                var offsets = new int[14];
                for (var length = 1; length < 13; length++) offsets[length + 1] = offsets[length] + _counts[length];
                _symbols = new int[lengths.Count];
                for (var symbol = 0; symbol < lengths.Count; symbol++) if (lengths[symbol] > 0) _symbols[offsets[lengths[symbol]]++] = symbol;
            }

            public int Decode(ref BitReader bits)
            {
                var code = 0;
                var first = 0;
                var index = 0;
                for (var length = 1; length <= 13; length++)
                {
                    code |= bits.Read(1) ^ 1;
                    var count = _counts[length];
                    if (code >= first && code < first + count) return _symbols[index + code - first];
                    index += count;
                    first = (first + count) << 1;
                    code <<= 1;
                }
                throw new InvalidDataException("The DCL stream contains an invalid Huffman code.");
            }
        }

        private ref struct BitReader(ReadOnlySpan<byte> input)
        {
            private readonly ReadOnlySpan<byte> _input = input;
            private int _offset;
            private uint _buffer;
            private int _count;

            public int Read(int count)
            {
                while (_count < count)
                {
                    if (_offset >= _input.Length) throw new InvalidDataException("The DCL stream is truncated.");
                    _buffer |= (uint)_input[_offset++] << _count;
                    _count += 8;
                }
                var value = (int)(_buffer & ((1u << count) - 1));
                _buffer >>= count;
                _count -= count;
                return value;
            }
        }
    }
}
