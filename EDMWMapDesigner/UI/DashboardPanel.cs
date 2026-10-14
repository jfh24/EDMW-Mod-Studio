using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EDMWMapDesigner.UI;

/// <summary>Paints Home's artwork and transparent child windows as one frame.</summary>
internal sealed class DashboardPanel : Panel
{
    private Bitmap? _scaledBackground;

    public DashboardPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Parent buffering alone still exposes the artwork between transparent
            // child paints. Compose only Home; native inputs in the other workspaces
            // must keep their ordinary paint lifecycle.
            parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return parameters;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (BackgroundImage is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }

        if (_scaledBackground is null)
        {
            _scaledBackground = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(_scaledBackground);
            graphics.Clear(BackColor);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(BackgroundImage, ClientRectangle);
        }

        // Transparent descendants request their parent's background too. Reuse
        // these pixels instead of scaling the full-screen image for every request.
        e.Graphics.DrawImageUnscaled(_scaledBackground, Point.Empty);
    }

    protected override void OnBackgroundImageChanged(EventArgs e)
    {
        ClearBackgroundCache();
        base.OnBackgroundImageChanged(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        ClearBackgroundCache();
        base.OnSizeChanged(e);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        ClearBackgroundCache();
        base.OnBackColorChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearBackgroundCache();
        base.Dispose(disposing);
    }

    private void ClearBackgroundCache()
    {
        _scaledBackground?.Dispose();
        _scaledBackground = null;
    }
}
