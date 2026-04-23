using JKDecompiler.Core;
using Xunit;
using System.IO;

namespace JKDecompiler.Tests;

public class UnitTest1
{
    [Fact]
    public void TestReadBsp()
    {
        var bspPath = Path.Combine("..", "..", "..", "..", "..", "t1_sour.bsp");
        Assert.True(File.Exists(bspPath), $"BSP file not found at {Path.GetFullPath(bspPath)}");

        var reader = new BspReader();
        var data = reader.Read(bspPath);

        Assert.NotNull(data);
        Assert.NotEmpty(data.Entities);
        Assert.NotEmpty(data.Shaders);
        Assert.NotEmpty(data.Planes);
        Assert.NotEmpty(data.Vertices);
        Assert.NotEmpty(data.Faces);
        Assert.NotEmpty(data.Lightmaps);
        Assert.NotEmpty(data.LightGrid);
        Assert.NotNull(data.Fogs);
        Assert.NotNull(data.Visibility);
        Assert.NotEmpty(data.LightArray);
        // Assert.NotEmpty(data.Decals);

        var playerStarts = data.Entities.FindAll(e => e.ClassName.Equals("info_player_start", System.StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(playerStarts);

        var exporter = new MapExporter();
        var mapPath = "test_export.map";
        exporter.Export(data, mapPath);
        Assert.True(File.Exists(mapPath));
        Assert.True(new FileInfo(mapPath).Length > 0);
    }
}
