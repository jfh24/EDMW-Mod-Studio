using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

/// <summary>A material's ranges in the highest detail, flattened scene.</summary>
public sealed record ModelMaterial(string Name, uint TextureId, string TextureName,
    int TriangleStart, int TriangleCount, int VertexStart, int VertexCount, string PartName);

public sealed record ModelTagPoint(string Name, Vector3 Position, string PartName);

/// <summary>One posed scene, using the mesh's unchanged triangle and UV arrays.</summary>
public sealed class ModelPose
{
    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<Vector3> Normals { get; }
    public IReadOnlyList<ModelTagPoint> TagPoints { get; }

    internal ModelPose(Vector3[] vertices, Vector3[] normals, ModelTagPoint[] tagPoints)
    {
        Vertices = Array.AsReadOnly(vertices);
        Normals = Array.AsReadOnly(normals);
        TagPoints = Array.AsReadOnly(tagPoints);
    }
}

/// <summary>Validated geometry, materials and every native animation frame.</summary>
public sealed class ModelMesh
{
    private readonly CemModelScene? scene;
    private readonly ModelPose firstPose;
    public string Name { get; }
    public IReadOnlyList<Vector3> Vertices => firstPose.Vertices;
    public IReadOnlyList<Vector3> Normals => firstPose.Normals;
    public IReadOnlyList<int> TriangleIndices { get; }
    public IReadOnlyList<Vector2> TextureCoordinates { get; }
    public IReadOnlyList<IReadOnlyList<Vector2>> TextureCoordinateChannels { get; }
    public IReadOnlyList<ModelMaterial> Materials { get; }
    public IReadOnlyList<ModelTagPoint> TagPoints => firstPose.TagPoints;
    public Vector3 BoundsMinimum { get; }
    public Vector3 BoundsMaximum { get; }
    public string Format { get; }
    public int FrameCount { get; }
    public int PartCount { get; }
    public int AdditionalDataBytes { get; }
    public string? PreviewNote { get; }

    internal ModelMesh(string name, List<Vector3> vertices, List<int> triangles,
        List<Vector2> textureCoordinates, string format, int frameCount, int partCount,
        int additionalDataBytes = 0, List<ModelMaterial>? materials = null,
        Vector3[]? normals = null)
    {
        Name = name;
        TriangleIndices = triangles.AsReadOnly();
        TextureCoordinates = textureCoordinates.AsReadOnly();
        TextureCoordinateChannels = Array.AsReadOnly<IReadOnlyList<Vector2>>([TextureCoordinates]);
        Materials = (materials ?? []).AsReadOnly();
        var positions = vertices.ToArray();
        firstPose = new ModelPose(positions, normals ?? CemModelScene.BuildNormals(positions,
            TriangleIndices, null), []);
        Format = format;
        FrameCount = frameCount;
        PartCount = partCount;
        AdditionalDataBytes = additionalDataBytes;
        PreviewNote = additionalDataBytes > 0
            ? "Additional model sections are retained but are not displayed in this preview."
            : null;
        BoundsMinimum = new Vector3(float.PositiveInfinity);
        BoundsMaximum = new Vector3(float.NegativeInfinity);
        foreach (var point in positions)
        {
            BoundsMinimum = Vector3.Min(BoundsMinimum, point);
            BoundsMaximum = Vector3.Max(BoundsMaximum, point);
        }
    }

    internal ModelMesh(string name, CemModelScene scene, int additionalDataBytes)
    {
        Name = name;
        this.scene = scene;
        TriangleIndices = scene.Triangles.AsReadOnly();
        TextureCoordinateChannels = Array.AsReadOnly(scene.TextureCoordinates
            .Select(channel => (IReadOnlyList<Vector2>)channel.AsReadOnly()).ToArray());
        TextureCoordinates = TextureCoordinateChannels[0];
        Materials = scene.Materials.AsReadOnly();
        firstPose = scene.GetPose(0);
        (BoundsMinimum, BoundsMaximum) = scene.CalculateBounds();
        Format = "CEM5";
        FrameCount = scene.Parts.Max(part => part.Frames.Length);
        PartCount = scene.Parts.Count;
        AdditionalDataBytes = additionalDataBytes;
        PreviewNote = additionalDataBytes > 0
            ? "Additional model sections are retained but are not displayed in this preview."
            : null;
    }

