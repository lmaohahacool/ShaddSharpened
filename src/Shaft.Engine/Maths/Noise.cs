namespace Shaft.Engine.Maths;

/// <summary>
/// Port of <c>dist/math.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two hash implementations are provided, deliberately:
/// </para>
/// <list type="bullet">
/// <item>
/// <term><c>*Legacy</c></term>
/// <description>
/// Bit-exact reproduction of the JavaScript, including its IEEE-754 precision
/// loss. Verified against the unmodified <c>dist/math.js</c> under Node:
/// <c>hash3(3,7,z,12345)</c> matches for every sampled z. This is the Phase A
/// default so that porting bugs can be distinguished from terrain changes.
/// </description>
/// </item>
/// <item>
/// <term><c>*ExactInt64</c></term>
/// <description>
/// The mathematically correct version using <c>long</c> throughout. Differs from
/// the legacy path in ~100% of samples and therefore regenerates every world.
/// Phase B switches <see cref="UseExactInt64"/> to <c>true</c> and bumps
/// <c>SAVE_VERSION</c>.
/// </description>
/// </item>
/// </list>
/// <para>
/// The defect being worked around: the literal <c>1442695040888963407</c> exceeds
/// <see cref="double"/> precision (2^53) and parses to <c>1442695040888963328</c>,
/// a drift of 79. In <c>hash2</c> it multiplies SEED — leaving only 6002 distinct
/// values across the entire 1,000,000-seed space (0.60%). In <c>hash3</c> it
/// multiplies z. See MIGRATION-BRIEF.md §2.
/// </para>
/// </remarks>
public static class Noise
{
    /// <summary>
    /// When false, use the bit-exact JavaScript reproduction (Phase A parity).
    /// When true, use exact 64-bit integer hashing (Phase B fix).
    /// </summary>
    public static bool UseExactInt64 = false;

    private const long C64 = 1442695040888963407L;      // exact as a long
    private const double C64D = 1442695040888963407;    // lossy as a double (JS behaviour)
    private const int X_MUL = 374761393;
    private const int Y_MUL = 668265263;
    private const int SEED3_MUL = 998244353;
    private const int FINAL_MUL = 1274126177;

    // ---------------------------------------------------------------- hashing

    public static double Hash2(int x, int y, int seed)
        => UseExactInt64 ? Hash2ExactInt64(x, y, seed) : Hash2Legacy(x, y, seed);

    public static double Hash3(int x, int y, int z, int seed)
        => UseExactInt64 ? Hash3ExactInt64(x, y, z, seed) : Hash3Legacy(x, y, z, seed);

    /// <summary>Faithful port of <c>hash2</c> from math.js, precision loss included.</summary>
    public static double Hash2Legacy(int x, int y, int seed)
    {
        // x*374761393 + y*668265263 + SEED*1442695040888963407, evaluated in
        // double precision exactly as V8 does, then ToInt32 via '|0'.
        double sum = x * (double)X_MUL + y * (double)Y_MUL + seed * C64D;
        return FinishLegacy(JsMath.ToInt32(sum));
    }

    /// <summary>Faithful port of <c>hash3</c> from math.js, precision loss included.</summary>
    public static double Hash3Legacy(int x, int y, int z, int seed)
    {
        double sum = x * (double)X_MUL
                   + y * (double)Y_MUL
                   + z * C64D
                   + seed * (double)SEED3_MUL;
        return FinishLegacy(JsMath.ToInt32(sum));
    }

    /// <summary>The corrected hash2: exact 64-bit integer accumulation.</summary>
    public static double Hash2ExactInt64(long x, long y, long seed)
    {
        long sum = x * X_MUL + y * Y_MUL + seed * C64;
        return FinishLegacy(unchecked((int)sum));
    }

