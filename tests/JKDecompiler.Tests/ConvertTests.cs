using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class ConvertTests
{
    [Fact]
    public void MapFileStatsCountsEntitiesAndKeywords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkd_{Guid.NewGuid():N}.map");
        File.WriteAllText(path, """
            {
                "classname" "worldspawn"
                brushDef
                ( 0 0 0 ) ( 1 0 0 ) ( 0 1 0 ) ( 0 0 1 )
                ( 0 128 0 ) ( 128 128 0 ) ( 128 0 0 ) ( 128 0 128 )
                {
                    ( 128 0 0 ) ( 0 0 0 ) ( 0 0 128 )
                    "textures/floor" 0 0 1 1 0 0 1 1
                }
            }
            {
                "classname" "target_light"
                "origin" "0 64 128"
            }
            {
                "classname" "misc_model"
                "model" "models/misc/lamp.md3"
                patchDef2
            }
            """);
        try
        {
            var s = MapFileStats.Parse(path);
            Assert.Equal(3, s.Entities);
            Assert.Equal(1, s.Patches);
            Assert.Equal(1, s.Brushes);
            Assert.True(s.BracesBalanced);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MapFileStatsKeywordNotInsideOtherWord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkd_{Guid.NewGuid():N}.map");
        File.WriteAllText(path, """
            {
                "classname" "worldspawn"
                "notbrushDef" "x"
                "patchDef2_backup" "y"
            }
            """);
        try
        {
            var s = MapFileStats.Parse(path);
            Assert.Equal(1, s.Entities);
            Assert.Equal(0, s.Patches);
            Assert.Equal(0, s.Brushes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MapFileStatsDetectsUnbalanced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkd_{Guid.NewGuid():N}.map");
        File.WriteAllText(path, "{ \"a\" \"b\" { }");
        try
        {
            var s = MapFileStats.Parse(path);
            Assert.False(s.BracesBalanced);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MapFileStatsCountsOldStyleQ3map2Brushes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkd_{Guid.NewGuid():N}.map");
        File.WriteAllText(path, """
            {
                "classname" "worldspawn"
                // brush 0
                {
                    ( -1600.000 128.000 656.000 ) ( -1600.000 128.000 640.000 ) ( -1600.000 144.000 640.000 ) system/caulk 0 0 0 1 1 0 0 0
                    ( -1216.000 144.000 656.000 ) ( -1216.000 144.000 640.000 ) ( -1216.000 128.000 640.000 ) system/caulk 0 0 0 1 1 0 0 0
                }
            }
            {
                "origin" "0 64 128"
            }
            """);
        try
        {
            var s = MapFileStats.Parse(path);
            Assert.Equal(2, s.Entities);
            Assert.Equal(1, s.Brushes);
            Assert.Equal(2, s.BrushSides);
        }
        finally { File.Delete(path); }
    }
}