    /// <summary>
    /// Samples a zero-based native frame, interpolating positions and transforms.
    /// Static child parts keep their one pose. Values beyond a part's last frame
    /// clamp; the caller chooses an animation clip and its looping interval.
    /// </summary>
    public ModelPose GetPose(float framePosition)
    {
        if (!float.IsFinite(framePosition))
            throw new ArgumentOutOfRangeException(nameof(framePosition));
        return scene is null || framePosition == 0 ? firstPose : scene.GetPose(framePosition);
    }
}

/// <summary>A bounded read-only reader; no native model bytes are rewritten.</summary>
public static class CivilizationModelReader
{
    public const int MaximumModelBytes = 64 * 1024 * 1024;
    private const uint CemMagic = 0x464D5353;
    private const int MaximumParts = 1024;
    private const int MaximumVertices = 1_000_000;
    private const int MaximumTriangleIndices = 6_000_000;

    public static ModelMesh Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumModelBytes)
            throw new InvalidDataException("The model exceeds the 64 MiB preview limit.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return Read(data, Path.GetFileName(path));
    }

    public static ModelMesh Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (data.Length > MaximumModelBytes)
            throw new InvalidDataException("The model exceeds the 64 MiB preview limit.");
        if (Path.GetExtension(name).Equals(".obj", StringComparison.OrdinalIgnoreCase))
            return ReadObj(data, name);
        // A project can retain its byte array. A scene owns its bounded source so
        // later mutations cannot bypass the frame validation below.
        var reader = new MeshReader((byte[])data.Clone());
        reader.ReadPart(-1, 0);
        if (reader.Scene.VertexCount == 0 || reader.Scene.Triangles.Count == 0)
            throw new InvalidDataException("The CEM scene contains no visible triangle geometry.");
        reader.Scene.ValidateFramePositions();
        // Some shipped walls append engine-specific sections after the complete
        // declared scene. Preserve the source; display its validated scene.
        return new ModelMesh(name, reader.Scene, reader.Remaining);
    }

    private static ModelMesh ReadObj(byte[] data, string name)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("The OBJ model must contain valid UTF-8 text.", exception); }
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var suppliedNormals = new List<Vector3>();
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var coordinates = new List<Vector2>();
        var triangles = new List<int>();
        var materials = new List<ModelMaterial>();
        var mapped = new Dictionary<(int Position, int Texture, int Normal), int>();
        var materialName = "";
        var materialStart = 0;
        var lineNumber = 0;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length > 32_768)
                throw new InvalidDataException($"OBJ line {lineNumber} exceeds the preview length limit.");
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields[0] == "v")
            {
                if (fields.Length < 4 || positions.Count >= MaximumVertices)
                    throw new InvalidDataException($"OBJ line {lineNumber} has an invalid vertex.");
                var point = new Vector3(ParseObjFloat(fields[1], lineNumber),
                    ParseObjFloat(fields[2], lineNumber), ParseObjFloat(fields[3], lineNumber));
                if (fields.Length == 5)
                {
                    var weight = ParseObjFloat(fields[4], lineNumber);
                    if (weight == 0)
                        throw new InvalidDataException($"OBJ line {lineNumber} has a zero vertex weight.");
                    point /= weight;
                    if (!CemModelScene.IsFinite(point))
                        throw new InvalidDataException($"OBJ line {lineNumber} produces an invalid vertex.");
                }
                positions.Add(point);
            }
            else if (fields[0] == "vt")
            {
                if (fields.Length < 3 || uvs.Count >= MaximumVertices)
                    throw new InvalidDataException($"OBJ line {lineNumber} has invalid texture coordinates.");
                uvs.Add(new Vector2(ParseObjFloat(fields[1], lineNumber),
                    ParseObjFloat(fields[2], lineNumber)));
            }
            else if (fields[0] == "vn")
            {
                if (fields.Length < 4 || suppliedNormals.Count >= MaximumVertices)
                    throw new InvalidDataException($"OBJ line {lineNumber} has an invalid normal.");
                var normal = new Vector3(ParseObjFloat(fields[1], lineNumber),
                    ParseObjFloat(fields[2], lineNumber), ParseObjFloat(fields[3], lineNumber));
                suppliedNormals.Add(CemModelScene.Normalize(normal));
            }
            else if (fields[0] == "usemtl")
            {
                AddMaterial();
                materialName = string.Join(' ', fields.Skip(1));
                materialStart = triangles.Count / 3;
            }
            else if (fields[0] == "f")
            {
                var count = fields.Length - 1;
                if (count < 3 || count > 1024 ||
                    (long)triangles.Count + (count - 2L) * 3 > MaximumTriangleIndices)
                    throw new InvalidDataException($"OBJ line {lineNumber} has an unsupported face size.");
                var face = new int[count];
                for (var index = 0; index < face.Length; index++)
                {
                    var components = fields[index + 1].Split('/');
                    if (components.Length > 3)
                        throw new InvalidDataException($"OBJ line {lineNumber} has an invalid face index.");
                    var source = ParseObjIndex(components[0], positions.Count, lineNumber);
                    var texture = components.Length > 1 && components[1].Length > 0
                        ? ParseObjIndex(components[1], uvs.Count, lineNumber) : -1;
                    var normal = components.Length > 2 && components[2].Length > 0
                        ? ParseObjIndex(components[2], suppliedNormals.Count, lineNumber) : -1;
                    var key = (source, texture, normal);
                    if (!mapped.TryGetValue(key, out var target))
                    {
                        if (vertices.Count >= MaximumVertices)
                            throw new InvalidDataException("The OBJ scene exceeds the preview vertex limit.");
                        target = vertices.Count;
                        mapped.Add(key, target);
                        vertices.Add(positions[source]);
                        coordinates.Add(texture < 0 ? Vector2.Zero : uvs[texture]);
                        normals.Add(normal < 0 ? Vector3.Zero : suppliedNormals[normal]);
                    }
                    face[index] = target;
                }
                for (var index = 1; index < face.Length - 1; index++)
                {
                    triangles.Add(face[0]); triangles.Add(face[index]); triangles.Add(face[index + 1]);
                }
            }
        }
        AddMaterial();
        if (vertices.Count == 0 || triangles.Count == 0)
            throw new InvalidDataException("The OBJ model contains no visible triangle geometry.");
        var computed = CemModelScene.BuildNormals(vertices.ToArray(), triangles, null);
        for (var index = 0; index < computed.Length; index++)
            if (normals[index] != Vector3.Zero) computed[index] = normals[index];
        return new ModelMesh(name, vertices, triangles, coordinates, "OBJ", 1, 1,
            materials: materials, normals: computed);

        void AddMaterial()
        {
            var count = triangles.Count / 3 - materialStart;
            if (count > 0) materials.Add(new ModelMaterial(materialName, 0, "",
                materialStart, count, 0, vertices.Count, "Scene Root"));
        }
    }

    private static int ParseObjIndex(string text, int count, int lineNumber)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
            out var index) || index == 0)
            throw new InvalidDataException($"OBJ line {lineNumber} has an invalid face index.");
        var resolved = index > 0 ? (long)index - 1 : (long)count + index;
        if (resolved < 0 || resolved >= count)
            throw new InvalidDataException($"OBJ line {lineNumber} references a missing vertex, UV or normal.");
        return (int)resolved;
    }

    private static float ParseObjFloat(string text, int lineNumber)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value) || Math.Abs(value) > 1_000_000_000f)
            throw new InvalidDataException($"OBJ line {lineNumber} contains an invalid coordinate.");
        return value;
    }

    private sealed class MeshReader
    {
        private readonly byte[] data;
        private int position;
        public int Remaining => data.Length - position;
        public CemModelScene Scene { get; }

        public MeshReader(byte[] data)
        {
            this.data = data;
            Scene = new CemModelScene(data);
        }

        public void ReadPart(int parentIndex, int depth)
        {
            if (depth > 128 || Scene.Parts.Count >= MaximumParts)
                throw new InvalidDataException("The CEM scene has too many nested model parts.");
            if (ReadUInt32() != CemMagic || ReadUInt16() != 5 || ReadUInt16() != 0)
                throw new InvalidDataException("This preview supports SSMF CEM version 5.0 models.");
            var triangleCount = ReadCount("triangles", 200_000);
            var vertexCount = ReadCount("vertices", 65_536);
            var tagCount = ReadCount("tag points", 4096);
            var materialCount = ReadCount("materials", 2048);
            var frameCount = ReadCount("animation frames", 100_000);
            var childCount = ReadCount("child parts", MaximumParts);
            var lodCount = ReadCount("levels of detail", 32);
            var pointCount = ReadCount("position points", 65_536);
            if (frameCount == 0 || lodCount == 0)
                throw new InvalidDataException("The CEM part is missing its frame or detail-level data.");
            var name = ReadString();
            _ = ReadVector3(); // Bounding center, not a model translation.
            if (ReadCount("common vertices", 65_536) != vertexCount)
                throw new InvalidDataException("The CEM common-vertex count does not match its header.");
            if ((long)Scene.VertexCount + vertexCount > MaximumVertices ||
                (long)Scene.Triangles.Count + triangleCount * 3L > MaximumTriangleIndices)
                throw new InvalidDataException("The scene exceeds the preview geometry limit.");
            var vertexOffset = Scene.VertexCount;
            var triangleOffset = Scene.Triangles.Count / 3;
            RequireBytes(vertexCount * 68L);
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                for (var channel = 0; channel < 8; channel++)
                    Scene.TextureCoordinates[channel].Add(new Vector2(ReadFloat(), ReadFloat()));
                _ = ReadUInt32(); // Common-vertex engine metadata.
            }
            var lodTriangleCounts = new int[lodCount];
            for (var lod = 0; lod < lodCount; lod++)
            {
                var count = ReadCount("detail-level triangles", 200_000);
                lodTriangleCounts[lod] = count;
                if (lod == 0 && count != triangleCount)
                    throw new InvalidDataException("The CEM triangle count does not match its first detail level.");
                RequireBytes(count * 6L);
                for (var index = 0; index < count * 3; index++)
                {
                    var value = ReadUInt16();
                    if (value >= vertexCount)
                        throw new InvalidDataException("A CEM triangle references a missing vertex.");
                    if (lod == 0) Scene.Triangles.Add(vertexOffset + value);
                }
            }
            for (var material = 0; material < materialCount; material++)
            {
                var materialName = ReadString();
                var textureId = ReadUInt32();
                var firstStart = 0;
                var firstCount = 0;
                for (var lod = 0; lod < lodCount; lod++)
                {
                    var start = ReadUInt32();
                    var count = ReadUInt32();
                    if ((ulong)start + count > (ulong)lodTriangleCounts[lod])
                        throw new InvalidDataException("A CEM material references a missing triangle range.");
                    if (lod == 0) { firstStart = (int)start; firstCount = (int)count; }
                }
                var startVertex = ReadUInt32();
                var countVertex = ReadUInt32();
                if ((ulong)startVertex + countVertex > (ulong)vertexCount)
                    throw new InvalidDataException("A CEM material references a missing vertex range.");
                Scene.Materials.Add(new ModelMaterial(materialName, textureId, ReadString(),
                    triangleOffset + firstStart, firstCount, vertexOffset + (int)startVertex,
                    (int)countVertex, name));
            }
            var tagNames = new string[tagCount];
            for (var tag = 0; tag < tagCount; tag++) tagNames[tag] = ReadString();
            var frameBytes = 4L + triangleCount * 2L + vertexCount * 4L + tagCount * 12L +
                64 + 1 + 24 + vertexCount * 4L;
            RequireBytes(frameBytes * frameCount + pointCount * 12L + 4);
            var frames = new CemModelFrame[frameCount];
            for (var frame = 0; frame < frameCount; frame++)
            {
                var radius = ReadFloat();
                if (radius < 0) throw new InvalidDataException("The CEM bounding radius is invalid.");
                Skip(triangleCount * 2L); // Packed face-normal table indices.
                var verticesPosition = position;
                for (var vertex = 0; vertex < vertexCount; vertex++)
                {
                    if (ReadUInt16() >= pointCount)
                        throw new InvalidDataException("A CEM frame references a missing position point.");
                    _ = ReadUInt16(); // Packed authored-normal table index, retained in source.
                }
                var tagsPosition = position;
                for (var tag = 0; tag < tagCount; tag++) _ = ReadVector3();
                // CEM matrices store translations in the last column. Numerics
                // uses row vectors, so transposition is required for child parts.
                var transform = Matrix4x4.Transpose(ReadMatrix());
                Skip(1); // Engine frame flag.
                var minimum = ReadVector3();
                var maximum = ReadVector3();
                if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
                    throw new InvalidDataException("The CEM frame bounds are invalid.");
                Skip(vertexCount * 4L); // Packed tangent/bump data.
                frames[frame] = new CemModelFrame(verticesPosition, tagsPosition, transform,
                    minimum, maximum);
            }
            var points = new Vector3[pointCount];
            for (var point = 0; point < pointCount; point++) points[point] = ReadVector3();
            var shadowCount = ReadCount("shadow edges", 600_000);
            Skip(shadowCount * 12L);
            var partIndex = Scene.Parts.Count;
            Scene.Parts.Add(new CemModelPart(name, parentIndex, vertexOffset, vertexCount,
                triangleOffset, triangleCount, points, frames, tagNames));
            Scene.VertexCount += vertexCount;
            for (var child = 0; child < childCount; child++) ReadPart(partIndex, depth + 1);
        }

        private int ReadCount(string label, int maximum)
        {
            var count = ReadUInt32();
            if (count > maximum)
                throw new InvalidDataException($"The CEM {label} count exceeds the preview limit.");
            return (int)count;
        }

        private string ReadString()
        {
            var length = ReadCount("string bytes", 4096);
            RequireBytes(length);
            var text = Encoding.ASCII.GetString(data, position, length).TrimEnd('\0');
            position += length;
            return text;
        }

        private Vector3 ReadVector3()
        {
            var value = new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
            if (Math.Abs(value.X) > 1_000_000_000f || Math.Abs(value.Y) > 1_000_000_000f ||
                Math.Abs(value.Z) > 1_000_000_000f)
                throw new InvalidDataException("The CEM geometry contains an excessive position.");
            return value;
        }
        private Matrix4x4 ReadMatrix() => new(
            ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(),
            ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(),
            ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(),
            ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat(), ReadTransformFloat());

        private float ReadTransformFloat()
        {
            var value = ReadFloat();
            if (Math.Abs(value) > 1_000_000_000f)
                throw new InvalidDataException("The CEM geometry contains an excessive transform.");
            return value;
        }

        private float ReadFloat()
        {
            var value = BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32()));
            if (!float.IsFinite(value))
                throw new InvalidDataException($"The CEM geometry contains a non-finite value at byte {position - 4}.");
            return value;
        }

        private uint ReadUInt32()
        {
            RequireBytes(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
            position += 4;
            return value;
        }

        private ushort ReadUInt16()
        {
            RequireBytes(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
            position += 2;
            return value;
        }

        private void Skip(long bytes) { RequireBytes(bytes); position += (int)bytes; }
        private void RequireBytes(long bytes)
        {
            if (bytes < 0 || bytes > Remaining)
                throw new InvalidDataException("The CEM model is truncated or uses an unsupported layout.");
        }
    }
}

