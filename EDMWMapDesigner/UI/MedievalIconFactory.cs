using System.Drawing.Drawing2D;
using System.Reflection;

namespace EDMWMapDesigner.UI;

internal enum MedievalIcon
{
    NewMap,
    Open,
    Save,
    Undo,
    Redo,
    Export,
    Install,
    Generator,
    Civilizations,
    Bundle
}

internal static class MedievalIconFactory
{
    private static readonly Lazy<Image> CivilizationArtwork = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "EDMWMapDesigner.Assets.dashboard-civilizations-icon.png")
            ?? throw new InvalidOperationException("Civilization editor artwork is missing.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    });

    public static Image Create(MedievalIcon icon, int size = 18)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var pen = new Pen(MedievalTheme.GoldBright, Math.Max(1.4f, size / 12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(Color.FromArgb(105, MedievalTheme.Gold));

        switch (icon)
        {
            case MedievalIcon.NewMap:
                DrawScroll(graphics, pen, fill, size);
                graphics.DrawLine(pen, size * 0.68f, size * 0.48f, size * 0.68f, size * 0.82f);
                graphics.DrawLine(pen, size * 0.51f, size * 0.65f, size * 0.85f, size * 0.65f);
                break;
            case MedievalIcon.Open:
                PointF[] folder =
                [
                    new(size * 0.08f, size * 0.31f), new(size * 0.39f, size * 0.31f),
                    new(size * 0.48f, size * 0.43f), new(size * 0.91f, size * 0.43f),
                    new(size * 0.82f, size * 0.83f), new(size * 0.13f, size * 0.83f)
                ];
                graphics.FillPolygon(fill, folder);
                graphics.DrawPolygon(pen, folder);
                break;
            case MedievalIcon.Save:
                graphics.FillRectangle(fill, size * 0.17f, size * 0.12f, size * 0.67f, size * 0.76f);
                graphics.DrawRectangle(pen, size * 0.17f, size * 0.12f, size * 0.67f, size * 0.76f);
                graphics.DrawRectangle(pen, size * 0.31f, size * 0.16f, size * 0.37f, size * 0.24f);
                graphics.DrawRectangle(pen, size * 0.32f, size * 0.58f, size * 0.36f, size * 0.29f);
                break;
            case MedievalIcon.Undo:
                DrawArrow(graphics, pen, size, false);
                break;
            case MedievalIcon.Redo:
                DrawArrow(graphics, pen, size, true);
                break;
            case MedievalIcon.Export:
                DrawScroll(graphics, pen, fill, size);
                graphics.DrawLine(pen, size * 0.45f, size * 0.61f, size * 0.88f, size * 0.61f);
                graphics.DrawLine(pen, size * 0.7f, size * 0.43f, size * 0.88f, size * 0.61f);
                graphics.DrawLine(pen, size * 0.7f, size * 0.79f, size * 0.88f, size * 0.61f);
                break;
            case MedievalIcon.Install:
                DrawAnchor(graphics, pen, size);
                break;
            case MedievalIcon.Generator:
                DrawShield(graphics, pen, fill, size);
                break;
            case MedievalIcon.Civilizations:
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(CivilizationArtwork.Value, new Rectangle(0, 0, size, size));
                break;
            case MedievalIcon.Bundle:
                DrawBundle(graphics, pen, fill, size);
                break;
        }

        return bitmap;
    }

    private static void DrawScroll(Graphics graphics, Pen pen, Brush fill, int size)
    {
        var body = new RectangleF(size * 0.2f, size * 0.16f, size * 0.52f, size * 0.7f);
        graphics.FillRectangle(fill, body);
        graphics.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
        graphics.DrawArc(pen, size * 0.1f, size * 0.1f, size * 0.24f, size * 0.19f, 70, 230);
        graphics.DrawArc(pen, size * 0.59f, size * 0.75f, size * 0.24f, size * 0.19f, 250, 230);
    }

    private static void DrawArrow(Graphics graphics, Pen pen, int size, bool right)
    {
        var rectangle = new RectangleF(size * 0.22f, size * 0.24f, size * 0.56f, size * 0.5f);
        graphics.DrawArc(pen, rectangle, right ? 210 : -30, 230);
        if (right)
        {
            graphics.DrawLine(pen, size * 0.72f, size * 0.3f, size * 0.91f, size * 0.32f);
            graphics.DrawLine(pen, size * 0.72f, size * 0.3f, size * 0.78f, size * 0.49f);
        }
        else
        {
            graphics.DrawLine(pen, size * 0.28f, size * 0.3f, size * 0.09f, size * 0.32f);
            graphics.DrawLine(pen, size * 0.28f, size * 0.3f, size * 0.22f, size * 0.49f);
        }
    }

    private static void DrawAnchor(Graphics graphics, Pen pen, int size)
    {
        graphics.DrawEllipse(pen, size * 0.39f, size * 0.08f, size * 0.22f, size * 0.22f);
        graphics.DrawLine(pen, size * 0.5f, size * 0.3f, size * 0.5f, size * 0.82f);
        graphics.DrawArc(pen, size * 0.15f, size * 0.52f, size * 0.7f, size * 0.38f, 4, 172);
        graphics.DrawLine(pen, size * 0.15f, size * 0.7f, size * 0.15f, size * 0.51f);
        graphics.DrawLine(pen, size * 0.85f, size * 0.7f, size * 0.85f, size * 0.51f);
    }

    private static void DrawBundle(Graphics graphics, Pen pen, Brush fill, int size)
    {
        var body = new RectangleF(size * 0.16f, size * 0.32f, size * 0.68f, size * 0.56f);
        graphics.FillRectangle(fill, body);
        graphics.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
        graphics.DrawLine(pen, size * 0.16f, size * 0.32f, size * 0.5f, size * 0.14f);
        graphics.DrawLine(pen, size * 0.84f, size * 0.32f, size * 0.5f, size * 0.14f);
        graphics.DrawLine(pen, size * 0.5f, size * 0.14f, size * 0.5f, size * 0.32f);
        graphics.DrawLine(pen, size * 0.32f, size * 0.5f, size * 0.68f, size * 0.5f);
    }

    private static void DrawShield(Graphics graphics, Pen pen, Brush fill, int size)
    {
        PointF[] shield =
        [
            new(size * 0.18f, size * 0.2f), new(size * 0.5f, size * 0.08f),
            new(size * 0.82f, size * 0.2f), new(size * 0.76f, size * 0.67f),
            new(size * 0.5f, size * 0.9f), new(size * 0.24f, size * 0.67f)
        ];
        graphics.FillPolygon(fill, shield);
        graphics.DrawPolygon(pen, shield);
        graphics.DrawLine(pen, size * 0.5f, size * 0.16f, size * 0.5f, size * 0.78f);
        graphics.DrawLine(pen, size * 0.27f, size * 0.45f, size * 0.73f, size * 0.45f);
    }

}
