using Shaft.Engine.Config;
using Shaft.Engine.Maths;

namespace Shaft.Engine.Game;

/// <summary>
/// Port of <c>dist/daynight.js</c>: the 600-second day cycle, sun direction, sky and
/// fog colour, and the three light intensities the JS scene used.
/// </summary>
/// <remarks>
/// Sky colours are blended in linear space, because three.js <c>Color.setHex</c>
/// converts sRGB hex to the working (linear-sRGB) space before <c>lerp</c> runs.
/// <see cref="SkyColorSrgb"/> provides the display-ready value for a framebuffer
/// without an sRGB attachment.
/// </remarks>
public sealed class DayNightSystem
{
    // core.js sky constants
    private static readonly double[] DaySky = HexToLinear(0x87ceeb);
    private static readonly double[] NightSky = HexToLinear(0x0a0a1a);
    private static readonly double[] DuskSky = HexToLinear(0xff8844);

    /// <summary>Fog near/far from <c>new THREE.Fog(0x87ceeb, 48, 72)</c>.</summary>
    public const float FOG_NEAR = 48f;
    public const float FOG_FAR = 72f;

    public float WorldTime { get; private set; }

    /// <summary>Normalised sun direction; <c>core.js</c> seeded it as (0.4,0.6,0.5).</summary>
    public float SunX, SunY, SunZ;

    public bool IsNightTime { get; private set; }

    public float SunIntensity { get; private set; }
    public float AmbientIntensity { get; private set; }
    public float HemisphereIntensity { get; private set; }

    /// <summary>Sky/fog colour in linear space, as three.js would hand to the renderer.</summary>
    public float SkyR, SkyG, SkyB;
    /// <summary>The same colour converted back to sRGB for a plain framebuffer.</summary>
    public float SkySrgbR, SkySrgbG, SkySrgbB;

    public DayNightSystem()
    {
        // core.js: worldTime = 600 * .25  ->  mid-morning
        WorldTime = GameConfig.DAY_LENGTH * .25f;
        SunX = .4f; SunY = .6f; SunZ = .5f;
        NormaliseSun();
        Apply(WorldTime);
    }

    public void SetWorldTime(float t) { WorldTime = t; Apply(WorldTime); }

    /// <summary>Port of <c>updateDayNight</c>.</summary>
    public void Update(float dt)
    {
        WorldTime = (WorldTime + dt) % GameConfig.DAY_LENGTH;
        Apply(WorldTime);
    }

    private void Apply(float worldTime)
    {
        double t = worldTime / GameConfig.DAY_LENGTH;
        double angle = t * Math.PI * 2 - Math.PI / 2;

        SunX = (float)(Math.Cos(angle) * .4);
        SunY = (float)Math.Sin(angle);
        SunZ = (float)(Math.Cos(angle) * .5);
        NormaliseSun();

        double sh = Math.Max(0, SunY);
        IsNightTime = SunY < -.1;

        double[] sky;
        if (SunY < -.1) sky = NightSky;
        else if (SunY < .15) sky = Lerp(NightSky, DuskSky, Noise.Smoothstep((SunY + .1) / .25));
        else if (SunY < .4) sky = Lerp(DuskSky, DaySky, Noise.Smoothstep((SunY - .15) / .25));
        else sky = DaySky;

        SkyR = (float)sky[0]; SkyG = (float)sky[1]; SkyB = (float)sky[2];
        SkySrgbR = (float)Colour.LinearToSrgb(sky[0]);
        SkySrgbG = (float)Colour.LinearToSrgb(sky[1]);
        SkySrgbB = (float)Colour.LinearToSrgb(sky[2]);

        SunIntensity = (float)Math.Max(.05, sh * 2.5);
        AmbientIntensity = (float)(.15 + sh * .9);
        HemisphereIntensity = (float)(.1 + sh * .6);
    }

    private void NormaliseSun()
    {
        double len = Math.Sqrt(SunX * (double)SunX + SunY * (double)SunY + SunZ * (double)SunZ);
        if (len < 1e-9) return;
        SunX = (float)(SunX / len);
        SunY = (float)(SunY / len);
        SunZ = (float)(SunZ / len);
    }

    /// <summary>The HUD string from daynight.js.</summary>
    public string TimeOfDayName =>
        IsNightTime ? "Night" : SunY < .4f ? "Dusk/Dawn" : "Day";

    /// <summary>0 = midnight, 0.5 = noon. Useful for the HUD clock.</summary>
    public float DayFraction => WorldTime / GameConfig.DAY_LENGTH;

    private static double[] HexToLinear(uint hex) => new[]
    {
        Colour.SrgbToLinear(((hex >> 16) & 0xFF) / 255.0),
        Colour.SrgbToLinear(((hex >> 8) & 0xFF) / 255.0),
        Colour.SrgbToLinear((hex & 0xFF) / 255.0),
    };

    private static double[] Lerp(double[] a, double[] b, double t) => new[]
    {
        a[0] + (b[0] - a[0]) * t,
        a[1] + (b[1] - a[1]) * t,
        a[2] + (b[2] - a[2]) * t,
    };
}
