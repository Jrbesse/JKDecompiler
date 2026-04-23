using System;
using System.Numerics;

namespace JKDecompiler.Core
{
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

    public class BspPlane
    {
        public Vector3 Normal;
        public float Distance;
    }

    public class BspNode
    {
        public int PlaneIndex;
        public int[] Children = new int[2];
        public int[] Mins = new int[3];
        public int[] Maxs = new int[3];
    }

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

    public class BspModel
    {
        public Vector3 Mins;
        public Vector3 Maxs;
        public int FirstFace;
        public int NumFaces;
        public int FirstBrush;
        public int NumBrushes;
    }

    public class BspBrush
    {
        public int FirstSide;
        public int NumSides;
        public int ShaderIndex;
    }

    public class BspBrushSide
    {
        public int PlaneIndex;
        public int ShaderIndex;
    }

    public class BspVertex
    {
        public Vector3 Position;
        public Vector2 SurfaceUV;
        public Vector2 LightmapUV;
        public Vector3 Normal;
        public byte[] Color = new byte[4];
    }

    public class BspShader
    {
        public string Name = string.Empty;
        public int SurfaceFlags;
        public int ContentFlags;
    }

    public enum FaceType
    {
        Polygon = 1,
        Patch = 2,
        Mesh = 3,
        Billboard = 4
    }

    public class BspFace
    {
        public int ShaderIndex;
        public int FogIndex;
        public FaceType Type;
        public int FirstVertexIndex;
        public int NumVertices;
        public int FirstMeshVertIndex;
        public int NumMeshVerts;
        public int LightmapIndex;
        public int[] LightmapStart = new int[2];
        public int[] LightmapSize = new int[2];
        public Vector3 LightmapOrigin;
        public Vector3[] LightmapVectors = new Vector3[3];
        public int PatchWidth;
        public int PatchHeight;
    }

    public class BspLightGrid
    {
        public byte[] Ambient = new byte[3];
        public byte[] Directed = new byte[3];
        public byte[] LatLong = new byte[2];
    }

    public class BspLightmap
    {
        public byte[,,] Data = new byte[128, 128, 3];
    }

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

    public class BspLightArray
    {
        public byte[] Data = new byte[4];
    }

    public class BspDecal
    {
        public Vector3 Origin;
        public Vector3 Normal;
        public int ShaderIndex;
        public float Size;
    }
}
