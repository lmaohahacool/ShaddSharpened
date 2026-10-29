using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Engine.World;

/// <summary>
/// Port of <c>dist/worldgen.js</c>.
/// </summary>
/// <remarks>
/// Structurally different from the JS in one deliberate way: the JS exposed
/// <c>queryBlock(x,y,z)</c> which recomputed terrain on demand and memoised caves,
/// ores, trees and cacti in four process-lifetime <c>Map</c> caches that were never
/// evicted. This class instead <em>bakes</em> a chunk into its flat array via
/// <see cref="PopulateChunk"/>, using generation-scoped scratch data that is
/// released when the chunk is done. Memory is then bounded by loaded chunks rather
/// than by how far the player has ever travelled.
/// </remarks>
public sealed class ChunkGenerator
{
    private readonly int _seed;
    private int _biomeBlend;
    private readonly int? _spawnBiome;

    // Scratch, reused across PopulateChunk calls to avoid per-chunk allocation.
    // Bounded on purpose: MIGRATION-BRIEF.md 1.1 flags the JS engine's four
    // never-evicted Map caches as a leak, so these are capped and cleared.
    private const int CACHE_LIMIT = 1 << 16;
    private readonly Dictionary<int, int> _heightCache = new();
    private readonly Dictionary<int, int> _biomeCache = new();
    private readonly Dictionary<int, byte> _oreCells = new();
    private readonly Dictionary<int, byte> _treeCells = new();
    private readonly Dictionary<int, byte> _cactusCells = new();
    private int _cellsCX = int.MinValue, _cellsCZ = int.MinValue;

    public ChunkGenerator(int seed, int biomeBlend = 4, int? spawnBiome = null)
    {
        _seed = seed;
        _biomeBlend = biomeBlend;
        _spawnBiome = spawnBiome;
    }

    public int Seed => _seed;
    public int BiomeBlend => _biomeBlend;

    /// <summary>
    /// Changes the grass-tint blend radius at runtime. <c>setBiomeBlend()</c> in worldgen.js
    /// did the same thing; only <see cref="GetGrassColorAt"/> reads the value, so callers
    /// just need to re-mesh (see <c>VoxelWorld.SetBiomeBlend</c>).
    /// </summary>
    public void SetBiomeBlend(int value) => _biomeBlend = value < 0 ? 0 : value;
    public int? SpawnBiome => _spawnBiome;

    /// <summary>Packs a chunk-local coordinate triple into one int key.</summary>
    private static int LocalKey(int lx, int ly, int lz)
        => ((ly - GameConfig.CHUNK_MIN_Y) << 8) | (lz << 4) | lx;

    private static int ColumnKey(int x, int z)
    {
        // Spatial hash of a world column; collisions are impossible within the
        // +/- 2^15 range a chunk generation ever touches.
        return ((x + 32768) << 16) | (z + 32768) & 0xFFFF;
    }

    private int GetHeightCached(int x, int z)
    {
        int k = ColumnKey(x, z);
        if (_heightCache.TryGetValue(k, out int h)) return h;
        if (_heightCache.Count >= CACHE_LIMIT) _heightCache.Clear();
        h = GetHeight(x, z);
        _heightCache[k] = h;
        return h;
    }

    /// <summary>
    /// Biome lookup with the same bounded caching as height. This matters a lot:
    /// <see cref="GetBaseBlock"/> needs the biome for every voxel in a column, and
    /// <c>GetBiomeRaw</c> runs three <c>fbm</c> calls (eleven <c>hash2</c> samples).
    /// Caching it per column is what keeps chunk generation in the millisecond range.
    /// </summary>
    private int GetBiomeCached(int x, int z)
    {
        int k = ColumnKey(x, z);
        if (_biomeCache.TryGetValue(k, out int b)) return b;
        if (_biomeCache.Count >= CACHE_LIMIT) _biomeCache.Clear();
        b = GetBiomeRaw(x, z);
        _biomeCache[k] = b;
        return b;
    }

