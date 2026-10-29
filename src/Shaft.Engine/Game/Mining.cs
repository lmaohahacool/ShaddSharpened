using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

/// <summary>Result of the block raycast, matching the object <c>getTargetedBlock</c> returns.</summary>
public readonly struct BlockHit
{
    public BlockHit(int x, int y, int z, byte block, int placeX, int placeY, int placeZ)
    { X = x; Y = y; Z = z; Block = block; PlaceX = placeX; PlaceY = placeY; PlaceZ = placeZ; }

    public int X { get; } public int Y { get; } public int Z { get; }
    public byte Block { get; }
    public int PlaceX { get; } public int PlaceY { get; } public int PlaceZ { get; }
}

/// <summary>An item entity lying in the world. Port of <c>droppedItems</c> in items.js.</summary>
public sealed class DroppedItem
{
    public double X, Y, Z;
    public double VelX, VelY, VelZ;
    public int Id;
    public int Count = 1;
    public int Durability;
    public float Age;
    public bool OnGround;
    public bool IsItem => ItemId.IsItem(Id);
    public bool Dead;
}

/// <summary>
/// Port of <c>dist/mining.js</c>: block targeting, progressive breaking, breaking
/// and placing, drops, and tool wear. Also hosts the dropped-item entities that
/// <c>items.js</c> managed separately.
/// </summary>
public sealed class MiningSystem
{
    private readonly VoxelWorld _world;
    private readonly Lighting _lighting;
    private readonly WaterSystem _water;
    private readonly PlayerEntity _player;
    private readonly Inventory _inventory;
    private readonly ChestSystem _chests;
    private readonly FurnaceSystem _furnaces;
    private readonly Random _rng = new();

    public List<DroppedItem> DroppedItems { get; } = new();

    public bool Breaking { get; private set; }
    public float BreakProgress { get; private set; }
    public BlockHit? BreakTarget { get; private set; }

    /// <summary>Reach distance in blocks; the JS raycast marched <c>t</c> to 6.</summary>
    public const float REACH = 6f;
    private const float RAY_STEP = 0.05f;

    // Presentation hooks. The engine raises them; the renderer/audio layer handles them.
    public event Action<int, int, int, byte>? BlockBroken;   // x,y,z,block — particles
    public event Action<string>? SoundRequested;              // "break" | "place" | "hit"

    public MiningSystem(VoxelWorld world, Lighting lighting, WaterSystem water,
                        PlayerEntity player, Inventory inventory,
                        ChestSystem chests, FurnaceSystem furnaces)
    {
        _world = world; _lighting = lighting; _water = water;
        _player = player; _inventory = inventory;
        _chests = chests; _furnaces = furnaces;
    }

    // --------------------------------------------------------------- targeting

    /// <summary>View direction from yaw/pitch, matching three.js's YXZ camera.</summary>
    public static void LookDirection(float yaw, float pitch, out double dx, out double dy, out double dz)
    {
        double cp = Math.Cos(pitch);
        dx = -Math.Sin(yaw) * cp;
        dy = Math.Sin(pitch);
        dz = -Math.Cos(yaw) * cp;
    }

    /// <summary>Port of <c>getTargetedBlock</c>: a fixed-step march rather than a DDA,
    /// so it selects exactly the blocks the JS build selected.</summary>
    public BlockHit? GetTargetedBlock()
    {
        double ox = _player.X, oy = _player.Y + GameConfig.PLAYER_EYE, oz = _player.Z;
        LookDirection(_player.Yaw, _player.Pitch, out double dx, out double dy, out double dz);

        int lastX = int.MinValue, lastY = int.MinValue, lastZ = int.MinValue;
        bool hasLast = false;

        for (float t = 0; t < REACH; t += RAY_STEP)
        {
            int px = (int)Math.Floor(ox + dx * t);
            int py = (int)Math.Floor(oy + dy * t);
            int pz = (int)Math.Floor(oz + dz * t);

            byte block = _world.GetBlock(px, py, pz);
            if (block != Block.AIR && !Block.IsWater(block))
            {
                int plx = hasLast ? lastX : px;
                int ply = hasLast ? lastY : py;
                int plz = hasLast ? lastZ : pz;
                return new BlockHit(px, py, pz, block, plx, ply, plz);
            }

            lastX = px; lastY = py; lastZ = pz;
            hasLast = true;
        }
        return null;
    }

    // ---------------------------------------------------------------- breaking

