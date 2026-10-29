using Shaft.Engine.Config;
using Shaft.Engine.World;

namespace Shaft.Engine.Meshing;

/// <summary>
/// Growable interleaved vertex buffer + index buffer for one render pass of a chunk.
/// Vertex layout is <c>pos(3) uv(2) normal(3) color(3)</c> = 11 floats, matching the
/// attributes the JS mesher emitted as separate <c>BufferAttribute</c>s.
/// </summary>
public sealed class MeshBuilder
{
    public const int FLOATS_PER_VERTEX = 11;

    private float[] _v;
    private int[] _i;

    public int VertexCount { get; private set; }
    public int IndexCount { get; private set; }

    public MeshBuilder(int initialVertices = 4096)
    {
        _v = new float[initialVertices * FLOATS_PER_VERTEX];
        _i = new int[initialVertices * 3 / 2];
    }

    public bool IsEmpty => VertexCount == 0;

    public void Clear() { VertexCount = 0; IndexCount = 0; }

    private void EnsureVertices(int count)
    {
        int need = (VertexCount + count) * FLOATS_PER_VERTEX;
        if (need <= _v.Length) return;
        int cap = _v.Length;
        while (cap < need) cap *= 2;
        Array.Resize(ref _v, cap);
    }

    private void EnsureIndices(int count)
    {
        int need = IndexCount + count;
        if (need <= _i.Length) return;
        int cap = _i.Length;
        while (cap < need) cap *= 2;
        Array.Resize(ref _i, cap);
    }

    /// <summary>Appends four vertices and the 0-1-2 / 0-2-3 index pair, exactly as
    /// the JS <c>quad()</c> closure did.</summary>
    public void Quad(
        float ax, float ay, float az, float bx, float by, float bz,
        float cx, float cy, float cz, float dx, float dy, float dz,
        float au, float av, float bu, float bv, float cu, float cv, float du, float dv,
        float nx, float ny, float nz,
        float cr, float cg, float cb)
    {
        EnsureVertices(4);
        EnsureIndices(6);
        int baseIdx = VertexCount;

        Write(baseIdx + 0, ax, ay, az, au, av, nx, ny, nz, cr, cg, cb);
        Write(baseIdx + 1, bx, by, bz, bu, bv, nx, ny, nz, cr, cg, cb);
        Write(baseIdx + 2, cx, cy, cz, cu, cv, nx, ny, nz, cr, cg, cb);
        Write(baseIdx + 3, dx, dy, dz, du, dv, nx, ny, nz, cr, cg, cb);

        _i[IndexCount++] = baseIdx;
        _i[IndexCount++] = baseIdx + 1;
        _i[IndexCount++] = baseIdx + 2;
        _i[IndexCount++] = baseIdx;
        _i[IndexCount++] = baseIdx + 2;
        _i[IndexCount++] = baseIdx + 3;
    }

    private void Write(int vi, float px, float py, float pz, float u, float v,
                       float nx, float ny, float nz, float cr, float cg, float cb)
    {
        int o = vi * FLOATS_PER_VERTEX;
        _v[o + 0] = px; _v[o + 1] = py; _v[o + 2] = pz;
        _v[o + 3] = u; _v[o + 4] = v;
        _v[o + 5] = nx; _v[o + 6] = ny; _v[o + 7] = nz;
        _v[o + 8] = cr; _v[o + 9] = cg; _v[o + 10] = cb;
        VertexCount = Math.Max(VertexCount, vi + 1);
    }

    /// <summary>Trims to a tightly-sized array ready for <c>glBufferData</c>.</summary>
    public float[] ToVertexArray()
    {
        var outArr = new float[VertexCount * FLOATS_PER_VERTEX];
        Array.Copy(_v, outArr, outArr.Length);
        return outArr;
    }

    public int[] ToIndexArray()
    {
        var outArr = new int[IndexCount];
        Array.Copy(_i, outArr, IndexCount);
        return outArr;
    }
}

/// <summary>The three render passes a chunk produces, mirroring the JS
/// <c>{ geom, oGeom, pGeom }</c> return value.</summary>
public sealed class ChunkMeshData
{
    /// <summary>Solid terrain and water. JS <c>material</c>: alphaTest 0.5, no blend.</summary>
    public MeshBuilder Opaque { get; } = new();
    /// <summary>Grass side overlay. JS <c>overlayMaterial</c>: transparent, alphaTest 0.5.</summary>
    public MeshBuilder Overlay { get; } = new();
    /// <summary>Short grass cross-quads. JS <c>plantMaterial</c>: double-sided, alphaTest 0.5.</summary>
    public MeshBuilder Plants { get; } = new();

