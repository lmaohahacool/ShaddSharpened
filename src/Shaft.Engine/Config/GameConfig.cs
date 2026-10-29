namespace Shaft.Engine.Config;

/// <summary>
/// Global tuning constants, ported verbatim from <c>dist/config.js</c> and
/// <c>dist/lighting.js</c>.
/// </summary>
public static class GameConfig
{
    public const int CHUNK_SIZE = 16;
    public const int RENDER_DISTANCE = 5;
    public const int SEA_LEVEL = 0;
    public const int WORLD_BOTTOM = -30;

    /// <summary>
    /// Voxel array vertical extent. Taken from <c>lighting.js</c>
    /// (<c>HEIGHT = 128, BOTTOM = -32</c>) so that the light volume and the
    /// block volume are the same shape and can share one index function.
    /// </summary>
    public const int CHUNK_MIN_Y = -32;
    public const int CHUNK_HEIGHT = 128;
    public const int CHUNK_MAX_Y = CHUNK_MIN_Y + CHUNK_HEIGHT - 1; // 95

    public const int CHUNK_VOLUME = CHUNK_SIZE * CHUNK_HEIGHT * CHUNK_SIZE; // 32768

    // Texture atlas geometry (config.js)
    public const int ATLAS_COLS = 64;
    public const int ATLAS_SIZE = 16;
    public const int ATLAS_WIDTH = ATLAS_COLS * ATLAS_SIZE;
    public const int ATLAS_HEIGHT = ATLAS_SIZE;
    public const float UV_INSET = 0.5f / ATLAS_SIZE / ATLAS_COLS;

    // Decoration grids
    public const int TREE_GRID = 12;
    public const int CACTUS_GRID = 14;
    public const float CACTUS_CHANCE = 0.35f;
    public const float CACTUS_WIDTH = 0.8f;

    // Player tuning (config.js)
    public const float PLAYER_HEIGHT = 1.8f;
    public const float PLAYER_EYE = 1.6f;
    public const float PLAYER_SNEAK_EYE = 1.35f;
    public const float WALK_SPEED = 6f;
    public const float SPRINT_SPEED = 9f;
    public const float SNEAK_SPEED = 2.5f;
    public const float GRAVITY = 25f;
    public const float JUMP_VELOCITY = 9f;
    public const float FLY_SPEED = 12f;
    public const float MOB_GRAVITY = 25f;

    /// <summary>Player collision half-extent. The JS isSolid probes treat the
    /// player as a point sampled at feet and at +1.6; the port keeps that exact
    /// behaviour in <see cref="Shaft.Engine.Game.PlayerPhysics"/> but exposes the
    /// radius here for the AABB-aware path.</summary>
    public const float PLAYER_RADIUS = 0.3f;

    public const int DAY_LENGTH = 600;

    // Inventory (config.js)
    public const int HOTBAR_SIZE = 9;
    public const int STORAGE_SIZE = 27;
    public const int TOTAL_SLOTS = HOTBAR_SIZE + STORAGE_SIZE;
    public const int INVENTORY_MAX_STACK = 64;
    public const int CHEST_SLOTS = 27;

    // Lighting (lighting.js)
    public const int MAX_LIGHT = 15;
    public const int TORCH_LIGHT = 14;

    // Water tick (water.js)
    public const int WATER_PLAYER_RADIUS = 40;
    public const int WATER_MAX_UPDATES_PER_TICK = 500;
    public const float WATER_TICK_INTERVAL = 0.1f;

    // Chunk streaming (main.js)
    public const float CHUNK_UPDATE_INTERVAL = 0.2f;
    public const float AUTOSAVE_INTERVAL = 30f;

    /// <summary>Save format version. Phase A keeps the JS v6 layout byte-identical;
    /// Phase B (exact-int64 hash) bumps this to 7. See MIGRATION-BRIEF.md §3.</summary>
    public const ushort SAVE_VERSION = 6;
}
