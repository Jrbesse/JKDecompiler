using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// One-shot diagnostic for fitrun31 (joint-NNLS 5b): per-point decomposition
/// of the bake/model discrepancy. The stage-A BAKE measured 201.49 L1 (≈98
/// worse than ambient-only) while the in-sample MODEL (probe + c + Σ
/// scale·field) is ≈ ambient — so the bake must disagree with the linear
/// model somewhere. Re-samples all of fitrun31's saved BSPs (probe, no-sky,
/// 9 singles, stage A; NO compiles) and attributes the residual per region.
/// Writes the full breakdown to the run dir's fitrun31_diag.json. Skips when
/// the artifacts are absent (CI).
/// </summary>
public class Fitrun31DiagTest
{
    const string Run = "/tmp/lfit/fitrun31";
    const string Run32 = "/tmp/lfit/fitrun32";
    const string OrigBsp = "/tmp/lfit/src/academy1.bsp";

    /// <summary>
    /// fitrun32 (instrumented): diff the run's OWN stage-A measurement
    /// (stagea_dbg.json: allVals + gscales + l1) against an offline
    /// re-sample of the saved fit_stageA.bsp. Answers: (a) is the run's
    /// l1 consistent with its own vals? (b) where do the run's vals
    /// diverge from the re-sample, and by how much?
    /// </summary>
    [Fact]
    public void DiffRunMeasurementVsResample()
    {
        string dbgPath = Path.Combine(Run32, "scratch", "stagea_dbg.json");
        string stageAPath = Path.Combine(Run32, "scratch", "fit_stageA", "fit_stageA.bsp");
        if (!File.Exists(dbgPath) || !File.Exists(stageAPath)) return;

        var dbg = JsonDocument.Parse(File.ReadAllText(dbgPath));
        double l1Run = dbg.RootElement.GetProperty("l1").GetDouble();
        var gsRun = dbg.RootElement.GetProperty("gscales").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var allVals = dbg.RootElement.GetProperty("allVals").EnumerateArray().Select(e => e.GetDouble()).ToArray();

        var probe = new BspReader().Read(Path.Combine(Run32, "scratch", "fit_probe", "fit_probe.bsp"));
        var stageA = new BspReader().Read(stageAPath);
        var ff = new LightFit("", "");
        int nL = gsRun.Length;
        var singles = Enumerable.Range(0, nL)
            .Select(i => new BspReader().Read(Path.Combine(Run32, "scratch", $"fit_s{i:D3}", $"fit_s{i:D3}.bsp")))
            .ToList();

        var orig = new BspReader().Read(OrigBsp);
        var origFaces = LightFit.BuildSampleFaces(orig);
        var probeFaces = LightFit.BuildSampleFaces(probe);
        var (pairs, unmatched, matched) = ff.BuildOrigPairs(orig, origFaces, probe, probeFaces);
        int nP = pairs.Count;
        Assert.True(nP == allVals.Length / 3, $"pair count {nP} != dbg vals {allVals.Length / 3}");
        var ambient = pairs.Select(p => (R: (double)p.CR, G: (double)p.CG, B: (double)p.CB)).ToList();
        var fields = new double[nL][];
        for (int l = 0; l < nL; l++)
        {
            var have = new bool[nP];
            var raw = ff.SampleCandidate(singles[l], LightFit.BuildSampleFaces(singles[l]), pairs, have);
            fields[l] = new double[nP * 3];
            for (int id = 0; id < nP; id++)
            {
                if (!have[id]) continue;
                fields[l][id * 3 + 0] = raw[id].R - ambient[id].R;
                fields[l][id * 3 + 1] = raw[id].G - ambient[id].G;
                fields[l][id * 3 + 2] = raw[id].B - ambient[id].B;
            }
        }
        // my re-sample of the saved stageA with the run's EXACT fill formula
        var bakeValid = new bool[nP];
        var mine = ff.SampleCandidate(stageA, LightFit.BuildSampleFaces(stageA), pairs, bakeValid,
            modelFill: id =>
            {
                double r = ambient[id].R, g = ambient[id].G, b = ambient[id].B;
                for (int l = 0; l < nL; l++)
                {
                    if (gsRun[l] == 0) continue;
                    r += gsRun[l] * fields[l][id * 3 + 0];
                    g += gsRun[l] * fields[l][id * 3 + 1];
                    b += gsRun[l] * fields[l][id * 3 + 2];
                }
                return (r, g, b);
            });
        double L1(System.Collections.Generic.IReadOnlyList<(double R, double G, double B)> vals)
        {
            double tot = 0, w = 0;
            for (int id = 0; id < nP; id++)
            {
                var p = pairs[id];
                tot += p.Weight * (Math.Abs(p.OR - vals[id].R) + Math.Abs(p.OG - vals[id].G) + Math.Abs(p.OB - vals[id].B));
                w += p.Weight;
            }
            return w > 0 ? tot / w : 0;
        }
        var runTuples = new (double R, double G, double B)[nP];
        for (int id = 0; id < nP; id++)
            runTuples[id] = (allVals[id * 3], allVals[id * 3 + 1], allVals[id * 3 + 2]);
        double l1RunVals = L1(runTuples);   // the run's own vals, my pairs
        double l1Mine = L1(mine);

        // point-for-point divergence between the run's vals and my re-sample
        long nDiv = 0; double wDiv = 0, wTot = 0;
        var divs = new List<(double D, object Row)>();
        for (int id = 0; id < nP; id++)
        {
            var p = pairs[id];
            double d = Math.Abs(runTuples[id].R - mine[id].R) + Math.Abs(runTuples[id].G - mine[id].G) + Math.Abs(runTuples[id].B - mine[id].B);
            wTot += p.Weight;
            if (d > 0.5)
            {
                nDiv++; wDiv += p.Weight;
                divs.Add((d, (object)new { id, x = p.X, y = p.Y, z = p.Z,
                    run = new[] { runTuples[id].R, runTuples[id].G, runTuples[id].B },
                    mine = new[] { mine[id].R, mine[id].G, mine[id].B },
                    o = new[] { p.OR, p.OG, p.OB }, d }));
            }
        }
        var worst = divs.OrderByDescending(t => t.D).Take(60).Select(t => t.Row).ToList();
        // sampled-vs-fill split of the RUN's vals
        double sErr = 0, sW = 0, fErr = 0, fW = 0; int nS = 0, nF = 0;
        for (int id = 0; id < nP; id++)
        {
            var p = pairs[id];
            double e = p.Weight * (Math.Abs(p.OR - mine[id].R) + Math.Abs(p.OG - mine[id].G) + Math.Abs(p.OB - mine[id].B));
            if (bakeValid[id]) { sErr += e; sW += p.Weight; nS++; } else { fErr += e; fW += p.Weight; nF++; }
        }
        var report = new
        {
            l1RunReported = l1Run,
            l1RunValsRescored = Math.Round(l1RunVals, 3),
            l1MyResample = Math.Round(l1Mine, 3),
            l1MyResampleSampled = Math.Round(sW > 0 ? sErr / sW : 0, 3),
            l1MyResampleFill = Math.Round(fW > 0 ? fErr / fW : 0, 3),
            nSampled = nS, nFill = nF,
            nDivergent = nDiv,
            weightFracDivergent = Math.Round(wTot > 0 ? wDiv / wTot : 0, 4),
            gsRun,
            worstDivergences = worst,
        };
        string outPath = Path.Combine(Run32, "fitrun32_diag.json");
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        double pctDiv = wTot > 0 ? 100 * wDiv / wTot : 0;
        Console.WriteLine($"[diag32] runL1={l1Run:F2} runValsRescored={l1RunVals:F2} myResample={l1Mine:F2} div={nDiv} ({pctDiv:F1}%w) → {outPath}");
    }