internal readonly record struct CemModelFrame(int VertexPosition, int TagPosition,
    Matrix4x4 Transform, Vector3 Minimum, Vector3 Maximum);

internal sealed record CemModelPart(string Name, int ParentIndex, int VertexOffset,
    int VertexCount, int TriangleOffset, int TriangleCount, Vector3[] Points,
    CemModelFrame[] Frames, string[] TagNames);

/// <summary>Compressed frame mappings remain in their bounded native byte buffer.</summary>
internal sealed class CemModelScene(byte[] data)
{
    public List<CemModelPart> Parts { get; } = [];
    public List<int> Triangles { get; } = [];
    public List<Vector2>[] TextureCoordinates { get; } = Enumerable.Range(0, 8)
        .Select(_ => new List<Vector2>()).ToArray();
    public List<ModelMaterial> Materials { get; } = [];
    public int VertexCount { get; set; }

    public ModelPose GetPose(float position)
    {
        var vertices = new Vector3[VertexCount];
        var groups = new (int Part, int Point, ushort Normal)[VertexCount];
        var tags = new List<ModelTagPoint>();
        var transforms = new Matrix4x4[Parts.Count];
        for (var partIndex = 0; partIndex < Parts.Count; partIndex++)
        {
            var part = Parts[partIndex];
            var sample = Math.Clamp(position, 0, part.Frames.Length - 1);
            var index = (int)sample;
            var next = Math.Min(index + 1, part.Frames.Length - 1);
            var fraction = sample - index;
            var frame = part.Frames[index];
            var nextFrame = part.Frames[next];
            var transform = InterpolateTransform(frame.Transform, nextFrame.Transform, fraction);
            if (part.ParentIndex >= 0) transform *= transforms[part.ParentIndex];
            transforms[partIndex] = transform;
            for (var vertex = 0; vertex < part.VertexCount; vertex++)
            {
                var pointIndex = ReadPointIndex(frame.VertexPosition + vertex * 4);
                var point = part.Points[pointIndex];
                if (fraction > 0)
                    point = Vector3.Lerp(point, part.Points[ReadPointIndex(
                        nextFrame.VertexPosition + vertex * 4)], fraction);
                var transformed = Vector3.Transform(point, transform);
                if (!IsFinite(transformed))
                    throw new InvalidDataException("The CEM transform produces an invalid position.");
                vertices[part.VertexOffset + vertex] = transformed;
                groups[part.VertexOffset + vertex] = (partIndex, pointIndex,
                    ReadPointIndex(frame.VertexPosition + vertex * 4 + 2));
            }
            for (var tag = 0; tag < part.TagNames.Length; tag++)
            {
                var point = ReadVector3(frame.TagPosition + tag * 12);
                if (fraction > 0) point = Vector3.Lerp(point,
                    ReadVector3(nextFrame.TagPosition + tag * 12), fraction);
                point = Vector3.Transform(point, transform);
                if (!IsFinite(point))
                    throw new InvalidDataException("The CEM transform produces an invalid tag position.");
                tags.Add(new ModelTagPoint(part.TagNames[tag], point, part.Name));
            }
        }
        return new ModelPose(vertices, BuildNormals(vertices, Triangles, groups), tags.ToArray());
    }

