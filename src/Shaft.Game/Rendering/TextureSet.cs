using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Shaft.Engine.Config;
using Shaft.Engine.Items;

namespace Shaft.Game.Rendering;

/// <summary>A sub-rectangle of a texture in GL coordinates (v increases upward).</summary>
public readonly struct TexRect
{
    public readonly float U0, U1, VTop, VBottom;
    public TexRect(float u0, float u1, float vTop, float vBottom)
    {
        U0 = u0; U1 = u1; VTop = vTop; VBottom = vBottom;
    }
    public static readonly TexRect None = new(0, 0, 0, 0);
}

/// <summary>
/// Loads <c>dist/textures</c> and <c>dist/models</c> and turns them into the GL textures
/// the renderers sample.
/// <para>
/// Every image is uploaded row-flipped so that GL <c>v = 1</c> corresponds to the top of
/// the source PNG. That reproduces three.js's <c>Texture.flipY = true</c> (the default for
/// <c>CanvasTexture</c>, <c>TextureLoader</c> and friends), which is what lets the UV maths
/// in <c>ChunkMesher</c> and <c>ModelLoader</c> be used exactly as the JS wrote it.
/// </para>
/// <para>
/// Images are uploaded as plain <c>RGBA8</c> rather than <c>SRGB8_ALPHA8</c>. three.js
/// gets its sRGB decode for free from the internal format when a texture is tagged
/// <c>colorSpace = SRGBColorSpace</c>; here the shaders call <c>sRGBToLinear</c> instead.
/// Same arithmetic, one less platform dependency.
/// </para>
/// </summary>
public sealed class TextureSet : IDisposable
{
    public const int TILE = GameConfig.ATLAS_SIZE;          // 16
    public const int ATLAS_COLS = GameConfig.ATLAS_COLS;    // 64
    public const int FONT_CELL = BitmapFont.GLYPH_W;        // 8
    public const int FONT_COLUMNS = 32;
    public const int SKY_SPRITE = 128;

    public static readonly int FontRows =
        (BitmapFont.GLYPH_COUNT + FONT_COLUMNS - 1) / FONT_COLUMNS;

    public GL Gl { get; }

    public uint TerrainAtlas { get; private set; }
    public int TerrainAtlasWidth { get; private set; }
    public int TerrainAtlasHeight { get; private set; }

    /// <summary>Item id (1000+) -> its own texture, as <c>items.js</c> loaded them for
    /// dropped-item meshes. The HUD samples the same textures, which is what the CSS
    /// <c>background-image</c> did.</summary>
    public Dictionary<int, uint> ItemTextures { get; } = new();

    public uint FontTexture { get; private set; }

    /// <summary>The real UI typeface (dist/font); null falls back to the bitmap font.</summary>
    public FontAtlas? Font { get; private set; }
    public int FontTextureWidth { get; private set; }
    public int FontTextureHeight { get; private set; }

    public uint SunTexture { get; private set; }
    public uint MoonTexture { get; private set; }

    /// <summary>A 1x1 white texture, used wherever the JS bound <c>map: null</c>.</summary>
    public uint WhiteTexture { get; private set; }

    public Dictionary<string, uint> ModelTextures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (int W, int H)> ModelTextureSizes { get; } = new(StringComparer.Ordinal);

    private readonly List<uint> _all = new();

    public TextureSet(GL gl, string distRoot, List<string> warnings)
    {
        Gl = gl;
        TerrainAtlasWidth = ATLAS_COLS * TILE;   // 1024
        TerrainAtlasHeight = TILE;               // 16
        FontTextureWidth = FontPixelWidth;
        FontTextureHeight = FontPixelHeight;

        TerrainAtlas = Upload(BuildTerrainAtlasPixels(distRoot, warnings), TerrainAtlasWidth, TerrainAtlasHeight);
        LoadItemTextures(distRoot, warnings);
        FontTexture = Upload(BuildFontAtlasPixels(), FontTextureWidth, FontTextureHeight);
        Font = FontAtlas.Load(gl, distRoot);
        if (Font is null) warnings.Add("dist/font atlas missing; HUD falls back to the built-in bitmap font");
        SunTexture = Upload(BuildSunPixels(), SKY_SPRITE, SKY_SPRITE);
        MoonTexture = Upload(BuildMoonPixels(), SKY_SPRITE, SKY_SPRITE);
        WhiteTexture = Upload(new byte[] { 255, 255, 255, 255 }, 1, 1);

        foreach (var (_, _, texturePath) in AssetManifest.Models)
            LoadModelTexture(distRoot, texturePath, warnings);
    }

