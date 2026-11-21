using System;
using System.Collections.Generic;
using System.Numerics;

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

    /// <summary>
    /// Quote-aware tokenizer for the entity lump. Replaces the old
    /// regex \{[^}]*\} which broke on any value containing '}' (script
    /// strings, quotes in comments, etc.).
    /// </summary>
    public static class BspEntityParser
    {
        public static List<BspEntity> Parse(string entityString)
        {
            var entities = new List<BspEntity>();
            int i = 0;
            int n = entityString.Length;

            while (i < n)
            {
                char c = entityString[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\0') { i++; continue; }
                if (c == '}') { i++; continue; } // stray close outside an entity

                if (c != '{')
                    throw new FormatException($"entity lump: unexpected char '{c}' at offset {i}");

                i++; // consume '{'
                var entity = new BspEntity();
                var keys = new List<string>();
                int pendingKey = -1; // index into keys awaiting a value; -1 = none

                while (i < n)
                {
                    c = entityString[i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\0') { i++; continue; }

                    if (c == '}') { i++; break; }
                    if (c == '{')
                        throw new FormatException($"entity lump: nested '{{' at offset {i}");

                    if (c != '"')
                        throw new FormatException($"entity lump: expected '\"' at offset {i}, got '{c}'");

                    // read a quoted string, honoring \" and \\ escapes
                    i++;
                    var sb = new System.Text.StringBuilder();
                    while (i < n)
                    {
                        c = entityString[i];
                        if (c == '\\' && i + 1 < n)
                        {
                            sb.Append(entityString[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (c == '"') { i++; break; }
                        sb.Append(c);
                        i++;
                    }
                    if (i >= n)
                        throw new FormatException($"entity lump: unterminated string at offset {i}");

                    string token = sb.ToString();
                    if (pendingKey < 0)
                    {
                        keys.Add(token);
                        pendingKey = keys.Count - 1;
                    }
                    else
                    {
                        entity.KeyValues[keys[pendingKey]] = token;
                        pendingKey = -1;
                    }
                }

                if (pendingKey >= 0)
                    throw new FormatException($"entity lump: key '{keys[pendingKey]}' missing value");
                entities.Add(entity);
            }

            return entities;
        }
    }
}