    public (Vector3 Minimum, Vector3 Maximum) CalculateBounds()
    {
        // Union each subtree in its parent's coordinate system. This also covers
        // static child parts carried by animated ancestors, without expanding
        // every possible ancestor/descendant frame combination.
        var minima = Enumerable.Repeat(new Vector3(float.PositiveInfinity), Parts.Count).ToArray();
        var maxima = Enumerable.Repeat(new Vector3(float.NegativeInfinity), Parts.Count).ToArray();
        var resultMinimum = new Vector3(float.PositiveInfinity);
        var resultMaximum = new Vector3(float.NegativeInfinity);
        for (var partIndex = Parts.Count - 1; partIndex >= 0; partIndex--)
        {
            var part = Parts[partIndex];
            foreach (var frame in part.Frames)
            {
                minima[partIndex] = Vector3.Min(minima[partIndex], frame.Minimum);
                maxima[partIndex] = Vector3.Max(maxima[partIndex], frame.Maximum);
            }
            foreach (var frame in part.Frames)
                for (var corner = 0; corner < 8; corner++)
                {
                    var minimum = minima[partIndex];
                    var maximum = maxima[partIndex];
                    var point = new Vector3((corner & 1) == 0 ? minimum.X : maximum.X,
                        (corner & 2) == 0 ? minimum.Y : maximum.Y,
                        (corner & 4) == 0 ? minimum.Z : maximum.Z);
                    point = Vector3.Transform(point, frame.Transform);
                    if (!IsSafePosition(point))
                        throw new InvalidDataException("The CEM transform produces invalid scene bounds.");
                    if (part.ParentIndex >= 0)
                    {
                        minima[part.ParentIndex] = Vector3.Min(minima[part.ParentIndex], point);
                        maxima[part.ParentIndex] = Vector3.Max(maxima[part.ParentIndex], point);
                    }
                    else
                    {
                        resultMinimum = Vector3.Min(resultMinimum, point);
                        resultMaximum = Vector3.Max(resultMaximum, point);
                    }
                }
        }
        return (resultMinimum, resultMaximum);
    }

