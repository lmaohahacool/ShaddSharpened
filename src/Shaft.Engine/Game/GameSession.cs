using System.Globalization;
using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.Meshing;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

/// <summary>
/// The whole game, minus presentation. Owns the world and every subsystem and runs
/// the same per-frame order as <c>animate()</c> in <c>dist/main.js</c>.
/// </summary>
/// <remarks>
/// This class has no dependency on Silk.NET, OpenGL or any windowing system, which is
/// the point: the entire simulation is runnable and unit-testable headless, and the
/// renderer is a consumer of it rather than a participant.
/// </remarks>
public sealed class GameSession : IDisposable
{
    public VoxelWorld World { get; }
    public Lighting Lighting { get; }
    public WaterSystem Water { get; }
    public ChunkMesher Mesher { get; }
    public PlayerEntity Player { get; }
    public Inventory Inventory { get; }
    public CraftingSystem Crafting { get; }
    public ChestSystem Chests { get; }
    public FurnaceSystem Furnaces { get; }
    public MiningSystem Mining { get; }
    public MobSystem Mobs { get; }
    public DayNightSystem DayNight { get; }
    public SaveSystem Saves { get; }

    /// <summary>Render distance in chunks, adjustable from the settings menu.</summary>
    public int RenderDistance { get; set; } = GameConfig.RENDER_DISTANCE;

    public int? CurrentSlot { get; private set; }
    public bool Started { get; private set; }

    /// <summary>True while the player is sleeping in a bed.</summary>
    public bool Sleeping { get; private set; }

    public ChatLog Chat { get; } = new();

    private float _chunkUpdateTimer;
    private float _autoSaveTimer;
    private float _unloadTimer;
    private ChunkStreamer? _streamer;

    /// <summary>Background chunk generator. Null until <see cref="EnableAsyncStreaming"/>
    /// is called, so headless tests stay single-threaded and deterministic.</summary>
    public ChunkStreamer? Streamer => _streamer;

    /// <summary>Chunks adopted from the streamer per streaming tick. Caps the per-frame
    /// cost of water seeding and remeshing so a fast player cannot stall the render
    /// thread.</summary>
    public int MaxChunksAdoptedPerTick { get; set; } = 8;

    /// <summary>How many chunk radii beyond <see cref="RenderDistance"/> to keep loaded.
    /// Unloading exactly at the render edge would thrash meshes on a player standing
    /// on a boundary.</summary>
    public int UnloadMargin { get; set; } = 2;

    /// <summary>Raised when a chunk needs (re)uploading to the GPU.</summary>
    public event Action<Chunk, ChunkMeshData>? ChunkMeshed;

    /// <summary>Raised with the packed chunk key when a chunk is unloaded, so the
    /// renderer can delete its buffers. Always on the main thread.</summary>
    public event Action<long>? ChunkUnloaded;

    private readonly Dictionary<long, ChunkMeshData> _meshes = new();

    public GameSession(int seed, string saveRoot, int biomeBlend = 4, int? spawnBiome = null)
    {
        World = new VoxelWorld(seed, biomeBlend, spawnBiome);
        Lighting = new Lighting(World);
        Water = new WaterSystem(World);
        Mesher = new ChunkMesher(World, Lighting);
        Player = new PlayerEntity(World);
        Inventory = new Inventory();
        Crafting = new CraftingSystem();
        Chests = new ChestSystem();
        Furnaces = new FurnaceSystem(World);
        Mining = new MiningSystem(World, Lighting, Water, Player, Inventory, Chests, Furnaces);
        DayNight = new DayNightSystem();
        Mobs = new MobSystem(World, Player, DayNight, Mining);
        Saves = new SaveSystem(saveRoot);

        Mobs.MobKilled += m => Mobs.DropLoot(m);
        Mining.BlockBroken += (_, _, _, _) => { };
    }

    public ChunkMeshData? GetMesh(Chunk chunk)
        => _meshes.TryGetValue(ChunkKeys.Pack(chunk.CX, chunk.CZ), out var m) ? m : null;

