using System.Linq;
using System.Numerics;

namespace JKDecompiler.Core
{
/// <summary>
/// A tiny but structurally complete RBSP: one world model, one node, two
/// leafs, one 6-sided brush, one lit planar face, one 128x128 lightmap
/// block, a 9x9x9 lightgrid. Used by CI and tests (no copyrighted data).
/// </summary>
    public static class SyntheticMap
{
    private struct VLight
    {
        public Vector3 Pos;
        public int Level;
        public Vector3 ColorNorm;
        public int Style;
    }

    /// <summary>
    /// A larger world (3008x3008x384) whose lightgrid was computed the exact
    /// q3map2 TraceGrid way (light.cpp) from two known point lights — the
    /// M2.5 stage-1 CI fixture (no copyrighted data). Grid: 48x48x4 = 9216
    /// corners, steps 64/64/128, origin (0,0,0).
    ///
    ///   light A "recov_truth_a": pos (768, 2304, 300), level 4000,
    ///                            color (255,160,80),  style 0
    ///   light B "recov_truth_b": pos (1856, 1216, 280), level 3000,
    ///                            color (120,180,255), style 1
    ///
    /// The lights are 1539 units apart (radius 1024 each): most of each
    /// footprint is single-light territory (clean directions), with a
    /// crescent blended-direction lens between them that must NOT yield
    /// spurious accepted lights.
    ///
    /// Per corner (q3map2 light.cpp:1518ff): each light with dist >= 64
    /// (inhibit sphere = max step * 0.5) contributes
    ///   contrib = colorNorm * (level/255) * max(0, 1 - (dist/1024)^2)
    /// the primary direction is the contribution-weighted average, then per
    /// light d = 0.25 + 0.75*dot(lightDir, primaryDir) and
    ///   directed[j] += contrib * d; ambient[j] += contrib * 0.25*(1-d)
    /// with j the slot for the light's style; latLong encodes the primary
    /// direction (NormalToLatLong). Corners with no contribution carry
    /// styles[0] = 0xff (LS_LSNONE).
    /// </summary>
    public static BspData BuildLightRecovery()
    {
        var d = new BspData();
        d.RawEntities = "{\n\"classname\" \"worldspawn\"\n}\n";
        d.Entities = BspEntityParser.Parse(d.RawEntities);
        d.Shaders.Add(new BspShader { Name = "textures/common/brick", SurfaceFlags = 0, ContentFlags = 1 });
        d.Models.Add(new BspModel
        {
            Mins = new Vector3(0, 0, 0),
            Maxs = new Vector3(3008, 3008, 384),
            FirstFace = 0, NumFaces = 0,
            FirstBrush = 0, NumBrushes = 0
        });

        var lights = new[]
        {
            new VLight { Pos = new Vector3(768, 2304, 300), Level = 4000,
                ColorNorm = new Vector3(1f, 160f/255f, 80f/255f), Style = 0 },
            new VLight { Pos = new Vector3(1856, 1216, 280), Level = 3000,
                ColorNorm = new Vector3(120f/255f, 180f/255f, 1f), Style = 1 },
        };

        const int W = 48, H = 48, V = 4;
        const int Radius = 1024;
        const int Inhibit = 64;
        var palette = new List<byte[]>();
        var paletteIndex = new Dictionary<byte[], int>();
        var array = new List<ushort>();
        for (int iz = 0; iz < V; iz++)
        for (int iy = 0; iy < H; iy++)
        for (int ix = 0; ix < W; ix++)
        {
            var P = new Vector3(ix * 64f, iy * 64f, iz * 128f);
            var cell = new byte[30];
            for (int k = 0; k < 4; k++) cell[24 + k] = 0xff; // LS_LSNONE
            var sumDir = Vector3.Zero;
            var contribs = new List<(Vector3 Color, Vector3 Dir, int Style)>();
            for (int li = 0; li < lights.Length; li++)
            {
                var L = lights[li];
                var to = L.Pos - P;
                float dist = Vector3.Distance(P, L.Pos);
                if (dist < Inhibit || dist >= Radius) continue;
                double dr = dist / (double)Radius;
                float atten = (float)Math.Max(0.0, 1.0 - dr * dr);
                var c = L.ColorNorm * (L.Level / 255f) * atten;
                contribs.Add((c, to / dist, L.Style));
                sumDir += (to / dist) * c.Length();
            }
            if (contribs.Count > 0)
            {
                var primary = Vector3.Normalize(sumDir);
                // slots by per-cell light index (q3map2: slot 0 = first
                // contributing light at this corner, ...); styles[slot] is
                // that light's style. Empty slots are 0xff and never precede
                // used ones, so readers may stop at the first 0xff.
                for (int ci = 0; ci < contribs.Count; ci++)
                {
                    int j = ci;
                    float dot = Vector3.Dot(contribs[ci].Dir, primary);
                    float dirFactor = Math.Max(0f, 0.25f + 0.75f * dot);
                    var c = contribs[ci].Color;
                    cell[12 + j * 3] = (byte)Math.Clamp((int)Math.Round(c.X), 0, 255);
                    cell[12 + j * 3 + 1] = (byte)Math.Clamp((int)Math.Round(c.Y), 0, 255);
                    cell[12 + j * 3 + 2] = (byte)Math.Clamp((int)Math.Round(c.Z), 0, 255);
                    var amb = c * 0.25f * (1f - dirFactor);
                    cell[j * 3] = (byte)Math.Clamp((int)Math.Round(amb.X), 0, 255);
                    cell[j * 3 + 1] = (byte)Math.Clamp((int)Math.Round(amb.Y), 0, 255);
                    cell[j * 3 + 2] = (byte)Math.Clamp((int)Math.Round(amb.Z), 0, 255);
                    cell[24 + j] = (byte)contribs[ci].Style;
                }
                cell[28] = LightRecovery.NormalToLatLong(primary)[0];
                cell[29] = LightRecovery.NormalToLatLong(primary)[1];
            }
            if (!paletteIndex.TryGetValue(cell, out int idx))
            {
                idx = palette.Count;
                palette.Add(cell);
                paletteIndex[cell] = idx;
            }
            array.Add((ushort)idx);
        }
        foreach (var p in palette)
        {
            var c = new BspLightGridCell();
            Array.Copy(p, 0, c.Ambient, 0, 12);
            Array.Copy(p, 12, c.Directed, 0, 12);
            Array.Copy(p, 24, c.Styles, 0, 4);
            Array.Copy(p, 28, c.LatLong, 0, 2);
            d.LightGrid.Add(c);
        }
        d.LightArray.Data = array.ToArray();

        d.Visibility = new BspVisibility
        {
            NumClusters = 1,
            ClusterSize = 1,
            Data = new byte[] { 0b1 }
        };
        return d;
    }

