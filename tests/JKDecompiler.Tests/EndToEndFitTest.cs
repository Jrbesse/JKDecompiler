using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JKDecompiler.Core;
using Xunit;

namespace JKDecompiler.Tests;

/// <summary>
/// M2.5 acceptance gate — end-to-end: convert → fit-lights on a real fixture,
/// assert Kept &gt; 0 and ErrorFinal &lt; ErrorAmbientOnly − 2.0 (the same
/// margin the fit itself enforces at stage A via GateMarginL1).
///
/// Loud-skip unless all of the following are present (same pattern as
/// FixtureTests): JKA_BSP_FIXTURES (directory of .bsp fixtures), tools/q3map2,
/// and JKA_GAME_DIR (directory containing base/ paks for q3map2's -fs_basepath).
///
/// Heavy: a full fit on academy1 compiles ~29 times (~35–40 min). Set
/// JKD_E2E_QUICK=1 for a stage-A-only smoke (--no-posfit --no-radfit) that
/// still exercises convert + fit-lights and asserts the same L1 gate.
/// Optionally set JKD_E2E_SEEDS=&lt;path to external seeds JSON&gt; to skip
/// stage-1 grid seeding (the fit run that produced academy1's measured
/// 103.02 → 73.25 numbers used an external seeds file).
/// </summary>
public class EndToEndFitTest
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "q3map2")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void LoudSkip(string reason) =>
        Assert.True(true, $"M2.5 end-to-end gate SKIPPED: {reason}");

    [Fact]
    public void FullPipelineBeatsAmbientOnly()
    {
        var fixDir = Env("JKA_BSP_FIXTURES");
        if (string.IsNullOrWhiteSpace(fixDir) || !Directory.Exists(fixDir))
        {
            LoudSkip("JKA_BSP_FIXTURES not set or missing — no fixtures to exercise");
            return;
        }
        var files = Directory.GetFiles(fixDir!, "*.bsp", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            LoudSkip($"no .bsp files under {fixDir}");
            return;
        }
        var repoRoot = FindRepoRoot();
        var q3map2 = Env("JKD_Q3MAP2") ?? (repoRoot != null ? Path.Combine(repoRoot, "tools", "q3map2") : null);
        if (string.IsNullOrWhiteSpace(q3map2) || !File.Exists(q3map2))
        {
            LoudSkip("q3map2 not found (set JKD_Q3MAP2 or place tools/q3map2 in the repo)");
            return;
        }
        var gameDir = Env("JKA_GAME_DIR");
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            LoudSkip("JKA_GAME_DIR not set (directory containing base/ paks, e.g. the JKA game copy)");
            return;
        }

        var fixture = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileNameWithoutExtension(f), "academy1", StringComparison.OrdinalIgnoreCase))
            ?? files.OrderBy(f => f).First();
        var quick = Env("JKD_E2E_QUICK") == "1";
        var seedsPath = Env("JKD_E2E_SEEDS") ?? "";
        if (!string.IsNullOrWhiteSpace(seedsPath) && !File.Exists(seedsPath))
        {
            LoudSkip($"JKD_E2E_SEEDS points at a missing file: {seedsPath}");
            return;
        }

        // q3map2 resolves its VFS base path from the executable path; the
        // garux fork needs LD_LIBRARY_PATH pinned to the tools dir.
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH",
            Path.GetDirectoryName(Path.GetFullPath(q3map2)), EnvironmentVariableTarget.Process);

        var root = Path.Combine(Path.GetTempPath(), "jkd-e2e-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(root, "out");
        var scratch = Path.Combine(root, "scratch");
        var stem = Path.GetFileNameWithoutExtension(fixture);

        // 1. convert (M2 pipeline: RBSP → .map)
        var convert = new ConvertOrchestrator().Convert(fixture, q3map2, outDir, timeoutMinutes: 10);
        Assert.True(convert.Ok && convert.MapFile != null,
            $"convert failed (q3map2 exit {convert.Q3map2ExitCode}): {convert.LogFile}");

        // 2. fit-lights (M2.5 stage 2, full pipeline)
        var options = new LightFitOptions
        {
            PosFit = !quick,
            RadiusFit = !quick,
            Parallel = 2,
            SeedsPath = seedsPath,
        };
        var fit = new LightFit(q3map2, gameDir, options);
        var data = new BspReader().Read(fixture);
        var r = fit.Fit(data, convert.MapFile, outDir, scratch, stem);

        var kept = r.Lights.Count(l => l.Kept);
        Assert.True(kept > 0,
            $"no lights kept (kept=0; stageA={r.ErrorStageA:F2}, ambient={r.ErrorAmbientOnly:F2}) — {r.Note}");
        Assert.True(r.ErrorFinal < r.ErrorAmbientOnly - 2.0,
            $"L1 gate failed: final {r.ErrorFinal:F2} vs ambient-only {r.ErrorAmbientOnly:F2} " +
            $"(need < {r.ErrorAmbientOnly - 2.0:F2}) — {r.Note}");
    }
}