    public void Clear() { Opaque.Clear(); Overlay.Clear(); Plants.Clear(); }
    public bool IsEmpty => Opaque.IsEmpty && Overlay.IsEmpty && Plants.IsEmpty;
}

/// <summary>
/// Port of <c>buildChunkGeometry</c> from <c>dist/chunks.js</c>.
/// </summary>
/// <remarks>
/// Deliberate deviations from the JS, each documented because it changes behaviour:
/// <list type="number">
/// <item>
/// Blocks are read from the chunk's flat array via <see cref="World.VoxelWorld"/>
/// instead of being recomputed from noise. Neighbour lookups that cross a chunk
/// border read the adjacent chunk; if it is not loaded the face is suppressed
/// (treated as opaque) rather than left open, which removes the border flicker the
/// JS engine could show at the edge of the loaded set.
/// </item>
/// <item>
/// The JS main loop does <c>if (block === BLOCK.TORCH) continue;</c>, which makes
/// every <c>isTorch</c> branch below it dead code — torches were instead drawn as
/// model instances by <c>torches.js</c>. This port meshes torches using the
/// author's own <c>isTorch</c> geometry (height 0.625, inset 0.375) so that they are
/// visible without a separate model-instancing pass.
/// </item>
/// <item>
/// No greedy meshing, matching the JS per-face emitter. The vertex layout is
/// interleaved for direct upload instead of four separate attribute arrays.
/// </item>
/// </list>
/// </remarks>
public sealed class ChunkMesher
{
    private readonly World.VoxelWorld _world;
    private readonly World.Lighting _lighting;

    public ChunkMesher(World.VoxelWorld world, World.Lighting lighting)
    {
        _world = world;
        _lighting = lighting;
    }

    private const int ATLAS_COLS = GameConfig.ATLAS_COLS;
    private const float UV_INSET = GameConfig.UV_INSET;

    // ------------------------------------------------------------------ entry

