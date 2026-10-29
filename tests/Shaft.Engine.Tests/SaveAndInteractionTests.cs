using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Tests;

public class SaveSystemTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "shaft-save-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryField()
    {
        string root = TempDir();
        var s = new GameSession(12345, root);
        s.StartNewGame(0, Gamemode.Survival);

        // Give the world something to remember.
        s.Player.X = 12.5; s.Player.Y = 33.25; s.Player.Z = -7.75;
        s.Player.Yaw = 1.234f; s.Player.Pitch = -0.42f;
        s.Player.Health = 13; s.Player.Hunger = 9;
        s.Player.Flying = true;
        s.Inventory.SelectedIndex = 4;
        s.Inventory.Add(Block.COBBLESTONE, 37);
        s.Inventory.Add(ItemId.SP, 1);
        s.Inventory.Slots[0].Durability = 77;

        s.World.SetBlock(5, 12, 6, Block.PLANKS);
        s.World.SetFacing(5, 12, 6, 2);

        s.Chests.OpenChest(1, 2, 3);
        s.Chests.GetState(1, 2, 3)[0] = new ItemStack { Id = ItemId.COAL, Count = 11 };

        s.Furnaces.OpenFurnace(4, 5, 6);
        var f = s.Furnaces.GetState(4, 5, 6);
        f.Input = new ItemStack { Id = ItemId.RAW_IRON, Count = 3 };
        f.Fuel = new ItemStack { Id = ItemId.COAL, Count = 2 };
        f.Output = new ItemStack { Id = ItemId.IRON_INGOT, Count = 1 };
        f.FuelTime = 12.5f; f.Progress = 3.25f;

        s.Mining.SpawnDroppedItem(8, 9, 10, ItemId.BEEF, 2);
        var mob = s.Mobs.SpawnMob(MobType.Zombie, 20, 20);
        mob.Health = 14; mob.Burning = true; mob.BurnTimer = 0.75f;
        var cow = s.Mobs.SpawnMob(MobType.Cow, 21, 21);
        cow.SetPersistent();
        cow.Health = 6;

        s.DayNight.SetWorldTime(321.5f);
        s.Save();

        // Reload into a brand-new session built on the same seed.
        var s2 = new GameSession(12345, root);
        s2.LoadGame(0);

        Assert.Equal(12.5, s2.Player.X, 3);
        Assert.Equal(33.25, s2.Player.Y, 3);
        Assert.Equal(-7.75, s2.Player.Z, 3);
        Assert.Equal(1.234f, s2.Player.Yaw, 4);
        Assert.Equal(-0.42f, s2.Player.Pitch, 4);
        Assert.Equal(13f, s2.Player.Health);
        Assert.Equal(9f, s2.Player.Hunger);
        Assert.True(s2.Player.Flying);
        Assert.Equal(Gamemode.Survival, s2.Player.Mode);
        Assert.Equal(4, s2.Inventory.SelectedIndex);

        Assert.Equal(Block.COBBLESTONE, s2.Inventory.Slots[0].Id);
        Assert.Equal(37, s2.Inventory.Slots[0].Count);
        Assert.Equal(77, s2.Inventory.Slots[0].Durability);
        Assert.Equal(ItemId.SP, s2.Inventory.Slots[1].Id);

        Assert.Equal(Block.PLANKS, s2.World.BlockChanges[new BlockPos(5, 12, 6)]);
        Assert.Equal(2, s2.World.GetFacing(5, 12, 6));

        Assert.Equal(ItemId.COAL, s2.Chests.GetState(1, 2, 3)[0].Id);
        Assert.Equal(11, s2.Chests.GetState(1, 2, 3)[0].Count);

        var f2 = s2.Furnaces.GetState(4, 5, 6);
        Assert.Equal(ItemId.RAW_IRON, f2.Input.Id);
        Assert.Equal(3, f2.Input.Count);
        Assert.Equal(ItemId.COAL, f2.Fuel.Id);
        Assert.Equal(ItemId.IRON_INGOT, f2.Output.Id);
        Assert.Equal(12.5f, f2.FuelTime, 3);
        Assert.Equal(3.25f, f2.Progress, 3);

        Assert.Single(s2.Mining.DroppedItems);
        Assert.Equal(ItemId.BEEF, s2.Mining.DroppedItems[0].Id);
        Assert.Equal(2, s2.Mining.DroppedItems[0].Count);

        Assert.Equal(2, s2.Mobs.Mobs.Count);
        var z = s2.Mobs.Mobs.First(m => m.Type == MobType.Zombie);
        Assert.Equal(14f, z.Health);
        Assert.True(z.Burning);
        Assert.Equal(0.75f, z.BurnTimer, 3);
        var cw = s2.Mobs.Mobs.First(m => m.Type == MobType.Cow);
        Assert.True(cw.Persistent);
        Assert.True(cw.HasHome);

        Assert.Equal(321.5f, s2.DayNight.WorldTime, 3);

        Directory.Delete(root, true);
    }

    [Fact]
    public void Save_HeaderMatchesJavaScriptLayout()
    {
        // save.js writes: u16 version, u32 seed, f32 worldTime, u8 gamemode.
        string root = TempDir();
        var s = new GameSession(987654, root);
        s.StartNewGame(3, Gamemode.Survival);
        s.DayNight.SetWorldTime(100f);
        s.Save();

        byte[] bytes = File.ReadAllBytes(Path.Combine(root, "shaft_save_v6_slot_3.dat"));
        Assert.Equal(6, bytes[0] | (bytes[1] << 8));                        // SAVE_VERSION
        Assert.Equal(987654u, BitConverter.ToUInt32(bytes, 2));              // seed, little-endian
        Assert.Equal(100f, BitConverter.ToSingle(bytes, 6));                 // worldTime
        Assert.Equal(1, bytes[10]);                                          // survival
        // Offsets: u16 version (0), u32 seed (2), f32 worldTime (6), u8 gamemode (10),
        // f32 x,y,z (11), f32 yaw,pitch (23), f32 health,hunger (31),
        // u8 flying (39), u8 selectedIndex (40), u8 pad (41), u32 slot count (42).
        Assert.Equal(36u, BitConverter.ToUInt32(bytes, 42));

        Directory.Delete(root, true);
    }

    [Fact]
    public void UntamedCows_AreNotSaved_ButZombiesAre()
    {
        string root = TempDir();
        var s = new GameSession(55, root);
        s.StartNewGame(0, Gamemode.Survival);
        s.Mobs.SpawnMob(MobType.Cow, 10, 10);              // untamed -> not persisted
        s.Mobs.SpawnMob(MobType.Zombie, 11, 11);           // always persisted
        var tamed = s.Mobs.SpawnMob(MobType.Cow, 12, 12);
        tamed.SetPersistent();                              // tamed -> persisted
        s.Save();

        var s2 = new GameSession(55, root);
        s2.LoadGame(0);
        Assert.Equal(2, s2.Mobs.Mobs.Count);
        Assert.Single(s2.Mobs.Mobs.Where(m => m.Type == MobType.Zombie));
        Assert.Single(s2.Mobs.Mobs.Where(m => m.Type == MobType.Cow));

        Directory.Delete(root, true);
    }

    [Fact]
    public void SlotMetadata_IsTrackedAndFreed()
    {
        string root = TempDir();
        var s = new GameSession(5, root);
        s.StartNewGame(0, Gamemode.Creative);
        s.Save();

        Assert.Equal(1, s.Saves.NextFreeSlot());
        Assert.Single(s.Saves.GetSavesMeta().Saves);

        s.Saves.DeleteSlot(0);
        Assert.Equal(0, s.Saves.NextFreeSlot());
        Directory.Delete(root, true);
    }
}