    // ------------------------------------------------------------------ atlases

    /// <summary>
    /// Port of <c>loadAllTextures()</c>: <c>drawImage(img, i * 16, 0, 16, 16)</c> into a
    /// 1024x16 canvas with <c>imageSmoothingEnabled = false</c>. The source PNGs are
    /// 96x96, so this is a 6:1 nearest-neighbour downscale — the browser samples the
    /// source pixel under the centre of each destination pixel, which is what
    /// <see cref="BlitNearest"/> does.
    /// </summary>
    public static int TerrainAtlasPixelWidth => ATLAS_COLS * TILE;
    public static int TerrainAtlasPixelHeight => TILE;
    public static int FontPixelWidth => FONT_COLUMNS * FONT_CELL;
    public static int FontPixelHeight => FontRows * FONT_CELL;

    /// <summary>Builds the 1024x16 atlas pixels without touching GL (used by --selftest).</summary>
    public static byte[] BuildTerrainAtlasPixels(string distRoot, List<string> warnings)
    {
        int atlasW = TerrainAtlasPixelWidth, atlasH = TerrainAtlasPixelHeight;
        var atlas = new byte[atlasW * atlasH * 4];
        for (int i = 0; i < AssetManifest.TextureFiles.Length; i++)
        {
            string path = Path.Combine(distRoot, AssetManifest.TextureFiles[i]);
            if (!TryLoadPng(path, out byte[] src, out int sw, out int sh))
            {
                // textures.js logs "FAILED: <path>" and leaves the slot transparent, which
                // alphaTest then discards. Reproduced here; a loud warning is the tell.
                warnings.Add($"missing texture: {AssetManifest.TextureFiles[i]}");
                continue;
            }
            BlitNearest(src, sw, sh, atlas, i * TILE, 0, TILE, TILE, atlasW);
        }
        return atlas;
    }

    /// <summary>
    /// Port of <c>loadItemTextures()</c>. Each PNG becomes its own texture rather than a
    /// cell in an atlas: <c>items.js</c> did <c>new TextureLoader().load(url)</c> per drop
    /// and <c>ui.js</c> set a CSS <c>background-image</c> per slot, so neither path wanted
    /// an atlas.
    /// </summary>
    private void LoadItemTextures(string distRoot, List<string> warnings)
    {
        for (int i = 0; i < AssetManifest.ItemTextureFiles.Length; i++)
        {
            string rel = AssetManifest.ItemTextureFiles[i];
            string path = Path.Combine(distRoot, rel);
            if (!TryLoadPng(path, out byte[] src, out int w, out int h))
            {
                // textures.js logs "missing texture: <path>" and carries on; getIconHTML()
                // then falls back to a flat coloured square.
                warnings.Add($"missing item texture: {rel}");
                continue;
            }
            ItemTextures[ItemId.STICK + i] = Upload(src, w, h);
        }
    }