    /// <summary>Port of <c>startBreaking</c>: creative breaks instantly, survival
    /// begins accumulating progress.</summary>
    public void StartBreaking(BlockHit? target)
    {
        if (target is null) return;
        if (_player.Mode == Gamemode.Creative) { BreakBlockInstant(target.Value); return; }
        Breaking = true;
        BreakTarget = target;
        BreakProgress = 0;
    }

    public void StopBreaking() { Breaking = false; BreakTarget = null; BreakProgress = 0; }

    /// <summary>Port of <c>updateMining</c>. Aborts if the crosshair moves off target.</summary>
    public void Update(float dt)
    {
        if (_player.Mode != Gamemode.Survival) return;
        if (!Breaking || BreakTarget is null) return;

        var current = GetTargetedBlock();
        var bt = BreakTarget.Value;
        if (current is null || current.Value.X != bt.X || current.Value.Y != bt.Y || current.Value.Z != bt.Z)
        {
            StopBreaking();
            return;
        }

        ref readonly var info = ref BlockInfoTable.Get(bt.Block);
        float hardness = info.Hardness > 0 ? info.Hardness : .5f;

        // Wrong-tier tools take 8x as long, as mining.js does.
        if (ItemTable.RequiresTool(bt.Block))
        {
            int heldId = _inventory.GetSelectedItemId();
            bool ok = ItemTable.CanHarvest(bt.Block, heldId);
            if (!ok) hardness *= 8f;
        }

        BreakProgress += dt / hardness;
        if (BreakProgress >= 1f)
        {
            BreakBlockInstant(bt);
            StopBreaking();
        }
    }

    /// <summary>Port of <c>breakBlockInstant</c>, including the paired-block rules for
    /// beds and doors, container spills, drop substitution and tool wear.</summary>
    public void BreakBlockInstant(BlockHit target)
    {
        byte blockId = target.Block;
        int x = target.X, y = target.Y, z = target.Z;

        _world.SetBlock(x, y, z, Block.AIR);
        _world.BlockFacings.Remove(new BlockPos(x, y, z));
        _furnaces.RemoveAt(x, y, z);

        // Chest contents spill into the world in survival.
        foreach (var it in _chests.TakeAll(x, y, z))
            if (_player.Mode == Gamemode.Survival)
                SpawnDroppedItem(x, y, z, it.Id, it.Count, it.Durability);

        // Beds: breaking one half clears the other.
        if (blockId == Block.BED_FOOT || blockId == Block.BED_HEAD)
        {
            byte partner = blockId == Block.BED_FOOT ? Block.BED_HEAD : Block.BED_FOOT;
            foreach (var (dx, dz) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                if (_world.GetBlock(x + dx, y, z + dz) != partner) continue;
                _world.SetBlock(x + dx, y, z + dz, Block.AIR);
                _world.QueueChunkRebuilds(x + dx, y, z + dz);
            }
        }

        // Doors: breaking either half removes both and drops one door item.
        if (Block.IsDoor(blockId))
        {
            bool isTop = blockId == Block.DOOR_TOP_CLOSED || blockId == Block.DOOR_TOP_OPEN;
            int otherY = isTop ? y - 1 : y + 1;
            byte otherBlock = _world.GetBlock(x, otherY, z);

            if (Block.IsDoor(otherBlock))
            {
                _world.SetBlock(x, otherY, z, Block.AIR);
                _world.BlockFacings.Remove(new BlockPos(x, otherY, z));
                _world.QueueChunkRebuilds(x, otherY, z);
            }

            if (_player.Mode == Gamemode.Survival)
                SpawnDroppedItem(x, isTop ? y - 1 : y, z, Block.DOOR_BOTTOM_CLOSED, 1);

            _world.QueueChunkRebuilds(x, y, z);
            SoundRequested?.Invoke("break");
            BlockBroken?.Invoke(x, y, z, blockId);
            return;
        }

        _world.QueueChunkRebuilds(x, y, z);
        SoundRequested?.Invoke("break");
        BlockBroken?.Invoke(x, y, z, blockId);

        // --- drop resolution ---
        int heldId = _inventory.GetSelectedItemId();
        bool canDrop = !ItemTable.RequiresTool(blockId) || ItemTable.CanHarvest(blockId, heldId);

        int dropItem = blockId, dropCount = 1;
        if (blockId == Block.STONE) dropItem = Block.COBBLESTONE;
        if (blockId == Block.COAL_ORE) { dropItem = ItemId.COAL; dropCount = 1 + _rng.Next(2); }
        if (blockId == Block.IRON_ORE) { dropItem = ItemId.RAW_IRON; dropCount = 1; }
        if (blockId == Block.FURNACE || blockId == Block.WORKING_FURNACE) dropItem = Block.FURNACE;
        if (blockId == Block.CHEST) dropItem = Block.CHEST;
        if (blockId == Block.BED_FOOT || blockId == Block.BED_HEAD) dropItem = Block.BED_FOOT;

        if (_player.Mode == Gamemode.Survival && canDrop)
            SpawnDroppedItem(x, y, z, dropItem, dropCount);

        if (_player.Mode == Gamemode.Survival)
            if (_inventory.DamageSelectedTool(1)) SoundRequested?.Invoke("break");

        _water.QueueWaterUpdate(x, y, z);

        if (blockId == Block.TORCH)
        {
            _lighting.RemoveLightAt(x, y, z);
            foreach (var (ccx, ccz) in _lighting.GetAffectedChunks(x, y, z))
                _world.MarkChunkDirty(ccx, ccz);
        }
    }

