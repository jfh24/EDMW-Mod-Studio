using System.Drawing.Drawing2D;

namespace EDMWMapDesigner.UI;

/// <summary>Retains numeric editing while painting the spinner in the editor's palette.</summary>
internal sealed class MedievalNumericUpDown : NumericUpDown
{
    private readonly Control _nativeButtons;
    private readonly SpinnerArrows _arrows;

    public MedievalNumericUpDown()
    {
        BackColor = MedievalTheme.Input;
        ForeColor = MedievalTheme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        _nativeButtons = Controls.Cast<Control>().Single(control => control.GetType().Name == "UpDownButtons");
        _arrows = new SpinnerArrows(this) { TabStop = false, AccessibleName = "Change numeric value" };
        Controls.Add(_arrows);
        PositionArrows();
    }

    internal Control ArrowsForVerification => _arrows;
    internal void StepForVerification(bool up)
    {
        if (up) UpButton();
        else DownButton();
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        // Base construction can lay out its children before our overlay exists.
        if (_arrows is not null) PositionArrows();
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        if (_arrows is not null) PositionArrows();
    }

    private void PositionArrows()
    {
        _nativeButtons.Visible = false;
        _arrows.Bounds = _nativeButtons.Bounds;
        _arrows.BringToFront();
    }

    private sealed class SpinnerArrows : Control
    {
        private readonly MedievalNumericUpDown _owner;
        private readonly System.Windows.Forms.Timer _repeat = new();
        private int _pressed;
        private int _hover;

        public SpinnerArrows(MedievalNumericUpDown owner)
        {
            _owner = owner;
            BackColor = MedievalTheme.SurfaceRaised;
            ForeColor = MedievalTheme.GoldBright;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _repeat.Tick += (_, _) =>
            {
                if (_pressed == 0 || !_owner.Enabled || _owner.IsDisposed) { StopRepeat(); return; }
                Step();
                _repeat.Interval = 70;
            };
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button != MouseButtons.Left || !_owner.Enabled) return;
            _owner.Focus();
            _pressed = args.Y < Height / 2 ? 1 : -1;
            Capture = true;
            Step();
            _repeat.Interval = 400;
            _repeat.Start();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            var hover = ClientRectangle.Contains(args.Location) ? args.Y < Height / 2 ? 1 : -1 : 0;
            if (_hover == hover) return;
            _hover = hover;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            _hover = 0;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button != MouseButtons.Left) return;
            StopRepeat();
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            base.OnMouseCaptureChanged(args);
            if (!Capture) StopRepeat();
        }

        protected override void OnEnabledChanged(EventArgs args)
        {
            base.OnEnabledChanged(args);
            if (!Enabled) StopRepeat();
            Invalidate();
        }

        private void Step()
        {
            if (_pressed > 0) _owner.UpButton();
            else _owner.DownButton();
        }

        private void StopRepeat()
        {
            _repeat.Stop();
            _pressed = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var middle = Height / 2;
            PaintHalf(args.Graphics, new Rectangle(0, 0, Width, middle), 1);
            PaintHalf(args.Graphics, new Rectangle(0, middle, Width, Height - middle), -1);
            using var border = new Pen(MedievalTheme.BorderStrong);
            args.Graphics.DrawLine(border, 0, 0, 0, Height);
            args.Graphics.DrawLine(border, 0, middle, Width, middle);
        }

        private void PaintHalf(Graphics graphics, Rectangle bounds, int direction)
        {
            var active = _pressed == direction || _hover == direction;
            using var background = new SolidBrush(active ? MedievalTheme.Border : MedievalTheme.SurfaceRaised);
            graphics.FillRectangle(background, bounds);
            var x = bounds.Left + bounds.Width / 2f;
            var y = bounds.Top + bounds.Height / 2f;
            var radius = Math.Max(2f, Math.Min(bounds.Width / 4f, bounds.Height / 3f));
            PointF[] triangle = direction > 0
                ? [new(x - radius, y + radius / 2), new(x + radius, y + radius / 2), new(x, y - radius / 2)]
                : [new(x - radius, y - radius / 2), new(x + radius, y - radius / 2), new(x, y + radius / 2)];
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var arrow = new SolidBrush(Enabled ? MedievalTheme.GoldBright : MedievalTheme.TextSecondary);
            graphics.FillPolygon(arrow, triangle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }
}