    [Fact]
    public void DecomposeStageABakeVsModel()
    {
        string stageAPath = Path.Combine(Run, "scratch", "fit_stageA", "fit_stageA.bsp");
        if (!File.Exists(stageAPath)) return;   // artifacts only exist in the fit container

        var orig = new BspReader().Read(OrigBsp);
        var probe = new BspReader().Read(Path.Combine(Run, "scratch", "fit_probe", "fit_probe.bsp"));
        var noSky = new BspReader().Read(Path.Combine(Run, "scratch", "fit_probe_nosky", "fit_probe_nosky.bsp"));
        var stageA = new BspReader().Read(stageAPath);
        // instance holder for the internal instance samplers (bin/gameDir unused — no compiles)
        var ff = new LightFit("", "");
        int nL = 9;
        var singles = Enumerable.Range(0, nL)
            .Select(i => new BspReader().Read(Path.Combine(Run, "scratch", $"fit_s{i:D3}", $"fit_s{i:D3}.bsp")))
            .ToList();

        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Run, "fit_report.json")));
        var scales = doc.RootElement.GetProperty("lights").EnumerateArray().Select(l =>
        {
            var c01 = l.GetProperty("color01");
            double iv = l.GetProperty("intensity").GetDouble();
            return new[] { iv * c01[0].GetDouble(), iv * c01[1].GetDouble(), iv * c01[2].GetDouble() };
        }).ToArray();
        double c = doc.RootElement.GetProperty("skyCorrection").GetProperty("modelOffsetPerChannel").GetDouble();

        var origFaces = LightFit.BuildSampleFaces(orig);
        var probeFaces = LightFit.BuildSampleFaces(probe);
        var (pairs, unmatched, matched) = ff.BuildOrigPairs(orig, origFaces, probe, probeFaces);
        int nP = pairs.Count;
        var ambient = pairs.Select(p => (R: (double)p.CR, G: (double)p.CG, B: (double)p.CB)).ToList();

        // wash = probe − noSky (same original-driven pair iteration → 1:1 aligned)
        var noskyFaces = LightFit.BuildSampleFaces(noSky);
        var (noskyPairs, _, _) = ff.BuildOrigPairs(orig, origFaces, noSky, noskyFaces);
        bool aligned = noskyPairs.Count == nP;
        if (aligned)
            for (int id = 0; id < nP && aligned; id++)
                if (Math.Abs(pairs[id].X - noskyPairs[id].X) > 1e-4) aligned = false;
        var wash = new (double R, double G, double B)[nP];
        for (int id = 0; id < nP; id++)
            wash[id] = (ambient[id].R - (aligned ? noskyPairs[id].CR : ambient[id].R),
                        ambient[id].G - (aligned ? noskyPairs[id].CG : ambient[id].G),
                        ambient[id].B - (aligned ? noskyPairs[id].CB : ambient[id].B));

        // per-light fields (single − probe, 0 where the single had no lightmap)
        var fields = new double[nL][];   // [l][id*3 + ch]
        for (int l = 0; l < nL; l++)
        {
            var have = new bool[nP];
            var raw = ff.SampleCandidate(singles[l], LightFit.BuildSampleFaces(singles[l]), pairs, have);
            fields[l] = new double[nP * 3];
            for (int id = 0; id < nP; id++)
            {
                if (have[id])
                {
                    fields[l][id * 3 + 0] = raw[id].R - ambient[id].R;
                    fields[l][id * 3 + 1] = raw[id].G - ambient[id].G;
                    fields[l][id * 3 + 2] = raw[id].B - ambient[id].B;
                }
            }
        }

        // gscales (the scalar the fit actually solved): scales = gscales·v,
        // intensity = gscales·max(v), color01 = v/max(v) → gscales =
        // intensity · |color01| (|v| = 1). The FIT's model contribution per
        // channel is gscales_l · f_l[ch] (rank-1: f = G·v, gscales·G·v per
        // channel reduces to gscales·f[ch]).
        var gscales = new double[nL];
        for (int l = 0; l < nL; l++)
        {
            double iv = scales[l].Max();
            double norm = Math.Sqrt(scales[l][0] * scales[l][0] + scales[l][1] * scales[l][1] + scales[l][2] * scales[l][2]);
            gscales[l] = iv > 1e-9 ? norm : 0;
        }

        // HYPOTHESIS CHECK — reproduce the Radfit baseline re-polish (the
        // scales the actual run's stage-A modelFill used): 5b joint NNLS over
        // all fields (ambientExt = probe + c, as in the spatial path), then
        // re-solve the positive set with (a) the CURRENT plain LSQ (1e-6
        // ridge) and (b) the proposed NNLS (1e-3 ridge).
        var ambientExt = new List<(double R, double G, double B)>(nP);
        for (int id = 0; id < nP; id++) ambientExt.Add((ambient[id].R + c, ambient[id].G + c, ambient[id].B + c));
        var basis = new List<LightFit.LightBasis>();
        for (int l = 0; l < nL; l++)
        {
            var trip = new List<(double R, double G, double B)>(nP);
            for (int id = 0; id < nP; id++)
                trip.Add((fields[l][id * 3 + 0], fields[l][id * 3 + 1], fields[l][id * 3 + 2]));
            basis.Add(LightFit.BuildBasis(trip));
        }
        var selAll = Enumerable.Range(0, nL).ToList();
        var (G5, h5) = LightFit.BuildGh1D(pairs, ambientExt, basis, selAll);
        int n5 = G5.GetLength(0);
        double tr5 = 0;
        for (int i = 0; i < n5; i++) tr5 += G5[i, i];
        var g5 = n5 > 0 ? LightFit.NnlsSolve(G5, h5, 1e-3 * Math.Max(1.0, tr5 / n5)) : Array.Empty<double>();
        var selected = selAll.Where(l => g5[l] > 0).ToList();
        var (Gpol, hpol) = LightFit.BuildGh1D(pairs, ambientExt, basis, selected);
        int npol = Gpol.GetLength(0);
        double trpol = 0;
        for (int i = 0; i < npol; i++) trpol += Gpol[i, i];
        var radfitPlain = new double[nL];
        var xPlain = npol > 0 ? LightFit.SolveLeastSquares(Gpol, npol, hpol, Math.Max(1e-4, (trpol / npol) * 1e-6)) : Array.Empty<double>();
        for (int k = 0; k < selected.Count; k++) radfitPlain[selected[k]] = Math.Max(0, xPlain[k]);
        var radfitNnls = new double[nL];
        var xNnls = npol > 0 ? LightFit.NnlsSolve(Gpol, hpol, 1e-3 * Math.Max(1.0, trpol / npol)) : Array.Empty<double>();
        for (int k = 0; k < selected.Count; k++) radfitNnls[selected[k]] = xNnls[k];

        // THE ACTUAL RUN: drop pass (step 6) removed sw_00 + m6_00/01/02 BEFORE
        // RefineRadii, so PolishedRms re-solved only the 5 surviving washers.
        var sel5 = new List<int> { 1, 2, 3, 4, 5 };
        var (G5c, h5c) = LightFit.BuildGh1D(pairs, ambientExt, basis, sel5);
        int n5c = G5c.GetLength(0);
        double tr5c = 0;
        for (int i = 0; i < n5c; i++) tr5c += G5c[i, i];
        var radfitPlain5 = new double[nL];
        var xP5 = n5c > 0 ? LightFit.SolveLeastSquares(G5c, n5c, h5c, Math.Max(1e-4, (tr5c / n5c) * 1e-6)) : Array.Empty<double>();
        for (int k = 0; k < sel5.Count; k++) radfitPlain5[sel5[k]] = Math.Max(0, xP5[k]);
        var radfitNnls5 = new double[nL];
        var xN5 = n5c > 0 ? LightFit.NnlsSolve(G5c, h5c, 1e-3 * Math.Max(1.0, tr5c / n5c)) : Array.Empty<double>();
        for (int k = 0; k < sel5.Count; k++) radfitNnls5[sel5[k]] = xN5[k];

        // DROP-PASS check: leave-one-out ΔE per light, current (plain LSQ,
        // 1e-6 ridge) vs proposed (NNLS, 1e-3 ridge). If the plain-LSQ ΔEs
        // are ~0 for lights with substantial scale (m6_02 @ 0.353), the
        // pre-gate drop is running on an ill-conditioned solve.
        double rmsFitAll = LightFit.RmsOf1D(pairs, basis, g5, ambientExt);
        var dEPlain = new double[nL];
        var dENnls = new double[nL];
        for (int l = 0; l < nL; l++)
        {
            if (g5[l] == 0) continue;
            var activeIdx = Enumerable.Range(0, nL).Where(m => m != l && g5[m] != 0).ToList();
            var (Gd1, hd1) = LightFit.BuildGh1D(pairs, ambientExt, basis, activeIdx);
            int nd = Gd1.GetLength(0);
            double trd = 0;
            for (int i = 0; i < nd; i++) trd += Gd1[i, i];
            var xdP = nd > 0 ? LightFit.SolveLeastSquares(Gd1, nd, hd1, Math.Max(1e-4, (trd / nd) * 1e-6)) : Array.Empty<double>();
            var gdP = new double[nL];
            for (int k = 0; k < activeIdx.Count; k++) gdP[activeIdx[k]] = Math.Max(0, xdP[k]);
            dEPlain[l] = LightFit.RmsOf1D(pairs, basis, gdP, ambientExt) - rmsFitAll;
            var (Gd2, hd2) = LightFit.BuildGh1D(pairs, ambientExt, basis, activeIdx);
            var xdN = nd > 0 ? LightFit.NnlsSolve(Gd2, hd2, 1e-3 * Math.Max(1.0, trd / nd)) : Array.Empty<double>();
            var gdN = new double[nL];
            for (int k = 0; k < activeIdx.Count; k++) gdN[activeIdx[k]] = xdN[k];
            dENnls[l] = LightFit.RmsOf1D(pairs, basis, gdN, ambientExt) - rmsFitAll;
        }

        // model = probe + c + Σ gscales·field
        var model = new (double R, double G, double B)[nP];
        for (int id = 0; id < nP; id++)
        {
            double r = ambient[id].R + c, g = ambient[id].G + c, b = ambient[id].B + c;
            for (int l = 0; l < nL; l++)
            {
                double gs = gscales[l];
                if (gs == 0) continue;
                r += gs * fields[l][id * 3 + 0];
                g += gs * fields[l][id * 3 + 1];
                b += gs * fields[l][id * 3 + 2];
            }
            model[id] = (r, g, b);
        }

        var bakeValid = new bool[nP];
        var bake = ff.SampleCandidate(stageA, LightFit.BuildSampleFaces(stageA), pairs, bakeValid,
            modelFill: id => (model[id].R - c, model[id].G - c, model[id].B - c));

        double L1(System.Collections.Generic.IReadOnlyList<(double R, double G, double B)> vals)
        {
            double tot = 0, w = 0;
            for (int id = 0; id < nP; id++)
            {
                var p = pairs[id];
                tot += p.Weight * (Math.Abs(p.OR - vals[id].R) + Math.Abs(p.OG - vals[id].G) + Math.Abs(p.OB - vals[id].B));
                w += p.Weight;
            }
            return tot / w;
        }

        double l1Amb = L1(ambient.Select(a => (a.R, a.G, a.B)).ToArray());
        double l1Bake = L1(bake);
        double l1Model = L1(model);

        // sampled vs fill split (the stage-A gate L1 mixes points read from
        // the stage-A lightmap with points filled by the linear model — the
        // two populations can fail for different reasons)
        double sErr = 0, sW = 0, fErr = 0, fW = 0; int nSamp = 0, nFill = 0;
        for (int id = 0; id < nP; id++)
        {
            var p = pairs[id];
            double e = p.Weight * (Math.Abs(p.OR - bake[id].R) + Math.Abs(p.OG - bake[id].G) + Math.Abs(p.OB - bake[id].B));
            if (bakeValid[id]) { sErr += e; sW += p.Weight; nSamp++; } else { fErr += e; fW += p.Weight; nFill++; }
        }
        double l1BakeSampled = sW > 0 ? sErr / sW : 0, l1BakeFill = fW > 0 ? fErr / fW : 0;

        // bake-model discrepancy, SAMPLED points only (fill points are the
        // linear model by construction): d = bake − (probe + Σ gscales·field).
        // If large, the multi-light bake adds energy the linear sum of single
        // fields does not predict (radiosity nonlinearity / shape mismatch).
        double unexplained = 0, uw = 0, unexplainedPos = 0, unexplainedNeg = 0;
        for (int id = 0; id < nP; id++)
        {
            if (!bakeValid[id]) continue;
            double dr = bake[id].R - (model[id].R - c), dg = bake[id].G - (model[id].G - c), db = bake[id].B - (model[id].B - c);
            double m = dr + dg + db;
            unexplained += pairs[id].Weight * (Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db));
            uw += pairs[id].Weight;
            if (m > 2) unexplainedPos += pairs[id].Weight;
            if (m < -2) unexplainedNeg += pairs[id].Weight;
        }
        unexplained /= uw;

        // which candidate metric equals the run's reported 201.49?
        double Wtot = pairs.Sum(p => p.Weight);
        double fillErrAmbientOnly = 0, fillErrZero = 0, fillErrSurv5 = 0, fillErrWithC = 0;
        int nSurv = 0;
        for (int id = 0; id < nP; id++)
        {
            if (bakeValid[id]) continue;
            var p = pairs[id];
            double amb = (Math.Abs(p.OR - ambient[id].R) + Math.Abs(p.OG - ambient[id].G) + Math.Abs(p.OB - ambient[id].B)) * p.Weight;
            double zr = (Math.Abs(p.OR) + Math.Abs(p.OG) + Math.Abs(p.OB)) * p.Weight;
            fillErrAmbientOnly += amb; fillErrZero += zr;
            double sr = 0;
            for (int l = 0; l < nL; l++)
                sr += gscales[l] * (fields[l][id * 3 + 0] + fields[l][id * 3 + 1] + fields[l][id * 3 + 2]) / 3.0;
            fillErrSurv5 += (Math.Abs(p.OR - (ambient[id].R + sr)) + Math.Abs(p.OG - (ambient[id].G + sr)) + Math.Abs(p.OB - (ambient[id].B + sr))) * p.Weight;
            fillErrWithC += (Math.Abs(p.OR - model[id].R) + Math.Abs(p.OG - model[id].G) + Math.Abs(p.OB - model[id].B)) * p.Weight;
            nSurv++;
        }
        double sErrTot = sErr; // sampled error total (from the split above)
        // the run's actual 7b fill scales: 5b scales with the 5 survivors
        // re-polished by PolishedRms (plain LSQ, 5 columns)
        var gsAct = (double[])gscales.Clone();
        for (int l = 1; l <= 5; l++) gsAct[l] = radfitPlain5[l];
        double fillErrActual = 0;
        for (int id = 0; id < nP; id++)
        {
            if (bakeValid[id]) continue;
            var p = pairs[id];
            double er = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                double fv = ch == 0 ? ambient[id].R : (ch == 1 ? ambient[id].G : ambient[id].B);
                double ov = ch == 0 ? p.OR : (ch == 1 ? p.OG : p.OB);
                double lit = 0;
                for (int l = 0; l < nL; l++) lit += gsAct[l] * fields[l][id * 3 + ch];
                er += Math.Abs(ov - (fv + lit));
            }
            fillErrActual += p.Weight * er;
        }
        var candidates = new Dictionary<string, double>
        {
            ["sampled+fill(amb+9light)"] = (sErrTot + fillErrSurv5) / Wtot, // all-9 == all-5 here (fill fields are 0)
            ["sampled+fill(ambient)"] = (sErrTot + fillErrAmbientOnly) / Wtot,
            ["sampled+fill(0)"] = (sErrTot + fillErrZero) / Wtot,
            ["sampled+fill(model+c)"] = (sErrTot + fillErrWithC) / Wtot,
            ["sampled+fill(actual7b-scales)"] = (sErrTot + fillErrActual) / Wtot,
            ["sampledOnlyOverTotalW"] = sErrTot / Wtot,
            ["sampledOnlyOverSampledW"] = l1BakeSampled,
        };

        // per-light field mass at FILL points (no face): if nonzero, the
        // run's modelFill carried light energy there that my candidates above
        // already account for (fillErrSurv5); if all-zero, the run's 201.49
        // must come from a different sampled lightmap than fit_stageA.bsp.
        var fillFieldMass = new double[nL];
        var fillFieldNz = new int[nL];
        for (int id = 0; id < nP; id++)
        {
            if (bakeValid[id]) continue;
            for (int l = 0; l < nL; l++)
            {
                double m = Math.Abs(fields[l][id * 3 + 0]) + Math.Abs(fields[l][id * 3 + 1]) + Math.Abs(fields[l][id * 3 + 2]);
                fillFieldMass[l] += pairs[id].Weight * m;
                if (m > 0.5) fillFieldNz[l]++;
            }
        }
        // HAVE[] at fill points: the field is raw−ambient where have=true, 0
        // otherwise — a have=true point with raw≈0 gives field≈−65 (negative
        // lobe), which the mass check above misreads as "small". Count have
        // directly and dump the raw distribution.
        var fillHave = new int[nL];
        var fillRawSum = new double[nL];
        for (int l = 0; l < nL; l++)
        {
            var have = new bool[nP];
            var raw = ff.SampleCandidate(singles[l], LightFit.BuildSampleFaces(singles[l]), pairs, have);
            int n = 0; double rs = 0;
            for (int id = 0; id < nP; id++)
            {
                if (bakeValid[id] || !have[id]) continue;
                n++; rs += (raw[id].R + raw[id].G + raw[id].B) / 3.0;
            }
            fillHave[l] = n;
            fillRawSum[l] = n > 0 ? rs / n : 0;
        }
        // L1(original vs X) for alternative lightmaps with the run's fill
        // logic (ambient + all-9 gscales·field), to see if 201.49 matches one.
        var altNames = new[] { "fit_r005_980", "fit_r004_1062", "fit_r001_630", "fit_s005", "fit_s004", "fit_s001" };
        // FIELD ENERGY of the on-disk radius candidates (sw_03→630, sw_05→980):
        // the run's stagea_dbg fillEnergyPerLight showed −867M/−1378M for
        // exactly these two — if the on-disk candidates give ≈0, the run's
        // in-memory candidate BSPs were not the files on disk.
        var candEnergy = new Dictionary<string, double>();
        foreach (var cn in new[] { "fit_r003_630", "fit_r005_980", "fit_r001_630", "fit_r004_765" })
        {
            string cp = Path.Combine(Run, "scratch", cn, cn + ".bsp");
            if (!File.Exists(cp)) continue;
            var haveC = new bool[nP];
            var rawC = ff.SampleCandidate(new BspReader().Read(cp), LightFit.BuildSampleFaces(new BspReader().Read(cp)), pairs, haveC);
            double e = 0, eFill = 0, eSamp = 0; int nHave = 0, nHaveFill = 0;
            for (int id = 0; id < nP; id++)
            {
                double f = haveC[id] ? (rawC[id].R + rawC[id].G + rawC[id].B) / 3.0 - (ambient[id].R + ambient[id].G + ambient[id].B) / 3.0 : 0;
                e += pairs[id].Weight * f;
                if (haveC[id]) { nHave++; if (!bakeValid[id]) { eFill += pairs[id].Weight * f; nHaveFill++; } else eSamp += pairs[id].Weight * f; }
            }
            candEnergy[cn] = Math.Round(e, 1);
            Console.WriteLine($"[cand] {cn}: energy={e:F1} nHave={nHave} nHaveAtFill={nHaveFill} eFill={eFill:F1} eSamp={eSamp:F1}");
        }
        var altL1s = new Dictionary<string, double>();
        foreach (var nm in altNames)
        {
            string p2 = Path.Combine(Run, "scratch", nm, nm + ".bsp");
            if (!File.Exists(p2)) continue;
            var alt = new BspReader().Read(p2);
            var av = ff.SampleCandidate(alt, LightFit.BuildSampleFaces(alt), pairs,
                modelFill: id =>
                {
                    double r = ambient[id].R, g = ambient[id].G, b = ambient[id].B;
                    for (int l = 0; l < nL; l++)
                    {
                        if (gscales[l] == 0) continue;
                        r += gscales[l] * fields[l][id * 3 + 0];
                        g += gscales[l] * fields[l][id * 3 + 1];
                        b += gscales[l] * fields[l][id * 3 + 2];
                    }
                    return (r, g, b);
                });
            altL1s[nm] = L1(av);
        }

        // region breakdown
        object[] Regions()
        {
            int reg = 3;
            var n = new int[reg]; var wa = new double[reg];
            var eAmb = new double[reg]; var eBake = new double[reg]; var eModel = new double[reg];
            var mOP = new double[reg]; var mBk = new double[reg]; var mMd = new double[reg]; var mBmF = new double[reg];
            for (int id = 0; id < nP; id++)
            {
                var p = pairs[id];
                double wMax = Math.Max(Math.Abs(wash[id].R), Math.Max(Math.Abs(wash[id].G), Math.Abs(wash[id].B)));
                double fMax = 0;
                for (int l = 0; l < nL; l++)
                    fMax = Math.Max(fMax, Math.Max(Math.Abs(fields[l][id * 3 + 0]),
                        Math.Max(Math.Abs(fields[l][id * 3 + 1]), Math.Abs(fields[l][id * 3 + 2]))));
                int r = wMax > 10 ? 0 : (fMax > 8 ? 1 : 2);
                n[r]++;
                double w = p.Weight; wa[r] += w;
                eAmb[r] += w * (Math.Abs(p.OR - ambient[id].R) + Math.Abs(p.OG - ambient[id].G) + Math.Abs(p.OB - ambient[id].B));
                eBake[r] += w * (Math.Abs(p.OR - bake[id].R) + Math.Abs(p.OG - bake[id].G) + Math.Abs(p.OB - bake[id].B));
                eModel[r] += w * (Math.Abs(p.OR - model[id].R) + Math.Abs(p.OG - model[id].G) + Math.Abs(p.OB - model[id].B));
                mOP[r] += w * ((p.OR - ambient[id].R) + (p.OG - ambient[id].G) + (p.OB - ambient[id].B)) / 3.0;
                mBk[r] += w * ((bake[id].R + bake[id].G + bake[id].B) / 3.0);
                mMd[r] += w * ((model[id].R + model[id].G + model[id].B) / 3.0);
                mBmF[r] += w * ((bake[id].R + bake[id].G + bake[id].B) - (model[id].R + model[id].G + model[id].B) + 3 * c) / 3.0;
            }
            var names = new[] { "washStrong(w>10)", "poolOnly(f>8)", "interior" };
            var out_ = new object[reg];
            for (int r = 0; r < reg; r++)
                out_[r] = new { name = names[r], n = n[r], l1Amb = eAmb[r] / wa[r], l1Bake = eBake[r] / wa[r],
                    l1Model = eModel[r] / wa[r], meanOrigMinusProbe = mOP[r] / wa[r], meanBake = mBk[r] / wa[r],
                    meanModel = mMd[r] / wa[r], meanBakeMinusFill = mBmF[r] / wa[r] };
            return out_;
        }

        // top overshoot points: bake far brighter than the original
        var worst = new List<(double Overshoot, object Row)>();
        for (int id = 0; id < nP; id++)
        {
            var p = pairs[id];
            double oAvg = (p.OR + p.OG + p.OB) / 3.0;
            double bAvg = (bake[id].R + bake[id].G + bake[id].B) / 3.0;
            double mAvg = (model[id].R + model[id].G + model[id].B) / 3.0;
            double ov = bAvg - oAvg;
            if (ov < 40) continue;
            var contrib = new double[nL];
            for (int l = 0; l < nL; l++)
                contrib[l] = gscales[l] * (fields[l][id * 3 + 0] + fields[l][id * 3 + 1] + fields[l][id * 3 + 2]) / 3.0;
            worst.Add((ov, new
            {
                id,
                x = p.X, y = p.Y, z = p.Z,
                orig = oAvg, probe = (ambient[id].R + ambient[id].G + ambient[id].B) / 3.0,
                bake = bAvg, model = mAvg,
                contrib,
            }));
        }
        var worstRows = worst.OrderByDescending(w => w.Overshoot).Take(25).Select(w => (object)w.Row).ToList();

        var report = new
        {
            nP, matched, unmatched, c, aligned,
            gscalesReport = gscales.Select(v => Math.Round(v, 4)).ToArray(),
            fiveBReplica = g5.Select(v => Math.Round(v, 4)).ToArray(),
            radfitPolishPlainLsq = radfitPlain.Select(v => Math.Round(v, 4)).ToArray(),
            radfitPolishNnls = radfitNnls.Select(v => Math.Round(v, 4)).ToArray(),
            radfitPolishPlain5 = radfitPlain5.Select(v => Math.Round(v, 4)).ToArray(),
            radfitPolishNnls5 = radfitNnls5.Select(v => Math.Round(v, 4)).ToArray(),
            deltaEPlainLsq = dEPlain.Select(v => Math.Round(v, 3)).ToArray(),
            deltaENnls = dENnls.Select(v => Math.Round(v, 3)).ToArray(),
            l1AmbientOnly = Math.Round(l1Amb, 3),
            l1BakeReconstructed = Math.Round(l1Bake, 3),
            l1BakeRun = 201.487,
            l1BakeSampled = Math.Round(l1BakeSampled, 3),
            l1BakeFillWith5bScales = Math.Round(l1BakeFill, 3),
            nBakeSampled = nSamp, nBakeFill = nFill,
            l1Model = Math.Round(l1Model, 3),
            runReportedStageA = 201.487,
            candidates = candidates.Select(kv => kv.Key + "=" + kv.Value.ToString("F2")).ToArray(),
            altLightmapL1 = altL1s.Select(kv => kv.Key + "=" + kv.Value.ToString("F2")).ToArray(),
            candEnergyOnDisk = candEnergy,
            fillFieldMassByLight = fillFieldMass.Select(v => Math.Round(v, 1)).ToArray(),
            fillFieldNzByLight = fillFieldNz,
            fillHaveCountByLight = fillHave,
            fillRawMeanByLight = fillRawSum.Select(v => Math.Round(v, 2)).ToArray(),
            unexplainedBakeMinusLinearSampled = Math.Round(unexplained, 3),
            weightFracUnexplainedPos = Math.Round(unexplainedPos / pairs.Sum(p => p.Weight), 4),
            weightFracUnexplainedNeg = Math.Round(unexplainedNeg / pairs.Sum(p => p.Weight), 4),
            regions = Regions(),
            worstOvershoot = worstRows,
        };
        string outPath = Path.Combine(Run, "fitrun31_diag.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[diag] n={nP} c={c:F3} l1Amb={l1Amb:F2} l1Bake(samp/fill)={l1BakeSampled:F2}/{l1BakeFill:F2} l1Model={l1Model:F2} unexplained={unexplained:F2} → {outPath}");
        // Faithfulness: the reconstruction reproduces the run's ambient L1 and
        // its 5b selection. (It does NOT reproduce 201.49 — the run's stage-A
        // modelFill used the POST-radfit gscales, which PolishedRms re-solves
        // with unconstrained LSQ on the near-singular wall-washer block; the
        // split above quantifies that. radfitPolish* shows the blow-up.)
        Assert.True(Math.Abs(l1Amb - 103.0216) < 0.5, $"l1AmbientOnly {l1Amb:F3} != 103.02");
        double maxDev = 0;
        for (int l = 0; l < nL; l++) maxDev = Math.Max(maxDev, Math.Abs(g5[l] - gscales[l]));
        Assert.True(maxDev < 0.02, $"5b replica deviates from report scales by {maxDev:F4}");
    }
}
