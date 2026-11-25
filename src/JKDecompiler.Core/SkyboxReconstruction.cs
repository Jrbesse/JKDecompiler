using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JKDecompiler.Core;

/// <summary>
/// Sky bake for light fitting.
///
/// The real sky brushes survive decompilation — the C# exporter writes them
/// with their original sky texture (academy1: 15 brushes, 90 sides,
/// skies/yavin), and a re-compiled BSP shows them as 15 sky-flagged
/// drawsurfs forming the original aperture. What is missing is light: the
/// base game sky shaders (skies.shader: textures/skies/yavin) carry
/// surfaceparm sky + skyParms but no sun/q3map_skyLight key, so the ydnar
/// light stage creates no sky light and bakes no wash.
///
/// A synthetic shell is the wrong shape for the same reason: a 6-slab box
/// placed over the map is mostly swallowed by the union with existing
/// geometry (only thin fragments survive as drawsurfaces). The original
/// sky brushes already define the aperture; they just need a skylight.
///
/// Fix: re-texture the sky-flagged brush sides in the map text with a
/// dedicated overlay shader (fit/sky) installed into the game VFS, carrying
/// q3map_skyLight. Re-texturing (rather than redefining the base shader
/// name) avoids q3map2's first-match-wins shader table, where the base
/// pk3's definition may load before any overlay.
///
/// Verified against academy1 (2026-11-24): probe BSP carries 15
/// textures/skies/yavin drawsurfs with the sky flag; the base shader has no
/// sun key; after re-texture the light stage logs "Sky: textures/fit/sky"
/// on the real aperture.
/// </summary>
public static class SkyboxReconstruction
{
    /// <summary>MASK_SKY in the JKA surface-flag set (BSP texture table value).</summary>
    public const int MF_SKY = 0x00002000;

    /// <summary>Overlay texture name in the .map (the q3map2 map reader prepends "textures/").</summary>
    public const string TextureName = "fit/sky";

    /// <summary>
    /// Zero-photon A/B overlay: identical to fit/sky but emitting no sky
    /// light. The (probe − no-sky) difference per world point is the spatial
    /// sky wash — the calibration basis the 5a sky fit scales up to the
    /// original's magnitude.
    /// </summary>
    public const string TextureNameNoSky = "fit/sky0";

    public const string ShaderFileName = "fit_sky.shader";
    public const string ShaderFileNameNoSky = "fit_sky0.shader";

    /// <summary>
    /// q3map_skyLight value (linear in emitted photons) — the sky-wash
    /// magnitude knob. 130 is the calibrated mid-bracket default (owner
    /// decision #16: 112–146 bracket, close to the ~118 face-level cross-check);
    /// the fit's residual gain tells you how far off it is. Re-tune via
    /// --sky-value.
    /// </summary>
    public const double DefaultSkyLightValue = 130;

    /// <summary>
    /// Overlay shader source. When <paramref name="sunIntensity"/> &gt; 0, the
    /// shader carries ONLY a <c>sun</c> key — the game's own directional wash
    /// (q3map_sun <red> <green> <blue> <intensity> <degrees> <elevation>;
    /// degree 0 = from the east, 90 = north, altitude 0 = horizon/90 = noon).
    /// The single q3map_skyLight value is radially symmetric; the directional
    /// key is what lets the baked wash match the original's south-strong /
    /// north-weak shape. Intensity 0 (the default) emits no sun key — identical
    /// to the single-value overlay.
    /// </summary>
    private static string ShaderSource(string texture, string imageBase, double skyLightValue,
        double sunIntensity = 0, double sunDegrees = 120, double sunElevation = 40,
        double sunR = 1.0, double sunG = 0.54, double sunB = 0.629)
    {
        // Sun-enabled overlay (game-shader convention, e.g. textures/mp/h_yavin:
        // only `sun`, plus q3map_surfacelight by the bake) — the directional sun
        // REPLACES the radially-symmetric q3map_skyLight key. Carrying both
        // double-counts wash energy (measured 0/7 kept + negative gain on the
        // capped smoke). Intensity 0 emits no sun key and is byte-identical to
        // the single-value overlay (back-compat).
        var body = new System.Text.StringBuilder();
        body.Append("textures/").Append(texture).Append("\n{\n");
        body.Append("    qer_editorimage    textures/skies/sky.tga\n");
        body.Append("    surfaceparm        noimpact\n");
        body.Append("    surfaceparm        nomarks\n");
        body.Append("    notc\n");
        body.Append("    q3map_nolightmap\n");
        body.Append("    skyParms           ").Append(imageBase).Append(" 512 -\n");
        if (sunIntensity > 0)
        {
            body.Append("    sun ")
                .Append($"{sunR:0.000} {sunG:0.000} {sunB:0.000} {sunIntensity:0.##} {sunDegrees:0.##} {sunElevation:0.##}\n");
        }
        else
        {
            body.Append("    q3map_skyLight     ").Append($"{skyLightValue:0.##} 16 -90 90 1\n");
        }
        body.Append("}\n");
        return body.ToString();
    }

