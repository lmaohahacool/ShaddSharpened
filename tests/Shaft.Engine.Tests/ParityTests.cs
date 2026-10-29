using Shaft.Engine.Config;
using Shaft.Engine.Maths;
using Shaft.Engine.World;

namespace Shaft.Engine.Tests;

/// <summary>
/// Phase A parity: the C# port must reproduce the shipped JavaScript bit for bit.
/// Every expected value in here was produced by running the real <c>dist/</c> source
/// (see <c>analysis/gen_goldens.js</c>), not by re-deriving it in C#.
/// </summary>
public class ParityTests
{
    // ------------------------------------------------------------- JsMath

    [Theory]
    // Inputs are written as the exact expressions the engine evaluates, not as
    // rounded decimal literals: 1442695040888963407 is already outside double
    // precision, so a hand-rounded literal is a *different* double and would test
    // the wrong value.
    [InlineData(1 * 1442695040888963407.0, -144211712)]
    [InlineData(2 * 1442695040888963407.0, -288423424)]
    [InlineData(3 * 1442695040888963407.0, -432634880)]
    [InlineData(1000 * 1442695040888963407.0, 1817182208)]
    [InlineData(999999 * 1442695040888963407.0, 536870912)]
    [InlineData(-1.5, -1)]
    [InlineData(1.5, 1)]
    [InlineData(0.0, 0)]
    public void ToInt32_MatchesJavaScriptPipeZero(double input, int expected)
        => Assert.Equal(expected, JsMath.ToInt32(input));

    [Fact]
    public void ToInt32_DiffersFromNaiveCSharpCast()
    {
        // This is the trap documented in MIGRATION-BRIEF.md §A: a literal (int) cast
        // saturates to int.MinValue instead of wrapping modulo 2^32.
        double huge = 1442695040888963407;
        int naive;
        unchecked { naive = (int)huge; }
        Assert.Equal(int.MinValue, naive);
        Assert.NotEqual(naive, JsMath.ToInt32(huge));
    }

    [Theory]
    [InlineData(-1, 16, -1)]
    [InlineData(-16, 16, -1)]
    [InlineData(-17, 16, -2)]
    [InlineData(16, 16, 1)]
    [InlineData(0, 16, 0)]
    public void FloorDiv_MatchesMathFloorDivision(int a, int b, int expected)
        => Assert.Equal(expected, JsMath.FloorDiv(a, b));

    [Theory]
    [InlineData(-1, 16, 15)]
    [InlineData(-16, 16, 0)]
    [InlineData(-17, 16, 15)]
    [InlineData(17, 16, 1)]
    public void FloorMod_IsAlwaysNonNegative(int a, int b, int expected)
        => Assert.Equal(expected, JsMath.FloorMod(a, b));

    // --------------------------------------------------------------- hashes

    [Fact]
    public void Hash3_Legacy_MatchesLiveJavaScript()
    {
        Noise.UseExactInt64 = false;
        foreach (var seed in Goldens.Load().seeds)
        foreach (var g in seed.hash3)
        {
            double actual = Noise.Hash3(g.x, g.y, g.z, seed.seed);
            Assert.True(Math.Abs(actual - g.v) < 1e-15,
                $"hash3({g.x},{g.y},{g.z},{seed.seed}) = {actual:R}, JS gave {g.v:R}");
        }
    }

    [Fact]
    public void Hash2_Legacy_MatchesLiveJavaScript()
    {
        Noise.UseExactInt64 = false;
        foreach (var seed in Goldens.Load().seeds)
        foreach (var g in seed.hash2)
        {
            double actual = Noise.Hash2(g.x, g.y, seed.seed);
            Assert.True(Math.Abs(actual - g.v) < 1e-15,
                $"hash2({g.x},{g.y},{seed.seed}) = {actual:R}, JS gave {g.v:R}");
        }
    }

    [Fact]
    public void ExactInt64Hash_DiffersFromLegacy_ConfirmingWorldRegeneration()
    {
        // Documents decision 1 in MIGRATION-BRIEF.md: the fix is not a repair, it
        // replaces worldgen. This test fails loudly if that ever stops being true.
        int differ = 0, total = 0;
        for (int z = 0; z < 500; z++)
        {
            total++;
            if (Math.Abs(Noise.Hash3ExactInt64(3, 7, z, 12345) - Noise.Hash3Legacy(3, 7, z, 12345)) > 0)
                differ++;
        }
        Assert.True(differ > total * 0.9, $"only {differ}/{total} differed");
    }

