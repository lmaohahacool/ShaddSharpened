using Shaft.Engine.Config;

namespace Shaft.Engine.World;

/// <summary>
/// A 16 x 128 x 16 column of the world, stored as a flat <see cref="byte"/> array.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the JavaScript engine never had. <c>dist/worldgen.js</c>
/// recomputed every block from procedural noise on each <c>queryBlock</c> call,
/// memoised through four unbounded <c>Map</c> caches keyed by template strings
/// (<c>`${x},${y},${z}`</c>). That allocated on every lookup and made it
/// impossible to ask "what is at this position" in constant time — which is a hard
/// requirement for both a particle solver's boundary queries and for meshing at
/// 60 Hz.
/// </para>
/// <para>
/// Layout matches <c>localIndex</c> in <c>dist/lighting.js</c> exactly:
/// <code>((ly - CHUNK_MIN_Y) * CHUNK_SIZE + lz) * CHUNK_SIZE + lx</code>
/// so the light volume and the block volume share one index function. Y is the
/// slowest-varying axis, which makes per-column height scans and per-slab meshing
/// both cache-friendly.
/// </para>
/// </remarks>
public sealed class Chunk
{
    /// <summary>1 byte per block. 32768 bytes per chunk.</summary>
    public byte[] Blocks { get; }

    /// <summary>1 byte per cell: high nibble = skylight, low nibble = block light.</summary>
    public byte[] Light { get; }

    public int CX { get; }
    public int CZ { get; }

    /// <summary>World-space origin of the chunk's minimum corner.</summary>
    public int OriginX => CX * GameConfig.CHUNK_SIZE;
    public int OriginZ => CZ * GameConfig.CHUNK_SIZE;

    /// <summary>Set once WorldGen has filled <see cref="Blocks"/>.</summary>
    public bool IsGenerated { get; set; }

    /// <summary>Set when the mesh is stale and must be rebuilt before the next draw.</summary>
    public bool MeshDirty { get; set; } = true;

    /// <summary>Set when light propagation must be re-run for this chunk.</summary>
    public bool LightDirty { get; set; } = true;

    /// <summary>True if the chunk contains no non-air block at all; such chunks
    /// can skip meshing entirely.</summary>
    public bool IsEmpty { get; private set; } = true;

    /// <summary>Highest non-air Y in this chunk, cached so meshing can skip empty sky.</summary>
    public int MaxOccupiedY { get; private set; } = GameConfig.CHUNK_MIN_Y;

    public Chunk(int cx, int cz)
    {
        CX = cx;
        CZ = cz;
        Blocks = new byte[GameConfig.CHUNK_VOLUME];
        Light = new byte[GameConfig.CHUNK_VOLUME];
    }

    /// <summary>Flat array offset for a chunk-local coordinate. No bounds check —
    /// callers must use <see cref="ContainsLocal"/>.</summary>
    public static int Index(int lx, int ly, int lz)
        => ((ly - GameConfig.CHUNK_MIN_Y) * GameConfig.CHUNK_SIZE + lz) * GameConfig.CHUNK_SIZE + lx;

    public static bool ContainsLocal(int lx, int ly, int lz) =>
        (uint)lx < GameConfig.CHUNK_SIZE &&
        (uint)lz < GameConfig.CHUNK_SIZE &&
        (uint)(ly - GameConfig.CHUNK_MIN_Y) < GameConfig.CHUNK_HEIGHT;

    public byte Get(int lx, int ly, int lz)
        => ContainsLocal(lx, ly, lz) ? Blocks[Index(lx, ly, lz)] : Block.AIR;

    /// <summary>Writes a block and maintains the occupancy cache.</summary>
    public void Set(int lx, int ly, int lz, byte id)
    {
        if (!ContainsLocal(lx, ly, lz)) return;
        int i = Index(lx, ly, lz);
        Blocks[i] = id;

        if (id != Block.AIR)
        {
            IsEmpty = false;
            if (ly > MaxOccupiedY) MaxOccupiedY = ly;
        }
    }

    /// <summary>Fast path used by WorldGen, which writes in place and fixes up the
    /// occupancy summary afterwards via <see cref="RecomputeOccupancy"/>.</summary>
    public void SetRaw(int lx, int ly, int lz, byte id) => Blocks[Index(lx, ly, lz)] = id;

    /// <summary>Rebuilds <see cref="IsEmpty"/> and <see cref="MaxOccupiedY"/>.</summary>
    public void RecomputeOccupancy()
    {
        int maxY = GameConfig.CHUNK_MIN_Y;
        bool any = false;
        // Scan downward from the top so the common case (a few surface blocks) exits early.
        for (int ly = GameConfig.CHUNK_MAX_Y; ly >= GameConfig.CHUNK_MIN_Y; ly--)
        {
            int slab = (ly - GameConfig.CHUNK_MIN_Y) * GameConfig.CHUNK_SIZE * GameConfig.CHUNK_SIZE;
            bool found = false;
            for (int i = 0; i < GameConfig.CHUNK_SIZE * GameConfig.CHUNK_SIZE; i++)
                if (Blocks[slab + i] != Block.AIR) { found = true; break; }
            if (found) { maxY = ly; any = true; break; }
        }
        MaxOccupiedY = maxY;
        IsEmpty = !any;
    }

    public byte GetLight(int lx, int ly, int lz)
        => ContainsLocal(lx, ly, lz) ? Light[Index(lx, ly, lz)] : (byte)0;

    public void SetLight(int lx, int ly, int lz, byte value)
    {
        if (!ContainsLocal(lx, ly, lz)) return;
        Light[Index(lx, ly, lz)] = value;
    }

    /// <summary>Skylight (high nibble), 0..15.</summary>
    public int GetSkyLight(int lx, int ly, int lz) => GetLight(lx, ly, lz) >> 4;

    /// <summary>Block/torch light (low nibble), 0..15.</summary>
    public int GetBlockLight(int lx, int ly, int lz) => GetLight(lx, ly, lz) & 0x0F;

    public void SetLightComponents(int lx, int ly, int lz, int sky, int block)
    {
        if (!ContainsLocal(lx, ly, lz)) return;
        Light[Index(lx, ly, lz)] = (byte)((Math.Clamp(sky, 0, 15) << 4) | Math.Clamp(block, 0, 15));
    }
}

/// <summary>Integer world position. A value type used as a dictionary key so that
/// block-change tracking allocates nothing — the direct replacement for the JS
/// engine's <c>`${x},${y},${z}`</c> string keys.</summary>
public readonly struct BlockPos : IEquatable<BlockPos>
{
    public readonly int X, Y, Z;

    public BlockPos(int x, int y, int z) { X = x; Y = y; Z = z; }

    public bool Equals(BlockPos other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is BlockPos p && Equals(p);

    public override int GetHashCode()
    {
        // FNV-ish mix; deliberately cheap because this is called per block edit.
        unchecked
        {
            int h = 17;
            h = h * 31 + X;
            h = h * 31 + Y;
            h = h * 31 + Z;
            return h;
        }
    }

    public static bool operator ==(BlockPos a, BlockPos b) => a.Equals(b);
    public static bool operator !=(BlockPos a, BlockPos b) => !a.Equals(b);
    public override string ToString() => $"{X},{Y},{Z}";
}

public static class ChunkKeys
{
    /// <summary>Packs a chunk coordinate pair into one <see cref="long"/> key.</summary>
    public static long Pack(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    public static void Unpack(long key, out int cx, out int cz)
    {
        cx = (int)(key >> 32);
        cz = (int)(key & 0xFFFFFFFFL);
    }
}
