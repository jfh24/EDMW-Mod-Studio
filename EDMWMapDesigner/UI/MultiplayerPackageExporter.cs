using System.IO.Compression;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

/// <summary>
/// Builds the map archive used by LAN exports and legacy diagnostic installers.
/// Stages the generator, RMV, payload, and preview without changing game files.
/// </summary>
internal static class MultiplayerPackageExporter
{
    /// <summary>
    /// Writes a zip with the layout:
    /// <code>
    /// EDMW_{mapId}.dll                                     (root)
    /// Data\Random Map Scripts\
    ///   {map name}.rmv
    ///   EDMW Anchors\
    ///     {mapId}.edmwa
    /// EDMW Installer\
    ///   map-preview.png
    /// </code>
    /// </summary>
    /// <param name="map">The map whose identity determines the generator/payload file names.</param>
    /// <param name="package">The result of <see cref="GeneratedMapPackage.Write"/> for this map.</param>
    /// <param name="zipPath">Destination .zip path.</param>
    /// <returns>The resolved path of the written zip.</returns>
    internal static string Export(
        MapProject map,
        MapPackageResult package,
        string zipPath,
        bool hideMapPreview = false)
        => ExportCore(map, package, zipPath, includeDiagnosticMarker: false,
            redactMapPreview: hideMapPreview);

    internal static string ExportDiagnosticProbe(
        MapProject map, MapPackageResult package, string zipPath)
        => ExportCore(map, package, zipPath, includeDiagnosticMarker: true,
            redactMapPreview: false);

    private static string ExportCore(
        MapProject map, MapPackageResult package, string zipPath,
        bool includeDiagnosticMarker, bool redactMapPreview)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        map.Validate();

        if (!File.Exists(package.ScriptPath))
            throw new FileNotFoundException("The generated RMV script was not found.", package.ScriptPath);
        if (!File.Exists(package.PayloadPath))
            throw new FileNotFoundException("The generated terrain payload was not found.", package.PayloadPath);

        var mapId = AnchorPackage.MapId(map);
        var generatorBytes = EmbeddedGenerator.Read();
        EmbeddedGenerator.ValidateX86Dll(generatorBytes);
        var generatorEntryName = AnchorPackage.GeneratorFileNameFor(mapId);

        var scriptEntryName = Path.GetFileName(package.ScriptPath);
        var payloadEntryName = Path.GetFileName(package.PayloadPath);
        if (payloadEntryName != AnchorPackage.FileName(map))
            throw new InvalidOperationException(
                "The terrain payload file name does not match this map's identity. " +
                "Re-export the map before building the multiplayer package.");

        var resolvedZipPath = Path.GetFullPath(zipPath);
        var parentFolder = Path.GetDirectoryName(resolvedZipPath);
        if (!string.IsNullOrEmpty(parentFolder)) Directory.CreateDirectory(parentFolder);

        var staged = resolvedZipPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, generatorEntryName, generatorBytes);
                if (includeDiagnosticMarker)
                    WriteEntry(archive, Path.ChangeExtension(generatorEntryName, ".trace"), []);
                WriteEntry(archive, $"Data/Random Map Scripts/{scriptEntryName}",
                    File.ReadAllBytes(package.ScriptPath));
                WriteEntry(archive, $"Data/Random Map Scripts/{AnchorPackage.DataFolderName}/{payloadEntryName}",
                    File.ReadAllBytes(package.PayloadPath));
                WriteEntry(archive, "EDMW Installer/map-preview.png",
                    redactMapPreview ? RenderRedactedPreview() : RenderPreview(map));
                stream.Flush(true);
            }
            File.Move(staged, resolvedZipPath, true);
            return resolvedZipPath;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    internal static byte[] RenderPreview(MapProject map)
    {
        using var source = new Bitmap(map.GridSize, map.GridSize, PixelFormat.Format32bppArgb);
        var pixels = map.Cells.Select(cell => ((TerrainKind)cell).Color().ToArgb()).ToArray();
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < source.Height; y++)
                Marshal.Copy(pixels, y * source.Width, data.Scan0 + y * data.Stride, source.Width);
        }
        finally { source.UnlockBits(data); }

        using var preview = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, 512, 512));
        }
        using var output = new MemoryStream();
        preview.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    internal static byte[] RenderRedactedPreview()
    {
        using var preview = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.Black);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.TranslateTransform(preview.Width / 2f, preview.Height / 2f);
            graphics.RotateTransform(-45f);
            using var font = new Font("Segoe UI", 76f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var shadow = new SolidBrush(Color.FromArgb(180, 70, 0, 0));
            using var red = new SolidBrush(Color.FromArgb(235, 34, 34));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            var bounds = new RectangleF(-350f, -70f, 700f, 140f);
            graphics.DrawString("Redacted", font, shadow,
                new RectangleF(bounds.X + 4f, bounds.Y + 4f, bounds.Width, bounds.Height), format);
            graphics.DrawString("Redacted", font, red, bounds, format);
        }
        using var output = new MemoryStream();
        preview.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(bytes, 0, bytes.Length);
    }
}
