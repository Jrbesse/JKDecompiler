using System;
using System.Numerics;

namespace JKDecompiler.Core
{
    /// <summary>
    /// JKA "RBSP" format constants. Record sizes verified 2026-11-21 against
    /// OpenJK codemp/qcommon/qfiles.h and byte-measured on GOG pak maps
    /// (academy1 / t1_inter / ctf1 lump lengths divide exactly):
    ///   mapVert_t 80 B, dsurface_t 148 B, dgrid_t 30 B, lightarray u16/cell.
    /// The old parser used Quake 3 sizes (44 / 104 / 8) which is why every
    /// record after the first was garbage.
    /// </summary>
    public static class JkaBsp
    {
        public const int MAX_LIGHTMAPS = 4;
        public const int LIGHTMAP_BLOCK = 128;
        public const int LIGHTMAP_BLOCK_BYTES = LIGHTMAP_BLOCK * LIGHTMAP_BLOCK * 3;
        public const int LUMP_COUNT = 18;
        public const int HEADER_BYTES = 4 + 4 + LUMP_COUNT * 8; // ident, version, 18 (off,len)

        public const int LUMP_ENTITIES = 0;
        public const int LUMP_SHADERS = 1;
        public const int LUMP_PLANES = 2;
        public const int LUMP_NODES = 3;
        public const int LUMP_LEAFS = 4;
        public const int LUMP_LEAFSURFS = 5;
        public const int LUMP_LEAFBRUSHES = 6;
        public const int LUMP_MODELS = 7;
        public const int LUMP_BRUSHES = 8;
        public const int LUMP_BRUSHSIDES = 9;
        public const int LUMP_DRAWVERTS = 10;
        public const int LUMP_DRAWINDEXES = 11;
        public const int LUMP_FOGS = 12;
        public const int LUMP_SURFACES = 13;
        public const int LUMP_LIGHTMAPS = 14;
        public const int LUMP_LIGHTGRID = 15;
        public const int LUMP_VISIBILITY = 16;
        public const int LUMP_LIGHTARRAY = 17;

        public const int SIZE_SHADER = 72;
        public const int SIZE_PLANE = 16;
        public const int SIZE_NODE = 36;
        public const int SIZE_LEAF = 48;
        public const int SIZE_MODEL = 40;
        public const int SIZE_BRUSH = 12;
        public const int SIZE_BRUSHSIDE = 12;
        public const int SIZE_MAPVERT = 80;
        public const int SIZE_FOG = 72;
        public const int SIZE_SURFACE = 148;
        public const int SIZE_LIGHTMAP_BLOCK = LIGHTMAP_BLOCK_BYTES;
        public const int SIZE_GRID_CELL = 30;
    }

    public struct Vector3f
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    public struct Vector2f
    {
        public float X;
        public float Y;

        public Vector2 ToVector2() => new Vector2(X, Y);
    }

    /// <summary>dplane_t — 16 B.</summary>
    public class BspPlane
    {
        public Vector3 Normal;
        public float Distance;
    }

    /// <summary>dnode_t — 36 B. Children: >=0 is a node index, -(leaf+1) is a leaf.</summary>
    public class BspNode
    {
        public int PlaneIndex;
        public int[] Children = new int[2];
        public int[] Mins = new int[3];
        public int[] Maxs = new int[3];
    }

    /// <summary>dleaf_t — 48 B.</summary>
    public class BspLeaf
    {
        public int Cluster;
        public int Area;
        public int[] Mins = new int[3];
        public int[] Maxs = new int[3];
        public int FirstLeafFace;
        public int NumLeafFaces;
        public int FirstLeafBrush;
        public int NumLeafBrushes;
    }

    /// <summary>dmodel_t — 40 B. Model 0 is the world.</summary>
    public class BspModel
    {
        public Vector3 Mins;
        public Vector3 Maxs;
        public int FirstFace;
        public int NumFaces;
        public int FirstBrush;
        public int NumBrushes;
    }

