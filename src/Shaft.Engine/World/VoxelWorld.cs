using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Engine.World;

/// <summary>
/// Owns every loaded <see cref="Chunk"/> and is the single authority on
/// "what block is at (x,y,z)".
/// </summary>
/// <remarks>
/// Replaces the JS engine's four unbounded <c>Map</c> caches plus
/// <c>blockChanges</c>/<c>blockFacings</c> string-keyed maps. Chunks are keyed by a
/// packed <see cref="long"/> and edits by a <see cref="BlockPos"/> value type, so
/// steady-state block queries and edits allocate nothing.
/// <para>
/// Player edits are still tracked as a sparse delta set (<see cref="BlockChanges"/>)
/// because that is what the save format persists; terrain itself is regenerated from
/// the seed. The delta set is applied on top of generated chunks at load time, so
/// the on-disk format stays compatible with <c>dist/save.js</c>.
/// </para>
/// </remarks>
public sealed class VoxelWorld
{
    private readonly Dictionary<long, Chunk> _chunks = new();
    private readonly ChunkGenerator _generator;

    /// <summary>Sparse player/water edits, keyed by world position. Persisted.</summary>
    public Dictionary<BlockPos, ushort> BlockChanges { get; } = new();

    /// <summary>Door/furnace/chest orientation, 0..3. Persisted.</summary>
    public Dictionary<BlockPos, byte> BlockFacings { get; } = new();

    /// <summary>Chunks whose mesh must be rebuilt, in insertion order.</summary>
    public Queue<Chunk> RebuildQueue { get; } = new();
    private readonly HashSet<long> _queuedRebuilds = new();

    public int Seed => _generator.Seed;
    public ChunkGenerator Generator => _generator;

    public int LoadedChunkCount => _chunks.Count;

    public VoxelWorld(int seed, int biomeBlend = 4, int? spawnBiome = null)
    {
        _generator = new ChunkGenerator(seed, biomeBlend, spawnBiome);
    }

    // ------------------------------------------------------------- chunk map

    public Chunk? GetChunk(int cx, int cz)
        => _chunks.TryGetValue(ChunkKeys.Pack(cx, cz), out var c) ? c : null;

