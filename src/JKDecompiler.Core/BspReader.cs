using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JKDecompiler.Core
{
    /// <summary>
    /// Reader for JKA "RBSP" files (18 lumps, JKA record sizes).
    /// Struct layout ground truth: OpenJK codemp/qcommon/qfiles.h —
    /// mapVert_t 80 B, dsurface_t 148 B, dgrid_t 30 B, lightarray u16/cell.
    /// </summary>
    public class BspReader
    {
        public const string RBSP_IDENT = "RBSP";
        public const int RBSP_VERSION = 1;

        public BspData Read(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Read(stream);
        }

        public BspData Read(Stream stream)
        {
            var fileLength = (long)stream.Length;
            if (fileLength < JkaBsp.HEADER_BYTES)
                throw new InvalidDataException($"File too small for RBSP header: {fileLength} bytes");

            using var reader = new BinaryReader(stream, Encoding.ASCII, true);

            string ident = new string(reader.ReadChars(4));
            if (ident != RBSP_IDENT)
                throw new InvalidDataException($"Invalid BSP ident: {ident}. Expected {RBSP_IDENT}");

            int version = reader.ReadInt32();
            if (version != RBSP_VERSION)
                throw new InvalidDataException($"Invalid BSP version: {version}. Expected {RBSP_VERSION}");

            var lumps = new Lump[JkaBsp.LUMP_COUNT];
            for (int i = 0; i < JkaBsp.LUMP_COUNT; i++)
            {
                lumps[i] = new Lump { Offset = reader.ReadInt32(), Length = reader.ReadInt32() };
                if (lumps[i].Offset < 0 || lumps[i].Length < 0 ||
                    (long)lumps[i].Offset + lumps[i].Length > fileLength)
                    throw new InvalidDataException(
                        $"Lump {lumps[i].Offset} out of bounds: lump {i} off={lumps[i].Offset} len={lumps[i].Length} filesize={fileLength}");
            }

            var data = new BspData
            {
                FileLength = fileLength,
                Lumps = lumps
            };

            data.RawEntities = ReadString(reader, lumps[JkaBsp.LUMP_ENTITIES]);
            data.Entities = BspEntityParser.Parse(data.RawEntities);
            data.Shaders = ReadShaders(reader, lumps[JkaBsp.LUMP_SHADERS]);
            data.Planes = ReadPlanes(reader, lumps[JkaBsp.LUMP_PLANES]);
            data.Nodes = ReadNodes(reader, lumps[JkaBsp.LUMP_NODES]);
            data.Leafs = ReadLeafs(reader, lumps[JkaBsp.LUMP_LEAFS]);
            data.LeafFaces = ReadIntList(reader, lumps[JkaBsp.LUMP_LEAFSURFS]);
            data.LeafBrushes = ReadIntList(reader, lumps[JkaBsp.LUMP_LEAFBRUSHES]);
            data.Models = ReadModels(reader, lumps[JkaBsp.LUMP_MODELS]);
            data.Brushes = ReadBrushes(reader, lumps[JkaBsp.LUMP_BRUSHES]);
            data.BrushSides = ReadBrushSides(reader, lumps[JkaBsp.LUMP_BRUSHSIDES]);
            data.Vertices = ReadVertices(reader, lumps[JkaBsp.LUMP_DRAWVERTS]);
            data.MeshVerts = ReadIntList(reader, lumps[JkaBsp.LUMP_DRAWINDEXES]);
            data.Fogs = ReadFogs(reader, lumps[JkaBsp.LUMP_FOGS]);
            data.Faces = ReadFaces(reader, lumps[JkaBsp.LUMP_SURFACES]);
            data.Lightmaps = ReadLightmaps(reader, lumps[JkaBsp.LUMP_LIGHTMAPS]);
            data.LightGrid = ReadLightGrid(reader, lumps[JkaBsp.LUMP_LIGHTGRID]);
            data.Visibility = ReadVisibility(reader, lumps[JkaBsp.LUMP_VISIBILITY]);
            data.LightArray = ReadLightArray(reader, lumps[JkaBsp.LUMP_LIGHTARRAY]);

            return data;
        }

        private static string ReadString(BinaryReader reader, Lump lump)
        {
            if (lump.Length == 0) return string.Empty;
            reader.BaseStream.Position = lump.Offset;
            byte[] bytes = reader.ReadBytes(lump.Length);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }

        private static List<BspShader> ReadShaders(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_SHADER, "shaders");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspShader>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspShader
                {
                    Name = new string(reader.ReadChars(64)).TrimEnd('\0'),
                    SurfaceFlags = reader.ReadInt32(),
                    ContentFlags = reader.ReadInt32()
                });
            }
            return list;
        }

        private static List<BspPlane> ReadPlanes(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_PLANE, "planes");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspPlane>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspPlane
                {
                    Normal = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Distance = reader.ReadSingle()
                });
            }
            return list;
        }

        private static List<BspNode> ReadNodes(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_NODE, "nodes");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspNode>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspNode
                {
                    PlaneIndex = reader.ReadInt32(),
                    Children = new[] { reader.ReadInt32(), reader.ReadInt32() },
                    Mins = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() },
                    Maxs = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() }
                });
            }
            return list;
        }

        private static List<BspLeaf> ReadLeafs(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_LEAF, "leafs");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspLeaf>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspLeaf
                {
                    Cluster = reader.ReadInt32(),
                    Area = reader.ReadInt32(),
                    Mins = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() },
                    Maxs = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() },
                    FirstLeafFace = reader.ReadInt32(),
                    NumLeafFaces = reader.ReadInt32(),
                    FirstLeafBrush = reader.ReadInt32(),
                    NumLeafBrushes = reader.ReadInt32()
                });
            }
            return list;
        }

        private static List<int> ReadIntList(BinaryReader reader, Lump lump)
        {
            if (lump.Length % 4 != 0)
                throw new InvalidDataException($"lump {lump.Offset}: length {lump.Length} is not a multiple of 4");
            int count = lump.Length / 4;
            reader.BaseStream.Position = lump.Offset;
            var list = new List<int>(count);
            for (int i = 0; i < count; i++) list.Add(reader.ReadInt32());
            return list;
        }

        private static List<BspModel> ReadModels(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_MODEL, "models");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspModel>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspModel
                {
                    Mins = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Maxs = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    FirstFace = reader.ReadInt32(),
                    NumFaces = reader.ReadInt32(),
                    FirstBrush = reader.ReadInt32(),
                    NumBrushes = reader.ReadInt32()
                });
            }
            return list;
        }

        private static List<BspBrush> ReadBrushes(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_BRUSH, "brushes");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspBrush>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspBrush
                {
                    FirstSide = reader.ReadInt32(),
                    NumSides = reader.ReadInt32(),
                    ShaderIndex = reader.ReadInt32()
                });
            }
            return list;
        }

        private static List<BspBrushSide> ReadBrushSides(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_BRUSHSIDE, "brushsides");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspBrushSide>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspBrushSide
                {
                    PlaneIndex = reader.ReadInt32(),
                    ShaderIndex = reader.ReadInt32()
                });
                reader.ReadInt32(); // pad
            }
            return list;
        }

        private static List<BspVertex> ReadVertices(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_MAPVERT, "drawverts (JKA mapVert_t is 80 B)");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspVertex>(count);
            for (int i = 0; i < count; i++)
            {
                var v = new BspVertex();
                v.Position = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                v.St = new System.Numerics.Vector2(reader.ReadSingle(), reader.ReadSingle());
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS * 2; c++) v.Lightmap[c] = reader.ReadSingle();
                v.Normal = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS * 4; c++) v.Color[c] = reader.ReadByte();
                list.Add(v);
            }
            return list;
        }

        private static List<BspFog> ReadFogs(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_FOG, "fogs");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspFog>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspFog
                {
                    Name = new string(reader.ReadChars(64)).TrimEnd('\0'),
                    BrushIndex = reader.ReadInt32(),
                    VisibleSide = reader.ReadInt32()
                });
            }
            return list;
        }

        private static List<BspFace> ReadFaces(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_SURFACE, "surfaces (JKA dsurface_t is 148 B)");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspFace>(count);
            for (int i = 0; i < count; i++)
            {
                var f = new BspFace();
                f.ShaderIndex = reader.ReadInt32();
                f.FogIndex = reader.ReadInt32();
                f.Type = (FaceType)reader.ReadInt32();
                f.FirstVertexIndex = reader.ReadInt32();
                f.NumVertices = reader.ReadInt32();
                f.FirstMeshVertIndex = reader.ReadInt32();
                f.NumMeshVerts = reader.ReadInt32();
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) f.LightmapStyles[c] = reader.ReadByte();
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) f.VertexStyles[c] = reader.ReadByte();
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) f.LightmapNum[c] = reader.ReadInt32();
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) f.LightmapX[c] = reader.ReadInt32();
                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) f.LightmapY[c] = reader.ReadInt32();
                f.LightmapWidth = reader.ReadInt32();
                f.LightmapHeight = reader.ReadInt32();
                f.LightmapOrigin = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int c = 0; c < 9; c++) f.LightmapVectors[c] = reader.ReadSingle();
                f.PatchWidth = reader.ReadInt32();
                f.PatchHeight = reader.ReadInt32();
                list.Add(f);
            }
            return list;
        }

        private static List<BspLightmap> ReadLightmaps(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.LIGHTMAP_BLOCK_BYTES, "lightmaps (128x128x3 blocks)");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspLightmap>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BspLightmap { Data = reader.ReadBytes(JkaBsp.LIGHTMAP_BLOCK_BYTES) });
            }
            return list;
        }

        private static List<BspLightGridCell> ReadLightGrid(BinaryReader reader, Lump lump)
        {
            int count = RequireDivisible(lump, JkaBsp.SIZE_GRID_CELL, "lightgrid (JKA dgrid_t is 30 B)");
            reader.BaseStream.Position = lump.Offset;
            var list = new List<BspLightGridCell>(count);
            for (int i = 0; i < count; i++)
            {
                var cell = new BspLightGridCell();
                cell.Ambient = reader.ReadBytes(JkaBsp.MAX_LIGHTMAPS * 3);
                cell.Directed = reader.ReadBytes(JkaBsp.MAX_LIGHTMAPS * 3);
                cell.Styles = reader.ReadBytes(JkaBsp.MAX_LIGHTMAPS);
                cell.LatLong = reader.ReadBytes(2);
                list.Add(cell);
            }
            return list;
        }

        private static BspVisibility ReadVisibility(BinaryReader reader, Lump lump)
        {
            if (lump.Length == 0) return new BspVisibility();
            reader.BaseStream.Position = lump.Offset;
            var vis = new BspVisibility
            {
                NumClusters = reader.ReadInt32(),
                ClusterSize = reader.ReadInt32()
            };
            if (lump.Length < 8 || (long)vis.NumClusters * vis.ClusterSize > lump.Length - 8)
                throw new InvalidDataException($"visibility lump inconsistent: {vis.NumClusters}x{vis.ClusterSize} in {lump.Length} bytes");
            vis.Data = reader.ReadBytes(vis.NumClusters * vis.ClusterSize);
            return vis;
        }

        private static BspLightArray ReadLightArray(BinaryReader reader, Lump lump)
        {
            if (lump.Length % 2 != 0)
                throw new InvalidDataException($"lightarray lump {lump.Length} is not a multiple of 2 (u16 per cell)");
            int count = lump.Length / 2;
            reader.BaseStream.Position = lump.Offset;
            var arr = new ushort[count];
            for (int i = 0; i < count; i++) arr[i] = reader.ReadUInt16();
            return new BspLightArray { Data = arr };
        }

        private static int RequireDivisible(Lump lump, int recordSize, string what)
        {
            if (lump.Length % recordSize != 0)
                throw new InvalidDataException(
                    $"{what} lump: length {lump.Length} is not a multiple of {recordSize} — wrong record size or corrupt file");
            return lump.Length / recordSize;
        }
    }

    public struct Lump
    {
        public int Offset;
        public int Length;
    }

    public class BspData
    {
        public long FileLength { get; set; }
        public Lump[] Lumps { get; set; } = Array.Empty<Lump>();

        public string RawEntities { get; set; } = string.Empty;
        public List<BspEntity> Entities { get; set; } = new();
        public List<BspShader> Shaders { get; set; } = new();
        public List<BspPlane> Planes { get; set; } = new();
        public List<BspNode> Nodes { get; set; } = new();
        public List<BspLeaf> Leafs { get; set; } = new();
        public List<int> LeafFaces { get; set; } = new();
        public List<int> LeafBrushes { get; set; } = new();
        public List<BspModel> Models { get; set; } = new();
        public List<BspBrush> Brushes { get; set; } = new();
        public List<BspBrushSide> BrushSides { get; set; } = new();
        public List<BspVertex> Vertices { get; set; } = new();
        public List<int> MeshVerts { get; set; } = new();
        public List<BspFace> Faces { get; set; } = new();
        public List<BspLightmap> Lightmaps { get; set; } = new();
        public List<BspLightGridCell> LightGrid { get; set; } = new();
        public List<BspFog> Fogs { get; set; } = new();
        public BspVisibility Visibility { get; set; } = new();
        public BspLightArray LightArray { get; set; } = new();
    }
}
