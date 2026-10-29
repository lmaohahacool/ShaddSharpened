using System.Numerics;
using System.Text;
using Silk.NET.OpenGL;
using Shaft.Engine.Config;

namespace Shaft.Game.Rendering;

public enum TextAlign { Left, Centre, Right }

/// <summary>
/// Immediate-mode 2D renderer for everything the browser build expressed as DOM:
/// the hotbar, hearts, hunger, panels, menus, chat, tooltips and the death screen.
/// <para>
/// Coordinates are in <b>window units</b> (the same device-independent pixels CSS used),
/// with the origin at the top-left and y increasing downward, so every number lifted out of
/// <c>dist/style.css</c> can be used as-is. The orthographic projection maps that space onto
/// the framebuffer, which may be a whole-number multiple larger at high DPI — exactly what
/// <c>renderer.setPixelRatio</c> did for the 3D pass.
/// </para>
/// <para>
/// Geometry is accumulated as unindexed triangles in one dynamic buffer and split into
/// batches whenever the bound texture changes, which preserves painter's order without a
/// depth buffer.
/// </para>
/// </summary>
public sealed class HudRenderer : IDisposable
{
    private const int FLOATS_PER_VERTEX = 8;   // pos(2) uv(2) colour(4)

    private readonly GL _gl;
    private readonly TextureSet _tex;
    private readonly ShaderProgram _program;

    private readonly List<float> _verts = new(1 << 14);
    private readonly List<(uint Tex, int Start, int Count)> _batches = new();
    private readonly List<(string Id, float X, float Y, float W, float H)> _widgets = new();
    private readonly Matrix4x4[] _ortho = new Matrix4x4[1];

    private uint _vao, _vbo;
    private int _vboCapacityFloats;

    public float Width { get; private set; } = 1f;
    public float Height { get; private set; } = 1f;
    public int FramebufferWidth { get; private set; } = 1;
    public int FramebufferHeight { get; private set; } = 1;

    /// <summary>Pointer position in window units.</summary>
    public Vector2 Mouse;

    // Edge-triggered input, set by the host and cleared by EndFrame().
    public bool LeftDown, RightDown, MiddleDown;
    public bool LeftClicked, RightClicked, MiddleClicked;
    public float WheelDelta;
    public bool ShiftHeld, ControlHeld;

