using System.Collections.Concurrent;
using System.Diagnostics;
using Shaft.Engine.Config;
using Shaft.Engine.World;
using Xunit;

namespace Shaft.Engine.Tests;

/// <summary>
/// Covers the off-thread chunk streamer: correctness versus synchronous generation,
/// concurrency safety, and that it actually removes worldgen from the caller's thread.
/// </summary>
public class StreamingTests
{
    private static byte[] Snapshot(Chunk c)
    {
        var a = new byte[c.Blocks.Length + c.Light.Length];
        Buffer.BlockCopy(c.Blocks, 0, a, 0, c.Blocks.Length);
        Buffer.BlockCopy(c.Light, 0, a, c.Blocks.Length, c.Light.Length);
        return a;
    }

    [Fact]
    public void AsyncChunksAreIdenticalToSyncChunks()
    {
        const int seed = 20260417;
        var sync = new VoxelWorld(seed);
        var syncChunks = new Dictionary<long, byte[]>();
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            var c = sync.GetOrGenerateChunk(dx, dz);
            syncChunks[ChunkKeys.Pack(dx, dz)] = Snapshot(c);
        }

        var async_ = new VoxelWorld(seed);
        using var streamer = new ChunkStreamer(async_, workerCount: 4);
        streamer.EnsureLoadedBlocking(8, 8, 2);

        Assert.Equal(25, async_.LoadedChunkCount);
        foreach (var (key, expected) in syncChunks)
        {
            ChunkKeys.Unpack(key, out int cx, out int cz);
            var got = async_.GetChunk(cx, cz);
            Assert.NotNull(got);
            Assert.Equal(expected, Snapshot(got!));
        }
    }

    [Fact]
    public void StreamingDoesNotBlockTheCaller()
    {
        var world = new VoxelWorld(777);
        using var streamer = new ChunkStreamer(world, workerCount: 3);

        var sw = Stopwatch.StartNew();
        streamer.RequestAround(0, 0, 4);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"RequestAround took {sw.ElapsedMilliseconds} ms; it must only enqueue work.");
        Assert.True(streamer.QueuedCount + streamer.InFlightCount > 0);

        // Drain on the caller's thread, exactly as the game loop does.
        int adopted = 0;
        sw.Restart();
        while (adopted < 81 && sw.ElapsedMilliseconds < 20000)
        {
            adopted += streamer.AdoptCompleted();
            if (adopted < 81) Thread.Sleep(2);
        }
        sw.Stop();

        Assert.Equal(81, adopted);
        Assert.Equal(81, world.LoadedChunkCount);
        Assert.Equal(0, streamer.InFlightCount);
    }

    [Fact]
    public void RequestsAreDeduplicatedWhileInFlight()
    {
        var world = new VoxelWorld(4242);
        using var streamer = new ChunkStreamer(world, workerCount: 1);

        // Count what the worker actually produces: a duplicate request would show up
        // as the same chunk generated twice.
        var generated = new ConcurrentDictionary<long, int>();
        streamer.ChunkGenerated += c => generated.AddOrUpdate(ChunkKeys.Pack(c.CX, c.CZ), 1, (_, v) => v + 1);

        // Hammer the same area three times, exactly as a fast-moving player would.
        streamer.RequestAround(0, 0, 2);
        streamer.RequestAround(0, 0, 2);
        streamer.RequestAround(0, 0, 2);

        var sw = Stopwatch.StartNew();
        int adopted = 0;
        while (adopted < 25 && sw.ElapsedMilliseconds < 20000)
        {
            adopted += streamer.AdoptCompleted();
            if (adopted < 25) Thread.Sleep(2);
        }

        Assert.Equal(25, adopted);
        Assert.Equal(25, generated.Count);
        Assert.All(generated.Values, v => Assert.Equal(1, v));
        Assert.Equal(0, streamer.InFlightCount);
    }

    [Fact]
    public void UnloadingFarChunksKeepsTheWorkingSetBounded()
    {
        var world = new VoxelWorld(99);
        using var streamer = new ChunkStreamer(world, workerCount: 2);
        streamer.EnsureLoadedBlocking(0, 0, 4);
        Assert.Equal(81, world.LoadedChunkCount);

        // Walk far away, let those chunks land, then trim.
        streamer.RequestAround(2000, 2000, 2);
        var sw = Stopwatch.StartNew();
        while (world.LoadedChunkCount < 81 + 25 && sw.ElapsedMilliseconds < 20000)
        {
            streamer.AdoptCompleted();
            Thread.Sleep(2);
        }
        Assert.Equal(81 + 25, world.LoadedChunkCount);

        int removed = streamer.UnloadFarChunks(2000, 2000, 3);
        Assert.True(removed >= 56, $"expected a large trim, removed {removed}");
        foreach (var chunk in world.AllChunks)
        {
            Assert.True(Math.Abs(chunk.CX - 125) <= 3 && Math.Abs(chunk.CZ - 125) <= 3);
        }
    }

    [Fact]
    public void AdoptedChunksReceivePendingEdits()
    {
        var world = new VoxelWorld(1234);
        // An edit recorded before the chunk exists must survive adoption. This is the
        // path a loaded save takes: deltas arrive first, chunks stream in afterwards.
        world.BlockChanges[new BlockPos(5, 40, 5)] = Block.STONE;
        Assert.False(world.HasChunk(0, 0));

        using var streamer = new ChunkStreamer(world, workerCount: 1);
        streamer.EnsureLoadedBlocking(0, 0, 0);

        Assert.True(world.HasChunk(0, 0));
        Assert.Equal(Block.STONE, world.GetBlock(5, 40, 5));
        Assert.Equal((ushort)Block.STONE, world.BlockChanges[new BlockPos(5, 40, 5)]);
    }

    [Fact]
    public void DisposeStopsWorkersWithoutHanging()
    {
        var world = new VoxelWorld(5);
        var streamer = new ChunkStreamer(world, workerCount: 4);
        streamer.RequestAround(0, 0, 5);

        var sw = Stopwatch.StartNew();
        streamer.Dispose();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000, $"Dispose took {sw.ElapsedMilliseconds} ms");
        streamer.Dispose(); // idempotent
    }
}
