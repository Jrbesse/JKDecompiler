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
            writer.WriteLine($"// entity {index}");
            writer.WriteLine("{");

            foreach (var kv in entity.KeyValues)
            {
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

            writer.WriteLine($"// patch {index}");
            writer.WriteLine("{");
            writer.WriteLine(" patchDef2");
            writer.WriteLine(" {");

            var shader = data.Shaders[face.ShaderIndex];
            writer.WriteLine($"  {shader.Name}");
            writer.WriteLine($"  ( {face.PatchWidth} {face.PatchHeight} 0 0 0 )");
            writer.WriteLine("  (");

            for (int w = 0; w < face.PatchWidth; w++)
            {
                writer.Write("   ( ");
                for (int h = 0; h < face.PatchHeight; h++)
                {
                    // BSP stores patches in a flat array, but Radiant expects a specific grid layout
                    // Depending on how JKA orders them, we might need to transpose h and w.
                    // Usually it's [h * width + w]
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
                ExportBrush(data, brush, writer, i);
            }
        }

        private void ExportBrush(BspData data, BspBrush brush, StreamWriter writer, int index)
        {
            writer.WriteLine($"// brush {index}");
            writer.WriteLine("{");

            bool isDetail = false;
            for (int i = 0; i < brush.NumSides; i++)
            {
                var side = data.BrushSides[brush.FirstSide + i];
                if (side.ShaderIndex >= 0 && side.ShaderIndex < data.Shaders.Count)
                {
                    // SURF_DETAIL is 0x8000000
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
                
                string shaderName = "common/caulk";
                if (side.ShaderIndex >= 0 && side.ShaderIndex < data.Shaders.Count)
                    shaderName = data.Shaders[side.ShaderIndex].Name;

                // Attempt to find a face that matches this plane to solve for UVs
                var alignment = SolveTextureAlignment(data, side, plane);

                var (p1, p2, p3) = GenerateThreePointsOnPlane(plane);

                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, 
                    "{0} {1} {2} {3} {4:0.######} {5:0.######} {6:0.######} {7:0.######} {8:0.######} 0 0 0",
                    FormatVector(p1), FormatVector(p2), FormatVector(p3), shaderName,
                    alignment.ShiftU, alignment.ShiftV, alignment.Rotation, alignment.ScaleU, alignment.ScaleV));
            }
            
            if (isDetail) writer.WriteLine(" // detail");

            writer.WriteLine("}");
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

        private TextureAlignment SolveTextureAlignment(BspData data, BspBrushSide side, BspPlane plane)
        {
            // Find a face that uses this shader and is on this plane
            // This is a heuristic: BSP faces are linked to shaders, but not directly to brush sides.
            // However, we can search for a face that has matching normal and shader.
            BspFace? bestMatch = null;
            foreach (var face in data.Faces)
            {
                if (face.ShaderIndex == side.ShaderIndex)
                {
                    if (face.FirstVertexIndex < 0 || face.FirstVertexIndex >= data.Vertices.Count) continue;
                    
                    // Check if the face vertices are on this plane
                    var v = data.Vertices[face.FirstVertexIndex];
                    float dist = Vector3.Dot(v.Position, plane.Normal) - plane.Distance;
                    if (Math.Abs(dist) < 0.1f)
                    {
                        bestMatch = face;
                        break;
                    }
                }
            }

            if (bestMatch == null || bestMatch.NumVertices < 3)
                return TextureAlignment.Default;

            GetBaseAxes(plane.Normal, out Vector3 axisU, out Vector3 axisV);

            // Using two vertices to solve for Scale and Shift (assuming 0 rotation for now)
            // U = (Pos . AxisU) / ScaleU + ShiftU
            // U1 = D1/S + H => S = (D1-D2)/(U1-U2)
            var v1 = data.Vertices[bestMatch.FirstVertexIndex];
            var v2 = data.Vertices[bestMatch.FirstVertexIndex + 1];

            float d1U = Vector3.Dot(v1.Position, axisU);
            float d2U = Vector3.Dot(v2.Position, axisU);
            float d1V = Vector3.Dot(v1.Position, axisV);
            float d2V = Vector3.Dot(v2.Position, axisV);

            float deltaU = v1.SurfaceUV.X - v2.SurfaceUV.X;
            float deltaV = v1.SurfaceUV.Y - v2.SurfaceUV.Y;

            var result = TextureAlignment.Default;

            if (Math.Abs(deltaU) > 0.0001f)
            {
                result.ScaleU = (d1U - d2U) / deltaU;
                result.ShiftU = v1.SurfaceUV.X - (d1U / result.ScaleU);
            }

            if (Math.Abs(deltaV) > 0.0001f)
            {
                result.ScaleV = (d1V - d2V) / deltaV;
                result.ShiftV = v1.SurfaceUV.Y - (d1V / result.ScaleV);
            }
            
            return result; 
        }

        private void GetBaseAxes(Vector3 normal, out Vector3 axisU, out Vector3 axisV)
        {
            // Standard Quake 3 / Radiant base axes logic
            // Find the axis with the smallest absolute normal component
            int axis = 0;
            float best = 1.0f;

            for (int i = 0; i < 3; i++)
            {
                float val = Math.Abs(i switch { 0 => normal.X, 1 => normal.Y, 2 => normal.Z, _ => 0 });
                if (val < best)
                {
                    best = val;
                    axis = i;
                }
            }

            // This is a simplification of the full Radiant table-based logic
            if (Math.Abs(normal.Z) > 0.70710678f) // Mostly Vertical
            {
                axisU = new Vector3(1, 0, 0);
                axisV = new Vector3(0, -1, 0);
            }
            else if (Math.Abs(normal.X) > 0.70710678f) // Mostly East/West
            {
                axisU = new Vector3(0, 1, 0);
                axisV = new Vector3(0, 0, -1);
            }
            else // Mostly North/South
            {
                axisU = new Vector3(1, 0, 0);
                axisV = new Vector3(0, 0, -1);
            }
        }

        private string FormatVector(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "( {0:0.######} {1:0.######} {2:0.######} )", v.X, v.Y, v.Z);
        }

        private (Vector3, Vector3, Vector3) GenerateThreePointsOnPlane(BspPlane plane)
        {
            Vector3 n = plane.Normal;
            float d = plane.Distance;

            // Find a vector not parallel to n
            Vector3 v = Math.Abs(n.X) < 0.7f ? Vector3.UnitX : Vector3.UnitY;

            Vector3 u = Vector3.Normalize(Vector3.Cross(v, n));
            Vector3 w = Vector3.Cross(n, u);

            Vector3 origin = n * d;
            Vector3 p1 = origin;
            Vector3 p2 = origin + u * 64;
            Vector3 p3 = origin + w * 64;

            return (p1, p2, p3);
        }
    }
}