    public HudRenderer(GL gl, TextureSet tex)
    {
        _gl = gl;
        _tex = tex;
        _program = new ShaderProgram(gl, Shaders.Expand(Shaders.HudVs),
                                     Shaders.Expand(Shaders.HudFs), "hud");

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = GlUtil.CreateBuffer(gl, BufferTargetARB.ArrayBuffer);
        const int stride = FLOATS_PER_VERTEX * 4;
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (nint)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (nint)8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (nint)16);
        gl.BindVertexArray(0);
    }

    public TextureSet Textures => _tex;

    // ------------------------------------------------------------------- frames

    public void BeginFrame(int winW, int winH, int fbW, int fbH)
    {
        Width = Math.Max(1, winW);
        Height = Math.Max(1, winH);
        FramebufferWidth = Math.Max(1, fbW);
        FramebufferHeight = Math.Max(1, fbH);

        _verts.Clear();
        _batches.Clear();
        _widgets.Clear();

        // Top-left origin, y down, in window units.
        _ortho[0] = Matrix4x4.CreateOrthographicOffCenter(0f, Width, Height, 0f, -1f, 1f);
    }

    /// <summary>Uploads the batch and draws it. Call once per frame, after all UI is built.</summary>
    public void EndFrame()
    {
        if (_batches.Count == 0)
        {
            LeftClicked = RightClicked = MiddleClicked = false;
            WheelDelta = 0f;
            return;
        }

        _gl.Viewport(0, 0, (uint)FramebufferWidth, (uint)FramebufferHeight);
        _gl.Scissor(0, 0, (uint)FramebufferWidth, (uint)FramebufferHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        int floats = _verts.Count;
        EnsureCapacity(floats);
        float[] array = _verts.ToArray();
        _gl.BufferSubData<float>(BufferTargetARB.ArrayBuffer, 0, array);

        _program.Use();
        _program.SetMatrix("uProj", _ortho[0]);
        _program.Set("uMap", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);

        foreach ((uint tex, int start, int count) in _batches)
        {
            _program.Set("uUseMap", tex == 0 ? 0f : 1f);
            if (tex != 0) _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.DrawArrays(PrimitiveType.Triangles, start, (uint)count);
        }

        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);

        LeftClicked = RightClicked = MiddleClicked = false;
        WheelDelta = 0f;
    }

    private unsafe void EnsureCapacity(int floats)
    {
        if (floats <= _vboCapacityFloats) return;
        _vboCapacityFloats = Math.Max(floats, Math.Max(_vboCapacityFloats * 2, 4096));

        // Allocate (orphan) the store once; per-frame updates go through BufferSubData so
        // the driver never has to reallocate a buffer that is still in flight.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vboCapacityFloats * 4),
                       (void*)null, BufferUsageARB.DynamicDraw);
    }

    // ----------------------------------------------------------------- geometry

    private void Vertex(uint tex, float x, float y, float u, float v, in Vector4 c)
    {
        if (_batches.Count == 0 || _batches[^1].Tex != tex)
            _batches.Add((tex, _verts.Count / FLOATS_PER_VERTEX, 0));
        var b = _batches[^1];
        _batches[^1] = (b.Tex, b.Start, b.Count + 1);

        _verts.Add(x); _verts.Add(y);
        _verts.Add(u); _verts.Add(v);
        _verts.Add(c.X); _verts.Add(c.Y); _verts.Add(c.Z); _verts.Add(c.W);
    }

    private void Triangle(uint tex, float x0, float y0, float u0, float v0,
                          float x1, float y1, float u1, float v1,
                          float x2, float y2, float u2, float v2, in Vector4 c)
    {
        Vertex(tex, x0, y0, u0, v0, c);
        Vertex(tex, x1, y1, u1, v1, c);
        Vertex(tex, x2, y2, u2, v2, c);
    }

    public void Fill(float x, float y, float w, float h, Vector4 c)
    {
        Triangle(0, x, y, 0, 0, x + w, y, 0, 0, x + w, y + h, 0, 0, c);
        Triangle(0, x, y, 0, 0, x + w, y + h, 0, 0, x, y + h, 0, 0, c);
    }

    /// <summary>A CSS <c>border</c>: four solid strips drawn inside the box.</summary>
    public void Border(float x, float y, float w, float h, float t, Vector4 c)
    {
        Fill(x, y, w, t, c);
        Fill(x, y + h - t, w, t, c);
        Fill(x, y + t, t, h - t * 2f, c);
        Fill(x + w - t, y + t, t, h - t * 2f, c);
    }

    /// <summary>Filled convex polygon as a triangle fan from its first vertex.</summary>
    public void Polygon(ReadOnlySpan<Vector2> pts, Vector4 c)
    {
        if (pts.Length < 3) return;
        for (int i = 1; i < pts.Length - 1; i++)
            Triangle(0, pts[0].X, pts[0].Y, 0, 0, pts[i].X, pts[i].Y, 0, 0,
                     pts[i + 1].X, pts[i + 1].Y, 0, 0, c);
    }

    /// <summary>
    /// Draws <paramref name="texRect"/> from <paramref name="tex"/> into a box, using
    /// nearest filtering. <see cref="TexRect"/> is stored bottom-up, so the top of the box
    /// takes <c>VTop</c> — which keeps flipped textures upright.
    /// </summary>
    public void Image(float x, float y, float w, float h, uint tex, TexRect r, Vector4? tint = null)
    {
        Vector4 c = tint ?? Vector4.One;
        Triangle(tex, x, y, r.U0, r.VTop, x + w, y, r.U1, r.VTop, x + w, y + h, r.U1, r.VBottom, c);
        Triangle(tex, x, y, r.U0, r.VTop, x + w, y + h, r.U1, r.VBottom, x, y + h, r.U0, r.VBottom, c);
    }

    // --------------------------------------------------------------------- text

    // --------------------------------------------------------------------- text

    /// <summary>Width of <paramref name="s"/> in framebuffer px at a CSS font-size.</summary>
    public float TextWidth(string s, float cssPx) =>
        _tex.Font is { } f ? f.Measure(s, cssPx) : BitmapFont.Measure(s, LegacyScale(cssPx));

    /// <summary>CSS line-box height (font-size times the normal leading the browser uses).</summary>
    public float TextHeight(float cssPx) =>
        _tex.Font is not null ? cssPx * 1.2f : BitmapFont.GLYPH_H * LegacyScale(cssPx);

    private static int LegacyScale(float cssPx) => Math.Max(1, (int)Math.Round(cssPx / 8f));

    /// <summary>
    /// Draws a string in the game's real UI typeface (Jersey 15 atlas from dist/font) at an
    /// exact CSS pixel size; falls back to the 8px bitmap font when the atlas is absent.
    /// </summary>
    public void Text(float x, float y, string s, float cssPx, Vector4 c, TextAlign align = TextAlign.Left)
    {
        if (string.IsNullOrEmpty(s)) return;
        float w = TextWidth(s, cssPx);
        float px = align switch
        {
            TextAlign.Centre => x - w * 0.5f,
            TextAlign.Right => x - w,
            _ => x,
        };

        if (_tex.Font is { } font)
        {
            float k = cssPx / font.RenderSize;
            foreach (char ch in s)
            {
                var g = font.Get(ch);
                if (g.W > 0 && g.H > 0)
                {
                    float u0 = g.X / font.Width, u1 = (g.X + g.W) / font.Width;
                    float v0 = g.Y / font.Height, v1 = (g.Y + g.H) / font.Height;
                    float gx = px + g.Bx * k, gy = y + g.By * k;
                    float gx1 = gx + g.W * k, gy1 = gy + g.H * k;
                    Triangle(font.Texture, gx, gy, u0, v0, gx1, gy, u1, v0, gx1, gy1, u1, v1, c);
                    Triangle(font.Texture, gx, gy, u0, v0, gx1, gy1, u1, v1, gx, gy1, u0, v1, c);
                }
                px += g.Adv * k;
            }
            return;
        }

        uint fontTex = _tex.FontTexture;
        int scale = LegacyScale(cssPx);
        float size = BitmapFont.GLYPH_W * scale;
        foreach (char ch in s)
        {
            char g = ch;
            if (g < BitmapFont.FIRST_CHAR || g > BitmapFont.LAST_CHAR) g = '?';
            if (g != ' ')
            {
                TexRect cell = _tex.Glyph(g);
                Triangle(fontTex, px, y, cell.U0, cell.VTop, px + size, y, cell.U1, cell.VTop,
                         px + size, y + size, cell.U1, cell.VBottom, c);
                Triangle(fontTex, px, y, cell.U0, cell.VTop, px + size, y + size, cell.U1, cell.VBottom,
                         px, y + size, cell.U0, cell.VBottom, c);
            }
            px += BitmapFont.ADVANCE * scale;
        }
    }

    /// <summary>
    /// Text with the 1px black drop shadow most HUD labels carry
    /// (<c>text-shadow: 1px 1px 0 #000</c>).
    /// </summary>
    public void ShadowText(float x, float y, string s, float cssPx, Vector4 c,
                           TextAlign align = TextAlign.Left, float shadowOffset = 1f)
    {
        Text(x + shadowOffset, y + shadowOffset, s, cssPx, new Vector4(0, 0, 0, c.W), align);
        Text(x, y, s, cssPx, c, align);
    }

    /// <summary>Wraps <paramref name="s"/> to <paramref name="maxWidth"/> at word boundaries.</summary>
    public string[] Wrap(string s, float cssPx, float maxWidth)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (string hard in s.Split('\n'))
        {
            var current = new StringBuilder();
            foreach (string word in hard.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (TextWidth(candidate, cssPx) > maxWidth && current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
                else
                {
                    current.Clear();
                    current.Append(candidate);
                }
            }
            lines.Add(current.ToString());
        }
        return lines.ToArray();
    }

    // ------------------------------------------------------------------ widgets

    /// <summary>Registers a hit-test rectangle. Later registrations win, matching z-order.</summary>
    public void Widget(string id, float x, float y, float w, float h)
        => _widgets.Add((id, x, y, w, h));

    /// <summary>The topmost widget under <paramref name="p"/>, or null.</summary>
    public string? HitTest(Vector2 p)
    {
        for (int i = _widgets.Count - 1; i >= 0; i--)
        {
            var w = _widgets[i];
            if (p.X >= w.X && p.X <= w.X + w.W && p.Y >= w.Y && p.Y <= w.Y + w.H) return w.Id;
        }
        return null;
    }

    public string? HoveredWidget => HitTest(Mouse);

    public bool IsHovered(string id) => ReferenceEquals(HoveredWidget, id) || HoveredWidget == id;

    /// <summary>
    /// A clickable rectangle. Returns the button that was pressed on it this frame
    /// (0 = none, 1 = left, 2 = right, 3 = middle) and draws nothing itself.
    /// </summary>
    public int Clicked(string id, float x, float y, float w, float h)
    {
        Widget(id, x, y, w, h);
        bool inside = Mouse.X >= x && Mouse.X <= x + w && Mouse.Y >= y && Mouse.Y <= y + h;
        if (!inside) return 0;
        if (LeftClicked) return 1;
        if (RightClicked) return 2;
        if (MiddleClicked) return 3;
        return 0;
    }

    public void Dispose()
    {
        _program.Dispose();
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
    }
}

