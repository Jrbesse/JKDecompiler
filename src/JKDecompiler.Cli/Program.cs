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

    private static void PrintUsage()
    {
        Console.WriteLine("""
            jkdecompiler — JKA RBSP decompiler CLI

            usage:
              jkdecompiler info <bsp>                print lump/entity/lighting summary
              jkdecompiler verify <bsp> [bsp ...]    run structural invariants (exit 1 on errors)
              jkdecompiler extract <bsp> <outdir>    extract lightmaps, face footprints, grid/array, sidecar
              jkdecompiler gen-synthetic <out.bsp>   write a small valid RBSP (CI/fixture use)
            """);
    }
}
