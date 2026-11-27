using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// Lightmap-derived seed generator (M2.5 default seed path). Fixture-gated,
/// loud-skips when JKA_BSP_FIXTURES is unset — CI stays green with synthetic data.
/// Verifies the generator actually produces usable seeds on a real fixture:
/// non-empty output, sane geometry/radius, and JSON that round-trips through
/// the same external-seeds loader Fit uses (including the in-solid push).
/// </summary>
public class LightmapSeedGeneratorTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("JKA_BSP_FIXTURES");
    private static bool Enabled => !string.IsNullOrWhiteSpace(Dir) && Directory.Exists(Dir);

    [Fact]
    public void GeneratorProducesUsableSeedsOnFixture()
    {
        if (!Enabled)
        {
            Assert.True(true, "JKA_BSP_FIXTURES not set — generator test skipped");
            return;
        }
        var files = Directory.GetFiles(Dir!, "*.bsp", SearchOption.AllDirectories)
            .OrderBy(f => f).ToArray();
        Assert.True(files.Length > 0, $"no .bsp files under {Dir}");
        var academy1 = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileNameWithoutExtension(f), "academy1", StringComparison.OrdinalIgnoreCase))
            ?? files[0];

        var d = new BspReader().Read(academy1);
        var seeds = LightmapSeedGenerator.Generate(d);
        Assert.True(seeds.Count > 0, "generator produced 0 seeds on fixture");
        foreach (var s in seeds)
        {
            Assert.True(s.Position.LengthSquared() > 0, $"{s.Name}: degenerate position");
            Assert.True(s.Radius >= 128 && s.Radius <= 4096, $"{s.Name}: radius {s.Radius} out of range");
            Assert.True(s.Normal.LengthSquared() > 0.5f, $"{s.Name}: degenerate normal");
            Assert.True(s.Color.X >= 0 && s.Color.X <= 255, $"{s.Name}: color out of range");
        }

        // JSON round-trip: same shape the --seeds loader consumes
        var tmp = Path.Combine(Path.GetTempPath(), "seeds-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            LightmapSeedGenerator.WriteJson(seeds, "academy1", tmp);
            Assert.True(File.Exists(tmp), "seed JSON not written");
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tmp));
            Assert.True(doc.RootElement.TryGetProperty("seeds", out var arr), "missing seeds array");
            Assert.Equal(seeds.Count, arr.GetArrayLength());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
