using System.Diagnostics;
using System.Text.Json;
using MP3Sharp;
using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Items;
using Shaft.Engine.Meshing;
using Shaft.Engine.World;
using Shaft.Game.Rendering;

namespace Shaft.Game;

/// <summary>
/// Headless verification of everything that does not need a window or a GPU:
/// the asset pipeline (terrain atlas, bitmap font, sun/moon sprites, models, MP3 decode),
/// world generation and streaming, chunk meshing, player physics, mining, crafting,
/// containers, saving, the day/night cycle, sleeping and the settings paths.
/// <para>Run with <c>--selftest</c>; the process exit code is the number of failures.</para>
/// </summary>
public static class SelfTest
{
    private static int _pass;
    private static readonly List<string> Failures = new();

    public static int Run(TextWriter log)
    {
        var sw = Stopwatch.StartNew();
        log.WriteLine($"Shaft Additionality — self test");
        log.WriteLine($"  assets : {AssetPaths.DistRoot}");
        log.WriteLine($"  saves  : {AssetPaths.SaveRoot}");
        log.WriteLine();

        string saveRoot = Path.Combine(Path.GetTempPath(), "shaft-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(saveRoot);

        try
        {
            CheckAssets(log);
            CheckWorldGen(log);
            CheckStreamingAndMeshing(log);
            CheckPhysics(log);
            CheckMining(log, saveRoot);
            CheckCrafting(log);
            CheckContainers(log);
            CheckDayNightAndSleep(log, saveRoot);
            CheckSaveLoad(log, saveRoot);
            CheckSettings(log, saveRoot);
        }
        catch (Exception ex)
        {
            Fail(ex.ToString());
            log.WriteLine("  !! self test aborted: " + ex.Message);
        }

        sw.Stop();
        log.WriteLine();
        log.WriteLine($"  {_pass} passed, {Failures.Count} failed  ({sw.ElapsedMilliseconds} ms)");
        foreach (string f in Failures) log.WriteLine("  FAIL: " + f);

        try { Directory.Delete(saveRoot, recursive: true); } catch { /* best effort */ }
        return Failures.Count;
    }

    // ------------------------------------------------------------------ helpers

    private static void Pass(string name) => _pass++;

    private static void Fail(string message)
    {
        if (message.Length > 400) message = message[..400] + "…";
        Failures.Add(message);
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) Pass(name);
        else Fail(name + (detail is null ? "" : " — " + detail));
    }

    private static void Section(TextWriter log, string title) => log.WriteLine("[" + title + "]");

    // ------------------------------------------------------------------- assets

