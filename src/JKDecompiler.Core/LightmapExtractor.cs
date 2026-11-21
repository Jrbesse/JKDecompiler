using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JKDecompiler.Core
{
    public class LightmapExtractResult
    {
        public int LightmapBlocks { get; set; }
        public int FacesWithLightmap { get; set; }
        public int LightGridCells { get; set; }
        public List<string> Files { get; } = new();
    }

    /// <summary>
    /// Extracts the baked lighting from a JKA RBSP — the part no other tool
    /// does. Compiled JKA BSPs contain ZERO light entities; this is the only
    /// lighting data left, so it's the whole product.
    ///
    /// JKA lightmap model (verified against OpenJK rd-vanilla + GOG maps):
    ///   - faces have NO assigned lightmap rect: lightmapX/Y/Width/Height are a
    ///     (0,0,1,1) placeholder and the surface's lightmapOrigin/vecs[0..1]
    ///     are zero (only vecs[2]=normal is used, tr_bsp.cpp:435)
    ///   - the real placement is the per-vertex lightmap[0] UVs: 0..1
    ///     block-relative, fed straight to GL (tr_surface.cpp:1446), and an
    ///     AFFINE MAP OF WORLD POSITION — all faces sharing a block share the
    ///     same world→UV coefficients (the block is a world-space lighting
    ///     canvas, not a packed atlas)
    ///   - a face's lightmap footprint = convex hull of its vertex UVs
    ///
    /// Outputs:
    ///   lightmap_<block>.png     raw 128x128 blocks as stored (v-axis flipped
    ///                            to image convention: stored row 0 = v=0)
    ///   overlays/lightmap_<b>_overlay.png   block + every face's footprint
    ///                            hull outlined in distinct colors
    ///   lightmap_info.json       face index -> shader, type, block, styles,
    ///                            per-vertex UVs (the M5 freeze ground truth)
    ///   lightgrid.bin            raw 30 B/cell palette (lump 15)
    ///   lightarray.bin           raw u16/cell (lump 17)
    ///   lightgrid.json           bounds + dims + palette metadata
    /// </summary>
    public class LightmapExtractor
    {
        public LightmapExtractResult Extract(BspData data, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var result = new LightmapExtractResult();

            // 1. raw blocks
            for (int b = 0; b < data.Lightmaps.Count; b++)
            {
                WriteBlockPng(data.Lightmaps[b].Data, Path.Combine(outDir, $"lightmap_{b}.png"));
                result.Files.Add($"lightmap_{b}.png");
            }
            result.LightmapBlocks = data.Lightmaps.Count;

            // 2. per-face footprints + sidecar
            var sidecar = new List<Dictionary<string, object?>>();
            var footprints = new List<(int Block, List<(float U, float V)> Hull, int Face)>();
            for (int i = 0; i < data.Faces.Count; i++)
            {
                var f = data.Faces[i];
                if (f.LightmapNum[0] < 0 || f.LightmapNum[0] >= data.Lightmaps.Count)
                    continue;

                // exact per-vertex lightmap UVs — the ground truth the M5
                // surface freeze copies
                var uvPts = new List<(float U, float V)>();
                for (int v = f.FirstVertexIndex; v < f.FirstVertexIndex + f.NumVertices && v < data.Vertices.Count; v++)
                    uvPts.Add((data.Vertices[v].GetLightmapU(0), data.Vertices[v].GetLightmapV(0)));

                var hull = ConvexHull(uvPts);
                footprints.Add((f.LightmapNum[0], hull, i));
                result.FacesWithLightmap++;

                string shader = f.ShaderIndex >= 0 && f.ShaderIndex < data.Shaders.Count
                    ? data.Shaders[f.ShaderIndex].Name : $"<shader {f.ShaderIndex}>";
                sidecar.Add(new Dictionary<string, object?>
                {
                    ["face"] = i,
                    ["shader"] = shader,
                    ["type"] = f.Type.ToString(),
                    ["lightmapBlock"] = f.LightmapNum[0],
                    ["styles"] = f.LightmapStyles.ToList(),
                    ["vertexUVs"] = uvPts.Select(p => new[] { p.U, p.V }).ToList(),
                    ["footprintUVs"] = hull.Select(p => new[] { p.X, p.Y }).ToList()
                });
            }

            var info = new Dictionary<string, object?>
            {
                ["lightmapBlocks"] = data.Lightmaps.Count,
                ["blockSize"] = 128,
                ["uvSpace"] = "0..1 block-relative (GL texcoord, no scaling), affine in world position; per-vertex UVs are the sole placement data",
                ["orientation"] = "PNG row 0 = stored row 127 (v-axis flipped to image convention; stored row 0 is v=0)",
                ["faces"] = sidecar
            };
            File.WriteAllText(Path.Combine(outDir, "lightmap_info.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = false }));
            result.Files.Add("lightmap_info.json");

            // 2b. per-block overlays: face footprint hulls over the block
            WriteBlockOverlays(data, outDir, result, footprints);

            // 3. lightgrid raw + metadata
            WriteRawCells(data, outDir, result);
            return result;
        }

        private static void WriteRawCells(BspData data, string outDir, LightmapExtractResult result)
        {
            using (var fs = File.Create(Path.Combine(outDir, "lightgrid.bin")))
            {
                var bw = new BinaryWriter(fs);
                foreach (var c in data.LightGrid)
                {
                    bw.Write(c.Ambient); bw.Write(c.Directed); bw.Write(c.Styles); bw.Write(c.LatLong);
                }
            }
            result.Files.Add("lightgrid.bin");
            result.LightGridCells = data.LightGrid.Count;

            using (var fs = File.Create(Path.Combine(outDir, "lightarray.bin")))
            {
                var bw = new BinaryWriter(fs);
                foreach (var v in data.LightArray.Data) bw.Write(v);
            }
            result.Files.Add("lightarray.bin");

            var m0 = data.Models.Count > 0 ? data.Models[0] : null;
            var meta = new Dictionary<string, object?>
            {
                ["cellSizeBytes"] = JkaBsp.SIZE_GRID_CELL,
                ["layout"] = "palette cell: ambient[4][3] rgb, directed[4][3] rgb, styles[4], latLong[2] (all bytes, little-endian)",
                ["paletteCellCount"] = data.LightGrid.Count,
                ["lightArray"] = "one u16 per world-grid cell; value is an INDEX into the palette (OpenJK tr_light.cpp: lightGridData + *gridPos)",
                ["lightArrayCount"] = data.LightArray.Data.Length,
                ["worldBounds"] = m0 == null ? null : new
                {
                    min = new[] { m0.Mins.X, m0.Mins.Y, m0.Mins.Z },
                    max = new[] { m0.Maxs.X, m0.Maxs.Y, m0.Maxs.Z }
                }
            };
            if (m0 != null)
            {
                meta["gridDims"] = new
                {
                    w = GridDim(m0, 0),
                    h = GridDim(m0, 1),
                    v = GridDim(m0, 2),
                    cellSteps = new[] { 64f, 64f, 128f }
                };
            }
            File.WriteAllText(Path.Combine(outDir, "lightgrid.json"),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            result.Files.Add("lightgrid.json");
        }

        /// <summary>
        /// Per-block overlay PNGs: every face's lightmap footprint (convex
        /// hull of its vertex UVs) outlined in a distinct color over the raw
        /// block. Shows how faces project onto the shared world-space
        /// lighting canvas (the M5 match surface).
        /// </summary>
        private static void WriteBlockOverlays(BspData data, string outDir, LightmapExtractResult result,
            List<(int Block, List<(float U, float V)> Hull, int Face)> footprints)
        {
            if (data.Lightmaps.Count == 0) return;
            Directory.CreateDirectory(Path.Combine(outDir, "overlays"));

            for (int b = 0; b < data.Lightmaps.Count; b++)
            {
                // match WriteBlockPng's v-flip: uv v -> py = (1-v)*127
                byte[] img = (byte[])data.Lightmaps[b].Data.Clone();
                for (int row = 0; row < JkaBsp.LIGHTMAP_BLOCK; row++)
                {
                    int srcRow = JkaBsp.LIGHTMAP_BLOCK - 1 - row;
                    Array.Copy(data.Lightmaps[b].Data, srcRow * 128 * 3, img, row * 128 * 3, 128 * 3);
                }

                foreach (var (block, hull, face) in footprints.Where(fp => fp.Block == b))
                {
                    if (hull.Count < 3) continue;
                    byte[] col = HsvToRgb(face / (float)Math.Max(1, data.Faces.Count));
                    var pts = hull.Select(p => (p.U * 127f, (1f - p.V) * 127f)).ToList();
                    for (int k = 0; k < pts.Count; k++)
                        DrawThickLine(img, pts[k], pts[(k + 1) % pts.Count], col);
                }

                string file = $"overlays/lightmap_{b}_overlay.png";
                PngWriter.SaveRgb(img, JkaBsp.LIGHTMAP_BLOCK, JkaBsp.LIGHTMAP_BLOCK, Path.Combine(outDir, file));
                result.Files.Add(file);
            }
        }

        private static void DrawThickLine(byte[] img, (float X, float Y) a, (float X, float Y) bb, byte[] col)
        {
            float dx = bb.X - a.X, dy = bb.Y - a.Y;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps < 1) steps = 1;
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                int x = (int)Math.Round(a.X + dx * t);
                int y = (int)Math.Round(a.Y + dy * t);
                for (int oy = 0; oy < 2; oy++)
                    for (int ox = 0; ox < 2; ox++)
                    {
                        int xx = x + ox, yy = y + oy;
                        if (xx < 0 || xx >= 128 || yy < 0 || yy >= 128) continue;
                        int p = (yy * 128 + xx) * 3;
                        img[p] = col[0]; img[p + 1] = col[1]; img[p + 2] = col[2];
                    }
            }
        }

        /// <summary>Monotone-chain convex hull (UV space, 0..1).</summary>
        private static List<(float X, float Y)> ConvexHull(List<(float X, float Y)> pts)
        {
            var u = new List<(float X, float Y)>();
            foreach (var pt in pts.OrderBy(p => (p.X, p.Y)))
                if (u.Count == 0 || u[^1].X != pt.X || u[^1].Y != pt.Y)
                    u.Add(pt);
            if (u.Count <= 2) return u;

            var lower = new List<(float X, float Y)>();
            foreach (var pt in u)
            {
                while (lower.Count >= 2 && Cross(lower[^2], lower[^1], pt) <= 0) lower.RemoveAt(lower.Count - 1);
                lower.Add(pt);
            }
            var upper = new List<(float X, float Y)>();
            for (int i = u.Count - 1; i >= 0; i--)
            {
                var pt = u[i];
                while (upper.Count >= 2 && Cross(upper[^2], upper[^1], pt) <= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(pt);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static float Cross((float X, float Y) a, (float X, float Y) bb, (float X, float Y) c)
            => (bb.X - a.X) * (c.Y - a.Y) - (bb.Y - a.Y) * (c.X - a.X);

        private static byte[] HsvToRgb(float h)
        {
            const float s = 0.85f, v = 1f;
            h %= 1f;
            int hi = (int)(h * 6) % 6;
            float f = h * 6 - hi;
            float p = v * (1 - s);
            float q = v * (1 - f * s);
            float t = v * (1 - (1 - f) * s);
            (float r, float g, float b) = hi switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q)
            };
            return new byte[] { (byte)(r * 255), (byte)(g * 255), (byte)(b * 255) };
        }

        /// <summary>Grid dim along one axis (shared with LightRecovery).</summary>
        internal static int GridDim(BspModel m0, int axis)
        {
            // mirrors R_LoadLightGrid in OpenJK tr_bsp.cpp (steps 64/64/128)
            float[] size = { 64f, 64f, 128f };
            float mn = axis == 0 ? m0.Mins.X : axis == 1 ? m0.Mins.Y : m0.Mins.Z;
            float mx = axis == 0 ? m0.Maxs.X : axis == 1 ? m0.Maxs.Y : m0.Maxs.Z;
            float origin = size[axis] * (float)Math.Ceiling(mn / size[axis]);
            float maxs = size[axis] * (float)Math.Floor(mx / size[axis]);
            return (int)((maxs - origin) / size[axis]) + 1;
        }

        private static void WriteBlockPng(byte[] data, string path)
        {
            // stored row 0 = v=0 (world-down for most surfaces) -> PNG bottom
            var flipped = new byte[128 * 128 * 3];
            for (int row = 0; row < 128; row++)
            {
                int srcRow = 127 - row;
                Array.Copy(data, srcRow * 128 * 3, flipped, row * 128 * 3, 128 * 3);
            }
            PngWriter.SaveRgb(flipped, 128, 128, path);
        }
    }
}
