namespace Shaft.Engine.Config;

/// <summary>Port of the <c>BLOCK</c> table and block helpers from <c>dist/config.js</c>.</summary>
/// <remarks>
/// Ids are <see cref="byte"/> because the largest is 34 — this is what makes the
/// flat voxel array 1 byte per block. Kept numerically identical to the JS table
/// so that <c>blockChanges</c> deltas and save files remain interchangeable.
/// </remarks>
public static class Block
{
    public const byte AIR = 0;
    public const byte GRASS = 1;
    public const byte DIRT = 2;
    public const byte STONE = 3;
    public const byte SAND = 4;
    public const byte WATER = 5;
    public const byte SNOW = 6;
    public const byte LOG = 7;
    public const byte LEAVES = 8;
    public const byte CACTUS = 9;
    public const byte PLANKS = 10;
    public const byte CRAFTING_TABLE = 11;
    public const byte COBBLESTONE = 12;
    public const byte COAL_ORE = 13;
    public const byte IRON_ORE = 14;
    public const byte FURNACE = 15;
    public const byte WORKING_FURNACE = 16;
    public const byte CHEST = 17;
    public const byte DIRT_DARK = 18;
    public const byte BED_FOOT = 19;
    public const byte BED_HEAD = 20;
    public const byte TORCH = 21;
    public const byte ICE = 22;

    // Water flow levels. WATER is the level-7 source; FLOW_6..FLOW_1 descend.
    public const byte WATER_FLOW_6 = 23;
    public const byte WATER_FLOW_5 = 24;
    public const byte WATER_FLOW_4 = 25;
    public const byte WATER_FLOW_3 = 26;
    public const byte WATER_FLOW_2 = 27;
    public const byte WATER_FLOW_1 = 28;
    public const byte WATER_FALLING = 29;

    public const byte SHORT_GRASS = 30;
    public const byte DOOR_BOTTOM_CLOSED = 31;
    public const byte DOOR_TOP_CLOSED = 32;
    public const byte DOOR_BOTTOM_OPEN = 33;
    public const byte DOOR_TOP_OPEN = 34;

    public const int MAX_ID = 34;

    public static bool IsWater(byte id) =>
        id == WATER || (id >= WATER_FLOW_6 && id <= WATER_FALLING);

    public static bool IsDoor(byte id) =>
        id == DOOR_BOTTOM_CLOSED || id == DOOR_TOP_CLOSED ||
        id == DOOR_BOTTOM_OPEN || id == DOOR_TOP_OPEN;

    public static bool IsDoorOpen(byte id) => id == DOOR_BOTTOM_OPEN || id == DOOR_TOP_OPEN;

    /// <summary>Port of <c>getWaterHeight</c>: the rendered surface height of a water block.</summary>
    public static float GetWaterHeight(byte id) => id switch
    {
        WATER => 0.8f,
        WATER_FLOW_6 => 0.7f,
        WATER_FLOW_5 => 0.6f,
        WATER_FLOW_4 => 0.5f,
        WATER_FLOW_3 => 0.4f,
        WATER_FLOW_2 => 0.3f,
        WATER_FLOW_1 => 0.2f,
        WATER_FALLING => 1.0f,
        _ => 1f,
    };

    /// <summary>Port of <c>getWaterLevel</c>. Source = 7, falling = 8, non-water = 0.</summary>
    public static int GetWaterLevel(byte id) => id switch
    {
        WATER => 7,
        WATER_FLOW_6 => 6,
        WATER_FLOW_5 => 5,
        WATER_FLOW_4 => 4,
        WATER_FLOW_3 => 3,
        WATER_FLOW_2 => 2,
        WATER_FLOW_1 => 1,
        WATER_FALLING => 8,
        _ => 0,
    };

    /// <summary>Port of <c>getWaterBlockFromLevel</c>.</summary>
    public static byte GetWaterBlockFromLevel(int level) => level switch
    {
        >= 7 => WATER,
        6 => WATER_FLOW_6,
        5 => WATER_FLOW_5,
        4 => WATER_FLOW_4,
        3 => WATER_FLOW_3,
        2 => WATER_FLOW_2,
        1 => WATER_FLOW_1,
        _ => AIR,
    };

    /// <summary>
    /// Port of <c>isSolid</c> from <c>mining.js</c> — the predicate used by player
    /// collision and mob collision. Note water, leaves, torches, beds, short grass
    /// and open doors are all passable.
    /// </summary>
    public static bool IsSolid(byte id)
    {
        if (id == AIR) return false;
        if (IsWater(id)) return false;
        if (id == LEAVES || id == TORCH || id == BED_FOOT || id == BED_HEAD) return false;
        if (id == SHORT_GRASS) return false;
        if (IsDoor(id)) return !IsDoorOpen(id);
        return true;
    }

