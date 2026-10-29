using Shaft.Engine.Config;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Engine.Game;

/// <summary>Port of <c>chestStates</c> in <c>dist/chest.js</c>: 27 slots per chest,
/// keyed by world position and persisted with the save.</summary>
public sealed class ChestSystem
{
    private readonly Dictionary<BlockPos, ItemStack[]> _states = new();

    public IReadOnlyDictionary<BlockPos, ItemStack[]> States => _states;

    public bool Open { get; private set; }
    public BlockPos? OpenKey { get; private set; }

    public ItemStack[] GetState(int x, int y, int z)
    {
        var key = new BlockPos(x, y, z);
        if (!_states.TryGetValue(key, out var slots))
        {
            slots = new ItemStack[GameConfig.CHEST_SLOTS];
            _states[key] = slots;
        }
        return slots;
    }

    public void OpenChest(int x, int y, int z)
    {
        Open = true;
        OpenKey = new BlockPos(x, y, z);
        GetState(x, y, z);
    }

    public void CloseChest() { Open = false; OpenKey = null; }

    public bool RemoveAt(int x, int y, int z) => _states.Remove(new BlockPos(x, y, z));

    /// <summary>Dumps a chest's contents so they can be dropped when it is broken.</summary>
    public IEnumerable<ItemStack> TakeAll(int x, int y, int z)
    {
        var key = new BlockPos(x, y, z);
        if (!_states.TryGetValue(key, out var slots)) yield break;
        foreach (var s in slots) if (!s.IsEmpty) yield return s;
        _states.Remove(key);
    }

    public void Load(BlockPos key, ItemStack[] slots) => _states[key] = slots;
}

/// <summary>Per-furnace smelting state, matching the record <c>furnace.js</c> keeps.</summary>
public sealed class FurnaceState
{
    public ItemStack Input;
    public ItemStack Fuel;
    public ItemStack Output;
    public float FuelTime;
    public float Progress;
}

/// <summary>
/// Port of <c>updateFurnaces</c> from <c>dist/furnace.js</c>.
/// </summary>
/// <remarks>
/// Preserves the original's unusual fuel accounting: <c>FUEL_VALUES</c> is divided
/// by 20 to produce <c>fuelTime</c>, which is then decremented by raw seconds — so
/// one coal (400) buys 20 seconds of burning. Smelting <c>progress</c> accumulates
/// seconds and completes at the recipe's <c>time</c> (raw iron 10 s, beef 8 s).
/// </remarks>
public sealed class FurnaceSystem
{
    private readonly Dictionary<BlockPos, FurnaceState> _states = new();
    private readonly VoxelWorld _world;

    public IReadOnlyDictionary<BlockPos, FurnaceState> States => _states;

    public bool Open { get; private set; }
    public BlockPos? OpenKey { get; private set; }

    public FurnaceSystem(VoxelWorld world) { _world = world; }

    public FurnaceState GetState(int x, int y, int z)
    {
        var key = new BlockPos(x, y, z);
        if (!_states.TryGetValue(key, out var st))
        {
            st = new FurnaceState();
            _states[key] = st;
        }
        return st;
    }

    public void OpenFurnace(int x, int y, int z) { Open = true; OpenKey = new BlockPos(x, y, z); GetState(x, y, z); }
    public void CloseFurnace() { Open = false; OpenKey = null; }

    /// <summary>
    /// Port of <c>closeFurnaceUI</c> (furnace.js:44). Closing the furnace hands the input,
    /// fuel and output stacks back to the player — in that order — and empties the furnace.
    /// The browser build really does this, so a furnace is never a storage container; the
    /// port keeps the behaviour rather than silently "fixing" it.
    /// </summary>
    /// <returns>The stacks to push into the inventory. Empty when no furnace was open.</returns>
    public List<ItemStack> CloseAndCollect()
    {
        var taken = new List<ItemStack>(3);
        if (!Open) return taken;

        if (OpenKey is BlockPos key && _states.TryGetValue(key, out var st))
        {
            if (!st.Input.IsEmpty) { taken.Add(st.Input); st.Input = ItemStack.Empty; }
            if (!st.Fuel.IsEmpty) { taken.Add(st.Fuel); st.Fuel = ItemStack.Empty; }
            if (!st.Output.IsEmpty) { taken.Add(st.Output); st.Output = ItemStack.Empty; }
        }

        Open = false;
        OpenKey = null;
        return taken;
    }
    public bool RemoveAt(int x, int y, int z) => _states.Remove(new BlockPos(x, y, z));
    public void Load(BlockPos key, FurnaceState st) => _states[key] = st;

    /// <summary>Runs every furnace for one frame and flips blocks between
    /// <c>FURNACE</c> and <c>WORKING_FURNACE</c> so the lit texture shows.</summary>
    public void Update(float dt)
    {
        if (_states.Count == 0) return;

        foreach (var (key, st) in _states)
        {
            bool inputOK = !st.Input.IsEmpty && SmeltingTable.TryGet(st.Input.Id, out _);
            bool active = false;

            if (inputOK)
            {
                if (st.FuelTime <= 0)
                {
                    int fuelPts = st.Fuel.IsEmpty ? 0 : ItemTable.GetFuelValue(st.Fuel.Id);
                    if (!st.Fuel.IsEmpty && fuelPts > 0)
                    {
                        st.FuelTime += fuelPts / 20f;
                        st.Fuel.Count--;
                        if (st.Fuel.Count <= 0) st.Fuel = ItemStack.Empty;
                    }
                }

                if (st.FuelTime > 0 && SmeltingTable.TryGet(st.Input.Id, out var recipe))
                {
                    bool canOutput = (st.Output.IsEmpty || st.Output.Id == recipe.Output)
                                     && st.Output.Count < GameConfig.INVENTORY_MAX_STACK;
                    if (canOutput)
                    {
                        active = true;
                        st.FuelTime -= dt;
                        st.Progress += dt;
                        if (st.Progress >= recipe.Time)
                        {
                            if (st.Output.IsEmpty)
                                st.Output = new ItemStack { Id = recipe.Output, Count = 1 };
                            else
                                st.Output.Count += 1;

                            st.Input.Count--;
                            if (st.Input.Count <= 0) st.Input = ItemStack.Empty;
                            st.Progress = 0;
                        }
                    }
                }
            }

            byte curBlock = _world.GetBlock(key.X, key.Y, key.Z);
            if (active && curBlock == Block.FURNACE)
                _world.SetBlock(key.X, key.Y, key.Z, Block.WORKING_FURNACE);
            else if (!active && curBlock == Block.WORKING_FURNACE)
                _world.SetBlock(key.X, key.Y, key.Z, Block.FURNACE);
        }
    }
}
