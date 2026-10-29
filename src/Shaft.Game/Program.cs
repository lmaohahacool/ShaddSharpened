using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Shaft.Engine.Config;
using Shaft.Game;
using Shaft.Game.Rendering;

// ---------------------------------------------------------------------------
// Shaft Additionality — C# / Silk.NET port of dist/ (three.js + DOM).
//
//   ShaftAdditionality                  title screen, then play
//   ShaftAdditionality --selftest       headless verification, exit code = failures
//   ShaftAdditionality --help           usage
// ---------------------------------------------------------------------------

const string GameVersion = "Late Alpha 2.0.2";        // core.js VERSION

var options = new GameOptions();
bool selfTest = false;
bool forceStart = false;
bool help = false;
string? saveDir = null;
string? spawnName = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--selftest": selfTest = true; break;
        case "--help":
        case "-h": help = true; break;
        case "--start": forceStart = true; break;
        case "--fullscreen": options.Fullscreen = true; break;
        case "--no-audio": options.DisableAudio = true; break;
        case "--js-quirks":
            // Reproduce the two browser-build bugs bit-for-bit instead of the fixed art.
            options.HeldItemUseWholeAtlasSquash = true;
            options.ItemsUseFlatColourIcons = true;
            break;
        case "--save-dir": saveDir = Value(args, ref i); break;
        case "--seed": options.InitialSeed = Int(Value(args, ref i)); break;
        case "--slot": options.InitialSlot = Int(Value(args, ref i)); break;
        case "--blend": options.BiomeBlend = Math.Clamp(Int(Value(args, ref i)) ?? 4, 0, 16); break;
        case "--spawn": spawnName = Value(args, ref i); break;
        case "--width": options.WindowWidth = Int(Value(args, ref i)) ?? options.WindowWidth; break;
        case "--height": options.WindowHeight = Int(Value(args, ref i)) ?? options.WindowHeight; break;
        case "--gl":
        {
            string? v = Value(args, ref i);
            int dot = v?.IndexOf('.') ?? -1;
            if (v is not null && dot > 0)
            {
                options.MajorVersion = Int(v[..dot]) ?? options.MajorVersion;
                options.MinorVersion = Int(v[(dot + 1)..]) ?? options.MinorVersion;
            }
            break;
        }
        case "--mesh-budget": options.MeshBudgetPerFrame = Math.Max(1, Int(Value(args, ref i)) ?? 4); break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}  (try --help)");
            return 2;
    }
}

if (help)
{
    PrintUsage();
    return 0;
}

if (saveDir is not null) AssetPaths.SaveRoot = Path.GetFullPath(saveDir);
if (spawnName is not null)
{
    options.SpawnBiome = SpawnBiome(spawnName);
    if (options.SpawnBiome is null)
    {
        Console.Error.WriteLine($"unknown spawn biome '{spawnName}' (forest|desert|mountain|plains|snowy|oldgrowth)");
        return 2;
    }
}
if (forceStart || options.InitialSeed is not null || options.InitialSlot is not null)
    options.SkipTitleScreen = true;

if (selfTest) return SelfTest.Run(Console.Out);

try
{
    Console.WriteLine($"Shaft Additionality {GameVersion} — C# / Silk.NET port");
    Console.WriteLine($"  assets  : {AssetPaths.DistRoot}");
    Console.WriteLine($"  saves   : {AssetPaths.SaveRoot}");
    Console.WriteLine($"  window  : {options.WindowWidth}x{options.WindowHeight}" +
                      (options.Fullscreen ? " fullscreen" : "") +
                      (options.DisableAudio ? " (no audio)" : ""));
    if (!Directory.Exists(Path.Combine(AssetPaths.DistRoot, "textures")))
    {
        Console.Error.WriteLine("  !! the dist/ asset folder was not found; textures will be missing.");
    }

    // main.js asked three.js for a WebGL context; here we ask for the newest core profile the
    // driver will give us and step down if it refuses. The shaders are GLSL 3.30, so 3.3 is
    // the floor and anything above it is a bonus (4.5 is what the SPH compute work will need).
    Window.ShouldLoadFirstPartyPlatforms(true);
    (int Major, int Minor, int Samples)[] attempts =
    {
        (options.MajorVersion, options.MinorVersion, 4),
        (4, 1, 4),
        (3, 3, 0),
    };

    Exception? lastError = null;
    foreach ((int major, int minor, int samples) in attempts)
    {
        IWindow? window = null;
        try
        {
            window = CreateWindow(options, major, minor, samples);
            Console.WriteLine($"  context : OpenGL {major}.{minor} core" + (samples > 0 ? $", {samples}x MSAA" : ""));
            using (var host = new GameHost(window, options))
            {
                window.Run();
                if (host.Warnings.Count > 0)
                {
                    Console.WriteLine("  warnings:");
                    foreach (string w in host.Warnings) Console.WriteLine("    - " + w);
                }
            }
            window.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            lastError = ex;
            Console.Error.WriteLine($"  OpenGL {major}.{minor} failed: {ex.Message}");
            try { window?.Dispose(); } catch { /* nothing else to do */ }
        }
    }

    return Fatal("window creation", lastError);
}
catch (Exception ex)
{
    return Fatal("startup", ex);
}