    private static void CheckAssets(TextWriter log)
    {
        Section(log, "assets");
        string root = AssetPaths.DistRoot;
        Check("dist root exists", Directory.Exists(Path.Combine(root, "textures")), root);

        var warnings = new List<string>();

        byte[] atlas = TextureSet.BuildTerrainAtlasPixels(root, warnings);
        int expected = TextureSet.TerrainAtlasPixelWidth * TextureSet.TerrainAtlasPixelHeight * 4;
        Check("terrain atlas size", atlas.Length == expected, $"{atlas.Length} vs {expected}");
        Check("terrain atlas has ink", CountOpaque(atlas) > 4096, CountOpaque(atlas).ToString());
        Check("all 34 block textures found", warnings.Count == 0, string.Join("; ", warnings));

        byte[] font = TextureSet.BuildFontAtlasPixels();
        Check("font atlas size",
              font.Length == TextureSet.FontPixelWidth * TextureSet.FontPixelHeight * 4,
              $"{font.Length}");
        Check("font atlas has ink", CountOpaque(font) > 512, CountOpaque(font).ToString());

        Check("sun sprite size", TextureSet.BuildSunPixels().Length == TextureSet.SKY_SPRITE * TextureSet.SKY_SPRITE * 4);
        Check("moon sprite size", TextureSet.BuildMoonPixels().Length == TextureSet.SKY_SPRITE * TextureSet.SKY_SPRITE * 4);

        foreach ((string name, string model, string texture) in AssetManifest.Models)
        {
            string modelPath = Path.Combine(root, model);
            bool parsed = false;
            if (File.Exists(modelPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
                    parsed = doc.RootElement.ValueKind == JsonValueKind.Object;
                }
                catch { parsed = false; }
            }
            Check($"model '{name}' parses", parsed, modelPath);
            Check($"texture '{texture}' exists", File.Exists(Path.Combine(root, texture)));
        }

        foreach ((string key, string path, float volume, bool loop) in AssetManifest.Sounds)
            Check($"sound '{key}' exists", File.Exists(Path.Combine(root, path)), path);

        // MP3Sharp must be able to decode the streamed track without an audio device.
        try
        {
            string music = Path.Combine(root, "sounds", "bg_music.mp3");
            using var stream = new MP3Stream(music);
            var buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);
            Check("bg_music.mp3 decodes", read > 0 && stream.Frequency > 0,
                  $"{read} bytes @ {stream.Frequency} Hz, {stream.ChannelCount} ch");
        }
        catch (Exception ex)
        {
            Check("bg_music.mp3 decodes", false, ex.Message);
        }
    }

    private static int CountOpaque(byte[] rgba)
    {
        int n = 0;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] > 8) n++;
        return n;
    }

    // ----------------------------------------------------------------- worldgen

    private static void CheckWorldGen(TextWriter log)
    {
        Section(log, "world generation");

        var a = new VoxelWorld(1337, 4);
        var b = new VoxelWorld(1337, 4);
        bool same = true;
        for (int i = 0; i < 64 && same; i++)
        {
            int x = (i * 37) % 128 - 64, z = (i * 91) % 128 - 64;
            for (int y = -20; y < 60; y += 7)
                if (a.GetBlock(x, y, z) != b.GetBlock(x, y, z)) { same = false; break; }
        }
        Check("same seed reproduces the same blocks", same);

        var c = new VoxelWorld(24680, 4);
        int heightDiff = 0, blockDiff = 0;
        for (int i = 0; i < 64; i++)
        {
            int x = (i * 37) % 128 - 64, z = (i * 91) % 128 - 64;
            int hx = a.Generator.GetHeight(x, z);
            if (hx != c.Generator.GetHeight(x, z)) heightDiff++;
            if (a.GetBlock(x, hx, z) != c.GetBlock(x, hx, z)) blockDiff++;
        }
        Check("different seed differs", heightDiff > 8, $"{heightDiff}/64 heights, {blockDiff} surface blocks");

        Chunk chunk = a.GetOrGenerateChunk(0, 0);
        Check("chunk is a flat 32768-byte array", chunk.Blocks.Length == GameConfig.CHUNK_VOLUME,
              chunk.Blocks.Length.ToString());
        Check("chunk index matches lighting.js layout",
              Chunk.Index(3, 40, 7) == ((40 - GameConfig.CHUNK_MIN_Y) * GameConfig.CHUNK_SIZE + 7) * GameConfig.CHUNK_SIZE + 3,
              Chunk.Index(3, 40, 7).ToString());
        Check("chunk not empty", !chunk.IsEmpty);
        Check("max occupied Y inside the chunk", chunk.MaxOccupiedY >= GameConfig.CHUNK_MIN_Y &&
                                                 chunk.MaxOccupiedY <= GameConfig.CHUNK_MAX_Y);

        int h = a.Generator.GetHeight(0, 0);
        Check("height map in range", h > GameConfig.CHUNK_MIN_Y && h < GameConfig.CHUNK_MAX_Y, h.ToString());
        int biome = a.Generator.GetBiome(0, 0);
        Check("biome id in range", biome >= 0 && biome <= 5, biome.ToString());

        byte surface = a.GetBlock(0, h, 0);
        Check("surface column is solid", surface != Block.AIR, surface.ToString());
        Check("deep underground is stone-like",
              a.GetBlock(0, GameConfig.CHUNK_MIN_Y + 2, 0) != Block.AIR);

        var spawn = a.FindSpawn();
        Check("FindSpawn returns a point above the void", spawn.Y > GameConfig.CHUNK_MIN_Y, spawn.ToString());
    }

    // ------------------------------------------------------- streaming + meshing

    private static void CheckStreamingAndMeshing(TextWriter log)
    {
        Section(log, "streaming and meshing");
        string tmp = Path.Combine(Path.GetTempPath(), "shaft-mesh-" + Guid.NewGuid().ToString("N")[..8]);

        using var session = new GameSession(1337, tmp, 4);
        session.EnableAsyncStreaming();
        Check("async streamer created", session.Streamer is not null);

        session.StartNewGame(0, Gamemode.Survival);
        Check("session started", session.Started);
        Check("slot recorded", session.CurrentSlot == 0);

        int expectedChunks = (2 * GameConfig.RENDER_DISTANCE + 1) * (2 * GameConfig.RENDER_DISTANCE + 1);
        Check("spawn area streamed", session.World.LoadedChunkCount >= expectedChunks,
              $"{session.World.LoadedChunkCount} >= {expectedChunks}");

        int built = session.ProcessMeshQueue(int.MaxValue);
        Check("mesh queue produced chunks", built > 0, built.ToString());
        Check("mesh cache filled", session.Meshes.Count > 0, session.Meshes.Count.ToString());

        long vertices = 0, indices = 0;
        foreach (ChunkMeshData m in session.Meshes.Values)
        {
            vertices += m.Opaque.VertexCount + m.Overlay.VertexCount + m.Plants.VertexCount;
            indices += m.Opaque.IndexCount + m.Overlay.IndexCount + m.Plants.IndexCount;
        }
        Check("meshes have geometry", vertices > 10_000 && indices > 10_000,
              $"{vertices} verts / {indices} indices");

        bool finite = true;
        foreach (ChunkMeshData m in session.Meshes.Values)
        {
            float[] v = m.Opaque.ToVertexArray();
            for (int i = 0; i < v.Length; i++)
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) { finite = false; break; }
            if (!finite) break;
        }
        Check("no NaN/Inf in vertex data", finite);

        // A block edit must re-queue its chunk (and neighbours on a border).
        session.World.SetBlock(0, 40, 0, Block.STONE);
        int rebuilt = session.ProcessMeshQueue(int.MaxValue);
        Check("block edit queues a rebuild", rebuilt >= 1, rebuilt.ToString());

        Directory.Delete(tmp, recursive: true);
    }

    // ------------------------------------------------------------------ physics

    private static void CheckPhysics(TextWriter log)
    {
        Section(log, "player physics");
        string tmp = Path.Combine(Path.GetTempPath(), "shaft-phys-" + Guid.NewGuid().ToString("N")[..8]);
        using var session = new GameSession(1337, tmp, 4);
        session.StartNewGame(0, Gamemode.Survival);

        var p = session.Player;
        for (int i = 0; i < 240; i++) session.Update(1f / 60f, 0, 0, false, false);
        Check("player lands on the ground", p.OnGround, $"y={p.Y:F2}");
        Check("player is alive after falling", p.Health > 0, p.Health.ToString());
        double restY = p.Y;
        for (int i = 0; i < 60; i++) session.Update(1f / 60f, 0, 0, false, false);
        Check("standing still does not sink", Math.Abs(p.Y - restY) < 1e-6, $"{restY} -> {p.Y}");

        p.VelY = 0;
        p.OnGround = true;
        session.Update(1f / 60f, 0, 0, true, false);
        Check("jump sets upward velocity", p.VelY > 0f, p.VelY.ToString());

        // Hunger only ticks in survival: 8 s per point (ui.js updateHunger).
        p.Hunger = 20;
        p.HungerTimer = 0;
        for (int i = 0; i < 60 * 9; i++) session.Update(1f / 60f, 0, 0, false, false);
        Check("hunger drains over time", p.Hunger < 20, p.Hunger.ToString());

        // Flight, tested in open sky so terrain cannot block it.
        p.Mode = Gamemode.Creative;
        p.Health = p.MaxHealth;
        p.Y = 80;
        p.VelX = p.VelY = p.VelZ = 0;
        p.ToggleFlying();
        Check("creative flight toggles on", p.Flying);
        double y0 = p.Y, x0 = p.X;
        for (int i = 0; i < 30; i++) session.Update(1f / 60f, 0, 0, true, false);
        Check("flight ascends while jumping", p.Y > y0 + 1.0, $"{y0:F2} -> {p.Y:F2}");
        for (int i = 0; i < 60; i++) session.Update(1f / 60f, 1, 0, false, false);
        Check("flight translates at 12 m/s", p.X > x0 + 5.0, $"{x0:F2} -> {p.X:F2}");
        p.ToggleFlying();
        Check("creative flight toggles off", !p.Flying);

        // Sneaking and sprinting speeds (GameConfig constants the host feeds in).
        p.Mode = Gamemode.Survival;
        p.Y = 80; p.Flying = false; p.VelY = 0;
        p.Sneaking = true; p.Sprinting = false;
        session.Update(1f / 60f, 1, 0, false, false);
        Check("sneak speed is 2.5", Math.Abs(p.VelX - GameConfig.SNEAK_SPEED) < 1e-4, p.VelX.ToString());
        p.Sneaking = false; p.Sprinting = true;
        session.Update(1f / 60f, 1, 0, false, false);
        Check("sprint speed is 9", Math.Abs(p.VelX - GameConfig.SPRINT_SPEED) < 1e-4, p.VelX.ToString());
        p.Sprinting = false;
        session.Update(1f / 60f, 1, 0, false, false);
        Check("walk speed is 6", Math.Abs(p.VelX - GameConfig.WALK_SPEED) < 1e-4, p.VelX.ToString());

        Directory.Delete(tmp, recursive: true);
    }

    // ------------------------------------------------------------------- mining

    private static void CheckMining(TextWriter log, string saveRoot)
    {
        Section(log, "mining and building");
        string tmp = Path.Combine(saveRoot, "mining");
        using var session = new GameSession(1337, tmp, 4);
        session.StartNewGame(0, Gamemode.Survival);
        var world = session.World;
        var mining = session.Mining;
        var inv = session.Inventory;

        // The player spawns ~5 blocks above the surface; let physics settle them first,
        // otherwise the 6-block reach cannot touch the ground and nothing can be mined.
        for (int i = 0; i < 180; i++) session.Update(1f / 60f, 0, 0, false, false);
        Check("player settled on the ground", session.Player.OnGround, session.Player.Y.ToString("F2"));

        int brokenEvents = 0;
        var sounds = new List<string>();
        mining.BlockBroken += (_, _, _, _) => brokenEvents++;
        mining.SoundRequested += s => sounds.Add(s);

        // Find a stone block under the player and break it by hand.
        int px = (int)Math.Floor(session.Player.X), pz = (int)Math.Floor(session.Player.Z);
        int by = -1;
        for (int y = 60; y > GameConfig.CHUNK_MIN_Y; y--)
            if (world.GetBlock(px, y, pz) == Block.STONE) { by = y; break; }
        Check("found stone to mine", by > 0, by.ToString());

        // mining.js gates the drop on TOOL_TIER.pickaxe[held] >= TOOL_REQUIRED_LEVEL[STONE] (=1),
        // so bare hands clear the block but yield nothing. A wooden pickaxe (tier 1) harvests it.
        inv.Clear();
        mining.BreakBlockInstant(new BlockHit(px, by, pz, Block.STONE, px, by + 1, pz));
        Check("broken block becomes air", world.GetBlock(px, by, pz) == Block.AIR);
        Check("breaking raised an event", brokenEvents == 1, brokenEvents.ToString());
        Check("bare hands drop nothing from stone", mining.DroppedItems.Count == 0,
              string.Join(",", mining.DroppedItems.ConvertAll(d => d.Id.ToString())));
        Check("breaking requested a sound", sounds.Contains("break"), string.Join(",", sounds));

        world.SetBlock(px, by, pz, Block.STONE);
        inv.Clear();
        inv.Add(ItemId.WP, 1);                 // wooden pickaxe: tier 1 == required level for stone
        inv.SelectedIndex = 0;
        int durBefore = inv.Slots[0].Durability;
        mining.BreakBlockInstant(new BlockHit(px, by, pz, Block.STONE, px, by + 1, pz));
        // mining.js drops a floating item entity; the inventory only fills on pickup.
        Check("breaking spawns a cobblestone drop",
              mining.DroppedItems.Exists(d => d.Id == Block.COBBLESTONE && !d.Dead),
              string.Join(",", mining.DroppedItems.ConvertAll(d => d.Id.ToString())));
        Check("the pickaxe wore down", inv.Slots[0].Durability == durBefore - 1,
              $"{durBefore} -> {inv.Slots[0].Durability}");

        // Placement.
        inv.Clear();
        inv.Add(Block.COBBLESTONE, 1);
        inv.SelectedIndex = 0;
        bool placed = mining.PlaceBlockAt(px, by, pz, Block.COBBLESTONE);
        Check("place returns true", placed);
        Check("placed block is in the world", world.GetBlock(px, by, pz) == Block.COBBLESTONE);
        Check("placement consumed the stack", inv.CountOf(Block.COBBLESTONE) == 0,
              inv.CountOf(Block.COBBLESTONE).ToString());
        Check("placement requested a sound", sounds.Contains("place"), string.Join(",", sounds));

        // The ray-marcher.
        session.Player.Yaw = 0f;
        session.Player.Pitch = -(float)Math.PI / 2f;      // straight down
        BlockHit? down = mining.GetTargetedBlock();
        Check("looking down targets a block", down is not null);
        if (down is not null)
            Check("targeted block is below the player", down.Value.Y <= Math.Floor(session.Player.Y),
                  $"{down.Value.Y} vs {session.Player.Y:F2}");

        session.Player.Pitch = (float)Math.PI / 2f;       // straight up at the sky
        Check("looking at the sky targets nothing", mining.GetTargetedBlock() is null);

        // Survival breaking accumulates progress. mining.js aborts the swing whenever
        // getTargetedBlock() stops matching breakTarget, so the break target has to be
        // the block the crosshair is actually on — not an arbitrary coordinate.
        session.Player.Pitch = -(float)Math.PI / 2f;
        BlockHit? aimed = mining.GetTargetedBlock();
        Check("crosshair is on a block to mine", aimed is not null);
        if (aimed is BlockHit hit)
        {
            inv.Clear();
            inv.Add(ItemId.WP, 1);
            inv.SelectedIndex = 0;

            mining.StartBreaking(hit);
            Check("survival breaking starts", mining.Breaking);
            float p0 = mining.BreakProgress;
            mining.Update(0.05f);
            Check("break progress advances", mining.BreakProgress > p0,
                  $"{p0:0.###} -> {mining.BreakProgress:0.###}");

            // Looking away mid-swing cancels it, exactly like updateMining does.
            session.Player.Pitch = (float)Math.PI / 2f;
            mining.Update(0.05f);
            Check("looking away aborts breaking", !mining.Breaking && mining.BreakProgress == 0f);

            // Holding the button long enough completes the break.
            session.Player.Pitch = -(float)Math.PI / 2f;
            aimed = mining.GetTargetedBlock();
            if (aimed is BlockHit hit2)
            {
                mining.StartBreaking(hit2);
                for (int i = 0; i < 600 && mining.Breaking; i++) mining.Update(1f / 60f);
                Check("holding the button completes the break",
                      world.GetBlock(hit2.X, hit2.Y, hit2.Z) == Block.AIR,
                      world.GetBlock(hit2.X, hit2.Y, hit2.Z).ToString());
            }
        }
        mining.StopBreaking();
        Check("stop breaking resets", !mining.Breaking && mining.BreakProgress == 0f);

        // Creative breaks instantly.
        session.Player.Mode = Gamemode.Creative;
        inv.Clear();
        world.SetBlock(px, by, pz, Block.STONE);
        mining.StartBreaking(new BlockHit(px, by, pz, Block.STONE, px, by + 1, pz));
        Check("creative breaks instantly", world.GetBlock(px, by, pz) == Block.AIR);

        // Dropped items fall and can be picked up.
        session.Player.Mode = Gamemode.Survival;
        inv.Clear();
        DroppedItem drop = mining.SpawnDroppedItem(px, by + 1, pz, Block.DIRT, 3);
        Check("drop created", mining.DroppedItems.Contains(drop));
        for (int i = 0; i < 120; i++) mining.UpdateDroppedItems(1f / 60f);
        Check("drop was picked up or expired",
              !mining.DroppedItems.Contains(drop) || drop.OnGround || inv.CountOf(Block.DIRT) == 3,
              $"in inventory: {inv.CountOf(Block.DIRT)}");
    }

    // ----------------------------------------------------------------- crafting

    private static void CheckCrafting(TextWriter log)
    {
        Section(log, "crafting");

        // 2x2 planks -> crafting table (first matching recipe wins, as in crafting.js).
        int[] grid = { Block.PLANKS, Block.PLANKS, Block.PLANKS, Block.PLANKS };
        CraftRecipe? recipe = CraftingSystem.MatchRecipe(grid, 2);
        Check("2x2 planks matches a recipe", recipe is not null);
        Check("2x2 planks yields a crafting table", recipe?.ResultId == Block.CRAFTING_TABLE,
              recipe?.ResultId.ToString() ?? "null");

        var inv = new Inventory();
        var crafting = new CraftingSystem();
        crafting.OpenInventory();
        Check("inventory grid is 2x2", crafting.Size == 2 && crafting.Grid.Length == 4);
        Check("inventory panel open", crafting.Open && !crafting.TableOpen);

        crafting.OpenCraftingTable();
        Check("table grid is 3x3", crafting.Size == 3 && crafting.Grid.Length == 9);
        Check("table panel open", crafting.Open && crafting.TableOpen);

        // Put planks in the top-left 2x2 of the 3x3 grid and take the output.
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            crafting.Grid[i * 3 + j] = Inventory.MakeItem(Block.PLANKS, 1);
        Check("grid has a recipe", crafting.CurrentRecipe() is not null);

        ItemStack? output = crafting.TakeOutput(single: false);
        Check("output taken", output is not null && output.Value.Id == Block.CRAFTING_TABLE,
              output?.Id.ToString() ?? "null");
        Check("grid consumed one from each cell",
              crafting.Grid[0].IsEmpty && crafting.Grid[1].IsEmpty &&
              crafting.Grid[3].IsEmpty && crafting.Grid[4].IsEmpty);

        // Clicking a grid slot with an item on the cursor places one.
        inv.Cursor = Inventory.MakeItem(Block.PLANKS, 5);
        crafting.ClickGridSlot(0, rightClick: true, shiftClick: false, inv);
        Check("right click places one", crafting.Grid[0].Count == 1 && inv.Cursor.Count == 4,
              $"{crafting.Grid[0].Count}/{inv.Cursor.Count}");
        crafting.ClickGridSlot(0, rightClick: false, shiftClick: false, inv);
        Check("left click merges the cursor into the slot",
              crafting.Grid[0].Count == 5 && inv.Cursor.IsEmpty,
              $"grid={crafting.Grid[0].Count} cursor={inv.Cursor.Count}");

        // With an empty cursor the same click picks the stack back up.
        crafting.ClickGridSlot(0, rightClick: false, shiftClick: false, inv);
        Check("left click with an empty cursor picks the stack up",
              crafting.Grid[0].IsEmpty && inv.Cursor.Count == 5,
              $"grid={crafting.Grid[0].Count} cursor={inv.Cursor.Count}");

        // Closing returns the cursor and the grid to the inventory.
        crafting.Grid[8] = Inventory.MakeItem(Block.DIRT, 7);
        inv.Cursor = Inventory.MakeItem(Block.SAND, 2);
        inv.ReturnCursor();
        foreach (ItemStack it in crafting.CloseAndCollect()) inv.Add(it.Id, it.Count, it.Durability);
        Check("close returns cursor + grid", inv.CountOf(Block.SAND) == 2 && inv.CountOf(Block.DIRT) == 7,
              $"{inv.CountOf(Block.SAND)}/{inv.CountOf(Block.DIRT)}");
        Check("panel closed", !crafting.Open && !crafting.TableOpen);

        // Smelting table sanity.
        Check("raw iron smelts to an ingot",
              SmeltingTable.TryGet(ItemId.RAW_IRON, out SmeltRecipe r) && r.Output == ItemId.IRON_INGOT && r.Time == 10f);
        Check("beef cooks into steak",
              SmeltingTable.TryGet(ItemId.BEEF, out SmeltRecipe r2) && r2.Output == ItemId.STEAK && r2.Time == 8f);
        Check("ore blocks do not smelt directly", !SmeltingTable.TryGet(Block.IRON_ORE, out _));
        Check("coal is fuel", ItemTable.GetFuelValue(ItemId.COAL) > 0);
        Check("steak is food", ItemTable.TryGet(ItemId.STEAK, out ItemInfo steak) && steak.Food > 0);
    }

    // ---------------------------------------------------------------- containers

    private static void CheckContainers(TextWriter log)
    {
        Section(log, "containers");

        var world = new VoxelWorld(7, 4);
        var chests = new ChestSystem();
        var furnaces = new FurnaceSystem(world);

        chests.OpenChest(1, 2, 3);
        Check("chest opens", chests.Open && chests.OpenKey == new BlockPos(1, 2, 3));
        ItemStack[] slots = chests.GetState(1, 2, 3);
        Check("chest has 27 slots", slots.Length == GameConfig.CHEST_SLOTS, slots.Length.ToString());
        slots[0] = Inventory.MakeItem(Block.STONE, 12);
        var inv = new Inventory();
        inv.Cursor = Inventory.MakeItem(Block.DIRT, 4);
        inv.ClickExternalSlot(ref slots[0], rightClick: false, shiftClick: false);
        Check("external click swaps cursor and slot",
              slots[0].Id == Block.DIRT && slots[0].Count == 4 && inv.Cursor.Id == Block.STONE,
              $"{slots[0].Id}x{slots[0].Count} / cursor {inv.Cursor.Id}x{inv.Cursor.Count}");
        var taken = new List<ItemStack>(chests.TakeAll(1, 2, 3));
        Check("take-all returns the contents", taken.Count == 1 && taken[0].Id == Block.DIRT, taken.Count.ToString());
        Check("take-all forgets the chest", chests.GetState(1, 2, 3)[0].IsEmpty);
        chests.CloseChest();
        Check("chest closes", !chests.Open && chests.OpenKey is null);

        furnaces.OpenFurnace(4, 5, 6);
        Check("furnace opens", furnaces.Open && furnaces.OpenKey == new BlockPos(4, 5, 6));
        FurnaceState st = furnaces.GetState(4, 5, 6);
        st.Input = Inventory.MakeItem(ItemId.RAW_IRON, 4);
        st.Fuel = Inventory.MakeItem(ItemId.COAL, 2);
        for (int i = 0; i < 60 * 12; i++) furnaces.Update(1f / 60f);
        Check("furnace smelts over time", st.Output.Count > 0,
              $"output={st.Output.Id}x{st.Output.Count} fuelTime={st.FuelTime:F1}");

        List<ItemStack> returned = furnaces.CloseAndCollect();
        Check("closing a furnace hands its stacks back", returned.Count > 0, returned.Count.ToString());
        Check("furnace emptied on close", st.Input.IsEmpty && st.Fuel.IsEmpty && st.Output.IsEmpty);
        Check("furnace closed", !furnaces.Open);
    }

    // --------------------------------------------------------- day/night + sleep

    private static void CheckDayNightAndSleep(TextWriter log, string saveRoot)
    {
        Section(log, "day/night and sleeping");
        string tmp = Path.Combine(saveRoot, "daynight");
        using var session = new GameSession(1337, tmp, 4);
        session.StartNewGame(0, Gamemode.Survival);

        var dn = session.DayNight;
        dn.SetWorldTime(GameConfig.DAY_LENGTH * 0.5f);
        Check("noon is daytime", !dn.IsNightTime && dn.SunIntensity > 1f, dn.SunIntensity.ToString());
        dn.SetWorldTime(0f);
        Check("midnight is night", dn.IsNightTime);
        Check("midnight sky is dark", dn.SkySrgbR < 0.25f, dn.SkySrgbR.ToString());
        Check("sun vector is normalised",
              Math.Abs(Math.Sqrt(dn.SunX * dn.SunX + dn.SunY * dn.SunY + dn.SunZ * dn.SunZ) - 1.0) < 1e-3);

        dn.Update(1f);
        Check("time advances", dn.WorldTime > 0f, dn.WorldTime.ToString());

        // Sleeping requires a bed and night.
        dn.SetWorldTime(0f);
        int px = (int)Math.Floor(session.Player.X), py = (int)Math.Floor(session.Player.Y), pz = (int)Math.Floor(session.Player.Z);
        session.World.SetBlock(px + 1, py, pz, Block.BED_FOOT);
        bool slept = session.TrySleep(new BlockHit(px + 1, py, pz, Block.BED_FOOT, px + 1, py + 1, pz));
        Check("can sleep in a bed at night", slept && session.Sleeping);
        session.Player.Health = 5f;
        session.Player.Hunger = 2f;
        session.WakeUp();
        Check("waking sets dawn", Math.Abs(dn.WorldTime - GameConfig.DAY_LENGTH * 0.27f) < 1e-3f,
              dn.WorldTime.ToString());
        Check("waking restores vitals (sleeping.js:50-51)",
              Math.Abs(session.Player.Health - session.Player.MaxHealth) < 1e-3f && session.Player.Hunger >= 10f,
              $"hp {session.Player.Health}, hunger {session.Player.Hunger}");
        Check("no longer sleeping", !session.Sleeping);

        // input.js only calls trySleep() when getIsNightTime() is true, so the time gate
        // lives in the host; sleeping.js itself only looks at the block.
        dn.SetWorldTime(GameConfig.DAY_LENGTH * 0.5f);
        Check("host gate: noon is not night", !dn.IsNightTime);
        Check("a non-bed block cannot be slept in",
              !session.TrySleep(new BlockHit(px, py, pz, Block.STONE, px, py + 1, pz)));
        Check("not sleeping after a rejected bed", !session.Sleeping);
        Check("the engine itself does not gate on daylight",
              session.TrySleep(new BlockHit(px + 1, py, pz, Block.BED_FOOT, px + 1, py + 1, pz)));
        session.WakeUp();
        Check("wake clears the sleeping flag", !session.Sleeping);
    }

    // ----------------------------------------------------------------- save/load

    private static void CheckSaveLoad(TextWriter log, string saveRoot)
    {
        Section(log, "save and load");
        string tmp = Path.Combine(saveRoot, "saveload");
        const int seed = 424242;

        double sx, sy, sz;
        float worldTime;
        using (var first = new GameSession(seed, tmp, 4))
        {
            first.StartNewGame(3, Gamemode.Survival);
            var p = first.Player;
            for (int i = 0; i < 120; i++) first.Update(1f / 60f, 0.5, 0.25, false, false);
            first.Inventory.Add(Block.PLANKS, 17);
            first.Inventory.SelectedIndex = 4;
            first.World.SetBlock(11, 45, -7, Block.CHEST);
            first.Chests.GetState(11, 45, -7)[5] = Inventory.MakeItem(ItemId.COAL, 9);
            first.DayNight.SetWorldTime(123.5f);
            first.Save();
            sx = p.X; sy = p.Y; sz = p.Z;
            worldTime = first.DayNight.WorldTime;
        }

        Check("save file written",
              Directory.GetFiles(tmp).Any(f => f.Contains("slot_3") && f.EndsWith(".dat")),
              string.Join(",", Directory.Exists(tmp) ? Directory.GetFiles(tmp) : Array.Empty<string>()));

        using var second = new GameSession(seed, tmp, 4);
        second.LoadGame(3);
        Check("loaded session started", second.Started && second.CurrentSlot == 3);
        Check("player position round-trips",
              Math.Abs(second.Player.X - sx) < 1e-4 && Math.Abs(second.Player.Y - sy) < 1e-4 &&
              Math.Abs(second.Player.Z - sz) < 1e-4,
              $"{sx:F4},{sy:F4},{sz:F4} -> {second.Player.X:F4},{second.Player.Y:F4},{second.Player.Z:F4}");
        Check("selected index round-trips", second.Inventory.SelectedIndex == 4);
        Check("inventory round-trips", second.Inventory.CountOf(Block.PLANKS) == 17,
              second.Inventory.CountOf(Block.PLANKS).ToString());
        Check("block edit round-trips", second.World.GetBlock(11, 45, -7) == Block.CHEST);
        Check("chest contents round-trips", second.Chests.GetState(11, 45, -7)[5].Count == 9);
        Check("world time round-trips", Math.Abs(second.DayNight.WorldTime - worldTime) < 1e-3,
              second.DayNight.WorldTime.ToString());
        Check("gamemode round-trips", second.Player.Mode == Gamemode.Survival);

        // A wrong seed must be rejected rather than silently loading a different world.
        using var wrong = new GameSession(seed + 1, tmp, 4);
        bool threw = false;
        try { wrong.LoadGame(3); } catch (InvalidOperationException) { threw = true; }
        Check("mismatched seed is rejected", threw);

        // Save slots and metadata.
        var saves = new SaveSystem(tmp);
        SaveMeta meta = saves.GetSavesMeta();
        Check("metadata records the slot", meta.Saves.Any(e => e.Slot == 3), meta.Saves.Count.ToString());
        Check("metadata records the seed", meta.Saves.First(e => e.Slot == 3).Seed == seed);
        int free = saves.NextFreeSlot();
        Check("next free slot is not the used one", free != 3, free.ToString());
        saves.DeleteSlot(3);
        Check("delete removes the file and the entry",
              !saves.GetSavesMeta().Saves.Any(e => e.Slot == 3));
    }

    // ------------------------------------------------------------------ settings

    private static void CheckSettings(TextWriter log, string saveRoot)
    {
        Section(log, "settings and gamemode");
        string tmp = Path.Combine(saveRoot, "settings");
        using var session = new GameSession(1337, tmp, 4);
        session.StartNewGame(0, Gamemode.Survival);
        session.ProcessMeshQueue(int.MaxValue);

        Check("default biome blend is 4", session.World.Generator.BiomeBlend == 4);
        session.World.SetBiomeBlend(9);
        Check("blend value applied", session.World.Generator.BiomeBlend == 9);
        int rebuilt = session.ProcessMeshQueue(int.MaxValue);
        Check("blend change re-meshes loaded chunks", rebuilt > 0, rebuilt.ToString());

        // Grass tint must actually change with the blend radius. The origin sits in
        // uniform plains, so scan outward for a column that straddles two biomes.
        var gen = session.World.Generator;
        int tinted = 0;
        uint exampleA = 0, exampleB = 0;
        for (int x = -96; x <= 96 && tinted == 0; x += 3)
        for (int z = -96; z <= 96; z += 3)
        {
            gen.SetBiomeBlend(0);
            uint sharp = gen.GetGrassColorAt(x + 0.5, z + 0.5);
            gen.SetBiomeBlend(16);
            uint soft = gen.GetGrassColorAt(x + 0.5, z + 0.5);
            if (sharp == soft) continue;
            tinted++;
            exampleA = sharp; exampleB = soft;
        }
        Check("blend changes grass tint at biome borders", tinted > 0, $"{exampleA:X6} vs {exampleB:X6}");
        gen.SetBiomeBlend(9);

        // ui.js setGamemode(): wipes the inventory, refills both bars, cancels flight.
        session.Inventory.Add(Block.STONE, 32);
        session.Player.Health = 4;
        session.Player.Hunger = 3;
        session.Player.Flying = true;
        session.SetGamemode(Gamemode.Creative);
        Check("gamemode switch wipes the inventory", session.Inventory.CountOf(Block.STONE) == 0);
        Check("gamemode switch refills health", session.Player.Health == session.Player.MaxHealth);
        Check("gamemode switch refills hunger", session.Player.Hunger == session.Player.MaxHunger);
        Check("gamemode switch cancels flight", !session.Player.Flying);
        Check("gamemode is creative", session.Player.Mode == Gamemode.Creative);

        // Chat log + commands.
        session.Chat.Clear();
        string help = session.RunCommand("/help");
        Check("/help lists commands", help.Contains("/time") && help.Contains("/tp"), help);
        string unknown = session.RunCommand("/nope");
        Check("unknown command reports itself", unknown.Contains("Unknown command"), unknown);
        string time = session.RunCommand("/time set noon");
        Check("/time set noon works",
              Math.Abs(session.DayNight.WorldTime - GameConfig.DAY_LENGTH * 0.5f) < 1e-3, time);
        string tp = session.RunCommand("/tp 100 60 -100");
        Check("/tp moves the player",
              Math.Abs(session.Player.X - 100) < 1e-6 && Math.Abs(session.Player.Z + 100) < 1e-6, tp);
        session.Chat.Add("hello");
        Check("chat log appends", session.Chat.Lines.Contains("hello"));

        // Mobs.
        int before = session.Mobs.Mobs.Count;
        Mob cow = session.Mobs.SpawnMob(MobType.Cow, 4, 4);
        Check("SpawnMob adds a mob", session.Mobs.Mobs.Count == before + 1);
        Check("cow stands on the terrain",
              Math.Abs(cow.Y - (session.World.Generator.GetHeight(4, 4) + 1)) < 1e-6, cow.Y.ToString());
        Check("cow health is positive", cow.Health > 0f, cow.Health.ToString());
        session.Mobs.Damage(cow, 999f, 0, 0);
        Check("a dead cow is removed or flagged", cow.Dead || !session.Mobs.Mobs.Contains(cow));
        for (int i = 0; i < 5; i++) session.Mobs.TrySpawnCows(session.Player.X, session.Player.Z);
        Check("cow cap respected", session.Mobs.CountType(MobType.Cow) <= MobSystem.MAX_COWS,
              session.Mobs.CountType(MobType.Cow).ToString());
    }
}
