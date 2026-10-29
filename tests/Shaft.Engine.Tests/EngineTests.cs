using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Items;
using Shaft.Engine.Meshing;
using Shaft.Engine.World;

namespace Shaft.Engine.Tests;

public class VoxelStorageTests
{
    [Fact]
    public void Chunk_FlatIndex_MatchesLightingJsLocalIndex()
    {
        // lighting.js: localIndex(lx,ly,lz) = ((ly - BOTTOM) * CHUNK_SIZE + lz) * CHUNK_SIZE + lx
        const int BOTTOM = -32, CS = 16;
        for (int lx = 0; lx < CS; lx += 5)
        for (int lz = 0; lz < CS; lz += 7)
        for (int ly = BOTTOM; ly < BOTTOM + 128; ly += 31)
            Assert.Equal(((ly - BOTTOM) * CS + lz) * CS + lx, Chunk.Index(lx, ly, lz));
    }

    [Fact]
    public void Chunk_IsExactlyOneBytePerBlock()
    {
        var c = new Chunk(0, 0);
        Assert.Equal(GameConfig.CHUNK_VOLUME, c.Blocks.Length);
        Assert.Equal(16 * 128 * 16, c.Blocks.Length);
    }

    [Fact]
    public void Chunk_SetGet_RoundTripsEveryCell()
    {
        var c = new Chunk(3, -4);
        c.Set(0, GameConfig.CHUNK_MIN_Y, 0, Block.STONE);
        c.Set(15, GameConfig.CHUNK_MAX_Y, 15, Block.GRASS);
        c.Set(7, 0, 9, Block.WATER);

        Assert.Equal(Block.STONE, c.Get(0, GameConfig.CHUNK_MIN_Y, 0));
        Assert.Equal(Block.GRASS, c.Get(15, GameConfig.CHUNK_MAX_Y, 15));
        Assert.Equal(Block.WATER, c.Get(7, 0, 9));
        Assert.Equal(Block.AIR, c.Get(8, 8, 8));
    }

    [Fact]
    public void Chunk_OutOfRangeReads_ReturnAirInsteadOfThrowing()
    {
        var c = new Chunk(0, 0);
        Assert.Equal(Block.AIR, c.Get(-1, 0, 0));
        Assert.Equal(Block.AIR, c.Get(16, 0, 0));
        Assert.Equal(Block.AIR, c.Get(0, GameConfig.CHUNK_MAX_Y + 1, 0));
    }

    [Fact]
    public void Chunk_OccupancySummary_TracksHighestBlock()
    {
        var c = new Chunk(0, 0);
        Assert.True(c.IsEmpty);
        c.Set(2, 10, 3, Block.DIRT);
        c.RecomputeOccupancy();
        Assert.False(c.IsEmpty);
        Assert.Equal(10, c.MaxOccupiedY);

        c.Set(2, 10, 3, Block.AIR);
        c.RecomputeOccupancy();
        Assert.True(c.IsEmpty);
    }

    [Fact]
    public void VoxelWorld_UsesNoStringKeys_ForEditsOrChunks()
    {
        // The whole point of Step 1: block identity is a value type, chunk identity a long.
        var p = new BlockPos(5, -12, 77);
        Assert.Equal(p, new BlockPos(5, -12, 77));
        Assert.Equal(p.GetHashCode(), new BlockPos(5, -12, 77).GetHashCode());
        Assert.NotEqual(p, new BlockPos(5, -12, 78));

        long k = ChunkKeys.Pack(-3, 9);
        ChunkKeys.Unpack(k, out int cx, out int cz);
        Assert.Equal(-3, cx);
        Assert.Equal(9, cz);
    }

    [Fact]
    public void VoxelWorld_SetBlock_WritesVoxelAndRecordsDelta()
    {
        var w = new VoxelWorld(1234);
        w.GetOrGenerateChunk(0, 0);
        Assert.True(w.SetBlock(3, 5, 4, Block.PLANKS));

        Assert.Equal(Block.PLANKS, w.GetBlock(3, 5, 4));
        Assert.True(w.BlockChanges.ContainsKey(new BlockPos(3, 5, 4)));
    }

    [Fact]
    public void VoxelWorld_SetBlock_SameValueReportsNoChange()
    {
        var w = new VoxelWorld(1234);
        w.GetOrGenerateChunk(0, 0);
        w.SetBlock(3, 5, 4, Block.PLANKS);
        Assert.False(w.SetBlock(3, 5, 4, Block.PLANKS));
    }

