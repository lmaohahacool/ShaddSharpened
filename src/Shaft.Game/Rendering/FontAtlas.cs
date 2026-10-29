using System.Text.Json;
using Silk.NET.OpenGL;

namespace Shaft.Game.Rendering;

/// <summary>
/// The game's real UI typeface: dist/style.css asks for 'Jersey 15', so the browser build
/// renders every HUD string in it. This loads the rasterised atlas produced by
/// tools/gen_font_atlas.py (dist/font/jersey15.png + .json) and draws text at exact CSS
/// pixel sizes, replacing the old 8px bitmap font whose integer scales could only
/// approximate the CSS sizes ("gui and text is a bit too big").
/// </summary>
public sealed class FontAtlas
{
    public readonly record struct Glyph(float X, float Y, float W, float H,
                                        float Bx, float By, float Adv);

    private readonly Dictionary<char, Glyph> _glyphs = new();
    private readonly float _renderSize;

    public uint Texture { get; }
    public int Width { get; }
    public int Height { get; }

    private FontAtlas(uint texture, int w, int h, float renderSize, Dictionary<char, Glyph> glyphs)
    {
        Texture = texture; Width = w; Height = h;
        _renderSize = renderSize; _glyphs = glyphs;
    }

    /// <summary>Returns null when the font assets are absent; callers fall back to BitmapFont.</summary>
    public static FontAtlas? Load(GL gl, string distRoot)
    {
        string png = Path.Combine(distRoot, "font", "jersey15.png");
        string json = Path.Combine(distRoot, "font", "jersey15.json");
        if (!File.Exists(png) || !File.Exists(json)) return null;

        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
        int w = img.Width, h = img.Height;
        byte[] rgba = new byte[w * h * 4];
        img.CopyPixelDataTo(rgba);
        // The grayscale atlas carries coverage in R; expand to white + coverage-as-alpha so
        // the HUD shader's colour multiply tints the glyphs.
        byte[] expanded = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            expanded[i * 4 + 0] = 255;
            expanded[i * 4 + 1] = 255;
            expanded[i * 4 + 2] = 255;
            expanded[i * 4 + 3] = rgba[i * 4];
        }

        uint tex = GlUtil.CreateTexture2D(gl, w, h, expanded, flipVertically: false);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                        (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                        (int)TextureWrapMode.ClampToEdge);
        gl.GenerateMipmap(TextureTarget.Texture2D);

        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        float size = doc.RootElement.GetProperty("size").GetSingle();
        var glyphs = new Dictionary<char, Glyph>();
        foreach (JsonProperty p in doc.RootElement.GetProperty("glyphs").EnumerateObject())
        {
            var v = p.Value;
            glyphs[(char)int.Parse(p.Name)] = new Glyph(
                v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle(), v[3].GetSingle(),
                v[4].GetSingle(), v[5].GetSingle(), v[6].GetSingle());
        }
        return new FontAtlas(tex, w, h, size, glyphs);
    }

    public float Measure(string s, float cssPx)
    {
        float k = cssPx / _renderSize, pen = 0;
        foreach (char ch in s) pen += Get(ch).Adv * k;
        return pen;
    }

    /// <summary>Font pixel size the atlas was rasterised at; callers scale by cssPx / this.</summary>
    public float RenderSize => _renderSize;

    public Glyph Get(char ch) => _glyphs.TryGetValue(ch, out Glyph g) ? g : _glyphs['?'];
}
