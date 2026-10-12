using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.UI;

public enum BrushShape
{
    Round,
    Square,
    Diamond,
    Horizontal,
    Vertical,
    ForwardDiagonal,
    BackwardDiagonal,
    Plus,
    X,
    Ring,
    Crescent,
    ReverseCrescent,
    RisingSwoop,
    FallingSwoop,
    SCurve,
    ReverseSCurve,
    TaperedBlade,
    ReverseTaperedBlade,
    HorizontalWave,
    VerticalWave
}

internal static class BrushShapeGeometry
{
    public static bool Contains(BrushShape shape, int dx, int dy, int radius)
        => Contains(shape, (double)dx, dy, radius);

    public static bool ContainsRotated(
        BrushShape shape,
        int dx,
        int dy,
        int radius,
        int rotationDegrees)
        => ContainsRotated(shape, dx, dy, radius, rotationDegrees, 100, 100);

    public static bool ContainsRotated(
        BrushShape shape,
        int dx,
        int dy,
        int radius,
        int rotationDegrees,
        int lengthPercent,
        int widthPercent,
        UsStateBrush? stateBrush = null)
    {
        // A one-cell brush also serves as the cursor for point-placement tools.
        if (stateBrush is not null && radius <= 0)
            return radius == 0 && dx == 0 && dy == 0;
        var (localX, localY) = ToLocalCoordinates(dx, dy, rotationDegrees, lengthPercent, widthPercent);
        return stateBrush is null
            ? Contains(shape, localX, localY, radius)
            : stateBrush.Contains(localX / radius, localY / radius);
    }

    internal static (double X, double Y) ToLocalCoordinates(
        int dx, int dy, int rotationDegrees, int lengthPercent, int widthPercent)
    {
        var normalizedDegrees = ((rotationDegrees % 360) + 360) % 360;
        var radians = normalizedDegrees * Math.PI / 180.0;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        if (Math.Abs(cosine) < 0.000000000001) cosine = 0;
        if (Math.Abs(sine) < 0.000000000001) sine = 0;
        var lengthScale = Math.Max(0.01, lengthPercent / 100.0);
        var widthScale = Math.Max(0.01, widthPercent / 100.0);
        var localX = (dx * cosine + dy * sine) / lengthScale;
        var localY = (-dx * sine + dy * cosine) / widthScale;
        return (localX, localY);
    }

    public static int FootprintExtent(int radius, int rotationDegrees)
        => FootprintExtent(radius, rotationDegrees, 100, 100);

    public static int FootprintExtent(
        int radius,
        int rotationDegrees,
        int lengthPercent,
        int widthPercent)
    {
        if (radius <= 0) return 0;
        var radians = rotationDegrees * Math.PI / 180.0;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var lengthRadius = (radius + 0.5) * Math.Max(0.01, lengthPercent / 100.0);
        var widthRadius = (radius + 0.5) * Math.Max(0.01, widthPercent / 100.0);
        var horizontalExtent = lengthRadius * cosine + widthRadius * sine;
        var verticalExtent = lengthRadius * sine + widthRadius * cosine;
        return (int)Math.Ceiling(
            Math.Max(horizontalExtent, verticalExtent) - 0.5 - 0.000000001);
    }

    private static bool Contains(BrushShape shape, double dx, double dy, int radius)
    {
        if (radius < 0 || Math.Abs(dx) > radius + 0.5 || Math.Abs(dy) > radius + 0.5)
            return false;
        var thickness = Math.Max(0, radius / 4) + 0.5;
        var narrowThickness = Math.Max(0, radius / 5) + 0.5;
        var distanceSquared = dx * dx + dy * dy;
        var innerRadius = Math.Max(0, radius - Math.Max(1, radius / 4));
        var insideRing = distanceSquared <= radius * radius + 0.5 &&
                         distanceSquared >= innerRadius * innerRadius;
        var waveAmplitude = radius * 0.35;
        var waveThickness = Math.Max(0.6, radius / 4.0);
        var horizontalWaveY = radius == 0
            ? 0
            : Math.Sin(dx * Math.PI / Math.Max(1, radius)) * waveAmplitude;
        var verticalWaveX = radius == 0
            ? 0
            : Math.Sin(dy * Math.PI / Math.Max(1, radius)) * waveAmplitude;
        return shape switch
        {
            BrushShape.Square => true,
            BrushShape.Diamond => Math.Abs(dx) + Math.Abs(dy) <= radius,
            BrushShape.Horizontal => Math.Abs(dy) <= thickness,
            BrushShape.Vertical => Math.Abs(dx) <= thickness,
            BrushShape.ForwardDiagonal => Math.Abs(dx + dy) <= thickness,
            BrushShape.BackwardDiagonal => Math.Abs(dx - dy) <= thickness,
            BrushShape.Plus => Math.Abs(dx) <= narrowThickness || Math.Abs(dy) <= narrowThickness,
            BrushShape.X => Math.Abs(dx - dy) <= narrowThickness || Math.Abs(dx + dy) <= narrowThickness,
            BrushShape.Ring => insideRing,
            BrushShape.Crescent => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.ReverseCrescent => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.RisingSwoop => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.FallingSwoop => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.SCurve => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.ReverseSCurve => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.TaperedBlade => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.ReverseTaperedBlade => InsideSweptShape(shape, dx, dy, radius),
            BrushShape.HorizontalWave => Math.Abs(dy - horizontalWaveY) <= waveThickness,
            BrushShape.VerticalWave => Math.Abs(dx - verticalWaveX) <= waveThickness,
            _ => distanceSquared <= radius * radius + 0.5
        };
    }

