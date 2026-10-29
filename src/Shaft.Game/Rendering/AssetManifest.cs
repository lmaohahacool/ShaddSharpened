namespace Shaft.Game.Rendering;

/// <summary>
/// The asset layout the browser build assumes, restated in C#.
/// <para>
/// <see cref="TextureFiles"/> is a verbatim port of <c>textureFiles</c> in
/// <c>dist/config.js</c>: entry <c>i</c> is blitted into atlas slot <c>i</c>, so the
/// slot numbers baked into <c>BlockTextureMap</c> (and therefore into every mesh the
/// engine produces) only line up if this order is preserved exactly.
/// </para>
/// <para>
/// <see cref="ItemTextureFiles"/> is a verbatim port of the list inside
/// <c>loadItemTextures()</c> in <c>dist/textures.js</c>. The JS keyed that table by
/// strings (<c>item_stick</c>, …) but every lookup used the numeric item id
/// (<c>itemTextures[1000]</c>), so the icons silently fell back to flat coloured
/// squares. The order here is the order the ids appear in <c>ItemId</c>, which is what
/// the table was obviously meant to be indexed by — see
/// <see cref="GameOptions.ItemsUseFlatColourIcons"/> for the bit-exact-JS switch.
/// </para>
/// </summary>
public static class AssetManifest
{
    /// <summary>Relative to the <c>dist</c> root, in atlas-slot order (slot 0..33).</summary>
    public static readonly string[] TextureFiles =
    {
        "textures/grass_top.png",          // 0
        "textures/grass_side.png",         // 1
        "textures/dirt.png",               // 2
        "textures/stone.png",              // 3
        "textures/sand.png",               // 4
        "textures/water.png",              // 5
        "textures/snow.png",               // 6
        "textures/wood_side.png",          // 7
        "textures/wood_top.png",           // 8
        "textures/leaves.png",             // 9
        "textures/cactus_top.png",         // 10
        "textures/cactus_side.png",        // 11
        "textures/cactus_bottom.png",      // 12
        "textures/planks.png",             // 13
        "textures/CT_top.png",             // 14
        "textures/CT_side.png",            // 15
        "textures/cobblestone.png",        // 16
        "textures/coal_ore.png",           // 17
        "textures/iron_ore.png",           // 18
        "textures/furnace_tbs.png",        // 19
        "textures/furnace_front.png",      // 20
        "textures/wfurnace_front.png",     // 21
        "textures/chest_tbs.png",          // 22
        "textures/chest_front.png",        // 23
        "textures/grass_side_overlay.png", // 24
        "textures/dirt_dark.png",          // 25
        "textures/bed_red.png",            // 26
        "textures/bed_white.png",          // 27
        "textures/torch.png",              // 28
        "textures/ice.png",                // 29
        "textures/short_grass.png",        // 30
        "textures/door_top.png",           // 31
        "textures/door_bottom.png",        // 32
        "textures/door_sides.png",         // 33
    };

    /// <summary>Item icon PNGs, indexed by <c>itemId - ItemId.STICK</c>.</summary>
    public static readonly string[] ItemTextureFiles =
    {
        "textures/sticks.png",        // 1000 STICK
        "textures/wp.png",            // 1001 WP
        "textures/wa.png",            // 1002 WA
        "textures/sp.png",            // 1003 SP
        "textures/sa.png",            // 1004 SA
        "textures/beef.png",          // 1005 BEEF
        "textures/steak.png",         // 1006 STEAK
        "textures/coal.png",          // 1007 COAL
        "textures/raw_iron.png",      // 1008 RAW_IRON
        "textures/iron_ingot.png",    // 1009 IRON_INGOT
        "textures/rotten_flesh.png",  // 1010 ROTTEN_FLESH
    };

    /// <summary>Port of <c>dist/models/manifest.json</c>.</summary>
    public static readonly (string Name, string Model, string Texture)[] Models =
    {
        ("cow", "models/cow.json", "textures/cowtexture.png"),
        ("zombie", "models/zombie.json", "textures/zombietexture.png"),
        ("torch", "models/torch.json", "textures/torchtexture.png"),
    };

    /// <summary>Port of the sound table in <c>dist/audio.js</c>, with its volumes.</summary>
    public static readonly (string Key, string Path, float Volume, bool Loop)[] Sounds =
    {
        ("break", "sounds/break.mp3", 0.5f, false),
        ("place", "sounds/place.mp3", 0.5f, false),
        ("footstep", "sounds/footstep.mp3", 0.2f, false),
        ("hit", "sounds/hit.mp3", 0.6f, false),
        ("eating", "sounds/eating.mp3", 0.6f, false),
        ("music", "sounds/bg_music.mp3", 0.3f, true),
    };
}

/// <summary>
/// Runtime knobs that differ from (or can be forced back to) the browser build.
/// Each one documents the JS behaviour it replaces.
/// </summary>
public sealed class GameOptions
{
    /// <summary>
    /// <c>main.js</c> renders the held block with the terrain atlas bound to a plain
    /// <c>BoxGeometry</c> whose UVs span 0..1, so the whole 34-tile strip gets squashed
    /// onto every face. That is a bug, not a style: the intent (and what the rest of the
    /// game does) is per-face block tiles. Left true, this reproduces the squashed cube.
    /// </summary>
    public bool HeldItemUseWholeAtlasSquash;

    /// <summary>
    /// <c>true</c> makes item icons flat coloured squares, exactly as the browser build
    /// does because of the string/numeric key mismatch described on
    /// <see cref="AssetManifest.ItemTextureFiles"/>. Default false = show the real art.
    /// </summary>
    public bool ItemsUseFlatColourIcons;

    /// <summary>Requested context version; falls back to 3.3 core if unavailable.</summary>
    public int MajorVersion = 4;
    public int MinorVersion = 5;

    public int WindowWidth = 1280;
    public int WindowHeight = 720;
    public bool Fullscreen;

    /// <summary>Mirrors <c>renderer.setPixelRatio(Math.min(devicePixelRatio, 2))</c>.</summary>
    public float MaxPixelRatio = 2.0f;

    /// <summary>How many chunk meshes may be built per frame (<c>processChunkQueues</c>).</summary>
    public int MeshBudgetPerFrame = 4;

    public string? InitialSaveName;
    public int? InitialSeed;
    public int? InitialSlot;
    public bool SkipTitleScreen;

    /// <summary>core.js <c>BIOME_BLEND</c>, i.e. the <c>?blend=</c> URL parameter. Default 4.</summary>
    public int BiomeBlend = 4;

    /// <summary>core.js <c>SPAWN_BIOME</c>, i.e. the <c>?spawn=</c> URL parameter (null = natural).</summary>
    public int? SpawnBiome;

    /// <summary>Set to skip OpenAL entirely (also forced by <c>--no-audio</c>).</summary>
    public bool DisableAudio;
}