    /// <summary>Drops the scratch caches. Called between chunks so long sessions do
    /// not accumulate state the way the JS caches did.</summary>
    public void ClearCaches() { _heightCache.Clear(); _biomeCache.Clear(); }

    // ---------------------------------------------------------------- biome

    /// <summary>Port of <c>getBiomeRaw</c>.</summary>
    public int GetBiomeRaw(int x, int z)
    {
        // URL ?spawn= override within a 400x400 box around the origin.
        if (_spawnBiome.HasValue && Math.Abs(x) < 200 && Math.Abs(z) < 200)
            return _spawnBiome.Value;

        double bn = Noise.Fbm(x * .0025, z * .0025, _seed, 3);
        int b;
        if (bn < .32) b = Biome.DESERT;
        else if (bn > .62) b = Biome.FOREST;
        else if (bn < .45) b = Biome.SNOWY;
        else b = Biome.PLAINS;

        double rh = Noise.Fbm(x * .008, z * .008, _seed, 5) * 30 - 10;
        if (rh >= 22) b = Biome.MOUNTAIN;

        double og = Noise.Fbm(x * .0018 + 500, z * .0018 + 500, _seed, 3);
        if (b == Biome.FOREST && og > .68) b = Biome.OLD_GROWTH;
        return b;
    }

    public int GetBiome(int x, int z) => GetBiomeRaw(x, z);

    // --------------------------------------------------------------- height

    /// <summary>Port of <c>getHeight</c>: desert/plain/normal/forest blending over
    /// a ridged-noise base.</summary>
    public int GetHeight(int x, int z)
    {
        double bn = Noise.Fbm(x * .0025, z * .0025, _seed, 3);
        double bse = Noise.Fbm(x * .008, z * .008, _seed, 5);
        double ridge = Noise.Fbm(x * .02, z * .02, _seed, 3);
        double rp = Math.Pow(ridge, 2.5);

        double nh = bse * 30 + rp * 25 - 12;
        double dh = 3 + bse * 3;
        double fh = nh * .85;

        double h;
        if (bn < .25) h = dh;
        else if (bn < .4) { double t = Noise.Smoothstep((bn - .25) / .15); h = dh * (1 - t) + nh * t; }
        else if (bn < .55) h = nh;
        else if (bn < .7) { double t = Noise.Smoothstep((bn - .55) / .15); h = nh * (1 - t) + fh * t; }
        else h = fh;

        return (int)Math.Floor(h);
    }

    // ---------------------------------------------------------- grass colour

    /// <summary>Port of <c>getGrassColorAt</c>, including three.js's colour-space
    /// behaviour: <c>Color.setHex</c> converts sRGB to linear, <c>lerp</c> blends in
    /// linear space, and <c>getHex</c> converts back.</summary>
    public uint GetGrassColorAt(double x, double z)
    {
        int raw = GetBiomeRaw((int)Math.Floor(x), (int)Math.Floor(z));
        if (_biomeBlend <= 0) return Biome.GrassColor(raw);

        int blend = _biomeBlend;
        int wx = (int)Math.Floor(x / blend), wz = (int)Math.Floor(z / blend);
        double fx = (x - wx * blend) / (double)blend;
        double fz = (z - wz * blend) / (double)blend;

        Span<double> c1 = stackalloc double[3];
        Span<double> c2 = stackalloc double[3];
        Span<double> tmp = stackalloc double[3];

        SetHexLinear(c1, Biome.GrassColor(GetBiomeRaw(wx * blend, wz * blend)));
        SetHexLinear(c2, Biome.GrassColor(GetBiomeRaw((wx + 1) * blend, wz * blend)));
        Lerp(c1, c2, fx);

        SetHexLinear(c2, Biome.GrassColor(GetBiomeRaw(wx * blend, (wz + 1) * blend)));
        SetHexLinear(tmp, Biome.GrassColor(GetBiomeRaw((wx + 1) * blend, (wz + 1) * blend)));
        Lerp(c2, tmp, fx);

        Lerp(c1, c2, fz);
        return GetHexFromLinear(c1);
    }