    /// <summary>The corrected hash3: exact 64-bit integer accumulation.</summary>
    public static double Hash3ExactInt64(long x, long y, long z, long seed)
    {
        long sum = x * X_MUL + y * Y_MUL + z * C64 + seed * SEED3_MUL;
        return FinishLegacy(unchecked((int)sum));
    }

    /// <summary>
    /// The shared avalanche mixer: <c>h = imul(h ^ (h&gt;&gt;&gt;13), 1274126177)</c>
    /// then <c>((h ^ (h&gt;&gt;&gt;16)) &gt;&gt;&gt; 0) / 2^32</c>.
    /// </summary>
    private static double FinishLegacy(int h)
    {
        h = JsMath.IMul(h ^ (int)JsMath.UnsignedShiftRight(h, 13), FINAL_MUL);
        uint u = (uint)(h ^ (int)JsMath.UnsignedShiftRight(h, 16));
        return u / 4294967296.0;
    }

    // ------------------------------------------------------------------ noise

    /// <summary><c>t*t*(3-2*t)</c> from math.js.</summary>
    public static double Smoothstep(double t) => t * t * (3 - 2 * t);

    /// <summary>Port of <c>noise2D</c>: bilinear value noise over hash2 lattice corners.</summary>
    public static double Noise2D(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;

        double a = Hash2(xi, yi, seed);
        double b = Hash2(xi + 1, yi, seed);
        double c = Hash2(xi, yi + 1, seed);
        double d = Hash2(xi + 1, yi + 1, seed);

        double u = Smoothstep(xf), v = Smoothstep(yf);
        return a * (1 - u) * (1 - v) + b * u * (1 - v) + c * (1 - u) * v + d * u * v;
    }

    /// <summary>Port of <c>fbm</c>: normalised fractional Brownian motion over noise2D.</summary>
    public static double Fbm(double x, double y, int seed, int octaves = 4)
    {
        double value = 0, amp = 1, freq = 1, max = 0;
        for (int i = 0; i < octaves; i++)
        {
            value += Noise2D(x * freq, y * freq, seed) * amp;
            max += amp;
            amp *= 0.5;
            freq *= 2;
        }
        return value / max;
    }

    /// <summary>Port of <c>noise3D</c>: trilinear value noise over hash3 lattice corners.</summary>
    public static double Noise3D(double x, double y, double z, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
        double xf = x - xi, yf = y - yi, zf = z - zi;

        double u = Smoothstep(xf), v = Smoothstep(yf), w = Smoothstep(zf);

        double c000 = Hash3(xi,     yi,     zi,     seed);
        double c100 = Hash3(xi + 1, yi,     zi,     seed);
        double c010 = Hash3(xi,     yi + 1, zi,     seed);
        double c110 = Hash3(xi + 1, yi + 1, zi,     seed);
        double c001 = Hash3(xi,     yi,     zi + 1, seed);
        double c101 = Hash3(xi + 1, yi,     zi + 1, seed);
        double c011 = Hash3(xi,     yi + 1, zi + 1, seed);
        double c111 = Hash3(xi + 1, yi + 1, zi + 1, seed);

        double x00 = c000 * (1 - u) + c100 * u;
        double x10 = c010 * (1 - u) + c110 * u;
        double x01 = c001 * (1 - u) + c101 * u;
        double x11 = c011 * (1 - u) + c111 * u;

        double y0 = x00 * (1 - v) + x10 * v;
        double y1 = x01 * (1 - v) + x11 * v;
        return y0 * (1 - w) + y1 * w;
    }

    /// <summary>Port of <c>fbm3D</c>, used for cave carving.</summary>
    public static double Fbm3D(double x, double y, double z, int seed, int octaves = 3)
    {
        double value = 0, amp = 1, freq = 1, max = 0;
        for (int i = 0; i < octaves; i++)
        {
            value += Noise3D(x * freq, y * freq, z * freq, seed) * amp;
            max += amp;
            amp *= 0.5;
            freq *= 2;
        }
        return value / max;
    }
}