// ------------------------------------------------------------------ local helpers

static IWindow CreateWindow(GameOptions o, int major, int minor, int samples)
{
    WindowOptions w = WindowOptions.Default;
    w.Title = "Shaft Additionality";
    w.Size = new Vector2D<int>(Math.Max(320, o.WindowWidth), Math.Max(240, o.WindowHeight));
    w.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
                            new APIVersion(major, minor));
    w.VSync = true;
    w.ShouldSwapAutomatically = true;
    w.IsEventDriven = false;
    w.PreferredDepthBufferBits = 24;
    if (samples > 0) w.Samples = samples;         // three.js: antialias: true
    w.WindowState = o.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
    w.WindowBorder = o.Fullscreen ? WindowBorder.Hidden : WindowBorder.Resizable;
    return Window.Create(w);
}

static string? Value(string[] args, ref int i)
{
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"missing value for {args[i]}");
        return null;
    }
    return args[++i];
}

static int? Int(string? s) => int.TryParse(s, out int v) ? v : null;

/// <summary>core.js <c>SPAWN_BIOME</c> URL-parameter mapping.</summary>
static int? SpawnBiome(string name) => name.Trim().ToLowerInvariant() switch
{
    "plains" => Biome.PLAINS,
    "forest" => Biome.FOREST,
    "mountain" => Biome.MOUNTAIN,
    "desert" => Biome.DESERT,
    "snowy" or "snow" => Biome.SNOWY,
    "oldgrowth" or "old_growth" or "ogg" => Biome.OLD_GROWTH,
    _ => null,
};

static void PrintUsage()
{
    Console.WriteLine("""
        Shaft Additionality — C# / Silk.NET port

        Usage: ShaftAdditionality [options]

          --selftest          run the headless engine + asset checks and exit
          --start             skip the title screen (new world, or --slot to load)
          --seed <n>          world seed for --start (default: random, 0..999999)
          --slot <n>          save slot to load with --start
          --blend <n>         biome blend radius 0..16 (default 4)
          --spawn <name>      force the spawn biome: forest|desert|mountain|plains|snowy|oldgrowth
          --save-dir <path>   where worlds are stored (default: ./saves next to the exe)
          --width <n>         window width  (default 1280)
          --height <n>        window height (default 720)
          --fullscreen        start in fullscreen
          --gl <maj.min>      requested OpenGL version (default 4.5, falls back to 3.3)
          --mesh-budget <n>   chunk meshes built per frame (default 4, as in chunks.js)
          --no-audio          do not open an OpenAL device
          --js-quirks         reproduce the browser build's two texture bugs exactly

        Keys: WASD move, Space jump / double-tap to fly (creative), Shift sneak,
              Ctrl sprint, Ctrl+S save, E inventory, U gamemode, M music, T chat,
              / command, N save and back to title, Escape release the mouse,
              1-9 or wheel select the hotbar, left click mine/attack, right click use.
        """);
}

/// <summary>Last resort for a double-clicked exe: its console window closes before anyone
/// can read the error, so the details also go to crash.log next to the executable and the
/// window is held open until a key is pressed.</summary>
static int Fatal(string context, Exception? ex)
{
    string text = $"Shaft Additionality crashed during {context} at {DateTime.UtcNow:O}\n" +
                  (ex?.ToString() ?? "unknown error") + "\n";
    Console.Error.Write(text);
    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), text); }
    catch { /* best effort: the log is a bonus, the console text is the contract */ }
    if (Environment.UserInteractive && !Console.IsInputRedirected)
    {
        Console.Error.WriteLine("press any key to close this window...");
        try { Console.ReadKey(true); } catch { /* no console attached */ }
    }
    return 1;
}
