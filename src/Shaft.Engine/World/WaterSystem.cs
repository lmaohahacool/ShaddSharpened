using Shaft.Engine.Config;

namespace Shaft.Engine.World;

/// <summary>
/// Port of <c>dist/water.js</c>: the discrete, level-based water automaton.
/// </summary>
/// <remarks>
/// This is the system the SPH solver is scheduled to sit alongside, not replace
/// (MIGRATION-BRIEF.md decision 4). Static seas and lakes stay as <c>WATER</c>
/// blocks driven by this automaton; particles handle splashes, waterfalls and
/// shore-breaking waves. Keeping it faithful now means the coupling layer has a
/// stable, well-understood block-side contract to talk to.
/// <para>
/// Two mechanical changes from the JS, both preserving behaviour:
/// the queue holds <see cref="BlockPos"/> values instead of <c>"x,y,z"</c> strings
/// (no allocation, no per-tick <c>split(',').map(Number)</c>), and the per-tick
/// distance sort operates on packed integers rather than freshly parsed arrays.
/// </para>
/// </remarks>
public sealed class WaterSystem
{
    private readonly VoxelWorld _world;
    private readonly Action<int, int, int>? _onBlockChanged;

    private readonly HashSet<BlockPos> _updateQueue = new();
    private float _tickTimer;

    // Scratch buffers reused every tick.
    private readonly List<BlockPos> _positions = new();

    public WaterSystem(VoxelWorld world, Action<int, int, int>? onBlockChanged = null)
    {
        _world = world;
        _onBlockChanged = onBlockChanged;
    }

    public int QueuedCount => _updateQueue.Count;

    private static readonly (int dx, int dz)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    // ------------------------------------------------------------ public API

    /// <summary>Port of <c>queueWaterUpdate</c>: queues a small cube around a changed
    /// position so neighbouring flows — including diagonals and flows one level up —
    /// get re-evaluated.</summary>
    public void QueueWaterUpdate(int x, int y, int z)
    {
        for (int dx = -2; dx <= 2; dx++)
        for (int dy = -2; dy <= 3; dy++)
        for (int dz = -2; dz <= 2; dz++)
            _updateQueue.Add(new BlockPos(x + dx, y + dy, z + dz));
    }

    public void QueueUpdate(int x, int y, int z) => _updateQueue.Add(new BlockPos(x, y, z));

    /// <summary>Port of <c>updateWater</c>: accumulates time and runs at 10 Hz.</summary>
    public void Update(float dt, double playerX, double playerZ)
    {
        _tickTimer += dt;
        if (_tickTimer < GameConfig.WATER_TICK_INTERVAL) return;
        _tickTimer = 0;
        RunTick(playerX, playerZ);
    }

    /// <summary>Port of <c>seedChunkWater</c>: primes the queue for a chunk's water
    /// in the y range the JS scanned (-5..14).</summary>
    public void SeedChunkWater(int cx, int cz)
    {
        int bx = cx * GameConfig.CHUNK_SIZE, bz = cz * GameConfig.CHUNK_SIZE;
        for (int lx = 0; lx < GameConfig.CHUNK_SIZE; lx++)
        for (int lz = 0; lz < GameConfig.CHUNK_SIZE; lz++)
        {
            int wx = bx + lx, wz = bz + lz;
            for (int ly = -5; ly < 15; ly++)
                if (Block.IsWater(_world.GetBlock(wx, ly, wz)))
                    _updateQueue.Add(new BlockPos(wx, ly, wz));
        }
    }

    // ------------------------------------------------------------------ tick

    private void RunTick(double playerX, double playerZ)
    {
        if (_updateQueue.Count == 0) return;

        int px = (int)Math.Floor(playerX);
        int pz = (int)Math.Floor(playerZ);

        _positions.Clear();
        _positions.AddRange(_updateQueue);
        _updateQueue.Clear();

        // Nearest-first, exactly like the JS comparator on squared XZ distance.
        _positions.Sort((a, b) =>
        {
            long da = (long)(a.X - px) * (a.X - px) + (long)(a.Z - pz) * (a.Z - pz);
            long db = (long)(b.X - px) * (b.X - px) + (long)(b.Z - pz) * (b.Z - pz);
            return da.CompareTo(db);
        });

        const int radiusSq = GameConfig.WATER_PLAYER_RADIUS * GameConfig.WATER_PLAYER_RADIUS;
        int processed = 0;
        foreach (var p in _positions)
        {
            if (processed >= GameConfig.WATER_MAX_UPDATES_PER_TICK) break;
            long dx = p.X - px, dz = p.Z - pz;
            if (dx * dx + dz * dz > radiusSq) continue;
            Evaluate(p.X, p.Y, p.Z);
            processed++;
        }
    }

    // -------------------------------------------------------------- evaluate

    private void Evaluate(int x, int y, int z)
    {
        byte block = _world.GetBlock(x, y, z);
        if (!Block.IsWater(block)) return;

        if (block == Block.WATER_FALLING) { EvaluateFalling(x, y, z); return; }
        if (block == Block.WATER) { EvaluateSource(x, y, z); return; }

        int level = Block.GetWaterLevel(block);
        if (level is >= 1 and <= 6) EvaluateFlow(x, y, z, level);
    }