    public void Build(World.Chunk chunk, ChunkMeshData outMesh)
    {
        outMesh.Clear();

        int bX = chunk.OriginX, bZ = chunk.OriginZ;
        var gen = _world.Generator;

        for (int lx = 0; lx < GameConfig.CHUNK_SIZE; lx++)
        for (int lz = 0; lz < GameConfig.CHUNK_SIZE; lz++)
        {
            int wx = bX + lx, wz = bZ + lz;

            // maxY from the JS: max(terrainHeight + 30, SEA_LEVEL + 30), clamped to
            // the top of the voxel array.
            int h = gen.GetHeight(wx, wz);
            int maxY = Math.Max(h + 30, GameConfig.SEA_LEVEL + 30);
            if (maxY > GameConfig.CHUNK_MAX_Y) maxY = GameConfig.CHUNK_MAX_Y;

            // Grass tint, sampled at the column centre exactly as the JS did.
            uint tintHex = gen.GetGrassColorAt(wx + 0.5, wz + 0.5);
            ChunkGenerator.HexToLinear(tintHex, out float tr, out float tg, out float tb);

            for (int ly = GameConfig.WORLD_BOTTOM; ly <= maxY; ly++)
            {
                byte block = chunk.Get(lx, ly, lz);
                if (block == Block.AIR) continue;
                // chunks.js: torches are model entities (torches.js) and short grass is emitted
                // from the grass branch below; neither is ever chunk geometry.
                if (block == Block.TORCH || block == Block.SHORT_GRASS) continue;
                if (block == Block.SHORT_GRASS) continue;   // emitted as cross-quads below

                if (Block.IsDoor(block))
                {
                    AddDoorBlock(outMesh, lx, ly, lz, wx, wz, block, _world.GetFacing(wx, ly, wz),
                                 Block.IsDoorOpen(block));
                    continue;
                }

                bool isW = Block.IsWater(block);
                bool isFalling = block == Block.WATER_FALLING;
                bool isL = block == Block.LEAVES;
                bool isC = block == Block.CACTUS;
                bool isG = block == Block.GRASS;
                bool isTorch = block == Block.TORCH;
                bool isBed = block == Block.BED_FOOT || block == Block.BED_HEAD;
                float pad = isC ? (1 - GameConfig.CACTUS_WIDTH) / 2f : 0f;

                byte above = Neighbour(chunk, lx, ly + 1, lz);
                byte below = Neighbour(chunk, lx, ly - 1, lz);

                float waterH = isW ? Block.GetWaterHeight(block) : 1f;

                // ---- top face ----
                bool sT;
                if (isW) sT = !Block.IsWater(above);
                else if (isC) sT = above != Block.CACTUS;
                else if (isL) sT = above != Block.LEAVES;
                else if (isTorch) sT = true;
                else sT = !Block.IsOpaque(above);

                if (sT)
                {
                    if (isW) AddFace(outMesh, chunk, lx, ly, lz, block, 0, waterH, pad);
                    else if (isG) AddFace(outMesh, chunk, lx, ly, lz, block, 0, 1f, pad, true, true, tr, tg, tb);
                    else
                    {
                        float hs2 = 1f;
                        if (isBed) hs2 = .5625f;
                        if (isTorch) hs2 = .625f;
                        AddFace(outMesh, chunk, lx, ly, lz, block, 0, hs2, pad);
                    }
                }

                // ---- bottom face ----
                bool sB;
                if (isW) sB = isFalling && !Block.IsWater(below) && !Block.IsOpaque(below);
                else if (isC) sB = below != Block.CACTUS;
                else if (isL) sB = below != Block.LEAVES;
                else if (isTorch) sB = true;
                else sB = !Block.IsOpaque(below);

                if (sB) AddFace(outMesh, chunk, lx, ly, lz, block, 1, 1f, pad);

                // ---- four side faces ----
                byte nF = Neighbour(chunk, lx, ly, lz + 1);
                byte nB = Neighbour(chunk, lx, ly, lz - 1);
                byte nR = Neighbour(chunk, lx + 1, ly, lz);
                byte nL = Neighbour(chunk, lx - 1, ly, lz);

                if (isW)
                {
                    if (ShouldDrawWaterSide(nF, waterH)) AddFace(outMesh, chunk, lx, ly, lz, block, 2, waterH, pad);
                    if (ShouldDrawWaterSide(nB, waterH)) AddFace(outMesh, chunk, lx, ly, lz, block, 3, waterH, pad);
                    if (ShouldDrawWaterSide(nR, waterH)) AddFace(outMesh, chunk, lx, ly, lz, block, 4, waterH, pad);
                    if (ShouldDrawWaterSide(nL, waterH)) AddFace(outMesh, chunk, lx, ly, lz, block, 5, waterH, pad);
                }
                else
                {
                    SideFace(outMesh, chunk, lx, ly, lz, block, 2, nF, isC, isL, isTorch, isG, isBed, pad, tr, tg, tb);
                    SideFace(outMesh, chunk, lx, ly, lz, block, 3, nB, isC, isL, isTorch, isG, isBed, pad, tr, tg, tb);
                    SideFace(outMesh, chunk, lx, ly, lz, block, 4, nR, isC, isL, isTorch, isG, isBed, pad, tr, tg, tb);
                    SideFace(outMesh, chunk, lx, ly, lz, block, 5, nL, isC, isL, isTorch, isG, isBed, pad, tr, tg, tb);
                }

                // ---- short grass decoration ----
                if (block == Block.GRASS && !Block.IsOpaque(above)
                    && _world.Generator.ShouldSpawnShortGrass(wx, wz))
                {
                    EmitShortGrass(outMesh, wx, ly, wz);
                }
            }
        }
    }

    private static bool ShouldDrawWaterSide(byte neighbor, float waterH)
    {
        if (Block.IsWater(neighbor)) return Block.GetWaterHeight(neighbor) < waterH;
        return !Block.IsOpaque(neighbor);
    }

    private void SideFace(ChunkMeshData outMesh, World.Chunk chunk, int lx, int ly, int lz,
                          byte block, int fd, byte neighbor,
                          bool isC, bool isL, bool isTorch, bool isG, bool isBed, float pad,
                          float tr, float tg, float tb)
    {
        bool shouldDraw;
        if (isC) shouldDraw = neighbor != Block.CACTUS;
        else if (isL) shouldDraw = neighbor != Block.LEAVES;
        else if (isTorch) shouldDraw = true;
        else shouldDraw = !Block.IsOpaque(neighbor);

        if (!shouldDraw) return;

        float shrink2 = isC ? pad : 0f;
        float hs3 = 1f;
        if (isBed) hs3 = .5625f;
        if (isTorch) { hs3 = .625f; shrink2 = .375f; }

        AddFace(outMesh, chunk, lx, ly, lz, block, fd, hs3, shrink2);

        // Grass sides get a second, tinted overlay quad in the transparent pass.
        if (isG) AddFace(outMesh, chunk, lx, ly, lz, block, fd, 1f, 0f, true, false, tr, tg, tb);
    }