    private static bool InsideSweptShape(
        BrushShape shape, double dx, double dy, int radius)
    {
        if (radius == 0) return dx == 0 && dy == 0;

        var u = dx / radius;
        var v = dy / radius;
        var cellAllowance = 0.55 / radius;
        if (Math.Abs(u) > 1 + cellAllowance) return false;

        u = Math.Clamp(u, -1, 1);
        var t = (u + 1) / 2;
        var taperedEnds = Math.Max(0, 1 - u * u);
        double center;
        double halfWidth;
        switch (shape)
        {
            case BrushShape.Crescent:
            case BrushShape.ReverseCrescent:
                var crescentDirection = shape == BrushShape.Crescent ? -1 : 1;
                center = crescentDirection * 0.40 * taperedEnds;
                halfWidth = 0.23 * Math.Pow(taperedEnds, 0.82);
                break;
            case BrushShape.RisingSwoop:
            case BrushShape.FallingSwoop:
                var swoopDirection = shape == BrushShape.RisingSwoop ? -1 : 1;
                center = swoopDirection *
                    (0.48 * Math.Sin(Math.PI * u / 2) -
                     0.12 * Math.Sin(Math.PI * u));
                halfWidth = 0.28 * Math.Pow(Math.Max(0, 1 - t), 0.72);
                break;
            case BrushShape.SCurve:
            case BrushShape.ReverseSCurve:
                var sDirection = shape == BrushShape.SCurve ? 1 : -1;
                center = sDirection * 0.40 * Math.Sin(Math.PI * u);
                halfWidth = 0.17 * Math.Pow(taperedEnds, 0.72);
                break;
            default:
                var bladeDirection = shape == BrushShape.TaperedBlade ? 1 : -1;
                center = bladeDirection *
                    (0.22 * Math.Sin(Math.PI * u) - 0.20 * taperedEnds);
                halfWidth = 0.30 * taperedEnds * (0.72 + 0.28 * t);
                break;
        }

        return Math.Abs(v - center) <= halfWidth + cellAllowance;
    }
}

