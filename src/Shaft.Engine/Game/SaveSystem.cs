using System.Text;
using System.Text.Json;
using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

/// <summary>Little-endian writer matching the <c>ByteWriter</c> class in save.js,
/// including its masking semantics (<c>v &amp; 0xff</c>, <c>v &gt;&gt;&gt; 0</c>, <c>v | 0</c>).</summary>
internal sealed class ByteWriter
{
    private readonly List<byte> _bytes = new();

    public int Length => _bytes.Count;

    public void U8(int v) => _bytes.Add((byte)(v & 0xff));

    public void U16(int v)
    {
        v &= 0xffff;
        _bytes.Add((byte)(v & 0xff));
        _bytes.Add((byte)((v >> 8) & 0xff));
    }

    public void U32(long v)
    {
        uint u = (uint)(v & 0xFFFFFFFFL);
        _bytes.Add((byte)(u & 0xff));
        _bytes.Add((byte)((u >> 8) & 0xff));
        _bytes.Add((byte)((u >> 16) & 0xff));
        _bytes.Add((byte)((u >> 24) & 0xff));
    }

    public void I32(int v) => U32(v);

    public void F32(float v)
    {
        var bits = BitConverter.SingleToInt32Bits(v);
        U32(bits);
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Little-endian reader matching save.js's <c>ByteReader</c>.</summary>
internal sealed class ByteReader
{
    private readonly byte[] _b;
    private int _off;

    public ByteReader(byte[] b) { _b = b; }

    public int Offset => _off;
    public bool Eof => _off >= _b.Length;

    public int U8() { int v = _b[_off]; _off += 1; return v; }

    public int U16()
    {
        int v = _b[_off] | (_b[_off + 1] << 8);
        _off += 2;
        return v;
    }

    public uint U32()
    {
        uint v = (uint)(_b[_off] | (_b[_off + 1] << 8) | (_b[_off + 2] << 16) | (_b[_off + 3] << 24));
        _off += 4;
        return v;
    }

    public int I32() => unchecked((int)U32());

    public float F32()
    {
        float v = BitConverter.Int32BitsToSingle(unchecked((int)U32()));
        return v;
    }
}

/// <summary>One entry in the save-slot list, replacing the <c>shaft_meta_v1</c>
/// localStorage record.</summary>
public sealed class SaveMetaEntry
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public int Seed { get; set; }
    public string Gamemode { get; set; } = "creative";
    public long LastPlayed { get; set; }
    public int PlayedSeconds { get; set; }
}

public sealed class SaveMeta
{
    public List<SaveMetaEntry> Saves { get; set; } = new();
}

/// <summary>
/// Port of <c>dist/save.js</c>.
/// </summary>
/// <remarks>
/// The byte layout is identical to the JavaScript version — same field order, same
/// widths, same little-endian packing, same <c>SAVE_VERSION</c> of 6 — so the two
/// engines describe a world the same way. The only change is the backing store:
/// files under a saves directory instead of base64 in <c>localStorage</c>, which is
/// what a native build needs anyway.
/// <para>
/// Terrain is still not persisted; only the seed plus the sparse
/// <c>blockChanges</c>/<c>blockFacings</c> deltas and entity state are. That is why
/// MIGRATION-BRIEF.md §3 flags the hash change as a save-invalidating event: the
/// deltas are applied to regenerated terrain, so different terrain means a different
/// world even with a byte-identical save file.
/// </para>
/// </remarks>
public sealed class SaveSystem
{
    public const ushort SAVE_VERSION = GameConfig.SAVE_VERSION;

    private readonly string _root;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SaveSystem(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;
    private string SlotPath(int slot) => Path.Combine(_root, $"shaft_save_v{SAVE_VERSION}_slot_{slot}.dat");
    private string MetaPath => Path.Combine(_root, "shaft_meta_v1.json");

    // ------------------------------------------------------------------- meta

    public SaveMeta GetSavesMeta()
    {
        try
        {
            if (!File.Exists(MetaPath)) return new SaveMeta();
            return JsonSerializer.Deserialize<SaveMeta>(File.ReadAllText(MetaPath)) ?? new SaveMeta();
        }
        catch { return new SaveMeta(); }
    }

    public void SetSavesMeta(SaveMeta meta) => File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, JsonOpts));

