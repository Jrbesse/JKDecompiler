using System.Numerics;
using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

public class LightRecoveryTests
{
    [Fact]
    public void LatLongRoundTripWithinQuantization()
    {
        var dirs = new[]
        {
            new Vector3(0, 0, 1),
            new Vector3(0, 0, -1),
            new Vector3(1, 0, 0),
            new Vector3(-1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, -1, 0),
            new Vector3(0.3f, -0.5f, 0.8f),
            new Vector3(0.1f, 0.2f, -0.97f),
            new Vector3(0.8f, 0.6f, 0.1f),
        };
        foreach (var raw in dirs)
        {
            var d = Vector3.Normalize(raw);
            var bytes = LightRecovery.NormalToLatLong(d);
            var dec = LightRecovery.DecodeLatLong(bytes);
            float cos = Vector3.Dot(d, dec);
            float angleDeg = (float)(Math.Acos(Math.Clamp(cos, -1f, 1f)) * 180.0 / Math.PI);
            Assert.True(angleDeg < 4.0f, $"dir {d} -> {bytes[0]},{bytes[1]} -> {dec} (off {angleDeg:F2} deg)");
        }
    }

    [Fact]
    public void RecoversBothSyntheticLights()
    {
        var data = SyntheticMap.BuildLightRecovery();
        var r = new LightRecovery().Recover(data);
        var truth = SyntheticMap.LightRecoveryTruth();

        Assert.True(r.GridValid, r.GridNote);
        Assert.Equal(48 * 48 * 4, r.Corners);
        Assert.True(r.SamplesUsed > 100, $"only {r.SamplesUsed} usable samples");

        // every truth light has a candidate nearby
        foreach (var t in truth)
        {
            var best = r.Lights.OrderBy(l => Vector3.Distance(l.Position, t.Pos)).First();
            float dist = Vector3.Distance(best.Position, t.Pos);
            Assert.True(dist <= 32f, $"truth {t.Pos} nearest candidate {best.Position} off by {dist:F1}");
        }
        // no spurious accepted light
        foreach (var l in r.Lights)
        {
            float best = truth.Min(t => Vector3.Distance(l.Position, t.Pos));
            Assert.True(best <= 32f, $"candidate {l.Position} is not near any truth light");
        }
    }

    [Fact]
    public void RecoveredColorsAndLevelsApproximateTruth()
    {
        var data = SyntheticMap.BuildLightRecovery();
        var r = new LightRecovery().Recover(data);
        var truth = SyntheticMap.LightRecoveryTruth();
        foreach (var t in truth)
        {
            var best = r.Lights.OrderBy(l => Vector3.Distance(l.Position, t.Pos)).First();
            // ratio-preserved color within 10% per channel
            Assert.True(Math.Abs(best.Color.X - t.Color.X) <= 25.5f, $"R {best.Color.X} vs {t.Color.X}");
            Assert.True(Math.Abs(best.Color.Y - t.Color.Y) <= 25.5f, $"G {best.Color.Y} vs {t.Color.Y}");
            Assert.True(Math.Abs(best.Color.Z - t.Color.Z) <= 25.5f, $"B {best.Color.Z} vs {t.Color.Z}");
            Assert.Equal(t.Style, best.Style);
            if (best.Level.HasValue)
                Assert.True(Math.Abs(best.Level.Value - t.Level) <= 0.35 * t.Level,
                    $"level {best.Level} vs truth {t.Level}");
        }
    }

    [Fact]
    public void RoundTripThroughWriterStillRecovers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jkd_lr_{Guid.NewGuid():N}.bsp");
        try
        {
            new BspWriter().Write(SyntheticMap.BuildLightRecovery(), path);
            var d = new BspReader().Read(path);
            var r = new LightRecovery().Recover(d);
            var truth = SyntheticMap.LightRecoveryTruth();
            Assert.True(r.GridValid, r.GridNote);
            foreach (var t in truth)
            {
                var best = r.Lights.OrderBy(l => Vector3.Distance(l.Position, t.Pos)).First();
                Assert.True(Vector3.Distance(best.Position, t.Pos) <= 32f);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DisabledGridIsReportedInvalid()
    {
        var d = SyntheticMap.BuildLightRecovery();
        // truncate the array: the 13 OF-era maps' grid-length-mismatch condition
        d.LightArray.Data = d.LightArray.Data.Take(500).ToArray();
        var r = new LightRecovery().Recover(d);
        Assert.False(r.GridValid);
        Assert.Empty(r.Lights);
        Assert.NotNull(r.GridNote);
    }

    [Fact]
    public void MapFragmentEmitsTargetLightPairs()
    {
        var r = new LightRecovery().Recover(SyntheticMap.BuildLightRecovery());
        Assert.True(r.Lights.Count >= 2);
        var path = Path.Combine(Path.GetTempPath(), $"jkd_lr_{Guid.NewGuid():N}.map");
        try
        {
            LightRecovery.WriteMapFragment(r, "synthetic.bsp", path);
            var text = File.ReadAllText(path);
            Assert.Contains("\"classname\" \"target_light\"", text);
            Assert.Equal(r.Lights.Count, text.Split("target_light").Length - 1);
            Assert.Equal(r.Lights.Count, text.Split("\"classname\" \"light\"").Length - 1);
            foreach (var l in r.Lights)
                Assert.Contains(l.Name, text);
        }
        finally { File.Delete(path); }
    }
}