    /// <summary>Reads a block for meshing, crossing into the neighbour chunk when
    /// the local coordinate falls outside this chunk.</summary>
    private byte Neighbour(World.Chunk chunk, int lx, int ly, int lz)
    {
        if (World.Chunk.ContainsLocal(lx, ly, lz)) return chunk.Get(lx, ly, lz);
        return _world.GetBlockForMesh(chunk.OriginX + lx, ly, chunk.OriginZ + lz);
    }

    // --------------------------------------------------------------- addFace

    /// <summary>Port of the <c>addFace</c> closure in chunks.js.</summary>
    private void AddFace(ChunkMeshData outMesh, World.Chunk chunk, int lx, int ly, int lz,
                         byte bt, int fd, float hs = 1f, float shrink = 0f,
                         bool overlay = false, bool overlayTop = false,
                         float tr = 1f, float tg = 1f, float tb = 1f)
    {
        int bX = chunk.OriginX, bZ = chunk.OriginZ;
        float x = bX + lx, y = ly, z = bZ + lz;
        float s = hs, pad = shrink;

        float x0 = x + pad, x1 = x + 1 - pad;
        float y0 = y, y1 = y + s;
        float z0 = z + pad, z1 = z + 1 - pad;

        ref readonly var texDef = ref BlockTextureMap.Get(bt);
        int ti;
        if (overlay)
        {
            ti = overlayTop ? texDef.Top : texDef.Overlay;
            if (ti < 0) ti = texDef.Side;
        }
        else if (fd == 0) ti = texDef.Top;
        else if (fd == 1) ti = texDef.Bottom;
        else if (texDef.HasFront)
        {
            byte f = _world.GetFacing(bX + lx, ly, bZ + lz);
            // frontFaceIdx = [3,2,5,4][f]
            int frontFaceIdx = f switch { 0 => 3, 1 => 2, 2 => 5, _ => 4 };
            ti = fd == frontFaceIdx ? texDef.Front : texDef.Side;
        }
        else ti = texDef.Side;

        float u0 = ti / (float)ATLAS_COLS + UV_INSET / ATLAS_COLS;
        float u1 = (ti + 1) / (float)ATLAS_COLS - UV_INSET / ATLAS_COLS;
        float v0 = UV_INSET, v1 = 1 - UV_INSET;

        // Light is sampled from the cell the face points INTO, so a face against a
        // solid block still picks up the light of the air cell it bounds.
        int sampleLx = lx, sampleLy = ly, sampleLz = lz;
        switch (fd)
        {
            case 0: sampleLy += 1; break;
            case 1: sampleLy -= 1; break;
            case 2: sampleLz += 1; break;
            case 3: sampleLz -= 1; break;
            case 4: sampleLx += 1; break;
            case 5: sampleLx -= 1; break;
        }

        int lightLevel = NeighbourLight(chunk, sampleLx, sampleLy, sampleLz);
        float lightMul = World.Lighting.LightToColor(lightLevel);

        float cr = lightMul * tr, cg = lightMul * tg, cb = lightMul * tb;
        var mb = overlay ? outMesh.Overlay : outMesh.Opaque;

        // uvTL / uvTR / uvBR / uvBL from the JS.
        const int TL = 0, TR = 1, BR = 2, BL = 3;
        float uu(int c) => c is TL or BL ? u0 : u1;
        float vv(int c) => c is TL or TR ? v1 : v0;

        switch (fd)
        {
            case 0:
                mb.Quad(x0, y1, z1, x1, y1, z1, x1, y1, z0, x0, y1, z0,
                        uu(BL), vv(BL), uu(BR), vv(BR), uu(TR), vv(TR), uu(TL), vv(TL),
                        0, 1, 0, cr, cg, cb);
                break;
            case 1:
                mb.Quad(x0, y0, z0, x1, y0, z0, x1, y0, z1, x0, y0, z1,
                        uu(TL), vv(TL), uu(TR), vv(TR), uu(BR), vv(BR), uu(BL), vv(BL),
                        0, -1, 0, cr, cg, cb);
                break;
            case 2:
                mb.Quad(x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1,
                        uu(BL), vv(BL), uu(BR), vv(BR), uu(TR), vv(TR), uu(TL), vv(TL),
                        0, 0, 1, cr, cg, cb);
                break;
            case 3:
                mb.Quad(x1, y0, z0, x0, y0, z0, x0, y1, z0, x1, y1, z0,
                        uu(BL), vv(BL), uu(BR), vv(BR), uu(TR), vv(TR), uu(TL), vv(TL),
                        0, 0, -1, cr, cg, cb);
                break;
            case 4:
                mb.Quad(x1, y0, z1, x1, y0, z0, x1, y1, z0, x1, y1, z1,
                        uu(BL), vv(BL), uu(BR), vv(BR), uu(TR), vv(TR), uu(TL), vv(TL),
                        1, 0, 0, cr, cg, cb);
                break;
            case 5:
                mb.Quad(x0, y0, z0, x0, y0, z1, x0, y1, z1, x0, y1, z0,
                        uu(BL), vv(BL), uu(BR), vv(BR), uu(TR), vv(TR), uu(TL), vv(TL),
                        -1, 0, 0, cr, cg, cb);
                break;
        }
    }