    public IReadOnlyDictionary<long, ChunkMeshData> Meshes => _meshes;

    public void DiscardMesh(Chunk chunk) => _meshes.Remove(ChunkKeys.Pack(chunk.CX, chunk.CZ));

    public void DiscardMesh(long key) => _meshes.Remove(key);

    /// <summary>
    /// Moves chunk generation onto background worker threads. Call once before
    /// <see cref="StartNewGame"/> or <see cref="LoadGame"/>.
    /// </summary>
    /// <param name="workerCount">0 picks <c>ProcessorCount - 2</c>, leaving cores for
    /// rendering and the OS.</param>
    public void EnableAsyncStreaming(int workerCount = 0)
    {
        _streamer ??= new ChunkStreamer(World, workerCount);
    }

    public void DisableAsyncStreaming()
    {
        _streamer?.Dispose();
        _streamer = null;
    }

    public void Dispose() => DisableAsyncStreaming();

    // ------------------------------------------------------------ new / load

    /// <summary>Starts a fresh world at the given slot.</summary>
    public void StartNewGame(int slot, Gamemode mode)
    {
        CurrentSlot = slot;
        Player.Mode = mode;
        Player.Spawn();
        Started = true;
        Chat.Add($"Created world (seed {World.Seed})");
        InitialStream();
    }

    /// <summary>Loads a saved world. Throws <see cref="InvalidDataException"/> on a
    /// version mismatch, matching how save.js rejected unknown versions.</summary>
    public void LoadGame(int slot)
    {
        var data = Saves.LoadGame(slot)
                   ?? throw new FileNotFoundException($"No save in slot {slot}");

        // The world must exist before deltas are applied, and the seed in the file is
        // authoritative — a slot's terrain is defined by the seed it was created with.
        if ((int)data.Seed != World.Seed)
            throw new InvalidOperationException(
                $"Save slot {slot} belongs to seed {data.Seed} but this session was created with {World.Seed}.");

        foreach (var (pos, id) in data.BlockChanges) World.BlockChanges[pos] = id;
        foreach (var (pos, facing) in data.BlockFacings) World.BlockFacings[pos] = facing;

        Player.X = data.X; Player.Y = data.Y; Player.Z = data.Z;
        Player.Yaw = data.Yaw; Player.Pitch = data.Pitch;
        Player.Health = data.Health; Player.Hunger = data.Hunger;
        Player.Flying = data.Flying;
        Player.Mode = data.Mode;
        Player.FallStartY = null;
        Player.HungerTimer = 0; Player.StarveTimer = 0;

        Inventory.SelectedIndex = Math.Clamp(data.SelectedIndex, 0, Inventory.HOTBAR - 1);
        Array.Copy(data.Inventory, Inventory.Slots, Inventory.SIZE);

        foreach (var (pos, slots) in data.Chests) Chests.Load(pos, slots);
        foreach (var (pos, st) in data.Furnaces) Furnaces.Load(pos, st);

        Mining.DroppedItems.Clear();
        Mining.DroppedItems.AddRange(data.DroppedItems);

        Mobs.Mobs.Clear();
        Mobs.Mobs.AddRange(data.Mobs);

        DayNight.SetWorldTime(data.WorldTime);

        CurrentSlot = slot;
        Started = true;
        Chat.Add($"Loaded world (seed {World.Seed})");
        InitialStream();
    }

    /// <summary>
    /// Writes the world to its slot. Silent, because <c>saveGame()</c> in save.js says
    /// nothing: the only feedback the browser build gives is the one-second
    /// "💾 Saved!" flash the Ctrl+S handler puts in <c>#info</c>, which is UI, not chat.
    /// </summary>
    public void Save()
    {
        if (!Started || CurrentSlot is null) return;
        Saves.SaveGame(CurrentSlot.Value, this);
    }

