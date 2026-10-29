using System.Text.Json;

namespace Shaft.Engine.Tests;

public sealed record GoldenHash3(int x, int y, int z, double v);
public sealed record GoldenHash2(int x, int y, double v);
public sealed record GoldenSample(int x, int z, int h, int b);
public sealed record GoldenColumn(int x, int z, List<int> blocks, int minY);

public sealed class GoldenSeed
{
    public int seed { get; set; }
    public List<GoldenHash3> hash3 { get; set; } = new();
    public List<GoldenHash2> hash2 { get; set; } = new();
    public List<GoldenSample> samples { get; set; } = new();
    public List<GoldenColumn> columns { get; set; } = new();
    public List<List<int>> shortGrass { get; set; } = new();
}

public sealed class GoldenFile
{
    public List<GoldenSeed> seeds { get; set; } = new();
}

/// <summary>Loads the golden values captured from the real JavaScript engine.</summary>
public static class Goldens
{
    private static GoldenFile? _cache;

    public static GoldenFile Load()
    {
        if (_cache is not null) return _cache;
        string path = Path.Combine(AppContext.BaseDirectory, "goldens.json");
        Assert.True(File.Exists(path), $"goldens.json not found at {path}");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _cache = JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(path), opts);
        Assert.NotNull(_cache);
        return _cache!;
    }
}
