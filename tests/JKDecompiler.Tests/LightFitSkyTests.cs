using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// LightFit.FitSkyCorrection — the 5a frozen sky correction. Synthetic
/// points only: the wash is a rank-1 spatial field (fixed sky-image color ×
/// per-point scalar), exactly the shape the no-sky A/B produces.
/// </summary>
public class LightFitSkyTests
{
    static (List<LightFit.SamplePair> Pairs, List<(double R, double G, double B)> Ambient)
        MakePairs(int n, Random rng, double[] phi, double washScale, double gTrue, double cTrue,
            out List<(double R, double G, double B)> noSky)
    {
        var pairs = new List<LightFit.SamplePair>(n);
        var ambient = new List<(double R, double G, double B)>(n);
        noSky = new List<(double R, double G, double B)>(n);
        for (int i = 0; i < n; i++)
        {
            double wsc = rng.NextDouble() * washScale;            // spatial wash scalar
            double nr = 20 + rng.NextDouble() * 20;
            double ng = 20 + rng.NextDouble() * 20;
            double nb = 20 + rng.NextDouble() * 20;
            double r = wsc * phi[0], g = wsc * phi[1], b = wsc * phi[2];
            noSky.Add((nr, ng, nb));
            ambient.Add((nr + r, ng + g, nb + b));
            double or_ = nr + gTrue * r + cTrue;
            double og = ng + gTrue * g + cTrue;
            double ob = nb + gTrue * b + cTrue;
            pairs.Add(new LightFit.SamplePair(0, 0, 0, 1.0,
                (byte)(int)Math.Clamp(Math.Round(or_), 0, 255), (byte)(int)Math.Clamp(Math.Round(og), 0, 255), (byte)(int)Math.Clamp(Math.Round(ob), 0, 255),
                0, 0, 0, 0, 0, 1, -1, -1, 0, 0, -1, -1, 0, 0));
        }
        return (pairs, ambient);
    }

    [Fact]
    public void FitSkyCorrection_SpatialBasis_RecoversGainAndOffset()
    {
        // orig = noSky + 2.3·wash + 7/ch, wash = spatial scalar × fixed color.
        // The diagnostic [wash, const] fit on the strong-wash points recovers
        // (2.3, 7). The MODEL is probe + c (c = mean of orig − probe, one
        // scalar) — the wash as baked, the bake-model-consistent ambient the
        // light fit targets (a scaled wash in the model would not be what
        // stage-A bakes — see the method summary).
        var rng = new Random(7);
        var (pairs, ambient) = MakePairs(400, rng, new[] { 0.55, 0.54, 0.42 }, 60.0, 2.3, 7.0, out var noSky);
        var (gain, offset, spatial, modelOffset, ext) =
            LightFit.FitSkyCorrection(pairs, ambient, noSky, new List<List<(double R, double G, double B)>>());
        Assert.True(spatial);
        Assert.InRange(gain, 2.2, 2.4);
        Assert.InRange(offset, 6.5, 7.5);
        // model = probe + c, c = the single-scalar mean of (orig − probe)
        double c = 0;
        for (int i = 0; i < 400; i++)
            c += (((double)pairs[i].OR - ambient[i].R) + (pairs[i].OG - ambient[i].G) + (pairs[i].OB - ambient[i].B)) / 3.0;
        c /= 400;
        Assert.InRange(modelOffset, c - 0.5, c + 0.5);
        foreach (int i in new[] { 0, 137, 399 })
        {
            Assert.InRange(ext[i].R, ambient[i].R + c - 0.5, ambient[i].R + c + 0.5);
            Assert.InRange(ext[i].G, ambient[i].G + c - 0.5, ambient[i].G + c + 0.5);
            Assert.InRange(ext[i].B, ambient[i].B + c - 0.5, ambient[i].B + c + 0.5);
        }
    }

    [Fact]
    public void FitSkyCorrection_SpatialBasis_NoBrightMask()
    {
        // The original's bright points ARE the strong-wash points. Control
        // vs treated: identical data, then the 30 HIGHEST-WASH points made
        // very bright (the original's saturated wash). With a bright-point
        // mask (the old indicator path) those points would be excluded and
        // the gain unchanged; the spatial fit lets them pull the gain up
        // (verified direction: top-wash spike → gain 1.0 → 4.6; a bottom-
        // wash spike instead drives it to −2.5, so the points' wash rank
        // matters — this is why the mask had to go with the spatial basis).
        var rng = new Random(11);
        var (pairs, ambient) = MakePairs(200, rng, new[] { 0.5, 0.5, 0.4 }, 40.0, 1.0, 0.0, out var noSky);
        var (gainBase, _, _, _, _) =
            LightFit.FitSkyCorrection(pairs, ambient, noSky, new List<List<(double R, double G, double B)>>());
        Assert.InRange(gainBase, 0.9, 1.1);

        var topWash = Enumerable.Range(0, 200)
            .OrderByDescending(id => (ambient[id].R - noSky[id].R) + (ambient[id].G - noSky[id].G) + (ambient[id].B - noSky[id].B))
            .Take(30)
            .ToList();
        foreach (var id in topWash)
        {
            pairs[id] = pairs[id] with
            {
                OR = (byte)(int)Math.Clamp(pairs[id].OR + 100, 0, 255),
                OG = (byte)(int)Math.Clamp(pairs[id].OG + 100, 0, 255),
                OB = (byte)(int)Math.Clamp(pairs[id].OB + 80, 0, 255),
            };
        }
        var (gainBright, _, spatial, _, _) =
            LightFit.FitSkyCorrection(pairs, ambient, noSky, new List<List<(double R, double G, double B)>>());
        Assert.True(spatial);
        Assert.True(gainBright > gainBase + 0.15,
            $"bright wash points should raise the gain (no mask), got {gainBright:F3} vs base {gainBase:F3}");
    }