    [Fact]
    public void VoxelWorld_DeltasAreReappliedToFreshlyGeneratedChunks()
    {
        // A chunk generated after an edit was recorded must still show the edit.
        var w = new VoxelWorld(99);
        w.BlockChanges[new BlockPos(40, 6, 40)] = Block.COBBLESTONE;
        var c = w.GetOrGenerateChunk(2, 2);   // covers x,z in [32,47]
        Assert.Equal(Block.COBBLESTONE, c.Get(40 - 32, 6, 40 - 32));
    }

    [Fact]
    public void VoxelWorld_EditsInvalidateNeighbourChunks_OnBorder()
    {
        var w = new VoxelWorld(7);
        w.GetOrGenerateChunk(0, 0);
        w.GetOrGenerateChunk(1, 0);

        while (w.RebuildQueue.TryDequeue(out _)) { }
        w.SetBlock(15, 5, 5, Block.STONE);   // lx == 15 -> east border

        var queued = new HashSet<long>();
        foreach (var c in w.RebuildQueue) queued.Add(ChunkKeys.Pack(c.CX, c.CZ));
        Assert.Contains(ChunkKeys.Pack(0, 0), queued);
        Assert.Contains(ChunkKeys.Pack(1, 0), queued);
    }

    [Fact]
    public void VoxelWorld_IsSolid_HasImplicitBedrockFloor()
    {
        var w = new VoxelWorld(5);
        // mining.js: if (by < WORLD_BOTTOM - 5) return true
        Assert.True(w.IsSolid(0, GameConfig.WORLD_BOTTOM - 6, 0));
    }

    [Fact]
    public void VoxelWorld_StreamingBoundsMemory()
    {
        var w = new VoxelWorld(5);
        w.EnsureChunksAround(0, 0, 2);
        int near = w.LoadedChunkCount;
        Assert.Equal(25, near);   // 5x5

        w.EnsureChunksAround(500, 500, 2);
        // Far chunks must have been released, unlike the JS caches which never evicted.
        Assert.True(w.LoadedChunkCount <= 30, $"loaded {w.LoadedChunkCount} chunks after teleport");
    }
}

public class MeshingTests
{
    [Fact]
    public void Mesher_ProducesGeometryForGeneratedChunk()
    {
        var w = new VoxelWorld(12345);
        var chunk = w.GetOrGenerateChunk(0, 0);
        var lighting = new Lighting(w);
        var mesher = new ChunkMesher(w, lighting);

        var mesh = new ChunkMeshData();
        mesher.Build(chunk, mesh);

        Assert.False(mesh.Opaque.IsEmpty);
        Assert.True(mesh.Opaque.VertexCount > 0);
        Assert.Equal(0, mesh.Opaque.IndexCount % 6);   // quads emit 6 indices
        Assert.Equal(0, mesh.Opaque.VertexCount % 4);   // and 4 vertices
    }

    [Fact]
    public void Mesher_FullyBuriedBlock_EmitsNoFaces()
    {
        var w = new VoxelWorld(1);
        var chunk = w.GetOrGenerateChunk(0, 0);
        // Fill the whole chunk with stone: no face is exposed.
        for (int i = 0; i < chunk.Blocks.Length; i++) chunk.Blocks[i] = Block.STONE;
        chunk.RecomputeOccupancy();

        // Neighbours must exist too, otherwise border faces are emitted.
        w.GetOrGenerateChunk(1, 0); w.GetOrGenerateChunk(-1, 0);
        w.GetOrGenerateChunk(0, 1); w.GetOrGenerateChunk(0, -1);
        foreach (int[] d in new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } })
        {
            var n = w.GetChunk(d[0], d[1])!;
            for (int i = 0; i < n.Blocks.Length; i++) n.Blocks[i] = Block.STONE;
            n.RecomputeOccupancy();
        }

        var mesh = new ChunkMeshData();
        new ChunkMesher(w, new Lighting(w)).Build(chunk, mesh);
        Assert.Equal(0, mesh.Opaque.VertexCount);
    }

    [Fact]
    public void Mesher_WaterTop_IsLoweredToWaterHeight()
    {
        var w = new VoxelWorld(2);
        var c = w.GetOrGenerateChunk(0, 0);
        // Isolated water block with air above: its top face must sit at 0.8, not 1.0.
        c.Set(4, 10, 4, Block.WATER);
        c.RecomputeOccupancy();

        var mesh = new ChunkMeshData();
        new ChunkMesher(w, new Lighting(w)).Build(c, mesh);

        var verts = mesh.Opaque.ToVertexArray();
        bool foundTopAt08 = false;
        for (int i = 0; i + MeshBuilder.FLOATS_PER_VERTEX <= verts.Length; i += MeshBuilder.FLOATS_PER_VERTEX)
            if (Math.Abs(verts[i + 1] - 10.8f) < 1e-4) foundTopAt08 = true;
        Assert.True(foundTopAt08, "no water surface vertex at y = 10.8");
    }

    [Fact]
    public void LightToColor_MatchesChunksJs()
    {
        // chunks.js: lightToColor(level) = 0.5 + 0.5 * (level / 15)
        Assert.Equal(0.5f, Lighting.LightToColor(0));
        Assert.Equal(1.0f, Lighting.LightToColor(15));
        Assert.Equal(0.5f + 0.5f * (7f / 15f), Lighting.LightToColor(7));
    }
}