    private int NeighbourLight(World.Chunk chunk, int lx, int ly, int lz)
    {
        if (ly < GameConfig.CHUNK_MIN_Y || ly > GameConfig.CHUNK_MAX_Y) return 0;
        if (World.Chunk.ContainsLocal(lx, ly, lz))
        {
            byte l = chunk.GetLight(lx, ly, lz);
            return Math.Max(l >> 4, l & 0xF);
        }
        return _world.GetLight(chunk.OriginX + lx, ly, chunk.OriginZ + lz);
    }

    // ------------------------------------------------------------ short grass

    /// <summary>Port of the short-grass cross-quad emitter at the end of the JS
    /// meshing loop. Two quads, 0.7 tall, on a 0.5 half-extent, double-sided.</summary>
    private void EmitShortGrass(ChunkMeshData outMesh, int wx, int ly, int wz)
    {
        const float s = 0.5f, h2 = 0.7f;
        float px = wx + 0.5f, py = ly + 1f, pz = wz + 0.5f;

        int ti = BlockTextureMap.Get(Block.SHORT_GRASS).Side;
        float u0 = ti / (float)ATLAS_COLS + UV_INSET / ATLAS_COLS;
        float u1 = (ti + 1) / (float)ATLAS_COLS - UV_INSET / ATLAS_COLS;
        float v0 = UV_INSET, v1 = 1 - UV_INSET;

        int lightLevel = _world.GetLight(wx, ly + 1, wz);
        float lightMul = World.Lighting.LightToColor(lightLevel);

        uint tintHex = _world.Generator.GetGrassColorAt(wx + 0.5, wz + 0.5);
        ChunkGenerator.HexToLinear(tintHex, out float tr, out float tg, out float tb);
        float cr = lightMul * tr, cg = lightMul * tg, cb = lightMul * tb;

        var mb = outMesh.Plants;

        // Diagonal 1
        mb.Quad(px - s, py, pz - s, px + s, py, pz + s, px + s, py + h2, pz + s, px - s, py + h2, pz - s,
                u0, v0, u1, v0, u1, v1, u0, v1, 0, 1, 0, cr, cg, cb);
        // Diagonal 2
        mb.Quad(px + s, py, pz - s, px - s, py, pz + s, px - s, py + h2, pz + s, px + s, py + h2, pz - s,
                u0, v0, u1, v0, u1, v1, u0, v1, 0, 1, 0, cr, cg, cb);
    }

    // ------------------------------------------------------------------ doors