    private void InitialStream()
    {
        if (_streamer is not null)
        {
            // The first frame must not be empty: block until the spawn area exists,
            // generating on this thread if the workers have not caught up.
            _streamer.EnsureLoadedBlocking(Player.X, Player.Z, RenderDistance);
        }
        else
        {
            World.EnsureChunksAround(Player.X, Player.Z, RenderDistance);
        }

        // Prime the water automaton for the loaded area, as seedChunkWater did.
        foreach (var c in World.AllChunks) Water.SeedChunkWater(c.CX, c.CZ);
        if (Lighting.EnableSkylight)
            foreach (var c in World.AllChunks) Lighting.ComputeSkylightForChunk(c);
    }

    private readonly List<Chunk> _adoptedScratch = new();
    private readonly List<long> _unloadedScratch = new();

    /// <summary>Streams chunks in and out around the player. Runs on the main thread;
    /// generation itself happens on <see cref="Streamer"/> workers.</summary>
    private void StreamChunks()
    {
        var newlyLoaded = _adoptedScratch;
        newlyLoaded.Clear();

        if (_streamer is not null)
        {
            _streamer.RequestAround(Player.X, Player.Z, RenderDistance);
            _streamer.AdoptCompleted(newlyLoaded, MaxChunksAdoptedPerTick);
        }
        else
        {
            World.EnsureChunksAround(Player.X, Player.Z, RenderDistance, newlyLoaded);
        }

        foreach (var c in newlyLoaded)
        {
            Water.SeedChunkWater(c.CX, c.CZ);
            if (Lighting.EnableSkylight) Lighting.ComputeSkylightForChunk(c);
        }
    }

    /// <summary>Drops chunks (and their meshes) far outside the render distance so a
    /// long walk does not grow memory without bound.</summary>
    private void UnloadChunks()
    {
        if (_streamer is null) return;

        _unloadedScratch.Clear();
        if (_streamer.UnloadFarChunks(Player.X, Player.Z, RenderDistance + UnloadMargin, _unloadedScratch) == 0)
            return;

        foreach (long key in _unloadedScratch)
        {
            _meshes.Remove(key);
            ChunkUnloaded?.Invoke(key);
        }
    }

    // ------------------------------------------------------------------ tick

    /// <summary>
    /// One frame of simulation, in main.js's order: player physics, fall damage,
    /// hunger, mining, mobs, water, chunk streaming, mesh queue, autosave, day/night.
    /// </summary>
    /// <param name="moveX">Normalised world-space wish direction X.</param>
    /// <param name="moveZ">Normalised world-space wish direction Z.</param>
    /// <returns>True if the player moved horizontally this frame (for footsteps).</returns>
    public bool Update(float dt, double moveX, double moveZ, bool jumpHeld, bool descendHeld)
    {
        if (!Started) return false;
        if (dt <= 0) dt = 1e-5f;
        // Clamp long frames (alt-tab, GC pause) so physics cannot tunnel.
        if (dt > 0.1f) dt = 0.1f;

        DayNight.Update(dt);

        bool moving = Player.Update(dt, moveX, moveZ, jumpHeld, descendHeld);

        Mining.Update(dt);
        Mobs.Update(dt);
        Mining.UpdateDroppedItems(dt);

        Water.Update(dt, Player.X, Player.Z);
        Furnaces.Update(dt);

        _chunkUpdateTimer += dt;
        if (_chunkUpdateTimer > GameConfig.CHUNK_UPDATE_INTERVAL)
        {
            _chunkUpdateTimer = 0;
            StreamChunks();
        }

        // Unloading is much cheaper than generation and does not need to be frequent.
        _unloadTimer += dt;
        if (_unloadTimer > GameConfig.CHUNK_UPDATE_INTERVAL * 5)
        {
            _unloadTimer = 0;
            UnloadChunks();
        }

        if (Started && CurrentSlot is not null)
        {
            _autoSaveTimer += dt;
            if (_autoSaveTimer >= GameConfig.AUTOSAVE_INTERVAL)
            {
                _autoSaveTimer = 0;
                Save();
            }
        }

        return moving;
    }

