using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Numerics;

namespace JKDecompiler.Core
{
    public class BspReader
    {
        public const string RBSP_IDENT = "RBSP";
        public const int RBSP_VERSION = 1;

        public BspData Read(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                return Read(stream);
            }
        }

        public BspData Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.ASCII, true))
            {
                string ident = new string(reader.ReadChars(4));
                if (ident != RBSP_IDENT)
                {
                    throw new InvalidDataException($"Invalid BSP ident: {ident}. Expected {RBSP_IDENT}");
                }

                int version = reader.ReadInt32();
                if (version != RBSP_VERSION)
                {
                    throw new InvalidDataException($"Invalid BSP version: {version}. Expected {RBSP_VERSION}");
                }

                // RBSP can have 18 or 19 lumps. Let's read 18 first, then check if we can read one more.
                var lumps = new List<Lump>();
                for (int i = 0; i < 18; i++)
                {
                    lumps.Add(new Lump
                    {
                        Offset = reader.ReadInt32(),
                        Length = reader.ReadInt32()
                    });
                }

                // Try to read 19th lump if it looks valid (doesn't point to crazy offsets)
                // However, most JKA maps are 18 lumps.
                // Let's stick to 18 for now to be safe, unless we have a reason to expect 19.
                // Looking at t1_sour, Lump 18 offset was garbage.

                var data = new BspData();
                for (int i = 0; i < lumps.Count; i++) Console.WriteLine($"Lump {i}: Offset {lumps[i].Offset}, Length {lumps[i].Length}");
                data.RawEntities = ReadEntities(reader, lumps[0]);
                data.Entities = BspEntityParser.Parse(data.RawEntities);
                data.Shaders = ReadShaders(reader, lumps[1]);
                data.Planes = ReadPlanes(reader, lumps[2]);
                data.Nodes = ReadNodes(reader, lumps[3]);
                data.Leafs = ReadLeafs(reader, lumps[4]);
                data.LeafFaces = ReadIntList(reader, lumps[5]);
                data.LeafBrushes = ReadIntList(reader, lumps[6]);
                data.Models = ReadModels(reader, lumps[7]);
                data.Brushes = ReadBrushes(reader, lumps[8]);
                data.BrushSides = ReadBrushSides(reader, lumps[9]);
                data.Vertices = ReadVertices(reader, lumps[10]);
                data.MeshVerts = ReadIntList(reader, lumps[11]);
                data.Faces = ReadFaces(reader, lumps[13]);
                data.Lightmaps = ReadLightmaps(reader, lumps[14]);
                data.LightGrid = ReadLightGrid(reader, lumps[15]);
                data.Fogs = ReadFogs(reader, lumps[12]);
                data.Visibility = ReadVisibility(reader, lumps[16]);
                data.LightArray = ReadLightArray(reader, lumps[17]);
                
                return data;
            }
        }

        private List<BspLightArray> ReadLightArray(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 4;
            var array = new List<BspLightArray>(count);
            for (int i = 0; i < count; i++)
            {
                array.Add(new BspLightArray { Data = reader.ReadBytes(4) });
            }
            return array;
        }

        private List<BspFog> ReadFogs(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 72;
            var fogs = new List<BspFog>(count);
            for (int i = 0; i < count; i++)
            {
                fogs.Add(new BspFog
                {
                    Name = new string(reader.ReadChars(64)).TrimEnd('\0'),
                    BrushIndex = reader.ReadInt32(),
                    VisibleSide = reader.ReadInt32()
                });
            }
            return fogs;
        }

        private BspVisibility ReadVisibility(BinaryReader reader, Lump lump)
        {
            if (lump.Length == 0) return new BspVisibility();
            reader.BaseStream.Position = lump.Offset;
            var vis = new BspVisibility
            {
                NumClusters = reader.ReadInt32(),
                ClusterSize = reader.ReadInt32()
            };
            vis.Data = reader.ReadBytes(vis.NumClusters * vis.ClusterSize);
            return vis;
        }

        private List<BspDecal> ReadDecals(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 32;
            var decals = new List<BspDecal>(count);
            for (int i = 0; i < count; i++)
            {
                decals.Add(new BspDecal
                {
                    Origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    ShaderIndex = reader.ReadInt32(),
                    Size = reader.ReadSingle()
                });
            }
            return decals;
        }

        private List<BspLightmap> ReadLightmaps(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / (128 * 128 * 3);
            var lightmaps = new List<BspLightmap>(count);
            for (int i = 0; i < count; i++)
            {
                var lm = new BspLightmap();
                for (int y = 0; y < 128; y++)
                {
                    for (int x = 0; x < 128; x++)
                    {
                        lm.Data[y, x, 0] = reader.ReadByte();
                        lm.Data[y, x, 1] = reader.ReadByte();
                        lm.Data[y, x, 2] = reader.ReadByte();
                    }
                }
                lightmaps.Add(lm);
            }
            return lightmaps;
        }

        private List<BspLightGrid> ReadLightGrid(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 8;
            var grid = new List<BspLightGrid>(count);
            for (int i = 0; i < count; i++)
            {
                grid.Add(new BspLightGrid
                {
                    Ambient = reader.ReadBytes(3),
                    Directed = reader.ReadBytes(3),
                    LatLong = reader.ReadBytes(2)
                });
            }
            return grid;
        }

        private string ReadEntities(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            byte[] bytes = reader.ReadBytes(lump.Length);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }

        private List<BspShader> ReadShaders(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 72;
            var shaders = new List<BspShader>(count);
            for (int i = 0; i < count; i++)
            {
                shaders.Add(new BspShader
                {
                    Name = new string(reader.ReadChars(64)).TrimEnd('\0'),
                    SurfaceFlags = reader.ReadInt32(),
                    ContentFlags = reader.ReadInt32()
                });
            }
            return shaders;
        }

        private List<BspPlane> ReadPlanes(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 16;
            var planes = new List<BspPlane>(count);
            for (int i = 0; i < count; i++)
            {
                planes.Add(new BspPlane
                {
                    Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Distance = reader.ReadSingle()
                });
            }
            return planes;
        }

        private List<BspNode> ReadNodes(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 36;
            var nodes = new List<BspNode>(count);
            for (int i = 0; i < count; i++)
            {
                nodes.Add(new BspNode
                {
                    PlaneIndex = reader.ReadInt32(),
                    Children = new[] { reader.ReadInt32(), reader.ReadInt32() },
                    Mins = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() },
                    Maxs = new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() }
                });
            }
            return nodes;
        }

        private List<BspLeaf> ReadLeafs(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 48;
            var leafs = new List<BspLeaf>(count);
            for (int i = 0; i < count; i++)
            {
                leafs.Add(new BspLeaf
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
            return leafs;
        }

        private List<int> ReadIntList(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 4;
            var list = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(reader.ReadInt32());
            }
            return list;
        }

        private List<BspModel> ReadModels(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 40;
            var models = new List<BspModel>(count);
            for (int i = 0; i < count; i++)
            {
                models.Add(new BspModel
                {
                    Mins = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Maxs = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    FirstFace = reader.ReadInt32(),
                    NumFaces = reader.ReadInt32(),
                    FirstBrush = reader.ReadInt32(),
                    NumBrushes = reader.ReadInt32()
                });
            }
            return models;
        }

        private List<BspBrush> ReadBrushes(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 12;
            var brushes = new List<BspBrush>(count);
            for (int i = 0; i < count; i++)
            {
                brushes.Add(new BspBrush
                {
                    FirstSide = reader.ReadInt32(),
                    NumSides = reader.ReadInt32(),
                    ShaderIndex = reader.ReadInt32()
                });
            }
            return brushes;
        }

        private List<BspBrushSide> ReadBrushSides(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 12; // JKA uses 12 bytes for brush sides
            var sides = new List<BspBrushSide>(count);
            for (int i = 0; i < count; i++)
            {
                sides.Add(new BspBrushSide
                {
                    PlaneIndex = reader.ReadInt32(),
                    ShaderIndex = reader.ReadInt32()
                });
                reader.ReadInt32(); // Skip the 3rd field (Brush index?)
            }
            return sides;
        }

        private List<BspVertex> ReadVertices(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 44;
            var vertices = new List<BspVertex>(count);
            for (int i = 0; i < count; i++)
            {
                vertices.Add(new BspVertex
                {
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    SurfaceUV = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    LightmapUV = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    Normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Color = reader.ReadBytes(4)
                });
            }
            return vertices;
        }

        private List<BspFace> ReadFaces(BinaryReader reader, Lump lump)
        {
            reader.BaseStream.Position = lump.Offset;
            int count = lump.Length / 104;
            var faces = new List<BspFace>(count);
            for (int i = 0; i < count; i++)
            {
                var face = new BspFace();
                face.ShaderIndex = reader.ReadInt32();
                face.FogIndex = reader.ReadInt32();
                face.Type = (FaceType)reader.ReadInt32();
                face.FirstVertexIndex = reader.ReadInt32();
                face.NumVertices = reader.ReadInt32();
                face.FirstMeshVertIndex = reader.ReadInt32();
                face.NumMeshVerts = reader.ReadInt32();
                face.LightmapIndex = reader.ReadInt32();
                face.LightmapStart = new[] { reader.ReadInt32(), reader.ReadInt32() };
                face.LightmapSize = new[] { reader.ReadInt32(), reader.ReadInt32() };
                face.LightmapOrigin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                face.LightmapVectors = new[]
                {
                    new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                };
                face.PatchWidth = reader.ReadInt32();
                face.PatchHeight = reader.ReadInt32();
                faces.Add(face);
            }
            return faces;
        }
    }

    public struct Lump
    {
        public int Offset;
        public int Length;
    }

    public class BspData
    {
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
        public List<BspLightGrid> LightGrid { get; set; } = new();
        public List<BspFog> Fogs { get; set; } = new();
        public BspVisibility Visibility { get; set; } = new();
        public List<BspLightArray> LightArray { get; set; } = new();
        public List<BspDecal> Decals { get; set; } = new();
    }
}
