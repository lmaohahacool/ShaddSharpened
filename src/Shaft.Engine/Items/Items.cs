using Shaft.Engine.Config;

namespace Shaft.Engine.Items;

/// <summary>
/// Unified stack-id space. Blocks keep their <see cref="Block"/> ids (0..34);
/// items use 1000+, matching <c>ITEM_STRING_TO_ID</c> in <c>save.js</c> so that
/// save files round-trip without a translation table.
/// </summary>
public static class ItemId
{
    public const int STICK = 1000;
    public const int WP = 1001;
    public const int WA = 1002;
    public const int SP = 1003;
    public const int SA = 1004;
    public const int BEEF = 1005;
    public const int STEAK = 1006;
    public const int COAL = 1007;
    public const int RAW_IRON = 1008;
    public const int IRON_INGOT = 1009;
    public const int ROTTEN_FLESH = 1010;

    /// <summary>Port of <c>isItem</c>: in JS items are strings and blocks are numbers.
    /// Here the discriminator is the numeric range.</summary>
    public static bool IsItem(int id) => id >= 1000;

    /// <summary>Port of <c>itemIdToNumber</c>.</summary>
    public static int ToNumber(int id) => id;

    /// <summary>Port of <c>numberToItemId</c>: 0 means empty.</summary>
    public static int FromNumber(int n) => n;
}

public readonly struct ItemInfo
{
    public ItemInfo(string name, uint color, int durability, ToolKind tool, int food, int fuel)
    {
        Name = name; Color = color; Durability = durability; Tool = tool; Food = food; Fuel = fuel;
    }

    public string Name { get; }
    public uint Color { get; }
    /// <summary>0 = not a tool.</summary>
    public int Durability { get; }
    public ToolKind Tool { get; }
    /// <summary>Hunger restored on eat; 0 = not food.</summary>
    public int Food { get; }
    /// <summary>Burn ticks when used as furnace fuel; 0 = not fuel.</summary>
    public int Fuel { get; }
}

public enum ToolKind { None = 0, Pickaxe = 1, Axe = 2 }

public static class ItemTable
{
    private static readonly Dictionary<int, ItemInfo> Items = new()
    {
        [ItemId.STICK]        = new("Stick",         0x8b5a2b, 0,   ToolKind.None,    0, 0),
        [ItemId.WP]           = new("Wooden Pickaxe",0xb58a5a, 60,  ToolKind.Pickaxe, 0, 0),
        [ItemId.WA]           = new("Wooden Axe",    0xb58a5a, 60,  ToolKind.Axe,     0, 0),
        [ItemId.SP]           = new("Stone Pickaxe", 0x808080, 130, ToolKind.Pickaxe, 0, 0),
        [ItemId.SA]           = new("Stone Axe",     0x808080, 130, ToolKind.Axe,     0, 0),
        [ItemId.BEEF]         = new("Raw Beef",      0xcc4444, 0,   ToolKind.None,    4, 0),
        [ItemId.STEAK]        = new("Steak",         0x8b3a1a, 0,   ToolKind.None,    8, 0),
        [ItemId.COAL]         = new("Coal",          0x1a1a1a, 0,   ToolKind.None,    0, 400),
        [ItemId.RAW_IRON]     = new("Raw Iron",      0xc8a890, 0,   ToolKind.None,    0, 0),
        [ItemId.IRON_INGOT]   = new("Iron Ingot",    0xd0d0d0, 0,   ToolKind.None,    0, 0),
        [ItemId.ROTTEN_FLESH] = new("Rotten Flesh",  0x6b7a2e, 0,   ToolKind.None,    2, 0),
    };

    public static bool TryGet(int id, out ItemInfo info) => Items.TryGetValue(id, out info);

    /// <summary>Port of <c>getInfo</c>: resolves either a block or an item id.</summary>
    public static (string Name, uint Color) GetInfo(int id)
    {
        if (ItemId.IsItem(id))
            return Items.TryGetValue(id, out var it) ? (it.Name, it.Color) : ("Unknown", 0xff00ff);
        ref readonly var b = ref BlockInfoTable.Get((byte)id);
        return (b.Name, b.Color);
    }

    public static int MaxStack(int id) => GameConfig.INVENTORY_MAX_STACK;

    // ---- tools: port of TOOL_EFFECT / TOOL_REQUIRED_LEVEL / TOOL_TIER ----

    private static readonly Dictionary<ToolKind, byte[]> ToolEffect = new()
    {
        [ToolKind.Pickaxe] = new byte[]
        {
            Block.STONE, Block.COBBLESTONE, Block.CRAFTING_TABLE, Block.COAL_ORE,
            Block.IRON_ORE, Block.FURNACE, Block.WORKING_FURNACE, Block.CHEST, Block.ICE
        },
        [ToolKind.Axe] = new byte[]
        {
            Block.LOG, Block.PLANKS, Block.DOOR_BOTTOM_CLOSED, Block.DOOR_TOP_CLOSED,
            Block.DOOR_BOTTOM_OPEN, Block.DOOR_TOP_OPEN
        },
    };

    private static readonly Dictionary<byte, int> ToolRequiredLevel = new()
    {
        [Block.STONE] = 1, [Block.COBBLESTONE] = 1, [Block.COAL_ORE] = 1,
        [Block.IRON_ORE] = 2, [Block.FURNACE] = 1, [Block.WORKING_FURNACE] = 1,
        [Block.CHEST] = 1,
    };

    private static readonly Dictionary<ToolKind, Dictionary<int, int>> ToolTier = new()
    {
        [ToolKind.Pickaxe] = new() { [ItemId.WP] = 1, [ItemId.SP] = 2 },
        [ToolKind.Axe] = new() { [ItemId.WA] = 1, [ItemId.SA] = 2 },
    };

