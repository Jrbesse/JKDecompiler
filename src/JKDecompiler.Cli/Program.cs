using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
                default: PrintUsage(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
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
            """);
    }
}