    private static void SetHexLinear(Span<double> dst, uint hex)
    {
        dst[0] = SrgbToLinear(((hex >> 16) & 0xFF) / 255.0);
        dst[1] = SrgbToLinear(((hex >> 8) & 0xFF) / 255.0);
        dst[2] = SrgbToLinear((hex & 0xFF) / 255.0);
    }

    private static void Lerp(Span<double> a, ReadOnlySpan<double> b, double t)
    {
        a[0] += (b[0] - a[0]) * t;
        a[1] += (b[1] - a[1]) * t;
        a[2] += (b[2] - a[2]) * t;
    }

    private static uint GetHexFromLinear(ReadOnlySpan<double> c)
    {
        int r = (int)Math.Round(Math.Clamp(LinearToSrgb(c[0]), 0, 1) * 255);
        int g = (int)Math.Round(Math.Clamp(LinearToSrgb(c[1]), 0, 1) * 255);
        int b = (int)Math.Round(Math.Clamp(LinearToSrgb(c[2]), 0, 1) * 255);
        return ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    /// <summary>Delegates to <see cref="Colour"/>; kept here so worldgen call sites
    /// read naturally.</summary>
    public static double SrgbToLinear(double c) => Colour.SrgbToLinear(c);

    public static double LinearToSrgb(double c) => Colour.LinearToSrgb(c);

    /// <summary>Converts an sRGB hex colour to linear components, as three.js
    /// vertex colours expect. Used by the mesher for the grass tint.</summary>
    public static void HexToLinear(uint hex, out float r, out float g, out float b)
        => Colour.HexToLinear(hex, out r, out g, out b);

    // ---------------------------------------------------------------- caves

    /// <summary>
    /// Port of the cave predicate that <c>buildCaveMap</c> + <c>isCave</c> expressed
    /// as a precomputed <c>Set</c> in worldgen.js.
    /// </summary>
    /// <remarks>
    /// The JS built a per-chunk <c>Set</c> of cave cells and cached it for the life of
    /// the process. Evaluating the predicate directly is arithmetically identical —
    /// same <c>fbm3D</c> call, same depth factor, same threshold, same y-range guards
    /// — but it removes three costs: the set allocation, one insert per candidate
    /// cell, and (the expensive one) rebuilding a *neighbouring* chunk's entire cave
    /// set whenever tree or cactus placement sampled a column across a chunk border.
    /// Verified equivalent by the column-parity test, which compares every stored
    /// voxel against the real JS <c>queryBlock</c>.
    /// </remarks>
    public bool IsCave(int x, int y, int z)
    {
        // isCave's first guard, and the lower bound of buildCaveMap's y loop.
        if (y <= GameConfig.WORLD_BOTTOM + 1) return false;

        int gh = GetHeightCached(x, z);
        // buildCaveMap's loop ran `for (y = WORLD_BOTTOM + 2; y < gh - 2)`.
        if (y >= gh - 2) return false;

        double depthFactor = Math.Min(1, Math.Max(0, 2 - Math.Abs(y + 25) / 15.0));
        if (depthFactor <= 0) return false;

        double n = Noise.Fbm3D(x * .08, y * .08, z * .08, _seed, 3);
        double threshold = 0.68 - 0.10 * depthFactor;
        return n > threshold;
    }

    private int _currentCX, _currentCZ;

    // ----------------------------------------------------------------- ores

    /// <summary>Port of <c>buildOreBlocks</c> + <c>growVein</c>: random-walk veins of
    /// coal and iron, driven entirely by hash3.</summary>
    private void BuildOreBlocks(int cx, int cz)
    {
        _oreCells.Clear();
        int bX = cx * GameConfig.CHUNK_SIZE, bZ = cz * GameConfig.CHUNK_SIZE;

        int coalCount = 4 + (int)(Noise.Hash3(cx, cz, 101, _seed) * 3);
        for (int v = 0; v < coalCount; v++)
        {
            int sx = 4 + (int)(Noise.Hash3(cx, cz, 200 + v, _seed) * 8);
            int sz = 4 + (int)(Noise.Hash3(cx, cz, 300 + v, _seed) * 8);
            int sy = -25 + (int)(Noise.Hash3(cx, cz, 400 + v, _seed) * 33);
            int size = 4 + (int)(Noise.Hash3(cx, cz, 500 + v, _seed) * 9);
            GrowVein(bX, bZ, sx, sy, sz, size, Block.COAL_ORE, cx, cz, 600 + v);
        }

        int ironCount = 4 + (int)(Noise.Hash3(cx, cz, 102, _seed) * 4);
        for (int v = 0; v < ironCount; v++)
        {
            int sx = 4 + (int)(Noise.Hash3(cx, cz, 700 + v, _seed) * 8);
            int sz = 4 + (int)(Noise.Hash3(cx, cz, 800 + v, _seed) * 8);
            int sy = -25 + (int)(Noise.Hash3(cx, cz, 900 + v, _seed) * 40);
            int size = 4 + (int)(Noise.Hash3(cx, cz, 1000 + v, _seed) * 6);
            GrowVein(bX, bZ, sx, sy, sz, size, Block.IRON_ORE, cx, cz, 1100 + v);
        }
    }

    private void GrowVein(int bX, int bZ, int sx, int sy, int sz, int size, byte oreId, int cx, int cz, int seed)
    {
        int x = sx, y = sy, z = sz;
        for (int i = 0; i < size; i++)
        {
            if (x >= 0 && x < GameConfig.CHUNK_SIZE && z >= 0 && z < GameConfig.CHUNK_SIZE
                && y > GameConfig.WORLD_BOTTOM && y < 64)
            {
                int wx = bX + x, wz = bZ + z;
                int gh = GetHeightCached(wx, wz);
                // JS writes when y < gh-3 AND y < gh-1; the first is strictly stronger.
                if (y < gh - 3 && y < gh - 1) _oreCells[LocalKey(x, y, z)] = oreId;
            }

            double d = Noise.Hash3(cx * 100 + i, cz * 100 + i, seed + i, _seed) * 6;
            int dir = (int)Math.Floor(d);
            if (dir == 0) x++;
            else if (dir == 1) x--;
            else if (dir == 2) y++;
            else if (dir == 3) y--;
            else if (dir == 4) z++;
            else z--;
        }
    }

    // ---------------------------------------------------------------- trees

    private static float GetTreeDensity(int biome) => biome switch
    {
        Biome.FOREST => .9f,
        Biome.OLD_GROWTH => 1.4f,
        Biome.PLAINS => .4f,
        Biome.MOUNTAIN => .05f,
        Biome.DESERT => 0f,
        Biome.SNOWY => 0f,
        _ => .3f,
    };

    private readonly struct TreeSpec
    {
        public TreeSpec(int x, int z, int groundY, int trunk, int canopy, bool isOld)
        { TreeX = x; TreeZ = z; GroundY = groundY; TrunkHeight = trunk; CanopyRadius = canopy; IsOld = isOld; }
        public int TreeX { get; } public int TreeZ { get; } public int GroundY { get; }
        public int TrunkHeight { get; } public int CanopyRadius { get; } public bool IsOld { get; }
    }

    /// <summary>Port of <c>getTreeForCell</c>.</summary>
    private TreeSpec? GetTreeForCell(int cx, int cz)
    {
        int sx = cx * GameConfig.TREE_GRID + GameConfig.TREE_GRID / 2;
        int sz = cz * GameConfig.TREE_GRID + GameConfig.TREE_GRID / 2;
        int biome = GetBiome(sx, sz);
        if (Noise.Hash3(cx, cz, 42, _seed) > GetTreeDensity(biome)) return null;

        int ox = (int)(Noise.Hash3(cx, cz, 101, _seed) * GameConfig.TREE_GRID);
        int oz = (int)(Noise.Hash3(cx, cz, 102, _seed) * GameConfig.TREE_GRID);
        int tx = cx * GameConfig.TREE_GRID + ox;
        int tz = cz * GameConfig.TREE_GRID + oz;

        int gy = GetHeightCached(tx, tz);
        if (gy <= GameConfig.SEA_LEVEL) return null;
        if (GetBaseBlock(tx, gy, tz) != Block.GRASS) return null;
        if (IsCave(tx, gy, tz) || IsCave(tx, gy + 1, tz)) return null;

        int th, cr;
        if (biome == Biome.OLD_GROWTH)
        {
            th = 9 + (int)(Noise.Hash3(tx, tz, 1, _seed) * 5);
            cr = 3 + (int)(Noise.Hash3(tx, tz, 3, _seed) * 3);
        }
        else if (biome == Biome.FOREST)
        {
            th = 5 + (int)(Noise.Hash3(tx, tz, 1, _seed) * 4);
            cr = 2 + (int)(Noise.Hash3(tx, tz, 3, _seed) * 2);
        }
        else
        {
            th = 4 + (int)(Noise.Hash3(tx, tz, 1, _seed) * 3);
            cr = 2 + (int)(Noise.Hash3(tx, tz, 3, _seed) * 2);
        }
        return new TreeSpec(tx, tz, gy, th, cr, biome == Biome.OLD_GROWTH);
    }

    /// <summary>Port of <c>buildTreeBlocks</c>: writes trunk and canopy into local space.</summary>
    private void BuildTreeBlocks(int cx, int cz)
    {
        _treeCells.Clear();
        int bX = cx * GameConfig.CHUNK_SIZE, bZ = cz * GameConfig.CHUNK_SIZE;
        const int CS = GameConfig.CHUNK_SIZE;

        int mcX0 = JsMath.FloorDiv(bX - 8, GameConfig.TREE_GRID);
        int mcX1 = JsMath.FloorDiv(bX + CS + 8, GameConfig.TREE_GRID);
        int mcZ0 = JsMath.FloorDiv(bZ - 8, GameConfig.TREE_GRID);
        int mcZ1 = JsMath.FloorDiv(bZ + CS + 8, GameConfig.TREE_GRID);

        for (int cx2 = mcX0; cx2 <= mcX1; cx2++)
        for (int cz2 = mcZ0; cz2 <= mcZ1; cz2++)
        {
            var t = GetTreeForCell(cx2, cz2);
            if (t is null) continue;
            int treeX = t.Value.TreeX, treeZ = t.Value.TreeZ, groundY = t.Value.GroundY;
            int trunkHeight = t.Value.TrunkHeight, canopyRadius = t.Value.CanopyRadius;

            for (int ty = 1; ty <= trunkHeight; ty++)
            {
                int lx = treeX - bX, lz = treeZ - bZ, ly = groundY + ty;
                if (lx >= 0 && lx < CS && lz >= 0 && lz < CS)
                    _treeCells[LocalKey(lx, ly, lz)] = Block.LOG;
            }

            int ccY = groundY + trunkHeight - 1;
            for (int ly = ccY - 1; ly <= ccY + 2; ly++)
            for (int dx = -canopyRadius; dx <= canopyRadius; dx++)
            for (int dz = -canopyRadius; dz <= canopyRadius; dz++)
            {
                int lx = treeX - bX + dx, lz = treeZ - bZ + dz;
                if (lx < 0 || lx >= CS || lz < 0 || lz >= CS) continue;

                int key = LocalKey(lx, ly, lz);
                if (_treeCells.TryGetValue(key, out var existing) && existing == Block.LOG) continue;

                int dSq = dx * dx + dz * dz + (ly - ccY) * (ly - ccY);
                if (dSq <= canopyRadius * canopyRadius + 2
                    && Noise.Hash3(lx + bX, ly, lz + bZ, _seed) < .9
                    && !_treeCells.ContainsKey(key))
                {
                    _treeCells[key] = Block.LEAVES;
                }
            }
        }
    }

    // -------------------------------------------------------------- cactus

    private readonly struct CactusSpec
    {
        public CactusSpec(int x, int z, int groundY, int height)
        { CactusX = x; CactusZ = z; GroundY = groundY; Height = height; }
        public int CactusX { get; } public int CactusZ { get; } public int GroundY { get; } public int Height { get; }
    }

    /// <summary>Port of <c>getCactusForCell</c>.</summary>
    private CactusSpec? GetCactusForCell(int cx, int cz)
    {
        int sx = cx * GameConfig.CACTUS_GRID + GameConfig.CACTUS_GRID / 2;
        int sz = cz * GameConfig.CACTUS_GRID + GameConfig.CACTUS_GRID / 2;
        if (GetBiome(sx, sz) != Biome.DESERT) return null;
        if (Noise.Hash3(cx, cz, 77, _seed) > GameConfig.CACTUS_CHANCE) return null;

        int ox = (int)(Noise.Hash3(cx, cz, 201, _seed) * GameConfig.CACTUS_GRID);
        int oz = (int)(Noise.Hash3(cx, cz, 202, _seed) * GameConfig.CACTUS_GRID);
        int ctx = cx * GameConfig.CACTUS_GRID + ox;
        int ctz = cz * GameConfig.CACTUS_GRID + oz;

        int gy = GetHeightCached(ctx, ctz);
        if (gy <= GameConfig.SEA_LEVEL) return null;
        if (GetBaseBlock(ctx, gy, ctz) != Block.SAND) return null;
        if (IsCave(ctx, gy, ctz) || IsCave(ctx, gy + 1, ctz)) return null;

        int h = 1 + (int)(Noise.Hash3(ctx, ctz, 88, _seed) * 3);
        return new CactusSpec(ctx, ctz, gy, h);
    }

    /// <summary>Port of <c>buildCactusBlocks</c>.</summary>
    private void BuildCactusBlocks(int cx, int cz)
    {
        _cactusCells.Clear();
        int bX = cx * GameConfig.CHUNK_SIZE, bZ = cz * GameConfig.CHUNK_SIZE;
        const int CS = GameConfig.CHUNK_SIZE;

        int mcX0 = JsMath.FloorDiv(bX - 6, GameConfig.CACTUS_GRID);
        int mcX1 = JsMath.FloorDiv(bX + CS + 6, GameConfig.CACTUS_GRID);
        int mcZ0 = JsMath.FloorDiv(bZ - 6, GameConfig.CACTUS_GRID);
        int mcZ1 = JsMath.FloorDiv(bZ + CS + 6, GameConfig.CACTUS_GRID);

        for (int cx2 = mcX0; cx2 <= mcX1; cx2++)
        for (int cz2 = mcZ0; cz2 <= mcZ1; cz2++)
        {
            var c = GetCactusForCell(cx2, cz2);
            if (c is null) continue;
            int cactusX = c.Value.CactusX, cactusZ = c.Value.CactusZ;
            int groundY = c.Value.GroundY, height = c.Value.Height;

            for (int ty = 1; ty <= height; ty++)
            {
                int lx = cactusX - bX, lz = cactusZ - bZ, ly = groundY + ty;
                if (lx >= 0 && lx < CS && lz >= 0 && lz < CS)
                    _cactusCells[LocalKey(lx, ly, lz)] = Block.CACTUS;
            }
        }
    }

    // ---------------------------------------------------------- short grass

    /// <summary>Port of <c>shouldSpawnShortGrass</c>. Evaluated at mesh time, exactly
    /// as in JS — short grass is decorative geometry, never a stored block.</summary>
    public bool ShouldSpawnShortGrass(int x, int z)
    {
        int biome = GetBiomeRaw(x, z);
        int h = GetHeightCached(x, z);
        if (h <= GameConfig.SEA_LEVEL) return false;
        if (GetBaseBlock(x, h, z) != Block.GRASS) return false;

        double chance = biome switch
        {
            Biome.PLAINS => 0.3,
            Biome.FOREST => 0.35,
            Biome.OLD_GROWTH => 0.25,
            _ => -1,
        };
        if (chance < 0) return false;

        double patch = Noise.Fbm(x * 0.15, z * 0.15, _seed + 9001, 2);
        if (patch < 0.4) return false;

        double r = Noise.Hash3(x, 0, z, _seed + 7777);
        return r < chance;
    }

    // ----------------------------------------------------------- base block

    /// <summary>Port of <c>getBaseBlock</c>. Cave and ore lookups must already be
    /// populated for the chunk containing (x,z).</summary>
    public byte GetBaseBlock(int x, int y, int z)
    {
        int height = GetHeightCached(x, z);
        int biome = GetBiomeCached(x, z);

        if (y > height)
        {
            if (y <= GameConfig.SEA_LEVEL)
            {
                if (biome == Biome.SNOWY && y == GameConfig.SEA_LEVEL) return Block.ICE;
                return Block.WATER;
            }
            return Block.AIR;
        }

        int cx = JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE);
        int cz = JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE);
        int lx = x - cx * GameConfig.CHUNK_SIZE;
        int lz = z - cz * GameConfig.CHUNK_SIZE;

