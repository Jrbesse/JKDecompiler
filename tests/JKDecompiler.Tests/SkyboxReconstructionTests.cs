using System.Numerics;
using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// SkyboxReconstruction (M2.5 stage 2 sky bake). Synthetic BspData only —
/// no copyrighted fixtures. The real sky brushes survive decompilation; this
/// code finds their sky texture names and re-textures the sides to the
/// overlay skylight shader.
/// </summary>
public class SkyboxReconstructionTests
{
    // A world model with one all-sky brush (6 sides, skies/yavin) and one
    // plain brush (6 sides, a wall texture). Mirrors academy1's topology.
    private static BspData MakeBsp()
    {
        var bsp = new BspData();
        bsp.Models.Add(new BspModel { FirstBrush = 0, NumBrushes = 2 });

        bsp.Shaders.Add(new BspShader { Name = "textures/yavin/temple_basicwall3" }); // 0
        bsp.Shaders.Add(new BspShader                                        // 1
        {
            Name = "textures/skies/yavin",
            SurfaceFlags = SkyboxReconstruction.MF_SKY,
        });

        // plane per side is irrelevant to the name tests; provide distinct planes
        for (int i = 0; i < 12; i++)
            bsp.Planes.Add(new BspPlane
            {
                Normal = new Vector3(0, 0, 1),
                Distance = i,
            });

        void AddBrush(int firstSide, int[] shaderIndices)
        {
            int brushIndex = bsp.Brushes.Count;
            bsp.Brushes.Add(new BspBrush { FirstSide = firstSide, NumSides = shaderIndices.Length, ShaderIndex = -1 });
            foreach (var sh in shaderIndices)
                bsp.BrushSides.Add(new BspBrushSide { PlaneIndex = firstSide + bsp.BrushSides.Count, ShaderIndex = sh });
        }

        // brush 0: all-sky (6 sides shader 1)
        AddBrush(0, new[] { 1, 1, 1, 1, 1, 1 });
        // brush 1: plain wall (6 sides shader 0)
        AddBrush(6, new[] { 0, 0, 0, 0, 0, 0 });
        return bsp;
    }

    [Fact]
    public void SkyTextureNames_FindsSkyTexture_MostUsedFirst()
    {
        var bsp = MakeBsp();
        var names = SkyboxReconstruction.SkyTextureNames(bsp);
        Assert.Equal(new[] { "skies/yavin" }, names);
    }

    [Fact]
    public void SkyTextureNames_NoSky_ReturnsEmpty()
    {
        var bsp = MakeBsp();
        // clear the sky flag
        bsp.Shaders[1].SurfaceFlags = 0;
        var names = SkyboxReconstruction.SkyTextureNames(bsp);
        Assert.Empty(names);
    }

    [Fact]
    public void SkyTextureNames_MixedBrush_CarriesSkySides()
    {
        var bsp = MakeBsp();
        // make brush 1 mixed: one sky side
        bsp.BrushSides[6].ShaderIndex = 1; // first side of brush 1 → sky
        var names = SkyboxReconstruction.SkyTextureNames(bsp);
        Assert.Contains("skies/yavin", names);
    }

    [Fact]
    public void SkyImageBase_DominantSkyShader()
    {
        var bsp = MakeBsp();
        Assert.Equal("textures/skies/yavin", SkyboxReconstruction.SkyImageBase(bsp));
    }

    [Fact]
    public void RetextureSky_ReplacesSkySidesOnly()
    {
        // map text: a worldspawn with one sky side (skies/yavin) and one wall side
        string map = """
            {
                "classname" "worldspawn"
                {
                    ( 0 0 0 ) ( 10 0 0 ) ( 10 10 0 ) skies/yavin 0 0 0 1 1 0 0 0
                    ( 0 0 0 ) ( 10 0 0 ) ( 10 10 0 ) yavin/temple_basicwall3 0 0 0 1 1 0 0 0
                }
            }

            {
                "classname" "misc_model"
                "model" "models/machines/light1.md3"
            }
            """;
        var (text, count) = SkyboxReconstruction.RetextureSky(map, new[] { "skies/yavin" });
        Assert.Equal(1, count);
        Assert.Contains("fit/sky", text);
        Assert.DoesNotContain("skies/yavin", text);
        // wall side untouched
        Assert.Contains("yavin/temple_basicwall3", text);
        // other entity untouched
        Assert.Contains("misc_model", text);
    }

    [Fact]
    public void RetextureSky_IndentedSideLine_ExporterFormat()
    {
        // the exporter writes side lines tab-indented: "\t\t( x y z ) ... "
        string map = "\r\n" +
            "{\r\n" +
            "\t\"classname\" \"worldspawn\"\r\n" +
            "\t{\r\n" +
            "\t\t( 0.0 0.0 0.0 ) ( 10.0 0.0 0.0 ) ( 10.0 10.0 0.0 ) skies/yavin 0.000000 0.000000 0.000000 1.000000 1.000000 0 0 0\r\n" +
            "\t}\r\n" +
            "}\r\n";
        var (text, count) = SkyboxReconstruction.RetextureSky(map, new[] { "skies/yavin" });
        Assert.Equal(1, count);
        Assert.Contains("fit/sky", text);
        Assert.DoesNotContain("skies/yavin", text);
        // indentation and CRLF preserved
        Assert.Contains("\t\t( 0.0 0.0 0.0 ) ( 10.0 0.0 0.0 ) ( 10.0 10.0 0.0 ) fit/sky 0.000000 0.000000 0.000000 1.000000 1.000000 0 0 0\r\n", text);
    }

    [Fact]
    public void RetextureSky_NoMatch_LeavesTextUnchanged()
    {
        string map = """
            {
                "classname" "worldspawn"
                {
                    ( 0 0 0 ) ( 10 0 0 ) ( 10 10 0 ) yavin/temple_basicwall3 0 0 0 1 1 0 0 0
                }
            }
            """;
        var (text, count) = SkyboxReconstruction.RetextureSky(map, new[] { "skies/yavin" });
        Assert.Equal(0, count);
        Assert.Equal(map, text);
    }

    [Fact]
    public void RetextureSky_EmptyList_LeavesTextUnchanged()
    {
        string map = """
            {
                "classname" "worldspawn"
                {
                    ( 0 0 0 ) ( 10 0 0 ) ( 10 10 0 ) skies/yavin 0 0 0 1 1 0 0 0
                }
            }
            """;
        var (text, count) = SkyboxReconstruction.RetextureSky(map, Array.Empty<string>());
        Assert.Equal(0, count);
        Assert.Equal(map, text);
    }

    [Fact]
    public void InstallShader_WritesOverlayWithSkyLightKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jk_skytest_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = SkyboxReconstruction.InstallShader(dir, "textures/skies/yavin");
            Assert.Equal(Path.Combine(dir, "base", "shaders", "fit_sky.shader"), path);
            string text = File.ReadAllText(path);
            Assert.Contains("textures/fit/sky", text);
            Assert.Contains("q3map_skyLight", text);
            Assert.Contains("textures/skies/yavin 512 -", text);
            // idempotent: same content → same bytes
            string path2 = SkyboxReconstruction.InstallShader(dir, "textures/skies/yavin");
            Assert.Equal(text, File.ReadAllText(path2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
