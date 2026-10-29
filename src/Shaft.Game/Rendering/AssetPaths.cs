using System.Reflection;

namespace Shaft.Game.Rendering;

/// <summary>
/// Locates the game's <c>dist</c> assets at runtime. The project copies
/// <c>../../dist/**</c> into the output directory, so a normal install finds them next to
/// the executable; the fallbacks let the game run from a build tree or a source checkout.
/// </summary>
public static class AssetPaths
{
    private static string? _cached;
    private static string? _saveRoot;

    /// <summary>Absolute path of the directory holding <c>textures/</c>, <c>models/</c>, <c>sounds/</c>.</summary>
    public static string DistRoot => _cached ??= Resolve();

    /// <summary>Where saves live. Overridable so tests and <c>--save-dir</c> can redirect it.</summary>
    public static string SaveRoot
    {
        get => _saveRoot ??= DefaultSaveRoot();
        set => _saveRoot = value;
    }

    private static string Resolve()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "dist"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "dist"),   // bin/Debug/net8.0
            Path.Combine(FindRepoRoot() ?? "", "dist"),
            Path.Combine(Directory.GetCurrentDirectory(), "dist"),
        };

        foreach (string c in candidates)
        {
            if (c.Length == 0) continue;
            string full = Path.GetFullPath(c);
            if (Directory.Exists(Path.Combine(full, "textures"))) return full;
        }
        // Nothing found: return the preferred location anyway so error messages are useful.
        return Path.GetFullPath(candidates[0]);
    }

    /// <summary>
    /// Saves go next to the executable when it is writable (a normal install) and into
    /// <c>%LOCALAPPDATA%/ShaftAdditionality</c> otherwise, so a read-only program directory
    /// never makes the game unsaveable.
    /// </summary>
    private static string DefaultSaveRoot()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "saves");
        try
        {
            Directory.CreateDirectory(local);
            string probe = Path.Combine(local, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        }
        catch
        {
            string appData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                          ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/tmp", ".local", "share");

            string fallback = Path.Combine(appData, "ShaftAdditionality", "saves");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string? FindRepoRoot()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "dist", "textures"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
