using System.Numerics;
using Silk.NET.OpenGL;
using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Maths;
using Shaft.Engine.Meshing;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Game.Rendering;

/// <summary>A VAO plus its buffers. One per chunk pass and one per static model box.</summary>
public sealed class GpuMesh : IDisposable
{
    private readonly GL _gl;
    public uint Vao, Vbo, Ebo;
    public int IndexCount;
    public PrimitiveType Mode = PrimitiveType.Triangles;

    public GpuMesh(GL gl) => _gl = gl;

    public bool IsEmpty => IndexCount == 0;

    public void Bind() => _gl.BindVertexArray(Vao);

    public unsafe void Draw()
    {
        if (IndexCount == 0) return;
        _gl.BindVertexArray(Vao);
        if (Mode == PrimitiveType.Lines)
            _gl.DrawArrays(Mode, 0, (uint)IndexCount);
        else
            _gl.DrawElements(Mode, (uint)IndexCount, DrawElementsType.UnsignedShort, (void*)0);
    }

    public void Dispose()
    {
        if (Vao != 0) { _gl.DeleteVertexArray(Vao); Vao = 0; }
        if (Vbo != 0) { _gl.DeleteBuffer(Vbo); Vbo = 0; }
        if (Ebo != 0) { _gl.DeleteBuffer(Ebo); Ebo = 0; }
    }
}