public class LightingTests
{
    [Fact]
    public void TorchFloodFill_ReachesExpectedRadius()
    {
        var w = new VoxelWorld(3);
        w.GetOrGenerateChunk(0, 0);
        // Clear a volume of air so light can propagate.
        var c = w.GetChunk(0, 0)!;
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;
        c.RecomputeOccupancy();

        var l = new Lighting(w);
        l.AddLightAt(8, 10, 8, Lighting.TORCH_LIGHT);

        Assert.Equal(Lighting.TORCH_LIGHT, l.GetBlockLight(8, 10, 8));
        Assert.Equal(Lighting.TORCH_LIGHT - 6, l.GetBlockLight(14, 10, 8));
        Assert.Equal(0, l.GetBlockLight(8 + Lighting.TORCH_LIGHT + 1, 10, 8));
    }

    [Fact]
    public void RemoveLight_ClearsTheFloodFill()
    {
        var w = new VoxelWorld(3);
        w.GetOrGenerateChunk(0, 0);
        var c = w.GetChunk(0, 0)!;
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;

        var l = new Lighting(w);
        l.AddLightAt(8, 10, 8);
        Assert.True(l.GetBlockLight(10, 10, 8) > 0);

        l.RemoveLightAt(8, 10, 8);
        Assert.Equal(0, l.GetBlockLight(8, 10, 8));
        Assert.Equal(0, l.GetBlockLight(10, 10, 8));
    }

    [Fact]
    public void SkylightIsOffByDefault_ToMatchJavaScript()
    {
        var w = new VoxelWorld(3);
        w.GetOrGenerateChunk(0, 0);
        var l = new Lighting(w);
        Assert.False(l.EnableSkylight);
        Assert.Equal(0, l.GetLight(0, 40, 0));
    }
}