public class InteractionTests
{
    private static GameSession Session()
        => new(12345, Path.Combine(Path.GetTempPath(), "shaft-int-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void BreakBlock_ClearsVoxelAndQueuesRebuild()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        var target = new BlockHit(3, 12, 3, Block.STONE, 3, 13, 3);

        s.Mining.BreakBlockInstant(target);

        Assert.Equal(Block.AIR, s.World.GetBlock(3, 12, 3));
        Assert.True(s.World.RebuildQueue.Count > 0);
    }

    [Fact]
    public void BreakStone_DropsCobblestone_OnlyWithAPickaxe()
    {
        // STONE is in TOOL_REQUIRED_LEVEL at tier 1, so a bare hand gets nothing.
        var bare = Session();
        bare.StartNewGame(0, Gamemode.Survival);
        bare.Mining.BreakBlockInstant(new BlockHit(3, 12, 3, Block.STONE, 3, 13, 3));
        Assert.Empty(bare.Mining.DroppedItems);

        var s = Session();
        s.StartNewGame(0, Gamemode.Survival);
        s.Inventory.Add(ItemId.WP, 1);
        s.Mining.BreakBlockInstant(new BlockHit(3, 12, 3, Block.STONE, 3, 13, 3));
        Assert.Contains(s.Mining.DroppedItems, d => d.Id == Block.COBBLESTONE);
    }

    [Fact]
    public void BreakingDoor_RemovesBothHalves()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(2, 20, 2, Block.DOOR_BOTTOM_CLOSED);
        s.World.SetBlock(2, 21, 2, Block.DOOR_TOP_CLOSED);
        s.World.SetFacing(2, 20, 2, 1);
        s.World.SetFacing(2, 21, 2, 1);

        s.Mining.BreakBlockInstant(new BlockHit(2, 20, 2, Block.DOOR_BOTTOM_CLOSED, 2, 20, 3));

        Assert.Equal(Block.AIR, s.World.GetBlock(2, 20, 2));
        Assert.Equal(Block.AIR, s.World.GetBlock(2, 21, 2));
    }