public sealed class MapCanvas : Control
{
    private const float MinimumZoom = 1f;
    private const float MaximumZoom = 8f;
    private const float ZoomStep = 1.25f;
    private MapProject _project = MapProject.CreateDefault();
    private bool _painting;
    private Point _hoverCell = new(-1, -1);
    private Point _lastPaintCell = new(-1, -1);
    private Bitmap? _mapBitmap;
    private bool _bitmapDirty = true;
    private bool _elevationMaskMode;
    private bool _rampMaskMode;
    private bool _manualResourceMode;
    private bool _manualPlayerStartMode;
    private readonly List<int> _manualPlayerPlacementHistory = [];
    private readonly Dictionary<Guid, CollaboratorCursorUpdate> _remoteCursors = [];
    private float _zoom = MinimumZoom;
    private PointF _panOffset;
    private bool _panning;
    private Point _panStart;
    private PointF _panOrigin;
    private Cursor? _cursorBeforePan;
    private (UsStateBrush State, int Radius, int Rotation, int Length, int Width, bool Water)? _stateFootprintKey;
    private bool _stateFootprintHasCells;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TerrainKind SelectedTerrain { get; set; } = TerrainKind.Grassland;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushSize { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BrushShape BrushShape { get; set; } = BrushShape.Round;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal UsStateBrush? StateBrush { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StateMountainsEnabled { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StateWaterEnabled { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StateForestsEnabled { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StateFeaturesAllowed { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushRotationDegrees { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushLengthPercent { get; set; } = 100;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushWidthPercent { get; set; } = 100;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MirrorHorizontal { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MirrorVertical { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowGrid { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SemanticAnchorMode { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ManualResourceMode
    {
        get => _manualResourceMode;
        set { if (_manualResourceMode == value) return; _manualResourceMode = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ManualResourceKind SelectedManualResource { get; set; } = ManualResourceKind.Gold;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ManualPlayerStartMode
    {
        get => _manualPlayerStartMode;
        set { if (_manualPlayerStartMode == value) return; _manualPlayerStartMode = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ElevationMaskMode
    {
        get => _elevationMaskMode;
        set { if (_elevationMaskMode == value) return; _elevationMaskMode = value; _bitmapDirty = true; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public sbyte SelectedElevation { get; set; } = 25;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PaintCliffs { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RampMaskMode
    {
        get => _rampMaskMode;
        set { if (_rampMaskMode == value) return; _rampMaskMode = value; _bitmapDirty = true; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SemanticAnchorRole SelectedAnchorRole { get; set; } = SemanticAnchorRole.Center;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public MapProject Project
    {
        get => _project;
        set
        {
            _project = value;
            _manualPlayerPlacementHistory.Clear();
            _bitmapDirty = true;
            if (_mapBitmap is not null && (_mapBitmap.Width != value.GridSize || _mapBitmap.Height != value.GridSize))
            {
                _mapBitmap.Dispose();
                _mapBitmap = null;
            }
            Invalidate();
        }
    }

    public event EventHandler? StrokeStarted;
    public event EventHandler? StrokeEnded;
    public event EventHandler? MapChanged;
    public event EventHandler<TerrainKind>? TerrainPicked;
    public event EventHandler<int>? ElevationPicked;
    public event EventHandler<string>? HoverTextChanged;
    public event EventHandler<Point>? CursorMoved;
    public event EventHandler? ZoomChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public void ZoomIn() => SetZoom(_zoom * ZoomStep, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    public void ZoomOut() => SetZoom(_zoom / ZoomStep, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    public void ResetView()
    {
        if (Math.Abs(_zoom - MinimumZoom) < 0.001f && _panOffset == PointF.Empty) return;
        _zoom = MinimumZoom;
        _panOffset = PointF.Empty;
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRemoteCursor(CollaboratorCursorUpdate cursor)
    {
        if (cursor.Cell.X < 0 || cursor.Cell.Y < 0) _remoteCursors.Remove(cursor.Id);
        else _remoteCursors[cursor.Id] = cursor;
        Invalidate();
    }

    public void RemoveRemoteCursor(Guid collaboratorId)
    {
        if (_remoteCursors.Remove(collaboratorId)) Invalidate();
    }

    public MapCanvas()
    {
        DoubleBuffered = true;
        BackColor = MedievalTheme.Canvas;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        MedievalArt.DrawCover(e.Graphics, MedievalAssets.CartographyTexture, ClientRectangle);
        using var wash = new SolidBrush(Color.FromArgb(38, Color.Black));
        e.Graphics.FillRectangle(wash, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        var area = GridRectangle();
        if (area.Width <= 0) return;
        var viewport = FitRectangle();
        var graphicsState = e.Graphics.Save();
        e.Graphics.SetClip(viewport);
        var cell = area.Width / (float)_project.GridSize;
        EnsureBitmap();
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_mapBitmap!, area);
        DrawTextureOverlay(e.Graphics, area, 0.34f);

        if (ShowGrid && cell >= 5)
        {
            using var pen = new Pen(Color.FromArgb(46, Color.Black), 1);
            for (var i = 0; i <= _project.GridSize; i++)
            {
                var p = area.Left + (int)Math.Round(i * cell);
                e.Graphics.DrawLine(pen, p, area.Top, p, area.Bottom);
                p = area.Top + (int)Math.Round(i * cell);
                e.Graphics.DrawLine(pen, area.Left, p, area.Right, p);
            }
        }

        var occupiedResourceLabels = new List<RectangleF>();
        var resourceMarkers = new List<(PointF Center, float Radius, Color Color,
            string Label, RectangleF LabelBounds)>();
        using var resourceLabelFont = MedievalTheme.UiFont(
            Math.Clamp(cell * 0.42f, 7.5f, 9f), FontStyle.Bold);
        foreach (var resource in _project.ManualResources)
        {
            var center = new PointF(area.Left + (resource.X + 0.5f) * cell,
                area.Top + (resource.Y + 0.5f) * cell);
            if (!viewport.Contains(Point.Round(center))) continue;
            var radius = Math.Clamp(cell * 0.42f, 3f, 10f);
            var color = ResourceColor(resource.Kind);
            using var fill = new SolidBrush(Color.FromArgb(220, color));
            using var edge = new Pen(Color.FromArgb(245, Color.Black), Math.Max(1f, radius / 4f));
            e.Graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            e.Graphics.DrawEllipse(edge, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            var label = resource.Kind.DisplayName();
            var labelBounds = ChooseResourceLabelBounds(
                center, radius, e.Graphics.MeasureString(label, resourceLabelFont),
                viewport, occupiedResourceLabels);
            occupiedResourceLabels.Add(labelBounds);
            resourceMarkers.Add((center, radius, color, label, labelBounds));
        }
        foreach (var marker in resourceMarkers)
        {
            var target = new PointF(
                Math.Clamp(marker.Center.X, marker.LabelBounds.Left, marker.LabelBounds.Right),
                Math.Clamp(marker.Center.Y, marker.LabelBounds.Top, marker.LabelBounds.Bottom));
            using var leader = new Pen(Color.FromArgb(185, marker.Color), 1f);
            using var labelFill = new SolidBrush(Color.FromArgb(225, MedievalTheme.Surface));
            using var labelEdge = new Pen(Color.FromArgb(220, marker.Color), 1f);
            using var labelText = new SolidBrush(Color.White);
            e.Graphics.DrawLine(leader, marker.Center, target);
            e.Graphics.FillRectangle(labelFill, marker.LabelBounds);
            e.Graphics.DrawRectangle(labelEdge, Rectangle.Round(marker.LabelBounds));
            e.Graphics.DrawString(marker.Label, resourceLabelFont, labelText,
                marker.LabelBounds.X + 4, marker.LabelBounds.Y + 1);
        }

        foreach (var start in _project.ManualPlayerStarts)
        {
            var center = new PointF(area.Left + (start.X + 0.5f) * cell,
                area.Top + (start.Y + 0.5f) * cell);
            var radius = Math.Clamp(cell * 0.72f, 8f, 15f);
            var color = Color.FromArgb(75, 154, 235);
            using var fill = new SolidBrush(Color.FromArgb(235, color));
            using var edge = new Pen(Color.FromArgb(250, MedievalTheme.GoldBright), 2f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            e.Graphics.DrawEllipse(edge, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            using var font = MedievalTheme.UiFont(Math.Clamp(radius * 0.72f, 7f, 10f), FontStyle.Bold);
            using var text = new SolidBrush(Color.White);
            var label = $"P{start.PlayerSlot}";
            var size = e.Graphics.MeasureString(label, font);
            e.Graphics.DrawString(label, font, text, center.X - size.Width / 2, center.Y - size.Height / 2);
            e.Graphics.SmoothingMode = SmoothingMode.None;
        }

        if (_hoverCell.X >= 0)
        {
            var pointMode = ManualResourceMode || ManualPlayerStartMode || SemanticAnchorMode;
            var radius = pointMode ? 0 : Math.Max(0, BrushSize - 1);
            var extent = pointMode
                ? 0
                : BrushShapeGeometry.FootprintExtent(
                    radius, BrushRotationDegrees, BrushLengthPercent, BrushWidthPercent);
            using var footprint = new SolidBrush(Color.FromArgb(82, MedievalTheme.GoldBright));
            using var mountainFootprint = new SolidBrush(Color.FromArgb(205, TerrainKind.Mountain.Color()));
            using var waterFootprint = new SolidBrush(Color.FromArgb(205, TerrainKind.Shallows.Color()));
            using var forestFootprint = new SolidBrush(Color.FromArgb(205, TerrainKind.Forest.Color()));
            using var edge = new Pen(Color.FromArgb(210, MedievalTheme.GoldBright), 1);
            for (var dy = -extent; dy <= extent; dy++)
                for (var dx = -extent; dx <= extent; dx++)
                {
                    var sample = SampleBrush(dx, dy, radius);
                    if (!sample.Included) continue;
                    var x = _hoverCell.X + dx;
                    var y = _hoverCell.Y + dy;
                    if (x < 0 || y < 0 || x >= _project.GridSize || y >= _project.GridSize) continue;
                    var bounds = Rectangle.FromLTRB(
                        area.Left + (int)Math.Floor(x * cell),
                        area.Top + (int)Math.Floor(y * cell),
                        area.Left + (int)Math.Ceiling((x + 1) * cell),
                        area.Top + (int)Math.Ceiling((y + 1) * cell));
                    e.Graphics.FillRectangle(sample.Terrain switch
                    {
                        TerrainKind.Mountain => mountainFootprint,
                        TerrainKind.Shallows => waterFootprint,
                        TerrainKind.Forest => forestFootprint,
                        _ => footprint
                    }, bounds);
                    if (cell >= 4) e.Graphics.DrawRectangle(edge, bounds);
                }
        }

        foreach (var cursor in _remoteCursors.Values)
        {
            if (cursor.Cell.X < 0 || cursor.Cell.Y < 0 || cursor.Cell.X >= _project.GridSize || cursor.Cell.Y >= _project.GridSize)
                continue;
            var color = CursorColor(cursor.Id);
            var center = new PointF(
                area.Left + (cursor.Cell.X + 0.5f) * cell,
                area.Top + (cursor.Cell.Y + 0.5f) * cell);
            var radius = Math.Max(4f, cell * 0.42f);
            using var fill = new SolidBrush(Color.FromArgb(88, color));
            using var edge = new Pen(color, Math.Max(1.5f, cell / 8f));
            e.Graphics.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            e.Graphics.DrawEllipse(edge, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            var name = string.IsNullOrWhiteSpace(cursor.Author) ? "Collaborator" : cursor.Author;
            using var font = MedievalTheme.UiFont(8f, FontStyle.Bold);
            var nameSize = e.Graphics.MeasureString(name, font);
            var label = new RectangleF(center.X + radius + 3, center.Y - nameSize.Height / 2, nameSize.Width + 8, nameSize.Height + 3);
            using var labelFill = new SolidBrush(Color.FromArgb(220, MedievalTheme.Surface));
            using var text = new SolidBrush(color);
            e.Graphics.FillRectangle(labelFill, label);
            e.Graphics.DrawString(name, font, text, label.X + 4, label.Y + 1);
        }
        e.Graphics.Restore(graphicsState);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStart = e.Location;
            _panOrigin = _panOffset;
            _cursorBeforePan = Cursor;
            Cursor = Cursors.Hand;
            Capture = true;
            return;
        }
        if (ManualPlayerStartMode && e.Button == MouseButtons.Right)
        {
            StrokeStarted?.Invoke(this, EventArgs.Empty);
            if (RemoveLastManualPlayerStart())
            {
                Invalidate();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        var cell = CellAt(e.Location);
        if (cell.X < 0) return;
        if (ManualPlayerStartMode)
        {
            if (e.Button != MouseButtons.Left) return;
            StrokeStarted?.Invoke(this, EventArgs.Empty);
            var changed = PlaceManualPlayerStart(cell.X, cell.Y);
            if (changed)
            {
                Invalidate();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (ManualResourceMode)
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            StrokeStarted?.Invoke(this, EventArgs.Empty);
            var changed = e.Button == MouseButtons.Right
                ? RemoveManualResource(cell.X, cell.Y)
                : PlaceManualResource(cell.X, cell.Y);
            if (changed)
            {
                Invalidate();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (SemanticAnchorMode)
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            if (_project[cell.X, cell.Y].IsWater()) return;
            StrokeStarted?.Invoke(this, EventArgs.Empty);
            var role = e.Button == MouseButtons.Right ? SemanticAnchorRole.None : SelectedAnchorRole;
            if (AssignConnectedComponent(cell.X, cell.Y, role))
            {
                _bitmapDirty = true;
                Invalidate();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (ElevationMaskMode && e.Button == MouseButtons.Right)
        {
            var value = _project.ElevationMask[cell.Y * _project.GridSize + cell.X];
            if (value != MapProject.AutomaticElevation)
            {
                if (value == MapProject.CliffElevation) value = 100;
                SelectedElevation = value;
                ElevationPicked?.Invoke(this, value);
            }
            return;
        }
        if (RampMaskMode && e.Button == MouseButtons.Right)
        {
            StrokeStarted?.Invoke(this, EventArgs.Empty);
            if (SetRampWithMirrors(cell.X, cell.Y, false))
            {
                _bitmapDirty = true;
                Invalidate();
                MapChanged?.Invoke(this, EventArgs.Empty);
            }
            StrokeEnded?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            SelectedTerrain = _project[cell.X, cell.Y];
            TerrainPicked?.Invoke(this, SelectedTerrain);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _painting = true;
        Capture = true;
        StrokeStarted?.Invoke(this, EventArgs.Empty);
        _lastPaintCell = cell;
        PaintAt(cell.X, cell.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            _panOffset = new PointF(
                _panOrigin.X + e.X - _panStart.X,
                _panOrigin.Y + e.Y - _panStart.Y);
            ClampPan();
            Invalidate();
            return;
        }
        var cell = CellAt(e.Location);
        if (cell != _hoverCell)
        {
            _hoverCell = cell;
            HoverTextChanged?.Invoke(this, cell.X < 0 ? "" : HoverText(cell));
            CursorMoved?.Invoke(this, cell);
            Invalidate();
        }
        if (!SemanticAnchorMode && !ManualResourceMode && !ManualPlayerStartMode && _painting && cell.X >= 0)
        {
            PaintLine(_lastPaintCell, cell);
            _lastPaintCell = cell;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && e.Button == MouseButtons.Middle)
        {
            _panning = false;
            Capture = false;
            Cursor = _cursorBeforePan ?? Cursors.Cross;
            _cursorBeforePan = null;
            return;
        }
        if (!_painting) return;
        _painting = false;
        _lastPaintCell = new Point(-1, -1);
        Capture = false;
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverCell = new Point(-1, -1);
        HoverTextChanged?.Invoke(this, "");
        CursorMoved?.Invoke(this, _hoverCell);
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || !_panning) return;
        _panning = false;
        Cursor = _cursorBeforePan ?? Cursors.Cross;
        _cursorBeforePan = null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_painting || _panning || e.Delta == 0 || !FitRectangle().Contains(e.Location)) return;
        var steps = e.Delta / (float)SystemInformation.MouseWheelScrollDelta;
        SetZoom(_zoom * MathF.Pow(ZoomStep, steps), e.Location);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampPan();
        Invalidate();
    }

    private void PaintAt(int centerX, int centerY)
    {
        var radius = Math.Max(0, BrushSize - 1);
        var extent = BrushShapeGeometry.FootprintExtent(
            radius, BrushRotationDegrees, BrushLengthPercent, BrushWidthPercent);
        var changed = false;
        for (var dy = -extent; dy <= extent; dy++)
            for (var dx = -extent; dx <= extent; dx++)
            {
                var sample = SampleBrush(dx, dy, radius);
                if (!sample.Included) continue;
                changed |= SetWithMirrors(centerX + dx, centerY + dy, sample.Terrain);
            }
        if (!changed) return;
        _bitmapDirty = true;
        Invalidate();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool InsideBrush(int dx, int dy, int radius)
        => SampleBrush(dx, dy, radius).Included;

    private (bool Included, TerrainKind? Terrain) SampleBrush(int dx, int dy, int radius)
    {
        var stateBrush = StateBrush;
        var featuresAllowed = StateFeaturesAllowed && !ElevationMaskMode && !RampMaskMode &&
                              !ManualResourceMode && !ManualPlayerStartMode && !SemanticAnchorMode;
        var water = featuresAllowed && StateWaterEnabled;
        var mountains = featuresAllowed && StateMountainsEnabled;
        var forests = featuresAllowed && StateForestsEnabled;
        if (stateBrush is not null && radius > 0)
        {
            var key = (stateBrush, radius, BrushRotationDegrees, BrushLengthPercent, BrushWidthPercent, water);
            if (_stateFootprintKey != key)
            {
                _stateFootprintKey = key;
                _stateFootprintHasCells = StateSample(0, 0).Included;
                var extent = BrushShapeGeometry.FootprintExtent(
                    radius, BrushRotationDegrees, BrushLengthPercent, BrushWidthPercent);
                // Island groups can fall between cells at tiny scales. Check
                // once per brush configuration so preview and painting agree.
                for (var sampleY = -extent; sampleY <= extent && !_stateFootprintHasCells; sampleY++)
                    for (var sampleX = -extent; sampleX <= extent; sampleX++)
                    {
                        if (!StateSample(sampleX, sampleY).Included) continue;
                        _stateFootprintHasCells = true;
                        break;
                    }
            }

            if (!_stateFootprintHasCells) return (dx == 0 && dy == 0, null);
            return StateSample(dx, dy);
        }

        return (BrushShapeGeometry.ContainsRotated(
            BrushShape, dx, dy, radius, BrushRotationDegrees,
            BrushLengthPercent, BrushWidthPercent, stateBrush), null);

        (bool Included, TerrainKind? Terrain) StateSample(int sampleX, int sampleY)
        {
            var (localX, localY) = BrushShapeGeometry.ToLocalCoordinates(
                sampleX, sampleY, BrushRotationDegrees, BrushLengthPercent, BrushWidthPercent);
            var x = localX / radius;
            var y = localY / radius;
            var terrain = stateBrush!.GetFeature(x, y, mountains, water, forests);
            return (stateBrush.Contains(x, y) || terrain == TerrainKind.Shallows, terrain);
        }
    }

    private void PaintLine(Point from, Point to)
    {
        if (from.X < 0) { PaintAt(to.X, to.Y); return; }
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0) return;
        for (var step = 1; step <= steps; step++)
        {
            var x = (int)Math.Round(from.X + dx * (step / (double)steps));
            var y = (int)Math.Round(from.Y + dy * (step / (double)steps));
            PaintAt(x, y);
        }
    }

    private bool SetWithMirrors(int x, int y, TerrainKind? terrainOverride = null)
    {
        var changed = Set(x, y, terrainOverride);
        if (MirrorHorizontal) changed |= Set(_project.GridSize - 1 - x, y, terrainOverride);
        if (MirrorVertical) changed |= Set(x, _project.GridSize - 1 - y, terrainOverride);
        if (MirrorHorizontal && MirrorVertical) changed |= Set(_project.GridSize - 1 - x, _project.GridSize - 1 - y, terrainOverride);
        return changed;
    }

    private bool Set(int x, int y, TerrainKind? terrainOverride = null)
    {
        if (x < 0 || y < 0 || x >= _project.GridSize || y >= _project.GridSize)
            return false;
        var index = y * _project.GridSize + x;
        if (RampMaskMode)
        {
            if (!_project[x, y].IsLand() || _project.CliffMask[index] != 0 ||
                _project.RampMask[index] == 1) return false;
            _project.RampMask[index] = 1;
            return true;
        }
        if (ElevationMaskMode)
        {
            var targetTerrain = SelectedElevation switch
            {
                <= -50 => TerrainKind.DeepWater,
                < 0 => TerrainKind.Shallows,
                0 => TerrainKind.Beach,
                _ when _project[x, y].IsWater() => TerrainKind.Grassland,
                _ => _project[x, y]
            };
            var removedResource = RemoveIncompatibleManualResourcesAt(x, y, targetTerrain);
            var cliff = PaintCliffs ? (byte)1 : (byte)0;
            if (_project.ElevationMask[index] == SelectedElevation && _project[x, y] == targetTerrain &&
                _project.CliffMask[index] == cliff &&
                (!(PaintCliffs || targetTerrain.IsWater()) || _project.RampMask[index] == 0))
                return removedResource;
            _project[x, y] = targetTerrain;
            _project.ElevationMask[index] = SelectedElevation;
            _project.CliffMask[index] = cliff;
            if (PaintCliffs || targetTerrain.IsWater()) _project.RampMask[index] = 0;
            if (!MapProject.IsSafePlayerStartTerrain(targetTerrain))
                _project.ManualPlayerStarts.RemoveAll(start => start.X == x && start.Y == y);
            if (targetTerrain.IsWater())
                _project.ComponentRoles[index] = (byte)SemanticAnchorRole.None;
            return true;
        }
        var terrain = terrainOverride ?? SelectedTerrain;
        var removedIncompatibleResource = RemoveIncompatibleManualResourcesAt(x, y, terrain);
        var metadataChanged = false;
        if (terrainOverride == TerrainKind.Mountain)
        {
            metadataChanged = _project.ElevationMask[index] != MapProject.CliffElevation ||
                _project.RampMask[index] != 0 || _project.CliffMask[index] != 0;
            _project.ElevationMask[index] = MapProject.CliffElevation;
            _project.RampMask[index] = 0;
            _project.CliffMask[index] = 0;
        }
        else if (_project.ElevationMask[index] == MapProject.CliffElevation)
        {
            _project.ElevationMask[index] = MapProject.AutomaticElevation;
            _project.CliffMask[index] = 0;
            metadataChanged = true;
        }
        if (_project[x, y] == terrain)
        {
            if (terrain.IsLand() && _project.ElevationMask[index] <= 0)
            {
                _project.ElevationMask[index] = MapProject.AutomaticElevation;
                _project.CliffMask[index] = 0;
                return true;
            }
            if (!terrain.IsWater() || _project.ComponentRoles[index] == (byte)SemanticAnchorRole.None)
                return removedIncompatibleResource || metadataChanged;
            _project.ComponentRoles[index] = (byte)SemanticAnchorRole.None;
            return true;
        }
        _project[x, y] = terrain;
        if (!MapProject.IsSafePlayerStartTerrain(terrain))
            _project.ManualPlayerStarts.RemoveAll(start => start.X == x && start.Y == y);
        if (terrain.IsLand() && _project.ElevationMask[index] <= 0)
        {
            _project.ElevationMask[index] = MapProject.AutomaticElevation;
            _project.CliffMask[index] = 0;
        }
        if (terrain.IsWater())
        {
            _project.ComponentRoles[index] = (byte)SemanticAnchorRole.None;
            _project.ElevationMask[index] = MapProject.AutomaticElevation;
            _project.CliffMask[index] = 0;
            _project.RampMask[index] = 0;
        }
        return true;
    }

    private bool RemoveIncompatibleManualResourcesAt(int x, int y, TerrainKind terrain) =>
        _project.ManualResources.RemoveAll(resource =>
            resource.X == x && resource.Y == y && resource.Kind.RequiresWater() == terrain.IsLand()) > 0;

    private bool SetRampWithMirrors(int x, int y, bool enabled)
    {
        var changed = SetRamp(x, y, enabled);
        if (MirrorHorizontal) changed |= SetRamp(_project.GridSize - 1 - x, y, enabled);
        if (MirrorVertical) changed |= SetRamp(x, _project.GridSize - 1 - y, enabled);
        if (MirrorHorizontal && MirrorVertical)
            changed |= SetRamp(_project.GridSize - 1 - x, _project.GridSize - 1 - y, enabled);
        return changed;
    }

    private bool SetRamp(int x, int y, bool enabled)
    {
        if (x < 0 || y < 0 || x >= _project.GridSize || y >= _project.GridSize) return false;
        var index = y * _project.GridSize + x;
        var value = enabled ? (byte)1 : (byte)0;
        if ((!enabled || (_project[x, y].IsLand() && _project.CliffMask[index] == 0)) &&
            _project.RampMask[index] != value)
        {
            _project.RampMask[index] = value;
            return true;
        }
        return false;
    }

    public bool ClearRampMask()
    {
        if (!_project.RampMask.Any(value => value != 0)) return false;
        Array.Clear(_project.RampMask);
        _bitmapDirty = true;
        Invalidate();
        return true;
    }

    public bool ClearSemanticAnchors()
    {
        if (!_project.ComponentRoles.Any(role => role != (byte)SemanticAnchorRole.None)) return false;
        Array.Clear(_project.ComponentRoles);
        _bitmapDirty = true;
        Invalidate();
        return true;
    }

    public bool ClearElevationMask()
    {
        if (!_project.ElevationMask.Any(value => value != MapProject.AutomaticElevation) &&
            !_project.CliffMask.Any(value => value != 0)) return false;
        Array.Fill(_project.ElevationMask, MapProject.AutomaticElevation);
        Array.Clear(_project.CliffMask);
        _bitmapDirty = true;
        Invalidate();
        return true;
    }

    public bool ClearManualPlayerStarts()
    {
        _manualPlayerPlacementHistory.Clear();
        if (_project.ManualPlayerStarts.Count == 0) return false;
        _project.ManualPlayerStarts.Clear();
        Invalidate();
        return true;
    }

    private bool AssignConnectedComponent(int startX, int startY, SemanticAnchorRole role)
    {
        var seen = new bool[_project.Cells.Length];
        var queue = new Queue<int>();
        var component = new List<int>();
        var start = startY * _project.GridSize + startX;
        seen[start] = true;
        queue.Enqueue(start);
        ReadOnlySpan<(int X, int Y)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            component.Add(index);
            var x = index % _project.GridSize;
            var y = index / _project.GridSize;
            foreach (var direction in directions)
            {
                var nx = x + direction.X;
                var ny = y + direction.Y;
                if (nx < 0 || ny < 0 || nx >= _project.GridSize || ny >= _project.GridSize) continue;
                var next = ny * _project.GridSize + nx;
                if (seen[next] || ((TerrainKind)_project.Cells[next]).IsWater()) continue;
                seen[next] = true;
                queue.Enqueue(next);
            }
        }

        var value = (byte)role;
        var changed = false;
        foreach (var index in component)
        {
            if (_project.ComponentRoles[index] == value) continue;
            _project.ComponentRoles[index] = value;
            changed = true;
        }
        return changed;
    }

    private string HoverText(Point cell)
    {
        var terrain = _project[cell.X, cell.Y];
        var elevation = _project.ElevationMask[cell.Y * _project.GridSize + cell.X];
        var terrainName = elevation == MapProject.CliffElevation ? "Cliffs" : terrain.DisplayName();
        var text = $"Cell {cell.X + 1}, {cell.Y + 1} · {terrainName}";
        var resource = _project.ManualResources.LastOrDefault(item => item.X == cell.X && item.Y == cell.Y);
        if (resource is not null) text += $" · {resource.Kind.DisplayName()}";
        var start = _project.ManualPlayerStarts.LastOrDefault(item => item.X == cell.X && item.Y == cell.Y);
        if (start is not null)
            text += $" · lobby slot {start.PlayerSlot}";
        if (ElevationMaskMode && elevation != MapProject.CliffElevation)
            text += elevation == MapProject.AutomaticElevation
                ? " · elevation automatic"
                : $" · elevation {elevation:+#;-#;0}";
        if (_project.CliffMask[cell.Y * _project.GridSize + cell.X] != 0)
            text += " · cliff edges";
        if (!terrain.IsLand()) return text;
        var role = (SemanticAnchorRole)_project.ComponentRoles[cell.Y * _project.GridSize + cell.X];
        return role == SemanticAnchorRole.None ? text : $"{text} · {role.DisplayName()}";
    }

    private Rectangle FitRectangle()
    {
        var size = Math.Max(0, Math.Min(ClientSize.Width, ClientSize.Height) - 18);
        return new Rectangle((ClientSize.Width - size) / 2, (ClientSize.Height - size) / 2, size, size);
    }

    private Rectangle GridRectangle()
    {
        var fit = FitRectangle();
        var size = Math.Max(0, (int)Math.Round(fit.Width * _zoom));
        return new Rectangle(
            (int)Math.Round((ClientSize.Width - size) / 2f + _panOffset.X),
            (int)Math.Round((ClientSize.Height - size) / 2f + _panOffset.Y),
            size,
            size);
    }

    private void SetZoom(float value, Point anchor)
    {
        var next = Math.Clamp(value, MinimumZoom, MaximumZoom);
        if (Math.Abs(next - _zoom) < 0.001f) return;
        var oldArea = GridRectangle();
        var relativeX = oldArea.Width == 0 ? 0.5f : (anchor.X - oldArea.Left) / (float)oldArea.Width;
        var relativeY = oldArea.Height == 0 ? 0.5f : (anchor.Y - oldArea.Top) / (float)oldArea.Height;
        _zoom = next;
        var fit = FitRectangle();
        var newSize = fit.Width * _zoom;
        _panOffset = new PointF(
            anchor.X - relativeX * newSize - (ClientSize.Width - newSize) / 2f,
            anchor.Y - relativeY * newSize - (ClientSize.Height - newSize) / 2f);
        ClampPan();
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClampPan()
    {
        var fit = FitRectangle();
        var overflow = Math.Max(0f, fit.Width * _zoom - fit.Width) / 2f;
        _panOffset = new PointF(
            Math.Clamp(_panOffset.X, -overflow, overflow),
            Math.Clamp(_panOffset.Y, -overflow, overflow));
        if (_zoom <= MinimumZoom) _panOffset = PointF.Empty;
    }

    private static void DrawTextureOverlay(Graphics graphics, Rectangle bounds, float opacity)
    {
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix
        {
            Matrix00 = 1,
            Matrix11 = 1,
            Matrix22 = 1,
            Matrix33 = opacity,
            Matrix44 = 1
        };
        attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        var texture = MedievalAssets.CartographyTexture;
        graphics.DrawImage(texture, bounds, 0, 0, texture.Width, texture.Height, GraphicsUnit.Pixel, attributes);
    }

    private static RectangleF ChooseResourceLabelBounds(
        PointF center,
        float radius,
        SizeF textSize,
        Rectangle area,
        IReadOnlyList<RectangleF> occupied)
    {
        var width = Math.Min(area.Width, Math.Max(20f, textSize.Width + 8f));
        var height = Math.Max(12f, textSize.Height + 2f);
        var candidates = new List<RectangleF>(22);
        var gap = radius + 4f;
        for (var step = 0; step <= 5; step++)
        {
            var offsets = step == 0 ? new[] { 0 } : new[] { -step, step };
            foreach (var offset in offsets)
            {
                var y = center.Y - height / 2 + offset * (height + 2f);
                candidates.Add(new RectangleF(center.X + gap, y, width, height));
                candidates.Add(new RectangleF(center.X - gap - width, y, width, height));
            }
        }

        RectangleF best = default;
        var bestScore = float.MaxValue;
        foreach (var original in candidates)
        {
            var candidate = new RectangleF(
                Math.Clamp(original.X, area.Left, Math.Max(area.Left, area.Right - width)),
                Math.Clamp(original.Y, area.Top, Math.Max(area.Top, area.Bottom - height)),
                width,
                height);
            var score = Math.Abs(candidate.X + width / 2 - center.X) +
                        Math.Abs(candidate.Y + height / 2 - center.Y);
            foreach (var existing in occupied)
            {
                var overlap = RectangleF.Intersect(candidate, existing);
                score += overlap.Width * overlap.Height * 1000f;
            }
            if (score >= bestScore) continue;
            bestScore = score;
            best = candidate;
            if (!occupied.Any(existing => existing.IntersectsWith(candidate))) break;
        }
        return best;
    }

    private void EnsureBitmap()
    {
        if (!_bitmapDirty && _mapBitmap is not null) return;
        if (_mapBitmap is null || _mapBitmap.Width != _project.GridSize || _mapBitmap.Height != _project.GridSize)
        {
            _mapBitmap?.Dispose();
            _mapBitmap = new Bitmap(_project.GridSize, _project.GridSize, PixelFormat.Format32bppArgb);
        }

        var palette = TerrainCatalog.All.ToDictionary(kind => (int)kind, kind => kind.Color().ToArgb());
        var pixels = new int[_project.Cells.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var terrain = (TerrainKind)_project.Cells[i];
            var baseColor = ElevationMaskMode || RampMaskMode
                ? ElevationColor(EffectiveElevation(terrain, _project.ElevationMask[i]))
                : Color.FromArgb(palette[_project.Cells[i]]);
            if (RampMaskMode && _project.RampMask[i] != 0 && _project.CliffMask[i] == 0)
            {
                var ramp = Color.FromArgb(246, 196, 68);
                baseColor = Color.FromArgb(
                    (baseColor.R * 3 + ramp.R * 2) / 5,
                    (baseColor.G * 3 + ramp.G * 2) / 5,
                    (baseColor.B * 3 + ramp.B * 2) / 5);
            }
            var role = (SemanticAnchorRole)_project.ComponentRoles[i];
            if (!ElevationMaskMode && role != SemanticAnchorRole.None && terrain.IsLand())
            {
                var overlay = role.Color();
                baseColor = Color.FromArgb(
                    (baseColor.R * 2 + overlay.R * 3) / 5,
                    (baseColor.G * 2 + overlay.G * 3) / 5,
                    (baseColor.B * 2 + overlay.B * 3) / 5);
            }
            if (_project.CliffMask[i] != 0 && IsCliffBoundary(i))
            {
                var cliff = Color.FromArgb(181, 187, 195);
                baseColor = Color.FromArgb(
                    (baseColor.R + cliff.R * 2) / 3,
                    (baseColor.G + cliff.G * 2) / 3,
                    (baseColor.B + cliff.B * 2) / 3);
            }
            pixels[i] = baseColor.ToArgb();
        }
        var bounds = new Rectangle(0, 0, _mapBitmap.Width, _mapBitmap.Height);
        var data = _mapBitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == _mapBitmap.Width * sizeof(int))
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            else
                for (var y = 0; y < _mapBitmap.Height; y++)
                    Marshal.Copy(pixels, y * _mapBitmap.Width, data.Scan0 + y * data.Stride, _mapBitmap.Width);
        }
        finally { _mapBitmap.UnlockBits(data); }
        _bitmapDirty = false;
    }

    private bool IsCliffBoundary(int index)
    {
        var size = _project.GridSize;
        var x = index % size;
        var y = index / size;
        var elevation = _project.ElevationMask[index];
        return x > 0 && IsDifferent(index - 1) || x + 1 < size && IsDifferent(index + 1) ||
               y > 0 && IsDifferent(index - size) || y + 1 < size && IsDifferent(index + size);

        bool IsDifferent(int neighbor) => _project.CliffMask[neighbor] == 0 ||
                                          _project.ElevationMask[neighbor] != elevation;
    }

    private static int EffectiveElevation(TerrainKind terrain, sbyte painted) =>
        painted == MapProject.CliffElevation ? 100 :
        painted != MapProject.AutomaticElevation ? painted : terrain switch
        {
            TerrainKind.DeepWater => -100,
            TerrainKind.Shallows => -30,
            TerrainKind.Beach => 0,
            TerrainKind.Grassland => 20,
            TerrainKind.Forest => 30,
            TerrainKind.Hills => 55,
            TerrainKind.Mountain => 85,
            TerrainKind.Desert => 25,
            _ => 0
        };

    private static Color ElevationColor(int value)
    {
        ReadOnlySpan<(int Stop, Color Color)> stops =
        [
            (-100, Color.FromArgb(10, 28, 78)),
            (-50, Color.FromArgb(21, 76, 139)),
            (0, Color.FromArgb(67, 178, 202)),
            (25, Color.FromArgb(74, 156, 87)),
            (50, Color.FromArgb(211, 184, 75)),
            (75, Color.FromArgb(181, 91, 54)),
            (100, Color.FromArgb(242, 238, 224))
        ];
        value = Math.Clamp(value, -100, 100);
        for (var i = 1; i < stops.Length; i++)
        {
            if (value > stops[i].Stop) continue;
            var lower = stops[i - 1];
            var upper = stops[i];
            var amount = (value - lower.Stop) / (double)(upper.Stop - lower.Stop);
            return Color.FromArgb(
                (int)Math.Round(lower.Color.R + (upper.Color.R - lower.Color.R) * amount),
                (int)Math.Round(lower.Color.G + (upper.Color.G - lower.Color.G) * amount),
                (int)Math.Round(lower.Color.B + (upper.Color.B - lower.Color.B) * amount));
        }
        return stops[^1].Color;
    }

    private static Color CursorColor(Guid id)
    {
        var hash = id.GetHashCode() & 0x7fffffff;
        return Color.FromArgb(255, 110 + hash % 120, 115 + (hash / 7) % 115, 120 + (hash / 17) % 110);
    }

    private bool PlaceManualResource(int x, int y)
    {
        var terrain = _project[x, y];
        if (SelectedManualResource.RequiresWater() == terrain.IsLand())
            return false;
        _project.ManualResources.RemoveAll(resource => resource.X == x && resource.Y == y);
        _project.ManualResources.Add(new ManualResource { Kind = SelectedManualResource, X = x, Y = y });
        return true;
    }

    private bool RemoveManualResource(int x, int y) =>
        _project.ManualResources.RemoveAll(resource => resource.X == x && resource.Y == y) > 0;

    private bool PlaceManualPlayerStart(int x, int y)
    {
        if (!MapProject.IsSafePlayerStartTerrain(_project[x, y])) return false;
        if (_project.ManualPlayerStarts.Any(start => start.X == x && start.Y == y)) return false;
        var nextSlot = Enumerable.Range(ManualPlayerStart.MinimumSlot,
                ManualPlayerStart.MaximumSlot - ManualPlayerStart.MinimumSlot + 1)
            .FirstOrDefault(slot => _project.ManualPlayerStarts.All(start => start.PlayerSlot != slot));
        if (nextSlot == 0) return false;

        _project.ManualPlayerStarts.Add(new ManualPlayerStart
        {
            PlayerSlot = nextSlot,
            X = x,
            Y = y
        });
        _project.ManualPlayerStarts.Sort((left, right) => left.PlayerSlot.CompareTo(right.PlayerSlot));
        _manualPlayerPlacementHistory.Add(nextSlot);
        return true;
    }

    private bool RemoveLastManualPlayerStart()
    {
        while (_manualPlayerPlacementHistory.Count > 0)
        {
            var historyIndex = _manualPlayerPlacementHistory.Count - 1;
            var slot = _manualPlayerPlacementHistory[historyIndex];
            _manualPlayerPlacementHistory.RemoveAt(historyIndex);
            if (_project.ManualPlayerStarts.RemoveAll(start => start.PlayerSlot == slot) > 0)
                return true;
        }
        if (_project.ManualPlayerStarts.Count == 0) return false;
        var fallbackSlot = _project.ManualPlayerStarts.Max(start => start.PlayerSlot);
        return _project.ManualPlayerStarts.RemoveAll(start => start.PlayerSlot == fallbackSlot) > 0;
    }

    private static Color ResourceColor(ManualResourceKind kind) => kind switch
    {
        ManualResourceKind.Gold => Color.Gold,
        ManualResourceKind.Stone => Color.SlateGray,
        ManualResourceKind.BerryBush => Color.MediumVioletRed,
        ManualResourceKind.Fish => Color.DeepSkyBlue,
        ManualResourceKind.Wildlife => Color.SaddleBrown,
        ManualResourceKind.Deer => Color.Peru,
        ManualResourceKind.Goat => Color.DarkKhaki,
        ManualResourceKind.BighornSheep => Color.Tan,
        ManualResourceKind.Tiger => Color.DarkOrange,
        ManualResourceKind.Bear => Color.Sienna,
        ManualResourceKind.Shark => Color.SteelBlue,
        ManualResourceKind.Squirrel => Color.Chocolate,
        ManualResourceKind.Sheep => Color.Wheat,
        ManualResourceKind.BlueBird => Color.Goldenrod,
        ManualResourceKind.Walrus => Color.RosyBrown,
        ManualResourceKind.Wolf => Color.DimGray,
        ManualResourceKind.Chicken => Color.Firebrick,
        ManualResourceKind.Horse => Color.DarkGoldenrod,
        ManualResourceKind.Dolphin => Color.DodgerBlue,
        ManualResourceKind.WildPig => Color.IndianRed,
        ManualResourceKind.Elk => Color.OliveDrab,
        _ => Color.White
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mapBitmap?.Dispose();
        base.Dispose(disposing);
    }

    private Point CellAt(Point point)
    {
        if (!FitRectangle().Contains(point)) return new Point(-1, -1);
        var area = GridRectangle();
        if (!area.Contains(point)) return new Point(-1, -1);
        var x = Math.Clamp((point.X - area.Left) * _project.GridSize / Math.Max(1, area.Width), 0, _project.GridSize - 1);
        var y = Math.Clamp((point.Y - area.Top) * _project.GridSize / Math.Max(1, area.Height), 0, _project.GridSize - 1);
        return new Point(x, y);
    }
}