    /// <summary>Rebuilds up to <paramref name="budget"/> queued chunk meshes. The
    /// renderer calls this once per frame and uploads whatever comes back.</summary>
    public int ProcessMeshQueue(int budget)
    {
        return World.ProcessRebuildQueue(chunk =>
        {
            long key = ChunkKeys.Pack(chunk.CX, chunk.CZ);
            if (!_meshes.TryGetValue(key, out var mesh))
            {
                mesh = new ChunkMeshData();
                _meshes[key] = mesh;
            }
            Mesher.Build(chunk, mesh);
            ChunkMeshed?.Invoke(chunk, mesh);
        }, budget);
    }

    // ------------------------------------------------------------- gamemode

    /// <summary>Cycles creative/survival, as the U key did.</summary>
    public void ToggleGamemode()
    {
        Player.Mode = Player.Mode == Gamemode.Creative ? Gamemode.Survival : Gamemode.Creative;
        if (Player.Mode == Gamemode.Creative) { Player.Health = 20; Player.Hunger = 20; }
        Chat.Add($"Mode: {(Player.Mode == Gamemode.Creative ? "Creative" : "Survival")}");
    }

    /// <summary>
    /// Port of <c>setGamemode(mode)</c> in ui.js:34 — what the gamemode menu's Creative and
    /// Survival buttons actually call. It differs from <see cref="ToggleGamemode"/> in two
    /// ways the browser build insists on: flight is always cancelled, health and hunger are
    /// refilled in BOTH directions, and the whole inventory is wiped.
    /// </summary>
    public void SetGamemode(Gamemode mode)
    {
        Player.Mode = mode;
        Player.Flying = false;
        Player.VelY = 0;
        Player.Health = Player.MaxHealth;
        Player.Hunger = Player.MaxHunger;
        Inventory.Clear();
        Inventory.Cursor = ItemStack.Empty;
    }

    // -------------------------------------------------------------- sleeping

    /// <summary>Port of <c>sleeping.js</c>: entering a bed at night skips to dawn.</summary>
    public bool TrySleep(BlockHit? target)
    {
        if (target is null) return false;
        byte b = target.Value.Block;
        if (b != Block.BED_FOOT && b != Block.BED_HEAD) return false;

        Sleeping = true;
        Chat.Add("Sleeping...");
        return true;
    }

    /// <summary>
    /// Port of <c>wakeUp</c> in <c>dist/sleeping.js</c>: advances time to dawn and
    /// restores vitals like Minecraft. The camera pose (pitch/y saved into
    /// <c>window._sleepSaved*</c>) and the sleep UI stay with the host, which owns them.
    /// </summary>
    public void WakeUp(bool skipToMorning = true)
    {
        if (!Sleeping) return;
        // sleeping.js: setWorldTimeGlobal(600 * 0.27) — "dawn, a bit past sunrise".
        if (skipToMorning) DayNight.SetWorldTime(GameConfig.DAY_LENGTH * 0.27f);
        Sleeping = false;

        // sleeping.js: player.health = player.maxHealth; player.hunger = max(hunger, 10).
        Player.Health = Player.MaxHealth;
        Player.Hunger = Math.Max(Player.Hunger, 10f);

        Chat.Add("You woke up");
    }

    // --------------------------------------------------------------- commands

    /// <summary>Port of the <c>COMMANDS</c> table in <c>dist/commands.js</c>.</summary>
    public string RunCommand(string input)
    {
        string trimmed = input.Trim();
        if (trimmed.Length == 0) return "";
        string noSlash = trimmed.StartsWith('/') ? trimmed[1..] : trimmed;

        var parts = noSlash.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        string name = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        switch (name)
        {
            case "time":
                if (args.Length < 2 || args[0].ToLowerInvariant() != "set")
                    return "Usage: /time set <dawn|noon|dusk|night|midnight>";
                return CmdTimeSet(args[1]);

            case "locate":
                if (args.Length < 2 || args[0].ToLowerInvariant() != "biome")
                    return "Usage: /locate biome <name>";
                return CmdLocateBiome(args[1]);

            case "tp":
                return CmdTp(args);

            case "help":
                return "/time set <value> — set world time\n"
                     + "/locate biome <name> — find nearest biome\n"
                     + "/tp <x> <y> <z> — teleport\n"
                     + "/help — this list";

            default:
                return $"Unknown command: {name}. Try /help";
        }
    }

