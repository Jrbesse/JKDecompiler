using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace JKDecompiler.Core
{
    /// <summary>One recovered light candidate (M2.5 stage 1: grid seeding).</summary>
    public class RecoveredLight
    {
        public string Name = "";
        public Vector3 Position = Vector3.Zero;
        public Vector3 Color = Vector3.One; // 0..255 rgb (ratio-preserved)
        public int? Level;                  // estimated JA "light" level (null = fully saturated grid)
        public int LevelLowerBound;         // conservative bound from saturated samples
        public int Radius = 1024;           // assumed q3map2 default; stage 2 refits
        public int Style;                   // dominant light style of the cluster
        public int CellCount;
        public float RayDistMedian;         // median distance from Position to each cluster ray
        public float TPositiveFrac;         // fraction of rays pointing toward Position
        public double LevelConsistency;     // IQR/median of implied per-sample levels (1 = not computed)
        public double AngleMedianDeg;       // median angle between evidence rays and apex direction
        public double AngleP90Deg;          // 90th percentile of that angle
        public List<(Vector3 Pos, float M)> Evidence = new(); // per-sample (pos, max directed channel)
    }

    public class RejectedCluster
    {
        public int CellCount;
        public string Reason = "";
        public Vector3 Position = Vector3.Zero;
        public float RayDistMedian;
        public float TPositiveFrac;
        public Vector3 Color = Vector3.Zero;
    }

    public class LightRecoveryResult
    {
        public bool GridValid;
        public string? GridNote;
        public int[] GridDims = { 0, 0, 0 };
        public Vector3 GridOrigin = Vector3.Zero;
        public int Corners;                 // world-grid corners (= lightarray length when valid)
        public int PaletteCells;
        public int SamplesUsed;
        public int SamplesSkippedFaint;
        public List<RecoveredLight> Lights = new();
        public List<RejectedCluster> Rejected = new();

        // overlay support: per-array-index dominant sample's cluster id (-1 = none)
        // and its directed magnitude (0..255)
        public int[] CornerCluster = Array.Empty<int>();
        public int[] CornerMag = Array.Empty<int>();
        // per cluster id (list index): color + accepted flag + name
        public Dictionary<int, (byte R, byte G, byte B, bool Accepted, string Name)> ClusterInfo = new();
    }

    /// <summary>
    /// M2.5 stage 1 — light entity recovery by grid seeding.
    ///
    /// Grid model (verified against OpenJK rd-vanilla + q3map2 (Garux fork) light.cpp,
    /// 2026-11-21):
    ///   world corner (ix,iy,iz) -> lightGridArray[ix + iy*W + iz*W*H] = u16 palette
    ///   index. Palette cell = 30 B: ambient[4][3], directLight[4][3], styles[4]
    ///   (0xff = LS_LSNONE = "no contribution in this slot"), latLong[2].
    ///   The latLong bytes are the ONLY direction data (the directed color is
    ///   intensity, not direction): q3map2 NormalToLatLong (nrc
    ///   libs/mathlib/mathlib.c:438) encodes lat = atan2(n.y, n.x) in degrees
    ///   * 255/360, lng = acos(n.z) * 255/360 (bytes[0]=lng, bytes[1]=lat); the
    ///   engine decodes via its 1024-entry sine table (tr_light.cpp):
    ///     dir = (cos a * sin b, sin a * sin b, cos b),
    ///     a = 2*pi*lat*4/1024, b = 2*pi*lng*4/1024.
    ///   dir is the normalized vector from the corner TOWARD the dominant light
    ///   (tr_local.h:129 "normalized direction towards light"). It is a
    ///   contribution-weighted average over all lights hitting the corner, so
    ///   cells between two lights carry a blended (unreliable) direction.
    ///   directLight[j] ~= lightColor * (level/255) * atten(dist) * d where
    ///   atten = max(0, 1 - (dist/radius)^2) (q3map2 default attenuation 1,
    ///   radius 1024) and d = 0.25 + 0.75*dot(lightDir, primaryDir) ~= 1 for
    ///   the dominant light (light.cpp:1612ff).
    ///
    /// Algorithm:
    ///   1. per corner + active slot: a sample (position, direction, directed
    ///      color, style). Wall samples (styles[0]==0xff) and faint directed
    ///      light are skipped.
    ///   2. union-find over samples: two samples from adjacent corners (one
    ///      grid step) link when their directed colors agree (<= 12 deg) AND
    ///      their rays intersect forward (common apex within tolerance).
    ///      Rays from the same light intersect exactly at the emitter, so this
    ///      is a strict same-light test that is direction-rotation invariant
    ///      (a point light's direction varies continuously across the map, so
    ///      bucketing by absolute direction would fragment it). The test also
    ///      links blend-zone cells between lights of similar hue, so real maps
    ///      produce one or two huge "superclusters" per dominant hue family.
    ///   3. per cluster, peel lights one at a time: solve an apex
    ///      (iterate-closest-point + iteratively reweighted trim), then remove
    ///      that light's inliers and repeat (up to MaxPeels). The apex solve
    ///      starts from the inlier centroid; if that fails (a supercluster has
    ///      no single common apex), a deterministic RANSAC locks on: random
    ///      ray triples give least-squares candidate apexes, keep the trial
    ///      with the most inliers, then refine.
    ///   4. an apex is accepted with >= MinCells inliers, >= 70% forward rays
    ///      and median ray distance <= MaxRayDist. Color = normalized centroid;
    ///      level = median(255 * m_max / atten) over unsaturated samples
    ///      (saturated bytes 255 only give a lower bound); style = dominant.
    ///   5. post-pass: a candidate whose samples are all consistent with a
    ///      single light at a larger same-color candidate's position (level-
    ///      free test: implied-level IQR/median <= 30% at that position) is a
    ///      fragment or a virtual lens apex, not a separate emitter — absorbed.
    ///
    /// Stage-1 contract (M2.5): this is a candidate generator, not the final
    /// answer. On dense real maps it deliberately over-seeds — the peel loop
    /// can lock on to virtual foci in blend tails, which no single geometric
    /// threshold separates cleanly from small real lights (verified on
    /// academy1/kor1/t1_sour/t1_inter/yavin1b). Every seed therefore carries
    /// quality scores in the sidecar (levelConsistency, angleMedianDeg,
    /// angleP90Deg, rayDistMedian, tPositiveFrac, cells) for stage 2's
    /// closed-loop fit against the baked lightmap blocks to rank and reject.
    /// Residues (clusters whose peel cap was hit) are recorded, not lost.
    /// </summary>
    public class LightRecovery
    {
        public float DirectedMin = 5f;      // min directed magnitude (0..255) to be usable
        public int MinCells = 6;            // min cluster size to accept
        public float MaxRayDist = 32f;      // max median ray->position distance (world units)
        public float MinTPositiveFrac = 0.7f;
        public int MaxIterations = 80;
        public float ColorAngleDeg = 12f;   // max angle between two linked samples' directed colors
        public float RayPairTol = 24f;      // max min-distance between two linked rays' closest points
        public int Radius = 1024;           // assumed light radius for the level estimate
        public int LevelMin = 255;
        public int LevelMax = 12000;
        public int MaxPeels = 48;              // max lights peeled from one supercluster
        public int RansacTrials = 64;          // random ray-triple lock-on attempts per apex
        public int MaxSeeds = 0;               // 0 = accept all; >0 = keep only the best-N seeds by quality

        private struct Sample
        {
            public int Id;
            public Vector3 Pos;
            public Vector3 Dir;
            public float R, G, B;
            public int Style;
            public int Corner;
        }

        private class Cluster
        {
            public List<Sample> Samples = new();
            public double SumR, SumG, SumB;
        }

        public LightRecoveryResult Recover(BspData data)
        {
            var res = new LightRecoveryResult();
            var m0 = data.Models.Count > 0 ? data.Models[0] : null;
            if (m0 == null || data.LightArray.Data.Length == 0 || data.LightGrid.Count == 0)
            {
                res.GridValid = false;
                res.GridNote = "no world model, lightgrid palette, or lightarray";
                return res;
            }

            float[] step = { 64f, 64f, 128f };
            int W = LightmapExtractor.GridDim(m0, 0);
            int H = LightmapExtractor.GridDim(m0, 1);
            int V = LightmapExtractor.GridDim(m0, 2);
            res.GridDims = new[] { W, H, V };
            res.GridOrigin = new Vector3(
                step[0] * (float)Math.Ceiling(m0.Mins.X / step[0]),
                step[1] * (float)Math.Ceiling(m0.Mins.Y / step[1]),
                step[2] * (float)Math.Ceiling(m0.Mins.Z / step[2]));
            int expected = W * H * V;
            res.Corners = expected;
            res.PaletteCells = data.LightGrid.Count;
            res.GridValid = true;

            if (data.LightArray.Data.Length != expected)
            {
                res.GridValid = false;
                res.GridNote = $"lightarray length {data.LightArray.Data.Length} != grid dims " +
                    $"{W}x{H}x{V} = {expected} (engine treats this as a warning and disables " +
                    "dynamic grid lighting — no seeding possible)";
                return res;
            }

            // 1. samples
            var samples = new List<Sample>();
            var cornerCluster = new int[expected];
            var cornerMag = new int[expected];
            Array.Fill(cornerCluster, -1);
            int faint = 0;
            for (int iz = 0; iz < V; iz++)
            for (int iy = 0; iy < H; iy++)
            for (int ix = 0; ix < W; ix++)
            {
                int corner = ix + iy * W + iz * W * H;
                ushort palIdx = data.LightArray.Data[corner];
                if (palIdx >= data.LightGrid.Count) continue;
                var cell = data.LightGrid[palIdx];
                if (cell.Styles[0] == 0xff) continue; // wall sample
                var dir = DecodeLatLong(cell.LatLong);
                var pos = res.GridOrigin + new Vector3(ix * step[0], iy * step[1], iz * step[2]);
                for (int j = 0; j < 4; j++)
                {
                    byte style = cell.Styles[j];
                    if (style == 0xff) break;
                    float r = cell.Directed[j * 3], g = cell.Directed[j * 3 + 1], b = cell.Directed[j * 3 + 2];
                    float mag = Math.Max(r, Math.Max(g, b));
                    if (mag < DirectedMin) { faint++; continue; }
                    samples.Add(new Sample { Id = samples.Count, Pos = pos, Dir = dir, R = r, G = g, B = b, Style = style, Corner = corner });
                }
            }
            res.SamplesUsed = samples.Count;
            res.SamplesSkippedFaint = faint;
            res.CornerCluster = cornerCluster;
            res.CornerMag = cornerMag;

            // 2. union-find: link same-color samples from adjacent corners whose
            //    rays intersect forward at a common apex (the emitter)
            var byCorner = new Dictionary<int, List<Sample>>();
            for (int si = 0; si < samples.Count; si++)
            {
                var smp = samples[si];
                if (!byCorner.TryGetValue(smp.Corner, out var l))
                {
                    l = new List<Sample>();
                    byCorner[smp.Corner] = l;
                }
                l.Add(smp);
            }
            var parent = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++) parent[i] = i;
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            float cosPair = (float)Math.Cos(ColorAngleDeg * Math.PI / 180f);
            for (int si = 0; si < samples.Count; si++)
            {
                var a = samples[si];
                int ax = a.Corner % W;
                int ay = (a.Corner / W) % H;
                int az = a.Corner / (W * H);
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    int nx = ax + dx, ny = ay + dy, nz = az + dz;
                    if (nx < 0 || nx >= W || ny < 0 || ny >= H || nz < 0 || nz >= V) continue;
                    int ncorner = nx + ny * W + nz * W * H;
                    if (!byCorner.TryGetValue(ncorner, out var nlist)) continue;
                    foreach (var b in nlist)
                    {
                        if (b.Corner == a.Corner) continue; // same position: rays are identical lines
                        if (!SameColor(a, b, cosPair)) continue;
                        if (RaysIntersect(a, b, out _, out _)) Union(si, b.Id);
                    }
                }
            }
            var clusters = new Dictionary<int, List<Sample>>();
            for (int si = 0; si < samples.Count; si++)
            {
                int root = Find(si);
                if (!clusters.TryGetValue(root, out var cl))
                {
                    cl = new List<Sample>();
                    clusters[root] = cl;
                }
                cl.Add(samples[si]);
            }
            var clusterList = new List<Cluster>();
            var sampleCluster = new int[samples.Count];
            foreach (var cl in clusters.Values)
            {
                var c = new Cluster();
                c.Samples = cl;
                int id = clusterList.Count;
                foreach (var smp in cl)
                {
                    c.SumR += smp.R; c.SumG += smp.G; c.SumB += smp.B;
                    sampleCluster[smp.Id] = id;
                }
                clusterList.Add(c);
            }

            // overlay: remember the strongest sample per corner
            for (int si = 0; si < samples.Count; si++)
            {
                var smp = samples[si];
                int mag = (int)Math.Max(smp.R, Math.Max(smp.G, smp.B));
                if (mag > cornerMag[smp.Corner])
                {
                    cornerMag[smp.Corner] = mag;
                    cornerCluster[smp.Corner] = sampleCluster[si];
                }
            }

            // 3 + 4. ray intersection + acceptance
            float mapExtent = Math.Max(
                Math.Max(Math.Abs(m0.Maxs.X - m0.Mins.X), Math.Abs(m0.Maxs.Y - m0.Mins.Y)),
                Math.Abs(m0.Maxs.Z - m0.Mins.Z));
            for (int id = 0; id < clusterList.Count; id++)
                SolveCluster(clusterList[id], id, res, mapExtent);

            AbsorbVirtualApexes(res);

            // Quality gate: order seeds best-first by the same metric stage 2
            // uses (level consistency, forward-ray fraction, evidence size)
            // and keep only the top MaxSeeds when the caller asks for one.
            res.Lights = res.Lights
                .OrderBy(l => l.LevelConsistency)
                .ThenByDescending(l => l.TPositiveFrac)
                .ThenByDescending(l => l.CellCount)
                .ToList();
            if (MaxSeeds > 0 && res.Lights.Count > MaxSeeds)
                res.Lights = res.Lights.Take(MaxSeeds).ToList();
            for (int i = 0; i < res.Lights.Count; i++)
                res.Lights[i].Name = $"recov_{i}";

            return res;
        }

        /// <summary>
        /// A candidate whose samples are all consistent with a single light
        /// at a LARGER same-color candidate's position is not a separate
        /// emitter — it is a fragment of that light's footprint, or (common
        /// in multi-light overlap "lens" regions) the virtual focus where
        /// the blended primary directions locally converge. The test is
        /// level-free (implied-level spread at the other candidate's
        /// position), so it works even when levels are clamped or saturated.
        /// Stage 2's closed-loop fit is the final arbiter either way.
        /// </summary>
        private void AbsorbVirtualApexes(LightRecoveryResult res)
        {
            for (int i = 0; i < res.Lights.Count; i++)
            {
                var a = res.Lights[i];
                foreach (var b in res.Lights)
                {
                    if (b == a || b.CellCount < 2 * a.CellCount) continue;
                    if (b.Style != a.Style) continue;
                    var ac = Vector3.Normalize(a.Color);
                    var bc = Vector3.Normalize(b.Color);
                    if (Math.Acos(Math.Clamp(Vector3.Dot(ac, bc), -1f, 1f)) > ColorAngleDeg * Math.PI / 180f)
                        continue;
                    // implied level of a's samples if a light sat at b's position
                    var vals = new List<double>();
                    foreach (var (P, m) in a.Evidence)
                    {
                        if (m >= 255f) continue; // saturated byte: no level info
                        float d = Vector3.Distance(b.Position, P);
                        double dr = d / (double)Radius;
                        double atten = Math.Max(0.0, 1.0 - dr * dr);
                        if (atten < 0.05) continue;
                        vals.Add(255.0 * m / atten);
                    }
                    if (vals.Count < 6) continue;
                    vals.Sort();
                    double med = vals[vals.Count / 2];
                    if (med <= 0) continue;
                    double iqr = vals[(int)(vals.Count * 0.75)] - vals[(int)(vals.Count * 0.25)];
                    if (iqr / med > 0.3) continue; // not a single light at b's position
                    double implied = med;
                    res.Rejected.Add(new RejectedCluster
                    {
                        CellCount = a.CellCount,
                        Reason = $"implied level constant ({implied:0.}) at {b.Name}'s position " +
                                 $"({iqr / med:P0} spread, {vals.Count} samples) — fragment or virtual " +
                                 "lens apex of that light, not a separate emitter",
                        Position = a.Position, RayDistMedian = a.RayDistMedian,
                        TPositiveFrac = a.TPositiveFrac,
                        Color = a.Color
                    });
                    res.Lights.RemoveAt(i);
                    i--;
                    break;
                }
            }
            for (int i = 0; i < res.Lights.Count; i++)
                res.Lights[i].Name = $"recov_{i}";
        }

        private void SolveCluster(Cluster c, int id, LightRecoveryResult res, float mapExtent)
        {
            var n0 = (float)c.Samples.Count;
            double cr = c.SumR / n0, cg = c.SumG / n0, cb = c.SumB / n0;
            var color = NormalizeColor((float)cr, (float)cg, (float)cb);
            // clusters are solved in id order, so append == index id
            res.ClusterInfo[id] = ((byte)color.X, (byte)color.Y, (byte)color.Z, false, "");

            if (c.Samples.Count < MinCells)
            {
                var pc = Vector3.Zero;
                foreach (var x in c.Samples) pc += x.Pos;
                pc /= c.Samples.Count;
                res.ClusterInfo[id] = ((byte)color.X, (byte)color.Y, (byte)color.Z, false, $"too-few-cells({c.Samples.Count})");
                res.Rejected.Add(new RejectedCluster
                {
                    CellCount = c.Samples.Count, Reason = $"fewer than {MinCells} cells",
                    Position = pc, RayDistMedian = 0, TPositiveFrac = 0,
                    Color = new Vector3(color.X, color.Y, color.Z)
                });
                return;
            }

            // Peel loop: real maps with a dominant light hue form one
            // supercluster per hue family (smooth direction gradients link
            // the whole map transitively). Repeatedly find the best
            // converging apex in the working set, accept it, and peel its
            // evidence out — the next iteration finds the next light.
            var work = new List<Sample>(c.Samples);
            int peels = 0;
            var rng = new Random(0x5EED ^ id);
            while (work.Count >= MinCells && peels < MaxPeels)
            {
                // 1. centroid start (fast path: the cluster is one cone)
                var Lc = Vector3.Zero;
                foreach (var x in work) Lc += x.Pos;
                Lc /= work.Count;
                bool ok = TryApex(work, Lc, mapExtent, out var s, out var L, out var rayMed, out var tfrac);

                // 2. RANSAC lock-on: a supercluster (dominant light hue,
                // whole-map transitive link) has no common apex — seed from
                // random ray triples and keep the trial with the most
                // inliers, then refine.
                if (!ok)
                {
                    int bestInl = 0;
                    var bestL = Vector3.Zero;
                    for (int trial = 0; trial < RansacTrials; trial++)
                    {
                        int i1 = rng.Next(work.Count);
                        int i2 = rng.Next(work.Count);
                        int i3 = rng.Next(work.Count);
                        if (i1 == i2 || i1 == i3 || i2 == i3) continue;
                        if (!ApexFromRays3(work[i1], work[i2], work[i3], out var L3)) continue;
                        int inl = 0;
                        foreach (var x in work)
                        {
                            float t = Math.Clamp(Vector3.Dot(L3 - x.Pos, x.Dir), -32768f, 32768f);
                            if (t >= 0 && Vector3.Distance(L3, x.Pos + x.Dir * t) <= MaxRayDist) inl++;
                        }
                        if (inl > bestInl) { bestInl = inl; bestL = L3; }
                    }
                    if (bestInl >= MinCells)
                    {
                        var inlSet = new List<Sample>();
                        foreach (var x in work)
                        {
                            float t = Math.Clamp(Vector3.Dot(bestL - x.Pos, x.Dir), -32768f, 32768f);
                            if (t >= 0 && Vector3.Distance(bestL, x.Pos + x.Dir * t) <= MaxRayDist) inlSet.Add(x);
                        }
                        var Li = Vector3.Zero;
                        foreach (var x in inlSet) Li += x.Pos;
                        Li /= inlSet.Count;
                        ok = TryApex(inlSet, Li, mapExtent, out s, out L, out rayMed, out tfrac);
                    }
                }

                if (!ok)
                {
                    res.ClusterInfo[id] = ((byte)color.X, (byte)color.Y, (byte)color.Z, false, "no-apex");
                    res.Rejected.Add(new RejectedCluster
                    {
                        CellCount = work.Count,
                        Reason = "no converging apex found (centroid or RANSAC) — blended/noise directions",
                        Position = L, RayDistMedian = rayMed, TPositiveFrac = tfrac,
                        Color = new Vector3(color.X, color.Y, color.Z)
                    });
                    return;
                }

                var (level, lowerBound, saturated, consistency) = EstimateLevel(s, L);
                // Direction-field agreement: a true point light's grid
                // directions are normalize(L - P) within byte quantization
                // (~1-2 deg). Virtual lens/noise apexes only converge
                // locally — their angles spread even when the rays come
                // close to the point.
                var angles = new List<double>();
                foreach (var x in s)
                {
                    float t = Vector3.Dot(L - x.Pos, x.Dir);
                    if (t < 0) continue;
                    var toL = Vector3.Normalize(L - x.Pos);
                    float cosA = Math.Clamp(Vector3.Dot(x.Dir, toL), -1f, 1f);
                    angles.Add(Math.Acos(cosA) * 180.0 / Math.PI);
                }
                angles.Sort();
                var light = new RecoveredLight
                {
                    Name = $"recov_{res.Lights.Count}",
                    Position = L,
                    Color = new Vector3(color.X, color.Y, color.Z),
                    Level = level,
                    LevelLowerBound = lowerBound,
                    Radius = Radius,
                    Style = DominantStyle(s),
                    CellCount = s.Count,
                    RayDistMedian = rayMed,
                    TPositiveFrac = tfrac,
                    LevelConsistency = consistency,
                    AngleMedianDeg = angles.Count > 0 ? angles[angles.Count / 2] : 90.0,
                    AngleP90Deg = angles.Count > 0 ? angles[Math.Min(angles.Count - 1, (int)(angles.Count * 0.9))] : 90.0,
                    Evidence = s.Select(x => (x.Pos, Math.Max(x.R, Math.Max(x.G, x.B)))).ToList()
                };
                res.Lights.Add(light);
                res.ClusterInfo[id] = ((byte)color.X, (byte)color.Y, (byte)color.Z, true, light.Name);
                peels++;

                var peeled = new HashSet<int>(s.Select(x => x.Id));
                work.RemoveAll(x => peeled.Contains(x.Id));
            }
            if (work.Count >= MinCells)
            {
                res.Rejected.Add(new RejectedCluster
                {
                    CellCount = work.Count,
                    Reason = $"residue after {peels} peels (max reached)",
                    Position = Vector3.Zero, RayDistMedian = 0, TPositiveFrac = 0,
                    Color = new Vector3(color.X, color.Y, color.Z)
                });
            }
        }

        /// <summary>
        /// Iteratively reweighted solve: blend-gradient samples from
        /// multi-light lenses link transitively into the working set (local
        /// directions change smoothly), but their rays miss the apex.
        /// Repeatedly keep only rays near the current apex and re-solve —
        /// the weighted mean of a converging ray set collapses onto the
        /// true emitter, and the median drops with each pass.
        /// </summary>
        private List<Sample> IrlsTrim(List<Sample> work, ref Vector3 L, float mapExtent, ref bool diverged)
        {
            var s = work;
            for (int pass = 0; pass < 3; pass++)
            {
                var pDists = new float[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    float t = Math.Clamp(Vector3.Dot(L - s[i].Pos, s[i].Dir), -32768f, 32768f);
                    pDists[i] = t >= 0 ? Vector3.Distance(L, s[i].Pos + s[i].Dir * t) : float.MaxValue;
                }
                var pSorted = (float[])pDists.Where(v => v < float.MaxValue).ToArray();
                Array.Sort(pSorted);
                if (pSorted.Length == 0) break;
                float pMedian = pSorted[pSorted.Length / 2];
                float thresh = Math.Max(2f * pMedian, 2f * MaxRayDist);
                var keep = new List<Sample>();
                for (int i = 0; i < s.Count; i++)
                    if (pDists[i] <= thresh) keep.Add(s[i]);
                if (keep.Count < MinCells || keep.Count == s.Count) break;
                s = keep;
                L = IterateApex(s, L, mapExtent, out diverged);
                if (diverged) break;
            }
            return s;
        }

        /// <summary>
        /// Full apex solve on a ray set (iterate + IRLS + acceptance
        /// metrics). Returns false when the set has no accepted apex.
        /// </summary>
        private bool TryApex(List<Sample> set, Vector3 startL, float mapExtent,
                             out List<Sample> s, out Vector3 L, out float rayMed, out float tfrac)
        {
            L = startL;
            bool diverged = false;
            L = IterateApex(set, L, mapExtent, out diverged);
            s = IrlsTrim(set, ref L, mapExtent, ref diverged);
            if (diverged) { rayMed = 0; tfrac = 0; return false; }

            var dists = new float[s.Count];
            int tpos = 0;
            for (int i = 0; i < s.Count; i++)
            {
                float t = Math.Clamp(Vector3.Dot(L - s[i].Pos, s[i].Dir), -32768f, 32768f);
                if (t >= 0) tpos++;
                dists[i] = Vector3.Distance(L, s[i].Pos + s[i].Dir * t);
            }
            var sorted = (float[])dists.Clone();
            Array.Sort(sorted);
            rayMed = sorted[sorted.Length / 2];
            tfrac = (float)tpos / s.Count;
            return s.Count >= MinCells && tfrac >= MinTPositiveFrac && rayMed <= MaxRayDist;
        }

        /// <summary>
        /// Least-squares common point of three rays: minimize
        /// sum |L - (P_i + t u_i)|^2 over L -> (sum A_i) L = sum A_i P_i,
        /// A_i = I - u_i u_i^T. False when the system is degenerate.
        /// </summary>
        private static bool ApexFromRays3(Sample a, Sample b, Sample c, out Vector3 L)
        {
            double m00 = 0, m01 = 0, m02 = 0, m10 = 0, m11 = 0, m12 = 0, m20 = 0, m21 = 0, m22 = 0;
            double r0 = 0, r1 = 0, r2 = 0;
            foreach (var x in new[] { a, b, c })
            {
                float ux = x.Dir.X, uy = x.Dir.Y, uz = x.Dir.Z;
                double A00 = 1 - ux * ux, A01 = -ux * uy,   A02 = -ux * uz;
                double A10 = -ux * uy,   A11 = 1 - uy * uy, A12 = -uy * uz;
                double A20 = -ux * uz,   A21 = -uy * uz,   A22 = 1 - uz * uz;
                m00 += A00; m01 += A01; m02 += A02;
                m10 += A10; m11 += A11; m12 += A12;
                m20 += A20; m21 += A21; m22 += A22;
                r0 += A00 * x.Pos.X + A01 * x.Pos.Y + A02 * x.Pos.Z;
                r1 += A10 * x.Pos.X + A11 * x.Pos.Y + A12 * x.Pos.Z;
                r2 += A20 * x.Pos.X + A21 * x.Pos.Y + A22 * x.Pos.Z;
            }
            var M = new double[,] { { m00, m01, m02, r0 }, { m10, m11, m12, r1 }, { m20, m21, m22, r2 } };
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int rr = col + 1; rr < 3; rr++)
                    if (Math.Abs(M[rr, col]) > Math.Abs(M[piv, col])) piv = rr;
                if (Math.Abs(M[piv, col]) < 1e-9) { L = default; return false; }
                if (piv != col)
                {
                    for (int cc = 0; cc < 4; cc++)
                    {
                        double tmp = M[col, cc];
                        M[col, cc] = M[piv, cc];
                        M[piv, cc] = tmp;
                    }
                }
                for (int rr = 0; rr < 3; rr++)
                {
                    if (rr == col) continue;
                    double f = M[rr, col] / M[col, col];
                    for (int cc = col; cc < 4; cc++) M[rr, cc] -= f * M[col, cc];
                }
            }
            L = new Vector3((float)(M[0, 3] / M[0, 0]), (float)(M[1, 3] / M[1, 1]), (float)(M[2, 3] / M[2, 2]));
            return true;
        }

        private (int? Level, int LowerBound, bool Saturated, double Consistency) EstimateLevel(List<Sample> s, Vector3 L)
        {
            var vals = new List<double>();
            double lb = 0; int satCount = 0;
            foreach (var x in s)
            {
                float d = Vector3.Distance(L, x.Pos);
                double dr = d / (double)Radius;
                double atten = Math.Max(0.0, 1.0 - dr * dr);
                if (atten < 0.05) continue;
                float m = Math.Max(x.R, Math.Max(x.G, x.B));
                if (m >= 255f) { satCount++; lb = Math.Max(lb, 255.0 / atten); continue; }
                // directed max channel = (level/255) * atten * colorMax(=1)
                // (direction factor d in [0.25,1] is absorbed in the tolerance)
                vals.Add(255.0 * m / atten);
            }
            double consistency = 1;
            int lowerBound = (int)Math.Round(lb);
            if (vals.Count >= 3)
            {
                vals.Sort();
                double v = vals[vals.Count / 2];
                if (vals.Count >= 12)
                {
                    double iqr = vals[(int)(vals.Count * 0.75)] - vals[(int)(vals.Count * 0.25)];
                    consistency = v > 0 ? iqr / v : 1;
                }
                v = Math.Clamp(v, LevelMin, LevelMax);
                return ((int)Math.Round(v), Math.Max(lowerBound, (int)Math.Round(v)), satCount == vals.Count, consistency);
            }
            return (null, lowerBound, satCount > 0 || vals.Count == 0, 1);
        }

        private Vector3 IterateApex(List<Sample> s, Vector3 L, float mapExtent, out bool diverged)
        {
            float n = s.Count;
            diverged = false;
            float prevStep = -1; int growStreak = 0;
            for (int it = 0; it < MaxIterations; it++)
            {
                var sum = Vector3.Zero;
                foreach (var x in s)
                {
                    float t = Math.Clamp(Vector3.Dot(L - x.Pos, x.Dir), -32768f, 32768f);
                    sum += x.Pos + x.Dir * t;
                }
                var L2 = sum / n;
                float move = Vector3.Distance(L, L2);
                L = L2;
                if (move < 0.5f) break;
                // divergence guard: step keeps growing (parallel rays -> no apex)
                if (prevStep > 0 && move > 2f * prevStep)
                    if (++growStreak >= 3) { diverged = true; break; }
                else growStreak = 0;
                prevStep = move;
                if (Vector3.Distance(L, Vector3.Zero) > 8f * mapExtent) { diverged = true; break; }
            }
            return L;
        }

        private static int DominantStyle(List<Sample> s)
        {
            var counts = new Dictionary<int, int>();
            foreach (var x in s) counts[x.Style] = counts.TryGetValue(x.Style, out int c) ? c + 1 : 1;
            return counts.OrderByDescending(kv => kv.Value).First().Key;
        }

        /// <summary>
        /// Inverse of q3map2 NormalToLatLong / engine sine-table decode:
        /// a = 2*pi*(latByte*4)/1024, b = 2*pi*(lngByte*4)/1024,
        /// dir = (cos a * sin b, sin a * sin b, cos b).
        /// </summary>
        public static Vector3 DecodeLatLong(byte[] latLong)
        {
            int lat = latLong[1] * 4; // 0..1020
            int lng = latLong[0] * 4;
            double a = lat * 2.0 * Math.PI / 1024.0;
            double b = lng * 2.0 * Math.PI / 1024.0;
            return new Vector3(
                (float)(Math.Cos(a) * Math.Sin(b)),
                (float)(Math.Sin(a) * Math.Sin(b)),
                (float)Math.Cos(b));
        }

        /// <summary>
        /// q3map2 NormalToLatLong (nrc libs/mathlib/mathlib.c:438):
        /// bytes[0] = lng = acos(n.z) in degrees * 255/360 (0 = up, 128 = down),
        /// bytes[1] = lat = atan2(n.y, n.x) in degrees * 255/360 (0 = +x).
        /// </summary>
        public static byte[] NormalToLatLong(Vector3 n)
        {
            if (n.X == 0 && n.Y == 0)
                return n.Z > 0 ? new byte[] { 0, 0 } : new byte[] { 128, 0 };
            int a = (int)(Math.Atan2(n.Y, n.X) * 180.0 / Math.PI * (255.0 / 360.0)) & 0xff;
            int b = (int)(Math.Acos(Math.Clamp(n.Z, -1f, 1f)) * 180.0 / Math.PI * (255.0 / 360.0)) & 0xff;
            return new byte[] { (byte)b, (byte)a };
        }

        /// <summary>Directed colors agree within ColorAngleDeg (same light hue).</summary>
        private static bool SameColor(Sample a, Sample b, float cosThreshold)
        {
            double am = Math.Sqrt((double)a.R * a.R + a.G * a.G + a.B * a.B);
            double bm = Math.Sqrt((double)b.R * b.R + b.G * b.G + b.B * b.B);
            if (am < 1e-9 || bm < 1e-9) return false;
            return ((double)a.R * b.R + (double)a.G * b.G + (double)a.B * b.B) / (am * bm) > cosThreshold;
        }

        /// <summary>
        /// Do the two rays (corner -> direction) intersect forward at a common
        /// apex? Solves the min-distance-between-lines problem analytically
        /// (unit directions): [1,-b;-b,1][ti;tj] = [-u.w; v.w], b = u.v.
        /// Rays of the same light meet at the emitter; rays of different
        /// lights (or blended-direction samples) stay skew.
        /// </summary>
        private bool RaysIntersect(Sample a, Sample b, out float ti, out float tj)
        {
            ti = tj = 0;
            float bd = Vector3.Dot(a.Dir, b.Dir);
            if (bd > 0.99996f) return false; // too parallel to pin the apex
            var w = a.Pos - b.Pos;
            float uw = Vector3.Dot(a.Dir, w);
            float vw = Vector3.Dot(b.Dir, w);
            float det = 1f - bd * bd;
            ti = (-uw + bd * vw) / det;
            tj = (vw - bd * uw) / det;
            if (ti < 48f || tj < 48f) return false; // behind the corners / inside the inhibit sphere
            var ci = a.Pos + a.Dir * ti;
            var cj = b.Pos + b.Dir * tj;
            return Vector3.Distance(ci, cj) < RayPairTol;
        }

        private static Vector3 NormalizeColor(float r, float g, float b)
        {
            float m = Math.Max(r, Math.Max(g, b));
            if (m <= 1e-9f) return new Vector3(255, 255, 255);
            return new Vector3(
                (byte)Math.Clamp((int)Math.Round(r / m * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(g / m * 255), 0, 255),
                (byte)Math.Clamp((int)Math.Round(b / m * 255), 0, 255));
        }

        /// <summary>
        /// Writes the candidates as a Q3/JA .map entity fragment: a
        /// target_light + light pair per candidate (the format Sheng's maps
        /// and the JA lightmapper consume; stage 2 merges this into the M2
        /// .map).
        /// </summary>
        public static void WriteMapFragment(LightRecoveryResult r, string sourceName, string path)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("// JKDecompiler M2.5 stage 1 — recovered light entities (grid seeding)");
            w.WriteLine($"// source: {sourceName}");
            w.WriteLine($"// accepted {r.Lights.Count} candidate(s); {r.Rejected.Count} rejected clusters; " +
                $"grid {r.GridNote ?? "valid"}");
            if (!r.GridValid)
            {
                w.WriteLine("// NOTE: lightgrid invalid — no candidates");
                return;
            }
            foreach (var l in r.Lights)
            {
                string origin = $"{l.Position.X:0.00} {l.Position.Y:0.00} {l.Position.Z:0.00}";
                int level = l.Level ?? LevelDefaultForSaturated(l);
                w.WriteLine("{");
                w.WriteLine($"  \"classname\" \"target_light\"");
                w.WriteLine($"  \"name\" \"{l.Name}\"");
                w.WriteLine($"  \"origin\" \"{origin}\"");
                w.WriteLine("}");
                w.WriteLine("{");
                w.WriteLine("  \"classname\" \"light\"");
                w.WriteLine($"  \"target\" \"{l.Name}\"");
                w.WriteLine($"  \"light\" \"{level} {(int)l.Color.X} {(int)l.Color.Y} {(int)l.Color.Z}\"");
                w.WriteLine($"  \"lightstyle\" \"{l.Style}\"");
                w.WriteLine($"  \"radius\" \"{l.Radius}\"");
                w.WriteLine("  \"_lightmapperscale\" \"1\"");
                w.WriteLine("}");
            }
        }

        private static int LevelDefaultForSaturated(RecoveredLight l)
            => Math.Clamp(l.LevelLowerBound > 0 ? l.LevelLowerBound * 2 : 1000, 255, 12000);

        /// <summary>
        /// Top-down (XY) overlay: each grid column's strongest corner painted
        /// in its cluster's color (brightness = directed magnitude), accepted
        /// lights as crosshairs, rejected candidates as small x marks.
        /// </summary>
        public static void WriteOverlay(LightRecoveryResult r, BspData data, string path)
        {
            var m0 = data.Models[0];
            float minX = m0.Mins.X, minY = m0.Mins.Y;
            int w = (int)Math.Ceiling(m0.Maxs.X - m0.Mins.X);
            int h = (int)Math.Ceiling(m0.Maxs.Y - m0.Mins.Y);
            const int maxDim = 4096;
            float scale = Math.Min(1f, (float)maxDim / Math.Max(w, h));
            int W2 = Math.Max(1, (int)(w * scale));
            int H2 = Math.Max(1, (int)(h * scale));
            var img = new byte[W2 * H2 * 3];

            // per (ix,iy) column: strongest corner across z
            if (r.GridValid && r.CornerCluster.Length > 0)
            {
                int W = r.GridDims[0], H = r.GridDims[1], V = r.GridDims[2];
                float[] bestMag = new float[W * H];
                int[] bestZ = new int[W * H];
                Array.Fill(bestZ, -1);
                for (int iz = 0; iz < V; iz++)
                for (int iy = 0; iy < H; iy++)
                for (int ix = 0; ix < W; ix++)
                {
                    int corner = ix + iy * W + iz * W * H;
                    float mag = r.CornerMag[corner];
                    int col = ix + iy * W;
                    if (mag > bestMag[col]) { bestMag[col] = mag; bestZ[col] = iz; }
                }
                for (int iy = 0; iy < H; iy++)
                for (int ix = 0; ix < W; ix++)
                {
                    int col = ix + iy * W;
                    if (bestZ[col] < 0 || bestMag[col] <= 0) continue;
                    int id = r.CornerCluster[ix + iy * W + bestZ[col] * W * H];
                    if (id < 0 || id >= r.ClusterInfo.Count) continue;
                    var (cr, cg, cb, _, _) = r.ClusterInfo[id];
                    float f = (float)(bestMag[col] / 255.0);
                    byte rr = (byte)(cr * f), gg = (byte)(cg * f), bb = (byte)(cb * f);
                    // grid corners start at GridOrigin (not the model mins): offset the column
                    float px0 = (ix * 64f + (r.GridOrigin.X - minX)) * scale;
                    float py0 = (iy * 64f + (r.GridOrigin.Y - minY)) * scale;
                    float px1 = ((ix + 1) * 64f + (r.GridOrigin.X - minX)) * scale;
                    float py1 = ((iy + 1) * 64f + (r.GridOrigin.Y - minY)) * scale;
                    FillRect(img, W2, H2, px0, py0, px1, py1, rr, gg, bb);
                }
            }

            // accepted: crosshair; rejected: x mark
            foreach (var l in r.Lights)
                DrawCrosshair(img, W2, H2, (l.Position.X - minX) * scale, (l.Position.Y - minY) * scale,
                    (byte)l.Color.X, (byte)l.Color.Y, (byte)l.Color.Z);
            foreach (var rc in r.Rejected)
            {
                float x = (rc.Position.X - minX) * scale, y = (rc.Position.Y - minY) * scale;
                if (x < 0 || x >= W2 || y < 0 || y >= H2) continue;
                DrawX(img, W2, H2, x, y, (byte)90);
            }

            PngWriter.SaveRgb(img, W2, H2, path);
        }

        private static void FillRect(byte[] img, int w, int h, float x0, float y0, float x1, float y1,
            byte r, byte g, byte b)
        {
            int ix0 = Math.Max(0, (int)x0), iy0 = Math.Max(0, (int)y0);
            int ix1 = Math.Min(w - 1, (int)x1), iy1 = Math.Min(h - 1, (int)y1);
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    int p = (y * w + x) * 3;
                    img[p] = r; img[p + 1] = g; img[p + 2] = b;
                }
        }

        private static void DrawCrosshair(byte[] img, int w, int h, float cx, float cy, byte r, byte g, byte b)
        {
            int len = 12;
            PutLine(img, w, h, cx - len, cy, cx + len, cy, r, g, b);
            PutLine(img, w, h, cx, cy - len, cx, cy + len, r, g, b);
            PutLine(img, w, h, cx - len, cy, cx + len, cy, 255, 255, 255);
            PutLine(img, w, h, cx, cy - len, cx, cy + len, 255, 255, 255);
        }

        private static void DrawX(byte[] img, int w, int h, float cx, float cy, byte v)
        {
            int len = 5;
            PutLine(img, w, h, cx - len, cy - len, cx + len, cy + len, v, v, v);
            PutLine(img, w, h, cx - len, cy + len, cx + len, cy - len, v, v, v);
        }

        private static void PutLine(byte[] img, int w, int h, float x0, float y0, float x1, float y1,
            byte r, byte g, byte b)
        {
            int steps = (int)Math.Max(1, Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                int x = (int)Math.Round(x0 + (x1 - x0) * t);
                int y = (int)Math.Round(y0 + (y1 - y0) * t);
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                int p = (y * w + x) * 3;
                img[p] = r; img[p + 1] = g; img[p + 2] = b;
            }
        }
    }
}
