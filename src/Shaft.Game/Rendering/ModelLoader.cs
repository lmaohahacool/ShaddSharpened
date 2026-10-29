using System.Numerics;
using System.Text.Json;
using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Game.Rendering;

/// <summary>
/// A static box mesh: 24 vertices laid out <c>pos(3) normal(3) uv(2)</c> and 36 indices —
/// the exact shape three.js's single-segment <c>BoxGeometry</c> produces.
/// </summary>
public sealed class ModelBox
{
    public const int FLOATS_PER_VERTEX = 8;

    public float[] Vertices = Array.Empty<float>();
    public ushort[] Indices = Array.Empty<ushort>();

    public static ModelBox Empty { get; } = new();
    public bool IsEmpty => Vertices.Length == 0;
}

/// <summary>One animated limb of a model: a transform plus the box it carries.</summary>
public sealed class ModelPart
{
    public string Id = "";
    /// <summary><c>partGroup.position</c> in blocks.</summary>
    public Vector3 PartPosition;
    /// <summary><c>partGroup.rotation</c> from the JSON, in radians (Euler XYZ).</summary>
    public Vector3 BaseRotation;
    /// <summary><c>mesh.position</c> in blocks.</summary>
    public Vector3 MeshPosition;
    public ModelBox Box = ModelBox.Empty;
    /// <summary>True when <c>part.id</c> contains "leg" — the walk cycle overwrites rotation.x.</summary>
    public bool IsLeg;
    /// <summary>Order within the leg list, which decides the swing sign.</summary>
    public int LegIndex;
    public bool IsHead;
}

/// <summary>A parsed <c>dist/models/*.json</c> plus the transform rules from <c>buildModelNew</c>.</summary>
public sealed class LoadedModel
{
    public string Name = "";
    public string TexturePath = "";
    public int TextureWidth = 64;
    public int TextureHeight = 64;
    public List<ModelPart> Parts { get; } = new();
    public int LegCount { get; private set; }

    /// <summary>
    /// <c>partGroup.matrix * mesh.matrix</c> for one part, given the leg swing the walk
    /// cycle wrote into <c>rotation.x</c>. Mirrors three.js's Object3D compose order
    /// (translation, then rotation) translated into row-vector convention.
    /// </summary>
    public Matrix4x4 PartMatrix(ModelPart part, float legRotationX)
    {
        Vector3 rot = part.BaseRotation;
        if (part.IsLeg) rot.X = legRotationX;
        // column-major  T * R  ==  row-major  R * T
        return EulerXyz(rot.X, rot.Y, rot.Z) * Matrix4x4.CreateTranslation(part.PartPosition);
    }

    public Matrix4x4 BoxMatrix(ModelPart part) => Matrix4x4.CreateTranslation(part.MeshPosition);

    internal void FinaliseLegs()
    {
        LegCount = 0;
        foreach (ModelPart p in Parts)
            if (p.IsLeg) p.LegIndex = LegCount++;
    }

    /// <summary>
    /// three.js <c>Matrix4.makeRotationFromEuler</c> for order XYZ, converted to the
    /// row-vector convention System.Numerics uses.
    /// </summary>
    public static Matrix4x4 EulerXyz(float x, float y, float z)
    {
        float ae = MathF.Cos(x), af = MathF.Sin(x);
        float be = MathF.Cos(y), bf = MathF.Sin(y);
        float ce = MathF.Cos(z), cf = MathF.Sin(z);

        float ac = ae * ce, ad = ae * cf, bc = af * ce, bd = af * cf;

        // te[] is three.js's column-major storage; System.Numerics wants the transpose,
        // which for row-major field order means reading te straight across.
        var m = Matrix4x4.Identity;
        m.M11 = be * ce; m.M12 = -be * cf; m.M13 = bf;
        m.M21 = ad + bc * bf; m.M22 = ac - bd * bf; m.M23 = -af * be;
        m.M31 = bd - ac * bf; m.M32 = bc + ad * bf; m.M33 = ae * be;
        m.M44 = 1f;
        return m;
    }
}