    public void ValidateFramePositions()
    {
        for (var partIndex = 0; partIndex < Parts.Count; partIndex++)
        {
            var part = Parts[partIndex];
            for (var frameIndex = 0; frameIndex < part.Frames.Length; frameIndex++)
            {
                var frame = part.Frames[frameIndex];
                var transform = GetTransform(partIndex, frameIndex);
                for (var vertex = 0; vertex < part.VertexCount; vertex++)
                    if (!IsSafePosition(Vector3.Transform(part.Points[ReadPointIndex(
                        frame.VertexPosition + vertex * 4)], transform)))
                        throw new InvalidDataException("The CEM transform produces an invalid position.");
                for (var tag = 0; tag < part.TagNames.Length; tag++)
                    if (!IsSafePosition(Vector3.Transform(ReadVector3(frame.TagPosition + tag * 12), transform)))
                        throw new InvalidDataException("The CEM transform produces an invalid tag position.");
            }
        }
    }

    private Matrix4x4 GetTransform(int partIndex, int frame)
    {
        var result = Matrix4x4.Identity;
        for (var index = partIndex; index >= 0; index = Parts[index].ParentIndex)
            result *= Parts[index].Frames[Math.Min(frame, Parts[index].Frames.Length - 1)].Transform;
        return result;
    }