    /// <summary>Ground-truth lights of BuildLightRecovery() for tests.</summary>
    public static (Vector3 Pos, int Level, Vector3 Color, int Style)[] LightRecoveryTruth() => new[]
    {
        (new Vector3(768, 2304, 300), 4000, new Vector3(255, 160, 80), 0),
        (new Vector3(1856, 1216, 280), 3000, new Vector3(120, 180, 255), 1),
    };

    public static BspData Build()
    {
        var d = new BspData();

        // entities: worldspawn + a player start + a value with a '}' in it
        d.RawEntities =
            "{\n\"classname\" \"worldspawn\"\n\"dmapversion\" \"93\"\n}\n" +
            "{\n\"classname\" \"info_player_start\"\n\"origin\" \"64 64 8\"\n}\n" +
            "{\n\"classname\" \"misc_model\"\n\"model\" \"models/props/megaphone2.md3\"\n\"usescript\" \"void think() { }\"\n}\n";
        d.Entities = BspEntityParser.Parse(d.RawEntities);

        d.Shaders.Add(new BspShader { Name = "textures/common/brick", SurfaceFlags = 0, ContentFlags = 1 });

        // 6 box planes
        float E = 128f;
        (Vector3 n, float dist)[] planes =
        {
            (new Vector3(1, 0, 0), 0f), (new Vector3(-1, 0, 0), E),
            (new Vector3(0, 1, 0), 0f), (new Vector3(0, -1, 0), E),
            (new Vector3(0, 0, 1), 0f), (new Vector3(0, 0, -1), E),
        };
        foreach (var (n, dist) in planes) d.Planes.Add(new BspPlane { Normal = n, Distance = dist });

        // one 6-sided brush (the world solid)
        d.BrushSides.AddRange(Enumerable.Range(0, 6).Select(p => new BspBrushSide { PlaneIndex = p, ShaderIndex = 0 }));
        d.Brushes.Add(new BspBrush { FirstSide = 0, NumSides = 6, ShaderIndex = 0 });

        // one node splitting on plane 0, two leaves
        d.Nodes.Add(new BspNode
        {
            PlaneIndex = 0,
            Children = new[] { -1, -2 },
            Mins = new[] { 0, 0, 0 },
            Maxs = new[] { 128, 128, 128 }
        });
        d.Leafs.Add(new BspLeaf
        {
            Cluster = 0, Area = 0,
            Mins = new[] { 0, 0, 0 }, Maxs = new[] { 0, 128, 128 },
            FirstLeafFace = 0, NumLeafFaces = 0,
            FirstLeafBrush = 0, NumLeafBrushes = 1
        });
        d.Leafs.Add(new BspLeaf
        {
            Cluster = 1, Area = 0,
            Mins = new[] { 128, 0, 0 }, Maxs = new[] { 256, 128, 128 },
            FirstLeafFace = 0, NumLeafFaces = 1,
            FirstLeafBrush = 1, NumLeafBrushes = 0
        });
        d.LeafFaces.Add(0);
        d.LeafBrushes.Add(0);

        d.Models.Add(new BspModel
        {
            Mins = new Vector3(0, 0, 0),
            Maxs = new Vector3(128, 128, 128),
            FirstFace = 0, NumFaces = 1,
            FirstBrush = 0, NumBrushes = 1
        });

        // one lit planar face (a +z wall of the box) with 4 verts.
        // JKA vertex lightmap UVs are 0..1 block-relative (see LightmapExtractor).
        float[,] lmUv =
        {
            { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 }
        };
        for (int i = 0; i < 4; i++)
        {
            float px = i switch { 0 => 0f, 1 => 128f, 2 => 128f, _ => 0f };
            float py = i switch { 0 => 0f, 1 => 0f, 2 => 128f, _ => 128f };
            var v = new BspVertex
            {
                Position = new Vector3(px, py, 128f),
                St = new Vector2(i * 32f, 0f),
                Normal = new Vector3(0, 0, 1),
            };
            v.Lightmap[0] = lmUv[i, 0];
            v.Lightmap[1] = lmUv[i, 1];
            for (int c = 0; c < 16; c++) v.Color[c] = 255;
            d.Vertices.Add(v);
        }
        d.MeshVerts.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        d.Faces.Add(new BspFace
        {
            ShaderIndex = 0,
            FogIndex = -1,
            Type = FaceType.Planar,
            FirstVertexIndex = 0,
            NumVertices = 4,
            FirstMeshVertIndex = 0,
            NumMeshVerts = 6,
            LightmapNum = new[] { 0, -1, -1, -1 },
            LightmapX = new[] { 0, -1, -1, -1 },
            LightmapY = new[] { 0, -1, -1, -1 },
            LightmapWidth = 128,
            LightmapHeight = 128,
            LightmapOrigin = new Vector3(0, 0, 128),
            LightmapVectors = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }
        });

        // one lightmap block: diagonal gradient so the PNG is obviously lit
        var lm = new BspLightmap();
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                int s = (y * 128 + x) * 3;
                lm.Data[s] = (byte)(x * 2);
                lm.Data[s + 1] = (byte)(y * 2);
                lm.Data[s + 2] = 128;
            }
        d.Lightmaps.Add(lm);

        // lightgrid: world grid at 64/64/128 steps for the 128^3 world
        // = 3 x 3 x 2 = 18 cells; the 30 B cells form a palette that the
        // lightarray u16 indices point into.
        for (int i = 0; i < 5; i++)
        {
            var c = new BspLightGridCell();
            for (int k = 0; k < 12; k++) c.Ambient[k] = (byte)(i * 40 + 15);
            c.Styles[0] = 0;
            d.LightGrid.Add(c);
        }
        d.LightArray.Data = new ushort[] { 0, 1, 2, 0, 1, 2, 3, 4, 2, 1, 3, 0, 2, 4, 1, 0, 3, 4 };

        d.Visibility = new BspVisibility
        {
            NumClusters = 2,
            ClusterSize = 1,
            Data = new byte[] { 0b11, 0b11 }
        };

        return d;
    }
    }
}
