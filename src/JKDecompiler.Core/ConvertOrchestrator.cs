using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JKDecompiler.Core
{
    /// <summary>
    /// Structural stats of a .map file. Top-level entities are counted by
    /// brace depth (an entity is a depth-0 '{'); patchDef2/brushDef are the
    /// JA/Q3 map-file keywords for patches and brushes. Raw counts — enough
    /// for the M2 convert report, not a full .map parser.
    /// </summary>
    public class MapFileStats
    {
        public long SizeBytes { get; set; }
        public int Entities { get; set; }
        public int Patches { get; set; }
        public int Brushes { get; set; }
        public int BrushSides { get; set; }
        public bool BracesBalanced { get; set; }

        public static MapFileStats Parse(string path)
        {
            var text = File.ReadAllText(path);
            int depth = 0, entities = 0;
            bool balanced = true;
            foreach (var ch in text)
            {
                if (ch == '{')
                {
                    if (depth == 0) entities++;
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth < 0) { balanced = false; break; }
                }
            }
            if (depth != 0) balanced = false;

            // keyword counts: count only occurrences that start a token
            // (q3map2 emits 'patchDef2' / 'brushDef' as their own words)
            int CountWord(string word)
            {
                int n = 0;
                for (int i = 0; i + word.Length <= text.Length; i++)
                {
                    if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0) continue;
                    bool leftOk = i == 0 || (char.IsWhiteSpace(text[i - 1]) || text[i - 1] == '{');
                    int j = i + word.Length;
                    bool rightOk = j >= text.Length || char.IsWhiteSpace(text[j]);
                    if (leftOk && rightOk) { n++; i = j - 1; }
                }
                return n;
            }

            return new MapFileStats
            {
                SizeBytes = new FileInfo(path).Length,
                Entities = entities,
                Patches = CountWord("patchDef2"),
                // two brush syntaxes in the wild: netradiant emits 'brushDef',
                // q3map2 -convert emits old-style brush blocks each preceded
                // by a '// brush N' comment (211/211 verified on academy1)
                Brushes = Math.Max(CountWord("brushDef"), CountComment(text, "brush ")),
                BrushSides = CountSideLines(text),
                BracesBalanced = balanced
            };
        }

        private static int CountComment(string text, string afterSlash)
        {
            // count line comments that start with "// <afterSlash>"
            int n = 0;
            foreach (var line in text.Split('\n'))
            {
                var t = line.TrimStart(' ', '\t');
                if (t.StartsWith("// ") && t[3..].StartsWith(afterSlash, StringComparison.Ordinal) &&
                    char.IsDigit(t[3 + afterSlash.Length]))
                    n++;
            }
            return n;
        }

        private static int CountSideLines(string text)
        {
            // old-style brush side: three "( x y z )" tuples then a texture token
            int n = 0;
            foreach (var raw in text.Split('\n'))
            {
                var t = raw.TrimStart(' ', '\t');
                int idx = 0;
                bool ok = true;
                for (int tuple = 0; tuple < 3; tuple++)
                {
                    if (idx >= t.Length || t[idx] != '(') { ok = false; break; }
                    int close = t.IndexOf(')', idx);
                    if (close < 0) { ok = false; break; }
                    bool hasNum = false;
                    for (int c = idx + 1; c < close; c++)
                        if (char.IsDigit(t[c]) || t[c] == '-' || t[c] == '.') { hasNum = true; break; }
                    if (!hasNum) { ok = false; break; }
                    idx = close + 1;
                    while (idx < t.Length && char.IsWhiteSpace(t[idx])) idx++;
                }
                if (ok && idx < t.Length && !char.IsWhiteSpace(t[idx])) n++;
            }
            return n;
        }
    }

    public class ConvertResult
    {
        public string SourceBsp { get; set; } = "";
        public string? MapFile { get; set; }
        public int Q3map2ExitCode { get; set; }
        public long ElapsedMs { get; set; }
        public List<string> Warnings { get; } = new();
        public string LogFile { get; set; } = "";
        public string ReportFile { get; set; } = "";
        public Dictionary<string, object?>? Original { get; set; }
        public Dictionary<string, object?>? Map { get; set; }
        public Dictionary<string, object?>? Recompile { get; set; }
        public bool Ok => MapFile != null && File.Exists(MapFile) && Q3map2ExitCode == 0;
    }

    /// <summary>
    /// M2 convert pipeline: delegate .map generation to
    /// `q3map2 -game ja -convert -format map` (Garux/netradiant-custom
    /// fork — the only q3map2 with RBSP support) and report per-map
    /// in/out stats against our own RBSP parse (the ground truth).
    /// </summary>
    public class ConvertOrchestrator
    {
        public ConvertResult Convert(string bspPath, string q3map2Bin, string outDir, string game = "ja", int timeoutMinutes = 10, bool recompile = false)
        {
            // The garux fork resolves its VFS base path from the executable
            // path; a relative binary ("tools/q3map2") dies with
            // "Failed to find a valid base path" and exit 1.
            q3map2Bin = Path.GetFullPath(q3map2Bin);
            Directory.CreateDirectory(outDir);
            var stem = Path.GetFileNameWithoutExtension(bspPath);
            var bspInDir = Path.Combine(outDir, Path.GetFileName(bspPath));
            File.Copy(bspPath, bspInDir, overwrite: true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = new System.Text.StringBuilder();
            int exitCode;
            using (var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = q3map2Bin,
                    Arguments = $"-game {game} -convert -format map {Path.GetFileName(bspPath)}",
                    WorkingDirectory = outDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            })
            {
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
                if (!proc.Start())
                    throw new InvalidOperationException($"failed to start {q3map2Bin}");
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutMinutes * 60_000))
                {
                    proc.Kill(entireProcessTree: true);
                    throw new TimeoutException($"q3map2 did not finish in {timeoutMinutes} min for {Path.GetFileName(bspPath)}");
                }
                // Parameterless WaitForExit() flushes the async output events;
                // without it the redirect log is silently empty/short.
                proc.WaitForExit();
                exitCode = proc.ExitCode;
                sw.Stop();
            }

            var result = new ConvertResult
            {
                SourceBsp = bspPath,
                Q3map2ExitCode = exitCode,
                ElapsedMs = sw.ElapsedMilliseconds,
            };

            string logFile = Path.Combine(outDir, $"{stem}.convert.log");
            File.WriteAllText(logFile, log.ToString());
            result.LogFile = logFile;
            result.Warnings.AddRange(log.ToString()
                .Split('\n')
                .Where(l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Trim())
                .Distinct()
                .Take(20));

            // q3map2 writes <stem>_converted.map next to the input
            string mapFile = Path.Combine(outDir, $"{stem}_converted.map");
            if (!File.Exists(mapFile))
            {
                var fallback = Directory.GetFiles(outDir, "*_converted.map").FirstOrDefault();
                if (fallback != null) mapFile = fallback;
            }
            if (File.Exists(mapFile)) result.MapFile = mapFile;

            // in/out stats: our RBSP parse vs the generated .map
            try
            {
                var d = new BspReader().Read(bspPath);
                result.Original = new Dictionary<string, object?>
                {
                    ["entities"] = d.Entities.Count,
                    ["faces"] = d.Faces.Count,
                    ["patches"] = d.Faces.Count(f => f.Type == FaceType.Patch),
                    ["brushes"] = d.Brushes.Count,
                    ["lightmapBlocks"] = d.Lightmaps.Count,
                };
            }
            catch (Exception ex)
            {
                result.Original = new Dictionary<string, object?> { ["error"] = ex.Message };
            }
            if (result.MapFile != null && File.Exists(result.MapFile))
            {
                var ms = MapFileStats.Parse(result.MapFile);
                result.Map = new Dictionary<string, object?>
                {
                    ["file"] = Path.GetFileName(result.MapFile),
                    ["sizeBytes"] = ms.SizeBytes,
                    ["entities"] = ms.Entities,
                    ["patches"] = ms.Patches,
                    ["brushes"] = ms.Brushes,
                    ["brushSides"] = ms.BrushSides,
                    ["bracesBalanced"] = ms.BracesBalanced,
                };

                // recompile test: the strongest validity proof short of a
                // Radiant GUI load — if q3map2 can compile the generated
                // .map back to a BSP without errors, the .map is structurally valid
                if (recompile)
                    result.Recompile = RunRecompile(q3map2Bin, result.MapFile, game, outDir, timeoutMinutes);
            }

            string reportFile = Path.Combine(outDir, $"{stem}.convert.json");
            File.WriteAllText(reportFile, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            result.ReportFile = reportFile;
            return result;
        }

        public List<ConvertResult> ConvertDirectory(string bspDir, string q3map2Bin, string outDir, string game = "ja", bool recompile = false)
        {
            var results = new List<ConvertResult>();
            foreach (var bsp in Directory.GetFiles(bspDir, "*.bsp").OrderBy(f => f))
            {
                results.Add(Convert(bsp, q3map2Bin, outDir, game, recompile: recompile));
            }
            var summary = new
            {
                total = results.Count,
                ok = results.Count(r => r.Ok),
                entityPreserved = results.Count(r =>
                    r.Original != null && r.Map != null
                    && (int)r.Original["entities"]! == (int)r.Map["entities"]!),
                maps = results.Select(r => new
                {
                    bsp = Path.GetFileName(r.SourceBsp),
                    ok = r.Ok,
                    exitCode = r.Q3map2ExitCode,
                    entitiesIn = r.Original?["entities"],
                    entitiesOut = r.Map?["entities"],
                    patchesIn = r.Original?["patches"],
                    patchesOut = r.Map?["patches"],
                    brushesIn = r.Original?["brushes"],
                    brushesOut = r.Map?["brushes"],
                    warnings = r.Warnings.Count,
                    elapsedMs = r.ElapsedMs
                })
            };
            File.WriteAllText(Path.Combine(outDir, "convert_summary.json"),
                JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            return results;
        }

        /// <summary>Compile the generated .map back to a BSP as a validity check.</summary>
        public static Dictionary<string, object?> RunRecompile(string q3map2Bin, string mapFile, string game, string outDir, int timeoutMinutes)
        {
            q3map2Bin = Path.GetFullPath(q3map2Bin); // see Convert()
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = new System.Text.StringBuilder();
            int exitCode;
            using (var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = q3map2Bin,
                    Arguments = $"-game {game} -noexit {Path.GetFileName(mapFile)}",
                    WorkingDirectory = outDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            })
            {
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
                if (!proc.Start()) throw new InvalidOperationException($"failed to start {q3map2Bin}");
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutMinutes * 60_000))
                {
                    proc.Kill(entireProcessTree: true);
                    throw new TimeoutException($"recompile did not finish in {timeoutMinutes} min for {Path.GetFileName(mapFile)}");
                }
                proc.WaitForExit(); // flush async output events (see Convert)
                exitCode = proc.ExitCode;
                sw.Stop();
            }
            var text = log.ToString();
            string rcLog = Path.Combine(outDir, Path.GetFileNameWithoutExtension(mapFile) + ".recompile.log");
            File.WriteAllText(rcLog, text);
            // q3map2 hard errors start with "Error" — substring checks for
            // 'fatal'/'cannot' false-positive on map names (t1_fatal) and
            // ordinary warning lines
            var errors = text.Split('\n')
                .Where(l => l.StartsWith("Error", StringComparison.Ordinal))
                .Select(l => l.Trim())
                .Distinct()
                .ToList();
            return new Dictionary<string, object?>
            {
                ["exitCode"] = exitCode,
                ["errors"] = errors,
                ["clean"] = exitCode == 0 && errors.Count == 0,
                ["logFile"] = rcLog,
                ["elapsedMs"] = sw.ElapsedMilliseconds
            };
        }
    }
}
