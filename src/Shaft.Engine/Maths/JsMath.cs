namespace Shaft.Engine.Maths;

/// <summary>
/// JavaScript numeric semantics that C# does NOT reproduce natively.
///
/// Every helper here exists because a literal transliteration of the code in
/// <c>dist/</c> silently produces wrong results in C#. See
/// <c>notes/MIGRATION-BRIEF.md</c> §4 and §A for the measurements that forced
/// each of these.
/// </summary>
public static class JsMath
{
    /// <summary>
    /// ECMAScript <c>ToInt32</c> (ECMA-262 §7.1.8), i.e. exactly what the
    /// JavaScript <c>|0</c> operator does.
    /// </summary>
    /// <remarks>
    /// This is NOT the same as a C# <c>(int)</c> cast. A C# cast of a double
    /// outside <c>int</c> range is unspecified and, on x86/x64, saturates to
    /// <c>int.MinValue</c> via the <c>cvttsd2si</c> overflow sentinel. Measured:
    /// <code>
    /// (int)1.442695E+018 == -2147483648      // C#
    /// ToInt32Js(1.442695E+018) == -144211712 // JavaScript
    /// </code>
    /// Using the cast instead of this helper collapses the world hash to a
    /// constant and yields flat, featureless terrain — with no compile error.
    /// </remarks>
    public static int ToInt32(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;

        double t = Math.Truncate(value);   // step 3: truncate toward zero
        t %= 4294967296.0;                 // step 4: modulo 2^32 (signed remainder)
        if (t < 0) t += 4294967296.0;      // step 5: into [0, 2^32)
        if (t <= 2147483647.0) return (int)t;
        return (int)(t - 4294967296.0);    // step 6: wrap into [-2^31, 0)
    }

    /// <summary>Equivalent of JavaScript <c>Math.imul</c>: wrapping 32-bit multiply.</summary>
    public static int IMul(int a, int b) => unchecked(a * b);

    /// <summary>JavaScript <c>&gt;&gt;&gt;</c>: logical (zero-filling) right shift.</summary>
    public static uint UnsignedShiftRight(int value, int count) => (uint)value >> count;

    /// <summary>
    /// <c>Math.floor(a / b)</c> for integers — the floor division JavaScript gets
    /// implicitly via <c>Math.floor(x / 16)</c>. C# integer division truncates
    /// toward zero, which differs for negative operands and would misalign every
    /// chunk west/south of the origin.
    /// </summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    /// <summary><c>a - Math.floor(a / b) * b</c> — always in <c>[0, b)</c> for b &gt; 0.</summary>
    public static int FloorMod(int a, int b) => a - FloorDiv(a, b) * b;

    /// <summary>JavaScript <c>Math.floor</c> on a double (identical semantics, named for parity).</summary>
    public static double Floor(double v) => Math.Floor(v);

    /// <summary>Clamp matching <c>Math.min(hi, Math.max(lo, v))</c>.</summary>
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
