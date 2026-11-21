using System;
using System.Collections.Generic;
using System.Linq;

namespace JKDecompiler.Core
{
    public class BspValidationReport
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool IsClean => Errors.Count == 0;

        public void Error(string msg) => Errors.Add(msg);
        public void Warning(string msg) => Warnings.Add(msg);

        public override string ToString()
        {
            var lines = new List<string> { IsClean ? "RESULT: OK" : $"RESULT: FAILED ({Errors.Count} error(s), {Warnings.Count} warning(s))" };
            foreach (var e in Errors) lines.Add($"  ERROR   {e}");
            foreach (var w in Warnings) lines.Add($"  WARNING {w}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Structural invariants for a parsed RBSP. These are the checks the
    /// old project never had — they catch the class of bug (wrong record
    /// sizes) that silently produced garbage.
    /// </summary>
    public static class BspValidator
    {
        public static BspValidationReport Validate(BspData d)
        {
            var r = new BspValidationReport();

            ValidateLumpLayout(d, r);
            ValidateNodes(d, r);
            ValidateLeafs(d, r);
            ValidateModels(d, r);
            ValidateBrushes(d, r);
            ValidateFaces(d, r);
            ValidateLightmaps(d, r);
            ValidateLightGrid(d, r);
            ValidateEntities(d, r);
            return r;
        }

        private static void ValidateLumpLayout(BspData d, BspValidationReport r)
        {
            // record-size divisibility (reader enforces at parse time; re-check for
            // data assembled in memory, e.g. by tests or the M5 re-injector)
            var expected = new (int lump, int length)[]
            {
                (JkaBsp.LUMP_SHADERS, d.Shaders.Count * JkaBsp.SIZE_SHADER),
                (JkaBsp.LUMP_PLANES, d.Planes.Count * JkaBsp.SIZE_PLANE),
                (JkaBsp.LUMP_NODES, d.Nodes.Count * JkaBsp.SIZE_NODE),
                (JkaBsp.LUMP_LEAFS, d.Leafs.Count * JkaBsp.SIZE_LEAF),
                (JkaBsp.LUMP_MODELS, d.Models.Count * JkaBsp.SIZE_MODEL),
                (JkaBsp.LUMP_BRUSHES, d.Brushes.Count * JkaBsp.SIZE_BRUSH),
                (JkaBsp.LUMP_BRUSHSIDES, d.BrushSides.Count * JkaBsp.SIZE_BRUSHSIDE),
                (JkaBsp.LUMP_DRAWVERTS, d.Vertices.Count * JkaBsp.SIZE_MAPVERT),
                (JkaBsp.LUMP_FOGS, d.Fogs.Count * JkaBsp.SIZE_FOG),
                (JkaBsp.LUMP_SURFACES, d.Faces.Count * JkaBsp.SIZE_SURFACE),
                (JkaBsp.LUMP_LIGHTMAPS, d.Lightmaps.Count * JkaBsp.LIGHTMAP_BLOCK_BYTES),
                (JkaBsp.LUMP_LIGHTGRID, d.LightGrid.Count * JkaBsp.SIZE_GRID_CELL),
                (JkaBsp.LUMP_LIGHTARRAY, d.LightArray.Data.Length * 2)
            };
            foreach (var (lump, len) in expected)
            {
                if (d.Lumps.Length > lump && d.Lumps[lump].Length != len)
                    r.Error($"lump {lump}: declared length {d.Lumps[lump].Length} != {len} from parsed record count");
            }
            if (d.Models.Count == 0) r.Error("no models (model 0 must be the world)");
            if (d.Leafs.Count == 0) r.Error("no leafs");
            if (d.Planes.Count == 0) r.Warning("no planes");
        }

        private static void ValidateNodes(BspData d, BspValidationReport r)
        {
            int bad = 0;
            for (int i = 0; i < d.Nodes.Count; i++)
            {
                var n = d.Nodes[i];
                if (n.PlaneIndex < 0 || n.PlaneIndex >= d.Planes.Count) { bad++; if (bad <= 5) r.Error($"node {i}: planeIndex {n.PlaneIndex} out of range [0,{d.Planes.Count})"); }
                for (int a = 0; a < 2; a++)
                {
                    int c = n.Children[a];
                    if (c >= 0 && c >= d.Nodes.Count) { bad++; if (bad <= 10) r.Error($"node {i}.child{a}: {c} >= node count {d.Nodes.Count}"); }
                    else if (c < 0 && -(c + 1) >= d.Leafs.Count) { bad++; if (bad <= 10) r.Error($"node {i}.child{a}: leaf ref {c} out of range (leafs={d.Leafs.Count})"); }
                }
            }
            if (bad > 10) r.Error($"nodes: {bad} bad references total (showing first 10)");
        }

        private static void ValidateLeafs(BspData d, BspValidationReport r)
        {
            int bad = 0;
            int maxSurf = 0, maxBrush = 0;
            for (int i = 0; i < d.Leafs.Count; i++)
            {
                var l = d.Leafs[i];
                if (l.FirstLeafFace < 0 || l.FirstLeafFace + l.NumLeafFaces > d.LeafFaces.Count)
                    { bad++; if (bad <= 5) r.Error($"leaf {i}: face range [{l.FirstLeafFace},{l.FirstLeafFace + l.NumLeafFaces}) outside leafsurfs ({d.LeafFaces.Count})"); }
                if (l.FirstLeafBrush < 0 || l.FirstLeafBrush + l.NumLeafBrushes > d.LeafBrushes.Count)
                    { bad++; if (bad <= 5) r.Error($"leaf {i}: brush range [{l.FirstLeafBrush},{l.FirstLeafBrush + l.NumLeafBrushes}) outside leafbrushes ({d.LeafBrushes.Count})"); }
                maxSurf = Math.Max(maxSurf, l.FirstLeafFace + l.NumLeafFaces);
                maxBrush = Math.Max(maxBrush, l.FirstLeafBrush + l.NumLeafBrushes);
            }
            if (bad > 5) r.Error($"leafs: {bad} bad ranges total (showing first 5)");
            if (maxSurf < d.LeafFaces.Count) r.Warning($"leafsurfs: {d.LeafFaces.Count - maxSurf} entries not reachable from any leaf");
            if (maxBrush < d.LeafBrushes.Count) r.Warning($"leafbrushes: {d.LeafBrushes.Count - maxBrush} entries not reachable from any leaf");
        }

        private static void ValidateModels(BspData d, BspValidationReport r)
        {
            for (int i = 0; i < d.Models.Count; i++)
            {
                var m = d.Models[i];
                if (m.FirstFace < 0 || m.FirstFace + m.NumFaces > d.Faces.Count)
                    r.Error($"model {i}: face range [{m.FirstFace},{m.FirstFace + m.NumFaces}) outside faces ({d.Faces.Count})");
                if (m.FirstBrush < 0 || m.FirstBrush + m.NumBrushes > d.Brushes.Count)
                    r.Error($"model {i}: brush range [{m.FirstBrush},{m.FirstBrush + m.NumBrushes}) outside brushes ({d.Brushes.Count})");
            }
        }

        private static void ValidateBrushes(BspData d, BspValidationReport r)
        {
            int bad = 0;
            for (int i = 0; i < d.Brushes.Count; i++)
            {
                var b = d.Brushes[i];
                if (b.NumSides < 3) { bad++; if (bad <= 5) r.Error($"brush {i}: numSides {b.NumSides} < 3"); }
                if (b.FirstSide < 0 || b.FirstSide + b.NumSides > d.BrushSides.Count)
                    { bad++; if (bad <= 5) r.Error($"brush {i}: side range outside brushsides ({d.BrushSides.Count})"); }
                if (b.ShaderIndex < 0 || b.ShaderIndex >= d.Shaders.Count)
                    { bad++; if (bad <= 5) r.Error($"brush {i}: shaderIndex {b.ShaderIndex} out of range"); }
            }
            for (int i = 0; i < d.BrushSides.Count; i++)
            {
                var s = d.BrushSides[i];
                if (s.PlaneIndex < 0 || s.PlaneIndex >= d.Planes.Count)
                    { bad++; if (bad <= 10) r.Error($"brushside {i}: planeIndex {s.PlaneIndex} out of range"); }
                if (s.ShaderIndex < 0 || s.ShaderIndex >= d.Shaders.Count)
                    { bad++; if (bad <= 10) r.Error($"brushside {i}: shaderIndex {s.ShaderIndex} out of range"); }
            }
            if (bad > 10) r.Error($"brushes: {bad} bad references total (showing first 10)");
        }

        private static void ValidateFaces(BspData d, BspValidationReport r)
        {
            int bad = 0;
            for (int i = 0; i < d.Faces.Count; i++)
            {
                var f = d.Faces[i];
                if (f.ShaderIndex < 0 || f.ShaderIndex >= d.Shaders.Count)
                    { bad++; if (bad <= 5) r.Error($"face {i}: shaderIndex {f.ShaderIndex} out of range [0,{d.Shaders.Count})"); }
                if (f.FogIndex != -1 && (f.FogIndex < 0 || f.FogIndex >= d.Fogs.Count))
                    r.Error($"face {i}: fogIndex {f.FogIndex} out of range [0,{d.Fogs.Count})");
                if (f.Type == FaceType.Bad)
                    { bad++; if (bad <= 5) r.Error($"face {i}: surfaceType 0 (bad)"); }

                if (f.FirstVertexIndex < 0 || f.FirstVertexIndex + f.NumVertices > d.Vertices.Count)
                {
                    bad++;
                    if (bad <= 10)
                        r.Error($"face {i}: vertex range [{f.FirstVertexIndex},{f.FirstVertexIndex + f.NumVertices}) outside drawverts ({d.Vertices.Count}) — classic wrong-record-size symptom");
                    continue; // vertex-range check below depends on valid range
                }
                if (f.Type == FaceType.Planar && f.NumVertices < 3)
                    r.Error($"face {i}: planar face with {f.NumVertices} vertices");
                if (f.FirstMeshVertIndex < 0 || f.FirstMeshVertIndex + f.NumMeshVerts > d.MeshVerts.Count)
                    { bad++; if (bad <= 10) r.Error($"face {i}: index range [{f.FirstMeshVertIndex},{f.FirstMeshVertIndex + f.NumMeshVerts}) outside drawindexes ({d.MeshVerts.Count})"); }

                if (f.Type == FaceType.Patch)
                {
                    if (f.PatchWidth < 2 || f.PatchHeight < 2)
                        r.Error($"face {i}: patch dims {f.PatchWidth}x{f.PatchHeight} invalid");
                    // JKA stores control points per axis; the engine reads
                    // exactly patchWidth * patchHeight verts (tr_bsp.cpp
                    // ParseMesh: numPoints = width * height).
                    int expected = f.PatchWidth * f.PatchHeight;
                    if (f.NumVertices != expected)
                        r.Error($"face {i}: patch has {f.NumVertices} control verts, expected patchWidth*patchHeight={expected}");
                }
            }
            if (bad > 10) r.Error($"faces: {bad} bad references total (showing first 10)");
        }

        private static void ValidateLightmaps(BspData d, BspValidationReport r)
        {
            if (d.Lightmaps.Count == 0)
                r.Warning("no lightmap blocks — map is fully unlit (rare for compiled JKA maps)");

            int badUv = 0, unlit = 0, style0 = 0;
            for (int i = 0; i < d.Faces.Count; i++)
            {
                var f = d.Faces[i];
                if (f.LightmapNum[0] < 0) { unlit++; continue; }
                if (f.LightmapNum[0] >= d.Lightmaps.Count)
                    { badUv++; if (badUv <= 5) r.Error($"face {i}: lightmap {f.LightmapNum[0]} out of range [0,{d.Lightmaps.Count})"); continue; }

                if (f.LightmapStyles[0] >= 0xfe) style0++; // 0xfe unused / 0xff none

                for (int lm = 0; lm < JkaBsp.MAX_LIGHTMAPS; lm++)
                {
                    if (f.LightmapNum[lm] < 0 || f.LightmapNum[lm] >= d.Lightmaps.Count) continue;
                    for (int v = f.FirstVertexIndex; v < f.FirstVertexIndex + f.NumVertices && v < d.Vertices.Count; v++)
                    {
                        float u = d.Vertices[v].GetLightmapU(lm);
                        float vv = d.Vertices[v].GetLightmapV(lm);
                        // JKA vertex lightmap UVs are 0..1 block-relative
                        // (fed straight to GL, tr_surface.cpp:1446)
                        if (u < -0.01f || u > 1.01f || vv < -0.01f || vv > 1.01f)
                        {
                            badUv++;
                            if (badUv <= 5)
                                r.Error($"face {i} vert {v}: lightmap UV ({u:F4},{vv:F4}) outside 0..1 block UV space (block {f.LightmapNum[lm]})");
                            break; // one report per face per lightmap is enough
                        }
                    }
                }

                if (f.Type == FaceType.Planar)
                {
                    // JKA planar faces often carry the unassigned-rect marker
                    // (-3, 0, 0, 0) — the real UVs live in the vertices. Only
                    // validate the rect when one is actually set.
                    if (f.LightmapWidth > 0 && f.LightmapHeight > 0 && f.LightmapX[0] >= 0 && f.LightmapY[0] >= 0)
                    {
                        if (f.LightmapX[0] + f.LightmapWidth > 128 || f.LightmapY[0] + f.LightmapHeight > 128)
                            r.Error($"face {i}: lightmap rect ({f.LightmapX[0]},{f.LightmapY[0]},{f.LightmapWidth}x{f.LightmapHeight}) outside 128x128 block");
                    }
                }
            }
            if (badUv > 5) r.Error($"lightmaps: {badUv} bad UV/lightmap refs total (showing first 5)");
            if (unlit > 0) r.Warning($"{unlit}/{d.Faces.Count} faces unlit (lightmapNum[0] < 0)");
            if (style0 > 0) r.Warning($"{style0} faces with lightmap style 0xfe/0xff (unlit-by-style)");
        }

        /// <summary>
        /// JKA lightgrid (verified in OpenJK tr_bsp.cpp / tr_light.cpp):
        /// - world grid is stepped at (64, 64, 128) per axis, origin derived
        ///   from model-0 bounds (R_LoadLightGrid).
        /// - lump 15 is a PALETTE of unique 30 B cells.
        /// - lump 17 (lightarray) holds one u16 per grid cell: an INDEX into
        ///   the palette (tr_light.cpp: `data = lightGridData + *gridPos`).
        /// The engine's own check: array length must equal the bounds product,
        /// else it disables the whole grid ("light grid array mismatch").
        /// </summary>
        private static void ValidateLightGrid(BspData d, BspValidationReport r)
        {
            if (d.Models.Count == 0) return;
            var m0 = d.Models[0];
            float[] size = { 64f, 64f, 128f };
            float[] mn = { m0.Mins.X, m0.Mins.Y, m0.Mins.Z };
            float[] mx = { m0.Maxs.X, m0.Maxs.Y, m0.Maxs.Z };
            int bw = 0, bh = 0, bv = 0;
            for (int a = 0; a < 3; a++)
            {
                float origin = size[a] * (float)Math.Ceiling(mn[a] / size[a]);
                float maxs = size[a] * (float)Math.Floor(mx[a] / size[a]);
                int b = (int)((maxs - origin) / size[a]) + 1;
                if (a == 0) bw = b; else if (a == 1) bh = b; else bv = b;
            }
            int expected = bw * bh * bv;

            if (d.LightArray.Data.Length != expected)
                // mirrors the engine: PRINT_WARNING "light grid array mismatch",
                // then the whole grid is disabled (no crash, no sampling).
                // Seen on OF-era maps compiled with a different grid step.
                r.Warning($"lightarray: {d.LightArray.Data.Length} u16 indices, expected bounds product {expected} ({bw}x{bh}x{bv} at 64/64/128 steps) — engine logs 'light grid array mismatch' and DISABLES dynamic grid lighting on this map");
            if (d.LightGrid.Count == 0)
                r.Warning("lightgrid palette empty (0 cells) — dynamic lighting disabled");
            else if (d.LightArray.Data.Length == expected)
            {
                // OOB checks only matter when the engine will actually sample
                int oob = 0, firstOobValue = -1;
                foreach (var idx in d.LightArray.Data)
                {
                    if (idx >= d.LightGrid.Count)
                    {
                        oob++;
                        if (firstOobValue < 0) firstOobValue = idx;
                    }
                }
                if (oob > 0)
                    r.Error($"lightarray: {oob} cell index(es) >= palette size {d.LightGrid.Count} (first value {firstOobValue}) — engine would read out of bounds");
            }
        }

        private static void ValidateEntities(BspData d, BspValidationReport r)
        {
            if (d.Entities.Count == 0)
            {
                r.Error("no entities parsed from entity lump");
                return;
            }
            int noClass = d.Entities.Count(e => string.IsNullOrEmpty(e.ClassName));
            if (noClass > 0) r.Warning($"{noClass} entities without classname");
            int worldspawns = d.Entities.Count(e => e.ClassName.Equals("worldspawn", StringComparison.OrdinalIgnoreCase));
            if (worldspawns != 1) r.Error($"{worldspawns} worldspawn entities (expected exactly 1)");
            int lights = d.Entities.Count(e => e.ClassName.Equals("light", StringComparison.OrdinalIgnoreCase));
            if (lights == 0)
                r.Warning("0 light entities — expected for compiled JKA BSPs (Raven's compiler strips them; lighting lives in the baked lightmaps)");
        }
    }
}
