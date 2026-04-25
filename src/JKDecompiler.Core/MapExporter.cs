using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace JKDecompiler.Core
{
    public class MapExporter
    {
        public void Export(BspData data, string filePath)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.ASCII))
            {
                for (int i = 0; i < data.Entities.Count; i++)
                {
                    ExportEntity(data, data.Entities[i], writer, i);
                }
            }
        }

        private void ExportEntity(BspData data, BspEntity entity, StreamWriter writer, int index)
        {
            writer.WriteLine("{");

            foreach (var kv in entity.KeyValues)
            {
                // Ensure key and value are properly quoted
                writer.WriteLine($"\"{kv.Key}\" \"{kv.Value}\"");
            }

            // If it's worldspawn, export all world brushes and patches
            if (entity.ClassName.Equals("worldspawn", StringComparison.OrdinalIgnoreCase))
            {
                ExportBrushes(data, 0, writer);
                ExportPatches(data, 0, writer);
            }
            else if (entity.KeyValues.TryGetValue("model", out var modelStr) && modelStr.StartsWith("*"))
            {
                // It's a brush entity (func_door, etc.)
                if (int.TryParse(modelStr.Substring(1), out var modelIndex))
                {
                    ExportBrushes(data, modelIndex, writer);
                    ExportPatches(data, modelIndex, writer);
                }
            }

            writer.WriteLine("}");
        }

        private void ExportPatches(BspData data, int modelIndex, StreamWriter writer)
        {
            var model = data.Models[modelIndex];
            for (int i = 0; i < model.NumFaces; i++)
            {
                int faceIndex = model.FirstFace + i;
                if (faceIndex < 0 || faceIndex >= data.Faces.Count) continue;
                var face = data.Faces[faceIndex];
                if (face.Type == FaceType.Patch)
                {
                    ExportPatch(data, face, writer, i);
                }
            }
        }

        private void ExportPatch(BspData data, BspFace face, StreamWriter writer, int index)
        {
            if (face.ShaderIndex < 0 || face.ShaderIndex >= data.Shaders.Count) return;
            if (face.FirstVertexIndex < 0 || face.FirstVertexIndex >= data.Vertices.Count) return;

            // Patch must have at least 3x3 vertices to be a valid patch
            if (face.PatchWidth < 3 || face.PatchHeight < 3) return;

            writer.WriteLine("{");
            writer.WriteLine(" patchDef2");
            writer.WriteLine(" {");

            var shader = data.Shaders[face.ShaderIndex];
            writer.WriteLine($"  {CleanShaderName(shader.Name)}");
            // Ensure patch dimensions do not exceed GTKRadiant limits (32x32)
            int exportWidth = Math.Min(face.PatchWidth, 32);
            int exportHeight = Math.Min(face.PatchHeight, 32);
            writer.WriteLine($"  ( {exportWidth} {exportHeight} 0 0 0 )");
            writer.WriteLine("  (");

            for (int h = 0; h < exportHeight; h++)
            {
                writer.Write("   ( ");
                for (int w = 0; w < exportWidth; w++)
                {
                    int vertexIndex = face.FirstVertexIndex + (h * face.PatchWidth + w);
                    if (vertexIndex < 0 || vertexIndex >= data.Vertices.Count)
                    {
                        writer.Write("( 0 0 0 0 0 ) ");
                        continue;
                    }
                    var v = data.Vertices[vertexIndex];
                    
                    writer.Write(string.Format(CultureInfo.InvariantCulture, 
                        "( {0:0.######} {1:0.######} {2:0.######} {3:0.######} {4:0.######} ) ",
                        v.Position.X, v.Position.Y, v.Position.Z, v.SurfaceUV.X, v.SurfaceUV.Y));
                }
                writer.WriteLine(")");
            }

            writer.WriteLine("  )");
            writer.WriteLine(" }");
            writer.WriteLine("}");
        }

        private void ExportBrushes(BspData data, int modelIndex, StreamWriter writer)
        {
            var model = data.Models[modelIndex];
            for (int i = 0; i < model.NumBrushes; i++)
            {
                var brush = data.Brushes[model.FirstBrush + i];
                writer.WriteLine($"// brush {i}");
                ExportBrush(data, brush, writer, i);
            }
        }

        private void ExportBrush(BspData data, BspBrush brush, StreamWriter writer, int index)
        {
            if (brush.NumSides < 4) return;

            writer.WriteLine("{");

            bool isDetail = false;
            // Use a list of exported planes and check similarity instead of just index
            var exportedPlanes = new List<BspPlane>();

            for (int i = 0; i < brush.NumSides; i++)
            {
                var side = data.BrushSides[brush.FirstSide + i];
                if (side.ShaderIndex >= 0 && side.ShaderIndex < data.Shaders.Count)
                {
                    if ((data.Shaders[side.ShaderIndex].SurfaceFlags & 0x8000000) != 0)
                    {
                        isDetail = true;
                        break;
                    }
                }
            }

            for (int i = 0; i < brush.NumSides; i++)
            {
                int sideIndex = brush.FirstSide + i;
                if (sideIndex >= data.BrushSides.Count) continue;
                
                var side = data.BrushSides[sideIndex];
                var plane = data.Planes[side.PlaneIndex];

                // Skip duplicate/near-identical planes within the same brush
                bool duplicate = false;
                foreach (var ep in exportedPlanes)
                {
                    if (Vector3.Dot(ep.Normal, plane.Normal) > 0.999f && Math.Abs(ep.Distance - plane.Distance) < 0.1f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;
                exportedPlanes.Add(plane);

                if (plane.Normal.LengthSquared() < 0.1f) continue;
                
                string shaderName = "common/caulk";
                uint contentFlags = 0;
                uint surfaceFlags = 0;

                if (side.ShaderIndex >= 0 && side.ShaderIndex < data.Shaders.Count)
                {
                    var shader = data.Shaders[side.ShaderIndex];
                    if (!string.IsNullOrEmpty(shader.Name) && shader.Name != "noshader")
                    {
                        shaderName = CleanShaderName(shader.Name);
                    }
                    contentFlags = (uint)shader.ContentFlags;
                    surfaceFlags = (uint)shader.SurfaceFlags;
                }

                // Plane points determination
                // Always use GeneratePlanePoints for Radiant stability (widely spaced, projected).
                // Use faces ONLY for solving texture alignment.
                Vector3 p1, p2, p3;
                TextureAlignment alignment = TextureAlignment.Default;
                (p1, p2, p3) = GeneratePlanePoints(plane);

                BspFace? matchedFace = null;
                foreach (var face in data.Faces)
                {
                    if (face.ShaderIndex == side.ShaderIndex && face.NumVertices >= 3)
                    {
                        if (face.FirstVertexIndex < 0 || face.FirstVertexIndex >= data.Vertices.Count) continue;
                        
                        var v0 = data.Vertices[face.FirstVertexIndex].Position;
                        float dist = Vector3.Dot(v0, plane.Normal) - plane.Distance;
                        if (Math.Abs(dist) < 0.1f)
                        {
                            var vn = data.Vertices[face.FirstVertexIndex].Normal;
                            if (Vector3.Dot(vn, plane.Normal) > 0.9f)
                            {
                                matchedFace = face;
                                break;
                            }
                        }
                    }
                }

                if (matchedFace != null)
                {
                    alignment = SolveTextureAlignmentFromFace(data, matchedFace, plane);
                }

                // Format: ( p1 ) ( p2 ) ( p3 ) shader shiftU shiftV rotate scaleU scaleV contentFlags surfaceFlags 0
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, 
                    "{0} {1} {2} {3} {4:0.######} {5:0.######} {6:0.######} {7:0.######} {8:0.######} {9} {10} 0",
                    FormatVector(p1), FormatVector(p2), FormatVector(p3), shaderName,
                    alignment.ShiftU, alignment.ShiftV, alignment.Rotation, alignment.ScaleU, alignment.ScaleV,
                    contentFlags, surfaceFlags));
            }
            
            if (isDetail) writer.WriteLine(" // detail");
            writer.WriteLine("}");
        }

        public void FinalizeExport()
        {
            Console.WriteLine("Map export finalized.");
        }

        private (Vector3, Vector3, Vector3) GeneratePlanePoints(BspPlane plane)
        {
            Vector3 n = plane.Normal;
            if (n.LengthSquared() < 0.0001f) n = Vector3.UnitZ;
            n = Vector3.Normalize(n);
            
            float d = plane.Distance;
            Vector3 origin = n * d;

            // Robust orthonormal basis generation
            Vector3 u;
            if (Math.Abs(n.X) < 0.5f) u = Vector3.UnitX;
            else if (Math.Abs(n.Y) < 0.5f) u = Vector3.UnitY;
            else u = Vector3.UnitZ;

            Vector3 v = Vector3.Normalize(Vector3.Cross(u, n));
            Vector3 w = Vector3.Cross(n, v);

            // Very large scale for stability
            float scale = 4096.0f;
            Vector3 p1 = origin;
            Vector3 p2 = origin + v * scale;
            Vector3 p3 = origin + w * scale;

            // Radiant expects the normal (p2-p1)x(p3-p1) to be INWARD.
            // BSP normals are OUTWARD. So we want (p2-p1)x(p3-p1) . n < 0.
            if (Vector3.Dot(Vector3.Cross(p2 - p1, p3 - p1), n) > 0)
            {
                var temp = p2;
                p2 = p3;
                p3 = temp;
            }

            return (p1, p2, p3);
        }

        private struct TextureAlignment
        {
            public float ShiftU;
            public float ShiftV;
            public float Rotation;
            public float ScaleU;
            public float ScaleV;

            public static TextureAlignment Default => new TextureAlignment { ScaleU = 0.5f, ScaleV = 0.5f };
        }

        private TextureAlignment SolveTextureAlignmentFromFace(BspData data, BspFace face, BspPlane plane)
        {
            GetBaseAxes(plane.Normal, out Vector3 axisU, out Vector3 axisV);
            
            // Need at least 2 vertices to solve for scale/shift
            if (face.NumVertices < 2 || face.FirstVertexIndex < 0 || face.FirstVertexIndex + 1 >= data.Vertices.Count)
                return TextureAlignment.Default;

            var v1 = data.Vertices[face.FirstVertexIndex];
            var v2 = data.Vertices[face.FirstVertexIndex + 1];

            float d1U = Vector3.Dot(v1.Position, axisU);
            float d2U = Vector3.Dot(v2.Position, axisU);
            float d1V = Vector3.Dot(v1.Position, axisV);
            float d2V = Vector3.Dot(v2.Position, axisV);

            float deltaU = v1.SurfaceUV.X - v2.SurfaceUV.X;
            float deltaV = v1.SurfaceUV.Y - v2.SurfaceUV.Y;

            var result = TextureAlignment.Default;

            if (Math.Abs(deltaU) > 0.000001f)
            {
                result.ScaleU = (d1U - d2U) / deltaU;
                result.ShiftU = v1.SurfaceUV.X - (d1U / result.ScaleU);
            }

            if (Math.Abs(deltaV) > 0.000001f)
            {
                result.ScaleV = (d1V - d2V) / deltaV;
                result.ShiftV = v1.SurfaceUV.Y - (d1V / result.ScaleV);
            }
            
            result.ShiftU %= 512.0f;
            result.ShiftV %= 512.0f;

            return result; 
        }

        private void GetBaseAxes(Vector3 normal, out Vector3 axisU, out Vector3 axisV)
        {
            int bestAxis = 0;
            float bestValue = -1.0f;

            for (int i = 0; i < 3; i++)
            {
                float val = Math.Abs(i switch { 0 => normal.X, 1 => normal.Y, 2 => normal.Z, _ => 0 });
                if (val > bestValue)
                {
                    bestValue = val;
                    bestAxis = i;
                }
            }

            if (bestAxis == 0) // X major
            {
                axisU = new Vector3(0, 1, 0);
                axisV = new Vector3(0, 0, -1);
            }
            else if (bestAxis == 1) // Y major
            {
                axisU = new Vector3(1, 0, 0);
                axisV = new Vector3(0, 0, -1);
            }
            else // Z major
            {
                axisU = new Vector3(1, 0, 0);
                axisV = new Vector3(0, -1, 0);
            }
        }

        private string FormatVector(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "( {0:0.######} {1:0.######} {2:0.######} )", v.X, v.Y, v.Z);
        }

        private string CleanShaderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "common/caulk";
            if (name.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(9);
            }
            return name;
        }
    }
}
