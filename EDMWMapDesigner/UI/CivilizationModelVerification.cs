using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Read-only format, rasterizer and offscreen control checks. No screenshots are produced.</summary>
internal static class CivilizationModelVerification
{
    public static IReadOnlyList<string> Run(string gamePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var databasePaths = new[] { "dbobjects.dat", "dbgraphics.dat" }
            .Select(name => Path.Combine(gamePath, "Data", "db", name)).ToArray();
        var before = databasePaths.Select(Hash).ToArray();
        var assets = new CivilizationGameAssets(gamePath);
        var catalog = CivilizationGameCatalog.Load(gamePath);
        var gameData = new CivilizationGameData(gamePath, catalog);
        var civilizations = CivilizationGameCatalog.ImportCivilizations(gamePath);
        var graphics = new HashSet<int> { 919, 916, 1094, 896 };
        foreach (var civilization in civilizations)
        foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
        {
            var graphic = gameData.GetRoster(civilization, category)
                .Select(entity => catalog.Items.First(item => item.SourceId == entity.SourceObjectId).GraphicsId)
                .FirstOrDefault(id => id.HasValue && assets.FindModelName(id.Value) is not null);
            Require(graphic.HasValue, $"{civilization.Name}/{category} has no model fixture.");
            graphics.Add(graphic!.Value);
        }
        foreach (var item in catalog.Items.Where(item => item.Name.Contains("King Tiger", StringComparison.OrdinalIgnoreCase)))
            if (item.GraphicsId is int id) graphics.Add(id);
        var reports = new List<object>();
        var animated = 0;
        var changedPoses = 0;
        var watch = Stopwatch.StartNew();
        foreach (var graphic in graphics.Order())
        {
            var modelName = assets.FindModelName(graphic) ?? throw new InvalidDataException($"Graphics {graphic} has no model.");
            var mesh = CivilizationModelReader.Read(assets.ReadModel(modelName), modelName);
            var appearance = assets.ReadModelAppearance(mesh, graphic);
            Require(mesh.Materials.Count > 0 && appearance.Textures.Count > 0, $"{modelName} has no game materials/textures.");
            using var viewport = new CivilizationModelViewport();
            viewport.SetModel(mesh, appearance);
            var frame = viewport.RenderFrame(288, 224);
            Require(frame.Pixels.Length == 288 * 224 && frame.Statistics.TexturedTriangles > 0 &&
                frame.Statistics.ShadedPixels > 0, $"{modelName} did not rasterize textured surfaces.");
            foreach (var clip in appearance.Animations)
            {
                var clipMesh = clip.ModelName is null ? mesh : CivilizationModelReader.Read(assets.ReadModel(clip.ModelName), clip.ModelName);
                Require(clip.StartFrame >= 0 && clip.EndFrame >= clip.StartFrame && clip.EndFrame < clipMesh.FrameCount &&
                    clip.FramesPerSecond > 0 && double.IsFinite(clip.FramesPerSecond), $"{modelName}/{clip.Name} has an invalid animation range.");
                var first = clipMesh.GetPose(clip.StartFrame);
                foreach (var position in new[] { clip.StartFrame + (clip.EndFrame - clip.StartFrame) * .5f, (float)clip.EndFrame })
                {
                    var pose = clipMesh.GetPose(position);
                    Require(pose.Vertices.Count == clipMesh.Vertices.Count && pose.Normals.Count == clipMesh.Vertices.Count &&
                        pose.Vertices.All(Finite) && pose.Normals.All(Finite), $"{modelName}/{clip.Name} has an invalid animated pose.");
                    if (!pose.Vertices.SequenceEqual(first.Vertices)) changedPoses++;
                }
            }
            if (appearance.Animations.Count > 0) animated++;
            reports.Add(new { Graphic = graphic, Model = modelName, mesh.FrameCount, mesh.PartCount,
                Materials = mesh.Materials.Count, Textures = appearance.Textures.Count,
                Animations = appearance.Animations.Select(clip => new { clip.Name, clip.StartFrame,
                    clip.EndFrame, clip.FramesPerSecond, clip.ModelName, Texture = clip.Texture?.Name }), Render = frame.Statistics });
        }
        Require(animated > 0 && changedPoses > 0, "Stock animation sampling did not change any model pose.");
        VerifyPlaybackAndControls(assets);
        var rasterizerChecks = CivilizationModelViewport.VerifyRasterizer()
            .Concat(CivilizationTextureVerification.Verify()).ToArray();
        Require(databasePaths.Select(Hash).SequenceEqual(before), "Model checks changed live game databases.");
        File.WriteAllText(Path.Combine(outputDirectory, "model-checks.json"), JsonSerializer.Serialize(new
        {
            Models = reports, AnimatedModels = animated, ChangedPoseSamples = changedPoses,
            ElapsedSeconds = watch.Elapsed.TotalSeconds, RasterizerChecks = rasterizerChecks,
            VisualInspection = "Handled by the user; no screenshots produced."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return [$"{graphics.Count} game models across all nine civilizations with decoded textures",
            $"{animated} animated models and {changedPoses} changing pose samples",
            "checkbox loops, sequence wrap, animation skin/mesh switches, native color selection, pause, hidden/disposed timer and compact control bounds",
            .. rasterizerChecks, "live game databases unchanged"];
    }

    private static void VerifyPlaybackAndControls(CivilizationGameAssets assets)
    {
        var name = assets.FindModelName(919) ?? throw new InvalidDataException("The citizen fixture is unavailable.");
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ClientSize = new Size(330, 440) };
        using var panel = new CivilizationModelPreviewPanel(assets, name, null, _ => { }, () => { }, _ => { }, 919)
            { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();
        var checks = panel.AnimationChecksForVerification;
        Require(checks.Count > 1 && !panel.AnimationTimerRunningForVerification, "Animation checkboxes were missing or autoplayed unchecked clips.");
        checks[0].Checked = true;
        Require(panel.AnimationTimerRunningForVerification, "Checking a clip did not start looping.");
        checks[1].Checked = true;
        var clips = checks.Take(2).Select(check => (ModelAnimationClip)check.Tag!).ToArray();
        var duration = clips.Sum(clip => (clip.EndFrame - (double)clip.StartFrame + 1) / clip.FramesPerSecond);
        foreach (var seconds in new[] { 0, .1, duration - .01, duration, duration * 3 + .1 })
        {
            panel.AdvanceAnimationForVerification(seconds);
            var expected = CivilizationModelPreviewPanel.SampleAnimation(clips, seconds);
            Require(Math.Abs(panel.ViewportForVerification.FramePosition - expected) < .001f,
                "The animation checkbox playlist sampled the wrong frame.");
            Require(clips.Any(clip => expected >= clip.StartFrame && expected <= clip.EndFrame),
                "A loop crossed into an unchecked clip.");
        }
        Require(CivilizationModelPreviewPanel.SampleAnimation(clips, duration) == clips[0].StartFrame,
            "The checked animation sequence did not wrap.");
        form.Hide();
        Require(!panel.AnimationTimerRunningForVerification, "Hidden model preview kept its animation timer running.");
        form.Show();
        Require(panel.AnimationTimerRunningForVerification, "Showing the model preview did not resume selected clips.");
        foreach (var check in checks) check.Checked = false;
        Require(!panel.AnimationTimerRunningForVerification, "Unchecking all clips did not pause playback.");
        foreach (var size in new[] { new Size(300, 365), new Size(330, 440), new Size(600, 365) })
        {
            form.ClientSize = size;
            form.PerformLayout(); panel.PerformLayout(); Application.DoEvents();
            Require(panel.ViewportForVerification.Width >= 200 && panel.ViewportForVerification.Height >= 140,
                "The model viewport lost its usable area.");
            foreach (var button in Descendants(panel).OfType<Button>().Where(button => button.Visible))
                Require(button.Width > 0 && button.Height >= 20 && panel.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)).Bottom <= panel.Height,
                    "A model action button was clipped or collapsed.");
        }
        var original = panel.ViewportForVerification.RenderFrame(128, 128);
        var skin = new ModelAnimationClip("Skin fixture", 0, 0, 1)
            { Texture = new ModelTexture("Skin fixture", 1, 1, [unchecked((int)0xff00ff00)]) };
        var alternateName = assets.FindModelName(1094) ?? throw new InvalidDataException("The alternate model fixture is missing.");
        var alternate = new ModelAnimationClip("Mesh fixture", 0, 0, 1) { ModelName = alternateName };
        panel.LoadAnimationsForVerification([skin, alternate]);
        panel.AnimationChecksForVerification[0].Checked = true;
        var changedSkin = panel.ViewportForVerification.RenderFrame(128, 128);
        Require(!original.Pixels.SequenceEqual(changedSkin.Pixels) && panel.ViewportForVerification.Mesh?.Name == name,
            "An animation skin override did not change surfaces on the current mesh.");
        panel.AnimationChecksForVerification[1].Checked = true;
        panel.AdvanceAnimationForVerification(1.25);
        Require(panel.ViewportForVerification.Mesh?.Name == alternateName,
            "A checked animation did not select its alternate mesh.");
        panel.AdvanceAnimationForVerification(2.25);
        Require(panel.ViewportForVerification.Mesh?.Name == name,
            "The animation playlist did not restore its primary mesh after a variant.");
        var colors = panel.ViewportForVerification.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(item => item.Text == "Player color");
        Require(colors is not null && colors.DropDownItems.Count == assets.PlayerColors.Count && colors.DropDownItems.Count >= 8,
            "The actual game player-color choices were missing.");
        ((ToolStripMenuItem)colors!.DropDownItems[1]).PerformClick();
        Require(panel.ViewportForVerification.PlayerColorArgb == assets.PlayerColors[1].Argb,
            "Choosing a player color did not use its native palette value.");
        panel.Dispose();
        Require(!panel.AnimationTimerRunningForVerification, "Disposed preview kept its animation timer running.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