    /// <summary>Rasterises <see cref="BitmapFont"/> into a 32-wide grid of 8x8 cells.</summary>
    public static byte[] BuildFontAtlasPixels()
    {
        int w = FontPixelWidth;
        int h = FontPixelHeight;
        var buf = new byte[w * h * 4];
        Span<byte> rows = stackalloc byte[BitmapFont.GLYPH_H];

        for (int g = 0; g < BitmapFont.GLYPH_COUNT; g++)
        {
            int col = g % FONT_COLUMNS, row = g / FONT_COLUMNS;
            for (int y = 0; y < BitmapFont.GLYPH_H; y++) rows[y] = BitmapFont.Glyphs[g * BitmapFont.GLYPH_H + y];

            for (int y = 0; y < BitmapFont.GLYPH_H; y++)
            {
                byte bits = rows[y];
                for (int x = 0; x < BitmapFont.GLYPH_W; x++)
                {
                    bool on = (bits & (0x80 >> x)) != 0;
                    int o = ((row * FONT_CELL + y) * w + col * FONT_CELL + x) * 4;
                    buf[o] = 255; buf[o + 1] = 255; buf[o + 2] = 255;
                    buf[o + 3] = (byte)(on ? 255 : 0);
                }
            }
        }
        return buf;
    }

    private void LoadModelTexture(string distRoot, string rel, List<string> warnings)
    {
        if (ModelTextures.ContainsKey(rel)) return;
        string path = Path.Combine(distRoot, rel);
        if (!TryLoadPng(path, out byte[] src, out int w, out int h))
        {
            warnings.Add($"missing model texture: {rel}");
            ModelTextures[rel] = WhiteTexture;
            ModelTextureSizes[rel] = (1, 1);
            return;
        }
        ModelTextures[rel] = Upload(src, w, h);
        ModelTextureSizes[rel] = (w, h);
    }

    // ------------------------------------------------------------- sun and moon

    /// <summary>Port of <c>buildSunMoon()</c>'s sun canvas.</summary>
    public static byte[] BuildSunPixels()
    {
        var stops = new (float T, float R, float G, float B, float A)[]
        {
            (0.0f, 255 / 255f, 255 / 255f, 240 / 255f, 1.0f),
            (0.5f, 255 / 255f, 255 / 255f, 200 / 255f, 1.0f),
            (0.8f, 255 / 255f, 250 / 255f, 180 / 255f, 0.5f),
            (1.0f, 255 / 255f, 250 / 255f, 180 / 255f, 0.0f),
        };
        return RadialGradientFill(stops, radius: 64f, clipRadius: null);
    }

    /// <summary>Port of <c>buildSunMoon()</c>'s moon canvas: gradient clipped to r = 50.</summary>
    public static byte[] BuildMoonPixels()
    {
        var stops = new (float T, float R, float G, float B, float A)[]
        {
            (0.0f, 240 / 255f, 240 / 255f, 255 / 255f, 1.0f),
            (0.6f, 200 / 255f, 200 / 255f, 220 / 255f, 0.7f),
            (1.0f, 200 / 255f, 200 / 255f, 220 / 255f, 0.0f),
        };
        return RadialGradientFill(stops, radius: 64f, clipRadius: 50f);
    }

