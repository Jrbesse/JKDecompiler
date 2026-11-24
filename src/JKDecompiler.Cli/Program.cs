using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using JKDecompiler.Core;

return RunCli.Run(args);

static class RunCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "info": return Info(args);
                case "verify": return Verify(args);
                case "extract": return Extract(args);
                case "gen-synthetic": return GenSynthetic(args);
                case "convert": return Convert(args);
                case "convert-dir": return ConvertDir(args);
                case "recover-lights": return RecoverLights(args);
                case "fit-lights": return FitLights(args);
                default: PrintUsage(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("JKD_TRACE") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Info(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: jkdecompiler info <bsp>"); return 2; }
        var d = new BspReader().Read(args[1]);
        Console.WriteLine($"=== {Path.GetFileName(args[1])} ({d.FileLength:N0} bytes) ===");
        Console.WriteLine($"entities      : {d.Entities.Count} (light entities: {d.Entities.Count(e => e.ClassName == "light")})");
        Console.WriteLine($"  classnames  : {string.Join(", ", d.Entities.Select(e => e.ClassName).GroupBy(s => s).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}({g.Count()})"))}");
        Console.WriteLine($"shaders       : {d.Shaders.Count}");
        Console.WriteLine($"planes        : {d.Planes.Count}");
        Console.WriteLine($"nodes         : {d.Nodes.Count}");
        Console.WriteLine($"leafs         : {d.Leafs.Count}");
        Console.WriteLine($"models        : {d.Models.Count}");
        Console.WriteLine($"brushes       : {d.Brushes.Count} (world: {d.Models[0].NumBrushes})");
        Console.WriteLine($"drawverts     : {d.Vertices.Count}");
        Console.WriteLine($"faces         : {d.Faces.Count} (planar {d.Faces.Count(f => f.Type == FaceType.Planar)}, patch {d.Faces.Count(f => f.Type == FaceType.Patch)}, soup {d.Faces.Count(f => f.Type == FaceType.TriangleSoup)}, flare {d.Faces.Count(f => f.Type == FaceType.Flare)})");
        Console.WriteLine($"  lit faces   : {d.Faces.Count(f => f.LightmapNum[0] >= 0)} / {d.Faces.Count(f => f.LightmapNum[0] < 0)} unlit");
        Console.WriteLine($"lightmap blocks: {d.Lightmaps.Count} (128x128 RGB)");
        Console.WriteLine($"lightgrid     : {d.LightGrid.Count} cells (30 B each)");
        Console.WriteLine($"lightarray    : {d.LightArray.Data.Length} u16");
        Console.WriteLine($"fogs          : {d.Fogs.Count}");
        Console.WriteLine($"visibility    : {d.Visibility.NumClusters} clusters");
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: jkdecompiler verify <bsp> [bsp ...]"); return 2; }
        int rc = 0;
        foreach (var path in args.Skip(1))
        {
            Console.WriteLine($"=== {Path.GetFileName(path)} ===");
            var d = new BspReader().Read(path);
            var report = BspValidator.Validate(d);
            Console.WriteLine(report);
            if (!report.IsClean) rc = 1;
        }
        return rc;
    }

    private static int Extract(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: jkdecompiler extract <bsp> <outdir>"); return 2; }
        var d = new BspReader().Read(args[1]);
        var report = BspValidator.Validate(d);
        if (!report.IsClean)
        {
            Console.Error.WriteLine("WARNING: validation failed on input, continuing anyway:");
            Console.Error.WriteLine(report.ToString());
        }
        var result = new LightmapExtractor().Extract(d, args[2]);
        Console.WriteLine($"extracted {result.LightmapBlocks} lightmap block(s), {result.FacesWithLightmap} lit face footprint(s), {result.LightGridCells:N0} grid cell(s)");
        Console.WriteLine($"wrote {result.Files.Count} files to {Path.GetFullPath(args[2])}");
        return 0;
    }

    private static int GenSynthetic(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: jkdecompiler gen-synthetic <out.bsp>"); return 2; }
        new BspWriter().Write(SyntheticMap.Build(), args[1]);
        Console.WriteLine($"wrote synthetic RBSP to {Path.GetFullPath(args[1])}");
        return 0;
    }

    private static (string[] pos, bool recompile) SplitArgs(string[] args)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        return (pos, args.Any(a => a == "--recompile"));
    }

    private static string? RecompileLine(ConvertResult r)
    {
        if (r.Recompile == null) return null;
        bool clean = (bool)r.Recompile["clean"];
        string detail = clean
            ? "CLEAN"
            : $"FAILED (rc={r.Recompile["exitCode"]}, {((List<string>)r.Recompile["errors"]!).Count} errors)";
        return $"  recompile: {detail}";
    }

    private static int Convert(string[] args)
    {
        var (pos, recompile) = SplitArgs(args);
        if (pos.Length < 4)
        {
            Console.Error.WriteLine("usage: jkdecompiler convert <bsp> <q3map2-binary> <outdir> [--recompile]");
            Console.Error.WriteLine("  runs: <q3map2> -game ja -convert -format map <bsp> (M2 pipeline)");
            Console.Error.WriteLine("  --recompile: also compile the generated .map back to a BSP (validity proof)");
            return 2;
        }
        var r = new ConvertOrchestrator().Convert(pos[1], pos[2], pos[3], recompile: recompile);
        Console.WriteLine(ConvertLine(r));
        var rc = RecompileLine(r);
        if (rc != null) Console.WriteLine(rc);
        return r.Ok && (r.Recompile == null || (bool)r.Recompile["clean"]) ? 0 : 1;
    }

    private static int ConvertDir(string[] args)
    {
        var (pos, recompile) = SplitArgs(args);
        if (pos.Length < 4)
        {
            Console.Error.WriteLine("usage: jkdecompiler convert-dir <bsp-dir> <q3map2-binary> <outdir> [--recompile]");
            return 2;
        }
        var rs = new ConvertOrchestrator().ConvertDirectory(pos[1], pos[2], pos[3], recompile: recompile);
        foreach (var r in rs)
        {
            Console.WriteLine(ConvertLine(r));
            var rc = RecompileLine(r);
            if (rc != null) Console.WriteLine(rc);
        }
        int ok = rs.Count(r => r.Ok);
        int rcClean = rs.Count(r => r.Recompile != null && (bool)r.Recompile["clean"]);
        int ent = rs.Count(r => r.Original != null && r.Map != null
            && (int)r.Original["entities"]! == (int)r.Map["entities"]!);
        Console.WriteLine($"RESULT: {ok}/{rs.Count} converted, {ent}/{rs.Count} with entity count preserved" +
            (recompile ? $", {rcClean}/{rs.Count} recompile-clean" : ""));
        Console.WriteLine($"summary: {Path.GetFullPath(Path.Combine(pos[3], "convert_summary.json"))}");
        return ok == rs.Count ? 0 : 1;
    }

    /// <summary>
    /// Positional args (command word removed). Values of known flags must NOT
    /// leak into the positional list — a bare !StartsWith("--") filter leaves
    /// them behind and shifts every positional after them (a --seeds value
    /// lands in the scratch-dir slot, e.g.).
    /// </summary>
    static string[] SplitPositional(string[] args, string[] valueFlags)
    {
        var pos = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (valueFlags.Contains(args[i])) { i++; continue; } // skip flag + its value
            if (!args[i].StartsWith("--")) pos.Add(args[i]);
        }
        return pos.ToArray();
    }

    private static int OptI(string[] args, string name, int dflt)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : dflt;
    }

    private static float OptF(string[] args, string name, float dflt)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], out float v) ? v : dflt;
    }

    private static string OptS(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }

    /// <summary>
    /// M2.5 stage 2: closed-loop light fit — recover lights (stage 1), then
    /// re-bake with q3map2 and least-squares fit intensity/color (and positions)
    /// against the original engine's lightmap blocks. Requires an M2 .map as the
    /// geometry base: <c>jkdecompiler convert</c> first.
    /// </summary>
    private static int FitLights(string[] args)
    {
        var pos = SplitPositional(args, new[] { "--max-seeds", "--parallel", "--pos-step", "--lm-size", "--seeds", "--sky-value" });
        if (pos.Length < 5)
        {
            Console.Error.WriteLine("usage: jkdecompiler fit-lights <orig.bsp> <base.map> <q3map2-binary> <game-dir> <outdir> [scratch-dir]");
            Console.Error.WriteLine("  M2.5 stage 2: closed-loop fit of recovered lights against the baked lightmap");
            Console.Error.WriteLine("  options: [--max-seeds N] [--parallel N] [--no-posfit] [--pos-step F] [--jka-format] [--lm-size N] [--sky-value N]");
            return 2;
        }
        if (!File.Exists(pos[1]))
        {
            Console.Error.WriteLine($"base map not found: {pos[1]} — run 'jkdecompiler convert' first");
            return 2;
        }
        var d = new BspReader().Read(pos[0]);
        string scratch = pos.Length >= 6 ? pos[5] : Path.Combine(pos[4], "scratch");
        var fit = new LightFit(pos[2], pos[3], new LightFitOptions
        {
            MaxSeeds = OptI(args, "--max-seeds", 0),
            Parallel = OptI(args, "--parallel", 2),
            PosFit = !args.Any(a => a == "--no-posfit"),
            RadiusFit = !args.Any(a => a == "--no-radfit"),
            PosStep = OptF(args, "--pos-step", 8f),
            WriteJkaFormat = args.Any(a => a == "--jka-format"),
            LightmapSize = OptI(args, "--lm-size", 128),
            SkyLightValue = OptF(args, "--sky-value", (float)SkyboxReconstruction.DefaultSkyLightValue),
            SeedsPath = OptS(args, "--seeds"),
        });
        string mapLabel = Path.GetFileNameWithoutExtension(pos[0]);
        Console.WriteLine($"=> {mapLabel}: fitting lights against baked lightmap blocks ({pos[2]})");
        var r = fit.Fit(d, pos[1], pos[4], scratch, mapLabel);
        Console.WriteLine($"  points: {r.MatchedPoints} matched / {r.UnmatchedPoints} unmatched");
        Console.WriteLine($"  error (area-weighted L1/channel): ambient-only={r.ErrorAmbientOnly:F2}  after fit={r.ErrorStageA:F2}  final={r.ErrorFinal:F2}" +
            (r.PositionsRolledBack ? "  (positions rolled back)" : ""));
        var kept = r.Lights.Where(l => l.Kept).ToList();
        Console.WriteLine($"  lights: kept {kept.Count} / dropped {r.Lights.Count - kept.Count} / compiles {r.CompilesRun} / {r.ElapsedMs / 1000.0:F1}s");
        foreach (var l in kept)
            Console.WriteLine($"  kept  {l.Name}: pos=({l.Position.X:0.#} {l.Position.Y:0.#} {l.Position.Z:0.#}) " +
                $"intensity={l.Intensity:0.#} color=({(int)(l.Color01.R * 255)} {(int)(l.Color01.G * 255)} {(int)(l.Color01.B * 255)}) " +
                $"contrib={l.ContributionPct:F1}% dE={l.DeltaE:F2} moved={l.PositionMoved}");
        foreach (var l in r.Lights.Where(l => !l.Kept).Take(10))
            Console.WriteLine($"  drop  {l.Name}: {l.DropReason}");
        if (r.Lights.Count(l => !l.Kept) > 10)
            Console.WriteLine($"  ... and {r.Lights.Count(l => !l.Kept) - 10} more dropped");
        if (!string.IsNullOrEmpty(r.Note)) Console.WriteLine($"  note: {r.Note}");
        var arts = new[] { "fit.map", "fit_report.json", "fit_lights_overlay.png" };
        if (r.DiffWritten) arts = arts.Append("lightmap_diff.png").ToArray();
        Console.WriteLine($"wrote {string.Join(" + ", arts)} to {Path.GetFullPath(pos[4])}");
        return r.ErrorFinal < r.ErrorAmbientOnly ? 0 : 1;
    }

    /// <summary>
    /// M2.5 stage 1: grid seeding — recover light candidates from the baked
    /// lightgrid (directions + directed colors), emit target_light/light
    /// pairs as a .map fragment + a sidecar + a top-down overlay.
    /// </summary>
    private static int RecoverLights(string[] args)
    {
        var pos = SplitPositional(args, new[] { "--min-cells", "--max-ray-dist", "--directed-min" });
        if (pos.Length < 2)
        {
            Console.Error.WriteLine("usage: jkdecompiler recover-lights <bsp> <outdir> [--min-cells N] [--max-ray-dist F] [--directed-min F]");
            Console.Error.WriteLine("  M2.5 stage 1: grid seeding — recover light entities from the baked lightgrid");
            return 2;
        }
        var d = new BspReader().Read(pos[0]);
        var lr = new LightRecovery
        {
            MinCells = OptI(args, "--min-cells", 6),
            MaxRayDist = OptF(args, "--max-ray-dist", 32f),
            DirectedMin = OptF(args, "--directed-min", 5f),
        };
        var r = lr.Recover(d);

        Directory.CreateDirectory(pos[1]);
        LightRecovery.WriteMapFragment(r, Path.GetFileName(pos[0]), Path.Combine(pos[1], "recovered_lights.map"));
        LightRecovery.WriteOverlay(r, d, Path.Combine(pos[1], "recovered_lights_overlay.png"));
        var sidecar = new Dictionary<string, object?>
        {
            ["map"] = Path.GetFileName(pos[0]),
            ["stage"] = "M2.5 stage 1 (grid seeding); stage 2 refits intensity/color against the baked blocks",
            ["grid"] = new
            {
                valid = r.GridValid,
                note = r.GridNote,
                dims = r.GridDims,
                origin = new[] { r.GridOrigin.X, r.GridOrigin.Y, r.GridOrigin.Z },
                corners = r.Corners,
                paletteCells = r.PaletteCells
            },
            ["samples"] = new { used = r.SamplesUsed, skippedFaint = r.SamplesSkippedFaint },
            ["accepted"] = r.Lights.Select(l => new
            {
                name = l.Name,
                position = new[] { l.Position.X, l.Position.Y, l.Position.Z },
                level = l.Level,
                levelLowerBound = l.LevelLowerBound,
                radius = l.Radius,
                color = new[] { (int)l.Color.X, (int)l.Color.Y, (int)l.Color.Z },
                style = l.Style,
                cells = l.CellCount,
                rayDistMedian = l.RayDistMedian,
                tPositiveFrac = l.TPositiveFrac,
                levelConsistency = l.LevelConsistency,
                angleMedianDeg = l.AngleMedianDeg,
                angleP90Deg = l.AngleP90Deg
            }).ToList(),
            ["rejected"] = r.Rejected.Select(rc => new
            {
                cells = rc.CellCount,
                reason = rc.Reason,
                position = new[] { rc.Position.X, rc.Position.Y, rc.Position.Z },
                rayDistMedian = rc.RayDistMedian,
                tPositiveFrac = rc.TPositiveFrac
            }).ToList()
        };
        File.WriteAllText(Path.Combine(pos[1], "recovered_lights.json"),
            JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"=> {Path.GetFileName(pos[0])}");
        if (!r.GridValid)
        {
            Console.WriteLine($"grid INVALID: {r.GridNote}");
            Console.WriteLine($"wrote (empty) fragment + sidecar to {Path.GetFullPath(pos[1])}");
            return 1;
        }
        Console.WriteLine($"grid {r.GridDims[0]}x{r.GridDims[1]}x{r.GridDims[2]} (corners {r.Corners}, palette {r.PaletteCells}) — samples {r.SamplesUsed}, faint skipped {r.SamplesSkippedFaint}");
        foreach (var l in r.Lights)
            Console.WriteLine($"  {l.Name}: pos=({l.Position.X:0.#} {l.Position.Y:0.#} {l.Position.Z:0.#}) " +
                $"level={l.Level?.ToString() ?? "saturated"} color=({(int)l.Color.X} {(int)l.Color.Y} {(int)l.Color.Z}) " +
                $"style={l.Style} cells={l.CellCount} rayMed={l.RayDistMedian:0.#} t+={l.TPositiveFrac:P0}");
        foreach (var rc in r.Rejected)
            Console.WriteLine($"  rejected: {rc.CellCount} cells — {rc.Reason}");
        Console.WriteLine($"accepted {r.Lights.Count}/{r.Lights.Count + r.Rejected.Count} clusters");
        Console.WriteLine($"wrote recovered_lights.map + recovered_lights_overlay.png + recovered_lights.json to {Path.GetFullPath(pos[1])}");
        return 0;
    }

    private static string ConvertLine(ConvertResult r)
    {
        string name = Path.GetFileName(r.SourceBsp);
        string orig = r.Original != null && r.Original.ContainsKey("entities")
            ? $"in  {r.Original["entities"]}e/{r.Original["patches"]}p/{r.Original["brushes"]}b" : "in  -";
        string map = r.Map != null
            ? $"out {r.Map["entities"]}e/{r.Map["patches"]}p/{r.Map["brushes"]}b" : "out -";
        string status = r.Ok ? "OK " : $"ERR(rc={r.Q3map2ExitCode})";
        return $"{status} {name}: {orig} {map} {r.Warnings.Count} warning(s) {r.ElapsedMs}ms";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            jkdecompiler — JKA RBSP decompiler CLI

            usage:
              jkdecompiler info <bsp>                print lump/entity/lighting summary
              jkdecompiler verify <bsp> [bsp ...]    run structural invariants (exit 1 on errors)
              jkdecompiler extract <bsp> <outdir>    extract lightmaps, face footprints, grid/array, sidecar
              jkdecompiler gen-synthetic <out.bsp>   write a small valid RBSP (CI/fixture use)
              jkdecompiler convert <bsp> <q3map2> <out>     M2: q3map2 -convert -> .map + report
              jkdecompiler convert-dir <dir> <q3map2> <out> M2: batch over a .bsp directory
              jkdecompiler recover-lights <bsp> <out>       M2.5s1: recover lights from the lightgrid
              jkdecompiler fit-lights <bsp> <map> <q3map2> <gamedir> <out> [scratch]  M2.5s2: closed-loop fit vs baked lightmap
            """);
    }
}