    [Fact]
    public void BreakingBed_RemovesBothHalves()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(4, 20, 4, Block.BED_FOOT);
        s.World.SetBlock(5, 20, 4, Block.BED_HEAD);

        s.Mining.BreakBlockInstant(new BlockHit(4, 20, 4, Block.BED_FOOT, 4, 21, 4));

        Assert.Equal(Block.AIR, s.World.GetBlock(4, 20, 4));
        Assert.Equal(Block.AIR, s.World.GetBlock(5, 20, 4));
    }

    [Fact]
    public void BreakingChest_SpillsContentsInSurvival()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Survival);
        s.World.SetBlock(6, 20, 6, Block.CHEST);
        s.Chests.GetState(6, 20, 6)[0] = new ItemStack { Id = ItemId.COAL, Count = 4 };

        s.Mining.BreakBlockInstant(new BlockHit(6, 20, 6, Block.CHEST, 6, 21, 6));

        Assert.Contains(s.Mining.DroppedItems, d => d.Id == ItemId.COAL && d.Count == 4);
        Assert.Empty(s.Chests.States);
    }

    [Fact]
    public void PlaceBlock_RefusesToOverlapThePlayer()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.Player.X = 8.5; s.Player.Y = 20; s.Player.Z = 8.5;

        Assert.False(s.Mining.PlaceBlockAt(8, 20, 8, Block.STONE));   // feet cell
        Assert.False(s.Mining.PlaceBlockAt(8, 21, 8, Block.STONE));   // head cell
        Assert.True(s.Mining.PlaceBlockAt(10, 20, 8, Block.STONE));   // clear
    }

    [Fact]
    public void PlaceBlock_ConsumesFromInventoryInSurvival()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Survival);
        s.Inventory.Add(Block.PLANKS, 3);

        Assert.True(s.Mining.PlaceBlockAt(20, 30, 20, Block.PLANKS));
        Assert.Equal(2, s.Inventory.CountOf(Block.PLANKS));

        s.Inventory.Remove(Block.PLANKS, 2);
        Assert.False(s.Mining.PlaceBlockAt(21, 30, 20, Block.PLANKS));   // none left
    }

    [Fact]
    public void PlaceDoor_RequiresAirAboveAndSetsBothFacings()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(30, 21, 30, Block.STONE);   // blocked above
        Assert.False(s.Mining.PlaceBlockAt(30, 20, 30, Block.DOOR_BOTTOM_CLOSED));

        s.World.SetBlock(30, 21, 30, Block.AIR);
        Assert.True(s.Mining.PlaceBlockAt(32, 20, 32, Block.DOOR_BOTTOM_CLOSED));
        Assert.Equal(Block.DOOR_BOTTOM_CLOSED, s.World.GetBlock(32, 20, 32));
        Assert.Equal(Block.DOOR_TOP_CLOSED, s.World.GetBlock(32, 21, 32));
        Assert.Equal(s.World.GetFacing(32, 20, 32), s.World.GetFacing(32, 21, 32));
    }

    [Fact]
    public void Raycast_FindsTheBlockUnderTheCrosshair()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        var c = s.World.GetOrGenerateChunk(0, 0);
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;
        // Eye height is pos.y + 1.6, so a level ray stays inside the y=21 cell.
        c.Set(8, 21, 4, Block.STONE);
        c.RecomputeOccupancy();

        // Player at z=8.5 aiming at a block at z=4 means looking toward -Z, which is
        // yaw 0 with three.js's YXZ camera convention.
        s.Player.X = 8.5; s.Player.Y = 20; s.Player.Z = 8.5;
        s.Player.Yaw = 0f;
        s.Player.Pitch = 0f;
        var hit = s.Mining.GetTargetedBlock();
        Assert.NotNull(hit);
        Assert.Equal(Block.STONE, hit!.Value.Block);
        Assert.Equal(4, hit.Value.Z);
        Assert.Equal(5, hit.Value.PlaceZ);   // the air cell in front of it
    }

    [Fact]
    public void Water_FlowsFromSourceIntoAdjacentAir()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        var c = s.World.GetOrGenerateChunk(0, 0);
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;

        // Floor, then a source block with air to the east.
        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++) c.Set(lx, 10, lz, Block.STONE);
        c.Set(8, 11, 8, Block.WATER);
        c.RecomputeOccupancy();

        s.Water.QueueWaterUpdate(8, 11, 8);
        s.Water.Update(GameConfig.WATER_TICK_INTERVAL + 0.001f, 8.5, 8.5);

        Assert.Equal(Block.WATER_FLOW_6, s.World.GetBlock(9, 11, 8));
        Assert.Equal(Block.WATER_FLOW_6, s.World.GetBlock(7, 11, 8));
    }

    [Fact]
    public void Water_FlowLevelDecaysWithDistance()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        var c = s.World.GetOrGenerateChunk(0, 0);
        for (int i = 0; i < c.Blocks.Length; i++) c.Blocks[i] = Block.AIR;
        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++) c.Set(lx, 10, lz, Block.STONE);
        c.Set(2, 11, 8, Block.WATER);
        c.RecomputeOccupancy();

        s.Water.QueueWaterUpdate(2, 11, 8);
        for (int i = 0; i < 12; i++) s.Water.Update(GameConfig.WATER_TICK_INTERVAL + 0.001f, 2.5, 8.5);

        Assert.Equal(Block.WATER_FLOW_6, s.World.GetBlock(3, 11, 8));
        Assert.Equal(Block.WATER_FLOW_5, s.World.GetBlock(4, 11, 8));
        Assert.Equal(Block.WATER_FLOW_4, s.World.GetBlock(5, 11, 8));
    }

    [Fact]
    public void Furnace_SmeltsRawIron_UsingCoal()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(2, 20, 2, Block.FURNACE);
        var f = s.Furnaces.GetState(2, 20, 2);
        f.Input = new ItemStack { Id = ItemId.RAW_IRON, Count = 2 };
        f.Fuel = new ItemStack { Id = ItemId.COAL, Count = 1 };

        // Lighting the furnace flips the block to WORKING_FURNACE so the lit
        // texture shows, exactly as updateFurnaces does.
        for (int i = 0; i < 60 && s.World.GetBlock(2, 20, 2) != Block.WORKING_FURNACE; i++)
            s.Furnaces.Update(1f / 60f);
        Assert.Equal(Block.WORKING_FURNACE, s.World.GetBlock(2, 20, 2));

        // The raw-iron recipe needs 10 s of accumulated progress.
        for (int i = 0; i < 60 * 11; i++) s.Furnaces.Update(1f / 60f);

        Assert.Equal(ItemId.IRON_INGOT, f.Output.Id);
        Assert.Equal(1, f.Output.Count);
        Assert.Equal(1, f.Input.Count);
    }

    [Fact]
    public void Furnace_DoesNothingWithoutFuel()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(2, 20, 2, Block.FURNACE);
        var f = s.Furnaces.GetState(2, 20, 2);
        f.Input = new ItemStack { Id = ItemId.BEEF, Count = 1 };

        for (int i = 0; i < 60 * 20; i++) s.Furnaces.Update(1f / 60f);

        Assert.True(f.Output.IsEmpty);
        Assert.Equal(1, f.Input.Count);
    }

    [Fact]
    public void Commands_MatchTheJavaScriptTable()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);

        s.RunCommand("/time set noon");
        Assert.Equal(GameConfig.DAY_LENGTH * 0.5f, s.DayNight.WorldTime, 3);

        s.RunCommand("/time set midnight");
        Assert.Equal(0f, s.DayNight.WorldTime);

        Assert.Contains("Unknown time", s.RunCommand("/time set blorple"));
        Assert.Contains("Usage:", s.RunCommand("/time"));

        s.RunCommand("/tp 100 40 -200");
        Assert.Equal(100, s.Player.X);
        Assert.Equal(40, s.Player.Y);
        Assert.Equal(-200, s.Player.Z);

        Assert.Contains("Y must be between", s.RunCommand("/tp 0 9000 0"));
        Assert.Contains("must be numbers", s.RunCommand("/tp a b c"));
        Assert.Contains("Usage:", s.RunCommand("/tp 1"));

        Assert.Contains("/help", s.RunCommand("/help"));
        Assert.Contains("Unknown command", s.RunCommand("/frobnicate"));

        Assert.Contains("blocks away", s.RunCommand("/locate biome forest"));
        Assert.Contains("Unknown biome", s.RunCommand("/locate biome atlantis"));
    }

    [Fact]
    public void Gamemode_TogglesAndRestoresVitals()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Survival);
        s.Player.Health = 5;
        s.ToggleGamemode();
        Assert.Equal(Gamemode.Creative, s.Player.Mode);
        Assert.Equal(20f, s.Player.Health);
        s.ToggleGamemode();
        Assert.Equal(Gamemode.Survival, s.Player.Mode);
    }

    [Fact]
    public void Sleeping_SkipsToMorning()
    {
        var s = Session();
        s.StartNewGame(0, Gamemode.Creative);
        s.World.SetBlock(3, 20, 3, Block.BED_FOOT);
        s.DayNight.SetWorldTime(GameConfig.DAY_LENGTH * 0.85f);   // night
        s.Player.Health = 5f;
        s.Player.Hunger = 3f;

        Assert.True(s.TrySleep(new BlockHit(3, 20, 3, Block.BED_FOOT, 3, 21, 3)));
        Assert.True(s.Sleeping);
        s.WakeUp();
        Assert.False(s.Sleeping);
        // sleeping.js:47 -> setWorldTimeGlobal(600 * 0.27), i.e. dawn just past sunrise.
        Assert.Equal(GameConfig.DAY_LENGTH * 0.27f, s.DayNight.WorldTime, 3);
        // sleeping.js:50-51 -> health = maxHealth; hunger = max(hunger, 10).
        Assert.Equal(s.Player.MaxHealth, s.Player.Health, 3);
        Assert.Equal(10f, s.Player.Hunger, 3);

        // A full hunger bar is left alone by the Math.max clamp.
        s.Player.Hunger = 18f;
        Assert.True(s.TrySleep(new BlockHit(3, 20, 3, Block.BED_FOOT, 3, 21, 3)));
        s.WakeUp();
        Assert.Equal(18f, s.Player.Hunger, 3);
    }

    [Fact]
    public void DayNight_CycleDrivesSunAndSky()
    {
        var d = new DayNightSystem();
        Assert.Equal(GameConfig.DAY_LENGTH * 0.25f, d.WorldTime, 3);

        d.SetWorldTime(GameConfig.DAY_LENGTH * 0.5f);   // noon
        Assert.True(d.SunY > 0.9f);
        Assert.False(d.IsNightTime);
        Assert.Equal("Day", d.TimeOfDayName);

        d.SetWorldTime(0f);                              // midnight
        Assert.True(d.SunY < -0.9f);
        Assert.True(d.IsNightTime);
        Assert.Equal("Night", d.TimeOfDayName);

        // sunDirection is normalised in daynight.js, so the raw sin() is not the
        // value the name bands test. At t=0.25 the angle is 0 and SunY is exactly 0,
        // which lands in the [-0.1, 0.4) Dusk/Dawn band.
        d.SetWorldTime(GameConfig.DAY_LENGTH * 0.25f);
        Assert.Equal(0f, d.SunY, 3);
        Assert.Equal("Dusk/Dawn", d.TimeOfDayName);
    }

    [Fact]
    public void DayNight_WrapsAround()
    {
        var d = new DayNightSystem();
        d.SetWorldTime(GameConfig.DAY_LENGTH - 1f);
        d.Update(2f);
        Assert.InRange(d.WorldTime, 0f, GameConfig.DAY_LENGTH);
        Assert.True(d.WorldTime < 2f);
    }
}