        if (y < height - 2 && IsCave(x, y, z)) return Block.AIR;

        if (y < height - 3 && _oreCells.TryGetValue(LocalKey(lx, y, lz), out byte ore)) return ore;

        bool isOld = biome == Biome.OLD_GROWTH;

        if (y == height)
        {
            if (biome == Biome.SNOWY)
            {
                if (height < GameConfig.SEA_LEVEL) return Block.ICE;
                return Block.SNOW;
            }
            if (height < GameConfig.SEA_LEVEL && biome != Biome.SNOWY) return Block.SAND;
            if (biome == Biome.DESERT) return Block.SAND;
            if (biome == Biome.FOREST) return height >= 24 ? Block.STONE : Block.GRASS;
            if (isOld) return height >= 24 ? Block.STONE : Block.GRASS;
            if (biome == Biome.MOUNTAIN) return height >= 28 ? Block.SNOW : Block.STONE;

            bool nw = biome != Biome.SNOWY &&
                      (GetHeightCached(x + 3, z) <= GameConfig.SEA_LEVEL ||
                       GetHeightCached(x - 3, z) <= GameConfig.SEA_LEVEL ||
                       GetHeightCached(x, z + 3) <= GameConfig.SEA_LEVEL ||
                       GetHeightCached(x, z - 3) <= GameConfig.SEA_LEVEL);

            if (biome != Biome.SNOWY)
            {
                if (height <= GameConfig.SEA_LEVEL + 1 || nw) return Block.SAND;
                if (height >= 28) return Block.SNOW;
                if (height >= 20) return Block.STONE;
            }
            return Block.GRASS;
        }

