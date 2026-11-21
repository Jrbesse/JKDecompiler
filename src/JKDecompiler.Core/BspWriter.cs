using System;
using System.IO;
using System.Text;

namespace JKDecompiler.Core
{
    /// <summary>
    /// Serializes BspData back to a valid JKA RBSP file (18 lumps, JKA
    /// record sizes). Needed for the entity fast-path (M3: rewrite the
    /// entity lump + header, leave the rest byte-stable) and the surface
    /// freeze re-injector (M5: copy lightmap blocks into a recompiled BSP).
    /// </summary>
    public class BspWriter
    {
        public void Write(BspData data, string filePath)
        {
            using var stream = new MemoryStream();
            Write(data, stream);
            File.WriteAllBytes(filePath, stream.ToArray());
        }

        public void Write(BspData data, Stream stream)
        {
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);

            // reserve the header, write lumps after it, patch the header last
            stream.Position += JkaBsp.HEADER_BYTES;
            int[] offsets = new int[JkaBsp.LUMP_COUNT];
            int[] lengths = new int[JkaBsp.LUMP_COUNT];

            for (int i = 0; i < JkaBsp.LUMP_COUNT; i++)
            {
                offsets[i] = (int)stream.Position;
                switch (i)
                {
                    case JkaBsp.LUMP_ENTITIES:
                        lengths[i] = WriteRawString(writer, data.RawEntities);
                        break;
                    case JkaBsp.LUMP_SHADERS:
                        lengths[i] = WriteInts(writer, data.Shaders.Count * JkaBsp.SIZE_SHADER, w =>
                        {
                            foreach (var s in data.Shaders)
                            {
                                WriteName(w, s.Name);
                                w.Write(s.SurfaceFlags);
                                w.Write(s.ContentFlags);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_PLANES:
                        lengths[i] = WriteInts(writer, data.Planes.Count * JkaBsp.SIZE_PLANE, w =>
                        {
                            foreach (var p in data.Planes)
                            {
                                w.Write(p.Normal.X); w.Write(p.Normal.Y); w.Write(p.Normal.Z);
                                w.Write(p.Distance);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_NODES:
                        lengths[i] = WriteInts(writer, data.Nodes.Count * JkaBsp.SIZE_NODE, w =>
                        {
                            foreach (var n in data.Nodes)
                            {
                                w.Write(n.PlaneIndex);
                                w.Write(n.Children[0]); w.Write(n.Children[1]);
                                for (int a = 0; a < 3; a++) w.Write(n.Mins[a]);
                                for (int a = 0; a < 3; a++) w.Write(n.Maxs[a]);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_LEAFS:
                        lengths[i] = WriteInts(writer, data.Leafs.Count * JkaBsp.SIZE_LEAF, w =>
                        {
                            foreach (var l in data.Leafs)
                            {
                                w.Write(l.Cluster); w.Write(l.Area);
                                for (int a = 0; a < 3; a++) w.Write(l.Mins[a]);
                                for (int a = 0; a < 3; a++) w.Write(l.Maxs[a]);
                                w.Write(l.FirstLeafFace); w.Write(l.NumLeafFaces);
                                w.Write(l.FirstLeafBrush); w.Write(l.NumLeafBrushes);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_LEAFSURFS:
                        lengths[i] = WriteInts(writer, data.LeafFaces.Count * 4,
                            w => { foreach (var v in data.LeafFaces) w.Write(v); });
                        break;
                    case JkaBsp.LUMP_LEAFBRUSHES:
                        lengths[i] = WriteInts(writer, data.LeafBrushes.Count * 4,
                            w => { foreach (var v in data.LeafBrushes) w.Write(v); });
                        break;
                    case JkaBsp.LUMP_MODELS:
                        lengths[i] = WriteInts(writer, data.Models.Count * JkaBsp.SIZE_MODEL, w =>
                        {
                            foreach (var m in data.Models)
                            {
                                w.Write(m.Mins.X); w.Write(m.Mins.Y); w.Write(m.Mins.Z);
                                w.Write(m.Maxs.X); w.Write(m.Maxs.Y); w.Write(m.Maxs.Z);
                                w.Write(m.FirstFace); w.Write(m.NumFaces);
                                w.Write(m.FirstBrush); w.Write(m.NumBrushes);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_BRUSHES:
                        lengths[i] = WriteInts(writer, data.Brushes.Count * JkaBsp.SIZE_BRUSH, w =>
                        {
                            foreach (var b in data.Brushes)
                            {
                                w.Write(b.FirstSide); w.Write(b.NumSides); w.Write(b.ShaderIndex);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_BRUSHSIDES:
                        lengths[i] = WriteInts(writer, data.BrushSides.Count * JkaBsp.SIZE_BRUSHSIDE, w =>
                        {
                            foreach (var s in data.BrushSides)
                            {
                                w.Write(s.PlaneIndex); w.Write(s.ShaderIndex); w.Write(0); // pad
                            }
                        });
                        break;
                    case JkaBsp.LUMP_DRAWVERTS:
                        lengths[i] = WriteInts(writer, data.Vertices.Count * JkaBsp.SIZE_MAPVERT, w =>
                        {
                            foreach (var v in data.Vertices)
                            {
                                w.Write(v.Position.X); w.Write(v.Position.Y); w.Write(v.Position.Z);
                                w.Write(v.St.X); w.Write(v.St.Y);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS * 2; c++) w.Write(v.Lightmap[c]);
                                w.Write(v.Normal.X); w.Write(v.Normal.Y); w.Write(v.Normal.Z);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS * 4; c++) w.Write(v.Color[c]);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_DRAWINDEXES:
                        lengths[i] = WriteInts(writer, data.MeshVerts.Count * 4,
                            w => { foreach (var v in data.MeshVerts) w.Write(v); });
                        break;
                    case JkaBsp.LUMP_FOGS:
                        lengths[i] = WriteInts(writer, data.Fogs.Count * JkaBsp.SIZE_FOG, w =>
                        {
                            foreach (var f in data.Fogs)
                            {
                                WriteName(w, f.Name);
                                w.Write(f.BrushIndex);
                                w.Write(f.VisibleSide);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_SURFACES:
                        lengths[i] = WriteInts(writer, data.Faces.Count * JkaBsp.SIZE_SURFACE, w =>
                        {
                            foreach (var f in data.Faces)
                            {
                                w.Write(f.ShaderIndex); w.Write(f.FogIndex); w.Write((int)f.Type);
                                w.Write(f.FirstVertexIndex); w.Write(f.NumVertices);
                                w.Write(f.FirstMeshVertIndex); w.Write(f.NumMeshVerts);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) w.Write(f.LightmapStyles[c]);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) w.Write(f.VertexStyles[c]);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) w.Write(f.LightmapNum[c]);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) w.Write(f.LightmapX[c]);
                                for (int c = 0; c < JkaBsp.MAX_LIGHTMAPS; c++) w.Write(f.LightmapY[c]);
                                w.Write(f.LightmapWidth); w.Write(f.LightmapHeight);
                                w.Write(f.LightmapOrigin.X); w.Write(f.LightmapOrigin.Y); w.Write(f.LightmapOrigin.Z);
                                for (int c = 0; c < 9; c++) w.Write(f.LightmapVectors[c]);
                                w.Write(f.PatchWidth); w.Write(f.PatchHeight);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_LIGHTMAPS:
                        lengths[i] = WriteInts(writer, data.Lightmaps.Count * JkaBsp.LIGHTMAP_BLOCK_BYTES, w =>
                        {
                            foreach (var lm in data.Lightmaps) w.Write(lm.Data);
                        });
                        break;
                    case JkaBsp.LUMP_LIGHTGRID:
                        lengths[i] = WriteInts(writer, data.LightGrid.Count * JkaBsp.SIZE_GRID_CELL, w =>
                        {
                            foreach (var g in data.LightGrid)
                            {
                                w.Write(g.Ambient); w.Write(g.Directed); w.Write(g.Styles); w.Write(g.LatLong);
                            }
                        });
                        break;
                    case JkaBsp.LUMP_VISIBILITY:
                        lengths[i] = WriteInts(writer, data.Visibility.Data.Length + 8, w =>
                        {
                            w.Write(data.Visibility.NumClusters);
                            w.Write(data.Visibility.ClusterSize);
                            w.Write(data.Visibility.Data);
                        });
                        break;
                    case JkaBsp.LUMP_LIGHTARRAY:
                        lengths[i] = WriteInts(writer, data.LightArray.Data.Length * 2, w =>
                        {
                            foreach (var v in data.LightArray.Data) w.Write(v);
                        });
                        break;
                }
            }

            // patch the header
            long end = stream.Position;
            stream.Position = 0;
            writer.Write(Encoding.ASCII.GetBytes("RBSP"));
            writer.Write(RBSP_VERSION);
            for (int i = 0; i < JkaBsp.LUMP_COUNT; i++)
            {
                writer.Write(offsets[i]);
                writer.Write(lengths[i]);
            }
            stream.Position = end;
        }

        public const int RBSP_VERSION = 1;

        private static int WriteInts(BinaryWriter writer, int expected, Action<BinaryWriter> body)
        {
            long start = writer.BaseStream.Position;
            body(writer);
            long len = writer.BaseStream.Position - start;
            if (len != expected)
                throw new InvalidOperationException($"lump size {len} != expected {expected}");
            return (int)len;
        }

        private static int WriteRawString(BinaryWriter writer, string s)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(s + "\0");
            writer.Write(bytes);
            return bytes.Length;
        }

        private static void WriteName(BinaryWriter writer, string name)
        {
            var buf = new byte[64];
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, 64));
            writer.Write(buf);
        }
    }
}