    /// <summary>
    /// Port of the mesher's <c>isOpaque</c> in <c>chunks.js</c>: decides face culling.
    /// Anything not listed lets neighbouring faces show through.
    /// </summary>
    public static bool IsOpaque(byte b) =>
        b != AIR &&
        !IsWater(b) &&
        !IsDoor(b) &&
        b != LEAVES &&
        b != CACTUS &&
        b != TORCH &&
        b != BED_FOOT &&
        b != BED_HEAD &&
        b != SHORT_GRASS;

    /// <summary>Port of the lighting module's <c>isTransparent</c>: what light floods through.</summary>
    public static bool IsLightTransparent(byte id) =>
        id == AIR || IsWater(id) || id == LEAVES || id == TORCH ||
        id == BED_FOOT || id == BED_HEAD;
}

/// <summary>Per-block metadata table, ported from <c>BLOCK_INFO</c>.</summary>
public readonly struct BlockInfo
{
    public BlockInfo(string name, uint color, float hardness)
    {
        Name = name; Color = color; Hardness = hardness;
    }

    public string Name { get; }
    /// <summary>0xRRGGBB, used for HUD icons and the flat-colour fallback.</summary>
    public uint Color { get; }
    /// <summary>Mining hardness; 999 marks unbreakable water.</summary>
    public float Hardness { get; }
}

public static class BlockInfoTable
{
    private static readonly BlockInfo[] Table = Build();

    public static ref readonly BlockInfo Get(byte id)
        => ref Table[id <= Block.MAX_ID ? id : 0];

    private static BlockInfo[] Build()
    {
        var t = new BlockInfo[Block.MAX_ID + 1];
        void S(byte id, string n, uint c, float h) => t[id] = new BlockInfo(n, c, h);

        S(Block.AIR, "Air", 0x000000, 0);
        S(Block.GRASS, "Grass", 0x4caf50, .6f);
        S(Block.DIRT, "Dirt", 0x8b5a2b, .5f);
        S(Block.DIRT_DARK, "Dark Dirt", 0x4a2c15, .5f);
        S(Block.STONE, "Stone", 0x808080, 1.5f);
        S(Block.SAND, "Sand", 0xf4e4a0, .5f);
        S(Block.WATER, "Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_6, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_5, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_4, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_3, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_2, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FLOW_1, "Flowing Water", 0x2a5db0, 999);
        S(Block.WATER_FALLING, "Falling Water", 0x2a5db0, 999);
        S(Block.SNOW, "Snow", 0xffffff, .3f);
        S(Block.LOG, "Wood", 0x6b4423, 1);
        S(Block.LEAVES, "Leaves", 0x3a7a3a, .2f);
        S(Block.CACTUS, "Cactus", 0x2d7a2d, .4f);
        S(Block.PLANKS, "Planks", 0xb58a5a, .8f);
        S(Block.CRAFTING_TABLE, "Crafting Table", 0x8b5a2b, 1.2f);
        S(Block.COBBLESTONE, "Cobblestone", 0x7a7a7a, 2);
        S(Block.COAL_ORE, "Coal Ore", 0x2a2a2a, 2);
        S(Block.IRON_ORE, "Iron Ore", 0xc8a890, 2.5f);
        S(Block.FURNACE, "Furnace", 0x666666, 2.5f);
        S(Block.WORKING_FURNACE, "Furnace", 0x666666, 2.5f);
        S(Block.CHEST, "Chest", 0x8b5a2b, 2.5f);
        S(Block.BED_FOOT, "Bed", 0xa02a2a, .4f);
        S(Block.BED_HEAD, "Bed", 0xffffff, .4f);
        S(Block.TORCH, "Torch", 0xffcc44, .2f);
        S(Block.ICE, "Ice", 0x7fc8e8, .5f);
        S(Block.SHORT_GRASS, "Short Grass", 0x4caf50, 0);
        S(Block.DOOR_BOTTOM_CLOSED, "Door", 0x8b5a2b, .8f);
        S(Block.DOOR_TOP_CLOSED, "Door", 0x8b5a2b, .8f);
        S(Block.DOOR_BOTTOM_OPEN, "Door", 0x8b5a2b, .8f);
        S(Block.DOOR_TOP_OPEN, "Door", 0x8b5a2b, .8f);
        return t;
    }
}

/// <summary>
/// Port of <c>BLOCK_TEXTURE_MAP</c>: which atlas slot each face samples.
/// <c>Front</c> is -1 when the block has no distinct front face.
/// </summary>
public readonly struct BlockTextures
{
    public BlockTextures(sbyte top, sbyte side, sbyte bottom, sbyte overlay, sbyte front)
    {
        Top = top; Side = side; Bottom = bottom; Overlay = overlay; Front = front;
    }

    public sbyte Top { get; }
    public sbyte Side { get; }
    public sbyte Bottom { get; }
    /// <summary>Grass side overlay slot; -1 when absent.</summary>
    public sbyte Overlay { get; }
    /// <summary>Furnace/chest/door front slot; -1 when absent.</summary>
    public sbyte Front { get; }

    public bool HasFront => Front >= 0;
    public bool HasOverlay => Overlay >= 0;
}

public static class BlockTextureMap
{
    private static readonly BlockTextures[] Table = Build();

