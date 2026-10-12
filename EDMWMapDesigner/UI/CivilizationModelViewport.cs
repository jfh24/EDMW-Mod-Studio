using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>A bounded software scene renderer with textured, depth-tested animated poses.</summary>
internal sealed class CivilizationModelViewport : Control
{
    private const int MaximumRenderDimension = 1024;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(-.4f, -.6f, 1));
    private ModelMesh? _mesh;
    private ModelPose? _pose;
    private ModelTexture?[] _triangleTextures = [];
    private bool[] _trianglePlayerColors = [];
    private readonly Dictionary<ModelTexture, bool> _opaqueTextures = [];
    private Vector3 _center;
    private double _normalization = 1;
    private float _ground;
    private float _framePosition;
    private float _yaw = .55f;
    private float _pitch = .45f;
    private float _zoom = 1;
    private Point? _drag;
    private string _emptyMessage = "Choose a model";
    private int _playerColorArgb = unchecked((int)0xff2125a2);
    private Bitmap? _image;
    private int[] _paintPixels = [];
    private float[] _paintDepth = [];
    private ScreenVertex[] _paintVertices = [];
    private readonly List<(int Triangle, float Depth)> _paintTransparent = [];
    private bool _dirty = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ModelMesh? Mesh { get => _mesh; set => SetModel(value); }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float FramePosition
    {
        get => _framePosition;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            var frame = Math.Clamp(value, 0, Math.Max(0, (_mesh?.FrameCount ?? 1) - 1));
            if (Math.Abs(frame - _framePosition) < .00001f) return;
            _framePosition = frame;
            _pose = _mesh?.GetPose(frame);
            MarkDirty();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyMessage
    {
        get => _emptyMessage;
        set { if (_emptyMessage == value) return; _emptyMessage = value; MarkDirty(); }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int PlayerColorArgb
    {
        get => _playerColorArgb;
        set { if (_playerColorArgb == value) return; _playerColorArgb = value | unchecked((int)0xff000000); MarkDirty(); }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ModelRenderStatistics LastRenderStatistics { get; private set; }

    public CivilizationModelViewport()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = MedievalTheme.Canvas;
        ForeColor = MedievalTheme.TextMuted;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleName = "Interactive game model preview";
        AccessibleDescription = "Drag to rotate, scroll to zoom, double-click to reset. Arrow keys rotate and plus or minus zoom.";
    }

    public void SetModel(ModelMesh? mesh, ModelAppearance? appearance = null, bool resetView = true)
    {
        _mesh = mesh;
        _pose = mesh?.GetPose(0);
        _framePosition = 0;
        _triangleTextures = new ModelTexture?[mesh?.TriangleIndices.Count / 3 ?? 0];
        _trianglePlayerColors = new bool[_triangleTextures.Length];
        _opaqueTextures.Clear();
        if (mesh is not null)
        {
            var minimum = mesh.BoundsMinimum;
            var maximum = mesh.BoundsMaximum;
            _center = new Vector3((float)(((double)minimum.X + maximum.X) * .5),
                (float)(((double)minimum.Y + maximum.Y) * .5), (float)(((double)minimum.Z + maximum.Z) * .5));
            _normalization = 2 / Math.Max(.00001, Math.Max((double)maximum.X - minimum.X,
                Math.Max((double)maximum.Y - minimum.Y, (double)maximum.Z - minimum.Z)));
            _ground = (float)(((double)minimum.Z - _center.Z) * _normalization) - .012f;
            foreach (var material in mesh.Materials)
            {
                var playerColor = material.Name.Equals("player color", StringComparison.OrdinalIgnoreCase);
                var texture = appearance?.FindTexture(material);
                for (var triangle = material.TriangleStart;
                    triangle < material.TriangleStart + material.TriangleCount && triangle < _triangleTextures.Length; triangle++)
                {
                    _triangleTextures[triangle] = texture;
                    _trianglePlayerColors[triangle] = playerColor;
                }
                if (texture is not null && !_opaqueTextures.ContainsKey(texture))
                    _opaqueTextures.Add(texture, texture.Pixels.All(pixel => (uint)pixel >> 24 == 255));
            }
        }
        LastRenderStatistics = default;
        if (resetView) ResetView();
        else MarkDirty();
    }

    public void ResetView()
    {
        _yaw = .55f;
        _pitch = .45f;
        _zoom = 1;
        MarkDirty();
    }

    private void MarkDirty() { _dirty = true; Invalidate(); }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); _dirty = true; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 1 || Height < 1) return;
        if (_mesh is null || _pose is null)
        {
            using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 29, 31),
                Color.FromArgb(10, 14, 16), LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(background, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, _emptyMessage, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        else
        {
            var ratio = Math.Min(1d, MaximumRenderDimension / (double)Math.Max(Width, Height));
            var width = Math.Max(1, (int)Math.Round(Width * ratio));
            var height = Math.Max(1, (int)Math.Round(Height * ratio));
            if (_dirty || _image is null || _image.Width != width || _image.Height != height)
            {
                var frame = RenderFrame(width, height, reusePaintBuffers: true);
                if (_image is null || _image.Width != width || _image.Height != height)
                {
                    _image?.Dispose();
                    _image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                }
                var data = _image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (data.Stride == width * 4) Marshal.Copy(frame.Pixels, 0, data.Scan0, frame.Pixels.Length);
                    else for (var row = 0; row < height; row++)
                        Marshal.Copy(frame.Pixels, row * width, data.Scan0 + row * data.Stride, width);
                }
                finally { _image.UnlockBits(data); }
                LastRenderStatistics = frame.Statistics;
                _dirty = false;
            }
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_image, ClientRectangle);
        }
        using var border = new Pen(MedievalTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Returns pixels and numeric diagnostics without creating a window or exporting an image.</summary>
    internal ModelRenderedFrame RenderFrame(int width, int height) => RenderFrame(width, height, reusePaintBuffers: false);

    private ModelRenderedFrame RenderFrame(int width, int height, bool reusePaintBuffers)
    {
        if (width is < 1 or > MaximumRenderDimension || height is < 1 or > MaximumRenderDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Preview dimensions must be between 1 and 1024 pixels.");
        var pixels = reusePaintBuffers && _paintPixels.Length == width * height ? _paintPixels : new int[width * height];
        if (reusePaintBuffers) _paintPixels = pixels;
        FillBackground(pixels, width, height);
        if (_mesh is null || _pose is null) return new ModelRenderedFrame(width, height, pixels, default);
        var depth = reusePaintBuffers && _paintDepth.Length == pixels.Length ? _paintDepth : new float[pixels.Length];
        if (reusePaintBuffers) _paintDepth = depth;
        Array.Fill(depth, float.PositiveInfinity);
        var scale = Math.Min(width, height) * .39f * _zoom;
        var sine = MathF.Sin(_yaw); var cosine = MathF.Cos(_yaw);
        var pitchSine = MathF.Sin(_pitch); var pitchCosine = MathF.Cos(_pitch);
        Vector3 Rotate(Vector3 point)
        {
            var x = point.X * cosine - point.Y * sine;
            var y = point.X * sine + point.Y * cosine;
            return new Vector3(x, point.Z * pitchCosine - y * pitchSine, y * pitchCosine + point.Z * pitchSine);
        }
        Vector3 Project(Vector3 point)
        {
            var rotated = Rotate(point);
            return new Vector3(width * .5f + rotated.X * scale, height * .51f - rotated.Y * scale, rotated.Z);
        }
        for (var line = -4; line <= 4; line++)
        {
            var at = line * .4f;
            DrawGridLine(pixels, width, height, Project(new Vector3(-1.6f, at, _ground)), Project(new Vector3(1.6f, at, _ground)));
            DrawGridLine(pixels, width, height, Project(new Vector3(at, -1.6f, _ground)), Project(new Vector3(at, 1.6f, _ground)));
        }
        var projected = reusePaintBuffers && _paintVertices.Length == _pose.Vertices.Count
            ? _paintVertices : new ScreenVertex[_pose.Vertices.Count];
        if (reusePaintBuffers) _paintVertices = projected;
        for (var vertex = 0; vertex < projected.Length; vertex++)
        {
            var point = _pose.Vertices[vertex];
            var position = Project(new Vector3((float)(((double)point.X - _center.X) * _normalization),
                (float)(((double)point.Y - _center.Y) * _normalization), (float)(((double)point.Z - _center.Z) * _normalization)));
            var normal = vertex < _pose.Normals.Count ? _pose.Normals[vertex] : Vector3.UnitZ;
            var lengthSquared = normal.LengthSquared();
            if (lengthSquared > .00000001f) normal /= MathF.Sqrt(lengthSquared);
            var light = .53f + .47f * Math.Max(0, Vector3.Dot(normal, LightDirection));
            var uv = vertex < _mesh.TextureCoordinates.Count ? _mesh.TextureCoordinates[vertex] : Vector2.Zero;
            projected[vertex] = new ScreenVertex(position.X, position.Y, position.Z, uv.X, uv.Y, light);
        }
        var transparent = reusePaintBuffers ? _paintTransparent : new List<(int Triangle, float Depth)>();
        transparent.Clear();
        var renderedTriangles = 0;
        var shadedPixels = 0;
        var alphaPixels = 0;
        var rejectedPixels = 0;
        var indices = _mesh.TriangleIndices;
        for (var triangle = 0; triangle < _triangleTextures.Length; triangle++)
        {
            var texture = _triangleTextures[triangle];
            if (texture is not null && !_opaqueTextures[texture])
            {
                var offset = triangle * 3;
                transparent.Add((triangle, (projected[indices[offset]].Depth + projected[indices[offset + 1]].Depth + projected[indices[offset + 2]].Depth) / 3));
            }
            else RenderTriangle(triangle);
        }
        transparent.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        foreach (var triangle in transparent) RenderTriangle(triangle.Triangle);
        var texturedTriangles = _triangleTextures.Count(texture => texture is not null);
        var playerColorTriangles = _trianglePlayerColors.Count(playerColor => playerColor);
        var statistics = new ModelRenderStatistics(_triangleTextures.Length, renderedTriangles, texturedTriangles,
            _opaqueTextures.Count, playerColorTriangles, _triangleTextures.Length - texturedTriangles - playerColorTriangles,
            shadedPixels, alphaPixels, rejectedPixels);
        return new ModelRenderedFrame(width, height, pixels, statistics);

        void RenderTriangle(int triangle)
        {
            var offset = triangle * 3;
            var a = projected[indices[offset]]; var b = projected[indices[offset + 1]]; var c = projected[indices[offset + 2]];
            var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            if (!float.IsFinite(area) || Math.Abs(area) < .00001f) return;
            var minX = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            if (minX > maxX || minY > maxY) return;
            renderedTriangles++;
            var inverseArea = 1 / area;
            var texture = _triangleTextures[triangle];
            for (var y = minY; y <= maxY; y++)
            {
                var sampleY = y + .5f;
                for (var x = minX; x <= maxX; x++)
                {
                    var sampleX = x + .5f;
                    var wa = Edge(b.X, b.Y, c.X, c.Y, sampleX, sampleY) * inverseArea;
                    var wb = Edge(c.X, c.Y, a.X, a.Y, sampleX, sampleY) * inverseArea;
                    var wc = 1 - wa - wb;
                    if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
                    var z = a.Depth * wa + b.Depth * wb + c.Depth * wc;
                    var pixel = y * width + x;
                    if (z >= depth[pixel] - .000001f) { rejectedPixels++; continue; }
                    var light = a.Light * wa + b.Light * wb + c.Light * wc;
                    var color = _trianglePlayerColors[triangle] ? ReadPixel(_playerColorArgb) :
                        texture is null ? new TextureSample(184, 184, 176, 1) :
                        SampleTexture(texture, (double)a.U * wa + (double)b.U * wb + (double)c.U * wc,
                            (double)a.V * wa + (double)b.V * wb + (double)c.V * wc);
                    if (color.Alpha < .015f) continue;
                    var background = pixels[pixel];
                    var inverseAlpha = 1 - color.Alpha;
                    var r = Math.Clamp((int)(color.Red * light + ((background >> 16) & 255) * inverseAlpha + .5f), 0, 255);
                    var g = Math.Clamp((int)(color.Green * light + ((background >> 8) & 255) * inverseAlpha + .5f), 0, 255);
                    var blue = Math.Clamp((int)(color.Blue * light + (background & 255) * inverseAlpha + .5f), 0, 255);
                    pixels[pixel] = unchecked((int)0xff000000) | r << 16 | g << 8 | blue;
                    if (color.Alpha >= .995f) depth[pixel] = z;
                    else alphaPixels++;
                    shadedPixels++;
                }
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float x, float y) =>
        (bx - ax) * (y - ay) - (by - ay) * (x - ax);

    private static TextureSample SampleTexture(ModelTexture texture, double u, double v)
    {
        // CEM uses Direct3D texture coordinates: v = 0 is the top row.
        if (!double.IsFinite(u) || !double.IsFinite(v)) return default;
        u -= Math.Floor(u); v -= Math.Floor(v);
        var x = u * texture.Width - .5; var y = v * texture.Height - .5;
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var tx = (float)(x - x0); var ty = (float)(y - y0);
        var left = (x0 + texture.Width) % texture.Width; var right = (left + 1) % texture.Width;
        var top = (y0 + texture.Height) % texture.Height; var bottom = (top + 1) % texture.Height;
        var a = ReadPixel(texture.Pixels[top * texture.Width + left]);
        var b = ReadPixel(texture.Pixels[top * texture.Width + right]);
        var c = ReadPixel(texture.Pixels[bottom * texture.Width + left]);
        var d = ReadPixel(texture.Pixels[bottom * texture.Width + right]);
        return TextureSample.Lerp(TextureSample.Lerp(a, b, tx), TextureSample.Lerp(c, d, tx), ty);
    }

    private static TextureSample ReadPixel(int pixel)
    {
        var alpha = ((uint)pixel >> 24) / 255f;
        return new TextureSample(((pixel >> 16) & 255) * alpha, ((pixel >> 8) & 255) * alpha, (pixel & 255) * alpha, alpha);
    }

    /// <summary>Checks rendering behavior numerically; never saves or displays verification pixels.</summary>
    internal static IReadOnlyList<string> VerifyRasterizer()
    {
        var checks = new List<string>();
        var red = new ModelTexture("red", 1, 1, [unchecked((int)0xffff0000)]);
        var blue = new ModelTexture("blue", 1, 1, [unchecked((int)0xff0000ff)]);
        var tiles = new ModelTexture("uv tiles", 2, 2,
            [unchecked((int)0xffff0000), unchecked((int)0xff00ff00), unchecked((int)0xff0000ff), unchecked((int)0xffffffff)]);
        var alpha = new ModelTexture("alpha cutout", 2, 2,
            [0, unchecked((int)0xffff0000), unchecked((int)0xffff0000), 0]);
        var invisible = new ModelTexture("invisible", 1, 1, [0]);
        var appearance = new ModelAppearance(new Dictionary<uint, ModelTexture> { [0] = red, [1] = blue });
        using var viewport = new CivilizationModelViewport();
        viewport.SetModel(CreateRasterFixture(false), appearance);
        var ordered = viewport.RenderFrame(128, 128);
        Require(ordered.Statistics is { SubmittedTriangles: 2, TexturedTriangles: 2, TextureCount: 2, UntexturedTriangles: 0 }
            && ordered.Statistics.ShadedPixels > 100 && ordered.Statistics.DepthRejectedPixels > 10,
            "Overlapping textured triangles must write and reject depth samples.");
        viewport.SetModel(CreateRasterFixture(true), appearance);
        var reversed = viewport.RenderFrame(128, 128);
        Require(ordered.Pixels.SequenceEqual(reversed.Pixels), "Opaque depth rendering must be independent of triangle submission order.");
        checks.Add("Opaque texture sampling and order-independent depth");

        var topLeft = SampleTexture(tiles, .25f, .25f);
        var bottomRight = SampleTexture(tiles, .75f, .75f);
        Require(topLeft.Red > 254 && topLeft.Green < 1 && bottomRight.Green > 254 && bottomRight.Blue > 254,
            "CEM UVs must select the correct top-left-origin texels.");
        Require(SampleTexture(tiles, 4.25f, -6.75f) == topLeft,
            "Game UV coordinates must repeat outside the first tile.");
        var largeUv = SampleTexture(tiles, float.MaxValue, float.MinValue);
        Require(float.IsFinite(largeUv.Red) && float.IsFinite(largeUv.Alpha),
            "Finite large native UV coordinates must remain bounded during sampling.");
        viewport.SetModel(CreateRasterFixture(false), new ModelAppearance(new Dictionary<uint, ModelTexture> { [0] = tiles, [1] = blue }));
        var textured = viewport.RenderFrame(128, 128);
        Require(textured.Pixels.Where((pixel, index) => pixel != ordered.Pixels[index]).Count() > 100,
            "A textured triangle must interpolate UVs across its surface.");
        checks.Add("Top-left UVs, repeat addressing and surface interpolation");

        viewport.SetModel(CreateRasterFixture(false), new ModelAppearance(new Dictionary<uint, ModelTexture> { [0] = alpha, [1] = blue }));
        var cutout = viewport.RenderFrame(128, 128);
        Require(cutout.Statistics.AlphaPixels > 100 && cutout.Pixels.Where((pixel, index) => pixel != ordered.Pixels[index]).Count() > 100,
            "Cutout textures must preserve the surface behind transparent samples.");
        var halfAlpha = SampleTexture(alpha, .5f, .5f);
        Require(Math.Abs(halfAlpha.Alpha - .5f) < .0001f && Math.Abs(halfAlpha.Red - 127.5f) < .001f,
            "Bilinear alpha filtering must interpolate premultiplied color without dark fringes.");
        viewport.SetModel(CreateRasterFixture(false), new ModelAppearance(new Dictionary<uint, ModelTexture> { [0] = invisible, [1] = blue }));
        var holes = viewport.RenderFrame(128, 128);
        Require(holes.Statistics.ShadedPixels > 100 && holes.Statistics.AlphaPixels == 0,
            "Fully transparent samples must neither write color nor conceal the opaque layer.");
        checks.Add("Cutout transparency, alpha blending and transparent depth");

        viewport.SetModel(CreateRasterFixture(false, true), appearance);
        var teamBlue = viewport.RenderFrame(128, 128);
        viewport.PlayerColorArgb = unchecked((int)0xffed3030);
        var teamRed = viewport.RenderFrame(128, 128);
        Require(teamBlue.Statistics is { PlayerColorTriangles: 1, TexturedTriangles: 1, UntexturedTriangles: 0 }
            && teamBlue.Pixels.Where((pixel, index) => pixel != teamRed.Pixels[index]).Count() > 100,
            "Player color must affect only the explicit game material.");
        checks.Add("Explicit game player-color material");

        var boundsRejected = false;
        try { _ = viewport.RenderFrame(1025, 128); }
        catch (ArgumentOutOfRangeException) { boundsRejected = true; }
        Require(boundsRejected && teamRed.Pixels.Length == 128 * 128, "Raster output allocations must remain bounded.");
        viewport.SetModel(null);
        var empty = viewport.RenderFrame(16, 16);
        Require(empty.Pixels.Length == 256 && empty.Statistics.SubmittedTriangles == 0, "An empty scene must render without stale geometry.");
        checks.Add("Bounded pixel allocations and scene reset");
        return checks;

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }

    private static ModelMesh CreateRasterFixture(bool reversed, bool playerColor = false)
    {
        var near = new[] { new Vector3(-1, -.08f, 0), new Vector3(1, -.08f, 0), new Vector3(0, -.08f, 2) };
        var far = new[] { new Vector3(-1, .08f, 0), new Vector3(1, .08f, 0), new Vector3(0, .08f, 2) };
        var points = reversed ? far.Concat(near).ToArray() : near.Concat(far).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(0x464d5353u); writer.Write((ushort)5); writer.Write((ushort)0);
        foreach (var count in new uint[] { 2, 6, 0, 2, 1, 0, 1, 6 }) writer.Write(count);
        WriteString("raster fixture"); WriteVector(Vector3.Zero);
        writer.Write(6u);
        for (var vertex = 0; vertex < 6; vertex++)
        {
            var uv = (vertex % 3) switch { 0 => new Vector2(0, 0), 1 => new Vector2(1, 0), _ => new Vector2(.5f, 1) };
            for (var channel = 0; channel < 8; channel++) { writer.Write(uv.X); writer.Write(uv.Y); }
            writer.Write(0u);
        }
        writer.Write(2u);
        for (ushort index = 0; index < 6; index++) writer.Write(index);
        for (var material = 0; material < 2; material++)
        {
            var front = reversed ? material == 1 : material == 0;
            WriteString(front && playerColor ? "player color" : front ? "front" : "back");
            writer.Write(front ? 0u : 1u);
            writer.Write((uint)material); writer.Write(1u);
            writer.Write((uint)(material * 3)); writer.Write(3u);
            WriteString(front ? "red" : "blue");
        }
        writer.Write(3f);
        writer.Write((ushort)0); writer.Write((ushort)0);
        for (ushort vertex = 0; vertex < 6; vertex++) { writer.Write(vertex); writer.Write((ushort)0); }
        foreach (var value in new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }) writer.Write(value);
        writer.Write((byte)0);
        WriteVector(new Vector3(-1, -.08f, 0)); WriteVector(new Vector3(1, .08f, 2));
        for (var vertex = 0; vertex < 6; vertex++) writer.Write(0u);
        foreach (var point in points) WriteVector(point);
        writer.Write(0u);
        writer.Flush();
        return CivilizationModelReader.Read(stream.ToArray(), "raster-fixture.cem");

        void WriteString(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + '\0'); writer.Write((uint)bytes.Length); writer.Write(bytes);
        }
        void WriteVector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    }

    private static void FillBackground(int[] pixels, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var fraction = y / (float)Math.Max(1, height - 1);
            var r = (int)(24 - 14 * fraction); var g = (int)(29 - 15 * fraction); var b = (int)(31 - 15 * fraction);
            Array.Fill(pixels, unchecked((int)0xff000000) | r << 16 | g << 8 | b, y * width, width);
        }
    }

    private static void DrawGridLine(int[] pixels, int width, int height, Vector3 a, Vector3 b)
    {
        var steps = Math.Min(4096, (int)MathF.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        if (steps < 1) return;
        for (var step = 0; step <= steps; step++)
        {
            var amount = step / (float)steps;
            var x = (int)MathF.Round(a.X + (b.X - a.X) * amount); var y = (int)MathF.Round(a.Y + (b.Y - a.Y) * amount);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            var index = y * width + x;
            var pixel = pixels[index];
            var r = ((pixel >> 16) & 255) + 11; var g = ((pixel >> 8) & 255) + 8; var blue = (pixel & 255) + 2;
            pixels[index] = unchecked((int)0xff000000) | Math.Min(255, r) << 16 | Math.Min(255, g) << 8 | Math.Min(255, blue);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus(); _drag = e.Location; Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not Point previous) return;
        _yaw += (e.X - previous.X) * .014f;
        _pitch = Math.Clamp(_pitch + (e.Y - previous.Y) * .014f, -.25f, 1.4f);
        _drag = e.Location;
        MarkDirty();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = null; Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _drag = null; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12f : .89f), .3f, 4); MarkDirty(); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); ResetView(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: _yaw -= .12f; break;
            case Keys.Right: _yaw += .12f; break;
            case Keys.Up: _pitch = Math.Clamp(_pitch + .12f, -.25f, 1.4f); break;
            case Keys.Down: _pitch = Math.Clamp(_pitch - .12f, -.25f, 1.4f); break;
            case Keys.Add: case Keys.Oemplus: _zoom = Math.Min(4, _zoom * 1.12f); break;
            case Keys.Subtract: case Keys.OemMinus: _zoom = Math.Max(.3f, _zoom * .89f); break;
            default: return;
        }
        e.Handled = true;
        MarkDirty();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }

    private readonly record struct ScreenVertex(float X, float Y, float Depth, float U, float V, float Light);
    private readonly record struct TextureSample(float Red, float Green, float Blue, float Alpha)
    {
        public static TextureSample Lerp(TextureSample a, TextureSample b, float amount) => new(
            a.Red + (b.Red - a.Red) * amount, a.Green + (b.Green - a.Green) * amount,
            a.Blue + (b.Blue - a.Blue) * amount, a.Alpha + (b.Alpha - a.Alpha) * amount);
    }
}

internal readonly record struct ModelRenderStatistics(int SubmittedTriangles, int RasterizedTriangles,
    int TexturedTriangles, int TextureCount, int PlayerColorTriangles, int UntexturedTriangles,
    int ShadedPixels, int AlphaPixels, int DepthRejectedPixels);

internal sealed record ModelRenderedFrame(int Width, int Height, int[] Pixels, ModelRenderStatistics Statistics);