    [Fact]
    public void FitSkyCorrection_SpatialBasis_PoolMask_KeepsGainClean()
    {
        // fitrun28 (unmasked spatial fit): the wall-washer pools sit on the
        // same outer walls as the sky wash, so the target (orig − noSky) at
        // pool points carries LIGHT energy correlated with the wash basis —
        // the gain inflated to 6.46 and the constant absorbed the pool mean
        // (+32.5/ch). The spatial path must exclude points inside a light
        // field (field > 8) from the sky fit while STILL keeping the bright
        // wash points (the NoBrightMask test above).
        //
        // Setup: one "light" field covering exactly the 30 top-wash points;
        // those points' targets are then brightened (their light energy).
        // Masked fit: gain unchanged (the brightened points are out).
        // Unmasked fit (same brightened data): gain inflates — that is the
        // contamination the mask removes.
        var rng = new Random(13);
        var (pairs, ambient) = MakePairs(200, rng, new[] { 0.5, 0.5, 0.4 }, 40.0, 1.0, 0.0, out var noSky);
        var topWash = Enumerable.Range(0, 200)
            .OrderByDescending(id => (ambient[id].R - noSky[id].R) + (ambient[id].G - noSky[id].G) + (ambient[id].B - noSky[id].B))
            .Take(30)
            .ToHashSet();
        var field = Enumerable.Range(0, pairs.Count)
            .Select(id => topWash.Contains(id) ? (30.0, 30.0, 30.0) : (0.0, 0.0, 0.0))
            .ToList();

        var (gainClean, _, spatial, _, _) =
            LightFit.FitSkyCorrection(pairs, ambient, noSky, new List<List<(double R, double G, double B)>> { field });
        Assert.True(spatial);
        Assert.InRange(gainClean, 0.9, 1.1);

        // brighten the pool points' targets (their light energy)
        var pairs2 = pairs.Select(p => p).ToList();
        foreach (var id in topWash)
        {
            pairs2[id] = pairs2[id] with
            {
                OR = (byte)(int)Math.Clamp(pairs2[id].OR + 100, 0, 255),
                OG = (byte)(int)Math.Clamp(pairs2[id].OG + 100, 0, 255),
                OB = (byte)(int)Math.Clamp(pairs2[id].OB + 80, 0, 255),
            };
        }
        var (gainMasked, _, _, _, _) =
            LightFit.FitSkyCorrection(pairs2, ambient, noSky, new List<List<(double R, double G, double B)>> { field });
        Assert.True(Math.Abs(gainMasked - gainClean) < 0.1,
            $"pool-masked gain must be unaffected by the pool points' light energy: {gainMasked:F3} vs {gainClean:F3}");

        var (gainUnmasked, _, _, _, _) =
            LightFit.FitSkyCorrection(pairs2, ambient, noSky, new List<List<(double R, double G, double B)>>());
        Assert.True(gainUnmasked > gainMasked + 0.5,
            $"unmasked fit should be contaminated by the brightened pool points (no mask to exclude them): {gainUnmasked:F3} vs {gainMasked:F3}");
    }

    [Fact]
    public void FitSkyCorrection_SpatialBasis_ZeroWash_DegradesToConstant()
    {
        // no-sky A/B identical to the probe (the sky light failed to fire):
        // the wash basis is degenerate — gain ≈ 0, the constant still fits.
        var rng = new Random(13);
        var (pairs, ambient) = MakePairs(200, rng, new[] { 0.5, 0.5, 0.4 }, 0.0, 0.0, 5.0, out var noSky);
        // ambient == noSky here (zero wash)
        var (gain, offset, spatial, _, _) =
            LightFit.FitSkyCorrection(pairs, ambient, noSky, new List<List<(double R, double G, double B)>>());
        Assert.True(spatial);
        Assert.InRange(gain, -0.05, 0.05);
        Assert.InRange(offset, 4.5, 5.5);
    }

    [Fact]
    public void FitSkyCorrection_Fallback_IndicatorBasis_MasksAndRestores()
    {
        // no A/B (ambientNoSky = null) → 0/1 indicator basis (no sky visible
        // here → all zeros → gain 0), bright-point mask keeps the constant
        // honest, and the mask restores the pair weights before returning.
        int n = 60;
        var pairs = new List<LightFit.SamplePair>(n);
        var ambient = new List<(double R, double G, double B)>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 20 + (i % 5);
            pairs.Add(new LightFit.SamplePair(0, 0, 0, 1.0,
                (byte)(a + 5), (byte)(a + 5), (byte)(a + 5), 0, 0, 0, 0, 0, 1,
                -1, -1, 0, 0, -1, -1, 0, 0));
            ambient.Add((a, a, a));
        }
        // point 7 is bright: orig − ambient = 50 (would drag the constant)
        pairs[7] = pairs[7] with { OR = (byte)(20 + 50), OG = (byte)(20 + 50), OB = (byte)(20 + 50) };

        var (gain, offset, spatial, _, _) =
            LightFit.FitSkyCorrection(pairs, ambient, null, new List<List<(double R, double G, double B)>>());
        Assert.False(spatial);
        Assert.InRange(gain, -0.05, 0.05);
        Assert.InRange(offset, 4.5, 5.5);      // the bright point did not drag it
        for (int i = 0; i < n; i++)
            Assert.Equal(1.0, pairs[i].Weight);  // mask restored
    }
}
