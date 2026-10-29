using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Shaft.Game.Rendering;

/// <summary>
/// Thin wrapper over a linked GL program with a uniform-location cache. The browser
/// build leaned on three.js for this; here it is ~80 lines because we only ever need
/// the handful of uniforms the ported shading model actually reads.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _locs = new(StringComparer.Ordinal);

    public uint Handle { get; private set; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource, string name)
    {
        _gl = gl;
        uint vs = Compile(gl, ShaderType.VertexShader, vertexSource, name + ".vert");
        uint fs = Compile(gl, ShaderType.FragmentShader, fragmentSource, name + ".frag");

        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vs);
        gl.AttachShader(Handle, fs);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(Handle);
            gl.DeleteProgram(Handle);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"Link failed for {name}:\n{log}");
        }

        gl.DetachShader(Handle, vs);
        gl.DetachShader(Handle, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    private static uint Compile(GL gl, ShaderType type, string source, string label)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, source);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetShaderInfoLog(sh);
            gl.DeleteShader(sh);
            throw new InvalidOperationException($"Compile failed for {label}:\n{log}");
        }
        return sh;
    }

    /// <summary>Returns -1 for an unknown/optimised-out uniform instead of throwing.</summary>
    public int Loc(string name)
    {
        if (_locs.TryGetValue(name, out int cached)) return cached;
        int loc = _gl.GetUniformLocation(Handle, name);
        _locs[name] = loc;
        return loc;
    }

    public void Use() => _gl.UseProgram(Handle);

    public void Set(string name, float v) { int l = Loc(name); if (l >= 0) _gl.Uniform1(l, v); }
    public void Set(string name, int v) { int l = Loc(name); if (l >= 0) _gl.Uniform1(l, v); }
    public void Set(string name, in Vector2 v) { int l = Loc(name); if (l >= 0) _gl.Uniform2(l, v.X, v.Y); }
    public void Set(string name, in Vector3 v) { int l = Loc(name); if (l >= 0) _gl.Uniform3(l, v.X, v.Y, v.Z); }
    public void Set(string name, float x, float y, float z)
    {
        int l = Loc(name);
        if (l >= 0) _gl.Uniform3(l, x, y, z);
    }
    public void Set(string name, float x, float y, float z, float w)
    {
        int l = Loc(name);
        if (l >= 0) _gl.Uniform4(l, x, y, z, w);
    }

    /// <summary>Convenience overload so call sites read <c>Set("uView", cam.View)</c>.</summary>
    public void Set(string name, in Matrix4x4 m) => SetMatrix(name, m);

    /// <summary>
    /// Uploads a <see cref="System.Numerics"/> matrix to a <c>mat4</c> uniform.
    /// <para>
    /// Every matrix in this codebase is built with System.Numerics, whose convention is
    /// <b>row vectors</b> (<c>v * M</c>, translation living in M41/M42/M43), while the GLSL
    /// shaders all evaluate <c>uM * v</c> with column vectors. The matrix the shader needs
    /// is therefore the transpose of the one we hold. Because <c>flat</c> below is the
    /// row-major image of our matrix — which is exactly the <b>column-major</b> image of
    /// its transpose — uploading it with <c>transpose: false</c> hands GL precisely the
    /// matrix the shader wants. Passing <c>true</c> here instead leaves the translation in
    /// the fourth <i>row</i>, which puts <c>x/y/z</c> terms into <c>w'</c> and turns every
    /// transform into a bogus perspective divide.
    /// </para>
    /// </summary>
    public void SetMatrix(string name, in Matrix4x4 m)
    {
        int l = Loc(name);
        if (l < 0) return;
        Span<float> flat = stackalloc float[16];
        flat[0] = m.M11; flat[1] = m.M12; flat[2] = m.M13; flat[3] = m.M14;
        flat[4] = m.M21; flat[5] = m.M22; flat[6] = m.M23; flat[7] = m.M24;
        flat[8] = m.M31; flat[9] = m.M32; flat[10] = m.M33; flat[11] = m.M34;
        flat[12] = m.M41; flat[13] = m.M42; flat[14] = m.M43; flat[15] = m.M44;
        _gl.UniformMatrix4(l, 1, false, ref MemoryMarshal.GetReference(flat));
    }

    public void Dispose()
    {
        if (Handle != 0) { _gl.DeleteProgram(Handle); Handle = 0; }
    }
}

/// <summary>GL object helpers shared by the world, entity and HUD renderers.</summary>
public static class GlUtil
{
    /// <summary>
    /// Uploads tightly packed RGBA8 pixels. <see cref="TextureSet"/> hands us row-flipped
    /// buffers so that texture <c>v</c> increases upward through the source image, which
    /// is what three.js gets from <c>Texture.flipY = true</c> — the default for every
    /// texture the browser build creates. Keeping that convention means every UV the
    /// mesher and the model loader compute can be used verbatim.
    /// </summary>
    public static uint CreateTexture2D(GL gl, int width, int height, ReadOnlySpan<byte> rgba,
                                       bool flipVertically, bool alreadyFlipped = false)
    {
        uint tex = gl.GenTexture();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        byte[]? scratch = null;
        ReadOnlySpan<byte> data = rgba;
        if (flipVertically && !alreadyFlipped)
        {
            scratch = new byte[rgba.Length];
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                int src = y * stride;
                int dst = (height - 1 - y) * stride;
                rgba.Slice(src, stride).CopyTo(scratch.AsSpan(dst, stride));
            }
            data = scratch;
        }

        unsafe
        {
            fixed (byte* p = data)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                              (uint)width, (uint)height, 0,
                              PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }

        // NearestFilter / NearestFilter / ClampToEdge — exactly what textures.js and
        // mobs.js set on every map they build.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        // No mipmaps: every map in the browser build is NearestFilter/NearestFilter.
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    public static uint CreateBuffer(GL gl, BufferTargetARB target)
    {
        uint b = gl.GenBuffer();
        gl.BindBuffer(target, b);
        return b;
    }

    /// <summary>Flattens a row-major <see cref="Matrix4x4"/> into the 16-float GL order.</summary>
    public static void Flatten(in Matrix4x4 m, Span<float> dst)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }
}