/// <summary>
/// Draws the 3D scene: chunk terrain, sky bodies, mobs, dropped items, break particles,
/// the block highlight and the held item.
/// <para>
/// The pass structure mirrors <c>core.js</c> exactly. There are three chunk materials —
/// <c>material</c> (opaque, alphaTest 0.5), <c>overlayMaterial</c> (transparent,
/// alphaTest 0.5, depthWrite) and <c>plantMaterial</c> (transparent, DoubleSide) — and
/// three.js sorts them into an opaque list and a transparent list which are drawn in that
/// order. Everything below reproduces that ordering, including the sun/moon sprites whose
/// <c>renderOrder = -1</c> puts them at the head of the transparent list.
/// </para>
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly TextureSet _tex;
    private readonly GameOptions _opts;
    private readonly List<string> _warnings;

    public ShaderProgram Terrain { get; }
    public ShaderProgram Entity { get; }
    public ShaderProgram Sprite { get; }
    public ShaderProgram Line { get; }

    public ParticleSystem Particles { get; }
    public Dictionary<string, LoadedModel> Models { get; }

    // three.js light colours, decoded from sRGB hex into the linear working space the
    // shaders expect. core.js: DirectionalLight(0xfff5d0), AmbientLight(0xffffff),
    // HemisphereLight(0x87ceeb sky, 0x4caf50 ground).
    private static readonly Vector3 SunColour = ModelLoader.HexToLinearVector(0xfff5d0);
    private static readonly Vector3 AmbientColour = Vector3.One;
    private static readonly Vector3 HemiSky = ModelLoader.HexToLinearVector(0x87ceeb);
    private static readonly Vector3 HemiGround = ModelLoader.HexToLinearVector(0x4caf50);
    private static readonly Vector3 SunDirectionConst =
        Vector3.Normalize(new Vector3(50f, 100f, 30f));
    /// <summary>Held-item scene: DirectionalLight(0xffffff,1.5) at (1,2,2) + Ambient .8.</summary>
    private static readonly Vector3 HeldLightDir = Vector3.Normalize(new Vector3(1f, 2f, 2f));

    private sealed class ChunkGpu : IDisposable
    {
        public GpuMesh Opaque = null!;
        public GpuMesh Overlay = null!;
        public GpuMesh Plants = null!;
        public Vector3 Min, Max;
        public Vector3 Centre;
        public void Dispose() { Opaque.Dispose(); Overlay.Dispose(); Plants.Dispose(); }
    }

    private readonly Dictionary<long, ChunkGpu> _chunks = new();
    private readonly Dictionary<ModelBox, GpuMesh> _meshCache = new(ReferenceEqualityComparer.Instance);

    private readonly List<(ChunkGpu Mesh, float Depth)> _sortOpaque = new();
    private readonly List<(ChunkGpu Mesh, float Depth)> _sortTransparent = new();
    private readonly Vector4[] _frustum = new Vector4[6];

    private GpuMesh _skyQuad = null!;
    private GpuMesh _highlight = null!;
    private GpuMesh _dropBlockCube = null!;
    private GpuMesh _dropItemPlane = null!;
    private GpuMesh _particleCube = null!;
    private GpuMesh _tameSphere = null!;
    private GpuMesh _heldItemBox = null!;
    private GpuMesh? _heldBlockMesh;
    private byte _heldBlockMeshId;

    private readonly float[] _skyVerts = new float[20];
    // Window size is in the device-independent units CSS used; the framebuffer is what
    // glViewport/glScissor take. Their ratio is renderer.setPixelRatio().
    private int _winW = 1, _winH = 1, _fbW = 1, _fbH = 1;

    public SceneRenderer(GL gl, TextureSet tex, GameOptions opts, VoxelWorld world,
                         List<string> warnings)
    {
        _gl = gl;
        _tex = tex;
        _opts = opts;
        _warnings = warnings;

        Terrain = new ShaderProgram(gl, Shaders.Expand(Shaders.TerrainVs),
                                    Shaders.Expand(Shaders.TerrainFs), "terrain");
        Entity = new ShaderProgram(gl, Shaders.Expand(Shaders.EntityVs),
                                   Shaders.Expand(Shaders.EntityFs), "entity");
        Sprite = new ShaderProgram(gl, Shaders.Expand(Shaders.SpriteVs),
                                   Shaders.Expand(Shaders.SpriteFs), "sprite");
        Line = new ShaderProgram(gl, Shaders.Expand(Shaders.LineVs),
                                 Shaders.Expand(Shaders.LineFs), "line");

        Particles = new ParticleSystem(world);
        Models = ModelLoader.LoadManifest(FindDistRoot(), warnings);

        BuildStaticGeometry();

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);   // three.js default: LessEqualDepth
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.Disable(EnableCap.Blend);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    }

    // ------------------------------------------------------------- construction

    private string FindDistRoot() => AssetPaths.DistRoot;

    private unsafe void BuildStaticGeometry()
    {
        // Sun/moon billboard: pos(3) + uv(2), refilled every frame.
        _skyQuad = new GpuMesh(_gl);
        _skyQuad.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_skyQuad.Vao);
        _skyQuad.Vbo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ArrayBuffer);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_skyVerts.Length * 4),
                       (void*)0, BufferUsageARB.DynamicDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 20, 0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 20, 12);
        _gl.BindVertexArray(0);
        _skyQuad.Mode = PrimitiveType.TriangleStrip;
        _skyQuad.IndexCount = 4;

        // mining.js: EdgesGeometry(BoxGeometry(1.001^3)), LineSegments.
        float[] edges = ModelLoader.BuildBoxEdges(1.001f);
        _highlight = CreateLineMesh(edges);

        // items.js: shared 0.25 cube for block drops, 0.4 plane for item drops.
        _dropBlockCube = CreateModelMesh(ModelLoader.BuildBoxGeometry(0.25f, 0.25f, 0.25f));
        _dropItemPlane = CreateModelMesh(ModelLoader.BuildPlaneGeometry(0.4f, 0.4f));
        // mining.js: shared 0.15 cube for break particles.
        _particleCube = CreateModelMesh(ModelLoader.BuildBoxGeometry(0.15f, 0.15f, 0.15f));
        // mobs.js togglePersistent(): SphereGeometry(0.12, 6, 4) marker above a tamed cow.
        _tameSphere = CreateModelMesh(SphereGeometry.Build(0.12f, 6, 4));

        // main.js held item: BoxGeometry(.15,.15,.5) for items, BoxGeometry(.6^3) for blocks.
        _heldItemBox = CreateModelMesh(ModelLoader.BuildBoxGeometry(0.15f, 0.15f, 0.5f));

        foreach (LoadedModel m in Models.Values)
            foreach (ModelPart p in m.Parts)
                if (!_meshCache.ContainsKey(p.Box)) _meshCache[p.Box] = CreateModelMesh(p.Box);
    }

    private GpuMesh CreateModelMesh(ModelBox box)
    {
        var mesh = new GpuMesh(_gl);
        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);

        mesh.Vbo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ArrayBuffer);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, box.Vertices, BufferUsageARB.StaticDraw);
        const int stride = ModelBox.FLOATS_PER_VERTEX * 4;
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 24);

        mesh.Ebo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ElementArrayBuffer);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, box.Indices, BufferUsageARB.StaticDraw);
        mesh.IndexCount = box.Indices.Length;
        mesh.Mode = PrimitiveType.Triangles;

        _gl.BindVertexArray(0);
        return mesh;
    }

    private GpuMesh CreateLineMesh(float[] positions)
    {
        var mesh = new GpuMesh(_gl);
        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);
        mesh.Vbo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ArrayBuffer);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, positions, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        mesh.IndexCount = positions.Length / 3;
        mesh.Mode = PrimitiveType.Lines;
        _gl.BindVertexArray(0);
        return mesh;
    }

    private GpuMesh GetMesh(ModelBox box)
    {
        if (_meshCache.TryGetValue(box, out GpuMesh? cached)) return cached;
        GpuMesh m = CreateModelMesh(box);
        _meshCache[box] = m;
        return m;
    }

    // ------------------------------------------------------------------ chunks

    public void UploadChunk(long key, Chunk chunk, ChunkMeshData data)
    {
        if (!_chunks.TryGetValue(key, out ChunkGpu? gpu))
        {
            gpu = new ChunkGpu
            {
                Opaque = new GpuMesh(_gl),
                Overlay = new GpuMesh(_gl),
                Plants = new GpuMesh(_gl),
            };
            _chunks[key] = gpu;
        }
        else
        {
            gpu.Opaque.Dispose();
            gpu.Overlay.Dispose();
            gpu.Plants.Dispose();
            gpu.Opaque = new GpuMesh(_gl);
            gpu.Overlay = new GpuMesh(_gl);
            gpu.Plants = new GpuMesh(_gl);
        }

        UploadPass(gpu.Opaque, data.Opaque);
        UploadPass(gpu.Overlay, data.Overlay);
        UploadPass(gpu.Plants, data.Plants);

        float x0 = chunk.OriginX, z0 = chunk.OriginZ;
        const float cs = GameConfig.CHUNK_SIZE;
        gpu.Min = new Vector3(x0, GameConfig.CHUNK_MIN_Y, z0);
        gpu.Max = new Vector3(x0 + cs, GameConfig.CHUNK_MIN_Y + GameConfig.CHUNK_HEIGHT, z0 + cs);
        gpu.Centre = (gpu.Min + gpu.Max) * 0.5f;
    }

    private void UploadPass(GpuMesh mesh, MeshBuilder builder)
    {
        if (builder.IsEmpty) return;

        float[] verts = builder.ToVertexArray();
        int[] srcIdx = builder.ToIndexArray();
        var idx = new ushort[srcIdx.Length];
        for (int i = 0; i < srcIdx.Length; i++) idx[i] = (ushort)srcIdx[i];

        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);

        mesh.Vbo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ArrayBuffer);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StaticDraw);
        const int stride = MeshBuilder.FLOATS_PER_VERTEX * 4;
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 12);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 20);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 32);

        mesh.Ebo = GlUtil.CreateBuffer(_gl, BufferTargetARB.ElementArrayBuffer);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);
        mesh.IndexCount = idx.Length;
        mesh.Mode = PrimitiveType.Triangles;

        _gl.BindVertexArray(0);
    }

    public void DropChunk(long key)
    {
        if (_chunks.TryGetValue(key, out ChunkGpu? gpu))
        {
            gpu.Dispose();
            _chunks.Remove(key);
        }
    }

    public void ClearChunks()
    {
        foreach (ChunkGpu gpu in _chunks.Values) gpu.Dispose();
        _chunks.Clear();
    }

    public int ChunkBufferCount => _chunks.Count;

    // ------------------------------------------------------------------ frames

    /// <summary>
    /// Records both sizes. <paramref name="fbW"/>/<paramref name="fbH"/> drive the GL
    /// viewport; <paramref name="winW"/>/<paramref name="winH"/> drive the CSS-derived
    /// layout maths in <c>main.js</c> (the held-item rectangle, in particular).
    /// </summary>
    public void SetSizes(int winW, int winH, int fbW, int fbH)
    {
        _winW = Math.Max(1, winW);
        _winH = Math.Max(1, winH);
        _fbW = Math.Max(1, fbW);
        _fbH = Math.Max(1, fbH);
    }

    public float PixelRatio => _fbW / (float)_winW;

    /// <summary>
    /// Clears to the sky colour and draws the whole main scene, in the order three.js's
    /// render lists produce.
    /// </summary>
    public void RenderScene(GameSession session, Camera cam, BlockHit? target, bool showHighlight)
    {
        DayNightSystem dn = session.DayNight;

        _gl.Viewport(0, 0, (uint)_fbW, (uint)_fbH);
        _gl.Scissor(0, 0, (uint)_fbW, (uint)_fbH);
        _gl.ClearColor(dn.SkySrgbR, dn.SkySrgbG, dn.SkySrgbB, 1f);
        _gl.ClearDepth(1.0);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        cam.GetFrustumPlanes(_frustum);
        CollectChunks(cam);

        // ---- opaque list -----------------------------------------------------
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);

        Terrain.Use();
        SetSceneLighting(Terrain, dn, fogEnabled: true);
        Terrain.Set("uView", cam.View);
        Terrain.Set("uProj", cam.Projection);
        Terrain.Set("uAlphaTest", 0.5f);
        BindAtlas();

        _sortOpaque.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));   // painterSortStable: near first
        foreach ((ChunkGpu mesh, _) in _sortOpaque) mesh.Opaque.Draw();

        Terrain.Set("uAlphaTest", 0f);
        DrawOpaqueEntities(session, cam, dn, showHighlight, target);

        // ---- transparent list ------------------------------------------------
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);       // overlayMaterial / plantMaterial keep depthWrite: true

        DrawSkyBodies(cam, dn);    // renderOrder -1 goes first

        _sortTransparent.Sort(static (a, b) => b.Depth.CompareTo(a.Depth)); // farthest first
        Terrain.Use();
        SetSceneLighting(Terrain, dn, fogEnabled: true);
        Terrain.Set("uView", cam.View);
        Terrain.Set("uProj", cam.Projection);
        Terrain.Set("uAlphaTest", 0.5f);
        BindAtlas();
        foreach ((ChunkGpu mesh, _) in _sortTransparent) mesh.Overlay.Draw();

        // plantMaterial is DoubleSide -> culling off.
        _gl.Disable(EnableCap.CullFace);
        foreach ((ChunkGpu mesh, _) in _sortTransparent) mesh.Plants.Draw();
        _gl.Enable(EnableCap.CullFace);

        DrawTransparentEntities(session, cam, dn);

        _gl.Disable(EnableCap.Blend);
    }

    private void BindAtlas()
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex.TerrainAtlas);
        Terrain.Set("uMap", 0);
    }

    private void CollectChunks(Camera cam)
    {
        _sortOpaque.Clear();
        _sortTransparent.Clear();
        Vector3 eye = cam.Position;
        foreach (ChunkGpu gpu in _chunks.Values)
        {
            if (gpu.Opaque.IsEmpty && gpu.Overlay.IsEmpty && gpu.Plants.IsEmpty) continue;
            if (!Camera.BoxInFrustum(_frustum, gpu.Min, gpu.Max)) continue;
            Vector3 d = gpu.Centre - eye;
            float depth = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            _sortOpaque.Add((gpu, depth));
            _sortTransparent.Add((gpu, depth));
        }
    }

    // ---------------------------------------------------------------- entities

    private void SetSceneLighting(ShaderProgram p, DayNightSystem dn, bool fogEnabled)
    {
        p.Set("uSunColor", SunColour);
        p.Set("uSunIntensity", dn.SunIntensity);
        p.Set("uSunDir", new Vector3(dn.SunX, dn.SunY, dn.SunZ));
        p.Set("uAmbientColor", AmbientColour);
        p.Set("uAmbientIntensity", dn.AmbientIntensity);
        p.Set("uHemiSky", HemiSky);
        p.Set("uHemiGround", HemiGround);
        p.Set("uHemiIntensity", dn.HemisphereIntensity);
        p.Set("uFogColor", dn.SkySrgbR, dn.SkySrgbG, dn.SkySrgbB);
        p.Set("uFogNear", DayNightSystem.FOG_NEAR);
        p.Set("uFogFar", DayNightSystem.FOG_FAR);
        p.Set("uFogEnabled", fogEnabled ? 1f : 0f);
    }

    /// <summary>Shared setup for anything drawn through the entity program.</summary>
    private void BeginEntities(Camera cam, DayNightSystem dn, bool fog)
    {
        Entity.Use();
        SetSceneLighting(Entity, dn, fog);
        Entity.Set("uView", cam.View);
        Entity.Set("uProj", cam.Projection);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void SetEntityMaterial(uint texture, bool useMap, bool lit, Vector3 colour,
                                   float opacity, float alphaTest, Vector3 emissive)
    {
        if (useMap) _gl.BindTexture(TextureTarget.Texture2D, texture);
        else _gl.BindTexture(TextureTarget.Texture2D, _tex.WhiteTexture);
        Entity.Set("uMap", 0);
        Entity.Set("uUseMap", useMap ? 1f : 0f);
        Entity.Set("uLit", lit ? 1f : 0f);
        Entity.Set("uColor", colour);
        Entity.Set("uOpacity", opacity);
        Entity.Set("uAlphaTest", alphaTest);
        Entity.Set("uEmissive", emissive);
    }

    private void DrawOpaqueEntities(GameSession session, Camera cam, DayNightSystem dn,
                                    bool showHighlight, BlockHit? target)
    {
        BeginEntities(cam, dn, fog: true);

        // ---- mobs (MeshLambertMaterial, map: skin, alphaTest 0) ----
        foreach (Mob mob in session.Mobs.Mobs)
        {
            if (mob.Dead) continue;
            string key = mob.Type == MobType.Cow ? "cow" : "zombie";
            if (!Models.TryGetValue(key, out LoadedModel? model)) continue;
            if (!_tex.ModelTextures.TryGetValue(model.TexturePath, out uint skin)) continue;

            // mobs.js: cows add PI to the atan2 heading; the engine stores the raw heading.
            float yaw = mob.Yaw + (mob.Type == MobType.Cow ? MathF.PI : 0f);
            Matrix4x4 group = Matrix4x4.CreateRotationY(yaw)
                            * Matrix4x4.CreateTranslation((float)mob.X, (float)mob.Y, (float)mob.Z);

            // Burning zombies tint their material (1.0, 0.55, 0.3); the hurt flash is an
            // emissive red ramp over the 0.15s FlashTimer window.
            Vector3 tint = mob.Burning ? new Vector3(1f, 0.55f, 0.3f) : Vector3.One;
            float flash = Math.Max(0f, mob.FlashTimer / 0.15f);
            var emissive = new Vector3(flash, 0f, 0f);

            float swing = MathF.Sin(mob.WalkAnim) * (mob.Type == MobType.Cow ? 0.4f : 0.5f);

            SetEntityMaterial(skin, useMap: true, lit: true, tint, 1f, 0f, emissive);
            foreach (ModelPart part in model.Parts)
            {
                float legRot = part.IsLeg ? (part.LegIndex % 2 == 0 ? swing : -swing) : 0f;
                Matrix4x4 m = model.BoxMatrix(part) * model.PartMatrix(part, legRot) * group;
                Entity.Set("uModel", m);
                GetMesh(part.Box).Draw();
            }

            // Tamed (persistent) cows carry a floating marker above their head.
            if (mob.Type == MobType.Cow && mob.Persistent)
            {
                SetEntityMaterial(0, useMap: false, lit: false,
                                  ModelLoader.HexToLinearVector(0xffcc44), 1f, 0f, Vector3.Zero);
                Matrix4x4 marker = Matrix4x4.CreateTranslation(0f, mob.Height + 0.35f, 0f) * group;
                Entity.Set("uModel", marker);
                _tameSphere.Draw();
            }
        }

        // ---- block-shaped drops (Lambert, colour only, no map) ----
        foreach (DroppedItem drop in session.Mining.DroppedItems)
        {
            if (drop.Dead || drop.IsItem) continue;
            Colour.HexToLinear(BlockInfoTable.Get((byte)drop.Id).Color, out float r, out float g, out float b);
            SetEntityMaterial(0, useMap: false, lit: true, new Vector3(r, g, b), 1f, 0f, Vector3.Zero);
            Matrix4x4 m = Matrix4x4.CreateRotationY(drop.Age * 1.5f)
                        * Matrix4x4.CreateTranslation((float)drop.X, RenderY(drop), (float)drop.Z);
            Entity.Set("uModel", m);
            _dropBlockCube.Draw();
        }

        // ---- break particles (Lambert, colour only) ----
        for (int i = 0; i < Particles.Count; i++)
        {
            Vector3 colour = Particles.ColorAt(i);
            Vector3 pos = Particles.PositionAt(i);
            (float rx, float ry) = Particles.RotationAt(i);
            float scale = Particles.ScaleAt(i);
            SetEntityMaterial(0, useMap: false, lit: true, colour, 1f, 0f, Vector3.Zero);
            Matrix4x4 m = Matrix4x4.CreateRotationX(rx) * Matrix4x4.CreateRotationY(ry)
                        * Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateTranslation(pos);
            Entity.Set("uModel", m);
            _particleCube.Draw();
        }

        // ---- block highlight (LineSegments, black) ----
        if (showHighlight && target is BlockHit hit)
        {
            _gl.LineWidth(2f);   // honoured by few drivers; core profile pins it to 1
            Line.Use();
            Line.Set("uView", cam.View);
            Line.Set("uProj", cam.Projection);
            Line.Set("uColor", 0f, 0f, 0f);
            Line.Set("uFogColor", dn.SkySrgbR, dn.SkySrgbG, dn.SkySrgbB);
            Line.Set("uFogNear", DayNightSystem.FOG_NEAR);
            Line.Set("uFogFar", DayNightSystem.FOG_FAR);
            Line.Set("uFogEnabled", 1f);
            Line.Set("uModel", Matrix4x4.CreateTranslation(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f));
            _highlight.Draw();
            BeginEntities(cam, dn, fog: true);
        }
    }

    private void DrawTransparentEntities(GameSession session, Camera cam, DayNightSystem dn)
    {
        BeginEntities(cam, dn, fog: true);

        // Flat item drops: MeshBasicMaterial, transparent, DoubleSide, map = item texture.
        _gl.Disable(EnableCap.CullFace);
        foreach (DroppedItem drop in session.Mining.DroppedItems)
        {
            if (drop.Dead || !drop.IsItem) continue;

            // items.js: mesh.lookAt(camera.position) — a camera-facing quad.
            Vector3 pos = new((float)drop.X, RenderY(drop), (float)drop.Z);
            Matrix4x4 m = BillboardMatrix(pos, cam.Position);
            Entity.Set("uModel", m);

            if (_opts.ItemsUseFlatColourIcons || !_tex.ItemTextures.TryGetValue(drop.Id, out uint icon))
            {
                // The fallback branch in spawnDroppedItem: MeshBasicMaterial({ color }).
                Colour.HexToLinear(ItemTable.GetInfo(drop.Id).Color, out float r, out float g, out float b);
                SetEntityMaterial(0, useMap: false, lit: false, new Vector3(r, g, b), 1f, 0.5f, Vector3.Zero);
            }
            else
            {
                SetEntityMaterial(icon, useMap: true, lit: false, Vector3.One, 1f, 0.5f, Vector3.Zero);
            }
            _dropItemPlane.Draw();
        }
        _gl.Enable(EnableCap.CullFace);
    }

    /// <summary><c>Object3D.lookAt</c> for an object whose +z faces the target.</summary>
    private static Matrix4x4 BillboardMatrix(Vector3 position, Vector3 target)
    {
        Vector3 z = target - position;
        if (z.LengthSquared() < 1e-12f) z = new Vector3(0f, 0f, 1f);
        z = Vector3.Normalize(z);
        Vector3 up = MathF.Abs(Vector3.Dot(z, Vector3.UnitY)) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z));
        Vector3 y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0f,
                              y.X, y.Y, y.Z, 0f,
                              z.X, z.Y, z.Z, 0f,
                              position.X, position.Y, position.Z, 1f);
        return m;
    }

    /// <summary>
    /// items.js bobs grounded drops by <c>sin(age*3)*0.1</c>; the telescoping
    /// <c>bobOffset</c> arithmetic reduces to adding that offset to the physics Y.
    /// </summary>
    private static float RenderY(DroppedItem drop)
        => (float)(drop.Y + (drop.OnGround ? Math.Sin(drop.Age * 3f) * 0.1f : 0f));

    // --------------------------------------------------------------- sky bodies

    /// <summary>
    /// <c>sunSprite</c> / <c>moonSprite</c>: camera-facing quads 500 blocks along ±sunDirection,
    /// scaled 100 and 80, with the opacity and visibility rules from <c>updateDayNight</c>.
    /// </summary>
    private void DrawSkyBodies(Camera cam, DayNightSystem dn)
    {
        Vector3 sunDir = new(dn.SunX, dn.SunY, dn.SunZ);
        float len = sunDir.Length();
        if (len < 1e-6f) return;
        sunDir /= len;

        float sunOpacity = Math.Clamp(sunDir.Y * 4f + 0.3f, 0f, 1f);
        float moonOpacity = Math.Clamp(-sunDir.Y * 4f + 0.3f, 0f, 1f);
        bool sunVisible = sunDir.Y > -0.15f;
        bool moonVisible = sunDir.Y < 0.15f;
        if (!sunVisible && !moonVisible) return;

        _gl.DepthMask(false);      // SpriteMaterial: depthWrite false
        _gl.Disable(EnableCap.CullFace);
        Sprite.Use();
        Sprite.Set("uView", cam.View);
        Sprite.Set("uProj", cam.Projection);
        Sprite.Set("uMap", 0);

        if (sunVisible && sunOpacity > 0f)
            DrawSkyQuad(cam, _tex.SunTexture, cam.Position + sunDir * 500f, 100f, sunOpacity);
        if (moonVisible && moonOpacity > 0f)
            DrawSkyQuad(cam, _tex.MoonTexture, cam.Position - sunDir * 500f, 80f, moonOpacity);

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
    }

    private void DrawSkyQuad(Camera cam, uint texture, Vector3 centre, float size, float opacity)
    {
        Vector3 right = cam.Right * (size * 0.5f);
        Vector3 up = cam.Up * (size * 0.5f);

        // Triangle strip: bottom-left, bottom-right, top-left, top-right.
        WriteSkyVertex(0, centre - right - up, 0f, 0f);
        WriteSkyVertex(1, centre + right - up, 1f, 0f);
        WriteSkyVertex(2, centre - right + up, 0f, 1f);
        WriteSkyVertex(3, centre + right + up, 1f, 1f);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        Sprite.Set("uColor", 1f, 1f, 1f, opacity);

        _gl.BindVertexArray(_skyQuad.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _skyQuad.Vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, _skyVerts, BufferUsageARB.DynamicDraw);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4u);
        _gl.BindVertexArray(0);
    }

    private void WriteSkyVertex(int i, Vector3 p, float u, float v)
    {
        int o = i * 5;
        _skyVerts[o] = p.X; _skyVerts[o + 1] = p.Y; _skyVerts[o + 2] = p.Z;
        _skyVerts[o + 3] = u; _skyVerts[o + 4] = v;
    }

    // -------------------------------------------------------------- held item

    /// <summary>
    /// <c>updateHeldItem</c> + the scissored second <c>renderer.render(heldScene, heldCamera)</c>
    /// in <c>main.js</c>. The held scene has its own camera (fov 50, at z = 3), its own
    /// lights and no fog.
    /// </summary>
    /// <param name="heldBob">Accumulated by the host: <c>dt * (moving ? (sprint?14:9) : 2)</c>.</param>
    public void RenderHeldItem(GameSession session, float heldBob, bool isMoving)
    {
        ItemStack held = session.Inventory.Slots[session.Inventory.SelectedIndex];
        if (held.IsEmpty) return;

        int size = (int)(Math.Min(_winW, _winH) * 0.18);
        if (size < 1) return;
        // main.js computes these in client (CSS) pixels; three.js multiplies by the pixel
        // ratio before handing them to gl.viewport, whose origin is the bottom-left corner.
        float ratio = PixelRatio;
        int x = (int)((_winW - size - 20) * ratio);
        int y = (int)((_winH - size - 90) * ratio);
        int px = Math.Max(1, (int)(size * ratio));
        if (y < 0) y = 0;

        _gl.Viewport(x, y, (uint)px, (uint)px);
        _gl.Scissor(x, y, (uint)px, (uint)px);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Clear((uint)ClearBufferMask.DepthBufferBit);

        // heldCamera: PerspectiveCamera(50, 1, .1, 10) at (0,0,3) looking down -z.
        Matrix4x4 view = Matrix4x4.CreateTranslation(0f, 0f, -3f);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180f, 1f, 0.1f, 10f);

        // heldMesh.rotation.set(-.2,.6,.1), then position.y and rotation.z are driven by
        // the bob. three.js Euler XYZ -> R = Rx(-.2) * Ry(.6) * Rz(z).
        float rz = 0.1f + MathF.Sin(heldBob * 0.5f) * 0.05f;
        float by = MathF.Sin(heldBob) * 0.03f;
        Matrix4x4 model = LoadedModel.EulerXyz(-0.2f, 0.6f, rz)
                        * Matrix4x4.CreateTranslation(0f, by, 0f);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);

        Entity.Use();
        Entity.Set("uView", view);
        Entity.Set("uProj", proj);
        Entity.Set("uModel", model);
        // heldScene has no fog and its own lights: directional 1.5 from (1,2,2), ambient .8.
        Entity.Set("uSunColor", Vector3.One);
        Entity.Set("uSunIntensity", 1.5f);
        Entity.Set("uSunDir", HeldLightDir);
        Entity.Set("uAmbientColor", Vector3.One);
        Entity.Set("uAmbientIntensity", 0.8f);
        Entity.Set("uHemiSky", Vector3.Zero);
        Entity.Set("uHemiGround", Vector3.Zero);
        Entity.Set("uHemiIntensity", 0f);
        Entity.Set("uFogEnabled", 0f);
        Entity.Set("uFogNear", 0f);
        Entity.Set("uFogFar", 1e9f);
        Entity.Set("uFogColor", 0f, 0f, 0f);
        Entity.Set("uOpacity", 1f);
        Entity.Set("uAlphaTest", 0f);
        Entity.Set("uEmissive", Vector3.Zero);

        _gl.ActiveTexture(TextureUnit.Texture0);
        if (ItemId.IsItem(held.Id))
        {
            // heldMat.map = null; heldMat.color = getInfo(id).color
            Colour.HexToLinear(ItemTable.GetInfo(held.Id).Color, out float r, out float g, out float b);
            Entity.Set("uUseMap", 0f);
            Entity.Set("uLit", 1f);
            Entity.Set("uColor", r, g, b);
            _gl.BindTexture(TextureTarget.Texture2D, _tex.WhiteTexture);
            Entity.Set("uMap", 0);
            _heldItemBox.Draw();
        }
        else
        {
            Entity.Set("uLit", 1f);
            Entity.Set("uColor", 1f, 1f, 1f);
            _gl.BindTexture(TextureTarget.Texture2D, _tex.TerrainAtlas);
            Entity.Set("uMap", 0);

            if (_opts.HeldItemUseWholeAtlasSquash)
            {
                // Exactly what main.js does: a plain BoxGeometry(.6^3) whose UVs span the
                // whole 1024x16 atlas, so all 34 tiles squash onto every face.
                Entity.Set("uUseMap", 1f);
                EnsureWholeAtlasCube();
                _heldBlockMesh?.Draw();
            }
            else
            {
                Entity.Set("uUseMap", 1f);
                GpuMesh cube = EnsureBlockCube((byte)held.Id);
                cube.Draw();
            }
        }

        _gl.Disable(EnableCap.ScissorTest);
        _gl.Viewport(0, 0, (uint)_fbW, (uint)_fbH);
        _gl.Scissor(0, 0, (uint)_fbW, (uint)_fbH);
    }

    private void EnsureWholeAtlasCube()
    {
        if (_heldBlockMesh is not null && _heldBlockMeshId == 0xFF) return;
        ModelBox box = ModelLoader.BuildBoxGeometry(0.6f, 0.6f, 0.6f);
        _heldBlockMesh = GetMesh(box);
        _heldBlockMeshId = 0xFF;
    }

    private GpuMesh EnsureBlockCube(byte blockId)
    {
        if (_heldBlockMesh is not null && _heldBlockMeshId == blockId) return _heldBlockMesh;
        ModelBox box = ModelLoader.BuildBlockCube(blockId, 0.6f);
        _heldBlockMesh = GetMesh(box);
        _heldBlockMeshId = blockId;
        return _heldBlockMesh;
    }

    // ------------------------------------------------------------------ lifecycle

    public void Dispose()
    {
        ClearChunks();
        foreach (GpuMesh m in _meshCache.Values) m.Dispose();
        _meshCache.Clear();
        _skyQuad.Dispose();
        _highlight.Dispose();
        _dropBlockCube.Dispose();
        _dropItemPlane.Dispose();
        _particleCube.Dispose();
        _tameSphere.Dispose();
        _heldItemBox.Dispose();
        _heldBlockMesh?.Dispose();
        Terrain.Dispose();
        Entity.Dispose();
        Sprite.Dispose();
        Line.Dispose();
    }
}

