using System.Collections.Concurrent;
using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Engine.World;

/// <summary>
/// Generates chunks on background threads so the render thread never blocks on
/// worldgen.
/// </summary>
/// <remarks>
/// <para>
/// This is the TPL role the migration brief originally assigned to the SPH solver,
/// relocated to where it actually pays off today (MIGRATION-BRIEF.md, decision 2
/// note). Chunk generation is ~10 ms of pure CPU work per chunk with no shared
/// mutable state, which makes it close to an ideal parallel workload.
/// </para>
/// <para>
/// Threading contract: <see cref="ChunkGenerator"/> keeps per-instance scratch state,
/// so each worker owns its own instance and no two workers share one. Workers never
/// touch <see cref="VoxelWorld"/>; they build a detached <see cref="Chunk"/> and hand
/// it back through <see cref="AdoptCompleted"/>, which runs on the caller's thread.
/// That keeps the chunk dictionary single-threaded and lock-free.
/// </para>
/// <para>
/// One global remains: <see cref="Maths.Noise.UseExactInt64"/> is static. It must be
/// set once at startup and never mutated during a session, otherwise workers could
/// mix hash implementations mid-generation. Phase B should replace it with a value
/// carried on the generator instance.
/// </para>
/// </remarks>
public sealed class ChunkStreamer : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly int _seed;
    private readonly int? _spawnBiome;

    private readonly BlockingCollection<(int cx, int cz)> _queue = new(new ConcurrentQueue<(int, int)>());
    private readonly ConcurrentQueue<Chunk> _completed = new();
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();

    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Number of generation workers. Defaults to leaving two cores for the
    /// render thread and the OS.</summary>
    public int WorkerCount { get; }

    public int QueuedCount => _queue.Count;
    public int InFlightCount => _inFlight.Count;
    public int CompletedPending => _completed.Count;

    /// <summary>Raised on the worker thread when a chunk finishes generating.</summary>
    public event Action<Chunk>? ChunkGenerated;

    public ChunkStreamer(VoxelWorld world, int workerCount = 0)
    {
        _world = world;
        // The biome-blend radius is read live off the world generator rather than cached
        // here, so the settings slider also applies to chunks generated after the change.
        _seed = world.Seed;
        _spawnBiome = world.Generator.SpawnBiome;

        WorkerCount = workerCount > 0
            ? workerCount
            : Math.Max(1, Environment.ProcessorCount - 2);

        _workers = new Task[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            int id = i;
            _workers[i] = Task.Factory.StartNew(() => WorkerLoop(id), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void WorkerLoop(int workerId)
    {
        // Each worker owns a generator: ChunkGenerator caches height/biome per column
        // in instance fields, so sharing one across threads would corrupt it.
        var gen = new ChunkGenerator(_seed, _world.Generator.BiomeBlend, _spawnBiome);

        try
        {
            foreach (var (cx, cz) in _queue.GetConsumingEnumerable(_cts.Token))
            {
                long key = ChunkKeys.Pack(cx, cz);
                if (_world.HasChunk(cx, cz)) { _inFlight.TryRemove(key, out _); continue; }

                var chunk = new Chunk(cx, cz);
                gen.PopulateChunk(chunk);

                _completed.Enqueue(chunk);
                _inFlight.TryRemove(key, out _);
                ChunkGenerated?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Queues every chunk within <paramref name="renderDistance"/> of the player that
    /// is not already loaded, queued or in flight, nearest first.
    /// </summary>
    public void RequestAround(double px, double pz, int renderDistance)
    {
        int ccx = JsMath.FloorDiv((int)Math.Floor(px), GameConfig.CHUNK_SIZE);
        int ccz = JsMath.FloorDiv((int)Math.Floor(pz), GameConfig.CHUNK_SIZE);

        int r = renderDistance;
        var wanted = new List<(int cx, int cz, int d2)>((2 * r + 1) * (2 * r + 1));
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            int cx = ccx + dx, cz = ccz + dz;
            long key = ChunkKeys.Pack(cx, cz);
            if (_world.HasChunk(cx, cz)) continue;
            if (_inFlight.ContainsKey(key)) continue;
            wanted.Add((cx, cz, dx * dx + dz * dz));
        }
        if (wanted.Count == 0) return;

        // Nearest-first so the player sees the ground under them before the horizon.
        wanted.Sort((a, b) => a.d2.CompareTo(b.d2));
        foreach (var (cx, cz, _) in wanted)
        {
            long key = ChunkKeys.Pack(cx, cz);
            if (_inFlight.TryAdd(key, 0)) _queue.Add((cx, cz));
        }
    }

    /// <summary>
    /// Moves finished chunks into the world. Must be called from the main thread.
    /// Returns the number adopted.
    /// </summary>
    /// <param name="adopted">If given, receives every chunk adopted by this call, so
    /// the caller can run its own per-chunk setup (water seeding, skylight) on the
    /// main thread. Worker threads must not do that work: it mutates the world.</param>
    public int AdoptCompleted(List<Chunk>? adopted = null, int max = int.MaxValue)
    {
        int n = 0;
        while (n < max && _completed.TryDequeue(out var chunk))
        {
            if (_world.AdoptChunk(chunk))
            {
                adopted?.Add(chunk);
                n++;
            }
        }
        return n;
    }

    /// <summary>Blocks until at least <paramref name="count"/> chunks are loaded
    /// around the position, generating synchronously if the workers fall behind.
    /// Used for the initial world load so the first frame is not empty.</summary>
    public void EnsureLoadedBlocking(double px, double pz, int renderDistance)
    {
        RequestAround(px, pz, renderDistance);
        var gen = new ChunkGenerator(_seed, _world.Generator.BiomeBlend, _spawnBiome);

        int ccx = JsMath.FloorDiv((int)Math.Floor(px), GameConfig.CHUNK_SIZE);
        int ccz = JsMath.FloorDiv((int)Math.Floor(pz), GameConfig.CHUNK_SIZE);

        // Synchronously fill anything the workers have not reached yet, nearest first.
        for (int r = 0; r <= renderDistance; r++)
        for (int dx = -renderDistance; dx <= renderDistance; dx++)
        for (int dz = -renderDistance; dz <= renderDistance; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            int cx = ccx + dx, cz = ccz + dz;
            if (_world.HasChunk(cx, cz)) continue;

            AdoptCompleted();
            if (_world.HasChunk(cx, cz)) continue;

            var chunk = new Chunk(cx, cz);
            gen.PopulateChunk(chunk);
            _world.AdoptChunk(chunk);
            long key = ChunkKeys.Pack(cx, cz);
            _inFlight.TryRemove(key, out _);
        }
        AdoptCompleted();
    }

    /// <summary>Unloads chunks outside the keep radius, bounding memory. Returns the
    /// number removed.</summary>
    /// <param name="removed">If given, receives the packed keys of every chunk
    /// unloaded, so the caller can release the matching GPU buffers.</param>
    public int UnloadFarChunks(double px, double pz, int keepDistance, List<long>? removed = null)
    {
        int ccx = JsMath.FloorDiv((int)Math.Floor(px), GameConfig.CHUNK_SIZE);
        int ccz = JsMath.FloorDiv((int)Math.Floor(pz), GameConfig.CHUNK_SIZE);

        // Snapshot first: UnloadChunk mutates the dictionary AllChunks enumerates.
        List<(int cx, int cz)>? doomed = null;
        foreach (var chunk in _world.AllChunks)
        {
            if (Math.Abs(chunk.CX - ccx) <= keepDistance && Math.Abs(chunk.CZ - ccz) <= keepDistance)
                continue;
            (doomed ??= new()).Add((chunk.CX, chunk.CZ));
        }
        if (doomed == null) return 0;
        foreach (var (cx, cz) in doomed)
        {
            _world.UnloadChunk(cx, cz);
            long key = ChunkKeys.Pack(cx, cz);
            _inFlight.TryRemove(key, out _);
            removed?.Add(key);
        }
        return doomed.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _queue.CompleteAdding();
        try { Task.WaitAll(_workers, 2000); } catch { /* cancellation races are expected */ }
        _queue.Dispose();
        _cts.Dispose();
    }
}