    /// <summary>dbrush_t — 12 B.</summary>
    public class BspBrush
    {
        public int FirstSide;
        public int NumSides;
        public int ShaderIndex;
    }

    /// <summary>dbrushside_t — 12 B (planeNum, shaderNum, pad).</summary>
    public class BspBrushSide
    {
        public int PlaneIndex;
        public int ShaderIndex;
    }

    /// <summary>
    /// mapVert_t — 80 B: xyz f3, st f2, lightmap[4][2] f8, normal f3,
    /// color[4][4] b16.
    /// </summary>
    public class BspVertex
    {
        public Vector3 Position;
        public Vector2 St;
        public float[] Lightmap = new float[JkaBsp.MAX_LIGHTMAPS * 2];
        public Vector3 Normal;
        public byte[] Color = new byte[JkaBsp.MAX_LIGHTMAPS * 4];

        public float GetLightmapU(int lightmap) => Lightmap[lightmap * 2];
        public float GetLightmapV(int lightmap) => Lightmap[lightmap * 2 + 1];
    }

    /// <summary>dshader_t — 72 B.</summary>
    public class BspShader
    {
        public string Name = string.Empty;
        public int SurfaceFlags;
        public int ContentFlags;
    }

    /// <summary>mapSurfaceType_t in OpenJK qfiles.h.</summary>
    public enum FaceType
    {
        Bad = 0,
        Planar = 1,
        Patch = 2,
        TriangleSoup = 3,
        Flare = 4
    }

    /// <summary>
    /// dsurface_t — 148 B. NOTE: lightmapStyles/vertexStyles are bytes in JKA
    /// (Quake 3 has no vertexStyles; the JKA layout fits 148 B exactly).
    /// </summary>
    public class BspFace
    {
        public int ShaderIndex;
        public int FogIndex;
        public FaceType Type;
        public int FirstVertexIndex;
        public int NumVertices;
        public int FirstMeshVertIndex;
        public int NumMeshVerts;
        public byte[] LightmapStyles = new byte[JkaBsp.MAX_LIGHTMAPS];
        public byte[] VertexStyles = new byte[JkaBsp.MAX_LIGHTMAPS];
        public int[] LightmapNum = new int[JkaBsp.MAX_LIGHTMAPS];
        public int[] LightmapX = new int[JkaBsp.MAX_LIGHTMAPS];
        public int[] LightmapY = new int[JkaBsp.MAX_LIGHTMAPS];
        public int LightmapWidth;
        public int LightmapHeight;
        public Vector3 LightmapOrigin;
        public float[] LightmapVectors = new float[9]; // matrix3_t, 3 columns

        /// <summary>
        /// For patches: control points per axis; the face stores exactly
        /// patchWidth * patchHeight control verts (engine: numPoints = width * height).
        /// </summary>
        public int PatchWidth;
        public int PatchHeight;
    }

    /// <summary>dgrid_t — 30 B: ambient[4][3], directed[4][3], styles[4], latLong[2].</summary>
    public class BspLightGridCell
    {
        public byte[] Ambient = new byte[JkaBsp.MAX_LIGHTMAPS * 3];
        public byte[] Directed = new byte[JkaBsp.MAX_LIGHTMAPS * 3];
        public byte[] Styles = new byte[JkaBsp.MAX_LIGHTMAPS];
        public byte[] LatLong = new byte[2];
    }

    /// <summary>Lump 14: fixed 128x128 RGB blocks, row-major, row 0 = v=0.</summary>
    public class BspLightmap
    {
        public byte[] Data = new byte[JkaBsp.LIGHTMAP_BLOCK_BYTES];
    }

    /// <summary>dfog_t — 72 B.</summary>
    public class BspFog
    {
        public string Name = string.Empty;
        public int BrushIndex;
        public int VisibleSide;
    }

    public class BspVisibility
    {
        public int NumClusters;
        public int ClusterSize;
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>Lump 17: one u16 per lightgrid cell (engine R_LoadLightGridArray).</summary>
    public class BspLightArray
    {
        public ushort[] Data = Array.Empty<ushort>();
    }
}
