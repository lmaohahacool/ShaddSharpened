using System.Numerics;
using System.Text;
using Shaft.Engine.Config;
using Shaft.Engine.Game;
using Shaft.Engine.Items;
using Shaft.Engine.World;

namespace Shaft.Game.Rendering;

/// <summary>Modal text prompt standing in for the browser's <c>prompt()</c>/<c>confirm()</c>.</summary>
public enum PromptKind { None, NewWorld, DeleteWorld, SaveQuit }

/// <summary>
/// The UI state the DOM held: which overlays are visible, the chat buffer, the prompt
/// dialog. Panel visibility for inventory/furnace/chest is not here — that lives in the
/// engine (<see cref="CraftingSystem.Open"/>, <see cref="FurnaceSystem.Open"/>,
/// <see cref="ChestSystem.Open"/>) exactly as it did in the JS modules.
/// </summary>
public sealed class HudState
{
    public bool TitleVisible = true;
    public bool ClickHintVisible;
    public bool GamemodeMenuVisible;
    public bool SettingsVisible;
    public bool DeathVisible;
    public bool SleepVisible;

    public bool ChatOpen;
    public string ChatBuffer = "";
    public int Caret;

    public PromptKind Prompt = PromptKind.None;
    public string PromptValue = "";
    public string PromptMessage = "";
    public int? PromptSlot;

    /// <summary>Biome-blend slider, 0..16, from the settings menu.</summary>
    public int BlendValue = 4;

    public bool MusicEnabled = true;

    /// <summary>"Saved!" flash over the info line (Ctrl+S).</summary>
    public float SavedFlash;

    /// <summary>Eating feedback: the green hunger flash and the rising "+N" popup.</summary>
    public float EatingFlash;
    public float EatPopup;
    public int EatPopupValue;

    /// <summary>Set while the pointer is unlocked and the game is running.</summary>
    public bool PointerLocked;

    /// <summary>True once the player has started a world; gates most of the HUD.</summary>
    public bool GameStarted;

    public string TitleStatus = "";

    /// <summary>Any panel/menu that pauses the player, matching <c>anyPanelOpen()</c>.</summary>
    public bool AnyPanelOpen(GameSession? s) =>
        s is not null && s.Started &&
        (s.Crafting.Open || s.Crafting.TableOpen || s.Furnaces.Open || s.Chests.Open ||
         GamemodeMenuVisible || SettingsVisible || DeathVisible);
}

/// <summary>
/// Everything the UI decided this frame that the host has to act on. Slot clicks carry the
/// slot index plus the modifier state, which is all <c>Inventory.ClickSlot</c> and friends
/// need; the widget ids themselves never leave this file.
/// </summary>
public sealed class HudEvents
{
    // Container clicks.
    public (int Slot, bool Right, bool Shift)? InvClick;
    public (int Slot, bool Right, bool Shift)? CraftClick;
    public (bool Right, bool Shift)? CraftOutputClick;
    public (int Slot, bool Right, bool Shift)? ChestClick;
    public (int Slot, bool Right, bool Shift)? FurnaceClick;
    public (int Id, int Amount)? CreativeGive;
    public bool TrashClick;

    public int? SelectHotbar;
    public int? PlaySaveSlot;
    public int? DeleteSaveSlot;
    public bool NewWorld;
    public bool Respawn;
    public bool BackToTitle;
    public bool SetCreative, SetSurvival, CloseGamemodeMenu;
    public bool CloseSettings, SaveAndQuit;
    public int? SetBlend;
    public bool WakeUp;
    public bool RequestPointerLock;
    public bool OpenSettingsFromHint;
    public string? ChatSubmit;
    public string? PromptConfirm;
    public bool PromptCancel;
    public bool CloseClickedPanel;

    /// <summary>Clears every field. The host reuses one instance per frame.</summary>
    public void Reset()
    {
        InvClick = null; CraftClick = null; CraftOutputClick = null;
        ChestClick = null; FurnaceClick = null; CreativeGive = null;
        TrashClick = false;
        SelectHotbar = null; PlaySaveSlot = null; DeleteSaveSlot = null;
        NewWorld = false; Respawn = false; BackToTitle = false;
        SetCreative = false; SetSurvival = false; CloseGamemodeMenu = false;
        CloseSettings = false; SaveAndQuit = false; SetBlend = null;
        WakeUp = false; RequestPointerLock = false; OpenSettingsFromHint = false;
        ChatSubmit = null; PromptConfirm = null; PromptCancel = false;
        CloseClickedPanel = false;
    }
}

/// <summary>
/// Draws the whole HUD. Every position, size and colour here is taken from
/// <c>dist/index.html</c> + <c>dist/style.css</c>; the comments name the element each
/// block reproduces so the mapping stays checkable against the browser build.
/// </summary>
public static class HudScreens
{
    // ------------------------------------------------------------- CSS palette

    public static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    public static Vector4 Hex(uint c, float a = 1f) => Rgb((int)(c >> 16) & 0xff, (int)(c >> 8) & 0xff, (int)(c & 0xff), a);

    private static readonly Vector4 White = new(1, 1, 1, 1);
    private static readonly Vector4 PanelBg = Rgb(0xc6, 0xc6, 0xc6);
    private static readonly Vector4 PanelBorder = Rgb(0x55, 0x55, 0x55);
    private static readonly Vector4 SlotBg = Rgb(0x8b, 0x8b, 0x8b);
    private static readonly Vector4 SlotBorder = Rgb(0x55, 0x55, 0x55);
    private static readonly Vector4 DarkText = Rgb(0x22, 0x22, 0x22);
    private static readonly Vector4 LabelText = Rgb(0x33, 0x33, 0x33);
    private static readonly Vector4 HotbarBg = new(0, 0, 0, 0.5f);
    private static readonly Vector4 HotbarBorder = new(100 / 255f, 100 / 255f, 100 / 255f, 0.8f);
    private static readonly Vector4 Green = Rgb(0x4c, 0xaf, 0x50);

    private const float SLOT = 50f;
    private const float GAP = 4f;
    private const float ICON = 36f;

    private static readonly Vector2[] HeartShape = BuildHeart();
    private static readonly Vector2[] FoodShape = BuildFood();

    private static Vector2[] BuildHeart()
    {
        // clip-path: polygon(50% 100%, 0 45%, 0 25%, 15% 0, 50% 20%, 85% 0, 100% 25%, 100% 45%)
        (float X, float Y)[] pct =
        {
            (50, 100), (0, 45), (0, 25), (15, 0), (50, 20), (85, 0), (100, 25), (100, 45),
        };
        var pts = new Vector2[pct.Length];
        for (int i = 0; i < pct.Length; i++) pts[i] = new Vector2(pct[i].X * 20f / 100f, pct[i].Y * 20f / 100f);
        return pts;
    }

    private static Vector2[] BuildFood()
    {
        // clip-path: polygon(20% 0, 80% 0, 100% 40%, 80% 100%, 40% 100%, 0 60%, 0 25%)
        (float X, float Y)[] pct = { (20, 0), (80, 0), (100, 40), (80, 100), (40, 100), (0, 60), (0, 25) };
        var pts = new Vector2[pct.Length];
        for (int i = 0; i < pct.Length; i++) pts[i] = new Vector2(pct[i].X * 20f / 100f, pct[i].Y * 20f / 100f);
        return pts;
    }

    // ------------------------------------------------------------------ entry

    /// <summary>
    /// Builds one HUD frame and fills <paramref name="ev"/> with the actions to apply.
    /// </summary>
    public static void Draw(HudRenderer hud, HudState st, GameSession? session,
                            SaveSystem saves, HudEvents ev, float dt)
    {
        if (st.SavedFlash > 0) st.SavedFlash -= dt;
        if (st.EatingFlash > 0) st.EatingFlash -= dt;
        if (st.EatPopup > 0) st.EatPopup -= dt;

        if (st.TitleVisible)
        {
            DrawTitle(hud, st, saves, ev);
            DrawPrompt(hud, st, ev);
            return;
        }

        if (session is null || !session.Started)
        {
            DrawPrompt(hud, st, ev);
            return;
        }

        DrawBlackout(hud, session);
        DrawHotbar(hud, st, session, ev);
        if (session.Player.Mode == Gamemode.Survival)
        {
            DrawHealthBar(hud, st, session);
            DrawHungerBar(hud, st, session);
            DrawEatPopup(hud, st);
        }
        DrawInfo(hud, st, session);
        DrawBiomeHud(hud, st, session);
        DrawMiningBar(hud, session);
        DrawHint(hud);
        DrawMusicCredit(hud);
        DrawChat(hud, st, session, ev);

        if (session.Crafting.Open || session.Crafting.TableOpen) DrawInventory(hud, st, session, ev);
        else if (session.Furnaces.Open) DrawFurnace(hud, session, ev);
        else if (session.Chests.Open) DrawChest(hud, session, ev);

        if (st.GamemodeMenuVisible) DrawGamemodeMenu(hud, ev);
        if (st.SettingsVisible) DrawSettings(hud, st, ev);
        if (st.SleepVisible) DrawSleep(hud, ev);
        if (st.DeathVisible) DrawDeath(hud, ev);

        DrawCrosshair(hud, st, session);
        if (st.ClickHintVisible && !st.DeathVisible) DrawClickHint(hud, ev);

        DrawCursorItem(hud, session);
        DrawPrompt(hud, st, ev);
    }