    // ---------------------------------------------------------------- placing

    /// <summary>
    /// Port of <c>placeBlockAt</c>. Refuses to place inside the player, consumes the
    /// block in survival, and handles the two-block door and bed specially.
    /// </summary>
    public bool PlaceBlockAt(int x, int y, int z, int blockId)
    {
        int px = (int)Math.Floor(_player.X), py = (int)Math.Floor(_player.Y), pz = (int)Math.Floor(_player.Z);
        if (x == px && z == pz && (y == py || y == py + 1)) return false;

        if (_player.Mode == Gamemode.Survival && !_inventory.Remove(blockId, 1)) return false;

        // Direction the player faces, quantised to a block facing 0..3.
        LookDirection(_player.Yaw, 0, out double fx, out _, out double fz);
        byte FacingFromLook()
        {
            if (Math.Abs(fx) > Math.Abs(fz)) return (byte)(fx > 0 ? 2 : 3);
            return (byte)(fz > 0 ? 0 : 1);
        }

        if (blockId == Block.DOOR_BOTTOM_CLOSED)
        {
            if (_world.GetBlock(x, y + 1, z) != Block.AIR) return false;
            byte facing = FacingFromLook();
            _world.SetBlock(x, y, z, Block.DOOR_BOTTOM_CLOSED);
            _world.SetBlock(x, y + 1, z, Block.DOOR_TOP_CLOSED);
            _world.SetFacing(x, y, z, facing);
            _world.SetFacing(x, y + 1, z, facing);
            _world.QueueChunkRebuilds(x, y, z);
            _world.QueueChunkRebuilds(x, y + 1, z);
            SoundRequested?.Invoke("place");
            return true;
        }

        if (blockId == Block.BED_FOOT)
        {
            int hx = x, hz = z;
            if (Math.Abs(fx) > Math.Abs(fz)) hx += fx > 0 ? 1 : -1;
            else hz += fz > 0 ? 1 : -1;
            if (_world.GetBlock(hx, y, hz) != Block.AIR) return false;

            _world.SetBlock(x, y, z, Block.BED_FOOT);
            _world.SetBlock(hx, y, hz, Block.BED_HEAD);
            _world.QueueChunkRebuilds(x, y, z);
            _world.QueueChunkRebuilds(hx, y, hz);
            SoundRequested?.Invoke("place");
            return true;
        }

        _world.SetBlock(x, y, z, (byte)blockId);

        if (blockId == Block.FURNACE || blockId == Block.WORKING_FURNACE || blockId == Block.CHEST)
            _world.SetFacing(x, y, z, FacingFromLook());

        _world.QueueChunkRebuilds(x, y, z);
        SoundRequested?.Invoke("place");
        _water.QueueWaterUpdate(x, y, z);

        if (blockId == Block.TORCH)
        {
            _lighting.AddLightAt(x, y, z, Lighting.TORCH_LIGHT);
            foreach (var (ccx, ccz) in _lighting.GetAffectedChunks(x, y, z))
                _world.MarkChunkDirty(ccx, ccz);
        }
        return true;
    }

