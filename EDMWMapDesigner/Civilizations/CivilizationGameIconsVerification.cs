using System.Drawing;
using System.Drawing.Imaging;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationGameIconsVerification
{
    public static void Run(string gamePath, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        using var icons = new CivilizationGameIcons(gamePath);
        var samples = new[]
        {
            (Label: "Ranger", Image: icons.ForObject(749), Source: icons.ObjectReference(749), Expected: "USarmyranger"),
            (Label: "Sherman", Image: icons.ForObject(777), Source: icons.ObjectReference(777), Expected: "USsherman"),
            (Label: "Barracks", Image: icons.ForObject(959), Source: icons.ObjectReference(959), Expected: "barracks"),
            (Label: "Stone gathering", Image: icons.ForTechnology(9404), Source: icons.TechnologyReference(9404), Expected: "PneumaticDrill"),
            (Label: "Stop command", Image: icons.ForTechnology(1700), Source: icons.TechnologyReference(1700), Expected: "act_xstop"),
            (Label: "Middle Ages", Image: icons.ForAge(5), Source: icons.TechnologyReference(5), Expected: "epoch_06"),
            (Label: "Gunpowder Age", Image: icons.ForAge(6), Source: icons.TechnologyReference(6), Expected: "epoch_07"),
            (Label: "WWII", Image: icons.ForAge(9), Source: icons.TechnologyReference(9), Expected: "epoch_11")
        };
        foreach (var sample in samples)
        {
            Require(sample.Source?.TexturePath.Contains(sample.Expected, StringComparison.OrdinalIgnoreCase) == true,
                sample.Label + " source-table identity");
            Require(sample.Image is Bitmap bitmap && VisiblePixels(bitmap) > 128, sample.Label + " decoded pixels");
        }
        Require(!ReferenceEquals(samples[0].Image, samples[1].Image), "different units use different source artwork");
        Require(ReferenceEquals(icons.ForObject(749), samples[0].Image), "cached borrowed image");
        Require(icons.ForObject(-1) is null && icons.ForTechnology(int.MaxValue) is null && icons.ForButton(-1) is null,
            "invalid references fall back without selecting unrelated artwork");
        var edited = new CivilizationEntityDraft { SourceObjectId = 749, Category = CivilizationCategory.Units };
        edited.Properties["unit.icon"] = "1386";
        Require(ReferenceEquals(icons.ForEntity(edited), samples[1].Image), "authored icon override preview");

        using var sheet = new Bitmap(samples.Length * 120, 160);
        using (var graphics = Graphics.FromImage(sheet))
        using (var font = new Font("Segoe UI", 10))
        using (var brush = new SolidBrush(Color.White))
        {
            graphics.Clear(Color.FromArgb(24, 26, 30));
            for (var index = 0; index < samples.Length; index++)
            {
                graphics.DrawImage(samples[index].Image!, new Rectangle(index * 120 + 28, 18, 64, 64));
                graphics.DrawString(samples[index].Label, font, brush, new RectangleF(index * 120 + 5, 96, 112, 55));
            }
        }
        sheet.Save(Path.Combine(outputPath, "game-icon-samples.png"), ImageFormat.Png);
        File.WriteAllLines(Path.Combine(outputPath, "game-icons-result.txt"),
            new[] { "PASS: real table joins, SSA/loose texture decoding, nonempty pixels, caching, overrides and missing references." }
                .Concat(samples.Select(sample => $"{sample.Label}: button {sample.Source!.ButtonId}, {sample.Source.TexturePath}")));
        Console.WriteLine("GAME ICONS PASS: source mappings, decoded pixels, cache, overrides and missing references.");
    }

    private static int VisiblePixels(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A > 16) count++;
        return count;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidDataException("Game icon verification failed: " + check);
    }
}
