namespace Shaft.Engine.Maths;

/// <summary>
/// sRGB transfer functions, matching three.js's <c>ColorManagement</c>.
/// </summary>
/// <remarks>
/// These exist because three.js <c>Color.setHex</c> converts an sRGB hex value into
/// the working (linear-sRGB) colour space, <c>Color.lerp</c> then blends in linear
/// space, and <c>getHex</c> converts back. Both the biome grass-tint blend in
/// <c>worldgen.js</c> and the sky-colour blend in <c>daynight.js</c> go through that
/// path, so blending in raw sRGB would give visibly different colours.
/// </remarks>
public static class Colour
{
    public static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double c)
        => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    /// <summary>Splits a 0xRRGGBB value into linear components.</summary>
    public static void HexToLinear(uint hex, out float r, out float g, out float b)
    {
        r = (float)SrgbToLinear(((hex >> 16) & 0xFF) / 255.0);
        g = (float)SrgbToLinear(((hex >> 8) & 0xFF) / 255.0);
        b = (float)SrgbToLinear((hex & 0xFF) / 255.0);
    }

    /// <summary>Packs linear components back into 0xRRGGBB, rounding as three.js does.</summary>
    public static uint LinearToHex(double r, double g, double b)
    {
        int ri = (int)Math.Round(Math.Clamp(LinearToSrgb(r), 0, 1) * 255);
        int gi = (int)Math.Round(Math.Clamp(LinearToSrgb(g), 0, 1) * 255);
        int bi = (int)Math.Round(Math.Clamp(LinearToSrgb(b), 0, 1) * 255);
        return ((uint)ri << 16) | ((uint)gi << 8) | (uint)bi;
    }
}
