using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Engine.World;

/// <summary>
/// Port of <c>dist/lighting.js</c>: BFS flood-fill light propagation.
/// </summary>
/// <remarks>
/// <para>
/// Faithful-port note worth knowing: the JavaScript engine has <em>no skylight
/// pass</em>. Nothing ever calls <c>setLight</c> except <c>addLightAt</c> from a
/// placed torch (mining.js:224), so <c>getLight</c> returns 0 for the whole world
/// and the mesher's <c>lightToColor</c> maps that to a flat 0.5 multiplier — the
/// entire game renders at half brightness, with torches brightening up to ~0.97.
/// That is the real look of the JS build, so it is the default here.
/// </para>
/// <para>
/// <see cref="EnableSkylight"/> turns on a genuine top-down sky pass as an opt-in
/// improvement. It changes appearance, so it is off during Phase A parity work.
/// </para>
/// <para>
/// Storage packs both channels into one byte per cell (high nibble sky, low nibble
/// block) so the array is the same size the JS used while leaving room for the sky
/// channel the JS never populated.
/// </para>
/// </remarks>
public sealed class Lighting
{
    public const int MAX_LIGHT = GameConfig.MAX_LIGHT;   // 15
    public const int TORCH_LIGHT = GameConfig.TORCH_LIGHT; // 14

    /// <summary>Opt-in sky light. Off = pixel-identical to the JS engine.</summary>
    public bool EnableSkylight { get; set; } = false;

    private readonly VoxelWorld _world;

    // Six face neighbours, same order as DIRS in lighting.js.
    private static readonly int[] DirX = { 1, -1, 0, 0, 0, 0 };
    private static readonly int[] DirY = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] DirZ = { 0, 0, 1, -1, 0, 0 };

    public Lighting(VoxelWorld world) { _world = world; }

    /// <summary>Port of <c>getLight</c>: the single 0..15 value the mesher samples.</summary>
    public int GetLight(int x, int y, int z) => _world.GetLight(x, y, z);

    /// <summary>Port of <c>lightToColor</c> in chunks.js: <c>0.5 + 0.5 * (level/15)</c>.</summary>
    public static float LightToColor(int level) => 0.5f + 0.5f * (level / 15f);

    // ------------------------------------------------------------ block light

    /// <summary>Port of <c>addLightAt</c>: BFS outward while the neighbour is dimmer
    /// and light-transparent.</summary>
    public void AddLightAt(int x, int y, int z, int level = TORCH_LIGHT)
    {
        var queue = new Queue<(int x, int y, int z, int lvl)>();
        if (GetBlockLight(x, y, z) >= level) return;
        SetBlockLight(x, y, z, level);
        queue.Enqueue((x, y, z, level));

        while (queue.Count > 0)
        {
            var (cx, cy, cz, current) = queue.Dequeue();
            if (current <= 1) continue;
            int next = current - 1;

            for (int d = 0; d < 6; d++)
            {
                int nx = cx + DirX[d], ny = cy + DirY[d], nz = cz + DirZ[d];
                if (ny < GameConfig.CHUNK_MIN_Y || ny > GameConfig.CHUNK_MAX_Y) continue;

                if (GetBlockLight(nx, ny, nz) >= next) continue;
                if (!Block.IsLightTransparent(_world.GetBlock(nx, ny, nz))) continue;

                SetBlockLight(nx, ny, nz, next);
                queue.Enqueue((nx, ny, nz, next));
            }
        }
    }

    /// <summary>
    /// Port of <c>removeLightAt</c>: pull light back out, collecting cells that were
    /// lit from elsewhere so they can be re-flooded afterwards.
    /// </summary>
    public void RemoveLightAt(int x, int y, int z)
    {
        int startLevel = GetBlockLight(x, y, z);
        if (startLevel == 0) return;
        SetBlockLight(x, y, z, 0);

        var removeQueue = new Queue<(int x, int y, int z, int lvl)>();
        var readdQueue = new List<(int x, int y, int z, int lvl)>();
        removeQueue.Enqueue((x, y, z, startLevel));

        while (removeQueue.Count > 0)
        {
            var (cx, cy, cz, current) = removeQueue.Dequeue();
            for (int d = 0; d < 6; d++)
            {
                int nx = cx + DirX[d], ny = cy + DirY[d], nz = cz + DirZ[d];
                if (ny < GameConfig.CHUNK_MIN_Y || ny > GameConfig.CHUNK_MAX_Y) continue;

                int nLight = GetBlockLight(nx, ny, nz);
                if (nLight == 0) continue;

                if (nLight < current)
                {
                    SetBlockLight(nx, ny, nz, 0);
                    removeQueue.Enqueue((nx, ny, nz, nLight));
                }
                else
                {
                    readdQueue.Add((nx, ny, nz, nLight));
                }
            }
        }

        foreach (var (rx, ry, rz, rLevel) in readdQueue) AddLightAt(rx, ry, rz, rLevel);
    }

    // -------------------------------------------------------------- sky light

    /// <summary>
    /// Top-down sky attenuation for one chunk: full brightness above the highest
    /// opaque block, then -1 per step through transparent cells. Not present in the
    /// JS engine; see the class remarks.
    /// </summary>
    public void ComputeSkylightForChunk(Chunk chunk)
    {
        if (!EnableSkylight) return;

        for (int lx = 0; lx < GameConfig.CHUNK_SIZE; lx++)
        for (int lz = 0; lz < GameConfig.CHUNK_SIZE; lz++)
        {
            int level = MAX_LIGHT;
            for (int ly = GameConfig.CHUNK_MAX_Y; ly >= GameConfig.CHUNK_MIN_Y; ly--)
            {
                byte b = chunk.Get(lx, ly, lz);
                if (Block.IsOpaque(b)) level = 0;
                else if (level > 0 && b != Block.AIR) level = Math.Max(0, level - 1);

                int sky = chunk.GetSkyLight(lx, ly, lz);
                if (sky != level) chunk.SetLightComponents(lx, ly, lz, level, chunk.GetBlockLight(lx, ly, lz));
            }
        }
    }

    // ------------------------------------------------------------- accessors

    private Chunk? ChunkAt(int x, int z) => _world.GetChunkAt(x, z);

    public int GetBlockLight(int x, int y, int z)
    {
        var c = ChunkAt(x, z);
        if (c is null) return 0;
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return 0;
        return c.GetBlockLight(x - c.OriginX, y, z - c.OriginZ);
    }

    public void SetBlockLight(int x, int y, int z, int value)
    {
        var c = ChunkAt(x, z);
        if (c is null) return;
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return;
        int lx = x - c.OriginX, lz = z - c.OriginZ;
        c.SetLightComponents(lx, y, lz, c.GetSkyLight(lx, y, lz), value);
    }

    /// <summary>
    /// Port of <c>getAffectedChunks</c>: the 5x5 chunk area around an edit, which is
    /// 80x80 blocks — enough to cover a torch's 14-block reach.
    /// </summary>
    public IEnumerable<(int cx, int cz)> GetAffectedChunks(int x, int y, int z)
    {
        int cx = JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE);
        int cz = JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE);
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
            yield return (cx + dx, cz + dz);
    }
}
