using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace JKDecompiler.Core
{
    public class BspEntity
    {
        public Dictionary<string, string> KeyValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string ClassName => KeyValues.GetValueOrDefault("classname", string.Empty);

        public Vector3 Origin
        {
            get
            {
                if (KeyValues.TryGetValue("origin", out var val))
                {
                    var parts = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0], out var x) &&
                        float.TryParse(parts[1], out var y) &&
                        float.TryParse(parts[2], out var z))
                    {
                        return new Vector3(x, y, z);
                    }
                }
                return Vector3.Zero;
            }
        }

        public List<string> GetIcarusScripts()
        {
            var scripts = new List<string>();
            string[] scriptKeys = { "spawnscript", "usescript", "deathscript", "painscript", "thinkscript" };
            foreach (var key in scriptKeys)
            {
                if (KeyValues.TryGetValue(key, out var script))
                {
                    scripts.Add(script);
                }
            }
            // Check PARM1-8 as well
            for (int i = 1; i <= 8; i++)
            {
                if (KeyValues.TryGetValue($"PARM{i}", out var parm) && parm.EndsWith(".ibi", StringComparison.OrdinalIgnoreCase))
                {
                    scripts.Add(parm);
                }
            }
            return scripts;
        }
    }

    public static class BspEntityParser
    {
        public static List<BspEntity> Parse(string entityString)
        {
            var entities = new List<BspEntity>();
            var matches = Regex.Matches(entityString, @"\{[^\}]*\}");

            foreach (Match match in matches)
            {
                var entity = new BspEntity();
                var kvMatches = Regex.Matches(match.Value, @"""([^""]+)""\s+""([^""]*)""");
                foreach (Match kvMatch in kvMatches)
                {
                    entity.KeyValues[kvMatch.Groups[1].Value] = kvMatch.Groups[2].Value;
                }
                entities.Add(entity);
            }

            return entities;
        }
    }
}
