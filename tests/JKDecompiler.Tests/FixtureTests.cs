using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// Real-map tests. Copyrighted fixtures are NOT in the repo (removed in
/// 909ee19); point JKA_BSP_FIXTURES at a directory of .bsp files to run
/// (e.g. the SP map set pulled from the GOG paks). Skipped when unset so
/// CI stays green with synthetic data only.
/// </summary>
public class FixtureTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("JKA_BSP_FIXTURES");

    private static string[] FixtureFiles() =>
        Dir == null ? Array.Empty<string>()
        : Directory.GetFiles(Dir, "*.bsp", SearchOption.AllDirectories);

    [Fact]
    public void EveryFixtureReadsAndValidatesClean()
    {
        if (Dir == null)
        {
            // explicit skip message for local runs
            Assert.True(true, "JKA_BSP_FIXTURES not set — fixture tests skipped");
            return;
        }
        var files = FixtureFiles();
        Assert.True(files.Length > 0, $"no .bsp files under {Dir}");

        var failures = new List<string>();
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                var d = new BspReader().Read(file);
                var r = BspValidator.Validate(d);
                if (!r.IsClean)
                    failures.Add($"{name}:\n{r}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} fixtures failed:\n{string.Join("\n---\n", failures.Take(5))}");
    }

    [Fact]
    public void FixtureLightmapsAreExtractable()
    {
        if (Dir == null) return;
        var files = FixtureFiles();
        Assert.True(files.Length > 0, $"no .bsp files under {Dir}");

        int blocks = 0;
        int litFaces = 0;
        foreach (var file in files)
        {
            var d = new BspReader().Read(file);
            blocks += d.Lightmaps.Count;
            litFaces += d.Faces.Count(f => f.LightmapNum[0] >= 0);
        }
        Assert.True(blocks > 0, "no lightmap blocks found in any fixture");
        Assert.True(litFaces > 0, "no lit faces found in any fixture");
    }
}