    /// <summary>Port of <c>nextFreeSlot</c>.</summary>
    public int NextFreeSlot()
    {
        var used = new HashSet<int>(GetSavesMeta().Saves.Select(s => s.Slot));
        for (int i = 0; i < 100; i++) if (!used.Contains(i)) return i;
        return -1;
    }

    public void UpdateMetaForSlot(int slot, Action<SaveMetaEntry> patch)
    {
        var meta = GetSavesMeta();
        var entry = meta.Saves.FirstOrDefault(s => s.Slot == slot);
        if (entry is null) { entry = new SaveMetaEntry { Slot = slot }; meta.Saves.Add(entry); }
        patch(entry);
        SetSavesMeta(meta);
    }

    public void DeleteSlot(int slot)
    {
        if (File.Exists(SlotPath(slot))) File.Delete(SlotPath(slot));
        var meta = GetSavesMeta();
        meta.Saves.RemoveAll(s => s.Slot == slot);
        SetSavesMeta(meta);
    }

    // ------------------------------------------------------------------- save

    /// <summary>Port of <c>saveGame</c>.</summary>
    public void SaveGame(int slot, GameSession s)
    {
        var w = new ByteWriter();

        // Header
        w.U16(SAVE_VERSION);
        w.U32((uint)s.World.Seed);
        w.F32(s.DayNight.WorldTime);
        w.U8(s.Player.Mode == Gamemode.Creative ? 0 : 1);

        // Player state
        w.F32((float)s.Player.X);
        w.F32((float)s.Player.Y);
        w.F32((float)s.Player.Z);
        w.F32(s.Player.Yaw);
        w.F32(s.Player.Pitch);
        w.F32(s.Player.Health);
        w.F32(s.Player.Hunger);
        w.U8(s.Player.Flying ? 1 : 0);
        w.U8(s.Inventory.SelectedIndex);
        w.U8(0);

        // Inventory
        w.U32(Inventory.SIZE);
        foreach (var slotItem in s.Inventory.Slots) WriteStack(w, slotItem);

        // Block changes
        w.U32(s.World.BlockChanges.Count);
        foreach (var (pos, id) in s.World.BlockChanges)
        {
            w.I32(pos.X); w.I32(pos.Y); w.I32(pos.Z);
            w.U16(id);
        }

        // Block facings
        w.U32(s.World.BlockFacings.Count);
        foreach (var (pos, facing) in s.World.BlockFacings)
        {
            w.I32(pos.X); w.I32(pos.Y); w.I32(pos.Z);
            w.U8(facing);
        }

        // Chest states
        w.U32(s.Chests.States.Count);
        foreach (var (pos, slots) in s.Chests.States)
        {
            w.I32(pos.X); w.I32(pos.Y); w.I32(pos.Z);
            w.U8(GameConfig.CHEST_SLOTS);
            for (int i = 0; i < GameConfig.CHEST_SLOTS; i++)
                WriteStack(w, i < slots.Length ? slots[i] : ItemStack.Empty);
        }

        // Furnace states
        w.U32(s.Furnaces.States.Count);
        foreach (var (pos, st) in s.Furnaces.States)
        {
            w.I32(pos.X); w.I32(pos.Y); w.I32(pos.Z);
            WriteStack(w, st.Input);
            WriteStack(w, st.Fuel);
            WriteStack(w, st.Output);
            w.F32(st.FuelTime);
            w.F32(st.Progress);
        }

        // Dropped items
        w.U32(s.Mining.DroppedItems.Count);
        foreach (var d in s.Mining.DroppedItems)
        {
            w.F32((float)d.X); w.F32((float)d.Y); w.F32((float)d.Z);
            w.F32((float)d.VelX); w.F32((float)d.VelY); w.F32((float)d.VelZ);
            w.U16(d.Id);
            w.U8(Math.Min(255, d.Count));
            w.U16(d.Durability);
            w.F32(d.Age);
            w.U8(d.OnGround ? 1 : 0);
            w.U8(d.IsItem ? 1 : 0);
        }

        // Mobs: only tamed cows and all zombies, exactly as getMobsToSave() filtered.
        var toSave = s.Mobs.Mobs.Where(m => m.Type == MobType.Zombie ||
                                           (m.Type == MobType.Cow && m.Persistent)).ToList();
        w.U32(toSave.Count);
        foreach (var m in toSave)
        {
            w.U8(m.Type == MobType.Cow ? 0 : 1);
            w.F32((float)m.X); w.F32((float)m.Y); w.F32((float)m.Z);
            w.F32((float)m.VelX); w.F32((float)m.VelY); w.F32((float)m.VelZ);
            w.U8(Math.Min(255, Math.Max(0, (int)m.Health)));
            w.U8(m.OnGround ? 1 : 0);

            if (m.Type == MobType.Cow)
            {
                w.F32(m.WalkAnim);
                w.F32(m.IdleTimer);
                w.F32((float)(m.HasHome ? m.HomeX : m.X));
                w.F32((float)(m.HasHome ? m.HomeY : m.Y));
                w.F32((float)(m.HasHome ? m.HomeZ : m.Z));
            }
            else
            {
                w.F32(m.WalkAnim);
                w.U8(m.Burning ? 1 : 0);
                w.F32(m.BurnTimer);
                w.F32(m.BurnCheckTimer);
            }
        }

        File.WriteAllBytes(SlotPath(slot), w.ToArray());

        // Metadata, mirroring updateMetaForSlot + the elapsed-seconds accounting.
        var meta = GetSavesMeta();
        var existing = meta.Saves.FirstOrDefault(e => e.Slot == slot);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int elapsed = existing is { LastPlayed: > 0 }
            ? Math.Max(0, (int)((nowMs - existing.LastPlayed) / 1000))
            : 0;

        UpdateMetaForSlot(slot, e =>
        {
            e.Seed = s.World.Seed;
            e.Gamemode = s.Player.Mode == Gamemode.Creative ? "creative" : "survival";
            e.LastPlayed = nowMs;
            e.PlayedSeconds += elapsed;
            if (string.IsNullOrEmpty(e.Name)) e.Name = $"World {slot + 1}";
        });
    }