/// <summary>
/// Ports <c>buildBoxMesh</c> / <c>buildModelNew</c> from <c>dist/mobs.js</c>, including the
/// <c>BoxGeometry</c> vertex generation they depend on. Kept deliberately literal: the UV
/// region table only lines up with the skins if the plane order, vertex order and index
/// order all match three.js exactly.
/// </summary>
public static class ModelLoader
{
    private const float PIXELS_PER_BLOCK = 16f;
    public static LoadedModel Load(string modelPath, string texturePath, List<string> warnings)
    {
        var model = new LoadedModel { TexturePath = texturePath };
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(modelPath));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                model.Name = nameEl.GetString() ?? Path.GetFileNameWithoutExtension(modelPath);
            else
                model.Name = Path.GetFileNameWithoutExtension(modelPath);

            if (TryGetPair(root, "textureSize", out float tw, out float th))
            {
                model.TextureWidth = (int)tw;
                model.TextureHeight = (int)th;
            }

            if (root.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                    AddPart(model, part);
            }
            else
            {
                // Legacy "models"/"coordinates" format — convertToNewFormat() in mobs.js.
                AddLegacyParts(model, root);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"failed to load model {modelPath}: {ex.Message}");
        }
        model.FinaliseLegs();
        return model;
    }

    private static void AddPart(LoadedModel model, JsonElement part)
    {
        string id = part.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? "part"
            : "part";

        TryGetTriple(part, "pivot", out float px, out float py, out float pz);
        TryGetTriple(part, "position", out float ox, out float oy, out float oz);
        TryGetTriple(part, "rotation", out float rx, out float ry, out float rz);

        if (!part.TryGetProperty("boxes", out JsonElement boxes) || boxes.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement box in boxes.EnumerateArray())
        {
            var p = new ModelPart
            {
                Id = id,
                IsLeg = id.Contains("leg", StringComparison.Ordinal),
                IsHead = id.Contains("head", StringComparison.Ordinal),
                // partGroup.position.set((px+ox)/16, -(py+oy)/16, (pz+oz)/16)
                PartPosition = new Vector3((px + ox) / PIXELS_PER_BLOCK,
                                           -(py + oy) / PIXELS_PER_BLOCK,
                                           (pz + oz) / PIXELS_PER_BLOCK),
                BaseRotation = new Vector3(DegToRad(rx), DegToRad(ry), DegToRad(rz)),
            };

            TryGetTriple(box, "origin", out float bx, out float by, out float bz);
            TryGetTriple(box, "size", out float sw, out float sh, out float sd);
            TryGetPair(box, "uv", out float u, out float v);
            float pad = box.TryGetProperty("inflate", out JsonElement inf) && inf.ValueKind == JsonValueKind.Number
                ? inf.GetSingle() : 0f;

            // mesh.position.set((bx + sw/2 - px)/16, -(by + sh/2 - py)/16, (bz + sd/2 - pz)/16)
            p.MeshPosition = new Vector3((bx + sw / 2f - px) / PIXELS_PER_BLOCK,
                                         -(by + sh / 2f - py) / PIXELS_PER_BLOCK,
                                         (bz + sd / 2f - pz) / PIXELS_PER_BLOCK);

            float bw = (sw + pad * 2f) / PIXELS_PER_BLOCK;
            float bh = (sh + pad * 2f) / PIXELS_PER_BLOCK;
            float bd = (sd + pad * 2f) / PIXELS_PER_BLOCK;
            p.Box = BuildSkinnedBox(bw, bh, bd, u, v, sw, sh, sd,
                                    model.TextureWidth, model.TextureHeight);
            model.Parts.Add(p);
        }
    }

    /// <summary>
    /// <c>new THREE.BoxGeometry(bw,bh,bd)</c> followed by the per-face UV remap in
    /// <c>buildBoxMesh</c>. <paramref name="w"/>/<paramref name="h"/>/<paramref name="d"/>
    /// are the un-inflated box size in texture pixels, which is what the UV layout uses.
    /// </summary>
    public static ModelBox BuildSkinnedBox(float bw, float bh, float bd,
                                           float u, float v, float w, float h, float d,
                                           int texW, int texH)
    {
        ModelBox raw = BuildBoxGeometry(bw, bh, bd);

        float U(float px) => px / texW;
        float V(float py) => 1f - py / texH;

        float uRight0 = u, uRight1 = u + d;
        float uFront0 = u + d, uFront1 = u + d + w;
        float uLeft0 = u + d + w, uLeft1 = u + d + w + d;
        float uBack0 = u + d + w + d, uBack1 = u + d + w + d + w;
        float uTop0 = u + d, uTop1 = u + d + w;
        float uBot0 = u + d + w, uBot1 = u + d + 2f * w;

        float vTop = v;
        float vRow = v + d;

        // [u0, u1, vTopRegion, vBotRegion] in BoxGeometry face order px,nx,py,ny,pz,nz.
        var regions = new (float U0, float U1, float VT, float VB)[]
        {
            (U(uRight0), U(uRight1), V(vRow + h), V(vRow)),
            (U(uLeft0), U(uLeft1), V(vRow + h), V(vRow)),
            (U(uTop0), U(uTop1), V(vRow), V(vTop)),
            (U(uBot0), U(uBot1), V(vRow), V(vTop)),
            (U(uFront0), U(uFront1), V(vRow + h), V(vRow)),
            (U(uBack0), U(uBack1), V(vRow + h), V(vRow)),
        };

        float[] verts = raw.Vertices;
        for (int f = 0; f < 6; f++)
        {
            var r = regions[f];
            for (int i = 0; i < 4; i++)
            {
                int o = (f * 4 + i) * ModelBox.FLOATS_PER_VERTEX;
                float nu = verts[o + 6];
                float nv = verts[o + 7];
                verts[o + 6] = r.U0 + nu * (r.U1 - r.U0);
                verts[o + 7] = r.VB + nv * (r.VT - r.VB);
            }
        }
        return raw;
    }

    /// <summary>
    /// three.js <c>BoxGeometry(w, h, d, 1, 1, 1)</c>. The six <c>buildPlane</c> calls are
    /// transcribed in order so the vertex numbering — and therefore the UV region mapping
    /// and the outward CCW winding — matches the library bit for bit.
    /// </summary>
    public static ModelBox BuildBoxGeometry(float width, float height, float depth)
    {
        var verts = new List<float>(24 * ModelBox.FLOATS_PER_VERTEX);
        var idx = new List<ushort>(36);

        // buildPlane(u, v, w, udir, vdir, planeWidth, planeHeight, planeDepth)
        void Plane(int ua, int va, int wa, float udir, float vdir, float pw, float ph, float pd)
        {
            int number = verts.Count / ModelBox.FLOATS_PER_VERTEX;
            float segW = pw, segH = ph;
            float widthHalf = pw / 2f, heightHalf = ph / 2f, depthHalf = pd / 2f;

            for (int iy = 0; iy <= 1; iy++)
            {
                float y = iy * segH - heightHalf;
                for (int ix = 0; ix <= 1; ix++)
                {
                    float x = ix * segW - widthHalf;
                    var p = new float[3];
                    p[ua] = x * udir;
                    p[va] = y * vdir;
                    p[wa] = depthHalf;

                    var n = new float[3];
                    n[wa] = depthHalf > 0f ? 1f : -1f;

                    verts.Add(p[0]); verts.Add(p[1]); verts.Add(p[2]);
                    verts.Add(n[0]); verts.Add(n[1]); verts.Add(n[2]);
                    verts.Add(ix / 1f);            // u = ix / gridX
                    verts.Add(1f - iy / 1f);       // v = 1 - iy / gridY
                }
            }

            // a = n + 2*iy + ix, b = a+2, c = b+1, d = a+1 ; push(a,b,d) push(b,c,d)
            for (int iy = 0; iy < 1; iy++)
            {
                for (int ix = 0; ix < 1; ix++)
                {
                    ushort a = (ushort)(number + 2 * iy + ix);
                    ushort b = (ushort)(number + 2 * (iy + 1) + ix);
                    ushort c = (ushort)(number + 2 * (iy + 1) + ix + 1);
                    ushort dd = (ushort)(number + 2 * iy + ix + 1);
                    idx.Add(a); idx.Add(b); idx.Add(dd);
                    idx.Add(b); idx.Add(c); idx.Add(dd);
                }
            }
        }

        // axis indices: 0 = x, 1 = y, 2 = z
        Plane(2, 1, 0, -1, -1, depth, height, width);    // px  (right)
        Plane(2, 1, 0, 1, -1, depth, height, -width);    // nx  (left)
        Plane(0, 2, 1, 1, 1, width, depth, height);      // py  (top)
        Plane(0, 2, 1, 1, -1, width, depth, -height);    // ny  (bottom)
        Plane(0, 1, 2, 1, -1, width, height, depth);     // pz  (front)
        Plane(0, 1, 2, -1, -1, width, height, -depth);   // nz  (back)

        return new ModelBox { Vertices = verts.ToArray(), Indices = idx.ToArray() };
    }

    /// <summary>
    /// three.js <c>PlaneGeometry(w, h)</c>: a single +z facing quad with uv (0,1),(1,1),
    /// (0,0),(1,0) and indices (0,2,1),(2,3,1). Used for flat item drops.
    /// </summary>
    public static ModelBox BuildPlaneGeometry(float width, float height)
    {
        float hw = width / 2f, hh = height / 2f;
        float[] verts =
        {
            -hw,  hh, 0f,   0f, 0f, 1f,   0f, 1f,
             hw,  hh, 0f,   0f, 0f, 1f,   1f, 1f,
            -hw, -hh, 0f,   0f, 0f, 1f,   0f, 0f,
             hw, -hh, 0f,   0f, 0f, 1f,   1f, 0f,
        };
        return new ModelBox { Vertices = verts, Indices = new ushort[] { 0, 2, 1, 2, 3, 1 } };
    }

    /// <summary>
    /// A cube whose six faces sample the correct atlas tiles for <paramref name="blockId"/>,
    /// laid out the way <c>ChunkMesher.AddFace</c> lays out a block. The browser build bound
    /// the whole atlas to a plain <c>BoxGeometry</c> here (see
    /// <see cref="GameOptions.HeldItemUseWholeAtlasSquash"/>), which squashes all 34 tiles
    /// onto every face; this is the behaviour the rest of the game implies.
    /// </summary>
    public static ModelBox BuildBlockCube(byte blockId, float size)
    {
        ref readonly BlockTextures tex = ref BlockTextureMap.Get(blockId);
        ModelBox box = BuildBoxGeometry(size, size, size);

        const int cols = GameConfig.ATLAS_COLS;
        float inset = GameConfig.UV_INSET / cols;
        (float U0, float U1, float V0, float V1) Slot(int ti) =>
            (ti / (float)cols + inset, (ti + 1) / (float)cols - inset,
             GameConfig.UV_INSET, 1f - GameConfig.UV_INSET);

        // BoxGeometry face order is px, nx, py, ny, pz, nz. The chunk mesher's face ids are
        // 0 = +y, 1 = -y, 2 = +z, 3 = -z, 4 = +x, 5 = -x, so remap before picking a slot.
        int[] mesherFace = { 4, 5, 0, 1, 2, 3 };
        for (int f = 0; f < 6; f++)
        {
            int fd = mesherFace[f];
            int ti = fd switch { 0 => tex.Top, 1 => tex.Bottom, _ => tex.Side };
            if (ti < 0) ti = tex.Side;
            var (u0, u1, v0, v1) = Slot(ti);

            for (int i = 0; i < 4; i++)
            {
                int o = (f * 4 + i) * ModelBox.FLOATS_PER_VERTEX;
                float nu = box.Vertices[o + 6];   // 0 or 1
                float nv = box.Vertices[o + 7];
                box.Vertices[o + 6] = u0 + nu * (u1 - u0);
                box.Vertices[o + 7] = v0 + nv * (v1 - v0);
            }
        }
        return box;
    }

    /// <summary>
    /// <c>EdgesGeometry(BoxGeometry(s,s,s))</c>: the 12 outer edges of the block highlight,
    /// as 24 line endpoints.
    /// </summary>
    public static float[] BuildBoxEdges(float size)
    {
        float e = size / 2f;
        Vector3[] c =
        {
            new(-e, -e, -e), new(e, -e, -e), new(e, e, -e), new(-e, e, -e),
            new(-e, -e, e), new(e, -e, e), new(e, e, e), new(-e, e, e),
        };
        (int A, int B)[] edges =
        {
            (0,1),(1,2),(2,3),(3,0),   // -z ring
            (4,5),(5,6),(6,7),(7,4),   // +z ring
            (0,4),(1,5),(2,6),(3,7),   // pillars
        };
        var v = new float[edges.Length * 6];
        int o = 0;
        foreach ((int a, int b) in edges)
        {
            v[o++] = c[a].X; v[o++] = c[a].Y; v[o++] = c[a].Z;
            v[o++] = c[b].X; v[o++] = c[b].Y; v[o++] = c[b].Z;
        }
        return v;
    }

    /// <summary>
    /// <c>convertToNewFormat()</c> from mobs.js: old model files carry <c>models</c> with
    /// <c>coordinates</c>/<c>textureOffset</c> instead of <c>parts</c> with
    /// <c>origin</c>/<c>size</c>/<c>uv</c>. Nothing in <c>dist/models</c> uses it any more,
    /// but the loader accepted it, so the port does too.
    /// </summary>
    internal static void AddLegacyParts(LoadedModel model, JsonElement root)
    {
        if (!root.TryGetProperty("models", out JsonElement oldParts) ||
            oldParts.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement op in oldParts.EnumerateArray())
        {
            string partId = op.TryGetProperty("part", out JsonElement pe) && pe.ValueKind == JsonValueKind.String
                ? pe.GetString()! : "part";
            if (!op.TryGetProperty("boxes", out JsonElement ob) || ob.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement b in ob.EnumerateArray())
            {
                float[] c = ReadArray(b, "coordinates", new float[] { 0, 0, 0, 8, 8, 8 });
                float[] t = ReadArray(b, "textureOffset", new float[] { 0, 0 });

                var p = new ModelPart
                {
                    Id = partId,
                    IsLeg = partId.Contains("leg", StringComparison.Ordinal),
                    IsHead = partId.Contains("head", StringComparison.Ordinal),
                    PartPosition = Vector3.Zero,
                    BaseRotation = Vector3.Zero,
                    MeshPosition = new Vector3((c[0] + c[3] / 2f) / PIXELS_PER_BLOCK,
                                               -(c[1] + c[4] / 2f) / PIXELS_PER_BLOCK,
                                               (c[2] + c[5] / 2f) / PIXELS_PER_BLOCK),
                };
                p.Box = BuildSkinnedBox(c[3] / PIXELS_PER_BLOCK, c[4] / PIXELS_PER_BLOCK, c[5] / PIXELS_PER_BLOCK,
                                        t[0], t[1], c[3], c[4], c[5],
                                        model.TextureWidth, model.TextureHeight);
                model.Parts.Add(p);
            }
        }
    }

    private static float[] ReadArray(JsonElement parent, string name, float[] fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            return fallback;
        var list = new List<float>();
        foreach (JsonElement e in el.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number) list.Add(e.GetSingle());
        return list.Count == fallback.Length ? list.ToArray() : fallback;
    }

    private static bool TryGetTriple(JsonElement el, string name, out float a, out float b, out float c)
    {
        a = b = c = 0f;
        if (!el.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return false;
        int i = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number) continue;
            if (i == 0) a = e.GetSingle();
            else if (i == 1) b = e.GetSingle();
            else if (i == 2) c = e.GetSingle();
            i++;
        }
        return i >= 3;
    }

    private static bool TryGetPair(JsonElement el, string name, out float a, out float b)
    {
        a = b = 0f;
        if (!el.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return false;
        int i = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number) continue;
            if (i == 0) a = e.GetSingle();
            else if (i == 1) b = e.GetSingle();
            i++;
        }
        return i >= 2;
    }

    private static float DegToRad(float d) => d * (MathF.PI / 180f);

    /// <summary>Loads every entry of <c>dist/models/manifest.json</c>.</summary>
    public static Dictionary<string, LoadedModel> LoadManifest(string distRoot, List<string> warnings)
    {
        var result = new Dictionary<string, LoadedModel>(StringComparer.Ordinal);
        string manifestPath = Path.Combine(distRoot, "models", "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == "version") continue;
                    string modelRel = prop.Value.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()! : $"models/{prop.Name}.json";
                    string texRel = prop.Value.TryGetProperty("texture", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()! : "";
                    string full = Path.Combine(distRoot, modelRel.Replace('/', Path.DirectorySeparatorChar));
                    LoadedModel loaded = Load(full, texRel, warnings);
                    result[prop.Name] = loaded;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"failed to read models/manifest.json: {ex.Message}");
            }
        }

        // Fall back to the known-good manifest if the file is missing, so the port still
        // has cows, zombies and torches to draw.
        if (result.Count == 0)
        {
            foreach ((string name, string modelRel, string texRel) in AssetManifest.Models)
            {
                string full = Path.Combine(distRoot, modelRel.Replace('/', Path.DirectorySeparatorChar));
                LoadedModel loaded = Load(full, texRel, warnings);
                result[name] = loaded;
            }
        }
        return result;
    }

    /// <summary>
    /// Converts a 0xRRGGBB colour the way <c>new THREE.Color(hex)</c> does when
    /// ColorManagement is on: sRGB decode into the linear working space.
    /// </summary>
    public static Vector3 HexToLinearVector(uint hex)
    {
        Colour.HexToLinear(hex, out float r, out float g, out float b);
        return new Vector3(r, g, b);
    }
}
