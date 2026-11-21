using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class ValidatorTests
{
    private static BspData Synthetic() => SyntheticMap.Build();

    [Fact]
    public void SyntheticMapIsClean()
    {
        var d = Synthetic();
        var r = BspValidator.Validate(d);
        Assert.True(r.IsClean, r.ToString());
    }

    [Fact]
    public void DetectsFaceVertexRangeOverflow()
    {
        var d = Synthetic();
        d.Faces[0].FirstVertexIndex = 1000;
        var r = BspValidator.Validate(d);
        Assert.False(r.IsClean);
        Assert.Contains(r.Errors, e => e.Contains("face 0") && e.Contains("vertex range"));
    }

    [Fact]
    public void DetectsLightmapIndexOutOfRange()
    {
        var d = Synthetic();
        d.Faces[0].LightmapNum[0] = 42;
        var r = BspValidator.Validate(d);
        Assert.False(r.IsClean);
        Assert.Contains(r.Errors, e => e.Contains("lightmap 42"));
    }

    [Fact]
    public void DetectsNodeChildOutOfRange()
    {
        var d = Synthetic();
        d.Nodes[0].Children[1] = 5;
        var r = BspValidator.Validate(d);
        Assert.False(r.IsClean);
        Assert.Contains(r.Errors, e => e.Contains("node 0.child1"));
    }

    [Fact]
    public void DetectsLightArrayGridCountMismatch()
    {
        // the engine treats this as a warning (disables the grid), not an error
        var d = Synthetic();
        d.LightArray.Data = new ushort[1];
        var r = BspValidator.Validate(d);
        Assert.Contains(r.Warnings, w => w.Contains("light grid array mismatch"));
    }

    [Fact]
    public void DetectsMissingWorldspawn()
    {
        var d = Synthetic();
        d.Entities = d.Entities.Where(e => e.ClassName != "worldspawn").ToList();
        var r = BspValidator.Validate(d);
        Assert.False(r.IsClean);
        Assert.Contains(r.Errors, e => e.Contains("worldspawn"));
    }

    [Fact]
    public void DetectsPatchControlVertMismatch()
    {
        var d = Synthetic();
        var patch = new BspFace
        {
            ShaderIndex = 0, FogIndex = -1, Type = FaceType.Patch,
            FirstVertexIndex = 0, NumVertices = 3,
            PatchWidth = 3, PatchHeight = 3 // expects 3*3 = 9 control verts
        };
        d.Faces.Add(patch);
        var r = BspValidator.Validate(d);
        Assert.Contains(r.Errors, e => e.Contains("control verts"));
    }

    [Fact]
    public void WarnsOnZeroLightEntities()
    {
        var d = Synthetic();
        var r = BspValidator.Validate(d);
        Assert.Contains(r.Warnings, w => w.Contains("0 light entities"));
    }
}