public class PlayerPhysicsTests
{
    private static GameSession FlatWorldSession(byte surface = Block.GRASS)
    {
        var s = new GameSession(12345, Path.Combine(Path.GetTempPath(), "shaft-tests-" + Guid.NewGuid().ToString("N")));
        // Flat platform at y = 20 so the tests do not depend on terrain shape.
        var c = s.World.GetOrGenerateChunk(0, 0);
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;
        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++)
        {
            c.Set(lx, 19, lz, surface);
            c.Set(lx, 18, lz, Block.STONE);
        }
        c.RecomputeOccupancy();
        return s;
    }

    [Fact]
    public void Player_FallsAndLandsOnGround()
    {
        var s = FlatWorldSession();
        s.Player.X = 8.5; s.Player.Y = 30; s.Player.Z = 8.5;
        s.Player.Mode = Gamemode.Survival;

        for (int i = 0; i < 200 && !s.Player.OnGround; i++) s.Player.Update(1f / 60f, 0, 0, false, false);

        Assert.True(s.Player.OnGround, "player never landed");
        Assert.Equal(20.0, s.Player.Y, 3);   // top of the y=19 platform
    }

    [Fact]
    public void Player_CannotWalkThroughWall()
    {
        var s = FlatWorldSession();
        s.Player.X = 8.5; s.Player.Y = 20; s.Player.Z = 8.5;
        s.Player.Mode = Gamemode.Survival;
        s.Player.OnGround = true;

        var c = s.World.GetChunk(0, 0)!;
        for (int y = 20; y <= 22; y++) c.Set(12, y, 8, Block.STONE);
        c.RecomputeOccupancy();

        for (int i = 0; i < 120; i++) s.Player.Update(1f / 60f, 1, 0, false, false);

        Assert.True(s.Player.X < 12.0, $"player walked through the wall to x={s.Player.X}");
    }

    [Fact]
    public void FallDamage_AppliesBeyondThreeBlocks()
    {
        var s = FlatWorldSession();
        s.Player.Mode = Gamemode.Survival;
        s.Player.X = 8.5; s.Player.Z = 8.5; s.Player.Y = 40;
        s.Player.Health = 20;

        for (int i = 0; i < 600 && !s.Player.OnGround; i++) s.Player.Update(1f / 60f, 0, 0, false, false);
        s.Player.Update(1f / 60f, 0, 0, false, false);   // one more frame to settle

        Assert.True(s.Player.Health < 20, $"no fall damage from a 20-block drop (health={s.Player.Health})");
    }

    [Fact]
    public void Creative_TakesNoFallDamage()
    {
        var s = FlatWorldSession();
        s.Player.Mode = Gamemode.Creative;
        s.Player.X = 8.5; s.Player.Z = 8.5; s.Player.Y = 40;

        for (int i = 0; i < 600 && !s.Player.OnGround; i++) s.Player.Update(1f / 60f, 0, 0, false, false);
        s.Player.Update(1f / 60f, 0, 0, false, false);

        Assert.Equal(20f, s.Player.Health);
    }

    [Fact]
    public void Flying_BypassesGravity()
    {
        var s = FlatWorldSession();
        s.Player.Mode = Gamemode.Creative;
        s.Player.Flying = true;
        s.Player.X = 8.5; s.Player.Y = 30; s.Player.Z = 8.5;

        for (int i = 0; i < 60; i++) s.Player.Update(1f / 60f, 0, 0, false, false);
        Assert.Equal(30.0, s.Player.Y, 3);
    }

    [Fact]
    public void Hunger_DecreasesOverTime_InSurvivalOnly()
    {
        var s = FlatWorldSession();
        s.Player.Mode = Gamemode.Survival;
        s.Player.X = 8.5; s.Player.Y = 20; s.Player.Z = 8.5;
        s.Player.Hunger = 20;

        for (int i = 0; i < 60 * 9; i++) s.Player.Update(1f / 60f, 0, 0, false, false);
        Assert.True(s.Player.Hunger < 20, "hunger did not drain after 9 seconds");

        var c = FlatWorldSession();
        c.Player.Mode = Gamemode.Creative;
        c.Player.X = 8.5; c.Player.Y = 20; c.Player.Z = 8.5;
        for (int i = 0; i < 60 * 20; i++) c.Player.Update(1f / 60f, 0, 0, false, false);
        Assert.Equal(20f, c.Player.Hunger);
    }

    [Fact]
    public void Starvation_DamagesWhenHungerEmpty()
    {
        var s = FlatWorldSession();
        s.Player.Mode = Gamemode.Survival;
        s.Player.X = 8.5; s.Player.Y = 20; s.Player.Z = 8.5;
        s.Player.Hunger = 0;
        s.Player.Health = 20;

        for (int i = 0; i < 60 * 3; i++) s.Player.Update(1f / 60f, 0, 0, false, false);
        Assert.True(s.Player.Health < 20, "starvation did not damage the player");
    }
}

public class InventoryAndCraftingTests
{
    [Fact]
    public void Add_StacksIntoExistingSlotUpTo64()
    {
        var inv = new Inventory();
        inv.Add(Block.DIRT, 40);
        inv.Add(Block.DIRT, 40);
        Assert.Equal(64, inv.Slots[0].Count);
        Assert.Equal(16, inv.Slots[1].Count);
    }

    [Fact]
    public void Tools_NeverMergeIntoAnExistingStack()
    {
        // Faithful to inventory.js: the merge pass is gated on
        // `!isItem(blockId) || !ITEM_INFO[blockId].durability`, but the empty-slot
        // pass is not gated at all and still caps at INVENTORY_MAX_STACK. So a single
        // add of two pickaxes lands as one stack of two (an upstream quirk, preserved
        // deliberately), while two separate adds never merge.
        var inv = new Inventory();
        inv.Add(ItemId.WP, 2);
        Assert.Equal(2, inv.Slots[0].Count);

        inv.Add(ItemId.WP, 1);
        Assert.Equal(2, inv.Slots[0].Count);   // unchanged: tools do not merge
        Assert.Equal(1, inv.Slots[1].Count);
    }