    /// <summary>
    /// The map-form names of the sky-flagged textures used by world brush
    /// sides (e.g. "skies/yavin"), most-used first. Empty when the map has no
    /// sky.
    /// </summary>
    public static List<string> SkyTextureNames(BspData bsp)
    {
        var counts = new Dictionary<string, int>();
        if (bsp == null || bsp.Models.Count == 0) return new List<string>();
        var world = bsp.Models[0];
        for (int i = 0; i < world.NumBrushes; i++)
        {
            int brushIndex = world.FirstBrush + i;
            if (brushIndex < 0 || brushIndex >= bsp.Brushes.Count) continue;
            var brush = bsp.Brushes[brushIndex];
            for (int s = 0; s < brush.NumSides; s++)
            {
                int sideIndex = brush.FirstSide + s;
                if (sideIndex < 0 || sideIndex >= bsp.BrushSides.Count) continue;
                int si = bsp.BrushSides[sideIndex].ShaderIndex;
                if (si < 0 || si >= bsp.Shaders.Count) continue;
                var shader = bsp.Shaders[si];
                if ((shader.SurfaceFlags & MF_SKY) == 0) continue;
                if (string.IsNullOrEmpty(shader.Name)) continue;
                string name = CleanShaderName(shader.Name);
                counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
            }
        }
        return counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// The skyParms image base for the dominant sky shader (JKA skies.shader
    /// convention: image base == full sky shader name).
    /// </summary>
    public static string SkyImageBase(BspData bsp)
    {
        var names = SkyTextureNames(bsp);
        if (names.Count == 0) return "textures/skies/yavin";
        return names[0].StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            ? names[0]
            : "textures/" + names[0];
    }

    /// <summary>
    /// Re-texture the sky sides in the map text: a brush side line is
    /// "( x y z ) ( x y z ) ( x y z ) &lt;texture&gt; shiftU shiftV rotate
    /// scaleU scaleV content surface 0" — replace the texture token when it is
    /// one of <paramref name="skyTextures"/>. <paramref name="target"/> is the
    /// replacement texture (the skylight overlay, or the zero-photon A/B
    /// overlay for the no-sky compile). Returns the modified text and the
    /// number of sides re-textured.
    /// </summary>
    public static (string Text, int Count) RetextureSky(string mapText, IReadOnlyList<string> skyTextures,
        string target = TextureName)
    {
        if (skyTextures.Count == 0) return (mapText, 0);
        var set = new HashSet<string>(skyTextures);
        var lines = mapText.Split('\n');
        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains(" ) ( ")) continue; // not a side line
            string indent = line.Substring(0, line.Length - line.TrimStart().Length);
            var tokens = line.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 22) continue;
            // three 3-point groups: ( x y z ) × 3 → texture at index 15
            if (tokens[0] != "(" || tokens[4] != ")" ||
                tokens[5] != "(" || tokens[9] != ")" ||
                tokens[10] != "(" || tokens[14] != ")") continue;
            string texture = tokens[15];
            if (!set.Contains(texture)) continue;
            tokens[15] = target;
            count++;
            lines[i] = indent + string.Join(" ", tokens);
        }
        return (string.Join("\n", lines), count);
    }

    static string CleanShaderName(string name) =>
        name.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            ? name["textures/".Length..]
            : name;

    /// <summary>
    /// Install the overlay shader into the game VFS so the q3map2 light stage
    /// loads it: with no shaders/shaderlist.txt present, q3map2 loads every
    /// .shader file under the VFS "shaders" directory (loose dirs + pk3s), so
    /// &lt;gameDir&gt;/base/shaders/fit_sky.shader is picked up. Idempotent; a
    /// changed <paramref name="skyLightValue"/>/<paramref name="sunIntensity"/>
    /// rewrites the file (the values are the wash-magnitude calibration knobs;
    /// sun replaces skyLight when intensity &gt; 0).
    /// </summary>
    public static string InstallShader(string gameDir, string imageBase,
        double skyLightValue = DefaultSkyLightValue,
        double sunIntensity = 0, double sunDegrees = 120, double sunElevation = 40,
        double sunR = 1.0, double sunG = 0.54, double sunB = 0.629)
    {
        string dir = Path.Combine(gameDir, "base", "shaders");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ShaderFileName);
        string text = ShaderSource(TextureName, imageBase, skyLightValue,
            sunIntensity, sunDegrees, sunElevation, sunR, sunG, sunB);
        if (!File.Exists(path) || File.ReadAllText(path) != text)
            File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Install the zero-photon A/B overlay (textures/fit/sky0,
    /// q3map_skyLight 0 …): same sky surface and lightmap-packing behavior
    /// as fit/sky, but no sky photons — the no-sky compile's difference from
    /// the probe isolate the spatial wash. Idempotent.
    /// </summary>
    public static string InstallShaderNoSky(string gameDir, string imageBase)
    {
        string dir = Path.Combine(gameDir, "base", "shaders");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ShaderFileNameNoSky);
        string text = ShaderSource(TextureNameNoSky, imageBase, 0);
        if (!File.Exists(path) || File.ReadAllText(path) != text)
            File.WriteAllText(path, text);
        return path;
    }
}