    /// <summary>
    /// Canvas <c>createRadialGradient(64,64,8,64,64,r)</c> painted over a transparent
    /// 128x128 canvas. Canvas interpolates colour stops in premultiplied sRGB, so the
    /// lerp happens on premultiplied components and is un-premultiplied afterwards; that
    /// is why the fade to alpha 0 keeps its hue instead of going dark.
    /// </summary>
    private static byte[] RadialGradientFill(
        (float T, float R, float G, float B, float A)[] stops, float radius, float? clipRadius)
    {
        int size = SKY_SPRITE;
        var buf = new byte[size * size * 4];
        const float inner = 8f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - 64f, dy = y + 0.5f - 64f;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (clipRadius is float cr && d > cr) continue;   // outside arc() -> untouched

                float t = radius <= inner ? 1f : (d - inner) / (radius - inner);
                t = Math.Clamp(t, 0f, 1f);

                int i = 0;
                while (i < stops.Length - 2 && t > stops[i + 1].T) i++;
                var a = stops[i];
                var b = stops[i + 1];
                float span = Math.Max(1e-6f, b.T - a.T);
                float k = Math.Clamp((t - a.T) / span, 0f, 1f);

                float pa = a.A + (b.A - a.A) * k;
                float pr = a.R * a.A + (b.R * b.A - a.R * a.A) * k;
                float pg = a.G * a.A + (b.G * b.A - a.G * a.A) * k;
                float pb = a.B * a.A + (b.B * b.A - a.B * a.A) * k;

                float inv = pa > 1e-6f ? 1f / pa : 0f;
                int o = (y * size + x) * 4;
                buf[o] = ToByte(pr * inv);
                buf[o + 1] = ToByte(pg * inv);
                buf[o + 2] = ToByte(pb * inv);
                buf[o + 3] = ToByte(pa);
            }
        }
        return buf;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    // -------------------------------------------------------------------- rects

    public TexRect BlockTile(int slot)
    {
        if (slot < 0) slot = 0;
        float u0 = slot / (float)ATLAS_COLS;
        float u1 = (slot + 1) / (float)ATLAS_COLS;
        return new TexRect(u0, u1, 1f, 0f);
    }

    public TexRect Glyph(char c)
    {
        if (c < BitmapFont.FIRST_CHAR || c > BitmapFont.LAST_CHAR) c = '?';
        int g = c - BitmapFont.FIRST_CHAR;
        int col = g % FONT_COLUMNS, row = g / FONT_COLUMNS;
        float u0 = col * FONT_CELL / (float)FontTextureWidth;
        float u1 = (col + 1) * FONT_CELL / (float)FontTextureWidth;
        float vTop = (FontRows - row) * FONT_CELL / (float)FontTextureHeight;
        float vBottom = (FontRows - row - 1) * FONT_CELL / (float)FontTextureHeight;
        return new TexRect(u0, u1, vTop, vBottom);
    }

    public TexRect Full => new(0f, 1f, 1f, 0f);

    // ------------------------------------------------------------------ helpers

    private uint Upload(byte[] rgbaTopDown, int width, int height)
    {
        uint tex = GlUtil.CreateTexture2D(Gl, width, height, rgbaTopDown, flipVertically: true);
        _all.Add(tex);
        return tex;
    }

    /// <summary>Reads a PNG as tightly packed RGBA8, top row first.</summary>
    internal static bool TryLoadPng(string path, out byte[] rgba, out int width, out int height)
    {
        rgba = Array.Empty<byte>();
        width = height = 0;
        if (!File.Exists(path)) return false;
        try
        {
            using var img = Image.Load<Rgba32>(path);
            // out parameters cannot be captured by a lambda, so decode into locals first.
            int w = img.Width, h = img.Height;
            var pixels = new byte[w * h * 4];
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    int o = y * w * 4;
                    for (int x = 0; x < row.Length; x++)
                    {
                        pixels[o + x * 4 + 0] = row[x].R;
                        pixels[o + x * 4 + 1] = row[x].G;
                        pixels[o + x * 4 + 2] = row[x].B;
                        pixels[o + x * 4 + 3] = row[x].A;
                    }
                }
            });
            width = w;
            height = h;
            rgba = pixels;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>ctx.imageSmoothingEnabled = false</c> semantics: each destination pixel takes
    /// the source pixel under its centre.
    /// </summary>
    internal static void BlitNearest(byte[] src, int sw, int sh, byte[] dst,
                                     int dx0, int dy0, int dw, int dh, int dstWidthPx)
    {
        int dstStride = dstWidthPx * 4;
        for (int y = 0; y < dh; y++)
        {
            int sy = (int)((y + 0.5) * sh / dh);
            if (sy >= sh) sy = sh - 1;
            if (sy < 0) sy = 0;
            for (int x = 0; x < dw; x++)
            {
                int sx = (int)((x + 0.5) * sw / dw);
                if (sx >= sw) sx = sw - 1;
                if (sx < 0) sx = 0;
                int s = (sy * sw + sx) * 4;
                int d = (dy0 + y) * dstStride + (dx0 + x) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
    }

    public void Dispose()
    {
        foreach (uint t in _all) Gl.DeleteTexture(t);
        _all.Clear();
    }
}
