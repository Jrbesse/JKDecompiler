using System;
using System.Collections.Generic;
using System.Linq;
using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// Pure-math regression tests for the 5b joint solve (BuildGh1D + NnlsSolve)
/// on small correlated-field systems with HAND-COMPUTED answers — no BSP
/// fixture needed, runs in CI. Existence: the 5b "joint NNLS" originally fed
/// the solvers a half-filled (upper-triangle-only) normal matrix, which
/// silently solves a different non-symmetric system; on academy1's correlated
/// wall-washer pools that flipped the per-light partition by up to 95%
/// relative scale (PR #13 review, re-derived on fitrun33's saved data).
/// BuildGh1D now returns a fully symmetric G; these tests pin both the
/// interior optimum and the KKT-at-zero boundary of the production path.
/// </summary>
public class LightFitSolveTest
{
    static LightFit.SamplePair P(byte r, byte g, byte b) => new(
        0, 0, 0, 1.0, r, g, b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Two correlated fields (cos 0.9), two points. Targets = ambient(65) +
    /// integer t: t1=(4,1,0), t2=(7,2,0). Hand computation (v1=(1,0,0),
    /// v2=(0.9,sqrt(0.19),0), g values (1,1) and (1,2)):
    ///   G = [[2, 2.7],[2.7, 5]],  h = (11, 18.3794494718)
    ///   unconstrained LSQ (both components > 0 ⇒ the NNLS interior optimum):
    ///   s2 = (2·h1 − 2.7·h0)/(10 − 2.7²) = (36.7588989435 − 29.7)/2.71 = 2.6047597578
    ///   s1 = (h0 − 2.7·s2)/2 = 1.9835743270
    /// The as-built (upper-triangle-only) G=[[2,2.7],[0,5]] instead converges
    /// to ≈(0.538, 3.676) — this test fails hard on the half-filled matrix.
    /// </summary>
    [Fact]
    public void JointSolveReproducesHandComputedInteriorSolution()
    {
        var pairs = new List<LightFit.SamplePair> { P(69, 66, 65), P(72, 67, 65) };
        var ambient = new List<(double R, double G, double B)> { (65, 65, 65), (65, 65, 65) };
        var v1 = new LightFit.LightBasis { VR = 1, VG = 0, VB = 0, G = new List<double> { 1, 1 } };
        var v2 = new LightFit.LightBasis { VR = 0.9, VG = Math.Sqrt(0.19), VB = 0, G = new List<double> { 1, 2 } };
        var basis = new List<LightFit.LightBasis> { v1, v2 };
        var idx = new List<int> { 0, 1 };

        var (G, h) = LightFit.BuildGh1D(pairs, ambient, basis, idx);
        // the contract under test: G fully symmetric
        Assert.True(Math.Abs(G[1, 0] - G[0, 1]) < 1e-12, "BuildGh1D returned a non-symmetric G");

        var s = LightFit.NnlsSolve(G, h, 0.0);
        Assert.True(Math.Abs(s[0] - 1.9835743270) < 1e-3, $"s[0] {s[0]:F6} != 1.983574");
        Assert.True(Math.Abs(s[1] - 2.6047597578) < 1e-3, $"s[1] {s[1]:F6} != 2.604760");
    }

    /// <summary>
    /// Boundary / KKT-at-zero: one point, correlated fields (cos 0.9),
    /// target t=(0,1,0) above ambient(65) ⇒ h = (t·v1, t·v2) = (0, −0.43589):
    /// a negative projection onto the second field. G = [[1,0.9],[0.9,1]].
    /// Unconstrained LSQ = (2.0647, −2.2942) — infeasible. At s = 0 the
    /// reduced gradient G·h = (−0.3923, −0.4359) ≤ 0 componentwise, so the
    /// KKT conditions hold at the zero vector: the EXACT NNLS optimum is
    /// (0, 0). (The half-filled G=[[1,0.9],[0,1]] also lands here, so this
    /// case pins the non-negativity behavior rather than the symmetrization.)
    /// </summary>
    [Fact]
    public void JointSolveReturnsExactZeroWhenAllGradientsAreNonPositive()
    {
        var pairs = new List<LightFit.SamplePair> { P(65, 66, 65) };
        var ambient = new List<(double R, double G, double B)> { (65, 65, 65) };
        var v1 = new LightFit.LightBasis { VR = 1, VG = 0, VB = 0, G = new List<double> { 1 } };
        var v2 = new LightFit.LightBasis { VR = 0.9, VG = -Math.Sqrt(0.19), VB = 0, G = new List<double> { 1 } };
        var basis = new List<LightFit.LightBasis> { v1, v2 };
        var idx = new List<int> { 0, 1 };

        var (G, h) = LightFit.BuildGh1D(pairs, ambient, basis, idx);
        var s = LightFit.NnlsSolve(G, h, 0.0);
        Assert.True(s[0] < 1e-9, $"s[0] {s[0]:E} != 0");
        Assert.True(s[1] < 1e-9, $"s[1] {s[1]:E} != 0");
    }

    /// <summary>
    /// NnlsSolve must not mutate the caller's matrix (the documented contract
    /// — it works on a ridge-added copy). The caller's G stays bit-identical.
    /// </summary>
    [Fact]
    public void NnlsSolveDoesNotMutateCallerMatrix()
    {
        var G = new double[,] { { 2, 2.7 }, { 2.7, 5 } };
        var h = new double[] { 11, 18.3794494718 };
        var Gbefore = (double[,])G.Clone();
        LightFit.NnlsSolve(G, h, 1e-3);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True(G[i, j] == Gbefore[i, j], $"G[{i},{j}] mutated by NnlsSolve");
    }
}