/// <summary>
/// three.js <c>SphereGeometry(radius, widthSegments, heightSegments)</c>, used for the
/// tamed-cow marker. Vertex order and the two pole-degenerate index skips are the
/// library's, so the mesh is watertight the same way.
/// </summary>
internal static class SphereGeometry
{
    public static ModelBox Build(float radius, int widthSegments, int heightSegments)
    {
        var verts = new List<float>();
        var idx = new List<ushort>();
        var grid = new int[heightSegments + 1, widthSegments + 1];
        var normal = Vector3.Zero;

        for (int iy = 0; iy <= heightSegments; iy++)
        {
            float v = iy / (float)heightSegments;
            for (int ix = 0; ix <= widthSegments; ix++)
            {
                float u = ix / (float)widthSegments;
                float phi = u * MathF.PI * 2f;
                float theta = v * MathF.PI;

                float x = -radius * MathF.Cos(phi) * MathF.Sin(theta);
                float y = radius * MathF.Cos(theta);
                float z = radius * MathF.Sin(phi) * MathF.Sin(theta);

                normal = Vector3.Normalize(new Vector3(x, y, z));
                verts.Add(x); verts.Add(y); verts.Add(z);
                verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z);
                verts.Add(u); verts.Add(1f - v);
                grid[iy, ix] = verts.Count / ModelBox.FLOATS_PER_VERTEX - 1;
            }
        }

        for (int iy = 0; iy < heightSegments; iy++)
        {
            for (int ix = 0; ix < widthSegments; ix++)
            {
                int a = grid[iy, ix + 1];
                int b = grid[iy, ix];
                int c = grid[iy + 1, ix];
                int d = grid[iy + 1, ix + 1];
                if (iy != 0) { idx.Add((ushort)a); idx.Add((ushort)b); idx.Add((ushort)d); }
                if (iy != heightSegments - 1) { idx.Add((ushort)b); idx.Add((ushort)c); idx.Add((ushort)d); }
            }
        }

        return new ModelBox { Vertices = verts.ToArray(), Indices = idx.ToArray() };
    }
}
