using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class RoundTripTests
{
    [Fact]
    public void WriteThenReadPreservesAllLumps()
    {
        var d = SyntheticMap.Build();
        string path = Path.Combine(Path.GetTempPath(), $"jkd_rt_{Guid.NewGuid():N}.bsp");
        try
        {
            new BspWriter().Write(d, path);
            var d2 = new BspReader().Read(path);

            Assert.Equal(d.Entities.Count, d2.Entities.Count);
            Assert.Equal(d.Shaders.Count, d2.Shaders.Count);
            Assert.Equal(d.Planes.Count, d2.Planes.Count);
            Assert.Equal(d.Nodes.Count, d2.Nodes.Count);
            Assert.Equal(d.Leafs.Count, d2.Leafs.Count);
            Assert.Equal(d.LeafFaces, d2.LeafFaces);
            Assert.Equal(d.LeafBrushes, d2.LeafBrushes);
            Assert.Equal(d.Models.Count, d2.Models.Count);
            Assert.Equal(d.Brushes.Count, d2.Brushes.Count);
            Assert.Equal(d.BrushSides.Count, d2.BrushSides.Count);
            Assert.Equal(d.Vertices.Count, d2.Vertices.Count);
            Assert.Equal(d.MeshVerts, d2.MeshVerts);
            Assert.Equal(d.Faces.Count, d2.Faces.Count);
            Assert.Equal(d.Lightmaps.Count, d2.Lightmaps.Count);
            Assert.Equal(d.LightGrid.Count, d2.LightGrid.Count);
            Assert.Equal(d.LightArray.Data.Length, d2.LightArray.Data.Length);

            // spot-check payload equality
            for (int i = 0; i < d.Vertices.Count; i++)
            {
                Assert.Equal(d.Vertices[i].Position, d2.Vertices[i].Position);
                Assert.Equal(d.Vertices[i].Lightmap, d2.Vertices[i].Lightmap);
                Assert.Equal(d.Vertices[i].Color, d2.Vertices[i].Color);
            }
            Assert.Equal(d.Faces[0].LightmapNum, d2.Faces[0].LightmapNum);
            Assert.Equal(d.Faces[0].LightmapStyles, d2.Faces[0].LightmapStyles);
            Assert.Equal(d.Lightmaps[0].Data, d2.Lightmaps[0].Data);
            Assert.Equal(d.LightGrid[0].Ambient, d2.LightGrid[0].Ambient);
            Assert.Equal(d.LightArray.Data, d2.LightArray.Data);
            Assert.Equal(d.RawEntities, d2.RawEntities);

            // and the re-read data must pass validation
            var r = BspValidator.Validate(d2);
            Assert.True(r.IsClean, r.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