    private bool SetAndRebuild(int x, int y, int z, byte id)
    {
        if (!_world.SetBlock(x, y, z, id)) return false;
        _onBlockChanged?.Invoke(x, y, z);
        return true;
    }

    /// <summary>Port of <c>evaluateSource</c>.</summary>
    private void EvaluateSource(int x, int y, int z)
    {
        byte below = _world.GetBlock(x, y - 1, z);
        if (below == Block.AIR)
        {
            if (SetAndRebuild(x, y - 1, z, Block.WATER_FALLING)) QueueUpdate(x, y - 1, z);
            return;
        }

        foreach (var (dx, dz) in Dirs)
        {
            int nx = x + dx, nz = z + dz;
            byte neighbor = _world.GetBlock(nx, y, nz);

            if (neighbor == Block.AIR)
            {
                if (SetAndRebuild(nx, y, nz, Block.WATER_FLOW_6)) QueueUpdate(nx, y, nz);
            }
            else if (Block.IsWater(neighbor))
            {
                int nLevel = Block.GetWaterLevel(neighbor);
                if (neighbor != Block.WATER_FALLING && nLevel > 0 && nLevel < 6)
                    if (SetAndRebuild(nx, y, nz, Block.WATER_FLOW_6)) QueueUpdate(nx, y, nz);
            }
        }

        CheckInfiniteSourceAt(x, y, z);
    }

    /// <summary>Port of <c>evaluateFlow</c>.</summary>
    private void EvaluateFlow(int x, int y, int z, int level)
    {
        if (!Block.IsWater(_world.GetBlock(x, y, z))) return;

        byte below = _world.GetBlock(x, y - 1, z);
        if (below == Block.AIR)
        {
            if (SetAndRebuild(x, y - 1, z, Block.WATER_FALLING)) QueueUpdate(x, y - 1, z);
            return;
        }

        int nextLevel = level - 1;
        if (nextLevel < 1) return;

        byte nextBlock = Block.GetWaterBlockFromLevel(nextLevel);
        foreach (var (dx, dz) in Dirs)
        {
            int nx = x + dx, nz = z + dz;
            byte neighbor = _world.GetBlock(nx, y, nz);

            if (neighbor == Block.AIR)
            {
                if (SetAndRebuild(nx, y, nz, nextBlock)) QueueUpdate(nx, y, nz);
            }
            else if (Block.IsWater(neighbor))
            {
                int nLevel = Block.GetWaterLevel(neighbor);
                if (neighbor != Block.WATER_FALLING && nLevel > 0 && nLevel < nextLevel)
                    if (SetAndRebuild(nx, y, nz, nextBlock)) QueueUpdate(nx, y, nz);
            }
        }

        CheckInfiniteSourceAt(x, y, z);
    }

    /// <summary>Port of <c>evaluateFalling</c>.</summary>
    private void EvaluateFalling(int x, int y, int z)
    {
        if (_world.GetBlock(x, y, z) != Block.WATER_FALLING) return;

        byte below = _world.GetBlock(x, y - 1, z);
        if (below == Block.AIR)
        {
            if (SetAndRebuild(x, y - 1, z, Block.WATER_FALLING)) QueueUpdate(x, y - 1, z);
            return;
        }

        SpreadFromFalling(x, y, z);
    }

    private void SpreadFromFalling(int x, int y, int z)
    {
        foreach (var (dx, dz) in Dirs)
        {
            int nx = x + dx, nz = z + dz;
            byte neighbor = _world.GetBlock(nx, y, nz);

            if (neighbor == Block.AIR)
            {
                if (SetAndRebuild(nx, y, nz, Block.WATER_FLOW_6)) QueueUpdate(nx, y, nz);
            }
            else if (Block.IsWater(neighbor) && neighbor != Block.WATER_FALLING)
            {
                int nLevel = Block.GetWaterLevel(neighbor);
                if (nLevel > 0 && nLevel < 6)
                    if (SetAndRebuild(nx, y, nz, Block.WATER_FLOW_6)) QueueUpdate(nx, y, nz);
            }
        }
    }

    /// <summary>
    /// Port of <c>checkInfiniteSourceAt</c>. Only triggers when two sources sit
    /// directly OPPOSITE each other; adjacent or same-side sources do not count.
    /// </summary>
    private void CheckInfiniteSourceAt(int x, int y, int z)
    {
        byte me = _world.GetBlock(x, y, z);
        if (!Block.IsWater(me)) return;
        if (me == Block.WATER) return;
        if (me == Block.WATER_FALLING) return;

        bool nE = _world.GetBlock(x + 1, y, z) == Block.WATER;
        bool nW = _world.GetBlock(x - 1, y, z) == Block.WATER;
        bool nN = _world.GetBlock(x, y, z + 1) == Block.WATER;
        bool nS = _world.GetBlock(x, y, z - 1) == Block.WATER;

        if (!((nE && nW) || (nN && nS))) return;

        if (SetAndRebuild(x, y, z, Block.WATER))
        {
            QueueUpdate(x + 1, y, z);
            QueueUpdate(x - 1, y, z);
            QueueUpdate(x, y, z + 1);
            QueueUpdate(x, y, z - 1);
            QueueUpdate(x, y - 1, z);
        }
    }
}