    /// <summary>Right-click interaction: opens chests, furnaces and crafting tables,
    /// toggles doors, and otherwise places the held block.</summary>
    public bool Interact(BlockHit? target)
    {
        if (target is null) return false;
        var t = target.Value;
        byte b = t.Block;

        if (b == Block.CHEST) { _chests.OpenChest(t.X, t.Y, t.Z); return true; }
        if (b == Block.FURNACE || b == Block.WORKING_FURNACE) { _furnaces.OpenFurnace(t.X, t.Y, t.Z); return true; }
        if (b == Block.CRAFTING_TABLE) return false;   // handled by the UI layer as "open 3x3"

        if (Block.IsDoor(b))
        {
            // Toggle this half and its partner together.
            bool isOpen = Block.IsDoorOpen(b);
            bool isTop = b == Block.DOOR_TOP_CLOSED || b == Block.DOOR_TOP_OPEN;
            int otherY = isTop ? t.Y - 1 : t.Y + 1;
            byte other = _world.GetBlock(t.X, otherY, t.Z);

            // input.js: the new state follows open/closed, never which half was clicked.
            byte newHere = isOpen
                ? (isTop ? Block.DOOR_TOP_CLOSED : Block.DOOR_BOTTOM_CLOSED)
                : (isTop ? Block.DOOR_TOP_OPEN : Block.DOOR_BOTTOM_OPEN);
            byte newOther = isOpen
                ? (isTop ? Block.DOOR_BOTTOM_CLOSED : Block.DOOR_TOP_CLOSED)
                : (isTop ? Block.DOOR_BOTTOM_OPEN : Block.DOOR_TOP_OPEN);

            _world.SetBlock(t.X, t.Y, t.Z, newHere);
            if (Block.IsDoor(other)) _world.SetBlock(t.X, otherY, t.Z, newOther);

            _world.QueueChunkRebuilds(t.X, t.Y, t.Z);
            _world.QueueChunkRebuilds(t.X, otherY, t.Z);
            SoundRequested?.Invoke("place");
            return true;
        }

        int held = _inventory.GetSelectedItemId();
        if (held == 0 || ItemId.IsItem(held)) return false;
        return PlaceBlockAt(t.PlaceX, t.PlaceY, t.PlaceZ, held);
    }

    // ------------------------------------------------------------ dropped items

    public DroppedItem SpawnDroppedItem(int x, int y, int z, int id, int count, int durability = 0)
    {
        var d = new DroppedItem
        {
            X = x + 0.5, Y = y + 0.2, Z = z + 0.5,
            VelX = (_rng.NextDouble() - 0.5) * 2,
            VelY = 3,
            VelZ = (_rng.NextDouble() - 0.5) * 2,
            Id = id, Count = count, Durability = durability
        };
        DroppedItems.Add(d);
        return d;
    }

    /// <summary>Simple gravity + ground collision + pickup for dropped items, and the
    /// despawn age the JS applied.</summary>
    public void UpdateDroppedItems(float dt)
    {
        for (int i = DroppedItems.Count - 1; i >= 0; i--)
        {
            var d = DroppedItems[i];
            d.Age += dt;

            d.VelY -= GameConfig.GRAVITY * dt;
            double nx = d.X + d.VelX * dt;
            double ny = d.Y + d.VelY * dt;
            double nz = d.Z + d.VelZ * dt;

            if (_world.IsSolid(nx, d.Y, d.Z)) { d.VelX = 0; } else d.X = nx;
            if (_world.IsSolid(d.X, d.Y, nz)) { d.VelZ = 0; } else d.Z = nz;

            d.OnGround = false;
            if (_world.IsSolid(d.X, ny, d.Z))
            {
                d.Y = Math.Floor(ny) + 1;
                d.VelY = 0;
                d.OnGround = true;
            }
            else d.Y = ny;

            if (d.OnGround) { d.VelX *= 0.8; d.VelZ *= 0.8; }

            // Pickup: within 1.2 blocks of the player's feet, and only in survival
            // or when the player is not in creative-fly mode (JS allowed both).
            double dx = d.X - _player.X, dy = d.Y - (_player.Y + 0.5), dz = d.Z - _player.Z;
            if (dx * dx + dy * dy + dz * dz < 1.44 && d.Age > 0.5f)
            {
                _inventory.Add(d.Id, d.Count, d.Durability);
                d.Dead = true;
            }

            if (d.Age > 300f) d.Dead = true;   // 5 minute despawn
            if (d.Dead) DroppedItems.RemoveAt(i);
        }
    }
}
