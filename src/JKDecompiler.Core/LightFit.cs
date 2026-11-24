using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace JKDecompiler.Core
{
    /// <summary>One light entity in the stage-2 fit (output: target_light + light pair).</summary>
    public class FitLight
    {
        public string Name = "";
        public string SeedName = "";
        public Vector3 SeedPosition = Vector3.Zero;
        public Vector3 Position = Vector3.Zero;
        /// <summary>Fitted absolute per-channel radiance scale (0..255 units; 255 = level-255 white at atten·d=1).</summary>
        public double[] Scale = { 0, 0, 0 };
        public int Radius = 1024;
        public int Style;
        public bool Kept = true;   // lights start alive; pruning removes them
        public string DropReason = "";
        public bool PositionMoved;
        public double ContributionPct;
        public double DeltaE;

        /// <summary>max-channel scale → intensity for the Q3-style "light" key.</summary>
        public double Intensity => Math.Max(Scale[0], Math.Max(Scale[1], Scale[2]));
        /// <summary>Normalized 0..1 color (ratios), as the _color key expects.</summary>
        public (float R, float G, float B) Color01
        {
            get
            {
                double m = Intensity;
                if (m <= 1e-9) return (0, 0, 0);
                return ((float)(Scale[0] / m), (float)(Scale[1] / m), (float)(Scale[2] / m));
            }
        }
        /// <summary>JKA byte factorization: level = max channel, color bytes keep the ratios.</summary>
        public (int Level, int R, int G, int B) JkaBytes
        {
            get
            {
                int level = (int)Math.Clamp(Math.Round(Intensity), 0, 255);
                if (level <= 0) return (0, 0, 0, 0);
                return (level,
                    (int)Math.Clamp(Math.Round(255.0 * Scale[0] / level), 0, 255),
                    (int)Math.Clamp(Math.Round(255.0 * Scale[1] / level), 0, 255),
                    (int)Math.Clamp(Math.Round(255.0 * Scale[2] / level), 0, 255));
            }
        }
    }

    public class LightFitOptions
    {
        public int MaxSeeds = 0;          // 0 = all accepted stage-1 seeds
        public float MergeDistance = 32f; // merge stage-1 seeds closer than this (0 = off)
        public string SeedsPath = "";     // external seeds JSON {seeds:[{name,position,color,level,radius,style}]} — skips stage 1
        public bool PosFit = true;        // analytic position refinement after the linear fit
        public bool RadiusFit = true;     // per-light radius hill-climb (JKA quadratic falloff)
        public float PosStep = 8f;        // first hill-climb step (world units); second round = step/2
        public int Parallel = 2;          // concurrent compiles
        public double MinContributionPct = 0.5;  // drop lights below this share of fitted energy (%)
        public double MinDeltaEPct = 0.25;       // drop lights whose removal lowers error by less than this %
        public double GateMarginL1 = 2.0;        // stage-A must beat ambient-only by at least this L1 — a
                                                 // zero-light compile differs from the probe compile by
                                                 // radiosity jitter alone (measured 0.87 on academy1:
                                                 // 103.29 probe vs 102.42 zero-light), so any margin
                                                 // below ~1.0 lets an empty/no-op set "pass"
        public int LightmapSize = 128;
        public bool WriteJkaFormat = false; // emit 4-value JKA "light" L R G B keys instead of Q3 light+_color
    }

    public class LightFitResult
    {
        public int MatchedPoints;
        public int UnmatchedPoints;      // original sample points with no candidate face (counted as black)
        public double ErrorAmbientOnly;  // L1 area-weighted error of the no-light compile
        public double ErrorStageA;       // after the joint linear fit, seed positions
        public double ErrorFinal;        // after position refinement (+ final compile)
        public double SkyGain;           // fitted gain on the probe sky field (1 = no correction)
        public double SkyOffset;         // fitted per-channel constant sky/ambient offset
        public bool PositionsRolledBack;
        public List<FitLight> Lights = new();
        public int CompilesRun;
        public long ElapsedMs;
        public bool DiffWritten;            // lightmap_diff.png actually emitted (a comparable candidate BSP existed)
        public string Note = "";
    }

    /// <summary>
    /// M2.5 stage 2 — closed-loop light fit against the baked lightmap blocks.
    ///
    /// Pipeline:
    ///   1. stage-1 seeds (grid recovery) → light entities appended to the M2 .map
    ///   2. per-light single compiles (q3map2 -bsp then -light) capture each light's
    ///      exact forward field S_l — shadowing, attenuation, color included. No
    ///      forward model is assumed; linearity in intensity is all that's needed.
    ///   3. joint linear least squares over [ambient offset c0, per-light channel
    ///      scales s_l] reproduces the ORIGINAL engine lightmap sampled at shared
    ///      world points. The JKA lightmap block is a world-space canvas, so faces
    ///      are never paired 1:1 (q3map2 re-tessellates/splits differently): every
    ///      sample point is mapped through each side's own world→UV affine.
    ///   4. analytic position hill-climb (atten/d proxy, no compiles), verified by
    ///      one final compile; rolled back if the measured error worsens.
    ///   5. pruning: drop lights whose marginal ΔE or energy share is below floor —
    ///      the concrete form of "must beat ambient-only".
    /// </summary>
    public class LightFit
    {
        public LightFitOptions Options { get; }
        public string Q3map2Bin { get; }
        public string GameDir { get; }          // directory containing base/ (paks)
        public LightFitResult Result { get; private set; } = new();

        public LightFit(string q3map2Bin, string gameDir, LightFitOptions? options = null)
        {
            Q3map2Bin = q3map2Bin;
            GameDir = gameDir;
            Options = options ?? new LightFitOptions();
        }

        // ------------------------------------------------------------------
        // World-space sampling model
        // ------------------------------------------------------------------

        internal class SampleFace
        {
            public int FaceIndex;
            public int Block;
            public double Area = 1;
            public (double X, double Y, double Z)[] Verts = Array.Empty<(double, double, double)>();
            public (double U, double V)[] VertexUvs = Array.Empty<(double, double)>();
            // world→UV affine per channel: uv_c = a·x + b·y + c·z + d
            public double[] AffineU = { 0, 0, 0, 1e9 };  // 1e9 sentinel = unusable
            public double[] AffineV = { 0, 0, 0, 1e9 };
            // 2D local frame for interior sampling + hull containment
            public (double X, double Y, double Z) FrameOrigin = default;
            public (double X, double Y, double Z) FrameU = (1, 0, 0);
            public (double X, double Y, double Z) FrameV = (0, 1, 0);
            public (double X, double Y)[] Hull2D = Array.Empty<(double, double)>();
            public (double X, double Y, double Z) Center = default;
            public (double X, double Y, double Z) Normal = (0, 0, 1);
            public double PlaneDist;
            // UV bounding box of the face's vertex UVs (block fractions).
            // Sampling must stay inside it: the 128x128 block is shared by
            // many faces, and bilinear bleeding across the box reads texels
            // of NEIGHBORING faces (their values are unrelated).
            public double UMin = 0, UMax = 0, VMin = 0, VMax = 0;
        }

        internal class SamplePoint
        {
            public double X, Y, Z;
        }

        /// <summary>
        /// Point-in-solid test against the world model's brush hulls: inside if
        /// within the convex hull of ANY world brush (all brush-side planes
        /// satisfied). Used to push light seeds out of slabs before compiling —
        /// a light inside a solid is culled or nudged by q3map2, which
        /// displaces its baked pool (measured: nudged pools correlate ~0.04
        /// with the target vs ~0.2+ for free-space lights).
        /// </summary>
        public static bool PointInWorldSolid(BspData d, Vector3 p)
        {
            if (d.Models.Count == 0) return false;
            var w = d.Models[0];
            for (int b = w.FirstBrush; b < w.FirstBrush + w.NumBrushes; b++)
            {
                var br = d.Brushes[b];
                bool inAll = true;
                for (int i = 0; i < br.NumSides; i++)
                {
                    var pl = d.Planes[d.BrushSides[br.FirstSide + i].PlaneIndex];
                    double dist = pl.Normal.X * p.X + pl.Normal.Y * p.Y + pl.Normal.Z * p.Z - pl.Distance;
                    if (dist > 0.01f) { inAll = false; break; }
                }
                if (inAll) return true;
            }
            return false;
        }

        /// <summary>
        /// Build lit-face sample data from a BSP: per-face world verts, UVs,
        /// world→UV affine (planar faces), 2D hull + local frame, interior grid.
        /// </summary>
        static List<SampleFace> BuildSampleFaces(BspData d)
        {
            var list = new List<SampleFace>();
            for (int i = 0; i < d.Faces.Count; i++)
            {
                var f = d.Faces[i];
                int block = f.LightmapNum[0];
                if (block < 0 || block >= d.Lightmaps.Count) continue;

                var sf = new SampleFace { FaceIndex = i, Block = block };
                int n = Math.Min(f.NumVertices, d.Vertices.Count - f.FirstVertexIndex);
                if (n < 3) continue;
                sf.Verts = new (double, double, double)[n];
                sf.VertexUvs = new (double, double)[n];
                for (int v = 0; v < n; v++)
                {
                    var vert = d.Vertices[f.FirstVertexIndex + v];
                    sf.Verts[v] = (vert.Position.X, vert.Position.Y, vert.Position.Z);
                    sf.VertexUvs[v] = (vert.GetLightmapU(0), vert.GetLightmapV(0));
                }
                {
                    double umn = double.MaxValue, umx = double.MinValue, vmn = double.MaxValue, vmx = double.MinValue;
                    for (int v = 0; v < n; v++)
                    {
                        umn = Math.Min(umn, sf.VertexUvs[v].U); umx = Math.Max(umx, sf.VertexUvs[v].U);
                        vmn = Math.Min(vmn, sf.VertexUvs[v].V); vmx = Math.Max(vmx, sf.VertexUvs[v].V);
                    }
                    sf.UMin = umn; sf.UMax = umx; sf.VMin = vmn; sf.VMax = vmx;
                }
                double cx = 0, cy = 0, cz = 0;
                for (int v = 0; v < n; v++) { cx += sf.Verts[v].X; cy += sf.Verts[v].Y; cz += sf.Verts[v].Z; }
                sf.Center = (cx / n, cy / n, cz / n);
                // face normal from cross product of first three verts
                var (ax, ay, az) = sf.Verts[1];
                var (bx, by, bz) = sf.Verts[2];
                var (ox, oy, oz) = sf.Verts[0];
                double nx = (ay - oy) * (bz - oz) - (az - oy) * (by - oy);
                double ny = (az - oz) * (bx - ox) - (ax - ox) * (bz - oz);
                double nz = (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
                double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-9) continue;
                sf.Normal = (nx / nl, ny / nl, nz / nl);
                sf.PlaneDist = sf.Normal.X * ox + sf.Normal.Y * oy + sf.Normal.Z * oz;

                // area (fan triangulation)
                double area = 0;
                for (int v = 1; v < n - 1; v++)
                {
                    var (u1x, u1y, u1z) = (sf.Verts[v].X - ox, sf.Verts[v].Y - oy, sf.Verts[v].Z - oz);
                    var (u2x, u2y, u2z) = (sf.Verts[v + 1].X - ox, sf.Verts[v + 1].Y - oy, sf.Verts[v + 1].Z - oz);
                    area += 0.5 * Math.Sqrt(
                        (u1y * u2z - u1z * u2y) * (u1y * u2z - u1z * u2y) +
                        (u1z * u2x - u1x * u2z) * (u1z * u2x - u1x * u2z) +
                        (u1x * u2y - u1y * u2x) * (u1x * u2y - u1y * u2x));
                }
                sf.Area = Math.Max(area, 1);

                // 2D local frame
                var (ndx, ndy, ndz) = sf.Normal;
                var up = Math.Abs(ndy) < 0.9 ? (X: 0.0, Y: 1.0, Z: 0.0) : (X: 1.0, Y: 0.0, Z: 0.0);
                // u = normalize(n × up)
                double ux = ndy * up.Z - ndz * up.Y, uy = ndz * up.X - ndx * up.Z, uz = ndx * up.Y - ndy * up.X;
                double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                ux /= ul; uy /= ul; uz /= ul;
                double vx = ndy * uz - ndz * uy, vy = ndz * ux - ndx * uz, vz = ndx * uy - ndy * ux;
                sf.FrameOrigin = sf.Verts[0];
                sf.FrameU = (ux, uy, uz);
                sf.FrameV = (vx, vy, vz);
                var pts2d = new (double X, double Y)[n];
                for (int v = 0; v < n; v++)
                {
                    var (px, py, pz) = sf.Verts[v];
                    double dx = px - sf.FrameOrigin.X, dy = py - sf.FrameOrigin.Y, dz = pz - sf.FrameOrigin.Z;
                    pts2d[v] = (dx * ux + dy * uy + dz * uz, dx * vx + dy * vy + dz * vz);
                }
                sf.Hull2D = ConvexHull2D(pts2d);

                // world→UV affine (3 params in the 2D frame) by least squares over verts
                TryFitAffine(sf.Verts, sf.VertexUvs, sf.FrameOrigin, sf.FrameU, sf.FrameV, out double[] au, out double[] av);
                sf.AffineU = au;
                sf.AffineV = av;
                if (Environment.GetEnvironmentVariable("JKD_UVDUMP") == "1" && list.Count < 3)
                {
                    var sbd = new System.Text.StringBuilder();
                    sbd.AppendLine($"face {i} block={block} n={n}");
                    for (int v2 = 0; v2 < n; v2++)
                        sbd.AppendLine($"  vert {v2}: pos=({sf.Verts[v2].X:F1},{sf.Verts[v2].Y:F1},{sf.Verts[v2].Z:F1}) uv=({sf.VertexUvs[v2].U:F3},{sf.VertexUvs[v2].V:F3})");
                    sbd.AppendLine($"  normal={sf.Normal} frameU={sf.FrameU} frameV={sf.FrameV} origin={sf.FrameOrigin}");
                    sbd.AppendLine($"  affineU=[{string.Join(",", au.Select(z => z.ToString("F6")))}]");
                    sbd.AppendLine($"  affineV=[{string.Join(",", av.Select(z => z.ToString("F6")))}]");
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "uvdump.txt"), sbd.ToString() + "\n");
                }
                list.Add(sf);
            }
            return list;
        }

        static bool TryFitAffine((double X, double Y, double Z)[] pts, (double U, double V)[] uvs,
            (double X, double Y, double Z) origin,
            (double X, double Y, double Z) fu, (double X, double Y, double Z) fv,
            out double[] au, out double[] av)
        {
            // Fit uv = a·px + b·py + c in the face's 2D local frame. Fitting in
            // world 3D (4 params) is rank-deficient for planar faces — the
            // design matrix [x y z 1] has rank 3 on a plane, so the normal
            // equations are singular and every quad face failed the fit and
            // fell back to nearest-vertex UVs (whole face → one corner pixel).
            au = av = Array.Empty<double>();
            int n = pts.Length;
            if (n < 3) return false;
            double[] g = new double[9];
            double[] hu = new double[3], hv = new double[3];
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i].X - origin.X, dy = pts[i].Y - origin.Y, dz = pts[i].Z - origin.Z;
                double x = dx * fu.X + dy * fu.Y + dz * fu.Z;
                double y = dx * fv.X + dy * fv.Y + dz * fv.Z;
                g[0] += x * x; g[1] += x * y; g[2] += x;
                g[3] += x * y; g[4] += y * y; g[5] += y;
                g[6] += x; g[7] += y; g[8] += 1;
                hu[0] += x * uvs[i].U; hu[1] += y * uvs[i].U; hu[2] += uvs[i].U;
                hv[0] += x * uvs[i].V; hv[1] += y * uvs[i].V; hv[2] += uvs[i].V;
            }
            if (!Solve3((double[])g.Clone(), (double[])hu.Clone(), out var tu)) return false;
            if (!Solve3((double[])g.Clone(), (double[])hv.Clone(), out var tv)) return false;
            au = new[] { tu[0], tu[1], 0.0, tu[2] };
            av = new[] { tv[0], tv[1], 0.0, tv[2] };
            return true;
        }

        /// <summary>3x3 Gaussian elimination with partial pivoting (row-major, mutates inputs).</summary>
        internal static bool Solve3(double[] m, double[] rhs, out double[] x)
        {
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 3; r++)
                    if (Math.Abs(m[r * 3 + col]) > Math.Abs(m[piv * 3 + col])) piv = r;
                if (Math.Abs(m[piv * 3 + col]) < 1e-12) { x = Array.Empty<double>(); return false; }
                if (piv != col)
                {
                    for (int c = 0; c < 3; c++) { var t = m[col * 3 + c]; m[col * 3 + c] = m[piv * 3 + c]; m[piv * 3 + c] = t; }
                    var tr = rhs[col]; rhs[col] = rhs[piv]; rhs[piv] = tr;
                }
                for (int r = col + 1; r < 3; r++)
                {
                    double f = m[r * 3 + col] / m[col * 3 + col];
                    if (f == 0) continue;
                    for (int c = col; c < 3; c++) m[r * 3 + c] -= f * m[col * 3 + c];
                    rhs[r] -= f * rhs[col];
                }
            }
            x = new double[3];
            for (int r = 2; r >= 0; r--)
            {
                double s = rhs[r];
                for (int c = r + 1; c < 3; c++) s -= m[r * 3 + c] * x[c];
                x[r] = s / m[r * 3 + r];
            }
            return true;
        }

        /// <summary>4x4 Gaussian elimination with partial pivoting (element-wise row swap).</summary>
        internal static bool Solve4(double[] r0, double[] r1, double[] r2, double[] r3, double[] rhs, out double[] x)
        {
            var rows = new[] { r0, r1, r2, r3 };
            for (int col = 0; col < 4; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 4; r++)
                    if (Math.Abs(rows[r][col]) > Math.Abs(rows[piv][col])) piv = r;
                if (Math.Abs(rows[piv][col]) < 1e-12) { x = Array.Empty<double>(); return false; }
                if (piv != col)
                {
                    var tmp = rows[piv]; rows[piv] = rows[col]; rows[col] = tmp;
                    double tr = rhs[piv]; rhs[piv] = rhs[col]; rhs[col] = tr;
                }
                for (int r = col + 1; r < 4; r++)
                {
                    double f = rows[r][col] / rows[col][col];
                    if (f == 0) continue;
                    for (int c = col; c < 4; c++) rows[r][c] -= f * rows[col][c];
                    rhs[r] -= f * rhs[col];
                }
            }
            x = new double[4];
            for (int r = 3; r >= 0; r--)
            {
                double s = rhs[r];
                for (int c = r + 1; c < 4; c++) s -= rows[r][c] * x[c];
                x[r] = s / rows[r][r];
            }
            return true;
        }

        internal static (double X, double Y)[] ConvexHull2D((double X, double Y)[] pts)
        {
            var u = pts.OrderBy(p => (p.X, p.Y)).Distinct().ToArray();
            if (u.Length <= 2) return u;
            var lower = new List<(double X, double Y)>();
            foreach (var p in u)
            {
                while (lower.Count >= 2 && Cross2D(lower[^2], lower[^1], p) <= 1e-12) lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }
            var upper = new List<(double X, double Y)>();
            for (int i = u.Length - 1; i >= 0; i--)
            {
                var p = u[i];
                while (upper.Count >= 2 && Cross2D(upper[^2], upper[^1], p) <= 1e-12) upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower.ToArray();
        }

        static double Cross2D((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        internal static bool PointInHull((double X, double Y)[] hull, double x, double y, double eps = 0.5)
        {
            int n = hull.Length;
            if (n == 0) return false;
            for (int i = 0; i < n; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % n];
                if ((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X) < -eps) return false;
            }
            return true;
        }

        /// <summary>Sample points for one face: verts + clipped interior grid (planar faces).</summary>
        internal static List<SamplePoint> FaceSamplePoints(SampleFace sf)
        {
            var pts = new List<SamplePoint>();
            if (sf.Hull2D.Length < 3)
            {
                foreach (var v in sf.Verts) pts.Add(new SamplePoint { X = v.X, Y = v.Y, Z = v.Z });
                return pts;
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in sf.Hull2D)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            // grid density by face size (world units), capped
            double span = Math.Max(maxX - minX, maxY - minY);
            int res = (int)Math.Clamp(span / 16.0, 3, 12);
            var seen = new HashSet<(int, int)>();
            int added = 0;
            for (int gy = 0; gy <= res && added < 48; gy++)
            {
                for (int gx = 0; gx <= res && added < 48; gx++)
                {
                    double px = minX + (maxX - minX) * gx / res;
                    double py = minY + (maxY - minY) * gy / res;
                    if (!PointInHull(sf.Hull2D, px, py, 0.25)) continue;
                    var key = ((int)(px * 4), (int)(py * 4));
                    if (!seen.Add(key)) continue;
                    var o = sf.FrameOrigin;
                    var u = sf.FrameU; var v = sf.FrameV;
                    pts.Add(new SamplePoint
                    {
                        X = o.X + u.X * px + v.X * py,
                        Y = o.Y + u.Y * px + v.Y * py,
                        Z = o.Z + u.Z * px + v.Z * py,
                    });
                    added++;
                }
            }
            foreach (var vt in sf.Verts)
            {
                var o = sf.FrameOrigin;
                var u = sf.FrameU; var v = sf.FrameV;
                double dx = vt.X - o.X, dy = vt.Y - o.Y, dz = vt.Z - o.Z;
                var p2 = (X: dx * u.X + dy * u.Y + dz * u.Z, Y: dx * v.X + dy * v.Y + dz * v.Z);
                if (PointInHull(sf.Hull2D, p2.X, p2.Y, 0.25))
                    pts.Add(new SamplePoint { X = vt.X, Y = vt.Y, Z = vt.Z });
            }
            if (pts.Count == 0)
                foreach (var vt in sf.Verts) pts.Add(new SamplePoint { X = vt.X, Y = vt.Y, Z = vt.Z });
            return pts;
        }

        // UV of a world point on a sample face (affine, or nearest-vertex fallback)
        internal static (double U, double V) WorldToUv(SampleFace sf, SamplePoint p)
        {
            // affine is only fitted for faces with ≥ 3 verts; shorter arrays mean "unfitted"
            bool ok = sf.AffineU.Length == 4 && sf.AffineV.Length == 4 && sf.AffineU[3] < 1e8;
            if (ok)
            {
                double dx = p.X - sf.FrameOrigin.X, dy = p.Y - sf.FrameOrigin.Y, dz = p.Z - sf.FrameOrigin.Z;
                double px = dx * sf.FrameU.X + dy * sf.FrameU.Y + dz * sf.FrameU.Z;
                double py = dx * sf.FrameV.X + dy * sf.FrameV.Y + dz * sf.FrameV.Z;
                return (sf.AffineU[0] * px + sf.AffineU[1] * py + sf.AffineU[3],
                        sf.AffineV[0] * px + sf.AffineV[1] * py + sf.AffineV[3]);
            }
            // exact for triangles: barycentric interpolation in the 2D frame
            if (sf.Verts.Length == 3)
            {
                var o = sf.FrameOrigin; var u = sf.FrameU; var v = sf.FrameV;
                double px = (p.X - o.X) * u.X + (p.Y - o.Y) * u.Y + (p.Z - o.Z) * u.Z;
                double py = (p.X - o.X) * v.X + (p.Y - o.Y) * v.Y + (p.Z - o.Z) * v.Z;
                // 2D positions of the three verts
                double[] qx = new double[3], qy = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    double dx = sf.Verts[i].X - o.X, dy = sf.Verts[i].Y - o.Y, dz = sf.Verts[i].Z - o.Z;
                    qx[i] = dx * u.X + dy * u.Y + dz * u.Z;
                    qy[i] = dx * v.X + dy * v.Y + dz * v.Z;
                }
                double den = (qy[1] - qy[2]) * (qx[0] - qx[2]) + (qx[2] - qx[1]) * (qy[0] - qy[2]);
                if (Math.Abs(den) > 1e-9)
                {
                    double l0 = ((qy[1] - qy[2]) * (px - qx[2]) + (qx[2] - qx[1]) * (py - qy[2])) / den;
                    double l1 = ((qy[2] - qy[0]) * (px - qx[2]) + (qx[0] - qx[2]) * (py - qy[2])) / den;
                    double l2 = 1.0 - l0 - l1;
                    return (l0 * sf.VertexUvs[0].U + l1 * sf.VertexUvs[1].U + l2 * sf.VertexUvs[2].U,
                            l0 * sf.VertexUvs[0].V + l1 * sf.VertexUvs[1].V + l2 * sf.VertexUvs[2].V);
                }
            }
            // nearest vertex fallback
            double best = double.MaxValue;
            int bi = 0;
            for (int i = 0; i < sf.Verts.Length; i++)
            {
                double dx = sf.Verts[i].X - p.X, dy = sf.Verts[i].Y - p.Y, dz = sf.Verts[i].Z - p.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best) { best = d2; bi = i; }
            }
            return sf.VertexUvs[bi];
        }

        internal static (byte R, byte G, byte B) SampleBlock(byte[] block, double u, double v)
        {
            u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1);
            double fx = u * 127, fy = v * 127;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(127, x0 + 1), y1 = Math.Min(127, y0 + 1);
            double tx = fx - x0, ty = fy - y0;
            (byte R, byte G, byte B) Tap(int xx, int yy)
            {
                int p = (yy * 128 + xx) * 3;
                return (block[p], block[p + 1], block[p + 2]);
            }
            var (a, b, c, d) = (Tap(x0, y0), Tap(x1, y0), Tap(x0, y1), Tap(x1, y1));
            (double R, double G, double B) Lerp(double r0, double g0, double b0, double r1, double g1, double b1, double t)
                => (r0 + (r1 - r0) * t, g0 + (g1 - g0) * t, b0 + (b1 - b0) * t);
            var top = Lerp(a.R, a.G, a.B, b.R, b.G, b.B, tx);
            var bot = Lerp(c.R, c.G, c.B, d.R, d.G, d.B, tx);
            var m = Lerp(top.R, top.G, top.B, bot.R, bot.G, bot.B, ty);
            return ((byte)Math.Clamp(Math.Round(m.R), 0, 255),
                    (byte)Math.Clamp(Math.Round(m.G), 0, 255),
                    (byte)Math.Clamp(Math.Round(m.B), 0, 255));
        }

        internal static (byte R, byte G, byte B) SampleFaceValue(byte[] block, SampleFace sf, double u, double v)
        {
            // Sub-texel (degenerate) faces — the compiler's "solid" faces get
            // one UV for the whole face — read the single nearest texel.
            // Bilinear there would blend in texels of NEIGHBORING faces that
            // share the same 128x128 block (unrelated values; measured up to
            // ±48 byte errors, e.g. a 65-ambient face read as 17).
            if ((sf.UMax - sf.UMin) * 127 < 1.0 || (sf.VMax - sf.VMin) * 127 < 1.0)
            {
                int x = Math.Clamp((int)Math.Round(u * 127), 0, 127);
                int y = Math.Clamp((int)Math.Round(v * 127), 0, 127);
                int p = (y * 128 + x) * 3;
                return (block[p], block[p + 1], block[p + 2]);
            }
            // Multi-texel face: clamp into the face's own UV box so the
            // bilinear window never crosses into a neighbor face's texels.
            u = Math.Clamp(u, sf.UMin, sf.UMax);
            v = Math.Clamp(v, sf.VMin, sf.VMax);
            return SampleBlock(block, u, v);
        }

        // ------------------------------------------------------------------
        // Candidate face location (spatial hash over candidate lit faces)
        // ------------------------------------------------------------------

        internal class CandidateIndex
        {
            public const int Cell = 64;
            readonly Dictionary<long, List<SampleFace>> _cells = new();
            public List<SampleFace> Faces = new();

            public CandidateIndex(List<SampleFace> faces)
            {
                Faces = faces;
                foreach (var f in faces)
                {
                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                    foreach (var p in f.Verts)
                    {
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                    }
                    for (long cx = (long)Math.Floor(minX / Cell); cx <= Math.Floor(maxX / Cell); cx++)
                    for (long cy = (long)Math.Floor(minY / Cell); cy <= Math.Floor(maxY / Cell); cy++)
                    for (long cz = (long)Math.Floor(minZ / Cell); cz <= Math.Floor(maxZ / Cell); cz++)
                    {
                        long key = (cx << 21) ^ (cy << 10) ^ cz;
                        if (!_cells.TryGetValue(key, out var l)) _cells[key] = l = new List<SampleFace>();
                        l.Add(f);
                    }
                }
            }

            public SampleFace? Find(double x, double y, double z)
            {
                long cx = (long)Math.Floor(x / Cell), cy = (long)Math.Floor(y / Cell), cz = (long)Math.Floor(z / Cell);
                SampleFace? best = null;
                double bestD = double.MaxValue;
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    long key = ((cx + dx) << 21) ^ ((cy + dy) << 10) ^ (cz + dz);
                    if (!_cells.TryGetValue(key, out var l)) continue;
                    foreach (var f in l)
                    {
                        double plane = f.Normal.X * x + f.Normal.Y * y + f.Normal.Z * z - f.PlaneDist;
                        if (Math.Abs(plane) > 1.5) continue;
                        var o = f.FrameOrigin;
                        var u = f.FrameU; var v = f.FrameV;
                        double ddx = x - o.X, ddy = y - o.Y, ddz = z - o.Z;
                        double px = ddx * u.X + ddy * u.Y + ddz * u.Z;
                        double py = ddx * v.X + ddy * v.Y + ddz * v.Z;
                        if (!PointInHull(f.Hull2D, px, py, 1.0)) continue;
                        // UV-box containment: coplanar neighbors share the
                        // plane test, and on a shared edge the hull test
                        // passes for BOTH. Without this, center-distance
                        // tie-breaks pick a different face per compile and
                        // the read lands on the neighbor's texels (measured:
                        // a point at a sliver corner sampled 1-2 texels
                        // outside the true face's UV box).
                        var (fu, fv) = WorldToUv(f, new SamplePoint { X = x, Y = y, Z = z });
                        double m = 0.75 / 127.0;
                        if (fu < f.UMin - m || fu > f.UMax + m || fv < f.VMin - m || fv > f.VMax + m) continue;
                        double cdx = f.Center.X - x, cdy = f.Center.Y - y, cdz = f.Center.Z - z;
                        double d = cdx * cdx + cdy * cdy + cdz * cdz;
                        if (d < bestD) { bestD = d; best = f; }
                    }
                }
                return best;
            }

            /// <summary>
            /// Loose plane tolerance for dense original-driven pairs. The
            /// recompiled BSP's interior is offset from the original by a few
            /// to ~20 units (decompiler brush recovery is not injective), and
            /// recompiled BSPs drop the sky-box faces entirely, so the strict
            /// 1.5u plane match misses ~40% of original texels (measured on
            /// academy1: 120k interior points with no strict probe face,
            /// offset histogram 3-8u:25k, 9-19u:19k, ≥20u:rest).
            /// </summary>
            public const double LoosePlane = 24.0;

            /// <summary>
            /// Tolerant variant of Find: same 27-cell window, but accepts any
            /// face whose plane is within <paramref name="maxPlane"/> of the
            /// point and whose normal agrees with the point's original face
            /// normal (|n·no| ≥ 0.98 — parallel surfaces qualify; the strict
            /// path already took the exact-plane ones). Closest plane wins,
            /// center distance breaks ties. Ambient is a smooth spatial field,
            /// so a neighbor up to 24u off the true surface reads the local
            /// ambient to within the light falloff gradient — far better than
            /// the global-mean fill the strict path fell back to.
            /// </summary>
            public SampleFace? FindLoose(double x, double y, double z,
                double nx, double ny, double nz, double maxPlane)
            {
                var strict = Find(x, y, z);
                if (strict != null) return strict;
                long cx = (long)Math.Floor(x / Cell), cy = (long)Math.Floor(y / Cell), cz = (long)Math.Floor(z / Cell);
                SampleFace? best = null;
                double bestPlane = double.MaxValue, bestD = double.MaxValue;
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    long key = ((cx + dx) << 21) ^ ((cy + dy) << 10) ^ (cz + dz);
                    if (!_cells.TryGetValue(key, out var l)) continue;
                    foreach (var f in l)
                    {
                        double plane = Math.Abs(f.Normal.X * x + f.Normal.Y * y + f.Normal.Z * z - f.PlaneDist);
                        if (plane > maxPlane) continue;
                        double ndot = Math.Abs(f.Normal.X * nx + f.Normal.Y * ny + f.Normal.Z * nz);
                        if (ndot < 0.98) continue;
                        var o = f.FrameOrigin;
                        var u = f.FrameU; var v = f.FrameV;
                        double ddx = x - o.X, ddy = y - o.Y, ddz = z - o.Z;
                        double px = ddx * u.X + ddy * u.Y + ddz * u.Z;
                        double py = ddx * v.X + ddy * v.Y + ddz * v.Z;
                        if (!PointInHull(f.Hull2D, px, py, 2.0 + 0.1 * maxPlane)) continue;
                        var (fu, fv) = WorldToUv(f, new SamplePoint { X = x, Y = y, Z = z });
                        double m = 2.0 / 127.0;
                        if (fu < f.UMin - m || fu > f.UMax + m || fv < f.VMin - m || fv > f.VMax + m) continue;
                        double cdx = f.Center.X - x, cdy = f.Center.Y - y, cdz = f.Center.Z - z;
                        double d = cdx * cdx + cdy * cdy + cdz * cdz;
                        if (plane < bestPlane || (plane == bestPlane && d < bestD))
                        { best = f; bestPlane = plane; bestD = d; }
                    }
                }
                return best;
            }
        }

        // ------------------------------------------------------------------
        // Pairwise error: original vs a candidate BSP, at shared world points
        // ------------------------------------------------------------------

        internal record SamplePair(double X, double Y, double Z, double Weight,
            byte OR, byte OG, byte OB, byte CR, byte CG, byte CB,
            double NX, double NY, double NZ,
            int PFace, int PBlock, double PU, double PV,
            int OfFace, int OfBlock, double Ou, double Ov);

        /// <summary>
        /// Dense original-driven sample points: every lightmap texel of every
        /// ORIGINAL face is a sample point — the original bake's own resolution
        /// (~150k points on academy1). The old probe-driven set (~5.9k points
        /// on probe faces, deduped per probe texel) was a byproduct of the
        /// no-light probe's lightmap packing, which assigns tiny UV areas to
        /// faces; it resolved only ~4% of the original texels, so individual
        /// point-light pools were worth &lt;0.3 L1 on it and the drop stage
        /// discarded even provably-correct seeds (fitrun11/13: ΔE≈0 for every
        /// light while face-level joins showed corr 0.85+).
        /// Targets are the original texels exactly (direct block read); ambient
        /// is the probe field at the same world point, read through the loose
        /// plane match (the recompiled interior is offset 3-24u from the
        /// original and sky-box faces are absent from recompiled BSPs — the
        /// strict match missed ~40% of texels). The remainder — sky-box
        /// texels and offsets beyond the tolerance — get the local ambient
        /// from matched neighbors (64u/96u, then the global mean). Candidates
        /// are still sampled through their own world→UV affine
        /// (SampleCandidate, loose as well).
        /// </summary>
        internal (List<SamplePair> Pairs, int Unmatched, int Matched)
            BuildOrigPairs(BspData orig, List<SampleFace> origFaces, BspData probe, List<SampleFace> probeFaces)
        {
            var pidx = new CandidateIndex(probeFaces);
            var pairs = new List<SamplePair>();
            int matched = 0, unmatched = 0;
            var noAmb = new List<int>();   // pair indices whose probe face wasn't found
            foreach (var of in origFaces)
            {
                if (of.Verts.Length < 3 || of.VertexUvs.Length != of.Verts.Length) continue;
                // Affine (u,v) = A·(1,px,py) in the face's 2D frame
                // (world = FrameOrigin + FrameU·px + FrameV·py), least-squares
                // over the vertices — UVs are planar on a face.
                double S00 = 0, S01 = 0, S02 = 0, S11 = 0, S12 = 0, S22 = 0;
                double bu0 = 0, bu1 = 0, bu2 = 0, bv0 = 0, bv1 = 0, bv2 = 0;
                for (int i = 0; i < of.Verts.Length; i++)
                {
                    var vrt = of.Verts[i];
                    double dx = vrt.X - of.FrameOrigin.X, dy = vrt.Y - of.FrameOrigin.Y, dz = vrt.Z - of.FrameOrigin.Z;
                    double px = dx * of.FrameU.X + dy * of.FrameU.Y + dz * of.FrameU.Z;
                    double py = dx * of.FrameV.X + dy * of.FrameV.Y + dz * of.FrameV.Z;
                    S00 += 1; S01 += px; S02 += py; S11 += px * px; S12 += px * py; S22 += py * py;
                    double uu = of.VertexUvs[i].U, vv = of.VertexUvs[i].V;
                    bu0 += uu; bu1 += uu * px; bu2 += uu * py;
                    bv0 += vv; bv1 += vv * px; bv2 += vv * py;
                }
                double[,] A = { { S00, S01, S02 }, { S01, S11, S12 }, { S02, S12, S22 } };
                double ridge = 1e-9 * Math.Max(1.0, (A[0, 0] + A[1, 1] + A[2, 2]) / 3);
                var ua = SolveLeastSquares(A, 3, new[] { bu0, bu1, bu2 }, ridge);
                var va = SolveLeastSquares(A, 3, new[] { bv0, bv1, bv2 }, ridge);
                double det = ua[1] * va[2] - ua[2] * va[1];
                if (Math.Abs(det) < 1e-12) continue;   // degenerate UV region
                int x0 = (int)Math.Round(of.UMin * 127), x1 = (int)Math.Round(of.UMax * 127);
                int y0 = (int)Math.Round(of.VMin * 127), y1 = (int)Math.Round(of.VMax * 127);
                if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
                if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
                int nx = x1 - x0 + 1, ny = y1 - y0 + 1;
                if (nx < 2 || ny < 2) continue;         // solid/sub-texel face
                double w = of.Area / (nx * ny);         // area weight per texel
                var block = orig.Lightmaps[of.Block].Data;
                for (int x = x0; x <= x1; x++)
                {
                    double u = x / 127.0;
                    for (int y = y0; y <= y1; y++)
                    {
                        double v = y / 127.0;
                        double du = u - ua[0], dv = v - va[0];
                        double px = (du * va[2] - ua[2] * dv) / det;
                        double py = (ua[1] * dv - du * va[1]) / det;
                        if (!PointInHull(of.Hull2D, px, py)) continue;
                        double wx = of.FrameOrigin.X + of.FrameU.X * px + of.FrameV.X * py;
                        double wy = of.FrameOrigin.Y + of.FrameU.Y * px + of.FrameV.Y * py;
                        double wz = of.FrameOrigin.Z + of.FrameU.Z * px + of.FrameV.Z * py;
                        int o3 = (y * 128 + x) * 3;
                        byte or_ = block[o3], og = block[o3 + 1], ob = block[o3 + 2];
                        var pf = pidx.FindLoose(wx, wy, wz, of.Normal.X, of.Normal.Y, of.Normal.Z,
                            CandidateIndex.LoosePlane);
                        int pface = -1, pblock = -1; double pu = 0, pv = 0;
                        byte pr = 0, pg = 0, pb = 0;
                        if (pf != null)
                        {
                            pface = pf.FaceIndex; pblock = pf.Block;
                            (pu, pv) = WorldToUv(pf, new SamplePoint { X = wx, Y = wy, Z = wz });
                            var pval = SampleFaceValue(probe.Lightmaps[pf.Block].Data, pf, pu, pv);
                            pr = pval.R; pg = pval.G; pb = pval.B;
                            matched++;
                        }
                        else
                        {
                            unmatched++;
                            noAmb.Add(pairs.Count);
                        }
                        pairs.Add(new SamplePair(wx, wy, wz, w, or_, og, ob, pr, pg, pb,
                            of.Normal.X, of.Normal.Y, of.Normal.Z, pface, pblock, pu, pv,
                            of.FaceIndex, of.Block, u, v));
                    }
                }
            }
            // Points with no probe face even under the loose plane test —
            // sky-box faces (recompiled BSPs drop them) and interior offsets
            // beyond the tolerance: fill with the LOCAL ambient, the mean of
            // matched probe values within 64u, then 96u, then the global
            // mean. The ambient field is smooth over tens of units, so a
            // nearby matched value is a faithful local estimate; the old
            // global mean was wrong by up to ~30 at sky-adjacent and
            // pool-adjacent texels and inflated the ambient-only baseline.
            if (noAmb.Count > 0)
            {
                const int Ac = 32;
                var ambGrid = new Dictionary<long, List<int>>();
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (pairs[i].PFace < 0) continue;
                    var p = pairs[i];
                    long key = ((long)Math.Floor(p.X / Ac) << 21) ^ ((long)Math.Floor(p.Y / Ac) << 10) ^ (long)Math.Floor(p.Z / Ac);
                    if (!ambGrid.TryGetValue(key, out var l)) ambGrid[key] = l = new List<int>();
                    l.Add(i);
                }
                double ar = 0, ag = 0, ab = 0; int am = 0;
                foreach (var p in pairs)
                {
                    if (p.PFace < 0) continue;
                    ar += p.CR; ag += p.CG; ab += p.CB; am++;
                }
                var mean = (R: ar / Math.Max(1, am), G: ag / Math.Max(1, am), B: ab / Math.Max(1, am));
                foreach (int idx in noAmb)
                {
                    var p = pairs[idx];
                    long cx = (long)Math.Floor(p.X / Ac), cy = (long)Math.Floor(p.Y / Ac), cz = (long)Math.Floor(p.Z / Ac);
                    (double r, double g, double b)? found = null;
                    for (int rad = 2; rad <= 3 && found == null; rad++)
                    {
                        double sr = 0, sg = 0, sb = 0; int sn = 0;
                        double lim = rad * 32.0;
                        for (long dx = -rad; dx <= rad; dx++)
                        for (long dy = -rad; dy <= rad; dy++)
                        for (long dz = -rad; dz <= rad; dz++)
                        {
                            long k = ((cx + dx) << 21) ^ ((cy + dy) << 10) ^ (cz + dz);
                            if (!ambGrid.TryGetValue(k, out var l)) continue;
                            foreach (int j in l)
                            {
                                var q = pairs[j];
                                double ddx = q.X - p.X, ddy = q.Y - p.Y, ddz = q.Z - p.Z;
                                if (ddx * ddx + ddy * ddy + ddz * ddz > lim * lim) continue;
                                sr += q.CR; sg += q.CG; sb += q.CB; sn++;
                            }
                        }
                        if (sn > 0) found = (sr / sn, sg / sn, sb / sn);
                    }
                    var val = found ?? mean;
                    pairs[idx] = p with
                    {
                        CR = (byte)Math.Clamp(Math.Round(val.r), 0, 255),
                        CG = (byte)Math.Clamp(Math.Round(val.g), 0, 255),
                        CB = (byte)Math.Clamp(Math.Round(val.b), 0, 255)
                    };
                }
            }
            return (pairs, unmatched, matched);
        }

        internal static double L1Error(List<SamplePair> pairs)
        {
            double total = 0, wsum = 0;
            foreach (var p in pairs)
            {
                total += p.Weight * (Math.Abs(p.OR - p.CR) + Math.Abs(p.OG - p.CG) + Math.Abs(p.OB - p.CB));
                wsum += p.Weight;
            }
            return wsum > 0 ? total / wsum : 0;
        }

        // ------------------------------------------------------------------
        // Linear joint fit
        // ------------------------------------------------------------------

        /// <summary>
        /// Build the normal-equations G/h for the given light subset (sparse:
        /// most single fields are zero at most points, so accumulate only over
        /// non-zero rows). idxList maps solve-columns to singleFields indices.
        /// </summary>
        internal static void BuildGh(List<SamplePair> pairs, List<List<(double R, double G, double B)>> singleFields,
            List<int> idxList, List<(double R, double G, double B)> ambient, out double[,] G, out double[] h)
        {
            int nL = idxList.Count;
            int n = 3 * nL;
            G = new double[n, n];
            h = new double[n];
            for (int id = 0; id < pairs.Count; id++)
            {
                var p = pairs[id];
                // target relative to the (known) sky field
                var amb = ambient[id];
                double[] t = { p.OR - amb.R, p.OG - amb.G, p.OB - amb.B };
                // collect non-zero s entries
                int[] nz = new int[3 * nL];
                int nzC = 0;
                double[] s = new double[n];
                for (int k = 0; k < nL; k++)
                {
                    var f = singleFields[idxList[k]][id];
                    if (f.R != 0) nz[nzC++] = 3 * k;
                    if (f.G != 0) nz[nzC++] = 3 * k + 1;
                    if (f.B != 0) nz[nzC++] = 3 * k + 2;
                    s[3 * k] = f.R; s[3 * k + 1] = f.G; s[3 * k + 2] = f.B;
                }
                for (int a = 0; a < nzC; a++)
                {
                    int i = nz[a];
                    double si = s[i];
                    for (int b = a; b < nzC; b++)
                    {
                        int j = nz[b];
                        G[i, j] += p.Weight * si * s[j];
                    }
                    for (int c = 0; c < 3; c++) h[i] += p.Weight * si * t[c];
                }
            }
        }

        /// <summary>
        /// Solve min Σ w‖T − sky − Σ_l S_l·s_l‖² for per-light scales s_l (3 each).
        /// The sky field (probe compile) is a known per-point offset — it is
        /// baked into every candidate compile, so it must not be a fitted
        /// constant (a fitted constant would be absent from the bake and the
        /// stage-A error would collapse to ‖c0‖).
        /// S_l at a point = single-l compile value minus the probe value.
        /// </summary>
        internal static (double[] C0, double[][] Scales, double Residual)
            FitLinear(List<SamplePair> pairs, List<List<(double R, double G, double B)>> singleFields, int nLights,
                List<(double R, double G, double B)> ambient, out double residualWithAll)
        {
            int n = 3 * nLights;
            var idxAll = new List<int>();
            for (int l = 0; l < nLights; l++) idxAll.Add(l);
            BuildGh(pairs, singleFields, idxAll, ambient, out double[,] G, out double[] h);
            // relative ridge: 1e-5 of mean diagonal — damps (near)singular twin
            // columns without biasing well-conditioned ones
            double trace = 0;
            for (int i = 0; i < n; i++) trace += G[i, i];
            double ridge = Math.Max(1e-4, (n > 0 ? trace / n : 0) * 1e-5);
            double[] x = n > 0 ? SolveLeastSquares(G, n, h, ridge) : Array.Empty<double>();
            // clip negatives, refit on active set (2 passes)
            for (int pass = 0; pass < 2; pass++)
            {
                var active = new List<int>();
                for (int i = 0; i < n; i++) if (x[i] > 1e-6) active.Add(i);
                if (active.Count >= 2)
                {
                    int m = active.Count;
                    var G2 = new double[m, m];
                    var h2 = new double[m];
                    for (int i = 0; i < m; i++)
                    {
                        for (int j = i; j < m; j++) G2[i, j] = G[active[i], active[j]];
                        h2[i] = h[active[i]];
                    }
                    for (int i = 0; i < m; i++) for (int j = 0; j < i; j++) G2[i, j] = G2[j, i];
                    var x2 = SolveLeastSquares(G2, m, h2, 0);
                    x = new double[n];
                    for (int i = 0; i < m; i++) x[active[i]] = x2[i];
                }
            }
            double residual = 0, wsum = 0;
            for (int id = 0; id < pairs.Count; id++)
            {
                var p = pairs[id];
                var amb = ambient[id];
                double r = p.OR - amb.R, g = p.OG - amb.G, b = p.OB - amb.B;
                for (int l = 0; l < nLights; l++)
                {
                    r -= x[3 * l] * singleFields[l][id].R;
                    g -= x[3 * l + 1] * singleFields[l][id].G;
                    b -= x[3 * l + 2] * singleFields[l][id].B;
                }
                residual += p.Weight * (r * r + g * g + b * b);
                wsum += p.Weight;
            }
            residualWithAll = wsum > 0 ? Math.Sqrt(residual / wsum) : 0;
            if (Environment.GetEnvironmentVariable("JKD_FIELDDBG") == "1")
            {
                var rowsX = new List<object>();
                for (int l = 0; l < nLights; l++)
                    rowsX.Add(new { l, x = new[] { x[3 * l], x[3 * l + 1], x[3 * l + 2] } });
                File.WriteAllText(Path.Combine(System.IO.Path.GetTempPath(), "fit_x.json"),
                    JsonSerializer.Serialize(new { n, residualWithAll, x = rowsX }, new JsonSerializerOptions { WriteIndented = true }));
            }
            var scales = new double[nLights][];
            for (int l = 0; l < nLights; l++)
                scales[l] = new[] { Math.Max(0, x[3 * l]), Math.Max(0, x[3 * l + 1]), Math.Max(0, x[3 * l + 2]) };
            // informational: mean sky (the actual per-point offset lives in ambient)
            double ar = 0, ag = 0, ab = 0;
            foreach (var a in ambient) { ar += a.R; ag += a.G; ab += a.B; }
            int ac = Math.Max(1, ambient.Count);
            return (new[] { ar / ac, ag / ac, ab / ac }, scales, residualWithAll);
        }

        /// <summary>
        /// Rank-1 basis of one light's sensitivity field: every point of the field
        /// is falloff × constant color, so the 3x3 second-moment A = Σ f fᵀ is
        /// (near) rank-1. One power step A·(1,1,1) gives the color direction
        /// exactly (rank-1 ⇒ any non-orthogonal start lands on the range); the
        /// scalar field G(q) = S(q)·v is the falloff.
        /// </summary>
        internal class LightBasis
        {
            public double VR, VG, VB;    // unit color direction
            public List<double> G;       // per-point scalar falloff field S·v
        }

        internal static LightBasis BuildBasis(List<(double R, double G, double B)> field)
        {
            double aRR = 0, aRG = 0, aRB = 0, aGG = 0, aGB = 0, aBB = 0;
            foreach (var f in field)
            {
                aRR += f.R * f.R; aRG += f.R * f.G; aRB += f.R * f.B;
                aGG += f.G * f.G; aGB += f.G * f.B; aBB += f.B * f.B;
            }
            double vr = aRR + aRG + aRB, vg = aRG + aGG + aGB, vb = aRB + aGB + aBB;
            double vn = Math.Sqrt(vr * vr + vg * vg + vb * vb);
            var g = new List<double>(field.Count);
            if (vn < 1e-12)
            {
                foreach (var f in field) g.Add(0);
                return new LightBasis { VR = 1, VG = 0, VB = 0, G = g };
            }
            vr /= vn; vg /= vn; vb /= vn;
            foreach (var f in field) g.Add(f.R * vr + f.G * vg + f.B * vb);
            return new LightBasis { VR = vr, VG = vg, VB = vb, G = g };
        }

        /// <summary>
        /// In-sample RMS error for scalar scales in the rank-1 light basis.
        /// </summary>
        internal static double RmsOf1D(List<SamplePair> pairs, List<LightBasis> basis, double[] gscales,
            List<(double R, double G, double B)> ambient)
        {
            double err = 0, wsum = 0;
            for (int id = 0; id < pairs.Count; id++)
            {
                var p = pairs[id];
                var amb = ambient[id];
                double r = p.OR - amb.R, g = p.OG - amb.G, b = p.OB - amb.B;
                for (int l = 0; l < gscales.Length; l++)
                {
                    double s = gscales[l] * basis[l].G[id];
                    r -= s * basis[l].VR; g -= s * basis[l].VG; b -= s * basis[l].VB;
                }
                err += p.Weight * (r * r + g * g + b * b);
                wsum += p.Weight;
            }
            return wsum > 0 ? Math.Sqrt(err / wsum) : 0;
        }

        /// <summary>
        /// Normal equations for the scalar-scale model T ≈ sky + Σ_l s_l·G_l·v_l:
        /// G_ij = Σw·G_i·G_j·(v_i·v_j),  h_i = Σw·G_i·(t·v_i).  n×n, n = idxList.Count.
        /// </summary>
        internal static (double[,] G, double[] h) BuildGh1D(List<SamplePair> pairs,
            List<(double R, double G, double B)> ambient, List<LightBasis> basis, List<int> idxList)
        {
            int n = idxList.Count;
            var G = new double[n, n];
            var h = new double[n];
            for (int id = 0; id < pairs.Count; id++)
            {
                var p = pairs[id];
                var amb = ambient[id];
                double tr = p.OR - amb.R, tg = p.OG - amb.G, tb = p.OB - amb.B;
                double[] gv = new double[n];
                double[] dot = new double[n];
                for (int a = 0; a < n; a++)
                {
                    gv[a] = basis[idxList[a]].G[id];
                    var v = basis[idxList[a]];
                    dot[a] = tr * v.VR + tg * v.VG + tb * v.VB;
                }
                for (int a = 0; a < n; a++)
                {
                    if (gv[a] == 0) continue;
                    var va = basis[idxList[a]];
                    for (int b = a; b < n; b++)
                    {
                        if (gv[b] == 0) continue;
                        var vb = basis[idxList[b]];
                        G[a, b] += p.Weight * gv[a] * gv[b] * (va.VR * vb.VR + va.VG * vb.VG + va.VB * vb.VB);
                    }
                    h[a] += p.Weight * gv[a] * dot[a];
                }
            }
            return (G, h);
        }

        /// <summary>
        /// Lawson–Hanson non-negative least squares: min ||Gs − h||² subject to
        /// s ≥ 0. Exact active-set algorithm — converges in finite steps for a
        /// positive-definite G. Used on the greedy-selected light set, where the
        /// normal equations are ill-conditioned (overlapping monochrome pools) and
        /// clamped coordinate descent oscillates. Ridge (relative to mean diagonal)
        /// is added by the caller for conditioning; it makes the solution the
        /// exact optimum of the slightly regularized problem, which for relative
        /// ridge 1e-3 differs from the unregularized optimum by ~0.1% in scale.
        /// </summary>
        public static double[] NnlsSolve(double[,] G, double[] h, double ridge = 0.0)
        {
            int n = h.Length;
            // add ridge to the diagonal (copy — the caller's matrix may be reused)
            for (int i = 0; i < n; i++) G[i, i] += ridge;

            var s = new double[n];
            var inSet = new bool[n];

            for (int iter = 0; iter < 4 * n + 4; iter++)
            {
                // residual gradient: g_j = (G^T (h − G s))_j
                var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double acc = h[i];
                    for (int j = 0; j < n; j++) acc -= G[i, j] * s[j];
                    r[i] = acc;
                }
                var grad = new double[n];
                double gnorm = 0;
                for (int j = 0; j < n; j++)
                {
                    double acc = 0;
                    for (int i = 0; i < n; i++) acc += G[i, j] * r[i];
                    grad[j] = acc;
                    gnorm += acc * acc;
                }
                // candidate: largest positive gradient outside the set
                int jNew = -1;
                double best = 1e-12 * Math.Max(1.0, Math.Sqrt(gnorm));
                for (int j = 0; j < n; j++)
                    if (!inSet[j] && grad[j] > best) { best = grad[j]; jNew = j; }
                if (jNew < 0) break; // converged
                inSet[jNew] = true;

                // inner loop: solve G_PP s_P = h_P; if any component < 0, drop the
                // most negative index and re-solve; when all ≥ 0, apply and go back
                // to the gradient test (NOT terminate — other variables may still
                // improve the fit).
                for (int inner = 0; inner < n + 1; inner++)
                {
                    var idx = new List<int>();
                    for (int j = 0; j < n; j++) if (inSet[j]) idx.Add(j);
                    int m = idx.Count;
                    var A = new double[m, m];
                    var b = new double[m];
                    for (int i = 0; i < m; i++)
                    {
                        b[i] = h[idx[i]];
                        for (int j = 0; j < m; j++) A[i, j] = G[idx[i], idx[j]];
                    }
                    double[] sp = SolveLinear(A, b, m);
                    if (sp == null)
                    {
                        // singular active set: undo this addition and let the outer
                        // loop pick the next candidate
                        inSet[jNew] = false;
                        break;
                    }
                    int worst = -1;
                    double worstVal = 0;
                    for (int i = 0; i < m; i++)
                        if (sp[i] < worstVal) { worstVal = sp[i]; worst = i; }
                    if (worst < 0)
                    {
                        for (int i = 0; i < m; i++) s[idx[i]] = sp[i];
                        break; // apply; outer loop re-tests the gradient
                    }
                    inSet[idx[worst]] = false;
                    s[idx[worst]] = 0;
                }
            }
            for (int j = 0; j < n; j++) if (s[j] < 0) s[j] = 0;
            return s;
        }

        /// <summary>
        /// Solves the m×m system Ax=b by Gaussian elimination with partial
        /// pivoting. Returns null if singular. m ≤ ~20 here.
        /// </summary>
        internal static double[] SolveLinear(double[,] A, double[] b, int m)
        {
            var M = new double[m, m + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < m; j++) M[i, j] = A[i, j];
                M[i, m] = b[i];
            }
            for (int col = 0; col < m; col++)
            {
                int piv = col;
                for (int row = col + 1; row < m; row++)
                    if (Math.Abs(M[row, col]) > Math.Abs(M[piv, col])) piv = row;
                if (Math.Abs(M[piv, col]) < 1e-14) return null;
                if (piv != col)
                    for (int j = 0; j <= m; j++)
                    {
                        double t = M[col, j]; M[col, j] = M[piv, j]; M[piv, j] = t;
                    }
                for (int row = col + 1; row < m; row++)
                {
                    double f = M[row, col] / M[col, col];
                    for (int j = col; j <= m; j++) M[row, j] -= f * M[col, j];
                }
            }
            var x = new double[m];
            for (int i = m - 1; i >= 0; i--)
            {
                double acc = M[i, m];
                for (int j = i + 1; j < m; j++) acc -= M[i, j] * x[j];
                x[i] = acc / M[i, i];
            }
            return x;
        }

        /// <summary>
        /// Greedy forward selection (orthogonal matching pursuit) over the scalar
        /// light fields. Each round picks the unused light whose 1D projection onto
        /// the current residual has the largest weighted gain, and subtracts it.
        /// Robust where pools overlap: the first light of a cluster absorbs the
        /// shared signal, the twins project to ~0 and are never selected.
        /// Returns the selection order and scalar scales (per light index).
        /// </summary>
        internal static (List<int> Order, double[] Scales)
            FitGreedy1D(List<SamplePair> pairs, List<(double R, double G, double B)> ambient,
                List<LightBasis> basis, int nLights)
        {
            int nP = pairs.Count;
            double[] rr = new double[nP], rg = new double[nP], rb = new double[nP];
            double e0 = 0;
            for (int id = 0; id < nP; id++)
            {
                var amb = ambient[id];
                rr[id] = pairs[id].OR - amb.R; rg[id] = pairs[id].OG - amb.G; rb[id] = pairs[id].OB - amb.B;
                e0 += pairs[id].Weight * (rr[id] * rr[id] + rg[id] * rg[id] + rb[id] * rb[id]);
            }
            const double minGainFrac = 1e-4; // stop when best round < 0.01% of initial residual energy
            double minGain = e0 * minGainFrac;
            var order = new List<int>();
            var picked = new HashSet<int>();
            var scales = new double[nLights];
            for (int round = 0; round < nLights; round++)
            {
                int bestL = -1;
                double bestGain = 0;
                double bestS = 0;
                for (int l = 0; l < nLights; l++)
                {
                    if (picked.Contains(l)) continue;
                    double num = 0, den = 0;
                    var v = basis[l];
                    var Gf = v.G;
                    for (int id = 0; id < nP; id++)
                    {
                        double g = Gf[id];
                        if (g == 0) continue;
                        double w = pairs[id].Weight;
                        num += w * g * (rr[id] * v.VR + rg[id] * v.VG + rb[id] * v.VB);
                        den += w * g * g;
                    }
                    if (den < 1e-9) continue;
                    double s = num / den;
                    double gain = num * num / den;
                    if (gain > bestGain)
                    {
                        bestGain = gain; bestL = l; bestS = s;
                    }
                }
                if (Environment.GetEnvironmentVariable("JKD_FIELDDBG") == "1")
                    System.Console.Error.WriteLine($"[greedy] round {round}: best light {bestL} gain {bestGain:F3} (min {minGain:F3}) scale {bestS:F5}");
                if (bestL < 0 || bestGain < minGain) break;
                picked.Add(bestL);
                order.Add(bestL);
                scales[bestL] = bestS;
                var Gupd = basis[bestL].G;
                var vb = basis[bestL];
                for (int id = 0; id < nP; id++)
                {
                    double g = Gupd[id];
                    if (g == 0) continue;
                    double s = bestS * g;
                    rr[id] -= s * vb.VR; rg[id] -= s * vb.VG; rb[id] -= s * vb.VB;
                }
            }
            return (order, scales);
        }

        /// <summary>Weighted normal-equations solve with Tikhonov ridge.</summary>
        internal static double[] SolveLeastSquares(double[,] G, int n, double[] h, double ridge)
        {
            var rows = new double[n][];
            for (int i = 0; i < n; i++)
            {
                rows[i] = new double[n + 1];
                for (int j = 0; j < n; j++) rows[i][j] = G[i, j];
                rows[i][n] = h[i];
                if (i < n - 1) rows[i][i] += ridge;
            }
            // gaussian elimination with partial pivoting, element-wise swaps
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(rows[r][col]) > Math.Abs(rows[piv][col])) piv = r;
                if (Math.Abs(rows[piv][col]) < 1e-9) continue;
                if (piv != col)
                {
                    var tr = rows[piv]; rows[piv] = rows[col]; rows[col] = tr;
                }
                for (int r = col + 1; r < n; r++)
                {
                    double f = rows[r][col] / rows[col][col];
                    if (f == 0) continue;
                    for (int c = col; c <= n; c++) rows[r][c] -= f * rows[col][c];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                if (Math.Abs(rows[r][r]) < 1e-9) { x[r] = 0; continue; }
                double s = rows[r][n];
                for (int c = r + 1; c < n; c++) s -= rows[r][c] * x[c];
                x[r] = s / rows[r][r];
            }
            return x;
        }

        // ------------------------------------------------------------------
        // Compile driver
        // ------------------------------------------------------------------

        internal class CompileResult
        {
            public int ExitCode;
            public bool HasLightmaps;
            public string Log = "";
            public long ElapsedMs;
        }

        /// <summary>
        /// Compile one light variant: copy the .map into a scratch dir, run
        /// q3map2 -bsp then -light (this build's two-stage model), read back the lit BSP.
        /// </summary>
        internal CompileResult CompileMap(string mapText, string baseName, string scratchRoot, out BspData? bsp)
        {
            bsp = null;
            var dir = Path.Combine(scratchRoot, baseName);
            Directory.CreateDirectory(dir);
            string mapPath = Path.Combine(dir, baseName + ".map");
            File.WriteAllText(mapPath, mapText);
            var res = new CompileResult();
            var sw = Stopwatch.StartNew();
            string bspPath = Path.Combine(dir, baseName + ".bsp");
            // Delete any stale BSP so a failed compile can't silently read old
            // lightmaps (a q3map2 that fails to launch — e.g. a missing shared
            // lib — leaves the prior run's BSP in place, which was then read as
            // if it were fresh). Forcing a fresh build makes failures visible.
            if (File.Exists(bspPath)) File.Delete(bspPath);
            try
            {
                var (bspLog, bspCode) = RunQ3map2(new[] { "-bsp", Path.GetFileName(mapPath) }, dir);
                if (bspCode != 0) { res.ExitCode = bspCode; res.Log = bspLog; return res; }
                var (lightLog, lightCode) = RunQ3map2(
                    new[] { "-light", "-lightmapsize", Options.LightmapSize.ToString(), baseName + ".bsp" }, dir);
                res.Log = bspLog + "\n" + lightLog;
                if (lightCode != 0) { res.ExitCode = lightCode; return res; }
                if (!File.Exists(bspPath)) { res.ExitCode = 2; return res; }
                bsp = new BspReader().Read(bspPath);
                res.HasLightmaps = bsp.Lightmaps.Count > 0;
            }
            catch (Exception ex)
            {
                res.ExitCode = 3;
                res.Log += "\n" + ex;
            }
            finally
            {
                res.ElapsedMs = sw.ElapsedMilliseconds;
            }
            return res;
        }

        private (string Log, int ExitCode) RunQ3map2(string[] args, string workDir)
        {
            var psi = new ProcessStartInfo
            {
                // Absolute path: the garux fork resolves its VFS base path from
                // the executable path; a relative binary fails at startup with
                // "Failed to find a valid base path" (exit 1).
                FileName = Path.GetFullPath(Q3map2Bin),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
            };
            // -game ja: this fork's JKA profile (games.cpp game_ja : game_sof2)
            // sets VFS dir <basepath>/base, 128x128 lightmaps, and RBSP v1
            // read/write — the format BspReader expects. (Spike logs show the
            // matching "VFS Init: ~/.ja/base/".)
            psi.ArgumentList.Add("-game");
            psi.ArgumentList.Add("ja");
            psi.ArgumentList.Add("-fs_basepath");
            psi.ArgumentList.Add(GameDir);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LD_LIBRARY_PATH"] = Path.GetDirectoryName(Path.GetFullPath(Q3map2Bin)) ?? "";
            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("q3map2 failed to start: " + Q3map2Bin);
            string outp = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10 * 60 * 1000))
            {
                proc.Kill(true);
                throw new TimeoutException("q3map2 timed out: " + string.Join(" ", args));
            }
            return (outp + err, proc.ExitCode);
        }

        // ------------------------------------------------------------------
        // Stage 2 driver
        // ------------------------------------------------------------------

        /// <summary>
        /// Run the full closed loop: seeds → single compiles → linear joint fit →
        /// prune → analytic position refinement → final compile. Writes artifacts
        /// to outDir: fit.map, fit_report.json, fit_lights_overlay.png, lightmap_diff.png.
        /// </summary>
        public LightFitResult Fit(BspData origBsp, string baseMapPath, string outDir, string scratchRoot, string mapLabel)
        {
            var sw = Stopwatch.StartNew();
            var res = new LightFitResult();
            Result = res;
            Directory.CreateDirectory(outDir);

            // 1. seeds — either external (lightmap-derived; stage 1's grid
            // ray-triangulation lands 300-1300u off the true light positions,
            // which the lightmap — the fit's ground truth — places on the
            // floor/wall peaks) or from stage-1 grid recovery.
            var lights = new List<FitLight>();
            if (!string.IsNullOrEmpty(Options.SeedsPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(Options.SeedsPath));
                foreach (var el in doc.RootElement.GetProperty("seeds").EnumerateArray())
                {
                    var pos = el.GetProperty("position");
                    var col = el.GetProperty("color");
                    double level = el.TryGetProperty("level", out var lv) ? lv.GetDouble() : 1024;
                    var p = new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle());
                    var name = el.TryGetProperty("name", out var nm) ? nm.GetString() ?? "lm" : "lm";

                    // Push seeds out of solids: a seed placed inside a wall/slab
                    // has its light culled or nudged by q3map2 (measured: culled
                    // seeds bake byte-identical fields; nudged ones bake displaced
                    // pools that correlate ~0.04 with the target instead of ~0.2+).
                    // Push IN-PLANE first (8 directions in the face's tangent plane,
                    // capped at the seed radius) so the light keeps pool height —
                    // pushing along the normal crosses tall pillars/beams (academy1
                    // has a 5464u beam through the floor) and leaves the light
                    // farther than its radius from the pool, baking nothing there.
                    // Only if no in-plane escape falls back to a short normal push.
                    var n = el.TryGetProperty("normal", out var nrm)
                        ? new Vector3(nrm[0].GetSingle(), nrm[1].GetSingle(), nrm[2].GetSingle())
                        : Vector3.Zero;
                    if (Vector3.Dot(n, n) < 0.5) n = Vector3.Zero;
                    if (PointInWorldSolid(origBsp, p) && Vector3.Dot(n, n) >= 0.5)
                    {
                        var nn = Vector3.Normalize(n);
                        // tangent frame: any orthonormal basis perpendicular to n
                        var help = Math.Abs(nn.Z) < 0.9 ? Vector3.UnitZ : Vector3.UnitX;
                        var u = Vector3.Normalize(Vector3.Cross(nn, help));
                        var v = Vector3.Cross(nn, u);
                        Vector3? escape = null;
                        int escDir = -1, escDist = 0;
                        int radCap = (int)Math.Min(512, Math.Max(16,
                            el.TryGetProperty("radius", out var rd2) ? rd2.GetDouble() : 256));
                        for (int dir = 0; dir < 8 && escape == null; dir++)
                        {
                            double ang = dir * Math.PI / 4;
                            var d = u * (float)Math.Cos(ang) + v * (float)Math.Sin(ang);
                            for (int dd = 8; dd <= radCap; dd += 8)
                            {
                                var q = p + d * dd;
                                if (!PointInWorldSolid(origBsp, q)) { escape = q; escDir = dir; escDist = dd; break; }
                            }
                        }
                        if (escape != null)
                        {
                            p = escape.Value;
                            Console.Error.WriteLine($"[seeds] {name}: pushed {escDist}u in-plane dir{escDir} (was in solid)");
                        }
                        else
                        {
                            int pushed = 0;
                            for (int i = 0; i < 16 && PointInWorldSolid(origBsp, p); i++)
                            {
                                p += n * Options.PosStep;
                                pushed++;
                            }
                            if (PointInWorldSolid(origBsp, p))
                            {
                                Console.Error.WriteLine($"[seeds] {name}: DROPPED — in solid, no escape within radius (in-plane) or {16 * Options.PosStep}u normal");
                                continue;
                            }
                            Console.Error.WriteLine($"[seeds] {name}: pushed {pushed * Options.PosStep}u along normal (in-plane failed)");
                        }
                    }
                    lights.Add(new FitLight
                    {
                        Name = name,
                        SeedName = name,
                        SeedPosition = p,
                        Position = p,
                        Style = el.TryGetProperty("style", out var st) ? st.GetInt32() : 0,
                        Radius = el.TryGetProperty("radius", out var rd) ? rd.GetInt32() : 1024,
                        Scale = new[]
                        {
                            level * col[0].GetSingle() / 255.0,
                            level * col[1].GetSingle() / 255.0,
                            level * col[2].GetSingle() / 255.0
                        },
                    });
                }
                Console.Error.WriteLine($"[seeds] external: {lights.Count} from {Options.SeedsPath}");
            }
            else
            {
                var lr = new LightRecovery();
                var lrRes = lr.Recover(origBsp);
                if (!lrRes.GridValid)
                {
                    res.Note = $"lightgrid invalid — stage 2 needs direction data: {lrRes.GridNote}";
                    WriteEmptyOutputs(outDir, baseMapPath, mapLabel, res.Note);
                    return res;
                }
                var seeds = lrRes.Lights
                    .OrderBy(l => l.LevelConsistency)
                    .ThenByDescending(l => l.TPositiveFrac)
                    .ThenByDescending(l => l.CellCount)
                    .ToList();
                // Merge duplicate seeds: stage 1 can seed one physical light
                // twice (adjacent grid cells / style variants). Identical
                // positions make the normal equations (near)singular — the
                // joint solver then splits the signal across the twin columns
                // (alternating sign, both ~0). Seeds arrive best-first, so the
                // survivor is the stronger seed.
                if (Options.MergeDistance > 0)
                {
                    var merged = new List<RecoveredLight>();
                    foreach (var s in seeds)
                    {
                        var twin = merged.FirstOrDefault(m =>
                            Vector3.Distance(m.Position, s.Position) <= Options.MergeDistance);
                        if (twin != null)
                            twin.CellCount += s.CellCount; // evidence folded into the survivor
                        else
                            merged.Add(s);
                    }
                    seeds = merged;
                }
                if (Options.MaxSeeds > 0 && seeds.Count > Options.MaxSeeds)
                    seeds = seeds.Take(Options.MaxSeeds).ToList();
                foreach (var s in seeds)
                {
                    lights.Add(new FitLight
                    {
                        Name = s.Name,
                        SeedName = s.Name,
                        SeedPosition = s.Position,
                        Position = s.Position,
                        Style = s.Style,
                        Scale = new[]
                        {
                            Stage1Scale(s, 0), Stage1Scale(s, 1), Stage1Scale(s, 2)
                        },
                    });
                }
            }
            int nL = lights.Count;
            res.Lights = lights;

            // 2. base map without any pre-existing lights
            string baseText = StripLightEntities(File.ReadAllText(baseMapPath));

            // 3. compile targets: original lightmap values at shared world points
            var origFaces = BuildSampleFaces(origBsp);
            // Probe compile WITH a tiny dummy light: with zero lights
            // q3map2 assigns lightmaps to far fewer faces (5 blocks vs 10 on
            // academy1), so probe values would not pointwise correspond to the
            // single-light fields and every fit column would share a large
            // global sky-mask component.
            // The dummy's level must be invisible under the ACTIVE falloff.
            // Under JKA quadratic (atten = 1-(d/r)^2) a level-1 light bakes a
            // ~255 dome across its 1024u radius (measured: E_ambient 123.58
            // with it vs 18.72 without — the worldspawn ambient is fine, the
            // whole excess was the dummy). Level 0.01 adds a negligible few
            // tenths of a byte at the peak and stays indistinguishable from
            // the pure sky/ambient field.
            double vminx = double.MaxValue, vminy = double.MaxValue, vminz = double.MaxValue;
            double vmaxx = double.MinValue, vmaxy = double.MinValue, vmaxz = double.MinValue;
            foreach (var v in origBsp.Vertices)
            {
                vminx = Math.Min(vminx, v.Position.X); vmaxx = Math.Max(vmaxx, v.Position.X);
                vminy = Math.Min(vminy, v.Position.Y); vmaxy = Math.Max(vmaxy, v.Position.Y);
                vminz = Math.Min(vminz, v.Position.Z); vmaxz = Math.Max(vmaxz, v.Position.Z);
            }
            var center = new Vector3((float)((vminx + vmaxx) / 2), (float)((vminy + vmaxy) / 2), (float)((vminz + vmaxz) / 2));
            // The dummy's radius must SPAN the map: q3map2 only assigns
            // lightmaps to faces touched by a light's envelope, and
            // BuildOrigPairs reads the ambient from the probe at every
            // original texel's world point. At the old fixed 1024u the probe
            // covered only the central 10 of academy1's 24 blocks, so every
            // seed pool outside that sphere had zero field on the sample set
            // and the fit could never see it (measured: 6/20 single fields
            // byte-identical dead while their compiles were healthy 10-block
            // bakes).
            double dummyR = Math.Sqrt(Math.Pow((vmaxx - vminx) / 2, 2)
                + Math.Pow((vmaxy - vminy) / 2, 2)
                + Math.Pow((vmaxz - vminz) / 2, 2)) * 1.05;
            var dummy = new FitLight
            {
                Name = "__fit_dummy__",
                SeedName = "__fit_dummy__",
                SeedPosition = center,
                Position = center,
                Style = 0,
                // 0.01, not 1.0: invisible under the JKA quadratic falloff
                // (a level-1 dummy bakes a ~255 dome — see above), but still
                // non-zero, so lightmap assignment is forced as before.
                Scale = new[] { 0.01, 0.01, 0.01 },
                Radius = (int)Math.Ceiling(Math.Min(8192, dummyR)),
            };
            string probeName = $"fit_probe";
            string probeText = baseText + "\n// fit probe (dummy ambient-referenced light)\n"
                + EntityText(dummy, dummy.Name, false);
            var probe = CompileMap(probeText, probeName, scratchRoot, out BspData? probeBsp);
            res.CompilesRun++;
            if (probeBsp == null || probeBsp.Lightmaps.Count == 0)
            {
                res.Note = "probe compile produced no lightmaps: " + Tail(probe.Log, 400);
                WriteEmptyOutputs(outDir, baseMapPath, mapLabel, res.Note);
                return res;
            }
            var probeFaces = BuildSampleFaces(probeBsp);
            var (pairs, unmatched, matched) = BuildOrigPairs(origBsp, origFaces, probeBsp, probeFaces);
            res.UnmatchedPoints = unmatched;
            res.MatchedPoints = matched;
            if (pairs.Count == 0)
            {
                res.Note = "no matched sample points between probe and original";
                WriteEmptyOutputs(outDir, baseMapPath, mapLabel, res.Note);
                return res;
            }

            // ambient/sky baseline: the probe values are stored in the pairs themselves
            var ambientVals = pairs
                .Select(p => (R: (double)p.CR, G: (double)p.CG, B: (double)p.CB))
                .ToList();
            res.ErrorAmbientOnly = L1Of(pairs, ambientVals);
            if (Environment.GetEnvironmentVariable("JKD_PAIRS") == "1")
            {
                var rows = new List<object>(pairs.Count);
                for (int id = 0; id < pairs.Count; id++)
                {
                    var p = pairs[id];
                    var a = ambientVals[id];
                    rows.Add(new { x = p.X, y = p.Y, z = p.Z, w = p.Weight,
                        o = new[] { p.OR, p.OG, p.OB }, s = new[] { a.R, a.G, a.B },
                        of = p.OfFace, ob = p.OfBlock, ou = p.Ou, ov = p.Ov });
                }
                File.WriteAllText(Path.Combine(scratchRoot, "pairs.json"),
                    JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false }));
            }

            // 4. single-light compiles → exact per-light forward fields.
            // The -light stage bakes worldspawn ambient into every lightmap, so
            // each single field is (ambient + light_l); subtract the probe field
            // to get the pure per-light contribution and keep the fit columns
            // linearly independent.
            var singleFields = new List<List<(double R, double G, double B)>>();
            var fields = new List<(double R, double G, double B)>[nL];
            var logs = new string[nL];
            var gate = new SemaphoreSlim(Math.Max(1, Options.Parallel));
            var tasks = new Task[nL];
            for (int l = 0; l < nL; l++)
            {
                int idx = l;
                tasks[l] = Task.Run(async () =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        string variant = baseText + "\n// fit single " + lights[idx].Name + "\n"
                            + EntityText(lights[idx], lights[idx].Name, Options.WriteJkaFormat);
                        string nm = $"fit_s{idx:D3}";
                        var c = CompileMap(variant, nm, scratchRoot, out BspData? bsp);
                        logs[idx] = c.Log;
                        var raw = new List<(double R, double G, double B)>(pairs.Count);
                        var have = new bool[pairs.Count];
                        if (c.HasLightmaps && bsp != null)
                        {
                            var candFaces = BuildSampleFaces(bsp);
                            raw = SampleCandidate(bsp, candFaces, pairs, have);
                        }
                        // strip the baked ambient → pure per-light contribution.
                        // Points with no lightmap in the single compile get 0
                        // (the light contributed nothing there — subtracting
                        // the probe's baked ambient would inject a fake
                        // negative lobe of -65).
                        var vals = new List<(double R, double G, double B)>(raw.Count);
                        for (int q = 0; q < raw.Count; q++)
                            vals.Add(have[q]
                                ? (raw[q].R - ambientVals[q].R,
                                   raw[q].G - ambientVals[q].G,
                                   raw[q].B - ambientVals[q].B)
                                : (R: 0.0, G: 0.0, B: 0.0));
                        fields[idx] = vals;
                        Interlocked.Increment(ref res.CompilesRun);
                        if (Environment.GetEnvironmentVariable("JKD_SAMPDBG") == "1")
                        {
                            var want = Environment.GetEnvironmentVariable("JKD_SAMPDBG_L") ?? "";
                            var wantSet = want.Length == 0
                                ? new System.Collections.Generic.HashSet<int> { idx }
                                : want.Split(',').Select(s => int.TryParse(s, out int v) ? v : -1).ToHashSet();
                            if (wantSet.Contains(idx))
                            {
                            var candFacesDbg = BuildSampleFaces(bsp!);
                            var spatialDbg = new CandidateIndex(candFacesDbg);
                            var rows = new List<object>();
                            for (int q = 0; q < pairs.Count; q++)
                            {
                                var p = pairs[q];
                                double dm = Math.Max(Math.Abs(raw[q].R - ambientVals[q].R),
                                    Math.Max(Math.Abs(raw[q].G - ambientVals[q].G), Math.Abs(raw[q].B - ambientVals[q].B)));
                                if (dm < 1) continue;
                                var f = spatialDbg.Find(p.X, p.Y, p.Z);
                                double plane = f == null ? -999 : f.Normal.X * p.X + f.Normal.Y * p.Y + f.Normal.Z * p.Z - f.PlaneDist;
                                rows.Add(new { q, x = p.X, y = p.Y, z = p.Z, pFace = p.PFace, pBlock = p.PBlock,
                                    pu = p.PU, pv = p.PV,
                                    fIdx = f?.FaceIndex ?? -1, fBlock = f?.Block ?? -1, plane,
                                    raw = new[] { raw[q].R, raw[q].G, raw[q].B },
                                    amb = new[] { ambientVals[q].R, ambientVals[q].G, ambientVals[q].B } });
                            }
                            File.WriteAllText(Path.Combine(scratchRoot, $"sampdbg_{idx:D3}.json"),
                                JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                            }
                        }
                    }
                    finally { gate.Release(); }
                });
            }
            Task.WaitAll(tasks);
            if (Environment.GetEnvironmentVariable("JKD_FIELDDBG") == "1")
            {
                var dbg = new List<object>();
                for (int l = 0; l < nL; l++)
                {
                    double mx = 0; int gt1 = 0, gt8 = 0, gt32 = 0, neg = 0;
                    double sumTatS = 0; int natS = 0; double sumSatT = 0; int satT = 0;
                    double sumProd = 0, sumS2 = 0, sumT2 = 0;
                    for (int q = 0; q < fields[l].Count; q++)
                    {
                        var f = fields[l][q];
                        double tR = pairs[q].OR - ambientVals[q].R;
                        double tG = pairs[q].OG - ambientVals[q].G;
                        double tB = pairs[q].OB - ambientVals[q].B;
                        double t = (tR + tG + tB) / 3;
                        double s = (f.R + f.G + f.B) / 3;
                        sumProd += s * t; sumS2 += s * s; sumT2 += t * t;
                        if (s > 8) { sumTatS += t; natS++; }
                        if (t > 16 && Math.Abs(s) > 1) { sumSatT += s; satT++; }
                        double m = Math.Max(Math.Abs(f.R), Math.Max(Math.Abs(f.G), Math.Abs(f.B)));
                        mx = Math.Max(mx, m);
                        if (m > 1) gt1++;
                        if (m > 8) gt8++;
                        if (m > 32) gt32++;
                        if (m > 1 && (f.R < -1 || f.G < -1 || f.B < -1)) neg++;
                    }
                    double corr = Math.Sqrt(sumS2 * sumT2) > 1e-9 ? sumProd / Math.Sqrt(sumS2 * sumT2) : 0;
                    dbg.Add(new { name = lights[l].Name, seedPos = new[] { lights[l].SeedPosition.X, lights[l].SeedPosition.Y, lights[l].SeedPosition.Z },
                        max = mx, gt1, gt8, gt32, neg, corr,
                        meanTwhereSgt8 = natS > 0 ? sumTatS / natS : 0,
                        meanSwhereTgt16 = satT > 0 ? sumSatT / satT : 0, satT,
                        logErr = logs[l].Contains("ERROR") });
                }
                double ambMax = 0; long ambSum = 0; int ambNz = 0;
                foreach (var a in ambientVals)
                {
                    double m = Math.Max(a.R, Math.Max(a.G, a.B));
                    ambMax = Math.Max(ambMax, m);
                    ambSum += (long)a.R + (long)a.G + (long)a.B;
                    if (m > 1) ambNz++;
                }
                File.WriteAllText(Path.Combine(scratchRoot, "field_debug.json"),
                    JsonSerializer.Serialize(new { ambMax, ambAvg = (double)ambSum / (ambientVals.Count * 3), ambNz, n = ambientVals.Count, lights = dbg },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            for (int l = 0; l < nL; l++)
            {
                if (logs[l].Contains("ERROR"))
                {
                    lights[l].Kept = false;
                    lights[l].DropReason = "single compile failed: " + Tail(logs[l], 200);
                }
                singleFields.Add(fields[l]);
            }

            // 5. fit in the rank-1 light basis (each light = scalar scale ×
            // falloff × color; the joint system is nL×nL, not 3nL×3nL — the
            // per-channel columns are exactly rank-1 per light).

            // 5a. sky correction (frozen): the recompiled sky/ambient does not
            // match the original's. The probe sky is uniformly 65 where sky is
            // visible and <= 26 elsewhere; the original adds a ~+15 step ONLY
            // where sky is visible (a linear gain on the probe sky value
            // undercorrects: gain≈-0.02, offset≈5.7), so fit [sky-presence
            // indicator, constant] and freeze the result before the light fit
            // — fitting these jointly with the light pool trades the constant
            // against overlapping pools and drives light scales negative.
            // Not emitted to fit.map.
            var skyField = new List<(double R, double G, double B)>(pairs.Count);
            var constField = new List<(double R, double G, double B)>(pairs.Count);
            for (int id = 0; id < pairs.Count; id++)
            {
                var a = ambientVals[id];
                double s = Math.Max(a.R, Math.Max(a.G, a.B));
                double c = s >= 40.0 ? 1.0 : 0.0;   // sky visible
                skyField.Add((c, c, c));
                constField.Add((1.0, 1.0, 1.0));
            }
            var corrBasis = new List<LightBasis> { BuildBasis(skyField), BuildBasis(constField) };
            // Mask the POOL points out of the sky/constant fit: on the dense
            // set the constant term absorbs the MEAN POOL ENERGY (fitrun14:
            // offset 34.19/ch — pools are ~25% of texels at +150), because the
            // LSQ over all points trades it against the pools. The correction
            // is then frozen into ambientExt while the stage-A recompile never
            // adds it back, so every captured pool is ~34 short and the gate
            // error explodes (190 vs 104). The lights own the pool regions;
            // [sky, constant] must only see non-pool points.
            var poolW = new List<(int Id, double W)>();
            for (int id = 0; id < pairs.Count; id++)
            {
                bool inPool = false;
                for (int l = 0; l < singleFields.Count && !inPool; l++)
                {
                    var f = singleFields[l][id];
                    inPool = Math.Max(Math.Abs(f.R), Math.Max(Math.Abs(f.G), Math.Abs(f.B))) > 8.0;
                }
                if (inPool)
                {
                    poolW.Add((id, pairs[id].Weight));
                    pairs[id] = pairs[id] with { Weight = 0 };
                }
            }
            var (Gc, hc) = BuildGh1D(pairs, ambientVals, corrBasis, new List<int> { 0, 1 });
            double[] xc = SolveLeastSquares(Gc, 2, hc, 1e-9 * Math.Max(1.0, (Gc[0, 0] + Gc[1, 1]) / 2));
            foreach (var (id, w) in poolW) pairs[id] = pairs[id] with { Weight = w };
            res.SkyGain = xc[0];
            res.SkyOffset = xc[1];
            Console.Error.WriteLine($"[sky] step correction: gain={xc[0]:F4} offset={xc[1]:F3}/ch");
            // per-point frozen correction = probe sky + gain·sky + offset
            var ambientExt = new List<(double R, double G, double B)>(pairs.Count);
            for (int id = 0; id < pairs.Count; id++)
            {
                var a = ambientVals[id];
                double cr = a.R, cg = a.G, cb = a.B;
                for (int m = 0; m < 2; m++)
                {
                    double s = xc[m] * corrBasis[m].G[id];
                    cr += s * corrBasis[m].VR; cg += s * corrBasis[m].VG; cb += s * corrBasis[m].VB;
                }
                ambientExt.Add((cr, cg, cb));
            }

            // 5b. greedy forward selection (robust to overlapping pools), then
            // a joint LSQ polish on the selected set. Lights only — the sky
            // correction is a fixed per-point offset now.
            var basis = new List<LightBasis>();
            for (int l = 0; l < nL; l++) basis.Add(BuildBasis(singleFields[l]));
            int nB = basis.Count;
            var (greedyOrder, gscales) = FitGreedy1D(pairs, ambientExt, basis, nB);
            if (greedyOrder.Count >= 2)
            {
                // Non-negative least squares (Lawson–Hanson) on the selected set.
                // The joint normal equations for overlapping monochrome pools are
                // ill-conditioned (naive Gaussian elimination collapsed every scale
                // to ~0 on academy1; unclamped BCD oscillates: 42.86→41.47→42.17
                // vs greedy 41.20). NNLS solves the box-constrained problem exactly
                // and is guaranteed ≤ the error of any feasible point — including
                // the ambient-only zero vector and the clamped greedy solution.
                var (Gp, hp) = BuildGh1D(pairs, ambientExt, basis, greedyOrder);
                double trp = 0;
                int np = Gp.GetLength(0);
                for (int i = 0; i < np; i++) trp += Gp[i, i];
                double ridge = 1e-3 * Math.Max(1.0, trp / np);
                double rmsGreedy = RmsOf1D(pairs, basis, gscales, ambientExt);
                var xs = NnlsSolve(Gp, hp, ridge);
                for (int k = 0; k < greedyOrder.Count; k++)
                    gscales[greedyOrder[k]] = xs[k];
                double rmsPolished = RmsOf1D(pairs, basis, gscales, ambientExt);
                Console.Error.WriteLine($"[polish] NNLS greedyRms={rmsGreedy:F4} after={rmsPolished:F4} (ridge {ridge:F4}) scales=" +
                    string.Join(" ", greedyOrder.ConvertAll(a => $"{a}:{gscales[a]:F3}")));
            }
            double rmsFit = RmsOf1D(pairs, basis, gscales, ambientExt);
            var scales = new double[nL][];
            for (int l = 0; l < nL; l++)
                scales[l] = new[] { gscales[l] * basis[l].VR, gscales[l] * basis[l].VG, gscales[l] * basis[l].VB };
            for (int l = 0; l < nL; l++)
            {
                lights[l].Scale = scales[l];
            }
            // informational mean sky (per-point sky offset lives in ambientExt)
            double ar = 0, ag = 0, ab = 0;
            foreach (var a in ambientExt) { ar += a.R; ag += a.G; ab += a.B; }
            int ac = Math.Max(1, ambientExt.Count);
            double[] c0 = { ar / ac, ag / ac, ab / ac };
            double fittedEnergy = 0, totalEnergy = 0;
            for (int l = 0; l < nL; l++)
            {
                double e = 0;
                for (int id = 0; id < pairs.Count; id++)
                {
                    var f = singleFields[l][id];
                    e += pairs[id].Weight * (f.R * scales[l][0] + f.G * scales[l][1] + f.B * scales[l][2]);
                }
                fittedEnergy += e;
                totalEnergy += e;
            }
            double skyEnergy = 0;
            for (int id = 0; id < pairs.Count; id++)
            {
                var a = ambientVals[id];
                skyEnergy += pairs[id].Weight * (a.R + a.G + a.B);
            }
            double denom = totalEnergy + skyEnergy + 1e-9;
            for (int l = 0; l < nL; l++)
            {
                double e = 0;
                for (int id = 0; id < pairs.Count; id++)
                {
                    var f = singleFields[l][id];
                    e += pairs[id].Weight * (f.R * scales[l][0] + f.G * scales[l][1] + f.B * scales[l][2]);
                }
                lights[l].ContributionPct = 100.0 * e / denom;
            }

            // 6. pruning: marginal ΔE from re-solving without the light
            // (sky correction is already baked into the ambientExt offset)
            for (int l = 0; l < nL; l++)
            {
                if (!lights[l].Kept) continue;
                var activeIdx = new List<int>();
                for (int m = 0; m < nL; m++) if (m != l && gscales[m] != 0) activeIdx.Add(m);
                var (G2, h2) = BuildGh1D(pairs, ambientExt, basis, activeIdx);
                int n2 = G2.GetLength(0);
                double tr2 = 0;
                for (int i = 0; i < n2; i++) tr2 += G2[i, i];
                var x2 = SolveLeastSquares(G2, n2, h2, Math.Max(1e-4, (n2 > 0 ? tr2 / n2 : 0) * 1e-6));
                var g2 = new double[nB];
                for (int k = 0; k < n2; k++) g2[activeIdx[k]] = Math.Max(0, x2[k]);
                double rmsWithout = RmsOf1D(pairs, basis, g2, ambientExt);
                lights[l].DeltaE = rmsWithout - rmsFit;   // > 0 = the light helps
            }
            for (int l = 0; l < nL; l++)
            {
                if (!lights[l].Kept) continue;
                if (lights[l].DeltaE < 0 && lights[l].ContributionPct < Options.MinContributionPct)
                {
                    lights[l].Kept = false;
                    lights[l].DropReason = $"no benefit: ΔE={lights[l].DeltaE:F2}, contribution {lights[l].ContributionPct:F2}%";
                }
                else if (lights[l].ContributionPct < Options.MinContributionPct && lights[l].DeltaE < Options.MinDeltaEPct)
                {
                    lights[l].Kept = false;
                    lights[l].DropReason = $"negligible: ΔE={lights[l].DeltaE:F2} (<{Options.MinDeltaEPct}), contribution {lights[l].ContributionPct:F2}%";
                }
            }

            // 6b. if every candidate light was dropped, the survivor set is
            // empty and the stage-A compile below would be a zero-light
            // compile: its error differs from the probe's by compile jitter
            // alone (measured 0.87 on academy1 — fitrun17 "passed" 102.42 vs
            // 103.29 with kept=0 that way). Treat an empty survivor set as
            // the same outcome as a gate fail: honest report, honest exit.
            if (!lights.Any(l => l.Kept))
            {
                res.Note = "all candidate lights dropped by the per-light gate (none individually improves the fit) — emitting empty light set";
                res.ErrorStageA = res.ErrorAmbientOnly;
                res.ErrorFinal = res.ErrorAmbientOnly;
                WriteReport(outDir, baseMapPath, mapLabel, lights, origBsp, c0, res, pairs, singleFields, probeBsp, 0);
                WriteFitMap(baseText, lights, outDir, mapLabel, Options.WriteJkaFormat);
                return res;
            }

            // 7. radius hill-climb FIRST: under the JKA quadratic falloff the
            // radius sets the pool edge, so a wrong seed radius mis-shapes the
            // fields before the stage-A gate could ever see the real fit.
            var survivorsA = lights.Where(l => l.Kept).ToList();
            if (Options.RadiusFit && survivorsA.Count > 0)
            {
                var sel = greedyOrder.Where(l => lights[l].Kept && gscales[l] > 0).ToList();
                RefineRadii(pairs, singleFields, basis, lights, sel, ambientExt, gscales, baseText, scratchRoot, res);
                for (int l = 0; l < nL; l++)
                    scales[l] = new[] { gscales[l] * basis[l].VR, gscales[l] * basis[l].VG, gscales[l] * basis[l].VB };
            }

            // 7b. stage A compile: all surviving lights, seed positions, fitted scales
            string stageAMap = baseText + "\n// fit stage A\n" +
                string.Concat(survivorsA.Select(l => EntityText(l, l.Name, Options.WriteJkaFormat)));
            var stageA = CompileMap(stageAMap, "fit_stageA", scratchRoot, out BspData? stageABsp);
            res.CompilesRun++;
            if (stageABsp != null && stageABsp.Lightmaps.Count > 0)
            {
                // Points the recompile can't sample (sky box, offsets beyond
                // the loose tolerance) get the linear model value so the gate
                // error is not inflated by zeros.
                var vals = SampleCandidate(stageABsp, BuildSampleFaces(stageABsp), pairs,
                    modelFill: i =>
                    {
                        double r = ambientVals[i].R, g = ambientVals[i].G, b = ambientVals[i].B;
                        for (int l = 0; l < gscales.Length; l++)
                        {
                            if (gscales[l] == 0) continue;
                            var f = singleFields[l][i];
                            r += gscales[l] * f.R; g += gscales[l] * f.G; b += gscales[l] * f.B;
                        }
                        return (r, g, b);
                    });
                res.ErrorStageA = L1Of(pairs, vals);
            }
            else
            {
                res.Note = "stage A compile produced no lightmaps: " + Tail(stageA.Log, 400);
                // No candidate field to diff; show original vs ambient-only (probe) —
                // still the pool-residual picture, and keeps the artifact list honest.
                WriteReport(outDir, baseMapPath, mapLabel, lights, origBsp, c0, res, pairs, singleFields, probeBsp, 0);
                return res;
            }
            if (res.ErrorStageA > res.ErrorAmbientOnly - Options.GateMarginL1)
            {
                res.Note = $"fitted lights do not beat ambient-only by the {Options.GateMarginL1:F1} L1 margin (E_fit={res.ErrorStageA:F2} vs E_ambient={res.ErrorAmbientOnly:F2}) — emitting empty light set";
                foreach (var l in lights) if (l.Kept) { l.Kept = false; l.DropReason = "did not beat ambient-only"; }
                // The emitted final set is empty: the final map IS the ambient-only
                // map, so its error is the ambient baseline. (Leaving ErrorFinal=0
                // made the CLI exit code 0 on a gate FAIL.)
                res.ErrorFinal = res.ErrorAmbientOnly;
                // Diff the original against the stage-A candidate field: this is the
                // residual map the next fit iteration works from (pooled = geometry,
                // broad soft = bounce). Previously null → lightmap_diff.png silently
                // missing on every gate-fail run (fitrun9).
                WriteReport(outDir, baseMapPath, mapLabel, lights, origBsp, c0, res, pairs, singleFields, stageABsp, 0);
                WriteFitMap(baseText, lights, outDir, mapLabel, Options.WriteJkaFormat);
                return res;
            }

            // 8. analytic position refinement (no compiles), for kept lights.
            // ambientExt already carries the frozen sky correction.
            if (Options.PosFit && survivorsA.Count > 0)
            {
                RefinePositions(pairs, singleFields, lights, ambientExt, scales, res);
            }

            // 9. final compile with kept lights at refined positions
            var kept = lights.Where(l => l.Kept).ToList();
            string finalMap = baseText + "\n// fit final\n" +
                string.Concat(kept.Select(l => EntityText(l, l.Name, Options.WriteJkaFormat)));
            var fin = CompileMap(finalMap, "fit_final", scratchRoot, out BspData? finBsp);
            res.CompilesRun++;
            if (finBsp != null && finBsp.Lightmaps.Count > 0)
            {
                // Same model fill as stage A. After position refinement the
                // single fields are at the seed positions (a few units stale)
                // — the fill covers only the handful of points the recompile
                // cannot sample, so the staleness is immaterial.
                var vals = SampleCandidate(finBsp, BuildSampleFaces(finBsp), pairs,
                    modelFill: i =>
                    {
                        double r = ambientVals[i].R, g = ambientVals[i].G, b = ambientVals[i].B;
                        for (int l = 0; l < gscales.Length; l++)
                        {
                            if (gscales[l] == 0) continue;
                            var f = singleFields[l][i];
                            r += gscales[l] * f.R; g += gscales[l] * f.G; b += gscales[l] * f.B;
                        }
                        return (r, g, b);
                    });
                res.ErrorFinal = L1Of(pairs, vals);
                if (res.ErrorFinal > res.ErrorStageA)
                {
                    // roll positions back
                    foreach (var l in kept)
                    {
                        l.Position = l.SeedPosition;
                        l.PositionMoved = false;
                    }
                    res.PositionsRolledBack = true;
                    res.ErrorFinal = res.ErrorStageA;
                    finBsp = stageABsp;
                }
            }
            else
            {
                res.Note = "final compile produced no lightmaps: " + Tail(fin.Log, 400);
                finBsp = stageABsp;
                res.ErrorFinal = res.ErrorStageA;
            }

            res.ElapsedMs = sw.ElapsedMilliseconds;
            WriteReport(outDir, baseMapPath, mapLabel, lights, origBsp, c0, res, pairs, singleFields, finBsp, 0);
            WriteFitMap(baseText, lights, outDir, mapLabel, Options.WriteJkaFormat);
            return res;
        }

        double Stage1Scale(RecoveredLight s, int ch)
        {
            // stage-1 "level" = 255 * c (c = per-channel radiance scale); clamp to sane
            double c = (s.Level ?? (s.LevelLowerBound > 0 ? s.LevelLowerBound : 255)) / 255.0;
            double ratio = s.Color[ch] / (Math.Max(s.Color.X, Math.Max(s.Color.Y, s.Color.Z)) + 1e-9);
            return Math.Clamp(c * (ch == 0 ? 1.0 : ratio), 0, 255);
        }

        /// <summary>
        /// Sample a candidate BSP's lightmap at the shared world points.
        /// Faces are located with the LOOSE plane match (the recompiled
        /// geometry is offset 3-24u from the original; the strict match
        /// missed ~40% of dense points and turned their values into 0,
        /// which on the stage-A recompile inflated the measured error to
        /// ~190 vs the 104 ambient baseline — fitrun14).
        /// When <paramref name="valid"/> is supplied it is filled with
        /// whether a lightmapped face actually existed at each point — the
        /// single-field construction masks those points to zero, because a
        /// point with no lightmap in the single compile means the light
        /// contributed nothing there (the raw 0 minus the probe's 65
        /// ambient would otherwise inject a fake negative lobe).
        /// When <paramref name="modelFill"/> is supplied it is called for
        /// points where even the loose match fails and its value is used —
        /// the linear model (ambient + fitted lights) at that point — so a
        /// stage-A/final error measurement is not inflated by the few
        /// surfaces the recompile cannot sample (sky box, far offsets).
        ///
        /// Each point is read through the CANDIDATE's own world→UV affine.
        /// (An earlier version reused the probe's stored UV whenever the
        /// candidate face's block index happened to equal the probe's — the
        /// assumption was "same light count ⇒ same lightmap layout". It is
        /// false: the probe's dummy and a single light touch different face
        /// sets, so the packings differ, and a coincidental block-index match
        /// read the wrong page. Measured symptom: 7 different single lights
        /// with byte-identical fields and ~40% of points negative.)
        /// </summary>
        internal List<(double R, double G, double B)> SampleCandidate(BspData cand, List<SampleFace> candFaces, List<SamplePair> pairs, bool[]? valid = null,
            Func<int, (double R, double G, double B)>? modelFill = null)
        {
            // Locate the candidate face spatially (face-array indices shift
            // when UV packing splits faces, so index matching is unsafe), then
            // read at the candidate's own UV for that world point.
            var spatial = new CandidateIndex(candFaces);
            var vals = new List<(double, double, double)>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                var f = spatial.FindLoose(p.X, p.Y, p.Z, p.NX, p.NY, p.NZ, CandidateIndex.LoosePlane);
                if (f == null)
                {
                    if (valid != null) valid[i] = false;
                    vals.Add(modelFill != null ? modelFill(i) : (0, 0, 0));
                    continue;
                }
                if (valid != null) valid[i] = true;
                var (u, v) = WorldToUv(f, new SamplePoint { X = p.X, Y = p.Y, Z = p.Z });
                var c = SampleFaceValue(cand.Lightmaps[f.Block].Data, f, u, v);
                vals.Add((c.R, c.G, c.B));
            }
            return vals;
        }

        static double L1Of(List<SamplePair> pairs, List<(double R, double G, double B)> vals)
        {
            double total = 0, wsum = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                total += p.Weight * (Math.Abs(p.OR - vals[i].R) + Math.Abs(p.OG - vals[i].G) + Math.Abs(p.OB - vals[i].B));
                wsum += p.Weight;
            }
            return wsum > 0 ? total / wsum : 0;
        }

        /// <summary>Re-polish the 1D scales on the selected set; returns model L1.</summary>
        double PolishedRms(List<SamplePair> pairs, List<LightBasis> basis, List<int> selected,
            List<(double R, double G, double B)> ambient, out double[] newScales)
        {
            int nB = basis.Count;
            var (Gp, hp) = BuildGh1D(pairs, ambient, basis, selected);
            int np = Gp.GetLength(0);
            double trp = 0;
            for (int i = 0; i < np; i++) trp += Gp[i, i];
            var xp = np > 0
                ? SolveLeastSquares(Gp, np, hp, Math.Max(1e-4, (trp / np) * 1e-6))
                : new double[0];
            var gs = new double[nB];
            for (int k = 0; k < selected.Count; k++)
                gs[selected[k]] = Math.Max(0, xp[k]);
            newScales = gs;
            return RmsOf1D(pairs, basis, gs, ambient);
        }

        /// <summary>
        /// Per-survivor radius hill-climb. Under the JKA quadratic falloff the
        /// radius IS the pool edge, so the lightmap-paraboloid radius estimate
        /// is only a seed. Changing r changes the field SHAPE, not just its
        /// gain: each candidate requires one single-light recompile, a basis
        /// rebuild, and a 1D re-polish of the selected set. Coordinates are
        /// ascended one light at a time (two shrinking rounds), keeping the
        /// best model-L1 per light.
        /// </summary>
        void RefineRadii(List<SamplePair> pairs, List<List<(double R, double G, double B)>> singleFields,
            List<LightBasis> basis, List<FitLight> lights, List<int> selected,
            List<(double R, double G, double B)> ambient, double[] gscales,
            string baseText, string scratchRoot, LightFitResult res)
        {
            int nL = lights.Count;
            var gate = new SemaphoreSlim(Math.Max(1, Options.Parallel));
            double[] rounds = { 0, 1 };
            foreach (int round in rounds)
            {
                double[] factors = round == 0 ? new[] { 0.7, 1.4 } : new[] { 0.85, 1.18 };
                for (int l = 0; l < nL; l++)
                {
                    if (!lights[l].Kept || gscales[l] <= 0) continue;
                    int r0 = lights[l].Radius;
                    int bestR = r0;
                    double bestRms = PolishedRms(pairs, basis, selected, ambient, out gscales);
                    List<(double R, double G, double B)>? bestField = null;
                    // candidate compiles (parallel), then evaluate
                    var cands = factors
                        .Select(m => (int)Math.Clamp(Math.Round(r0 * m), 64, 4096))
                        .Where(rc => rc != r0)
                        .Distinct()
                        .ToList();
                    var cFields = new List<(double R, double G, double B)>[cands.Count];
                    var tasks = new Task[cands.Count];
                    for (int i = 0; i < cands.Count; i++)
                    {
                        int idx = i;
                        tasks[i] = Task.Run(async () =>
                        {
                            await gate.WaitAsync();
                            try
                            {
                                lights[l].Radius = cands[idx];
                                string variant = baseText + "\n// fit rad " + lights[l].Name + "\n"
                                    + EntityText(lights[l], lights[l].Name, Options.WriteJkaFormat);
                                var c = CompileMap(variant, $"fit_r{l:D3}_{cands[idx]}", scratchRoot, out BspData? bsp);
                                Interlocked.Increment(ref res.CompilesRun);
                                var raw = c.HasLightmaps && bsp != null
                                    ? SampleCandidate(bsp, BuildSampleFaces(bsp), pairs)
                                    : Enumerable.Repeat((R: 0.0, G: 0.0, B: 0.0), pairs.Count).ToList();
                                var fld = new List<(double R, double G, double B)>(raw.Count);
                                for (int q = 0; q < raw.Count; q++)
                                    fld.Add((raw[q].R - ambient[q].R,
                                             raw[q].G - ambient[q].G,
                                             raw[q].B - ambient[q].B));
                                cFields[idx] = fld;
                            }
                            finally { gate.Release(); }
                        });
                    }
                    Task.WaitAll(tasks);
                    lights[l].Radius = r0; // restore; winner applied below
                    for (int i = 0; i < cands.Count; i++)
                    {
                        if (cFields[i] == null) continue;
                        basis[l] = BuildBasis(cFields[i]);
                        double rms = PolishedRms(pairs, basis, selected, ambient, out _);
                        if (rms < bestRms - 1e-6)
                        {
                            bestRms = rms;
                            bestR = cands[i];
                            bestField = cFields[i];
                        }
                    }
                    if (bestR != r0)
                    {
                        lights[l].Radius = bestR;
                        singleFields[l] = bestField!;
                        basis[l] = BuildBasis(bestField!);
                        bestRms = PolishedRms(pairs, basis, selected, ambient, out gscales);
                        if (Environment.GetEnvironmentVariable("JKD_FIELDDBG") == "1")
                            Console.Error.WriteLine($"[radfit] {lights[l].Name}: {r0} -> {bestR} (rms {bestRms:F3})");
                    }
                    else
                    {
                        basis[l] = BuildBasis(singleFields[l]); // restore current field
                    }
                }
            }
        }

        /// <summary>
        /// Per-kept-light coordinate-ascent position refinement using an analytic
        /// atten/d proxy of the single-compile field. No compiles; the final
        /// compile (with rollback) is the arbiter.
        /// </summary>
        void RefinePositions(List<SamplePair> pairs, List<List<(double R, double G, double B)>> singleFields,
            List<FitLight> lights, List<(double R, double G, double B)> ambient, double[][] scales, LightFitResult res)
        {
            int nL = lights.Count;
            int nP = pairs.Count;
            // precompute total[p] = sky + Σ_m s_m·S_m (model prediction)
            var total = new (double R, double G, double B)[nP];
            for (int id = 0; id < nP; id++)
            {
                var amb = ambient[id];
                double r = amb.R, g = amb.G, b = amb.B;
                for (int l = 0; l < nL; l++)
                {
                    var f = singleFields[l][id];
                    r += scales[l][0] * f.R; g += scales[l][1] * f.G; b += scales[l][2] * f.B;
                }
                total[id] = (r, g, b);
            }
            var order = lights.Where(l => l.Kept).OrderByDescending(l => l.ContributionPct).ToList();
            foreach (var light in order)
            {
                int li = lights.IndexOf(light);
                var sl = singleFields[li];
                var s = scales[li];
                var x0 = light.SeedPosition;
                var x = light.Position;
                double E(Vector3 pos)
                {
                    double err = 0;
                    for (int id = 0; id < nP; id++)
                    {
                        var p = pairs[id];
                        var f = sl[id];
                        if (Math.Abs(f.R) < 0.5 && Math.Abs(f.G) < 0.5 && Math.Abs(f.B) < 0.5)
                            continue;   // light doesn't reach here (post-ambient-subtraction noise floor)
                        double dx = p.X - pos.X, dy = p.Y - pos.Y, dz = p.Z - pos.Z;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        double d0 = Math.Sqrt(Math.Pow(p.X - x0.X, 2) + Math.Pow(p.Y - x0.Y, 2) + Math.Pow(p.Z - x0.Z, 2));
                        // q3map2 point lights decay inverse-square (d0/dist)² — match the real bake
                        double ar = (d0 * d0) / Math.Max(1e-9, dist * dist);
                        // angular factor ratio
                        double dotNew = (dx * p.NX + dy * p.NY + dz * p.NZ);
                        if (dist > 1e-6) dotNew /= dist;
                        double dNew = 0.25 + 0.75 * Math.Max(0, dotNew);
                        double dr = (p.X - x0.X) * p.NX + (p.Y - x0.Y) * p.NY + (p.Z - x0.Z);
                        double dotOld = d0 > 1e-6 ? dr / d0 : 0;
                        double dOld = 0.25 + 0.75 * Math.Max(0, dotOld);
                        double dr2 = dNew / Math.Max(0.25, dOld);
                        double rr = total[id].R - s[0] * f.R + s[0] * f.R * ar * dr2;
                        double gg = total[id].G - s[1] * f.G + s[1] * f.G * ar * dr2;
                        double bb = total[id].B - s[2] * f.B + s[2] * f.B * ar * dr2;
                        double er = p.OR - rr, eg = p.OG - gg, eb = p.OB - bb;
                        err += p.Weight * (er * er + eg * eg + eb * eb);
                    }
                    return err;
                }
                double cur = E(x);
                foreach (float step in new[] { Options.PosStep, Options.PosStep / 2f })
                {
                    foreach (int axis in new[] { 0, 1, 2 })
                    {
                        foreach (float dir in new[] { -step, step })
                        {
                            var cand = x;
                            if (axis == 0) cand = new Vector3(x.X + dir, x.Y, x.Z);
                            else if (axis == 1) cand = new Vector3(x.X, x.Y + dir, x.Z);
                            else cand = new Vector3(x.X, x.Y, x.Z + dir);
                            double e = E(cand);
                            if (e < cur * 0.999) { cur = e; x = cand; }
                        }
                    }
                }
                light.Position = x;
                light.PositionMoved = Vector3.Distance(x, x0) > 1e-3;
            }
        }

        static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

        void WriteEmptyOutputs(string outDir, string baseMapPath, string mapLabel, string note)
        {
            string baseText = StripLightEntities(File.ReadAllText(baseMapPath));
            WriteFitMap(baseText, new List<FitLight>(), outDir, mapLabel, Options.WriteJkaFormat);
            File.WriteAllText(Path.Combine(outDir, "fit_report.json"),
                JsonSerializer.Serialize(new { map = mapLabel, note, lights = Array.Empty<object>() },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        void WriteFitMap(string baseText, List<FitLight> lights, string outDir, string mapLabel, bool jkaFormat)
        {
            var kept = lights.Where(l => l.Kept).ToList();
            string text = baseText + $"\n// {mapLabel} — JKDecompiler M2.5 stage 2: {kept.Count} fitted light(s)\n" +
                string.Concat(kept.Select(l => EntityText(l, l.Name, jkaFormat)));
            File.WriteAllText(Path.Combine(outDir, "fit.map"), text);
        }

        void WriteReport(string outDir, string baseMapPath, string mapLabel, List<FitLight> lights, BspData origBsp,
            double[] c0, LightFitResult res, List<SamplePair> pairs, List<List<(double R, double G, double B)>> singleFields,
            BspData? finalBsp, int unused)
        {
            string baseText = StripLightEntities(File.ReadAllText(baseMapPath));
            WriteFitMap(baseText, lights, outDir, mapLabel, Options.WriteJkaFormat);
            var report = new Dictionary<string, object?>
            {
                ["map"] = mapLabel,
                ["stage"] = "M2.5 stage 2 (closed-loop fit vs baked lightmap blocks)",
                ["q3map2"] = Q3map2Bin,
                ["gameDir"] = GameDir,
                ["ambientOffset"] = new[] { c0[0], c0[1], c0[2] },
                ["skyCorrection"] = new { gain = res.SkyGain, offsetPerChannel = res.SkyOffset },
                ["points"] = new { matched = res.MatchedPoints, unmatched = res.UnmatchedPoints },
                ["error"] = new
                {
                    ambientOnly = res.ErrorAmbientOnly,
                    stageA = res.ErrorStageA,
                    final = res.ErrorFinal,
                    positionsRolledBack = res.PositionsRolledBack,
                    note = res.Note,
                },
                ["compiles"] = res.CompilesRun,
                ["elapsedMs"] = res.ElapsedMs,
                ["kept"] = lights.Where(l => l.Kept).Count(),
                ["dropped"] = lights.Where(l => !l.Kept).Count(),
                ["lights"] = lights.Select(l => new
                {
                    name = l.Name,
                    seed = l.SeedName,
                    seedPos = new[] { l.SeedPosition.X, l.SeedPosition.Y, l.SeedPosition.Z },
                    pos = new[] { l.Position.X, l.Position.Y, l.Position.Z },
                    intensity = l.Intensity,
                    color01 = new[] { l.Color01.R, l.Color01.G, l.Color01.B },
                    jka = new[] { l.JkaBytes.Level, l.JkaBytes.R, l.JkaBytes.G, l.JkaBytes.B },
                    radius = l.Radius,
                    style = l.Style,
                    contributionPct = l.ContributionPct,
                    deltaE = l.DeltaE,
                    kept = l.Kept,
                    dropReason = l.DropReason,
                    positionMoved = l.PositionMoved,
                }).ToList(),
            };
            File.WriteAllText(Path.Combine(outDir, "fit_report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            WriteOverlay(outDir, lights, origBsp, mapLabel);
            if (finalBsp != null)
            {
                WriteBlockDiff(outDir, origBsp, finalBsp, mapLabel);
                res.DiffWritten = true;
            }
        }

        /// <summary>Top-down (XY) overlay: kept lights as +, dropped as ×.</summary>
        void WriteOverlay(string outDir, List<FitLight> lights, BspData origBsp, string mapLabel)
        {
            var m0 = origBsp.Models[0];
            float minX = m0.Mins.X, minY = m0.Mins.Y;
            int w = (int)Math.Ceiling(m0.Maxs.X - m0.Mins.X);
            int h = (int)Math.Ceiling(m0.Maxs.Y - m0.Mins.Y);
            const int maxDim = 4096;
            float scale = Math.Min(1f, (float)maxDim / Math.Max(w, h));
            int W2 = Math.Max(1, (int)(w * scale));
            int H2 = Math.Max(1, (int)(h * scale));
            var img = new byte[W2 * H2 * 3];
            // subtle grid
            for (int y = 0; y < H2; y++)
            for (int x = 0; x < W2; x++)
            {
                int p = (y * W2 + x) * 3;
                img[p] = img[p + 1] = img[p + 2] = 16;
            }
            void Mark(Vector3 pos, byte[] col, bool plus)
            {
                int px = (int)((pos.X - minX) * scale);
                int py = (int)((pos.Y - minY) * scale);
                int r = plus ? 5 : 3;
                for (int oy = -r; oy <= r; oy++)
                for (int ox = -r; ox <= r; ox++)
                {
                    int xx = px + ox, yy = py + oy;
                    if (xx < 0 || xx >= W2 || yy < 0 || yy >= H2) continue;
                    bool draw = plus ? (Math.Abs(ox) <= 1 || Math.Abs(oy) <= 1) : (Math.Abs(ox) == Math.Abs(oy));
                    if (!draw) continue;
                    int p = (yy * W2 + xx) * 3;
                    img[p] = col[0]; img[p + 1] = col[1]; img[p + 2] = col[2];
                }
            }
            foreach (var l in lights)
                Mark(l.Position, l.Kept ? new byte[] { 80, 255, 120 } : new byte[] { 255, 90, 90 }, l.Kept);
            PngWriter.SaveRgb(img, W2, H2, Path.Combine(outDir, "fit_lights_overlay.png"));
        }

        /// <summary>
        /// Side-by-side block diff: match orig blocks to final blocks by face
        /// world-bbox center, render orig | final | |diff| for each pair.
        /// </summary>
        void WriteBlockDiff(string outDir, BspData orig, BspData fin, string mapLabel)
        {
            (List<double> Mins, List<double> Maxs, double[] Center) BlockBbox(BspData d)
            {
                var mins = new double[d.Lightmaps.Count * 3];
                var maxs = new double[d.Lightmaps.Count * 3];
                var used = new bool[d.Lightmaps.Count];
                for (int i = 0; i < d.Faces.Count; i++)
                {
                    int b = d.Faces[i].LightmapNum[0];
                    if (b < 0 || b >= d.Lightmaps.Count) continue;
                    used[b] = true;
                    for (int v = d.Faces[i].FirstVertexIndex; v < d.Faces[i].FirstVertexIndex + d.Faces[i].NumVertices; v++)
                    {
                        var p = d.Vertices[v].Position;
                        mins[b * 3] = Math.Min(mins[b * 3], p.X);
                        maxs[b * 3] = Math.Max(maxs[b * 3], p.X);
                        mins[b * 3 + 1] = Math.Min(mins[b * 3 + 1], p.Y);
                        maxs[b * 3 + 1] = Math.Max(maxs[b * 3 + 1], p.Y);
                        mins[b * 3 + 2] = Math.Min(mins[b * 3 + 2], p.Z);
                        maxs[b * 3 + 2] = Math.Max(maxs[b * 3 + 2], p.Z);
                    }
                }
                var center = new double[d.Lightmaps.Count * 2];
                for (int b = 0; b < d.Lightmaps.Count; b++)
                {
                    if (!used[b]) { continue; }
                    center[b * 2] = (mins[b * 3] + maxs[b * 3]) / 2;
                    center[b * 2 + 1] = (mins[b * 3 + 1] + maxs[b * 3 + 1]) / 2;
                }
                return ((List<double>)mins.ToList(), (List<double>)maxs.ToList(), center);
            }
            var ob = BlockBbox(orig);
            var fb = BlockBbox(fin);
            var matches = new List<(int O, int F)>();
            var usedF = new bool[fin.Lightmaps.Count];
            for (int o = 0; o < orig.Lightmaps.Count; o++)
            {
                int best = -1; double bd = double.MaxValue;
                for (int f = 0; f < fin.Lightmaps.Count; f++)
                {
                    if (usedF[f]) continue;
                    double dx = ob.Center[o * 2] - fb.Center[f * 2];
                    double dy = ob.Center[o * 2 + 1] - fb.Center[f * 2 + 1];
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bd) { bd = d; best = f; }
                }
                if (best >= 0 && bd < 128) { matches.Add((o, best)); usedF[best] = true; }
            }
            // render
            int perRow = 8;
            int cell = 128, gap = 4, pairW = 3 * cell + 2 * gap;
            int rows = (matches.Count + perRow - 1) / perRow;
            int imgW = pairW * perRow + gap * (perRow - 1);
            int imgH = rows == 0 ? 1 : cell * rows + gap * (rows - 1);
            var img = new byte[imgW * imgH * 3];
            for (int k = 0; k < matches.Count; k++)
            {
                var (o, f) = matches[k];
                int col = k % perRow, row = k / perRow;
                int ox = col * (pairW + gap), oy = row * (cell + gap);
                byte[] src = orig.Lightmaps[o].Data, dst = fin.Lightmaps[f].Data;
                for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    int si = (y * 128 + x) * 3;
                    int px0 = ox + x;
                    int py0 = oy + y;
                    int P(int xoff, int yoff) => (yoff * imgW + xoff) * 3;
                    int pOrig = P(px0, py0);
                    int pFin = P(px0 + cell + gap, py0);
                    int pDiff = P(px0 + 2 * (cell + gap), py0);
                    img[pOrig] = src[si]; img[pOrig + 1] = src[si + 1]; img[pOrig + 2] = src[si + 2];
                    img[pFin] = dst[si]; img[pFin + 1] = dst[si + 1]; img[pFin + 2] = dst[si + 2];
                    int dr = Math.Abs(src[si] - dst[si]), dg = Math.Abs(src[si + 1] - dst[si + 1]), db = Math.Abs(src[si + 2] - dst[si + 2]);
                    img[pDiff] = (byte)Math.Min(255, dr * 3); img[pDiff + 1] = (byte)Math.Min(255, dg * 3); img[pDiff + 2] = (byte)Math.Min(255, db * 3);
                }
            }
            PngWriter.SaveRgb(img, imgW, imgH, Path.Combine(outDir, "lightmap_diff.png"));
        }

        internal static string EntityText(FitLight l, string name, bool jkaFormat)
        {
            // Plain point light with its own origin. (A target_light pair makes
            // q3map2 a spotlight whose origin key it takes from the light
            // entity — without one it bakes at (0,0,0), far off the map.)
            // Falloff here is q3map2's inverse-square; the closed-loop fit
            // solves scales against whatever the compiler actually bakes, so
            // the output map stays self-consistent.
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"classname\" \"light\"");
            sb.AppendLine($"  \"origin\" \"{l.Position.X:0.00} {l.Position.Y:0.00} {l.Position.Z:0.00}\"");
            sb.AppendLine($"  \"radius\" \"{l.Radius}\"");
            if (jkaFormat)
            {
                var (lv, r, g, b) = l.JkaBytes;
                sb.AppendLine($"  \"light\" \"{lv} {r} {g} {b}\"");
            }
            else
            {
                var (cr, cg, cb) = l.Color01;
                // 0.### — NOT 0.#: the probe dummy uses level 0.01, which the
                // 1-decimal format truncates to "0" (a dead light that no
                // longer forces lightmap assignment: 6 blocks vs 10 on
                // academy1, and the probe-driven sample set collapses onto
                // the probe's few lit regions).
                sb.AppendLine($"  \"light\" \"{l.Intensity:0.###}\"");
                sb.AppendLine($"  \"_color\" \"{cr:0.000} {cg:0.000} {cb:0.000}\"");
            }
            sb.AppendLine($"  \"lightstyle\" \"{l.Style}\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Map text with pre-existing light entities (light/lightJunior/target_light/light_mover) removed.</summary>
        internal static string StripLightEntities(string map)
        {
            var ranges = new List<(int, int)>();
            int i = 0, n = map.Length;
            int depth = 0, entStart = -1;
            string? classname = null;
            while (i < n)
            {
                char c = map[i];
                if (c == '{')
                {
                    if (depth == 0) { entStart = i; classname = null; }
                    depth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    depth--;
                    if (depth == 0 && classname != null)
                    {
                        if (classname is "light" or "lightJunior" or "target_light" or "light_mover")
                            ranges.Add((entStart, i + 1));
                        classname = null;
                    }
                    i++;
                    continue;
                }
                if (depth > 0 && c == '"')
                {
                    int kEnd = map.IndexOf('"', i + 1);
                    if (kEnd > 0 && map[(i + 1)..kEnd] == "classname")
                    {
                        int vStart = kEnd + 1;
                        while (vStart < n && char.IsWhiteSpace(map[vStart])) vStart++;
                        if (vStart < n && map[vStart] == '"')
                        {
                            int vEnd = map.IndexOf('"', vStart + 1);
                            if (vEnd > 0) classname = map[(vStart + 1)..vEnd];
                        }
                    }
                }
                i++;
            }
            var sb = new StringBuilder();
            int prev = 0;
            foreach (var (s, e) in ranges)
            {
                sb.Append(map, prev, s - prev);
                prev = e;
            }
            sb.Append(map, prev, n - prev);
            return sb.ToString();
        }
    }
}
