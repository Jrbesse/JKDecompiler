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
