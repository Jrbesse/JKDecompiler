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
    /// the per-light gate drops them all. The fit's ground truth is the baked
    /// lightmap, so the default seed path derives seeds from it directly:
    ///
    ///   1. per lit face, texels with mean-RGB &gt; T_BRIGHT form 8-connected
    ///      components (≥ COMP_MIN texels) — one component is one physical pool;
    ///   2. component centroids within CLUSTER_DIST merge into one cluster
    ///      (corners merge — floors well apart stay separate);
    ///   3. per cluster, the geometry is fitted against the on-surface JKA
    ///      falloff model b(d) = floor + L·max(0, 1 − (d/R)²) by coordinate
    ///      descent over (x, y, z, R): L solved in closed form per (x,y,z,R),
    ///      L1 objective over the bright texels + same-face dim tails (which
    ///      only constrain R from below);
    ///   4. long multi-pool strips are split along their major axis before the
    ///      fit (extent &gt; StripMinExtent AND aspect &gt; StripMinAspect), and
    ///      a post-fit split fires when the core corr is too weak;
    ///   5. seeds are deduped by 0.6·min(radius) separation.
    ///
    /// Emission format is the same JSON the external <c>--seeds</c> loader
    /// consumes: <c>{map, source, seeds:[{name, position, color, level, radius, style, normal}]}</c>.
    /// </summary>
    public static class LightmapSeedGenerator
    {
        /// <summary>Bright-texel threshold (mean RGB). Pools start at ≥ 8.</summary>
        public const double T_BRIGHT = 8.0;
        /// <summary>Core-texel threshold (mean RGB) — the points that drive the corr gate.</summary>
        public const double T_CORE = 50.0;
        /// <summary>Minimum bright texels per component to count as a pool.</summary>
        public const int COMP_MIN = 30;
        /// <summary>Cross-face clustering distance (world units).</summary>
        public const double CLUSTER_DIST = 60.0;
        /// <summary>Minimum core texels for a usable fit.</summary>
        public const int MIN_CORE = 25;
        /// <summary>Strip split: extent beyond this AND aspect beyond this → split.</summary>
        public const double StripMinExtent = 500.0;
        public const double StripMinAspect = 1.5;
        /// <summary>Equal-width segment width for the no-gap strip fallback.</summary>
        public const double StripSegmentExtent = 650.0;
        /// <summary>Max seeds emitted.</summary>
        public const int MaxSeeds = 20;
        /// <summary>Min core corr to keep a seed.</summary>
        public const double KeepCorr = 0.65;
        /// <summary>Min fitted intensity to keep a seed.</summary>
        public const double KeepL = 30.0;
        /// <summary>Initial apex offset along the face normal (descent moves it).
        /// Negative places the apex slightly BEHIND the wall — like the
        /// measured-good 6-washer set — so the bake is concentrated near the
        /// wall, not floating mid-room (the room-side apex lands ~0.5x the
        /// brightness at the wall and its per-light field collapses).</summary>
        public const double InitHeight = -40.0;
        /// <summary>Max fit radius (world units).</summary>
        public const double MaxRadius = 1200.0;
        /// <summary>Min fit radius (world units).</summary>
        public const double MinRadius = 128.0;

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

        private class FaceData
        {
            public int FaceIndex;
            public Vector3 Normal = Vector3.Zero;
            public Dictionary<(int Tx, int Ty), double> Bright = new();
            public Dictionary<(int Tx, int Ty), Vector3> World = new();
            public Dictionary<(int Tx, int Ty), Vector3> Rgb = new();
        }

        private class Component
        {
            public List<(int Tx, int Ty)> Texels = new();
            public List<(int Tx, int Ty)> DimTail = new();
            public int FaceIndex;
            public Vector3 Centroid = Vector3.Zero;
        }

        private class ClusterPoint
        {
            public readonly Vector3 World;
            public readonly double B;
            public readonly Vector3 Rgb;
            public readonly Vector3 FaceNormal;
            public ClusterPoint(Vector3 world, double b, Vector3 rgb, Vector3 faceNormal)
            { World = world; B = b; Rgb = rgb; FaceNormal = faceNormal; }
        }

        /// <summary>
        /// Derive candidate lights from the baked lightmap blocks.
        /// </summary>
        public static List<GeneratedSeed> Generate(BspData d)
        {
            var seeds = new List<GeneratedSeed>();
            var faces = LightFit.BuildSampleFaces(d);
            const int FaceWidth = 128;

            // Global room-side reference: entities live inside the room, so the
            // side of a face they fall on is the room. Flip each face's
            // geometry normal to point at that side — the stored vertex normals
            // are NOT trustworthy for this fixture (inconsistent across the
            // wall segments) and the cross-product winding can be flipped too.
            var entityPts = new List<Vector3>();
            foreach (var ent in d.Entities)
            {
                var o = ent.Origin;
                if (o.LengthSquared() > 0.25f) entityPts.Add(o);
            }

            // ---- 1. per-face bright texel collection ----
            var faceData = new List<FaceData>();
            foreach (var sf in faces)
            {
                if (sf.Hull2D.Length < 3) continue;
                var fDef = d.Faces[sf.FaceIndex];
                var shaderName = fDef.ShaderIndex >= 0 && fDef.ShaderIndex < d.Shaders.Count
                    ? d.Shaders[fDef.ShaderIndex].Name ?? ""
                    : "";
                if (shaderName.Contains("sky", StringComparison.OrdinalIgnoreCase)) continue;
                if (sf.UMax - sf.UMin < 0.02 && sf.VMax - sf.VMin < 0.02) continue;
                if (sf.Block < 0 || sf.Block >= d.Lightmaps.Count) continue;
                var block = d.Lightmaps[sf.Block];
                if (block == null) continue;
                var data = block.Data;
                if (data.Length < FaceWidth * FaceWidth * 3) continue;

                var o = sf.FrameOrigin; var fu = sf.FrameU; var fv = sf.FrameV;
                var au = sf.AffineU; var av = sf.AffineV;
                if (au.Length < 4 || av.Length < 4) continue;
                if (Math.Abs(au[3]) > 1e8 || Math.Abs(av[3]) > 1e8) continue;

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
                    double x = ((u - au[3]) * av[1] - (v - av[3]) * au[1]) * invDet;
                    double y = ((v - av[3]) * au[0] - (u - au[3]) * av[0]) * invDet;
                    return new Vector3(
                        (float)(o.X + fu.X * x + fv.X * y),
                        (float)(o.Y + fu.Y * x + fv.Y * y),
                        (float)(o.Z + fu.Z * x + fv.Z * y));
                }

                // Geometry cross-product normal, flipped to face the room side.
                var n = Vector3.Normalize(new Vector3((float)sf.Normal.X, (float)sf.Normal.Y, (float)sf.Normal.Z));
                if (entityPts.Count > 0)
                {
                    // signed distance of each entity from this face's plane
                    Vector3 fc = new Vector3((float)sf.Center.X, (float)sf.Center.Y, (float)sf.Center.Z);
                    int pos = 0, neg = 0;
                    foreach (var e in entityPts)
                    {
                        double dist = (e.X - fc.X) * n.X + (e.Y - fc.Y) * n.Y + (e.Z - fc.Z) * n.Z;
                        if (dist > 0) pos++; else if (dist < 0) neg++;
                    }
                    // the room side is the side with more entities; flip if needed
                    if (neg > pos) n = -n;
                }

                var fd = new FaceData { FaceIndex = sf.FaceIndex, Normal = n };
                for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    double u = (tx + 0.5) / FaceWidth;
                    double v = (ty + 0.5) / FaceWidth;
                    double x = ((u - au[3]) * av[1] - (v - av[3]) * au[1]) * invDet;
                    double y = ((v - av[3]) * au[0] - (u - au[3]) * av[0]) * invDet;
                    if (!LightFit.PointInHull(sf.Hull2D, x, y, 1.0)) continue;
                    int idx = (ty * FaceWidth + tx) * 3;
                    if (idx + 2 >= data.Length) continue;
                    double b = (data[idx] + data[idx + 1] + data[idx + 2]) / 3.0;
                    fd.Bright[(tx, ty)] = b;
                    fd.World[(tx, ty)] = WorldFromUv(u, v);
                    fd.Rgb[(tx, ty)] = new Vector3(data[idx], data[idx + 1], data[idx + 2]);
                }
                if (fd.Bright.Count < 2) continue;
                faceData.Add(fd);
            }

            if (faceData.Count == 0) return seeds;

            // ---- 2. per-face 8-connected components (bright texels ≥ T_BRIGHT) ----
            var allComponents = new List<Component>();
            foreach (var fd in faceData)
            {
                var bright = new Dictionary<(int, int), double>();
                foreach (var kv in fd.Bright)
                    if (kv.Value > T_BRIGHT) bright[kv.Key] = kv.Value;
                if (bright.Count < COMP_MIN) continue;

                var visited = new HashSet<(int, int)>();
                foreach (var k in bright.Keys.ToList())
                {
                    if (visited.Contains(k)) continue;
                    var stack = new Stack<(int, int)>();
                    stack.Push(k); visited.Add(k);
                    var comp = new Component { FaceIndex = fd.FaceIndex };
                    while (stack.Count > 0)
                    {
                        var (cx, cy) = stack.Pop();
                        comp.Texels.Add((cx, cy));
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nk = (cx + dx, cy + dy);
                            if (!bright.ContainsKey(nk) || visited.Contains(nk)) continue;
                            visited.Add(nk);
                            stack.Push(nk);
                        }
                    }
                    if (comp.Texels.Count < COMP_MIN) continue;

                    // dim tail: same-face dim texels within 2 texels (dim = ≤ T_BRIGHT)
                    var tailSet = new HashSet<(int, int)>();
                    foreach (var (tx, ty) in comp.Texels)
                    for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nk = (tx + dx, ty + dy);
                        if (fd.Bright.TryGetValue(nk, out var db) && db <= T_BRIGHT && tailSet.Add(nk))
                            comp.DimTail.Add(nk);
                    }

                    double ccx = 0, ccy = 0, ccz = 0;
                    foreach (var (tx, ty) in comp.Texels)
                    {
                        var w = fd.World[(tx, ty)];
                        ccx += w.X; ccy += w.Y; ccz += w.Z;
                    }
                    comp.Centroid = new Vector3((float)(ccx / comp.Texels.Count), (float)(ccy / comp.Texels.Count), (float)(ccz / comp.Texels.Count));
                    allComponents.Add(comp);
                }
            }

            if (allComponents.Count == 0) return seeds;

            // ---- 3. cross-face union-find clustering (60u) ----
            var parent = new int[allComponents.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int a)
            {
                while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
                return a;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }
            for (int i = 0; i < allComponents.Count; i++)
            for (int j = i + 1; j < allComponents.Count; j++)
            {
                var a = allComponents[i].Centroid; var b = allComponents[j].Centroid;
                double d2 = (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
                if (d2 < CLUSTER_DIST * CLUSTER_DIST) Union(i, j);
            }
            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < allComponents.Count; i++)
            {
                int root = Find(i);
                if (!clusters.TryGetValue(root, out var lst)) { lst = new List<int>(); clusters[root] = lst; }
                lst.Add(i);
            }

            // ---- 4. fit each cluster (strip-split first) ----
            var keptSeeds = new List<GeneratedSeed>();
            var faceByIndex = faceData.ToDictionary(f2 => f2.FaceIndex);
            var clusterList = clusters.Values
                .OrderByDescending(cluster => cluster.Sum(ci =>
                    allComponents[ci].Texels.Sum(t => faceByIndex[allComponents[ci].FaceIndex].Bright[t])))
                .ToList();

            foreach (var cluster in clusterList)
            {
                if (keptSeeds.Count >= MaxSeeds) break;
                var pts = new List<ClusterPoint>();
                foreach (var ci in cluster)
                {
                    var comp = allComponents[ci];
                    var fd = faceByIndex[comp.FaceIndex];
                    foreach (var (tx, ty) in comp.Texels)
                        pts.Add(new ClusterPoint(fd.World[(tx, ty)], fd.Bright[(tx, ty)], fd.Rgb[(tx, ty)], fd.Normal));
                    foreach (var (tx, ty) in comp.DimTail)
                        pts.Add(new ClusterPoint(fd.World[(tx, ty)], fd.Bright[(tx, ty)], fd.Rgb[(tx, ty)], fd.Normal));
                }
                if (pts.Count < 2) continue;

                var subClusters = new List<(List<ClusterPoint> pts, string tag)>();
                splitCluster(pts, "c", 0, subClusters);

                foreach (var (subPts, tag) in subClusters)
                {
                    if (keptSeeds.Count >= MaxSeeds) break;
                    var seed = FitCluster(subPts, tag);
                    if (seed != null) keptSeeds.Add(seed);
                }
            }

            // ---- 5. dedupe by 0.6*min(radius) separation ----
            var deduped = new List<GeneratedSeed>();
            foreach (var s in keptSeeds.OrderByDescending(s => s.Color.X + s.Color.Y + s.Color.Z))
            {
                bool dup = false;
                foreach (var kept in deduped)
                {
                    double minR = Math.Min(s.Radius, kept.Radius);
                    if (Vector3.Distance(kept.Position, s.Position) < 0.6 * minR) { dup = true; break; }
                }
                if (!dup) deduped.Add(s);
                if (deduped.Count >= MaxSeeds) break;
            }
            for (int i = 0; i < deduped.Count; i++) deduped[i].Name = $"lm_{i}";
            return deduped;
        }

        /// <summary>Strip split: split a cluster's core along its major axis at the
        /// widest inter-core gap (not the median — median splits thin strips into
        /// halves with &lt; MIN_CORE core texels and drops the whole band) when
        /// extent &gt; StripMinExtent AND aspect &gt; StripMinAspect.</summary>
        private static void splitCluster(List<ClusterPoint> pts, string tag, int depth,
            List<(List<ClusterPoint>, string)> outList)
        {
            if (depth >= 4) { outList.Add((pts, tag)); return; }
            var core = pts.Where(p => p.B >= T_CORE).ToList();
            if (core.Count < MIN_CORE) { outList.Add((pts, tag)); return; }
            double extX = core.Max(p => p.World.X) - core.Min(p => p.World.X);
            double extY = core.Max(p => p.World.Y) - core.Min(p => p.World.Y);
            double extZ = core.Max(p => p.World.Z) - core.Min(p => p.World.Z);
            var ext = new[] { (index: 0, value: extX), (index: 1, value: extY), (index: 2, value: extZ) }
                .OrderByDescending(e => e.value).ToArray();
            double se0 = ext[0].value, se1 = ext[1].value;
            if (se0 > StripMinExtent && se0 > StripMinAspect * Math.Max(50.0, se1))
            {
                int axis = ext[0].index;
                var sorted = core.OrderBy(p => p.World[axis]).ToList();
                double span = sorted.Last().World[axis] - sorted.First().World[axis];
                double meanGap = span / Math.Max(1, sorted.Count - 1);
                int bestIdx = -1; double bestGap = -1.0;
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    int coreA = i + 1; int coreB = sorted.Count - (i + 1);
                    if (coreA < MIN_CORE || coreB < MIN_CORE) continue;
                    double gap = sorted[i + 1].World[axis] - sorted[i].World[axis];
                    if (gap > bestGap) { bestGap = gap; bestIdx = i; }
                }
                if (bestIdx >= 0 && bestGap > Math.Max(2.0 * meanGap, 150.0))
                {
                    double splitAt = (sorted[bestIdx].World[axis] + sorted[bestIdx + 1].World[axis]) / 2.0;
                    var a = pts.Where(p => p.World[axis] <= splitAt).ToList();
                    var b = pts.Where(p => p.World[axis] > splitAt).ToList();
                    if (a.Count(p => p.B >= T_CORE) >= MIN_CORE)
                        splitCluster(a, tag + "a", depth + 1, outList);
                    else
                        outList.Add((a, tag + "a"));
                    if (b.Count(p => p.B >= T_CORE) >= MIN_CORE)
                        splitCluster(b, tag + "b", depth + 1, outList);
                    else
                        outList.Add((b, tag + "b"));
                    return;
                }
                // Fallback: no meaningful gap — split the strip at equal-width
                // segments (e.g. a long wall band with no inter-pool void).
                int nSeg = Math.Max(2, (int)Math.Ceiling(span / StripSegmentExtent));
                double segW = span / nSeg;
                double lo = sorted.First().World[axis];
                for (int i = 0; i < nSeg; i++)
                {
                    double segLo = lo + i * segW;
                    double segHi = (i == nSeg - 1) ? lo + span : lo + (i + 1) * segW;
                    var seg = pts.Where(p => p.World[axis] >= segLo && p.World[axis] <= segHi).ToList();
                    if (seg.Count == 0) continue;
                    if (seg.Count(p => p.B >= T_CORE) >= MIN_CORE)
                        splitCluster(seg, $"{tag}s{i}", depth + 1, outList);
                    else
                        outList.Add((seg, $"{tag}s{i}"));
                }
                return;
            }
            outList.Add((pts, tag));
        }

        /// <summary>Fit one (possibly split) cluster to a seed. Returns null if the
        /// fit fails (corr/L gate) or the post-fit split fires with no viable half.</summary>
        private static GeneratedSeed? FitCluster(List<ClusterPoint> pts, string tag)
        {
            var core = pts.Where(p => p.B >= T_CORE).ToList();
            if (core.Count < MIN_CORE) return null;

            var centroid = new Vector3(
                (float)core.Average(p => p.World.X),
                (float)core.Average(p => p.World.Y),
                (float)core.Average(p => p.World.Z));

            // dominant normal: vote the axis-dominant sign pattern over the core
            var votes = new Dictionary<(int, int, int), (double w, Vector3 sum)>();
            foreach (var p in core)
            {
                var n = p.FaceNormal;
                var key = ((int)Math.Sign(n.X), (int)Math.Sign(n.Y), (int)Math.Sign(n.Z));
                if (!votes.TryGetValue(key, out var cur)) votes[key] = (1.0, n);
                else votes[key] = (cur.w + 1.0, cur.sum + n);
            }
            var best = votes.OrderByDescending(kv => kv.Value.w).First().Value;
            var apexNormal = Vector3.Normalize(best.sum);
            if (apexNormal.LengthSquared() < 0.25f)
                apexNormal = pts[0].FaceNormal.LengthSquared() >= 0.25f ? pts[0].FaceNormal : Vector3.UnitZ;

            double x = centroid.X + apexNormal.X * InitHeight;
            double y = centroid.Y + apexNormal.Y * InitHeight;
            double z = centroid.Z + apexNormal.Z * InitHeight;
            var ds = core.Select(p => Dist3D(p.World, x, y, z)).OrderBy(v => v).ToList();
            double p85 = ds[(int)(0.85 * (ds.Count - 1))];
            double R = Math.Min(MaxRadius, Math.Max(MinRadius, 1.15 * p85));

            // ambient floor estimate: median dim-tail brightness
            var dim = pts.Where(p => p.B <= T_BRIGHT).Select(p => p.B).OrderBy(v => v).ToList();
            double floor = dim.Count > 0 ? dim[dim.Count / 2] : 0.0;

            double err;
            double L;
            (err, L) = ErrOf(pts, x, y, z, R, floor);
            for (int step = 0; step < 8; step++)
            {
                double s = new[] { 96.0, 48.0, 24.0, 12.0, 6.0, 3.0, 1.5, 0.75 }[step];
                bool improved = true;
                while (improved)
                {
                    improved = false;
                    for (int ax = 0; ax < 3; ax++)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        var (nx, ny, nz) = (x, y, z);
                        if (ax == 0) nx += sgn * s;
                        if (ax == 1) ny += sgn * s;
                        if (ax == 2) nz += sgn * s;
                        double dcx = nx - centroid.X, dcy = ny - centroid.Y, dcz = nz - centroid.Z;
                        if (dcx * dcx + dcy * dcy + dcz * dcz > 500.0 * 500.0) continue;
                        // Keep the apex within a tight band around the face
                        // plane (InitHeight .. -2*InitHeight, i.e. behind the
                        // wall up to ~2x that into the room): close enough that
                        // the sphere model stays meaningful, but not so far
                        // into the room that the field covers too much of the
                        // floor and the joints collapse.
                        double projN = dcx * apexNormal.X + dcy * apexNormal.Y + dcz * apexNormal.Z;
                        if (projN < InitHeight || projN > -2.0 * InitHeight) continue;
                        var (e2, L2) = ErrOf(pts, nx, ny, nz, R, floor);
                        if (e2 < err - 0.1) { x = nx; y = ny; z = nz; err = e2; L = L2; improved = true; }
                    }
                    foreach (var fR in new[] { 1.15, 0.87 })
                    {
                        double R2 = Math.Min(MaxRadius, Math.Max(MinRadius, R * fR));
                        if (Math.Abs(R2 - R) < 0.5) continue;
                        var (e2, L2) = ErrOf(pts, x, y, z, R2, floor);
                        if (e2 < err - 0.1) { R = R2; err = e2; L = L2; improved = true; }
                    }
                }
            }

            // correlation of the fitted model vs the core texels
            double mT = core.Average(p => p.B);
            double mM = core.Average(p => L * Falloff(Dist3D(p.World, x, y, z), R));
            double num = 0, denT = 0, denM = 0;
            foreach (var p in core)
            {
                double mVal = L * Falloff(Dist3D(p.World, x, y, z), R);
                double tDev = p.B - mT;
                double mDev = mVal - mM;
                num += tDev * mDev; denT += tDev * tDev; denM += mDev * mDev;
            }
            double corr = num / (Math.Sqrt(denT * denM) + 1e-9);

            var dsFit = core.Select(p => Dist3D(p.World, x, y, z)).OrderBy(v => v).ToList();
            double p95 = dsFit[Math.Min(dsFit.Count - 1, (int)(0.95 * dsFit.Count))];


            // Wide wall-band-type segments: the single-sphere model cannot fit
            // a 2-wide strip (see the control washers — 6 per wall with R=900),
            // so let these through and let the fit-lights pipeline (joint NNLS
            // + posfit) decide. Narrow pools still go through the corr gate.
            double segExtX = core.Count > 0 ? core.Max(p => p.World.X) - core.Min(p => p.World.X) : 0.0;
            double segExtZ = core.Count > 0 ? core.Max(p => p.World.Z) - core.Min(p => p.World.Z) : 0.0;
            double segExt = Math.Max(segExtX, segExtZ);
            bool wideSegment = segExt > 500.0 && core.Count >= 3 * MIN_CORE;
            if (wideSegment || (corr >= KeepCorr && L >= KeepL))
            {
                int topN = Math.Max(1, core.Count / 20);
                var top = core.OrderByDescending(p => p.B).Take(topN).ToArray();
                var avg = top.Aggregate(new Vector3(0, 0, 0), (a, p) => a + p.Rgb) / top.Length;
                // Color stays as 0..1 ratios (max = 1); level matches the
                // control convention (level/255 ≈ initial bake intensity).
                // The loader turns this into Scale = level * color / 255 —
                // baking with a 0..255 color and a raw Level (e.g. 245) made
                // every single-light field ~500x too bright and the joint NNLS
                // collapsed to 0 on every seed.
                double cmax = Math.Max(avg.X, Math.Max(avg.Y, avg.Z));
                var colorOut = cmax > 1e-9
                    ? new Vector3((float)(avg.X / cmax), (float)(avg.Y / cmax), (float)(avg.Z / cmax))
                    : new Vector3(1f, 1f, 1f);
                return new GeneratedSeed
                {
                    Position = new Vector3((float)x, (float)y, (float)z),
                    Color = colorOut,
                    Level = 128,
                    Radius = (int)Math.Round(Math.Max(R, MinRadius)),
                    Normal = apexNormal,
                    Style = 0,
                };
            }

            // post-fit split: multi-pool core
            if (p95 > 1.6 * R)
            {
                var ext = new[] {
                    (ext: core.Max(p => p.World.X) - core.Min(p => p.World.X), axis: 0),
                    (ext: core.Max(p => p.World.Y) - core.Min(p => p.World.Y), axis: 1),
                    (ext: core.Max(p => p.World.Z) - core.Min(p => p.World.Z), axis: 2),
                }.OrderByDescending(e => e.ext).First();
                var sortedCore = core.OrderBy(p => p.World[ext.axis]).ToList();
                double span = sortedCore.Last().World[ext.axis] - sortedCore.First().World[ext.axis];
                double meanGap = span / Math.Max(1, sortedCore.Count - 1);
                int bestIdx = -1; double bestGap = -1.0;
                for (int i = 0; i + 1 < sortedCore.Count; i++)
                {
                    int coreA = i + 1; int coreB = sortedCore.Count - (i + 1);
                    if (coreA < MIN_CORE || coreB < MIN_CORE) continue;
                    double gap = sortedCore[i + 1].World[ext.axis] - sortedCore[i].World[ext.axis];
                    if (gap > bestGap) { bestGap = gap; bestIdx = i; }
                }
                if (bestIdx >= 0 && bestGap > Math.Max(2.0 * meanGap, 150.0))
                {
                    double splitAt = (sortedCore[bestIdx].World[ext.axis] + sortedCore[bestIdx + 1].World[ext.axis]) / 2.0;
                    var a = pts.Where(p => p.World[ext.axis] <= splitAt).ToList();
                    var b = pts.Where(p => p.World[ext.axis] > splitAt).ToList();
                    if (a.Count(p => p.B >= T_CORE) >= MIN_CORE) return FitCluster(a, tag + "a");
                    if (b.Count(p => p.B >= T_CORE) >= MIN_CORE) return FitCluster(b, tag + "b");
                }
            }
            return null;
        }

        private static double Dist3D(Vector3 w, double x, double y, double z)
            => Math.Sqrt((w.X - x) * (w.X - x) + (w.Y - y) * (w.Y - y) + (w.Z - z) * (w.Z - z));

        private static double Falloff(double d, double R)
        {
            if (d <= 0) return 1.0;
            if (d >= R) return 0.0;
            return Math.Max(0.0, 1.0 - (d * d) / (R * R));
        }

        private static (double err, double L) ErrOf(List<ClusterPoint> pts, double x, double y, double z, double R, double floor)
        {
            double sumNum = 0, sumDen = 0;
            foreach (var p in pts)
            {
                double d = Dist3D(p.World, x, y, z);
                double f = Falloff(d, R);
                if (f <= 0) continue;
                sumNum += f * (p.B - floor);
                sumDen += f * f;
            }
            double L = sumDen > 1e-9 ? Math.Max(0.0, sumNum / sumDen) : 0.0;
            double err = 0;
            foreach (var p in pts)
            {
                double d = Dist3D(p.World, x, y, z);
                double f = Falloff(d, R);
                double pred = floor + L * f;
                err += Math.Abs(p.B - pred);
            }
            return (err, L);
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