    // ------------------------------------------------------------- in-world HUD

    /// <summary><c>#blackout</c>: full-screen black while the camera is inside a block.</summary>
    private static void DrawBlackout(HudRenderer hud, GameSession s)
    {
        var p = s.Player;
        bool inside = s.World.IsSolid(p.X, p.Y + p.EyeHeight, p.Z);
        if (!inside) return;
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 1));
    }

    /// <summary><c>#crosshair</c>: two white bars, 2x20 and 20x2, centred.</summary>
    private static void DrawCrosshair(HudRenderer hud, HudState st, GameSession s)
    {
        bool hidden = !st.PointerLocked || st.ClickHintVisible || st.DeathVisible ||
                      st.SleepVisible || st.AnyPanelOpen(s) || st.ChatOpen;
        if (hidden) return;

        float cx = hud.Width * 0.5f, cy = hud.Height * 0.5f;
        var c = White;
        hud.Fill(cx - 1f, cy - 10f, 2f, 20f, c);
        hud.Fill(cx - 10f, cy - 1f, 20f, 2f, c);
    }

    /// <summary><c>#hotbar</c>: bottom 20, centred, nine 50px slots with a 4px gap.</summary>
    private static void DrawHotbar(HudRenderer hud, HudState st, GameSession s, HudEvents ev)
    {
        float total = Inventory.HOTBAR * SLOT + (Inventory.HOTBAR - 1) * GAP;
        float x0 = (hud.Width - total) * 0.5f;
        float y0 = hud.Height - 20f - SLOT;

        for (int i = 0; i < Inventory.HOTBAR; i++)
        {
            float x = x0 + i * (SLOT + GAP);
            bool selected = i == s.Inventory.SelectedIndex;
            hud.Fill(x, y0, SLOT, SLOT, HotbarBg);
            hud.Border(x, y0, SLOT, SLOT, 3f, selected ? White : HotbarBorder);
            if (selected)
            {
                // box-shadow: 0 0 10px rgba(255,255,255,.6)
                hud.Border(x - 2f, y0 - 2f, SLOT + 4f, SLOT + 4f, 2f, new Vector4(1, 1, 1, 0.25f));
            }

            DrawSlotContents(hud, x, y0, s.Inventory.Slots[i], label: (i + 1).ToString());

            // The hotbar is clickable: digits select, clicking does too.
            int click = hud.Clicked($"hotbar:{i}", x, y0, SLOT, SLOT);
            if (click == 1) ev.SelectHotbar = i;
        }
    }

    /// <summary><c>#health-bar</c>: ten 20px hearts, bottom 150, centred, 2px gap.</summary>
    private static void DrawHealthBar(HudRenderer hud, HudState st, GameSession s)
    {
        DrawStatRow(hud, hud.Height - 150f - 20f, s.Player.Health, s.Player.MaxHealth,
                    HeartShape, Rgb(0xff, 0x2a, 0x2a), Rgb(0x44, 0x44, 0x44),
                    Rgb(0x8b, 0x00, 0x00), Rgb(0x22, 0x22, 0x22));
    }

    /// <summary><c>#hunger-bar</c>: ten 20px drumsticks, bottom 120, centred, 2px gap.</summary>
    private static void DrawHungerBar(HudRenderer hud, HudState st, GameSession s)
    {
        bool flash = st.EatingFlash > 0f;
        DrawStatRow(hud, hud.Height - 120f - 20f, s.Player.Hunger, s.Player.MaxHunger,
                    FoodShape,
                    flash ? Rgb(0x88, 0xff, 0x88) : Rgb(0xc0, 0x60, 0x30),
                    flash ? Rgb(0x44, 0xaa, 0x44) : Rgb(0x44, 0x44, 0x44),
                    flash ? Rgb(0x44, 0xff, 0x44) : Rgb(0x6b, 0x30, 0x10),
                    flash ? Rgb(0x22, 0x66, 0x22) : Rgb(0x22, 0x22, 0x22));
    }

    /// <summary>
    /// ui.js: <c>hp = value - i*2</c>, and the pip is empty when <c>hp &lt;= 0</c> — so there
    /// are no half icons, a pip with one point left still draws full.
    /// </summary>
    private static void DrawStatRow(HudRenderer hud, float y, float value, float max,
                                    Vector2[] shape, Vector4 full, Vector4 empty,
                                    Vector4 fullBorder, Vector4 emptyBorder)
    {
        int count = (int)Math.Ceiling(max / 2f);
        float total = count * 20f + (count - 1) * 2f;
        float x0 = (hud.Width - total) * 0.5f;

        Span<Vector2> pts = stackalloc Vector2[shape.Length];
        for (int i = 0; i < count; i++)
        {
            float x = x0 + i * 22f;
            bool isFull = value - i * 2f > 0f;
            for (int k = 0; k < shape.Length; k++) pts[k] = shape[k] + new Vector2(x, y);
            hud.Polygon(pts, isFull ? full : empty);

            // border: 1px — drawn as a thin outline fan between the shape and an inset copy.
            for (int k = 0; k < shape.Length; k++) pts[k] = shape[k] * (18f / 20f) + new Vector2(x + 1f, y + 1f);
            // Redraw the interior over the border strip so the outline reads as 1px.
            hud.Polygon(pts, isFull ? fullBorder : emptyBorder);
            for (int k = 0; k < shape.Length; k++) pts[k] = shape[k] * (16f / 20f) + new Vector2(x + 2f, y + 2f);
            hud.Polygon(pts, isFull ? full : empty);
        }
    }

    /// <summary><c>.eat-popup</c>: rises 40px and fades over 0.8s, bottom 155, centred.</summary>
    private static void DrawEatPopup(HudRenderer hud, HudState st)
    {
        if (st.EatPopup <= 0f) return;
        float t = 1f - Math.Clamp(st.EatPopup / 0.8f, 0f, 1f);   // 0 -> 1 over the animation
        float rise = t * 40f;
        float alpha = t < 0.2f ? t / 0.2f : 1f - (t - 0.2f) / 0.8f;
        float scale = t < 0.2f ? 0.5f + t / 0.2f * 0.7f : 1f;
        int fs = (int)Math.Round(22f / 8f * scale);
        string text = "+" + st.EatPopupValue;
        hud.ShadowText(hud.Width * 0.5f, hud.Height - 155f - rise, text, Math.Max(1, fs),
                       new Vector4(0.533f, 1f, 0.533f, Math.Clamp(alpha, 0f, 1f)),
                       TextAlign.Centre, 2f);
    }

    /// <summary><c>#info</c>: top-left status line, 18px, white, 2px shadow.</summary>
    private static void DrawInfo(HudRenderer hud, HudState st, GameSession s)
    {
        string line = st.SavedFlash > 0f
            ? "Saved!"
            : $"Beta 1.1.0 | Seed: {s.World.Seed} | X: {s.Player.X:F1} Y: {s.Player.Y:F1} Z: {s.Player.Z:F1} | Mobs: {s.Mobs.Mobs.Count}";
        hud.ShadowText(10f, 10f, line, 18f, White, TextAlign.Left, 2f);
    }

    /// <summary><c>#biome-hud</c>: four right-aligned lines, top-right, 20px.</summary>
    private static void DrawBiomeHud(HudRenderer hud, HudState st, GameSession s)
    {
        string[] biomeNames =
        {
            "Plains", "Forest", "Mountain", "Desert", "Snowy Plains", "Old Growth Forest",
        };
        int bi = s.World.Generator.GetBiome((int)Math.Floor(s.Player.X), (int)Math.Floor(s.Player.Z));
        string biome = bi >= 0 && bi < biomeNames.Length ? biomeNames[bi] : "?";

        float x = hud.Width - 10f;
        float y = 10f;
        float scale = 20f;
        float lineH = scale * 1.2f + 4f;

        hud.ShadowText(x, y, (st.MusicEnabled ? "Music: ON" : "Music: OFF"), scale,
                       st.MusicEnabled ? Rgb(0x88, 0xff, 0x88) : Rgb(0xff, 0x88, 0x88), TextAlign.Right, 2f);
        y += lineH;
        hud.ShadowText(x, y, $"Biome: {biome}{(s.Player.Flying ? " . FLYING" : "")}", scale, White, TextAlign.Right, 2f);
        y += lineH;
        hud.ShadowText(x, y, $"Mode: {(s.Player.Mode == Gamemode.Creative ? "Creative" : "Survival")}",
                       scale, White, TextAlign.Right, 2f);
        y += lineH;
        hud.ShadowText(x, y, $"Time: {s.DayNight.TimeOfDayName}", scale, White, TextAlign.Right, 2f);
    }

    /// <summary><c>#mining-bar-container</c>: bottom 180, 200x14, with a label above.</summary>
    private static void DrawMiningBar(HudRenderer hud, GameSession s)
    {
        if (!s.Mining.Breaking || s.Mining.BreakProgress <= 0f) return;
        float w = 200f, h = 14f;
        float x = (hud.Width - w) * 0.5f;
        float y = hud.Height - 180f - h;

        hud.ShadowText(x + w * 0.5f, y - 22f, "Mining...", 16f, White,
                       TextAlign.Centre, 1f);
        hud.Fill(x, y, w, h, new Vector4(0, 0, 0, 0.6f));
        hud.Border(x, y, w, h, 2f, White);
        float pct = Math.Clamp(s.Mining.BreakProgress, 0f, 1f);
        if (pct > 0f) hud.Fill(x + 2f, y + 2f, (w - 4f) * pct, h - 4f, Green);
    }

    /// <summary><c>#hint</c> and <c>#music-credit</c>: bottom-right, dimmed.</summary>
    private static void DrawHint(HudRenderer hud)
    {
        // style.css: #hint { bottom: 30px } — that is the bottom edge of the text box.
        float scale = 15f;
        hud.ShadowText(hud.Width - 10f, hud.Height - 30f - scale, "M to turn off BG music",
                       scale, new Vector4(1, 1, 1, 0.7f), TextAlign.Right, 1f);
    }

    private static void DrawMusicCredit(HudRenderer hud)
        => hud.ShadowText(hud.Width - 10f, hud.Height - 8f - 14f,
                          "The background music by Slainte",
                          14f, new Vector4(1, 1, 1, 0.6f), TextAlign.Right, 1f);

    /// <summary>Cuts <paramref name="s"/> with an ellipsis so it fits <paramref name="maxW"/>.</summary>
    private static string Truncate(HudRenderer hud, string s, float scale, float maxW)
    {
        if (hud.TextWidth(s, scale) <= maxW) return s;
        for (int i = s.Length - 1; i > 0; i--)
            if (hud.TextWidth(s[..i] + "...", scale) <= maxW) return s[..i] + "...";
        return "...";
    }

    // ------------------------------------------------------------------- slots

    /// <summary>
    /// One slot's contents: the 36x36 icon (<c>.slot-icon</c> / <c>.item-icon</c>), the
    /// stack count (<c>.slot-count</c>), the optional hotbar digit (<c>.slot-label</c>) and
    /// the tool durability bar (<c>.durability</c>).
    /// </summary>
    private static void DrawSlotContents(HudRenderer hud, float x, float y, ItemStack item,
                                         string? label = null, bool hovered = false)
    {
        if (label is not null)
            hud.ShadowText(x + 4f, y + 2f, label, 13f, White, TextAlign.Left, 1f);

        if (!item.IsEmpty)
        {
            float ix = x + (SLOT - ICON) * 0.5f;
            float iy = y + (SLOT - ICON) * 0.5f;
            DrawItemIcon(hud, ix, iy, ICON, item.Id);

            if (item.Count > 1)
                hud.ShadowText(x + SLOT - 4f, y + SLOT - 2f - 17f,
                               item.Count.ToString(), 17f, White, TextAlign.Right, 1f);

            DrawDurability(hud, x, y, item);
        }
        if (hovered) hud.Border(x, y, SLOT, SLOT, 2f, White);
    }

    /// <summary>
    /// <c>getIconHTML</c>: items use their own PNG, blocks use the atlas side tile. When no
    /// texture is available it falls back to the flat <c>getInfo(id).color</c> square.
    /// </summary>
    private static void DrawItemIcon(HudRenderer hud, float x, float y, float size, int id)
    {
        if (id <= 0) return;
        if (ItemId.IsItem(id))
        {
            if (hud.Textures.ItemTextures.TryGetValue(id, out uint itemTex))
                hud.Image(x, y, size, size, itemTex, hud.Textures.Full);
            else
                hud.Fill(x, y, size, size, Hex(ItemTable.GetInfo(id).Color));
            return;
        }

        ref readonly BlockTextures tex = ref BlockTextureMap.Get((byte)id);
        int slot = tex.Side >= 0 ? tex.Side : 0;
        hud.Image(x, y, size, size, hud.Textures.TerrainAtlas, hud.Textures.BlockTile(slot));
    }

    /// <summary><c>getDurabilityHTML</c>: 36x3 bar, green/orange/red by remaining fraction.</summary>
    private static void DrawDurability(HudRenderer hud, float x, float y, ItemStack item)
    {
        if (!ItemId.IsItem(item.Id)) return;
        if (!ItemTable.TryGet(item.Id, out ItemInfo info) || info.Durability <= 0) return;

        int cur = item.Durability != 0 ? item.Durability : info.Durability;
        float pct = Math.Clamp(cur / (float)info.Durability, 0f, 1f);
        Vector4 colour = pct > 0.5f ? Green : pct > 0.25f ? Rgb(0xff, 0xaa, 0x00) : Rgb(0xff, 0x33, 0x33);

        float bx = x + 4f, by = y + SLOT - 2f - 3f;
        hud.Fill(bx, by, 36f, 3f, new Vector4(0, 0, 0, 1));
        hud.Border(bx - 1f, by - 1f, 38f, 5f, 1f, Rgb(0x33, 0x33, 0x33));
        if (pct > 0f) hud.Fill(bx, by, 36f * pct, 3f, colour);
    }

    /// <summary>
    /// <c>#cursor-item</c>: the stack under the pointer while a container is open, 40x40.
    /// </summary>
    private static void DrawCursorItem(HudRenderer hud, GameSession s)
    {
        ItemStack c = s.Inventory.Cursor;
        if (c.IsEmpty) return;
        float x = hud.Mouse.X - 20f, y = hud.Mouse.Y - 20f;
        DrawItemIcon(hud, x, y, 40f, c.Id);
        if (c.Count > 1)
            hud.ShadowText(x + 40f, y + 40f - 17f, c.Count.ToString(),
                           17f, White, TextAlign.Right, 1f);
    }

    /// <summary><c>#tooltip</c>: name + detail box that follows the pointer.</summary>
    private static void DrawTooltip(HudRenderer hud, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string[] lines = text.Split('\n');
        float scale = 15f;
        float lineH = scale * 1.2f + 2f;
        float w = 0f;
        foreach (string l in lines) w = Math.Max(w, hud.TextWidth(l, scale));
        w += 20f;
        float h = lines.Length * lineH + 12f;

        float x = Math.Min(hud.Mouse.X + 14f, hud.Width - w - 4f);
        float y = Math.Min(hud.Mouse.Y + 18f, hud.Height - h - 4f);

        hud.Fill(x, y, w, h, new Vector4(16 / 255f, 0f, 16 / 255f, 0.94f));
        hud.Border(x, y, w, h, 2f, Rgb(0x2a, 0x00, 0x44));
        for (int i = 0; i < lines.Length; i++)
            hud.ShadowText(x + 10f, y + 6f + i * lineH, lines[i], scale, White, TextAlign.Left, 1f);
    }

    private static string Describe(ItemStack item)
    {
        if (item.IsEmpty) return "";
        var sb = new StringBuilder();
        if (ItemId.IsItem(item.Id))
        {
            sb.Append(ItemTable.GetInfo(item.Id).Name);
            if (ItemTable.TryGet(item.Id, out ItemInfo info))
            {
                if (info.Durability > 0)
                {
                    int cur = item.Durability != 0 ? item.Durability : info.Durability;
                    sb.Append('\n').Append("Durability: ").Append(cur).Append('/').Append(info.Durability);
                }
                if (info.Food > 0) sb.Append('\n').Append("Food: +").Append(info.Food);
                if (info.Fuel > 0) sb.Append('\n').Append("Fuel: ").Append(info.Fuel);
            }
        }
        else
        {
            sb.Append(BlockInfoTable.Get((byte)item.Id).Name);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------- containers

    /// <summary>
    /// <c>#inv-screen</c>: the dimmed overlay, the grey panel with the 2x2 crafting grid and
    /// output, the 3x9 storage grid, the hotbar row, and — in creative — the block palette
    /// and trash slot beside it.
    /// </summary>
    private static void DrawInventory(HudRenderer hud, HudState st, GameSession s, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 0.7f));

        Inventory inv = s.Inventory;
        CraftingSystem craft = s.Crafting;
        bool creative = s.Player.Mode == Gamemode.Creative;

        int gridCols = craft.Size;                        // 2 in the inventory, 3 at a table
        int gridRows = craft.Grid.Length / gridCols;
        float gridW = gridCols * SLOT + (gridCols - 1) * GAP;
        float gridH = gridRows * SLOT + (gridRows - 1) * GAP;
        float scale = 26f;
        float labelScale = 18f;
        float hintScale = 15f;

        // CSS box model: #inv-title mb10, .inv-label mt10 mb6, #crafting-area mb14 gap14,
        // #inv-hint mt10 - measured with the real font, not guessed bitmap heights.
        float arrowW = hud.TextWidth("->", 30f);
        float leftW = Math.Max(9f * SLOT + 8f * GAP, gridW + 14f + arrowW + 14f + SLOT);
        float titleH = hud.TextHeight(scale) + 10f;
        float craftH = gridH + 14f;
        float labelH = 10f + hud.TextHeight(labelScale) + 6f;
        float storageH = 3f * SLOT + 2f * GAP;
        float hintH = 10f + hud.TextHeight(hintScale);
        float leftH = titleH + craftH + labelH + storageH + labelH + SLOT + hintH;

        float creativeW = 0f;
        if (creative) creativeW = 20f + (5f * SLOT + 4f * GAP) + 20f + 6f;   // padding + grid + padding + border

        float panelW = 40f + 8f + leftW + (creative ? 20f + creativeW : 0f);
        float panelH = 40f + 8f + Math.Max(leftH, creative ? 480f : 0f);
        float px = (hud.Width - panelW) * 0.5f;
        float py = (hud.Height - panelH) * 0.5f;

        hud.Fill(px, py, panelW, panelH, PanelBg);
        hud.Border(px, py, panelW, panelH, 4f, PanelBorder);

        float cx = px + 24f;
        float cy = py + 24f;

        // ---- #inv-title ----
        hud.Text(cx + leftW * 0.5f, cy, craft.TableOpen ? "Crafting" : "Inventory", scale, DarkText, TextAlign.Centre);
        cy += titleH;

        // ---- #crafting-area: grid, arrow, output ----
        float arrowScale = 30f;
        float areaY = cy;
        for (int i = 0; i < craft.Grid.Length; i++)
        {
            int gx = i % gridCols, gy = i / gridCols;
            float sx = cx + gx * (SLOT + GAP);
            float sy = areaY + gy * (SLOT + GAP);
            int gi = i;
            DrawContainerSlot(hud, sx, sy, craft.Grid[i], $"craft:{i}", ev,
                              (_, right, shift) => ev.CraftClick = (gi, right, shift));
        }
        float arrowX = cx + gridW + 14f;
        hud.Text(arrowX + arrowW * 0.5f, areaY + gridH * 0.5f - hud.TextHeight(arrowScale) * 0.5f,
                 "->", arrowScale, Rgb(0x55, 0x55, 0x55), TextAlign.Centre);
        float outX = arrowX + arrowW + 14f;
        ItemStack? output = craft.CurrentRecipe() is CraftRecipe r
            ? new ItemStack { Id = r.ResultId, Count = r.ResultCount }
            : null;
        DrawContainerSlot(hud, outX, areaY, output ?? ItemStack.Empty, "craft:out", ev,
                          (_, right, shift) => ev.CraftOutputClick = (right, shift));
        cy += craftH;

        // ---- Storage ----
        hud.Text(cx, cy, "Storage", labelScale, DarkText);
        cy += labelH;
        for (int i = Inventory.HOTBAR; i < Inventory.SIZE; i++)
        {
            int col = (i - Inventory.HOTBAR) % 9, row = (i - Inventory.HOTBAR) / 9;
            int si = i;
            DrawContainerSlot(hud, cx + col * (SLOT + GAP), cy + row * (SLOT + GAP), inv.Slots[i],
                              $"inv:{i}", ev, (_, right, shift) => ev.InvClick = (si, right, shift));
        }
        cy += storageH;

        // ---- Hotbar ----
        hud.Text(cx, cy, "Hotbar", labelScale, DarkText);
        cy += labelH;
        for (int i = 0; i < Inventory.HOTBAR; i++)
        {
            int hb = i;
            DrawContainerSlot(hud, cx + i * (SLOT + GAP), cy, inv.Slots[i], $"inv:{i}", ev,
                              (_, right, shift) => ev.InvClick = (hb, right, shift));
        }
        cy += SLOT;

        hud.Text(cx + leftW * 0.5f, cy,
                 Truncate(hud, "LMB: pick up . RMB: split . E to close", hintScale, leftW),
                 hintScale, LabelText, TextAlign.Centre);

        if (creative) DrawCreativePanel(hud, px + 24f + leftW + 20f, py + 24f, creativeW, panelH - 48f, s, ev);

        string? hovered = hud.HoveredWidget;
        if (hovered is not null && TrySlotItem(hovered, s, craft, out ItemStack hi))
            DrawTooltip(hud, Describe(hi));
    }

    /// <summary><c>#creative-panel</c>: five-column palette of every giveable id plus a trash slot.</summary>
    private static void DrawCreativePanel(HudRenderer hud, float x, float y, float w, float h,
                                          GameSession s, HudEvents ev)
    {
        hud.Fill(x, y, w, h, Rgb(0xa0, 0xa0, 0xa0));
        hud.Border(x, y, w, h, 3f, Rgb(0x55, 0x55, 0x55));

        float titleScale = 18f;
        float hintScale = 14f;
        float ix = x + 13f;
        float iy = y + 13f;

        hud.Text(x + w * 0.5f, iy, "Creative Blocks", titleScale, DarkText, TextAlign.Centre);
        iy += titleScale + 8f;
        hud.Text(x + w * 0.5f, iy, "LMB: +1 . RMB: +64", hintScale, LabelText, TextAlign.Centre);
        iy += hintScale + 6f;

        var items = CreativeItems.List;
        for (int i = 0; i < items.Length; i++)
        {
            int col = i % 5, row = i / 5;
            float sx = ix + col * (SLOT + GAP);
            float sy = iy + row * (SLOT + GAP);
            if (sy + SLOT > y + h - 70f) continue;

            hud.Fill(sx, sy, SLOT, SLOT, SlotBg);
            hud.Border(sx, sy, SLOT, SLOT, 2f, SlotBorder);
            DrawItemIcon(hud, sx + (SLOT - ICON) * 0.5f, sy + (SLOT - ICON) * 0.5f, ICON, items[i]);
            if (hud.IsHovered($"creative:{items[i]}")) hud.Border(sx, sy, SLOT, SLOT, 2f, White);

            int click = hud.Clicked($"creative:{items[i]}", sx, sy, SLOT, SLOT);
            if (click == 1) ev.CreativeGive = (items[i], 1);
            else if (click == 2) ev.CreativeGive = (items[i], 64);

            if (hud.HoveredWidget == $"creative:{items[i]}")
                DrawTooltip(hud, ItemId.IsItem(items[i])
                    ? ItemTable.GetInfo(items[i]).Name
                    : BlockInfoTable.Get((byte)items[i]).Name);
        }

        // #trash-slot: 50x50, dark red with a dashed light-red border, centred below the grid.
        float rows = (float)Math.Ceiling(items.Length / 5f);
        float ty = Math.Min(iy + rows * (SLOT + GAP) + 10f, y + h - 60f);
        float tx = x + (w - SLOT) * 0.5f;
        hud.Fill(tx, ty, SLOT, SLOT, Rgb(0x8b, 0x20, 0x20));
        DashedBorder(hud, tx, ty, SLOT, SLOT, 2f, Rgb(0xff, 0x80, 0x80));
        if (hud.Clicked("trash", tx, ty, SLOT, SLOT) == 1) ev.TrashClick = true;
    }

    private static void DashedBorder(HudRenderer hud, float x, float y, float w, float h, float t, Vector4 c)
    {
        const float dash = 6f, gapLen = 4f;
        for (float d = 0f; d < w; d += dash + gapLen)
        {
            float len = Math.Min(dash, w - d);
            hud.Fill(x + d, y, len, t, c);
            hud.Fill(x + d, y + h - t, len, t, c);
        }
        for (float d = 0f; d < h; d += dash + gapLen)
        {
            float len = Math.Min(dash, h - d);
            hud.Fill(x, y + d, t, len, c);
            hud.Fill(x + w - t, y + d, t, len, c);
        }
    }

    /// <summary>An <c>.inv-slot</c>: grey box, 2px border, white on hover.</summary>
    private static void DrawContainerSlot(HudRenderer hud, float x, float y, ItemStack item,
                                          string id, HudEvents ev,
                                          Action<string, bool, bool> onClick)
    {
        hud.Fill(x, y, SLOT, SLOT, SlotBg);
        hud.Border(x, y, SLOT, SLOT, 2f, SlotBorder);
        bool hovered = hud.IsHovered(id);
        DrawSlotContents(hud, x, y, item, null, hovered);

        int click = hud.Clicked(id, x, y, SLOT, SLOT);
        if (click != 0) onClick(id, click == 2, hud.ShiftHeld);
    }

    private static bool TrySlotItem(string widget, GameSession s, CraftingSystem craft, out ItemStack item)
    {
        item = ItemStack.Empty;
        if (widget.StartsWith("inv:", StringComparison.Ordinal) &&
            int.TryParse(widget.AsSpan(4), out int idx) && idx >= 0 && idx < Inventory.SIZE)
        {
            item = s.Inventory.Slots[idx];
            return true;
        }
        if (widget.StartsWith("craft:", StringComparison.Ordinal))
        {
            if (widget == "craft:out")
            {
                if (craft.CurrentRecipe() is CraftRecipe r)
                {
                    item = new ItemStack { Id = r.ResultId, Count = r.ResultCount };
                    return true;
                }
                return false;
            }
            if (int.TryParse(widget.AsSpan(6), out int gi) && gi >= 0 && gi < craft.Grid.Length)
            {
                item = craft.Grid[gi];
                return true;
            }
        }
        if (widget.StartsWith("creative:", StringComparison.Ordinal) &&
            int.TryParse(widget.AsSpan(9), out int cid))
        {
            item = new ItemStack { Id = cid, Count = 1 };
            return true;
        }
        return false;
    }

    /// <summary><c>#furnace-ui</c>: input, flame, fuel with its gauge, progress bar, output.</summary>
    private static void DrawFurnace(HudRenderer hud, GameSession s, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 0.7f));
        if (s.Furnaces.OpenKey is not BlockPos key) return;
        FurnaceState st = s.Furnaces.GetState(key.X, key.Y, key.Z);

        float titleScale = 26f;
        float labelScale = 18f;
        float labelH = 10f + hud.TextHeight(labelScale) + 6f;

        float mainW = 48f + SLOT + 20f + 90f + 20f + SLOT + 4f;
        float mainH = 32f + SLOT + 26f + 4f;
        float panelW = 48f + Math.Max(mainW, 9f * SLOT + 8f * GAP);
        float panelH = 48f + hud.TextHeight(titleScale) + 10f + mainH + 14f + labelH + 3f * SLOT + 2f * GAP + 10f + labelH + SLOT;
        float px = (hud.Width - panelW) * 0.5f;
        float py = (hud.Height - panelH) * 0.5f;

        hud.Fill(px, py, panelW, panelH, PanelBg);
        hud.Border(px, py, panelW, panelH, 4f, PanelBorder);

        float cx = px + 24f;
        float cy = py + 24f;
        hud.Text(px + panelW * 0.5f, cy, "Furnace", titleScale, DarkText, TextAlign.Centre);
        cy += hud.TextHeight(titleScale) + 10f;

        // ---- #furnace-main ----
        float mx = px + (panelW - mainW) * 0.5f;
        hud.Fill(mx, cy, mainW, mainH, SlotBg);
        hud.Border(mx, cy, mainW, mainH, 2f, SlotBorder);

        float colX = mx + 24f;
        float inputY = cy + 16f;
        DrawFurnaceSlot(hud, colX, inputY, st.Input, "furn:0", ev);

        // #furnace-flames: greyscale and dim when cold, lit when there is fuel time left.
        bool lit = st.FuelTime > 0f;
        float flameY = inputY + SLOT + 4f;
        hud.Fill(colX + 15f, flameY + 4f, 20f, 16f,
                 lit ? Rgb(0xff, 0x88, 0x00) : Rgb(0x60, 0x60, 0x60, 0.4f));

        float fuelY = flameY + 24f;
        DrawFurnaceSlot(hud, colX, fuelY, st.Fuel, "furn:1", ev);

        // #furnace-fuel-bar sits 26px above the fuel slot: 20x20, filling from the bottom.
        float barX = colX, barY = fuelY - 26f;
        hud.Fill(barX, barY, 20f, 20f, Rgb(0x55, 0x55, 0x55));
        hud.Border(barX, barY, 20f, 20f, 2f, Rgb(0x33, 0x33, 0x33));
        float fuelPct = Math.Clamp(st.FuelTime / 20f, 0f, 1f);
        if (fuelPct > 0f)
            hud.Fill(barX + 2f, barY + 2f + (16f * (1f - fuelPct)), 16f, 16f * fuelPct, Rgb(0xff, 0x88, 0x00));

        // ---- #furnace-center: progress bar + arrow ----
        float centX = colX + SLOT + 20f + 10f;
        float progY = cy + mainH * 0.5f - 20f;
        hud.Fill(centX, progY, 70f, 16f, Rgb(0x55, 0x55, 0x55));
        hud.Border(centX, progY, 70f, 16f, 2f, Rgb(0x33, 0x33, 0x33));
        float totalTime = SmeltingTable.TryGet(st.Input.Id, out SmeltRecipe rec) ? rec.Time : 10f;
        float prog = Math.Clamp(st.Progress / totalTime, 0f, 1f);
        if (prog > 0f) hud.Fill(centX + 2f, progY + 2f, 66f * prog, 12f, Green);
        hud.Text(centX + 35f, progY + 22f, "->", 24f, Rgb(0x55, 0x55, 0x55), TextAlign.Centre);

        float outX = centX + 90f;
        DrawFurnaceSlot(hud, outX, inputY, st.Output, "furn:2", ev);

        cy += mainH + 14f;

        // ---- inventory + hotbar ----
        hud.Text(cx, cy, "Inventory", labelScale, DarkText);
        cy += labelH;
        for (int i = Inventory.HOTBAR; i < Inventory.SIZE; i++)
        {
            int col = (i - Inventory.HOTBAR) % 9, row = (i - Inventory.HOTBAR) / 9;
            int fi = i;
            DrawContainerSlot(hud, cx + col * (SLOT + GAP), cy + row * (SLOT + GAP), s.Inventory.Slots[i],
                              $"inv:{i}", ev, (_, right, shift) => ev.InvClick = (fi, right, shift));
        }
        cy += 3f * SLOT + 2f * GAP + 10f;
        hud.Text(cx, cy, "Hotbar", labelScale, DarkText);
        cy += labelH;
        for (int i = 0; i < Inventory.HOTBAR; i++)
        {
            int fh = i;
            DrawContainerSlot(hud, cx + i * (SLOT + GAP), cy, s.Inventory.Slots[i], $"inv:{i}", ev,
                              (_, right, shift) => ev.InvClick = (fh, right, shift));
        }

        DrawFurnaceTooltip(hud, s, st);
    }

    private static void DrawFurnaceSlot(HudRenderer hud, float x, float y, ItemStack item,
                                        string id, HudEvents ev)
    {
        DrawContainerSlot(hud, x, y, item, id, ev,
                          (wid, right, shift) =>
                          {
                              if (int.TryParse(wid.AsSpan(5), out int slot))
                                  ev.FurnaceClick = (slot, right, shift);
                          });
    }

    private static void DrawFurnaceTooltip(HudRenderer hud, GameSession s, FurnaceState st)
    {
        string? w = hud.HoveredWidget;
        if (w is null) return;
        ItemStack item = w switch
        {
            "furn:0" => st.Input,
            "furn:1" => st.Fuel,
            "furn:2" => st.Output,
            _ => ItemStack.Empty,
        };
        if (w.StartsWith("inv:", StringComparison.Ordinal) &&
            int.TryParse(w.AsSpan(4), out int idx) && idx >= 0 && idx < Inventory.SIZE)
            item = s.Inventory.Slots[idx];
        if (!item.IsEmpty) DrawTooltip(hud, Describe(item));
    }

    /// <summary><c>#chest-ui</c>: 27 storage cells above the player's inventory and hotbar.</summary>
    private static void DrawChest(HudRenderer hud, GameSession s, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 0.7f));
        if (s.Chests.OpenKey is not BlockPos key) return;
        ItemStack[] chest = s.Chests.GetState(key.X, key.Y, key.Z);

        float titleScale = 26f;
        float labelScale = 18f;
        float labelH = 10f + hud.TextHeight(labelScale) + 6f;
        float gridW = 9f * SLOT + 8f * GAP;
        float panelW = 48f + gridW;
        float panelH = 48f + hud.TextHeight(titleScale) + 10f + labelH + 3f * SLOT + 2f * GAP + 10f
                     + labelH + 3f * SLOT + 2f * GAP + 10f + labelH + SLOT;
        float px = (hud.Width - panelW) * 0.5f;
        float py = (hud.Height - panelH) * 0.5f;

        hud.Fill(px, py, panelW, panelH, PanelBg);
        hud.Border(px, py, panelW, panelH, 4f, PanelBorder);

        float cx = px + 24f;
        float cy = py + 24f;
        hud.Text(px + panelW * 0.5f, cy, "Chest", titleScale, DarkText, TextAlign.Centre);
        cy += hud.TextHeight(titleScale) + 10f;

        hud.Text(cx, cy, "Storage", labelScale, DarkText);
        cy += labelH;
        for (int i = 0; i < GameConfig.CHEST_SLOTS && i < chest.Length; i++)
        {
            int col = i % 9, row = i / 9;
            int captured = i;
            DrawContainerSlot(hud, cx + col * (SLOT + GAP), cy + row * (SLOT + GAP), chest[i],
                              $"chest:{i}", ev,
                              (_, right, shift) => ev.ChestClick = (captured, right, shift));
        }
        cy += 3f * SLOT + 2f * GAP + 10f;

        hud.Text(cx, cy, "Inventory", labelScale, DarkText);
        cy += labelH;
        for (int i = Inventory.HOTBAR; i < Inventory.SIZE; i++)
        {
            int col = (i - Inventory.HOTBAR) % 9, row = (i - Inventory.HOTBAR) / 9;
            int fi = i;
            DrawContainerSlot(hud, cx + col * (SLOT + GAP), cy + row * (SLOT + GAP), s.Inventory.Slots[i],
                              $"inv:{i}", ev, (_, right, shift) => ev.InvClick = (fi, right, shift));
        }
        cy += 3f * SLOT + 2f * GAP + 10f;

        hud.Text(cx, cy, "Hotbar", labelScale, DarkText);
        cy += labelH;
        for (int i = 0; i < Inventory.HOTBAR; i++)
        {
            int fh = i;
            DrawContainerSlot(hud, cx + i * (SLOT + GAP), cy, s.Inventory.Slots[i], $"inv:{i}", ev,
                              (_, right, shift) => ev.InvClick = (fh, right, shift));
        }

        string? w = hud.HoveredWidget;
        if (w is not null)
        {
            ItemStack item = ItemStack.Empty;
            if (w.StartsWith("chest:", StringComparison.Ordinal) &&
                int.TryParse(w.AsSpan(6), out int ci) && ci >= 0 && ci < chest.Length)
                item = chest[ci];
            else if (w.StartsWith("inv:", StringComparison.Ordinal) &&
                     int.TryParse(w.AsSpan(4), out int idx) && idx >= 0 && idx < Inventory.SIZE)
                item = s.Inventory.Slots[idx];
            if (!item.IsEmpty) DrawTooltip(hud, Describe(item));
        }
    }

    // -------------------------------------------------------------------- chat

    /// <summary>
    /// <c>#chat-wrapper</c>: bottom-left, 480 wide, 13px monospace. The log fades out when
    /// chat is closed; the input row and autocomplete list only exist while it is open.
    /// </summary>
    private static void DrawChat(HudRenderer hud, HudState st, GameSession s, HudEvents ev)
    {
        float scale = 13f;
        float lineH = scale * 1.2f + 3f;
        float x = 12f;
        float width = Math.Min(480f, hud.Width * 0.55f);

        IReadOnlyList<string> lines = s.Chat.Lines;
        int maxLines = (int)Math.Floor(200f / lineH);
        int start = Math.Max(0, lines.Count - maxLines);

        float bottomY = hud.Height - 12f;
        if (st.ChatOpen) bottomY -= 34f;   // room for the input row

        float y = bottomY - (lines.Count - start) * lineH;
        float alpha = st.ChatOpen ? 1f : 0.85f;
        for (int i = start; i < lines.Count; i++)
        {
            string line = lines[i];
            Vector4 colour = ChatColour(line, alpha);
            foreach (string wrapped in hud.Wrap(line, scale, width))
            {
                hud.Fill(x - 2f, y - 1f, Math.Min(width, hud.TextWidth(wrapped, scale) + 6f), lineH,
                         new Vector4(0, 0, 0, 0.35f * alpha));
                hud.Text(x, y, wrapped, scale, colour);
                y += lineH;
            }
        }

        if (!st.ChatOpen) return;

        // Autocomplete suggestions for a leading '/'.
        if (st.ChatBuffer.StartsWith('/') && !st.ChatBuffer.Contains(' '))
        {
            string prefix = st.ChatBuffer[1..];
            var matches = new List<string>();
            foreach (string cmd in CommandNames)
                if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matches.Add("/" + cmd);
            if (matches.Count > 0 && matches.Count <= 8)
            {
                float ah = matches.Count * lineH + 6f;
                float ay = bottomY - 6f - ah;
                hud.Fill(x, ay, width, ah, new Vector4(0, 0, 0, 0.9f));
                hud.Border(x, ay, width, ah, 1f, Rgb(0x33, 0x33, 0x33));
                for (int i = 0; i < matches.Count; i++)
                    hud.Text(x + 6f, ay + 3f + i * lineH, matches[i], scale, Rgb(0xcc, 0xff, 0xcc));
            }
        }

        // #chat-input
        float iy = bottomY - 30f;
        hud.Fill(x, iy, width, 30f, new Vector4(0, 0, 0, 0.85f));
        hud.Border(x, iy, width, 30f, 1f, Rgb(0x55, 0x55, 0x55));
        string shown = st.ChatBuffer.Length == 0 ? "/ for commands . T to chat" : st.ChatBuffer;
        Vector4 tc = st.ChatBuffer.Length == 0 ? Rgb(0x88, 0x88, 0x88) : White;
        hud.Text(x + 10f, iy + 8f, shown, scale, tc);

        // Caret.
        float caretX = x + 10f + hud.TextWidth(st.ChatBuffer[..Math.Clamp(st.Caret, 0, st.ChatBuffer.Length)], scale);
        hud.Fill(caretX, iy + 6f, 1f, 18f, White);

        hud.Widget("chat-input", x, iy, width, 30f);
        _ = ev;
    }

    private static readonly string[] CommandNames = { "time", "locate", "tp", "help" };

    private static Vector4 ChatColour(string line, float alpha)
    {
        if (line.StartsWith("> ", StringComparison.Ordinal)) return new Vector4(0.667f, 0.667f, 1f, alpha);
        if (line.StartsWith("!", StringComparison.Ordinal) ||
            line.Contains("Unknown command", StringComparison.Ordinal) ||
            line.Contains("Usage:", StringComparison.Ordinal)) return new Vector4(1f, 0.533f, 0.533f, alpha);
        return new Vector4(0.8f, 1f, 0.8f, alpha);
    }

    // ------------------------------------------------------------------- menus

    /// <summary><c>#click-hint</c>: the centred "Click to play" card with a settings button.</summary>
    private static void DrawClickHint(HudRenderer hud, HudEvents ev)
    {
        // style.css: #click-hint { padding: 20px 40px } h2 34px, p 18px. The DOM card is
        // shrink-to-fit, so size the panel from the widest line we actually draw.
        float h2 = 34f;
        float body = 18f;
        const string line1 = "WASD . SPACE jump . double-SPACE fly . SHIFT sneak . CTRL sprint";
        const string line2 = "LMB break . RMB place/use . U gamemode . E inventory . Ctrl+S save";
        float textW = Math.Max(hud.TextWidth(line1, body), hud.TextWidth(line2, body));
        textW = Math.Max(textW, hud.TextWidth("Click to play", h2));
        float w = Math.Min(hud.Width - 24f, textW + 80f);
        float h = h2 * 1.2f + 20f + 2f * (body * 1.2f + 8f) + 60f + 40f;
        float x = (hud.Width - w) * 0.5f, y = (hud.Height - h) * 0.5f;

        hud.Fill(x, y, w, h, new Vector4(0, 0, 0, 0.7f));
        hud.Border(x, y, w, h, 3f, White);

        float cy = y + 20f;
        hud.ShadowText(x + w * 0.5f, cy, "Click to play", h2, White, TextAlign.Centre, 2f);
        cy += h2 * 1.2f + 20f;
        hud.ShadowText(x + w * 0.5f, cy, line1, body, new Vector4(1, 1, 1, 0.8f), TextAlign.Centre, 2f);
        cy += body * 1.2f + 8f;
        hud.ShadowText(x + w * 0.5f, cy, line2, body, new Vector4(1, 1, 1, 0.8f), TextAlign.Centre, 2f);
        cy += body * 1.2f + 20f;

        float bw = 200f, bh = 48f;
        float bx = x + (w - bw) * 0.5f;
        ModeButton(hud, "btn-settings", bx, cy, bw, bh, "Settings", 22f);
        if (hud.Clicked("btn-settings", bx, cy, bw, bh) == 1) ev.OpenSettingsFromHint = true;
        if (hud.Clicked("click-hint", x, y, w, h) == 1) ev.RequestPointerLock = true;
    }

    /// <summary><c>#gamemode-menu</c>.</summary>
    private static void DrawGamemodeMenu(HudRenderer hud, HudEvents ev)
    {
        float h2 = 34f;
        float w = 340f;
        float h = 30f + h2 * 1.2f + 10f + 3f * (52f + 10f) + 30f;
        float x = (hud.Width - w) * 0.5f, y = (hud.Height - h) * 0.5f;

        hud.Fill(x, y, w, h, new Vector4(20 / 255f, 20 / 255f, 30 / 255f, 0.95f));
        hud.Border(x, y, w, h, 3f, White);
        hud.Text(x + w * 0.5f, y + 30f, "Choose Gamemode", h2, White, TextAlign.Centre);

        float by = y + 30f + h2 * 1.2f + 10f;
        by = ModeButtonRow(hud, "btn-creative", x, by, w, "Creative", ev, out bool creative);
        by = ModeButtonRow(hud, "btn-survival", x, by, w, "Survival", ev, out bool survival);
        ModeButtonRow(hud, "btn-close-mode", x, by, w, "Close", ev, out bool close);

        if (creative) ev.SetCreative = true;
        if (survival) ev.SetSurvival = true;
        if (close) ev.CloseGamemodeMenu = true;
    }

    private static float ModeButtonRow(HudRenderer hud, string id, float panelX, float y, float panelW,
                                       string label, HudEvents ev, out bool clicked)
    {
        float bw = 240f, bh = 52f;
        float bx = panelX + (panelW - bw) * 0.5f;
        ModeButton(hud, id, bx, y, bw, bh, label, 22f);
        clicked = hud.Clicked(id, bx, y, bw, bh) == 1;
        return y + bh + 10f;
    }

    private static void ModeButton(HudRenderer hud, string id, float x, float y, float w, float h,
                                   string label, float cssPx)
    {
        bool hovered = hud.IsHovered(id);
        hud.Fill(x, y, w, h, hovered ? Rgb(0x5a, 0x5a, 0x5a) : Rgb(0x44, 0x44, 0x44));
        hud.Border(x, y, w, h, 2f, hovered ? White : Rgb(0x88, 0x88, 0x88));
        hud.Text(x + w * 0.5f, y + (h - cssPx) * 0.5f, label,
                 cssPx, White, TextAlign.Centre);
    }

    /// <summary><c>#settings-menu</c>: biome-blend slider, close, save-and-quit.</summary>
    private static void DrawSettings(HudRenderer hud, HudState st, HudEvents ev)
    {
        float h2 = 34f;
        float w = 420f;
        float h = 30f + h2 * 1.2f + 20f + 40f + 30f + 2f * (52f + 10f) + 30f;
        float x = (hud.Width - w) * 0.5f, y = (hud.Height - h) * 0.5f;

        hud.Fill(x, y, w, h, new Vector4(20 / 255f, 20 / 255f, 30 / 255f, 0.95f));
        hud.Border(x, y, w, h, 3f, White);
        hud.Text(x + w * 0.5f, y + 30f, "Settings", h2, White, TextAlign.Centre);

        float cy = y + 30f + h2 * 1.2f + 20f;
        float labelScale = 20f;
        hud.Text(x + 30f, cy, $"Biome blending: {st.BlendValue} blocks", labelScale, White);
        cy += labelScale + 12f;

        // input[type=range], 0..16.
        float sx = x + 30f, sw = w - 60f, sh = 12f;
        hud.Fill(sx, cy, sw, sh, Rgb(0x33, 0x33, 0x33));
        float knobX = sx + sw * (st.BlendValue / 16f);
        hud.Fill(knobX - 7f, cy - 4f, 14f, sh + 8f, Rgb(0xcc, 0xcc, 0xcc));
        hud.Widget("blend-slider", sx, cy - 6f, sw, sh + 12f);
        if (hud.LeftDown && hud.HoveredWidget == "blend-slider")
        {
            int v = (int)Math.Round((hud.Mouse.X - sx) / sw * 16f);
            v = Math.Clamp(v, 0, 16);
            if (v != st.BlendValue) { st.BlendValue = v; ev.SetBlend = v; }
        }
        cy += sh + 24f;

        cy = SettingsButton(hud, "btn-close-settings", x, cy, w, "Close", out bool close);
        SettingsButton(hud, "btn-save-quit", x, cy, w, "Save and Quit to Menu", out bool quit);
        if (close) ev.CloseSettings = true;
        if (quit) ev.SaveAndQuit = true;
    }

    private static float SettingsButton(HudRenderer hud, string id, float panelX, float y, float panelW,
                                        string label, out bool clicked)
    {
        float bw = panelW - 60f, bh = 44f;
        float bx = panelX + 30f;
        ModeButton(hud, id, bx, y, bw, bh, label, 20f);
        clicked = hud.Clicked(id, bx, y, bw, bh) == 1;
        return y + bh + 10f;
    }

    /// <summary><c>#death-screen</c>.</summary>
    private static void DrawDeath(HudRenderer hud, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(80 / 255f, 0f, 0f, 0.75f));

        float h1 = 58f;
        float score = 30f;
        float cy = hud.Height * 0.5f - 160f;

        hud.ShadowText(hud.Width * 0.5f, cy, "You died!", h1, Rgb(0xff, 0xcc, 0x44), TextAlign.Centre, 3f);
        cy += h1 * 1.2f + 20f;
        // ui.js always reports "Score: 0"; kept as-is rather than inventing a formula.
        hud.ShadowText(hud.Width * 0.5f, cy, "Score: 0", score, White, TextAlign.Centre, 2f);
        cy += score * 1.2f + 40f;

        cy = DeathButton(hud, "btn-respawn", cy, "Respawn", out bool respawn);
        DeathButton(hud, "btn-title", cy, "Title screen", out bool title);
        if (respawn) ev.Respawn = true;
        if (title) ev.BackToTitle = true;
    }

    private static float DeathButton(HudRenderer hud, string id, float y, string label, out bool clicked)
    {
        float bw = 340f, bh = 56f;
        float bx = (hud.Width - bw) * 0.5f;
        bool hovered = hud.IsHovered(id);
        hud.Fill(bx, y, bw, bh, hovered ? Rgb(0x8a, 0x8a, 0x8a) : Rgb(0x7a, 0x7a, 0x7a));
        hud.Border(bx, y, bw, bh, 3f, Rgb(0x55, 0x55, 0x55));
        hud.ShadowText(bx + bw * 0.5f, y + (bh - 24f) * 0.5f, label,
                       24f, White, TextAlign.Centre, 2f);
        clicked = hud.Clicked(id, bx, y, bw, bh) == 1;
        return y + bh + 8f;
    }

    /// <summary><c>#sleep-ui</c>: black screen with a "Leave Bed" button near the bottom.</summary>
    private static void DrawSleep(HudRenderer hud, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 1));
        float bw = Math.Min(520f, hud.Width * 0.8f), bh = 56f;
        float bx = (hud.Width - bw) * 0.5f;
        float by = hud.Height - 140f - bh;
        bool hovered = hud.IsHovered("sleep-leave-btn");
        hud.Fill(bx, by, bw, bh, hovered ? Rgb(0x8a, 0x8a, 0x8a) : Rgb(0x7a, 0x7a, 0x7a));
        hud.Border(bx, by, bw, bh, 3f, Rgb(0x55, 0x55, 0x55));
        hud.ShadowText(bx + bw * 0.5f, by + (bh - 24f) * 0.5f, "Leave Bed",
                       24f, White, TextAlign.Centre, 2f);
        if (hud.Clicked("sleep-leave-btn", bx, by, bw, bh) == 1) ev.WakeUp = true;
    }

    // ------------------------------------------------------------------- title

    /// <summary><c>#startup-menu</c>: world list plus the new-world button.</summary>
    private static void DrawTitle(HudRenderer hud, HudState st, SaveSystem saves, HudEvents ev)
    {
        hud.Fill(0, 0, hud.Width, hud.Height, Rgb(0x1a, 0x1a, 0x2e));

        // style.css: #startup-menu h1 { font-size: 58px; color: #ffcc44; text-shadow: 3px 3px 0 #000 }
        float h1 = 58f;
        float sub = 22f;
        float cy = hud.Height * 0.5f - 300f;
        if (cy < 20f) cy = 20f;

        hud.ShadowText(hud.Width * 0.5f, cy, "Shaft Additionality", h1, Rgb(0xff, 0xcc, 0x44),
                       TextAlign.Centre, 3f);
        cy += h1 * 1.2f + 10f;
        hud.Text(hud.Width * 0.5f, cy, "Select a world", sub, Rgb(0x88, 0x88, 0x88), TextAlign.Centre);
        cy += sub * 1.2f + 20f;

        float listW = Math.Min(500f, hud.Width * 0.9f);
        float lx = (hud.Width - listW) * 0.5f;

        SaveMeta meta = saves.GetSavesMeta();
        if (meta.Saves.Count == 0)
        {
            hud.Text(hud.Width * 0.5f, cy + 20f, "No worlds yet - create one below.",
                     16f, Rgb(0x66, 0x66, 0x66), TextAlign.Centre);
            cy += 60f;
        }
        else
        {
            foreach (SaveMetaEntry entry in meta.Saves)
            {
                float eh = 70f;
                bool entryHovered = hud.IsHovered($"save:{entry.Slot}");
                hud.Fill(lx, cy, listW, eh, entryHovered ? Rgb(0x36, 0x36, 0x60) : Rgb(0x2a, 0x2a, 0x4e));
                hud.Border(lx, cy, listW, eh, 2f, entryHovered ? Rgb(0x88, 0x88, 0xcc) : Rgb(0x44, 0x44, 0x44));

                string name = string.IsNullOrEmpty(entry.Name) ? $"World {entry.Slot}" : entry.Name;
                hud.Text(lx + 20f, cy + 12f, name, 24f, White);
                string detail = $"Seed: {entry.Seed} . Played: {entry.PlayedSeconds / 60}m . {(entry.Gamemode ?? "creative")}";
                float dScale = 15f;
                detail = Truncate(hud, detail, dScale, listW - 40f - 90f - 16f - 8f);
                hud.Text(lx + 20f, cy + 12f + 24f + 4f, detail,
                         dScale, Rgb(0xaa, 0xaa, 0xaa));

                float dw = 90f, dh = 34f;
                float dx = lx + listW - dw - 16f, dy = cy + (eh - dh) * 0.5f;
                bool dHover = hud.IsHovered($"del:{entry.Slot}");
                hud.Fill(dx, dy, dw, dh, dHover ? Rgb(0x8b, 0x30, 0x30) : Rgb(0x6b, 0x20, 0x20));
                hud.Border(dx, dy, dw, dh, 1f, Rgb(0x8b, 0x30, 0x30));
                hud.Text(dx + dw * 0.5f, dy + 8f, "Delete", 16f, White, TextAlign.Centre);

                if (hud.Clicked($"del:{entry.Slot}", dx, dy, dw, dh) == 1) ev.DeleteSaveSlot = entry.Slot;
                else if (hud.Clicked($"save:{entry.Slot}", lx, cy, listW, eh) == 1) ev.PlaySaveSlot = entry.Slot;

                cy += eh + 10f;
                if (cy > hud.Height - 120f) break;
            }
        }

        float bw = 240f, bh = 56f;
        float bx = (hud.Width - bw) * 0.5f;
        float by = Math.Min(cy + 20f, hud.Height - 80f);
        bool hovered = hud.IsHovered("btn-new-world");
        hud.Fill(bx, by, bw, bh, hovered ? Rgb(0x3a, 0x74, 0xcc) : Rgb(0x2a, 0x5d, 0xb0));
        hud.Border(bx, by, bw, bh, 2f, Rgb(0x4c, 0x8f, 0xd8));
        hud.Text(bx + bw * 0.5f, by + (bh - 22f) * 0.5f, "+ New World",
                 22f, White, TextAlign.Centre);
        if (hud.Clicked("btn-new-world", bx, by, bw, bh) == 1) ev.NewWorld = true;

        if (st.TitleStatus.Length > 0)
            hud.Text(hud.Width * 0.5f, hud.Height - 40f, st.TitleStatus,
                     16f, Rgb(0xff, 0x88, 0x88), TextAlign.Centre);
    }

    // ------------------------------------------------------------------ prompt

    /// <summary>Stands in for <c>prompt()</c> and <c>confirm()</c>.</summary>
    private static void DrawPrompt(HudRenderer hud, HudState st, HudEvents ev)
    {
        if (st.Prompt == PromptKind.None) return;
        hud.Fill(0, 0, hud.Width, hud.Height, new Vector4(0, 0, 0, 0.6f));

        float w = 460f;
        float msg = 18f;
        string[] lines = hud.Wrap(st.PromptMessage, msg, w - 60f);
        bool needsText = st.Prompt == PromptKind.NewWorld;
        float h = 30f + lines.Length * (msg * 1.2f + 4f) + 20f + (needsText ? 44f : 0f) + 66f;
        float x = (hud.Width - w) * 0.5f, y = (hud.Height - h) * 0.5f;

        hud.Fill(x, y, w, h, Rgb(0x2a, 0x2a, 0x4e));
        hud.Border(x, y, w, h, 3f, White);

        float cy = y + 30f;
        foreach (string l in lines)
        {
            hud.Text(x + 30f, cy, l, msg, White);
            cy += msg * 1.2f + 4f;
        }
        cy += 20f;

        if (needsText)
        {
            hud.Fill(x + 30f, cy, w - 60f, 34f, new Vector4(0, 0, 0, 0.85f));
            hud.Border(x + 30f, cy, w - 60f, 34f, 1f, Rgb(0x55, 0x55, 0x55));
            hud.Text(x + 40f, cy + 9f, st.PromptValue, msg, White);
            float caretX = x + 40f + hud.TextWidth(st.PromptValue, msg);
            hud.Fill(caretX, cy + 7f, 1f, 20f, White);
            hud.Widget("prompt-input", x + 30f, cy, w - 60f, 34f);
            cy += 44f;
        }

        float bw = 130f, bh = 42f;
        float okX = x + w * 0.5f - bw - 10f, cancelX = x + w * 0.5f + 10f;
        ModeButton(hud, "prompt-ok", okX, cy, bw, bh, needsText ? "Create" : "Yes", 18f);
        ModeButton(hud, "prompt-cancel", cancelX, cy, bw, bh, "Cancel", 18f);
        if (hud.Clicked("prompt-ok", okX, cy, bw, bh) == 1) ev.PromptConfirm = st.PromptValue;
        if (hud.Clicked("prompt-cancel", cancelX, cy, bw, bh) == 1) ev.PromptCancel = true;
    }

    // ------------------------------------------------------------- creative list

    /// <summary><c>CREATIVE_ITEMS_BASE</c> from main.js, in order.</summary>
    public static class CreativeItems
    {
        public static readonly int[] List =
        {
            Block.GRASS, Block.DIRT, Block.DIRT_DARK, Block.STONE, Block.COBBLESTONE,
            Block.SAND, Block.SNOW, Block.ICE, Block.LOG, Block.LEAVES,
            Block.CACTUS, Block.PLANKS, Block.CRAFTING_TABLE, Block.COAL_ORE, Block.IRON_ORE,
            Block.FURNACE, Block.CHEST, Block.BED_FOOT, Block.TORCH, Block.DOOR_BOTTOM_CLOSED,
            ItemId.STICK, ItemId.WP, ItemId.WA, ItemId.SP, ItemId.SA,
            ItemId.BEEF, ItemId.STEAK, ItemId.COAL, ItemId.RAW_IRON, ItemId.IRON_INGOT,
            ItemId.ROTTEN_FLESH,
        };
    }
}