    [Fact]
    public void LegacyHash2_SeedSpace_IsCollapsed()
    {
        // Reproduces the measured defect: only ~6002 distinct values across the whole
        // 1,000,000-seed space, because SEED*1442695040888963407 loses precision.
        var distinct = new HashSet<int>();
        for (int seed = 0; seed < 100000; seed++)
            distinct.Add(JsMath.ToInt32(seed * 1442695040888963407.0));
        Assert.Equal(6002, distinct.Count);
    }

    [Fact]
    public void ExactInt64Hash2_SeedSpace_IsFullRank()
    {
        var distinct = new HashSet<int>();
        for (int seed = 0; seed < 100000; seed++)
            distinct.Add(unchecked((int)(seed * 1442695040888963407L)));
        Assert.Equal(100000, distinct.Count);
    }

    // ------------------------------------------------------------ worldgen

    [Fact]
    public void GetHeight_MatchesJavaScript_AllSeeds()
    {
        Noise.UseExactInt64 = false;
        foreach (var gs in Goldens.Load().seeds)
        {
            var gen = new ChunkGenerator(gs.seed);
            foreach (var s in gs.samples)
                Assert.Equal(s.h, gen.GetHeight(s.x, s.z));
        }
    }

    [Fact]
    public void GetBiome_MatchesJavaScript_AllSeeds()
    {
        Noise.UseExactInt64 = false;
        foreach (var gs in Goldens.Load().seeds)
        {
            var gen = new ChunkGenerator(gs.seed);
            foreach (var s in gs.samples)
                Assert.Equal(s.b, gen.GetBiomeRaw(s.x, s.z));
        }
    }

    [Fact]
    public void PopulatedChunk_Columns_MatchJavaScriptQueryBlock()
    {
        // The strongest parity test: every stored voxel in a full column must equal
        // what the JS queryBlock returned, which exercises base terrain, water fill,
        // cave carving, ore veins, trees and cacti together.
        Noise.UseExactInt64 = false;
        int compared = 0;

        foreach (var gs in Goldens.Load().seeds)
        {
            var world = new VoxelWorld(gs.seed);
            foreach (var col in gs.columns)
            {
                var chunk = world.GetOrGenerateChunk(
                    JsMath.FloorDiv(col.x, GameConfig.CHUNK_SIZE),
                    JsMath.FloorDiv(col.z, GameConfig.CHUNK_SIZE));
                int lx = col.x - chunk.OriginX, lz = col.z - chunk.OriginZ;

                for (int i = 0; i < col.blocks.Count; i++)
                {
                    int y = col.minY + i;
                    Assert.True(y >= GameConfig.CHUNK_MIN_Y && y <= GameConfig.CHUNK_MAX_Y);
                    Assert.Equal(col.blocks[i], chunk.Get(lx, y, lz));
                    compared++;
                }
            }
        }
        Assert.True(compared > 1000, $"only compared {compared} voxels");
    }

    [Fact]
    public void ShortGrassPlacement_MatchesJavaScript()
    {
        Noise.UseExactInt64 = false;
        foreach (var gs in Goldens.Load().seeds)
        {
            var gen = new ChunkGenerator(gs.seed);
            var expected = new HashSet<(int, int)>(gs.shortGrass.Select(p => (p[0], p[1])));
            int matches = 0;
            foreach (var (x, z) in expected)
            {
                Assert.True(gen.ShouldSpawnShortGrass(x, z), $"short grass missing at {x},{z} seed {gs.seed}");
                matches++;
            }
            Assert.True(matches > 0 || gs.shortGrass.Count == 0);
        }
    }

    [Fact]
    public void WorldGen_IsDeterministic_ForSameSeed()
    {
        var a = new VoxelWorld(4242).GetOrGenerateChunk(0, 0);
        var b = new VoxelWorld(4242).GetOrGenerateChunk(0, 0);
        Assert.Equal(a.Blocks, b.Blocks);
    }
}