    private static void WriteStack(ByteWriter w, ItemStack s)
    {
        if (s.IsEmpty) { w.U16(0); w.U8(0); w.U16(0); return; }
        w.U16(s.Id);
        w.U8(Math.Min(255, s.Count));
        w.U16(s.Durability);
    }

    // ------------------------------------------------------------------- load

    /// <summary>Everything read back out of a save file.</summary>
    public sealed class LoadedSave
    {
        public ushort Version;
        public uint Seed;
        public float WorldTime;
        public Gamemode Mode;
        public float X, Y, Z, Yaw, Pitch, Health, Hunger;
        public bool Flying;
        public int SelectedIndex;
        public ItemStack[] Inventory = new ItemStack[Config.GameConfig.TOTAL_SLOTS];
        public List<(BlockPos Pos, ushort Id)> BlockChanges = new();
        public List<(BlockPos Pos, byte Facing)> BlockFacings = new();
        public List<(BlockPos Pos, ItemStack[] Slots)> Chests = new();
        public List<(BlockPos Pos, FurnaceState State)> Furnaces = new();
        public List<DroppedItem> DroppedItems = new();
        public List<Mob> Mobs = new();
    }

    /// <summary>Port of <c>loadGame</c>. Returns null if the slot does not exist or
    /// the version does not match, which is how the JS build rejected old saves.</summary>
    public LoadedSave? LoadGame(int slot)
    {
        string path = SlotPath(slot);
        if (!File.Exists(path)) return null;

        var r = new ByteReader(File.ReadAllBytes(path));
        var save = new LoadedSave();

        save.Version = (ushort)r.U16();
        if (save.Version != SAVE_VERSION)
            throw new InvalidDataException(
                $"Save version mismatch: found {save.Version}, expected {SAVE_VERSION}. " +
                "This build uses the legacy JavaScript hash, so it reads v6 worlds. " +
                "A world written by the Phase B exact-int64 build will be v7 and is not interchangeable.");

        save.Seed = r.U32();
        save.WorldTime = r.F32();
        save.Mode = r.U8() == 0 ? Gamemode.Creative : Gamemode.Survival;

        save.X = r.F32(); save.Y = r.F32(); save.Z = r.F32();
        save.Yaw = r.F32(); save.Pitch = r.F32();
        save.Health = r.F32(); save.Hunger = r.F32();
        save.Flying = r.U8() != 0;
        save.SelectedIndex = r.U8();
        r.U8(); // padding

        int invCount = (int)r.U32();
        for (int i = 0; i < invCount && i < save.Inventory.Length; i++)
            save.Inventory[i] = ReadStack(r);
        // Skip any surplus slots a future version might add.
        for (int i = save.Inventory.Length; i < invCount; i++) ReadStack(r);

        int bcCount = (int)r.U32();
        for (int i = 0; i < bcCount; i++)
        {
            int x = r.I32(), y = r.I32(), z = r.I32();
            ushort id = (ushort)r.U16();
            save.BlockChanges.Add((new BlockPos(x, y, z), id));
        }

        int bfCount = (int)r.U32();
        for (int i = 0; i < bfCount; i++)
        {
            int x = r.I32(), y = r.I32(), z = r.I32();
            save.BlockFacings.Add((new BlockPos(x, y, z), (byte)r.U8()));
        }

        int chestCount = (int)r.U32();
        for (int i = 0; i < chestCount; i++)
        {
            int x = r.I32(), y = r.I32(), z = r.I32();
            int n = r.U8();
            var slots = new ItemStack[Math.Max(n, 0)];
            for (int j = 0; j < n; j++) slots[j] = ReadStack(r);
            save.Chests.Add((new BlockPos(x, y, z), slots));
        }

        int furnaceCount = (int)r.U32();
        for (int i = 0; i < furnaceCount; i++)
        {
            int x = r.I32(), y = r.I32(), z = r.I32();
            var st = new FurnaceState { Input = ReadStack(r), Fuel = ReadStack(r), Output = ReadStack(r) };
            st.FuelTime = r.F32();
            st.Progress = r.F32();
            save.Furnaces.Add((new BlockPos(x, y, z), st));
        }

        int dropCount = (int)r.U32();
        for (int i = 0; i < dropCount; i++)
        {
            var d = new DroppedItem
            {
                X = r.F32(), Y = r.F32(), Z = r.F32(),
                VelX = r.F32(), VelY = r.F32(), VelZ = r.F32(),
                Id = r.U16(),
                Count = r.U8(),
                Durability = r.U16(),
                Age = r.F32(),
                OnGround = r.U8() != 0
            };
            r.U8(); // isItem flag, derived from Id
            save.DroppedItems.Add(d);
        }

        int mobCount = (int)r.U32();
        for (int i = 0; i < mobCount; i++)
        {
            bool isCow = r.U8() == 0;
            var m = new Mob(isCow ? MobType.Cow : MobType.Zombie, r.F32(), r.F32(), r.F32());
            m.VelX = r.F32(); m.VelY = r.F32(); m.VelZ = r.F32();
            m.Health = r.U8();
            m.OnGround = r.U8() != 0;

            if (isCow)
            {
                m.WalkAnim = r.F32();
                m.IdleTimer = r.F32();
                m.HomeX = r.F32(); m.HomeY = r.F32(); m.HomeZ = r.F32();
                m.HasHome = true;
                m.Persistent = true;
            }
            else
            {
                m.WalkAnim = r.F32();
                m.Burning = r.U8() != 0;
                m.BurnTimer = r.F32();
                m.BurnCheckTimer = r.F32();
                m.Persistent = true;
            }
            save.Mobs.Add(m);
        }

        return save;
    }

    private static ItemStack ReadStack(ByteReader r)
    {
        int id = r.U16();
        int count = r.U8();
        int dur = r.U16();
        if (id == 0 || count <= 0) return ItemStack.Empty;
        return new ItemStack { Id = id, Count = count, Durability = dur };
    }
}