    public Chunk? GetChunkAt(int x, int z)
        => GetChunk(JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE), JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE));

    /// <summary>Returns an existing chunk or generates it on demand.</summary>
    public Chunk GetOrGenerateChunk(int cx, int cz)
    {
        long key = ChunkKeys.Pack(cx, cz);
        if (_chunks.TryGetValue(key, out var existing)) return existing;

        var chunk = new Chunk(cx, cz);
        _chunks[key] = chunk;
        _generator.PopulateChunk(chunk);
        ApplyPendingChanges(chunk);
        chunk.LightDirty = true;
        chunk.MeshDirty = true;
        return chunk;
    }

    public bool HasChunk(int cx, int cz) => _chunks.ContainsKey(ChunkKeys.Pack(cx, cz));

    /// <summary>
    /// Inserts an already-populated chunk. This is the hand-off point for
    /// <see cref="ChunkStreamer"/>: workers generate off-thread and the main thread
    /// adopts the result, which keeps every mutation of <c>_chunks</c> on one thread
    /// and the dictionary lock-free.
    /// </summary>
    /// <returns>False if the chunk was already present, in which case it is not replaced.</returns>
    public bool AdoptChunk(Chunk chunk)
    {
        long key = ChunkKeys.Pack(chunk.CX, chunk.CZ);
        if (_chunks.ContainsKey(key)) return false;
        _chunks[key] = chunk;
        ApplyPendingChanges(chunk);
        chunk.MeshDirty = true;
        MarkChunkDirty(chunk.CX, chunk.CZ);
        return true;
    }

    public void UnloadChunk(int cx, int cz)
    {
        long key = ChunkKeys.Pack(cx, cz);
        if (_chunks.Remove(key)) _queuedRebuilds.Remove(key);
    }

    public IEnumerable<Chunk> AllChunks => _chunks.Values;

    // ----------------------------------------------------------- block access

    /// <summary>
    /// Port of <c>queryBlock</c>. Returns <see cref="Block.AIR"/> for any position
    /// outside the vertical extent or in an unloaded chunk.
    /// </summary>
    public byte GetBlock(int x, int y, int z)
    {
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return Block.AIR;
        var chunk = GetChunkAt(x, z);
        if (chunk is null) return Block.AIR;
        return chunk.Get(x - chunk.OriginX, y, z - chunk.OriginZ);
    }

    /// <summary>Like <see cref="GetBlock"/> but treats unloaded chunks as opaque, so
    /// meshing at a chunk border does not flash open faces.</summary>
    public byte GetBlockForMesh(int x, int y, int z)
    {
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return Block.AIR;
        var chunk = GetChunkAt(x, z);
        // JS queryBlock is procedural: an unstored neighbour must answer with what worldgen
        // WOULD place, never a placeholder. Returning STONE here culled every face towards a
        // not-yet-generated neighbour until the next rebuild (the missing-side bug).
        if (chunk is null) return _generator.QueryBlock(x, y, z);
        return chunk.Get(x - chunk.OriginX, y, z - chunk.OriginZ);
    }

    /// <summary>
    /// Port of <c>isSolid</c> from mining.js, including its implicit bedrock floor:
    /// anything below <c>WORLD_BOTTOM - 5</c> is solid so the player cannot fall out
    /// of the world.
    /// </summary>
    public bool IsSolid(int x, int y, int z)
    {
        if (y < GameConfig.WORLD_BOTTOM - 5) return true;
        return Block.IsSolid(GetBlock(x, y, z));
    }

    /// <summary>Float-coordinate overload matching JS <c>isSolid(x,y,z)</c>, which floors.</summary>
    public bool IsSolid(float x, float y, float z)
        => IsSolid((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

    public bool IsSolid(double x, double y, double z)
        => IsSolid((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

    public int GetLight(int x, int y, int z)
    {
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return 0;
        var chunk = GetChunkAt(x, z);
        if (chunk is null) return GameConfig.MAX_LIGHT;
        byte l = chunk.GetLight(x - chunk.OriginX, y, z - chunk.OriginZ);
        // The JS engine stores a single 0..15 value combining sky and torch light.
        int sky = l >> 4, blk = l & 0xF;
        return Math.Max(sky, blk);
    }

    public byte GetFacing(int x, int y, int z)
        => BlockFacings.TryGetValue(new BlockPos(x, y, z), out byte f) ? f : (byte)0;

    public void SetFacing(int x, int y, int z, byte facing)
        => BlockFacings[new BlockPos(x, y, z)] = facing;

    // -------------------------------------------------------------- mutation

    /// <summary>
    /// Writes a block, records the delta for saving, and marks every affected chunk
    /// for remeshing. Equivalent to the JS <c>setBlock</c> + <c>queueChunkRebuilds</c>
    /// pair that water.js and mining.js both perform.
    /// </summary>
    /// <returns>False if the block was already that value (no work needed).</returns>
    public bool SetBlock(int x, int y, int z, byte id, bool recordDelta = true)
    {
        if (y < GameConfig.CHUNK_MIN_Y || y > GameConfig.CHUNK_MAX_Y) return false;

        var pos = new BlockPos(x, y, z);
        if (BlockChanges.TryGetValue(pos, out var current) && current == id) return false;

        if (recordDelta) BlockChanges[pos] = id;

        var chunk = GetOrGenerateChunk(JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE),
                                       JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE));
        int lx = x - chunk.OriginX, lz = z - chunk.OriginZ;
        chunk.Set(lx, y, lz, id);

        QueueChunkRebuilds(x, y, z);
        return true;
    }

    /// <summary>Applies any recorded deltas that fall inside a freshly generated chunk.</summary>
    private void ApplyPendingChanges(Chunk chunk)
    {
        if (BlockChanges.Count == 0) return;
        int bX = chunk.OriginX, bZ = chunk.OriginZ;
        foreach (var kv in BlockChanges)
        {
            int lx = kv.Key.X - bX, lz = kv.Key.Z - bZ;
            if ((uint)lx >= GameConfig.CHUNK_SIZE || (uint)lz >= GameConfig.CHUNK_SIZE) continue;
            if (kv.Key.Y < GameConfig.CHUNK_MIN_Y || kv.Key.Y > GameConfig.CHUNK_MAX_Y) continue;
            chunk.Set(lx, kv.Key.Y, lz, (byte)kv.Value);
        }
    }

    /// <summary>
    /// Port of <c>queueChunkRebuilds</c>: a block edit invalidates its own chunk plus
    /// any neighbour sharing the touched face, edge or corner.
    /// </summary>
    public void QueueChunkRebuilds(int x, int y, int z)
    {
        int cx = JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE);
        int cz = JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE);
        int lx = x - cx * GameConfig.CHUNK_SIZE;
        int lz = z - cz * GameConfig.CHUNK_SIZE;

        void Add(int ccx, int ccz)
        {
            long key = ChunkKeys.Pack(ccx, ccz);
            if (!_chunks.TryGetValue(key, out var c)) return;
            if (!_queuedRebuilds.Add(key)) return;
            c.MeshDirty = true;
            RebuildQueue.Enqueue(c);
        }

        Add(cx, cz);
        bool w = lx == 0, e = lx == GameConfig.CHUNK_SIZE - 1;
        bool n = lz == 0, s = lz == GameConfig.CHUNK_SIZE - 1;
        if (w) Add(cx - 1, cz);
        if (e) Add(cx + 1, cz);
        if (n) Add(cx, cz - 1);
        if (s) Add(cx, cz + 1);
        if (w && n) Add(cx - 1, cz - 1);
        if (w && s) Add(cx - 1, cz + 1);
        if (e && n) Add(cx + 1, cz - 1);
        if (e && s) Add(cx + 1, cz + 1);
    }

    public void MarkChunkDirty(int cx, int cz)
    {
        long key = ChunkKeys.Pack(cx, cz);
        if (!_chunks.TryGetValue(key, out var c)) return;
        c.MeshDirty = true;
        if (_queuedRebuilds.Add(key)) RebuildQueue.Enqueue(c);
    }

    /// <summary>Drains the rebuild queue. The renderer supplies the callback that
    /// turns a chunk into GPU buffers, keeping this class free of any GL dependency.</summary>
    public int ProcessRebuildQueue(Action<Chunk> rebuild, int maxPerFrame = int.MaxValue)
    {
        int done = 0;
        while (done < maxPerFrame && RebuildQueue.TryDequeue(out var chunk))
        {
            long key = ChunkKeys.Pack(chunk.CX, chunk.CZ);
            _queuedRebuilds.Remove(key);
            if (!_chunks.ContainsKey(key)) continue;   // unloaded while queued
            if (!chunk.MeshDirty) continue;
            chunk.MeshDirty = false;
            rebuild(chunk);
            done++;
        }
        return done;
    }

    // ------------------------------------------------------------ streaming

    /// <summary>
    /// Port of <c>ensureChunksAround</c>: keeps a square of chunks loaded around the
    /// player and unloads anything beyond it, so memory stays bounded (the JS engine
    /// never unloaded its caches).
    /// </summary>
    public void EnsureChunksAround(double px, double pz, int renderDistance, List<Chunk>? newlyLoaded = null)
    {
        int ccx = JsMath.FloorDiv((int)Math.Floor(px), GameConfig.CHUNK_SIZE);
        int ccz = JsMath.FloorDiv((int)Math.Floor(pz), GameConfig.CHUNK_SIZE);

        for (int dx = -renderDistance; dx <= renderDistance; dx++)
        for (int dz = -renderDistance; dz <= renderDistance; dz++)
        {
            int cx = ccx + dx, cz = ccz + dz;
            if (HasChunk(cx, cz)) continue;
            var c = GetOrGenerateChunk(cx, cz);
            newlyLoaded?.Add(c);
            // GetOrGenerateChunk flags the chunk dirty but does not enqueue it; without this
            // the synchronous streaming path would generate chunks that never get meshed.
            MarkChunkDirty(cx, cz);
        }

        // Unload far chunks. Keep a one-chunk hysteresis band so a player standing on
        // a border does not thrash generation every frame.
        int limit = renderDistance + 1;
        List<long>? toRemove = null;
        foreach (var key in _chunks.Keys)
        {
            ChunkKeys.Unpack(key, out int cx, out int cz);
            if (Math.Abs(cx - ccx) <= limit && Math.Abs(cz - ccz) <= limit) continue;
            (toRemove ??= new List<long>()).Add(key);
        }
        if (toRemove is not null)
            foreach (var key in toRemove) { _chunks.Remove(key); _queuedRebuilds.Remove(key); }
    }

    /// <summary>Finds a safe spawn: highest solid column near the origin at or above sea level.</summary>
    public (double X, double Y, double Z) FindSpawn()
    {
        for (int r = 0; r < 64; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                int x = dx, z = dz;
                int h = _generator.GetHeight(x, z);
                if (h <= GameConfig.SEA_LEVEL + 1) continue;
                var chunk = GetOrGenerateChunk(JsMath.FloorDiv(x, GameConfig.CHUNK_SIZE),
                                               JsMath.FloorDiv(z, GameConfig.CHUNK_SIZE));
                // Walk down from the top of the world to the first solid block.
                for (int y = GameConfig.CHUNK_MAX_Y; y > GameConfig.CHUNK_MIN_Y; y--)
                {
                    byte b = chunk.Get(x - chunk.OriginX, y, z - chunk.OriginZ);
                    if (b != Block.AIR && !Block.IsWater(b))
                        return (x + 0.5, y + 1.0, z + 0.5);
                }
            }
        }
        return (0.5, GameConfig.SEA_LEVEL + 2.0, 0.5);
    }

    /// <summary>Port of <c>rebuildAllChunks</c>, used when biome blending changes.</summary>
    public void RebuildAllChunks()
    {
        foreach (var c in _chunks.Values) MarkChunkDirty(c.CX, c.CZ);
    }

    /// <summary>
    /// The settings-menu biome-blend slider: <c>setBiomeBlend(v); rebuildAllChunks()</c> from
    /// main.js. Terrain shape does not depend on the blend radius — only grass tinting does —
    /// so re-meshing the loaded chunks is the whole effect.
    /// </summary>
    public void SetBiomeBlend(int value)
    {
        _generator.SetBiomeBlend(value);
        RebuildAllChunks();
    }
}
