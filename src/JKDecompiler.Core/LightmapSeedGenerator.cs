using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace JKDecompiler.Core
{
    /// <summary>
    /// Lightmap-derived seed generator (M2.5 default seed path).
    ///
    /// Stage-1 grid recovery (<see cref="LightRecovery"/>) is positionally wrong
    /// for the fit — grid seeds land 300–1300u off the true light positions, so
    /// the per-light gate drops them all (kept=0 on academy1). The fit's ground
    /// truth is the baked lightmap, so the default seed path derives seeds from
    /// it directly: per lit face, the bright texel clusters in the face's
    /// lightmap footprint are the pools of one physical light. Per pool, the
    /// 1D brightness profile along the pool's major axis is fit against the
    /// on-surface JKA falloff model
    ///     b(d) = floor + L · max(0, 1 - (d²+h²)/r²), d ≤ √(r²-h²)
    /// (h = apex height above the surface, r = radius, L = intensity) by grid
    /// search over (h, r), emitting the fitted apex at the pool centroid +
    /// normal·h. The fit refits intensity/color/radius per light, so the
    /// emitted values are starting points, not answers.
    ///
    /// Emission format is the same JSON the external <c>--seeds</c> loader
    /// consumes: <c>{map, source, seeds:[{name, position, color, level, radius, style, normal}]}</c>.
    /// </summary>
    public static class LightmapSeedGenerator
    {
        /// <summary>Fixed apex height as the fallback when the (L,h,r) fit has
        /// nothing to latch onto.</summary>
        public const double Height = 128.0;

        /// <summary>Minimum texels in a bright cluster to count as a pool.</summary>
        public const int MinPoolTexels = 4;

        /// <summary>Maximum pools emitted (per map) — well-separated means the
        /// per-light fields are distinct bases, so ten is plenty for a fit.</summary>
        public const int MaxSeeds = 10;

        /// <summary>Minimum separation between kept seeds (world units). Within
        /// this distance the fields are near-collinear (same pool seen from two
        /// adjacent faces), which is what broke multi-light selection in v40.</summary>
        public const double MinSpacing = 1000.0;

        /// <summary>Hard cap on the fitted radius — pools don't extend past
        /// room-scale falloff; 4096u would wash the whole map and drown the
        /// real pool shape in the model.</summary>
        public const double MaxRadius = 2048.0;

        public class GeneratedSeed
        {
            public string Name = "";
            public Vector3 Position;
            public Vector3 Color = Vector3.One;    // 0..255 rgb
            public int Level = 1024;
            public int Radius = 1024;
            public int Style;
            public Vector3 Normal = Vector3.Zero;   // apex normal (for in-solid pushes)
        }

        /// <summary>
        /// Derive candidate lights from the baked lightmap blocks.
        /// </summary>
        public static List<GeneratedSeed> Generate(BspData d)
        {
            var seeds = new List<GeneratedSeed>();
            var faces = LightFit.BuildSampleFaces(d);
            const int FaceWidth = 128;

            foreach (var sf in faces)
            {
                if (sf.Hull2D.Length < 3) continue;
                // Skip sky faces — they carry the sky wash, never a physical light
                // pool (the fit re-bakes the sky separately via SkyboxReconstruction).
                var fDef = d.Faces[sf.FaceIndex];
                var shaderName = fDef.ShaderIndex >= 0 && fDef.ShaderIndex < d.Shaders.Count
                    ? d.Shaders[fDef.ShaderIndex].Name ?? ""
                    : "";
                if (shaderName.Contains("sky", StringComparison.OrdinalIgnoreCase)) continue;
                if (sf.UMax - sf.UMin < 0.02 && sf.VMax - sf.VMin < 0.02) continue; // sub-pixel box
                if (sf.Block < 0 || sf.Block >= d.Lightmaps.Count) continue;
                var block = d.Lightmaps[sf.Block];
                if (block == null) continue;
                var data = block.Data;
                if (data.Length < FaceWidth * FaceWidth * 3) continue;

                var o = sf.FrameOrigin; var fu = sf.FrameU; var fv = sf.FrameV;
                var au = sf.AffineU; var av = sf.AffineV;
                if (au.Length < 4 || av.Length < 4) continue;
                if (Math.Abs(au[3]) > 1e8 || Math.Abs(av[3]) > 1e8) continue; // unusable sentinel
                // au = [a0, a1, 0, c]: U = a0*x + a1*y + c ; av = [b0, b1, 0, f]
                double det = au[0] * av[1] - au[1] * av[0];
                if (Math.Abs(det) < 1e-12) continue;
                double invDet = 1.0 / det;

                int tx0 = Math.Max(0, (int)Math.Floor(sf.UMin * FaceWidth));
                int tx1 = Math.Min(FaceWidth - 1, (int)Math.Ceiling(sf.UMax * FaceWidth));
                int ty0 = Math.Max(0, (int)Math.Floor(sf.VMin * FaceWidth));
                int ty1 = Math.Min(FaceWidth - 1, (int)Math.Ceiling(sf.VMax * FaceWidth));
                if (tx1 - tx0 < 1 || ty1 - ty0 < 1) continue;

                Vector3 WorldFromUv(double u, double v)
                {
                    double x = ( (u - au[3]) * av[1] - (v - av[3]) * au[1] ) * invDet;
                    double y = ( (v - av[3]) * au[0] - (u - au[3]) * av[0] ) * invDet;
                    return new Vector3(
                        (float)(o.X + fu.X * x + fv.X * y),
                        (float)(o.Y + fu.Y * x + fv.Y * y),
                        (float)(o.Z + fu.Z * x + fv.Z * y));
                }

                // per-texel brightness (mean RGB), inside the hull only
                int w = tx1 - tx0 + 1, h = ty1 - ty0 + 1;
                var bright = new double[w, h];
                var collected = new bool[w, h];
                var texels = new List<(int tx, int ty, double b)>();
                double bmax = 0;
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        double u = (tx + 0.5) / FaceWidth;
                        double v = (ty + 0.5) / FaceWidth;
                        double x = ( (u - au[3]) * av[1] - (v - av[3]) * au[1] ) * invDet;
                        double y = ( (v - av[3]) * au[0] - (u - au[3]) * av[0] ) * invDet;
                        if (!LightFit.PointInHull(sf.Hull2D, x, y, 1.0)) continue;
                        int idx = (ty * FaceWidth + tx) * 3;
                        if (idx + 2 >= data.Length) continue;
                        double b = (data[idx] + data[idx + 1] + data[idx + 2]) / 3.0;
                        bright[tx - tx0, ty - ty0] = b;
                        collected[tx - tx0, ty - ty0] = true;
                        texels.Add((tx, ty, b));
                        if (b > bmax) bmax = b;
                    }
                }
                if (texels.Count < MinPoolTexels) continue;

                double median = texels.Select(t => t.b).OrderBy(z => z).ElementAt(texels.Count / 2);
                double dynamicRange = bmax - median;
                if (dynamicRange < 12.0) continue; // no significant pool on this face
                double threshold = median + 0.30 * dynamicRange;

                // connected components over threshold texels (8-connectivity)
                var visited = new bool[w, h];
                var pools = new List<(List<(int tx, int ty, double b)> texels, double peak, int peakTx, int peakTy)>();
                for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    if (!collected[tx, ty] || visited[tx, ty]) continue;
                    if (bright[tx, ty] < threshold) continue;
                    var stack = new Stack<(int x, int y)>();
                    stack.Push((tx, ty)); visited[tx, ty] = true;
                    var pool = new List<(int tx, int ty, double b)>();
                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        pool.Add((tx0 + cx, ty0 + cy, bright[cx, cy]));
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (!collected[nx, ny] || visited[nx, ny]) continue;
                            if (bright[nx, ny] < threshold) continue;
                            visited[nx, ny] = true;
                            stack.Push((nx, ny));
                        }
                    }
                    if (pool.Count < MinPoolTexels) continue;
                    var peak = pool.MaxBy(p => p.b);
                    pools.Add((pool, peak.b, peak.tx, peak.ty));
                }
                if (pools.Count == 0) continue;

                // apex normal: average of stored vertex normals (authoritative),
                // falling back to the cross-product normal.
                var f = d.Faces[sf.FaceIndex];
                Vector3 n = Vector3.Zero; int nUsed = 0;
                for (int v = 0; v < f.NumVertices; v++)
                {
                    var idx = f.FirstVertexIndex + v;
                    if (idx < 0 || idx >= d.Vertices.Count) continue;
                    var vn = d.Vertices[idx].Normal;
                    if (vn.LengthSquared() < 0.25f) continue; // degenerate stored normal
                    n += vn; nUsed++;
                }
                if (nUsed > 0) n = Vector3.Normalize(n);
                else n = Vector3.Normalize(new Vector3((float)sf.Normal.X, (float)sf.Normal.Y, (float)sf.Normal.Z));

                double floor = median;
                foreach (var (pool, peakB, peakTx, peakTy) in pools)
                {
                    if (pool.Count < 8) continue; // need enough points for a profile fit

                    // world positions of pool texels
                    var pts = pool.Select(p => WorldFromUv((p.tx + 0.5) / FaceWidth, (p.ty + 0.5) / FaceWidth)).ToArray();
                    var centroid = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Length;

                    // 1D profile along the pool's major axis (2D local frame)
                    Vector2 LocalXY(Vector3 w)
                    {
                        double dx = w.X - o.X, dy = w.Y - o.Y, dz = w.Z - o.Z;
                        return new Vector2(
                            (float)(dx * fu.X + dy * fu.Y + dz * fu.Z),
                            (float)(dx * fv.X + dy * fv.Y + dz * fv.Z));
                    }
                    var local = pts.Select(LocalXY).ToArray();
                    var localCentroid = LocalXY(centroid);
                    double xx = 0, yy = 0, xy = 0;
                    foreach (var lp in local)
                    {
                        double dX = lp.X - localCentroid.X, dY = lp.Y - localCentroid.Y;
                        xx += dX * dX; yy += dY * dY; xy += dX * dY;
                    }
                    xx /= local.Length; yy /= local.Length; xy /= local.Length;
                    double tr = xx + yy, det2 = xx * yy - xy * xy;
                    if (tr < 1e-6) continue;
                    double disc = Math.Sqrt(Math.Max(0.0, tr * tr / 4 - det2));
                    double lambda = tr / 2 + disc;
                    double theta = Math.Atan2(lambda - xx, xy);
                    var axis = new Vector2((float)Math.Cos(theta), (float)Math.Sin(theta));
                    var profile = new List<(double s, double b, int tx, int ty)>();
                    for (int i = 0; i < pts.Length; i++)
                    {
                        double s = (local[i].X - localCentroid.X) * axis.X + (local[i].Y - localCentroid.Y) * axis.Y;
                        profile.Add((s, pool[i].b, pool[i].tx, pool[i].ty));
                    }

                    // grid search (h, r); L solved in closed form per (h, r)
                    double bestSse = double.MaxValue, bestH = 128.0, bestR = 1024.0, bestL = 1.0;
                    foreach (var hh in new[] { 0.0, 32.0, 64.0, 128.0, 192.0, 256.0, 384.0, 512.0 })
                    {
                        foreach (var rr in new[] { 128.0, 256.0, 512.0, 1024.0, 1536.0, 2048.0, 3072.0, 4096.0 })
                        {
                            if (rr <= hh + 32.0) continue;
                            double sumNum = 0, sumDen = 0;
                            foreach (var (s, b, tx, ty) in profile)
                            {
                                double inv = 1.0 - (s * s + hh * hh) / (rr * rr);
                                if (inv <= 0) continue;
                                sumNum += inv * (b - floor);
                                sumDen += inv * inv;
                            }
                            if (sumDen <= 1e-9) continue;
                            double L = sumNum / sumDen;
                            double sse = 0;
                            foreach (var (s, b, tx, ty) in profile)
                            {
                                double inv = 1.0 - (s * s + hh * hh) / (rr * rr);
                                if (inv < 0) inv = 0;
                                double pred = floor + L * inv;
                                double res = b - pred;
                                sse += res * res;
                            }
                            if (sse < bestSse) { bestSse = sse; bestH = hh; bestR = rr; bestL = L; }
                        }
                    }
                    double fittedH = Math.Clamp(bestH, 32.0, 512.0);
                    double fittedR = Math.Min(MaxRadius, Math.Max(bestR, fittedH + 32.0));

                    // color from the top-5% bright texels in the pool
                    int topN = Math.Max(1, pool.Count / 20);
                    var top = pool.OrderByDescending(p => p.b).Take(topN).ToArray();
                    double cr = 0, cg = 0, cb = 0;
                    foreach (var (tx, ty, b) in top)
                    {
                        int i = (ty * FaceWidth + tx) * 3;
                        if (i + 2 < data.Length)
                        {
                            cr += data[i]; cg += data[i + 1]; cb += data[i + 2];
                        }
                    }
                    double cnt = Math.Max(1, top.Length);
                    cr /= cnt; cg /= cnt; cb /= cnt;

                    var apex = centroid + n * (float)fittedH;
                    seeds.Add(new GeneratedSeed
                    {
                        Name = $"lm_{seeds.Count}",
                        Position = apex,
                        Color = new Vector3((float)Math.Clamp(cr, 0, 255), (float)Math.Clamp(cg, 0, 255), (float)Math.Clamp(cb, 0, 255)),
                        Level = 1024,
                        Radius = (int)Math.Round(fittedR),
                        Style = 0,
                        Normal = n,
                    });
                }
            }

            // dedupe: keep strong, well-separated pools only. Within
            // MinSpacing the fields become near-collinear bases, which is
            // exactly the failure mode v40 documented (sw_01 proven good
            // alone, dropped in multi-light runs because greedy absorbs its
            // signal). Keeping one bright pool per loose cluster keeps the
            // bases distinct.
            var deduped = new List<GeneratedSeed>();
            foreach (var s in seeds.OrderByDescending(s => s.Color.X + s.Color.Y + s.Color.Z))
            {
                bool dup = false;
                foreach (var kept in deduped)
                {
                    if (Vector3.Distance(kept.Position, s.Position) < MinSpacing) { dup = true; break; }
                }
                if (!dup) deduped.Add(s);
                if (deduped.Count >= MaxSeeds) break;
            }
            return deduped;
        }

        /// <summary>Serialize seeds to the external-seeds JSON format (same shape the
        /// <c>--seeds</c> loader consumes).</summary>
        public static void WriteJson(List<GeneratedSeed> seeds, string mapLabel, string outPath)
        {
            var doc = new Dictionary<string, object?>
            {
                ["map"] = mapLabel,
                ["source"] = "lightmap-derived (LightmapSeedGenerator)",
                ["seeds"] = seeds.Select(s => new Dictionary<string, object?>
                {
                    ["name"] = s.Name,
                    ["position"] = new[] { s.Position.X, s.Position.Y, s.Position.Z },
                    ["color"] = new[] { s.Color.X, s.Color.Y, s.Color.Z },
                    ["level"] = s.Level,
                    ["radius"] = s.Radius,
                    ["style"] = s.Style,
                    ["normal"] = new[] { s.Normal.X, s.Normal.Y, s.Normal.Z },
                }).ToList(),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