    /// <summary>Port of <c>addDoorBlock</c>: a 0.2-thick slab whose panel texture
    /// depends on facing and open state.</summary>
    private void AddDoorBlock(ChunkMeshData outMesh, int lx, int ly, int lz, int wx, int wz,
                              byte blockId, byte facing, bool isOpen)
    {
        ref readonly var texDef = ref BlockTextureMap.Get(blockId);
        int panelSlot = texDef.Front >= 0 ? texDef.Front : texDef.Side;
        int edgeSlot = texDef.Side;

        const float T = 0.2f;
        float x0 = 0, x1 = 1, z0 = 0, z1 = 1;
        char panelAxis = 'z';

        if (!isOpen)
        {
            switch (facing)
            {
                case 0: z0 = 1 - T; z1 = 1; break;
                case 1: z0 = 0; z1 = T; break;
                case 2: x0 = 1 - T; x1 = 1; break;
                default: x0 = 0; x1 = T; break;
            }
            panelAxis = (facing == 0 || facing == 1) ? 'z' : 'x';
        }
        else
        {
            switch (facing)
            {
                case 0: x0 = 0; x1 = T; break;
                case 1: x0 = 1 - T; x1 = 1; break;
                case 2: z0 = 1 - T; z1 = 1; break;
                default: z0 = 0; z1 = T; break;
            }
            panelAxis = (facing == 0 || facing == 1) ? 'x' : 'z';
        }

        float wx0 = wx + x0, wx1 = wx + x1;
        float wy0 = ly, wy1 = ly + 1f;
        float wz0 = wz + z0, wz1 = wz + z1;

        float lightMul = World.Lighting.LightToColor(_world.GetLight(wx, ly, wz));
        var mb = outMesh.Opaque;

        void SlotUV(int slot, out float a, out float b, out float c, out float d)
        {
            a = slot / (float)ATLAS_COLS + UV_INSET / ATLAS_COLS;
            b = (slot + 1) / (float)ATLAS_COLS - UV_INSET / ATLAS_COLS;
            c = UV_INSET;
            d = 1 - UV_INSET;
        }
        SlotUV(panelSlot, out float pu0, out float pu1, out float pv0, out float pv1);
        SlotUV(edgeSlot, out float eu0, out float eu1, out float ev0, out float ev1);

        void PushQuad(float[] verts, float nu0, float nu1, float nv0, float nv1, float nx, float ny, float nz)
        {
            // Vertex order from the JS pushQuad calls: (v0,v0) (v1,v0) (v1,v1) (v0,v1)
            mb.Quad(verts[0], verts[1], verts[2], verts[3], verts[4], verts[5],
                    verts[6], verts[7], verts[8], verts[9], verts[10], verts[11],
                    nu0, nv0, nu1, nv0, nu1, nv1, nu0, nv1,
                    nx, ny, nz, lightMul, lightMul, lightMul);
        }

        bool panelIsX = panelAxis == 'x';
        float ax0 = panelIsX ? pu0 : eu0, ax1 = panelIsX ? pu1 : eu1;
        float av0 = panelIsX ? pv0 : ev0, av1 = panelIsX ? pv1 : ev1;

        // +X
        PushQuad(new[] { wx1, wy0, wz1, wx1, wy0, wz0, wx1, wy1, wz0, wx1, wy1, wz1 }, ax0, ax1, av0, av1, 1, 0, 0);
        // -X
        PushQuad(new[] { wx0, wy0, wz0, wx0, wy0, wz1, wx0, wy1, wz1, wx0, wy1, wz0 }, ax0, ax1, av0, av1, -1, 0, 0);

        bool panelIsZ = panelAxis == 'z';
        float az0 = panelIsZ ? pu0 : eu0, az1 = panelIsZ ? pu1 : eu1;
        float zv0 = panelIsZ ? pv0 : ev0, zv1 = panelIsZ ? pv1 : ev1;

        // +Z
        PushQuad(new[] { wx0, wy0, wz1, wx1, wy0, wz1, wx1, wy1, wz1, wx0, wy1, wz1 }, az0, az1, zv0, zv1, 0, 0, 1);
        // -Z
        PushQuad(new[] { wx1, wy0, wz0, wx0, wy0, wz0, wx0, wy1, wz0, wx1, wy1, wz0 }, az0, az1, zv0, zv1, 0, 0, -1);
        // +Y and -Y always use the edge texture
        PushQuad(new[] { wx0, wy1, wz1, wx1, wy1, wz1, wx1, wy1, wz0, wx0, wy1, wz0 }, eu0, eu1, ev0, ev1, 0, 1, 0);
        PushQuad(new[] { wx0, wy0, wz0, wx1, wy0, wz0, wx1, wy0, wz1, wx0, wy0, wz1 }, eu0, eu1, ev0, ev1, 0, -1, 0);
    }
}