    private ushort ReadPointIndex(int position) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
    private Vector3 ReadVector3(int position) => new(ReadFloat(position), ReadFloat(position + 4),
        ReadFloat(position + 8));
    private float ReadFloat(int position) => BitConverter.Int32BitsToSingle(
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, 4)));

    private static Matrix4x4 InterpolateTransform(Matrix4x4 first, Matrix4x4 second, float fraction)
    {
        if (fraction <= 0 || first == second) return first;
        if (Matrix4x4.Decompose(first, out var scale1, out var rotation1, out var translation1) &&
            Matrix4x4.Decompose(second, out var scale2, out var rotation2, out var translation2))
            return Matrix4x4.CreateScale(Vector3.Lerp(scale1, scale2, fraction)) *
                Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(rotation1, rotation2, fraction)) *
                Matrix4x4.CreateTranslation(Vector3.Lerp(translation1, translation2, fraction));
        return Matrix4x4.Lerp(first, second, fraction);
    }

    internal static Vector3[] BuildNormals(Vector3[] vertices, IReadOnlyList<int> triangles,
        (int Part, int Point, ushort Normal)[]? groups)
    {
        var sums = new Vector3[vertices.Length];
        for (var triangle = 0; triangle < triangles.Count; triangle += 3)
        {
            var a = triangles[triangle]; var b = triangles[triangle + 1]; var c = triangles[triangle + 2];
            // Normalize edges first so large model coordinates do not overflow
            // a cross product used only for lighting.
            var normal = Vector3.Cross(Normalize(vertices[b] - vertices[a]),
                Normalize(vertices[c] - vertices[a]));
            if (!IsFinite(normal)) continue;
            sums[a] += normal; sums[b] += normal; sums[c] += normal;
        }
        if (groups is not null)
        {
            // The native normal table is engine-owned. Preserve its indices and
            // reconstruct smooth normals across matching authored normal/point
            // groups, including duplicated vertices at UV seams.
            var grouped = new Dictionary<(int Part, int Point, ushort Normal), Vector3>();
            for (var index = 0; index < sums.Length; index++)
                grouped[groups[index]] = grouped.GetValueOrDefault(groups[index]) + sums[index];
            for (var index = 0; index < sums.Length; index++) sums[index] = grouped[groups[index]];
        }
        for (var index = 0; index < sums.Length; index++) sums[index] = Normalize(sums[index]);
        return sums;
    }

    internal static Vector3 Normalize(Vector3 value)
    {
        var largest = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        if (!float.IsFinite(largest) || largest < 1e-20f) return Vector3.Zero;
        value /= largest;
        return Vector3.Normalize(value);
    }

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsSafePosition(Vector3 value) => IsFinite(value) &&
        Math.Abs(value.X) <= 1_000_000_000f && Math.Abs(value.Y) <= 1_000_000_000f &&
        Math.Abs(value.Z) <= 1_000_000_000f;
}
