using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

public sealed record MapLanFile(string RelativePath, int Length);

/// <summary>An immutable, verified copy of the existing multiplayer export archive.</summary>
public sealed partial class MapLanPackage
{
    private const string ScriptsFolder = "Data/Random Map Scripts/";
    private const string PreviewPath = "EDMW Installer/map-preview.png";
    private const int MaximumEntryBytes = 16 * 1024 * 1024;
    private const int MaximumScriptBytes = 4 * 1024 * 1024;
    private const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private const int MaximumPreviewBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _archive;
    private readonly byte[] _preview;
    private readonly PackageFile[] _files;

    public string MapName { get; }
    public uint MapId { get; }
    public byte[] PreviewBytes => (byte[])_preview.Clone();
    public IReadOnlyList<MapLanFile> Files { get; }

    private MapLanPackage(byte[] archive, uint mapId, string mapName, byte[] preview, PackageFile[] files)
    {
        _archive = archive; _preview = preview; _files = files; MapId = mapId; MapName = mapName;
        Files = Array.AsReadOnly(files.Select(file => new MapLanFile(file.RelativePath, file.Bytes.Length)).ToArray());
    }

    public static MapLanPackage FromArchive(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.Length is < 1 or > MapLanTransfer.MaxPayloadBytes)
            throw new InvalidDataException("The map export size is invalid.");
        // Keep our own copy so a sender or caller cannot change reviewed bytes during installation.
        var copy = (byte[])archiveBytes.Clone();
        using var memory = new MemoryStream(copy, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Count != 4) throw new InvalidDataException("The map export must contain exactly a generator, RMV script, terrain payload, and preview.");
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, ReadEntry(entry))) throw new InvalidDataException("The map export contains duplicate paths.");
        }
        var generatorPath = entries.Keys.SingleOrDefault(path => !path.Contains('/') && path.EndsWith(".dll", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The map generator is missing.");
        if (!AnchorPackage.TryParseGeneratorName(Path.GetFileNameWithoutExtension(generatorPath), out var mapId) ||
            generatorPath != AnchorPackage.GeneratorFileNameFor(mapId))
            throw new InvalidDataException("The map generator name has an invalid map identity.");
        var scriptPath = entries.Keys.SingleOrDefault(path => path.StartsWith(ScriptsFolder, StringComparison.Ordinal) &&
            !path[ScriptsFolder.Length..].Contains('/') && path.EndsWith(".rmv", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The map RMV script is missing.");
        var payloadPath = $"{ScriptsFolder}{AnchorPackage.DataFolderName}/{mapId:X6}.edmwa";
        if (!entries.TryGetValue(payloadPath, out var payload) || !entries.TryGetValue(PreviewPath, out var preview) ||
            entries.Keys.Any(path => path != generatorPath && path != scriptPath && path != payloadPath && path != PreviewPath))
            throw new InvalidDataException("The map export paths do not match its identity.");
        var generator = entries[generatorPath];
        EmbeddedGenerator.ValidateX86Dll(generator);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(generator), SHA256.HashData(EmbeddedGenerator.Read())))
            throw new InvalidDataException("The map generator does not match this editor's verified generator. Use the same editor version on each computer.");
        ValidatePayload(payload, mapId);
        ValidateScript(entries[scriptPath], mapId);
        ValidatePreview(preview);
        return new(copy, mapId, Path.GetFileNameWithoutExtension(scriptPath), preview,
            [new(generatorPath, generator), new(payloadPath, payload), new(scriptPath, entries[scriptPath])]);
    }

    /// <summary>Copies a verified installed map without loading a project or writing game data.</summary>
    internal static MapLanPackage FromInstalledFiles(string gamePath, string scriptFileName, uint mapId,
        bool hidePreview = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptFileName);
        if (scriptFileName != Path.GetFileName(scriptFileName) ||
            !string.Equals(Path.GetExtension(scriptFileName), ".rmv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed map script name is invalid.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        if (!GameInstallationLocator.IsValid(root) ||
            !Directory.Exists(Path.Combine(root, "Data", "Random Map Scripts")))
            throw new InvalidOperationException("The installed map's game folder was not found.");
        CheckPlainPath(root);
        CheckPlainPath(Path.Combine(root, GameInstallationLocator.GameExecutableName));
        var generatorPath = AnchorPackage.GeneratorFileNameFor(mapId);
        var installedScriptPath = ScriptsFolder + scriptFileName;
        ValidateArchivePath(installedScriptPath);
        var scriptPath = ScriptsFolder + Path.ChangeExtension(scriptFileName, ".rmv");
        var payloadPath = $"{ScriptsFolder}{AnchorPackage.DataFolderName}/{mapId:X6}.edmwa";
        var generator = ReadBounded(ResolveTarget(root, generatorPath), MaximumEntryBytes);
        var script = ReadBounded(ResolveTarget(root, installedScriptPath), MaximumScriptBytes);
        var payload = ReadBounded(ResolveTarget(root, payloadPath), MaximumPayloadBytes);
        var map = ValidatePayload(payload, mapId);
        EmbeddedGenerator.ValidateX86Dll(generator);
        var verifiedGenerator = EmbeddedGenerator.Read();
        EmbeddedGenerator.ValidateX86Dll(verifiedGenerator);
        // Earlier editor builds can install another x86 generator under the same
        // content-addressed map identity. Use the supported current generator for the
        // verified terrain format instead of distributing unknown installed native code.
        // This upgrades only the outgoing package; the sender's installation is untouched.
        if (!generator.AsSpan().SequenceEqual(verifiedGenerator)) generator = verifiedGenerator;

        // Previews are review metadata and are never installed. Reconstruct that image
        // from the validated terrain while retaining installed RMV and terrain bytes exactly.
        var preview = hidePreview ? MultiplayerPackageExporter.RenderRedactedPreview() :
            MultiplayerPackageExporter.RenderPreview(map);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WritePackageEntry(archive, generatorPath, generator);
            WritePackageEntry(archive, scriptPath, script);
            WritePackageEntry(archive, payloadPath, payload);
            WritePackageEntry(archive, PreviewPath, preview);
        }
        return FromArchive(memory.ToArray());
    }

    private static void WritePackageEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Checks a recipient folder and all existing targets without creating or modifying anything.</summary>
    public string ValidateDestination(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Empires_DMW.exe")) ||
            !Directory.Exists(Path.Combine(root, "Data", "Random Map Scripts")))
            throw new InvalidOperationException("Choose the game folder containing Empires_DMW.exe and Data\\Random Map Scripts.");
        CheckPlainPath(root);
        CheckPlainPath(Path.Combine(root, "Empires_DMW.exe"));
        foreach (var file in _files)
        {
            var target = ResolveTarget(root, file.RelativePath);
            if (Directory.Exists(target)) throw new InvalidOperationException($"A folder already occupies {file.RelativePath}.");
            if (File.Exists(target)) ValidateExisting(root, file.RelativePath, ReadBounded(target, MaximumEntryBytes));
        }
        return root;
    }

    /// <summary>Installs reviewed data directly. Received executable installers are never run.</summary>
    public string Install(string gamePath) => InstallCore(gamePath, null);

    internal string InstallForVerification(string gamePath, Action<int> afterCommit) => InstallCore(gamePath, afterCommit);

    private string InstallCore(string gamePath, Action<int>? afterCommit)
    {
        var root = ValidateDestination(gamePath);
        var runningGames = Process.GetProcessesByName("Empires_DMW");
        var gameIsRunning = runningGames.Length > 0;
        foreach (var process in runningGames) process.Dispose();
        if (gameIsRunning)
            throw new InvalidOperationException("Close Empires: Dawn of the Modern World before installing the map.");
        var writes = _files.Select(file => new PendingWrite(file, ResolveTarget(root, file.RelativePath))).ToArray();
        var createdDirectories = new List<string>();
        var committed = new List<PendingWrite>();
        try
        {
            // Probe every existing target before staging or replacing any file.
            foreach (var write in writes)
            {
                if (!File.Exists(write.Target)) continue;
                using var probe = new FileStream(write.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (probe.Length > MaximumEntryBytes) throw new InvalidDataException("An existing map file is too large to replace safely.");
                write.Original = new byte[(int)probe.Length];
                probe.ReadExactly(write.Original);
                ValidateExisting(root, write.File.RelativePath, write.Original);
            }
            foreach (var write in writes)
            {
                var parent = Path.GetDirectoryName(write.Target)!;
                CreateDirectories(parent, root, createdDirectories);
                write.Staged = write.Target + $".{Guid.NewGuid():N}.tmp";
                WriteStaged(write.Staged, write.File.Bytes);
            }
            foreach (var write in writes)
            {
                _ = ResolveTarget(root, write.File.RelativePath);
                var exists = File.Exists(write.Target);
                if (exists != (write.Original is not null) || (exists && !ReadBounded(write.Target, MaximumEntryBytes).AsSpan().SequenceEqual(write.Original)))
                    throw new IOException("A map file changed during installation. Try the export again.");
                File.Move(write.Staged!, write.Target, overwrite: write.Original is not null);
                write.Staged = null;
                committed.Add(write);
                afterCommit?.Invoke(committed.Count);
            }
            return root;
        }
        catch (Exception originalError) when (originalError is not OutOfMemoryException and not StackOverflowException)
        {
            Exception? rollbackError = null;
            foreach (var write in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (!ReadBounded(write.Target, MaximumEntryBytes).AsSpan().SequenceEqual(write.File.Bytes))
                        throw new IOException("An installed map file changed before rollback.");
                    if (write.Original is null) File.Delete(write.Target);
                    else
                    {
                        var restore = write.Target + $".{Guid.NewGuid():N}.tmp";
                        try { WriteStaged(restore, write.Original); File.Move(restore, write.Target, true); }
                        finally { if (File.Exists(restore)) File.Delete(restore); }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { rollbackError ??= error; }
            }
            if (rollbackError is not null) throw new IOException("Map installation failed and a map file could not be restored. " + rollbackError.Message, originalError);
            throw;
        }
        finally
        {
            foreach (var write in writes)
                if (write.Staged is not null && File.Exists(write.Staged)) File.Delete(write.Staged);
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    internal byte[] ArchiveBytes() => (byte[])_archive.Clone();

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        var limit = entry.FullName.EndsWith(".rmv", StringComparison.Ordinal) ? MaximumScriptBytes :
            entry.FullName.EndsWith(".edmwa", StringComparison.Ordinal) ? MaximumPayloadBytes :
            entry.FullName == PreviewPath ? MaximumPreviewBytes : MaximumEntryBytes;
        if (entry.Length is < 1 || entry.Length > limit || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new InvalidDataException("The map export contains an invalid or oversized entry.");
        using var stream = entry.Open();
        var bytes = new byte[(int)entry.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("A map export entry exceeds its declared size.");
        return bytes;
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || Path.IsPathRooted(path) ||
            path.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.TrimEnd(' ', '.') != segment || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                ReservedFileNameRegex().IsMatch(segment)))
            throw new InvalidDataException("The map export contains an unsafe path.");
    }

    private static void ValidateScript(byte[] bytes, uint mapId)
    {
        if (bytes.Length > MaximumScriptBytes) throw new InvalidDataException("The map RMV script is too large.");
        var script = StrictUtf8.GetString(bytes);
        if (script.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("The map RMV script contains invalid text.");
        var generators = GeneratorRegex().Matches(script);
        if (generators.Count != 1 || generators[0].Groups["name"].Value != AnchorPackage.GeneratorName(mapId) ||
            !MapScriptBranding.HasGeneratedMarker(script))
            throw new InvalidDataException("The map RMV script does not identify its exported generator.");
        foreach (Match include in IncludeRegex().Matches(script))
        {
            var relative = include.Groups["path"].Value.Replace('\\', '/');
            ValidateArchivePath(relative);
        }
    }

    private static void ValidatePreview(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes.Length > MaximumPreviewBytes || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != 512 || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != 512)
            throw new InvalidDataException("The map preview must be the exported 512 by 512 PNG.");
        using var memory = new MemoryStream(bytes, writable: false);
        using var image = Image.FromStream(memory, useEmbeddedColorManagement: false, validateImageData: true);
        if (image.Width != 512 || image.Height != 512 || image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid)
            throw new InvalidDataException("The map preview is invalid.");
    }

    private static MapProject ValidatePayload(byte[] bytes, uint mapId)
    {
        if (bytes.Length is < AnchorPackage.HeaderSize or > MaximumPayloadBytes || !bytes.AsSpan(0, 8).SequenceEqual("EDMWA07\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != mapId)
            throw new InvalidDataException("The map terrain payload has an invalid identity or format.");
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(memory);
        memory.Position = 12;
        var width = reader.ReadUInt32(); var height = reader.ReadUInt32();
        var variance = reader.ReadUInt32(); var flags = reader.ReadUInt32(); var expectedChecksum = reader.ReadUInt32();
        if (width is < MapProject.MinGridSize or > MapProject.MaxGridSize || width != height || variance > 50 ||
            (flags & ~0x3Fu) != 0 || (flags & 7u) != 7u)
            throw new InvalidDataException("The map terrain payload header is invalid.");
        var count = checked((int)(width * height));
        var terrain = ReadExactly(reader, count); var roles = ReadExactly(reader, count);
        var elevations = ReadExactly(reader, count); var ramps = ReadExactly(reader, count);
        var cliffs = (flags & AnchorPackage.ForcedCliffMaskFlag) != 0 ? ReadExactly(reader, count) : [];
        var resourceCount = reader.ReadUInt32();
        if (resourceCount > 262144) throw new InvalidDataException("The map contains too many resource placements.");
        var resources = ReadExactly(reader, checked((int)resourceCount * 9));
        var playerCount = reader.ReadUInt32();
        if (playerCount > ManualPlayerStart.MaximumSlot) throw new InvalidDataException("The map contains too many player starts.");
        var starts = ReadExactly(reader, checked((int)playerCount * 10));
        if (memory.Position != memory.Length) throw new InvalidDataException("The map terrain payload has trailing data.");
        uint checksum = 2166136261;
        foreach (var section in new[] { terrain, roles, elevations, ramps, cliffs, resources, starts })
            foreach (var value in section) checksum = unchecked((checksum ^ value) * 16777619);
        if (checksum != expectedChecksum) throw new InvalidDataException("The map terrain payload failed integrity verification.");
        var map = new MapProject
        {
            GridSize = (int)width, Variance = (int)variance, PreserveCoastline = (flags & AnchorPackage.PreserveCoastlineFlag) != 0,
            Cells = terrain.Select(value => (int)value).ToArray(), ComponentRoles = roles, RampMask = ramps,
            CliffMask = cliffs.Length == 0 ? new byte[count] : (byte[])cliffs.Clone(),
            ElevationMask = elevations.Select(value => unchecked((sbyte)value)).ToArray()
        };
        for (var index = 0; index < resources.Length; index += 9)
            map.ManualResources.Add(new() { Kind = (ManualResourceKind)resources[index],
                X = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(index + 1, 4)), Y = BinaryPrimitives.ReadInt32LittleEndian(resources.AsSpan(index + 5, 4)) });
        for (var index = 0; index < starts.Length; index += 10)
        {
            if (starts[index + 1] != 0) throw new InvalidDataException("The map player-start data is invalid.");
            map.ManualPlayerStarts.Add(new() { PlayerSlot = starts[index],
                X = BinaryPrimitives.ReadInt32LittleEndian(starts.AsSpan(index + 2, 4)), Y = BinaryPrimitives.ReadInt32LittleEndian(starts.AsSpan(index + 6, 4)) });
        }
        map.Validate();
        if (!map.CliffMask.AsSpan().SequenceEqual(cliffs.Length == 0 ? new byte[count] : cliffs) ||
            AnchorPackage.MapId(map) != mapId || AnchorPackage.PlacementFlagsFor(map) != flags)
            throw new InvalidDataException("The map terrain contents do not match the exported map identity.");
        return map;
    }

    private void ValidateExisting(string root, string path, byte[] bytes)
    {
        if (path.EndsWith(".dll", StringComparison.Ordinal))
        {
            if (bytes.AsSpan().SequenceEqual(_files[0].Bytes)) return;
            // An earlier editor can have installed an older native generator at the
            // same reserved custom name. Its matching terrain payload proves ownership;
            // accepting a verified current export can then update that custom DLL.
            EmbeddedGenerator.ValidateX86Dll(bytes);
            var payload = _files.Single(file => file.RelativePath.EndsWith(".edmwa", StringComparison.Ordinal));
            var companion = ResolveTarget(root, payload.RelativePath);
            if (!File.Exists(companion) || !ReadBounded(companion, MaximumPayloadBytes).AsSpan().SequenceEqual(payload.Bytes))
                throw new InvalidOperationException("An unidentified generator already occupies this custom map identity. Move it aside before accepting.");
        }
        else if (path.EndsWith(".edmwa", StringComparison.Ordinal))
        {
            ValidatePayload(bytes, MapId);
            if (!bytes.AsSpan().SequenceEqual(_files.Single(file => file.RelativePath == path).Bytes))
                throw new InvalidOperationException("A different terrain payload already uses this map identity. Make a small map edit before exporting again.");
        }
        else
        {
            var script = StrictUtf8.GetString(bytes);
            var generator = GeneratorRegex().Matches(script);
            if (generator.Count != 1 || !AnchorPackage.TryParseGeneratorName(generator[0].Groups["name"].Value, out _) ||
                !MapScriptBranding.HasOwnershipMarker(script))
                throw new InvalidOperationException("The export would replace an original or unidentified RMV script. Rename the shared map before exporting.");
        }
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("The map terrain payload is truncated.");
        return bytes;
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("An existing map file is too large to read safely.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static string ResolveTarget(string root, string relative)
    {
        var boundary = root + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe map installation path.");
        CheckPlainPath(target); return target;
    }
    private static void CheckPlainPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                // Attributes also catch dangling links that File.Exists/Directory.Exists omit.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Map installation cannot use a linked file or directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void CreateDirectories(string path, string root, List<string> created)
    {
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path)!;
        if (!parent.Equals(root, StringComparison.OrdinalIgnoreCase)) CreateDirectories(parent, root, created);
        Directory.CreateDirectory(path); created.Add(path);
    }
    private static void WriteStaged(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }

    [GeneratedRegex("^[ \\t]*UseMapGenerator[ \\t]+\"(?<name>[^\"\\r\\n]+)\"[^\\r\\n]*\\r?$", RegexOptions.Multiline)]
    private static partial Regex GeneratorRegex();
    [GeneratedRegex("^[ \\t]*#include[ \\t]+\"(?<path>[^\"\\r\\n]+)\"", RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedFileNameRegex();
    private sealed record PackageFile(string RelativePath, byte[] Bytes);
    private sealed class PendingWrite(PackageFile file, string target)
    {
        public PackageFile File { get; } = file;
        public string Target { get; } = target;
        public byte[]? Original { get; set; }
        public string? Staged { get; set; }
    }
}

public static class MapLanPackageCodec
{
    public static byte[] Encode(MapLanPackage package) { ArgumentNullException.ThrowIfNull(package); return package.ArchiveBytes(); }
    public static MapLanPackage Decode(byte[] payload) => MapLanPackage.FromArchive(payload);
}