        if (y >= height - 3)
        {
            if (biome == Biome.SNOWY) return Block.DIRT;
            if (isOld) return Block.DIRT_DARK;
            if (height < GameConfig.SEA_LEVEL && biome != Biome.SNOWY) return Block.SAND;
            if (biome == Biome.DESERT) return Block.SAND;
            if (biome == Biome.MOUNTAIN) return Block.STONE;
            if (height >= 20) return Block.STONE;
            return Block.DIRT;
        }

        return Block.STONE;
    }

    // ----------------------------------------------------------- population
    /// <summary>
    /// Builds the ore/tree/cactus cell maps for an arbitrary chunk coordinate and points the
    /// per-column helpers at it. worldgen.js answers <c>queryBlock</c> for any coordinate from
    /// cached per-chunk maps; this is the same facility, so meshing a chunk whose neighbour is
    /// not stored yet can still ask what would be there instead of guessing.
    /// </summary>
    private void EnsureCellMaps(int cx, int cz)
    {
        if (_cellsCX == cx && _cellsCZ == cz) return;
        _heightCache.Clear();
        _biomeCache.Clear();
        _currentCX = cx;
        _currentCZ = cz;
        BuildOreBlocks(cx, cz);
        BuildTreeBlocks(cx, cz);
        BuildCactusBlocks(cx, cz);
        _cellsCX = cx;
        _cellsCZ = cz;
    }

    /// <summary>Port of <c>queryBlock</c>: the procedural block at any world coordinate.</summary>
    public byte QueryBlock(int x, int y, int z)
    {
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return Block.AIR;
        int cx = JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE);
        int cz = JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE);
        EnsureCellMaps(cx, cz);
        int key = LocalKey(x - cx * GameConfig.CHUNK_SIZE, y, z - cz * GameConfig.CHUNK_SIZE);
        // queryBlock precedence: cactus beats tree beats base.
        if (_cactusCells.TryGetValue(key, out byte cac)) return cac;
        if (_treeCells.TryGetValue(key, out byte tre)) return tre;
        int baseTop = Math.Max(GetHeightCached(x, z), GameConfig.SEA_LEVEL);
        if (baseTop > GameConfig.CHUNK_MAX_Y) baseTop = GameConfig.CHUNK_MAX_Y;
        return y <= baseTop ? GetBaseBlock(x, y, z) : Block.AIR;
    }


    /// <summary>
    /// Fills <paramref name="chunk"/> with generated terrain. This is where the
    /// JavaScript engine's lazy <c>queryBlock</c> becomes actual stored voxels.
    /// </summary>
    public void PopulateChunk(Chunk chunk)
    {
        EnsureCellMaps(chunk.CX, chunk.CZ);

        int bX = chunk.OriginX, bZ = chunk.OriginZ;

        for (int lx = 0; lx < GameConfig.CHUNK_SIZE; lx++)
        for (int lz = 0; lz < GameConfig.CHUNK_SIZE; lz++)
        {
            int wx = bX + lx, wz = bZ + lz;

            // getBaseBlock returns AIR for every y above max(height, SEA_LEVEL):
            // water fills height+1..SEA_LEVEL and nothing exists above that. Skipping
            // those cells avoids ~60-90 GetBaseBlock calls per column, which is the
            // difference between milliseconds and hundreds of milliseconds per chunk.
            int baseTop = Math.Max(GetHeightCached(wx, wz), GameConfig.SEA_LEVEL);
            if (baseTop > GameConfig.CHUNK_MAX_Y) baseTop = GameConfig.CHUNK_MAX_Y;

            for (int ly = GameConfig.CHUNK_MIN_Y; ly <= GameConfig.CHUNK_MAX_Y; ly++)
            {
                byte id = ly <= baseTop ? GetBaseBlock(wx, ly, wz) : Block.AIR;

                int key = LocalKey(lx, ly, lz);
                // queryBlock precedence: cactus beats tree beats base.
                if (_cactusCells.TryGetValue(key, out byte cac)) id = cac;
                else if (_treeCells.TryGetValue(key, out byte tre)) id = tre;

                chunk.SetRaw(lx, ly, lz, id);
            }
        }

        chunk.RecomputeOccupancy();
        chunk.IsGenerated = true;
        chunk.MeshDirty = true;
        chunk.LightDirty = true;

        _heightCache.Clear();
        _biomeCache.Clear();
        _oreCells.Clear();
        _cellsCX = int.MinValue;
        _treeCells.Clear();
        _cactusCells.Clear();
    }
}