    [Fact]
    public void NonTools_DoMergeIntoAnExistingStack()
    {
        var inv = new Inventory();
        inv.Add(Block.DIRT, 10);
        inv.Add(Block.DIRT, 10);
        Assert.Equal(20, inv.Slots[0].Count);
        Assert.True(inv.Slots[1].IsEmpty);
    }

    [Fact]
    public void Remove_FailsAtomicallyWhenShort()
    {
        var inv = new Inventory();
        inv.Add(Block.DIRT, 5);
        Assert.False(inv.Remove(Block.DIRT, 10));
        Assert.Equal(5, inv.CountOf(Block.DIRT));   // nothing consumed
        Assert.True(inv.Remove(Block.DIRT, 5));
        Assert.Equal(0, inv.CountOf(Block.DIRT));
    }

    [Fact]
    public void Remove_PrefersTheSelectedSlot()
    {
        var inv = new Inventory();
        inv.Add(Block.STONE, 64);   // fills slot 0 to the cap
        inv.Add(Block.STONE, 10);   // overflows into slot 1
        Assert.Equal(64, inv.Slots[0].Count);
        Assert.Equal(10, inv.Slots[1].Count);

        inv.SelectedIndex = 1;
        Assert.True(inv.Remove(Block.STONE, 5));
        Assert.Equal(64, inv.Slots[0].Count);   // untouched
        Assert.Equal(5, inv.Slots[1].Count);    // consumed first
    }

    [Fact]
    public void ClickSlot_LeftClickPicksUpWholeStack()
    {
        var inv = new Inventory();
        inv.Add(Block.SAND, 12);
        inv.ClickSlot(0, rightClick: false, shiftClick: false);
        Assert.True(inv.Slots[0].IsEmpty);
        Assert.Equal(12, inv.Cursor.Count);
        Assert.Equal(Block.SAND, inv.Cursor.Id);
    }

    [Fact]
    public void ClickSlot_RightClickTakesHalf()
    {
        var inv = new Inventory();
        inv.Add(Block.SAND, 11);
        inv.ClickSlot(0, rightClick: true, shiftClick: false);
        Assert.Equal(5, inv.Cursor.Count);      // floor(11/2)
        Assert.Equal(6, inv.Slots[0].Count);
    }

    [Fact]
    public void ClickSlot_SwapWhenIncompatible()
    {
        var inv = new Inventory();
        inv.Add(Block.SAND, 5);
        inv.Add(Block.STONE, 3);
        inv.ClickSlot(1, false, false);          // pick up stone
        inv.ClickSlot(0, false, false);          // drop onto sand -> swap
        Assert.Equal(Block.SAND, inv.Cursor.Id);
        Assert.Equal(Block.STONE, inv.Slots[0].Id);
    }

    [Fact]
    public void ClickSlot_RightClickPlacesOne()
    {
        var inv = new Inventory();
        inv.Add(Block.SAND, 5);
        inv.ClickSlot(0, false, false);
        inv.ClickSlot(3, rightClick: true, shiftClick: false);
        Assert.Equal(1, inv.Slots[3].Count);
        Assert.Equal(4, inv.Cursor.Count);
    }

    [Fact]
    public void ToolWear_BreaksTheToolAtZero()
    {
        var inv = new Inventory();
        inv.Add(ItemId.WP, 1);
        Assert.Equal(60, inv.Slots[0].Durability);
        for (int i = 0; i < 59; i++) Assert.False(inv.DamageSelectedTool());
        Assert.True(inv.DamageSelectedTool());
        Assert.True(inv.Slots[0].IsEmpty);
    }

    // ---- crafting: exact matchRecipe semantics ----

    private static int[] Grid2(params int[] ids) { var g = new int[4]; Array.Copy(ids, g, ids.Length); return g; }
    private static int[] Grid3(params int[] ids) { var g = new int[9]; Array.Copy(ids, g, ids.Length); return g; }

    [Fact]
    public void Craft_Log_Alone_YieldsFourPlanks()
    {
        var r = CraftingSystem.MatchRecipe(Grid2(Block.LOG), 2);
        Assert.NotNull(r);
        Assert.Equal(Block.PLANKS, r!.ResultId);
        Assert.Equal(4, r.ResultCount);
    }

    [Fact]
    public void Craft_TwoPlanksVertically_YieldsSticks()
    {
        // Grid2 is row-major: (row0col0, row0col1, row1col0, row1col1).
        // The stick recipe is one column of two planks.
        var r = CraftingSystem.MatchRecipe(Grid2(0, Block.PLANKS, 0, Block.PLANKS), 2);
        Assert.NotNull(r);
        Assert.Equal(ItemId.STICK, r!.ResultId);
        Assert.Equal(2, r.ResultCount);
    }