    public static ref readonly BlockTextures Get(byte id)
        => ref Table[id <= Block.MAX_ID ? id : 0];

    private static BlockTextures[] Build()
    {
        var t = new BlockTextures[Block.MAX_ID + 1];
        for (int i = 0; i < t.Length; i++) t[i] = new BlockTextures(0, 0, 0, -1, -1);

        void S(byte id, sbyte top, sbyte side, sbyte bottom, sbyte overlay = -1, sbyte front = -1)
            => t[id] = new BlockTextures(top, side, bottom, overlay, front);

        S(Block.GRASS, 0, 1, 2, overlay: 24);
        S(Block.DIRT, 2, 2, 2);
        S(Block.DIRT_DARK, 25, 25, 25);
        S(Block.STONE, 3, 3, 3);
        S(Block.SAND, 4, 4, 4);
        // All eight water ids share slot 5, exactly as in config.js.
        S(Block.WATER, 5, 5, 5);
        S(Block.WATER_FLOW_6, 5, 5, 5);
        S(Block.WATER_FLOW_5, 5, 5, 5);
        S(Block.WATER_FLOW_4, 5, 5, 5);
        S(Block.WATER_FLOW_3, 5, 5, 5);
        S(Block.WATER_FLOW_2, 5, 5, 5);
        S(Block.WATER_FLOW_1, 5, 5, 5);
        S(Block.WATER_FALLING, 5, 5, 5);
        S(Block.SNOW, 6, 6, 6);
        S(Block.LOG, 8, 7, 8);
        S(Block.LEAVES, 9, 9, 9);
        S(Block.CACTUS, 10, 11, 12);
        S(Block.PLANKS, 13, 13, 13);
        S(Block.CRAFTING_TABLE, 14, 15, 13);
        S(Block.COBBLESTONE, 16, 16, 16);
        S(Block.COAL_ORE, 17, 17, 17);
        S(Block.IRON_ORE, 18, 18, 18);
        S(Block.FURNACE, 19, 19, 19, front: 20);
        S(Block.WORKING_FURNACE, 19, 19, 19, front: 21);
        S(Block.CHEST, 22, 22, 22, front: 23);
        S(Block.BED_FOOT, 26, 26, 26);
        S(Block.BED_HEAD, 27, 27, 27);
        S(Block.TORCH, 28, 28, 28);
        S(Block.ICE, 29, 29, 29);
        S(Block.SHORT_GRASS, 30, 30, 30);

        // Doors: top/bottom textures for front/back faces, sides for thin edges.
        // Note config.js puts the *front* slot values here, matching the mesher's
        // addDoorBlock which reads texDef.front for the panel and texDef.side for edges.
        S(Block.DOOR_BOTTOM_CLOSED, 33, 33, 33, front: 32);
        S(Block.DOOR_TOP_CLOSED, 33, 33, 33, front: 31);
        S(Block.DOOR_BOTTOM_OPEN, 33, 33, 33, front: 32);
        S(Block.DOOR_TOP_OPEN, 33, 33, 33, front: 31);
        return t;
    }
}

/// <summary>Port of <c>BIOME</c> and <c>GRASS_COLORS</c>.</summary>
public static class Biome
{
    public const int PLAINS = 0;
    public const int FOREST = 1;
    public const int MOUNTAIN = 2;
    public const int DESERT = 3;
    public const int SNOWY = 4;
    public const int OLD_GROWTH = 5;

    public static readonly uint[] GrassColors =
    {
        0x91bd59, // PLAINS
        0x79c05a, // FOREST
        0x8ab689, // MOUNTAIN
        0xbfb755, // DESERT
        0x80b497, // SNOWY
        0x4f7d33, // OLD_GROWTH
    };

    public static uint GrassColor(int biome)
        => biome >= 0 && biome < GrassColors.Length ? GrassColors[biome] : 0x91bd59;
}