    /// <summary>Port of <c>requiresTool</c>.</summary>
    public static bool RequiresTool(byte blockId) => ToolRequiredLevel.ContainsKey(blockId);

    /// <summary>
    /// Port of <c>getMiningSpeed</c>. Returns the hardness divisor applied while
    /// mining: 6 for a correct pickaxe, 3 for a correct axe, otherwise 1.
    /// </summary>
    public static int GetMiningSpeed(byte blockId, int heldItemId)
    {
        if (heldItemId == 0 || !ItemId.IsItem(heldItemId)) return 1;
        if (!Items.TryGetValue(heldItemId, out var info) || info.Tool == ToolKind.None) return 1;
        if (!ToolEffect.TryGetValue(info.Tool, out var list)) return 1;
        if (Array.IndexOf(list, blockId) < 0) return 1;
        return info.Tool == ToolKind.Pickaxe ? 6 : info.Tool == ToolKind.Axe ? 3 : 1;
    }

    /// <summary>
    /// Whether a held item is strong enough to obtain a drop from this block.
    /// Combines <c>TOOL_REQUIRED_LEVEL</c> and <c>TOOL_TIER</c> as mining.js does.
    /// </summary>
    public static bool CanHarvest(byte blockId, int heldItemId)
    {
        if (!ToolRequiredLevel.TryGetValue(blockId, out int required)) return true;
        if (heldItemId == 0 || !ItemId.IsItem(heldItemId)) return false;
        if (!Items.TryGetValue(heldItemId, out var info)) return false;
        if (!ToolTier.TryGetValue(info.Tool, out var tiers)) return false;
        return tiers.TryGetValue(heldItemId, out int tier) && tier >= required;
    }

    /// <summary>Port of <c>FUEL_VALUES</c> — coal 400, planks 300, log 250.</summary>
    public static int GetFuelValue(int id) => id switch
    {
        ItemId.COAL => 400,
        Block.PLANKS => 300,
        Block.LOG => 250,
        _ => 0,
    };
}

/// <summary>Port of <c>SMELT_RECIPES</c>.</summary>
public readonly record struct SmeltRecipe(int Output, float Time);

public static class SmeltingTable
{
    private static readonly Dictionary<int, SmeltRecipe> Recipes = new()
    {
        [ItemId.RAW_IRON] = new(ItemId.IRON_INGOT, 10f),
        [ItemId.BEEF] = new(ItemId.STEAK, 8f),
    };

    public static bool TryGet(int inputId, out SmeltRecipe recipe) => Recipes.TryGetValue(inputId, out recipe);
}

/// <summary>A crafting pattern cell: 0 = empty, otherwise a block/item id.</summary>
public sealed class CraftRecipe
{
    public CraftRecipe(int[,] pattern, int width, int height, int resultId, int resultCount)
    {
        Pattern = pattern; Width = width; Height = height;
        ResultId = resultId; ResultCount = resultCount;
    }

    /// <summary>Row-major; <c>Pattern[y, x] == 0</c> means the cell must be empty.</summary>
    public int[,] Pattern { get; }
    public int Width { get; }
    public int Height { get; }
    public int ResultId { get; }
    public int ResultCount { get; }
}

/// <summary>Port of <c>CRAFT_RECIPES</c> from config.js, in list order.</summary>
/// <remarks>
/// Order is significant: <c>matchRecipe</c> returns the first match. Three recipes
/// share a 2x2-planks shape (crafting table, bed, and — at 3x2 — the door), so the
/// crafting table wins, exactly as it does in the JS build.
/// </remarks>
public static class CraftTable
{
    public static readonly IReadOnlyList<CraftRecipe> Recipes = Build();

    private static CraftRecipe[] Build()
    {
        const int L = Block.LOG, P = Block.PLANKS, C = Block.COBBLESTONE;
        const int S = ItemId.STICK, CO = ItemId.COAL;

        CraftRecipe R(int[][] rows, int resultId, int count)
        {
            int h = rows.Length, w = rows[0].Length;
            var p = new int[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    p[y, x] = rows[y][x];
            return new CraftRecipe(p, w, h, resultId, count);
        }

        return new[]
        {
            R(new[]{ new[]{ L } },                                              Block.PLANKS, 4),
            R(new[]{ new[]{ P, P }, new[]{ P, P } },                            Block.CRAFTING_TABLE, 1),
            R(new[]{ new[]{ P }, new[]{ P } },                                  ItemId.STICK, 2),
            R(new[]{ new[]{ P, P, P }, new[]{ 0, S, 0 }, new[]{ 0, S, 0 } },    ItemId.WP, 1),
            R(new[]{ new[]{ P, P }, new[]{ P, S }, new[]{ 0, S } },             ItemId.WA, 1),
            R(new[]{ new[]{ C, C, C }, new[]{ 0, S, 0 }, new[]{ 0, S, 0 } },    ItemId.SP, 1),
            R(new[]{ new[]{ C, C }, new[]{ C, S }, new[]{ 0, S } },             ItemId.SA, 1),
            R(new[]{ new[]{ C, C, C }, new[]{ C, 0, C }, new[]{ C, C, C } },    Block.FURNACE, 1),
            R(new[]{ new[]{ P, P, P }, new[]{ P, 0, P }, new[]{ P, P, P } },    Block.CHEST, 1),
            R(new[]{ new[]{ P, P }, new[]{ P, P } },                            Block.BED_FOOT, 1),
            R(new[]{ new[]{ CO }, new[]{ S } },                                 Block.TORCH, 4),
            R(new[]{ new[]{ P, P }, new[]{ P, P }, new[]{ P, P } },             Block.DOOR_BOTTOM_CLOSED, 1),
        };
    }
}