    [Fact]
    public void Craft_FourPlanks_YieldsCraftingTable_NotBed()
    {
        // Both recipes share a 2x2-planks shape; first match wins, exactly as in JS.
        var r = CraftingSystem.MatchRecipe(Grid2(Block.PLANKS, Block.PLANKS, Block.PLANKS, Block.PLANKS), 2);
        Assert.NotNull(r);
        Assert.Equal(Block.CRAFTING_TABLE, r!.ResultId);
    }

    [Fact]
    public void Craft_ThreeByTwoPlanks_YieldsDoor_OnlyOn3x3Grid()
    {
        Assert.Null(CraftingSystem.MatchRecipe(Grid2(Block.PLANKS, Block.PLANKS, Block.PLANKS, Block.PLANKS), 2) is { } r
                    && r.ResultId == Block.DOOR_BOTTOM_CLOSED ? r : null);

        var g = Grid3(Block.PLANKS, Block.PLANKS, 0, Block.PLANKS, Block.PLANKS, 0, Block.PLANKS, Block.PLANKS, 0);
        var door = CraftingSystem.MatchRecipe(g, 3);
        Assert.NotNull(door);
        Assert.Equal(Block.DOOR_BOTTOM_CLOSED, door!.ResultId);
    }

    [Fact]
    public void Craft_RejectsPatternWhenSurroundingCellsAreOccupied()
    {
        // A log in the top-left of a 2x2 matches only if every cell outside the
        // 1x1 pattern is empty.
        Assert.NotNull(CraftingSystem.MatchRecipe(Grid2(Block.LOG, 0, 0, 0), 2));
        Assert.Null(CraftingSystem.MatchRecipe(Grid2(Block.LOG, Block.DIRT, 0, 0), 2));
    }

    [Fact]
    public void Craft_WoodenPickaxe_On3x3Grid()
    {
        var g = Grid3(Block.PLANKS, Block.PLANKS, Block.PLANKS,
                      0, ItemId.STICK, 0,
                      0, ItemId.STICK, 0);
        var r = CraftingSystem.MatchRecipe(g, 3);
        Assert.NotNull(r);
        Assert.Equal(ItemId.WP, r!.ResultId);
    }

    [Fact]
    public void TakeOutput_ConsumesOneFromEveryGridCell()
    {
        var c = new CraftingSystem();
        c.OpenCraftingTable();
        c.Grid[0] = new ItemStack { Id = Block.PLANKS, Count = 2 };
        c.Grid[1] = new ItemStack { Id = Block.PLANKS, Count = 1 };
        c.Grid[3] = new ItemStack { Id = Block.PLANKS, Count = 1 };
        c.Grid[4] = new ItemStack { Id = Block.PLANKS, Count = 1 };

        var result = c.TakeOutput(single: false);
        Assert.NotNull(result);
        Assert.Equal(Block.CRAFTING_TABLE, result!.Value.Id);

        Assert.Equal(1, c.Grid[0].Count);
        Assert.True(c.Grid[1].IsEmpty);
    }

    [Fact]
    public void MiningSpeed_MatchesConfigTable()
    {
        Assert.Equal(6, ItemTable.GetMiningSpeed(Block.STONE, ItemId.WP));
        Assert.Equal(6, ItemTable.GetMiningSpeed(Block.IRON_ORE, ItemId.SP));
        Assert.Equal(3, ItemTable.GetMiningSpeed(Block.LOG, ItemId.WA));
        Assert.Equal(1, ItemTable.GetMiningSpeed(Block.STONE, ItemId.WA));   // wrong tool
        Assert.Equal(1, ItemTable.GetMiningSpeed(Block.STONE, 0));           // bare hand
    }

    [Fact]
    public void ToolTier_GatesHarvest()
    {
        Assert.False(ItemTable.CanHarvest(Block.IRON_ORE, ItemId.WP));  // needs tier 2
        Assert.True(ItemTable.CanHarvest(Block.IRON_ORE, ItemId.SP));
        Assert.True(ItemTable.CanHarvest(Block.STONE, ItemId.WP));      // needs tier 1
        Assert.False(ItemTable.CanHarvest(Block.STONE, 0));             // bare hand
        Assert.True(ItemTable.CanHarvest(Block.DIRT, 0));               // no tool required
    }
}