    private static readonly Dictionary<string, float> TimeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dawn"] = GameConfig.DAY_LENGTH * 0.25f,
        ["sunrise"] = GameConfig.DAY_LENGTH * 0.25f,
        ["morning"] = GameConfig.DAY_LENGTH * 0.30f,
        ["noon"] = GameConfig.DAY_LENGTH * 0.50f,
        ["day"] = GameConfig.DAY_LENGTH * 0.50f,
        ["afternoon"] = GameConfig.DAY_LENGTH * 0.65f,
        ["dusk"] = GameConfig.DAY_LENGTH * 0.75f,
        ["sunset"] = GameConfig.DAY_LENGTH * 0.75f,
        ["evening"] = GameConfig.DAY_LENGTH * 0.80f,
        ["night"] = GameConfig.DAY_LENGTH * 0.85f,
        ["midnight"] = 0f,
    };

    private string CmdTimeSet(string value)
    {
        if (!TimeValues.TryGetValue(value, out float t))
            return $"Unknown time: \"{value}\"";
        DayNight.SetWorldTime(t);
        return $"Time set to {value.ToLowerInvariant()}";
    }

    private string CmdTp(string[] args)
    {
        if (args.Length < 3) return "Usage: /tp <x> <y> <z>";
        if (!TryParse(args[0], out double x) || !TryParse(args[1], out double y) || !TryParse(args[2], out double z))
            return "Coordinates must be numbers";
        if (y < -100 || y > 500) return "Y must be between -100 and 500";

        Player.X = x; Player.Y = y; Player.Z = z;
        Player.VelX = Player.VelY = Player.VelZ = 0;
        Player.FallStartY = null;
        return $"Teleported to {x}, {y}, {z}";
    }

    private static bool TryParse(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Port of <c>locateBiome</c>: expanding ring search around the player.</summary>
    private string CmdLocateBiome(string name)
    {
        int? target = name.ToLowerInvariant() switch
        {
            "plains" => Biome.PLAINS,
            "forest" => Biome.FOREST,
            "mountain" => Biome.MOUNTAIN,
            "desert" => Biome.DESERT,
            "snowy" or "snow" => Biome.SNOWY,
            "old_growth" or "oldgrowth" or "ogg" => Biome.OLD_GROWTH,
            _ => null,
        };
        if (target is null) return $"Unknown biome: \"{name}\"";

        int px = (int)Math.Floor(Player.X), pz = (int)Math.Floor(Player.Z);
        const int maxRadius = 512;
        for (int r = 0; r <= maxRadius; r += 4)
        {
            for (int a = 0; a < 64; a++)
            {
                double ang = a / 64.0 * Math.PI * 2;
                int cx = px + (int)Math.Round(Math.Cos(ang) * r);
                int cz = pz + (int)Math.Round(Math.Sin(ang) * r);
                if (World.Generator.GetBiome(cx, cz) != target.Value) continue;

                int dx = cx - px, dz = cz - pz;
                int dist = (int)Math.Round(Math.Sqrt(dx * (double)dx + dz * (double)dz));
                return $"Nearest {name.ToLowerInvariant()} at {cx}, {cz} ({dist} blocks away)";
            }
        }
        return $"No {name.ToLowerInvariant()} biome found nearby";
    }
}

/// <summary>Ring buffer of chat lines, replacing the DOM chat log.</summary>
public sealed class ChatLog
{
    private readonly List<string> _lines = new();
    public const int MAX_LINES = 100;

    public IReadOnlyList<string> Lines => _lines;

    public void Add(string line)
    {
        foreach (var l in line.Split('\n'))
        {
            _lines.Add(l);
            if (_lines.Count > MAX_LINES) _lines.RemoveAt(0);
        }
    }

    public void Clear() => _lines.Clear();
}
