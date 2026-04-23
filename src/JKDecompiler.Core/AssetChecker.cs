using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace JKDecompiler.Core
{
    public class AssetChecker
    {
        private readonly string _gamePath; // Path to GameData
        private readonly string _basePath; // Path to base
        private HashSet<string> _availableAssets = new(StringComparer.OrdinalIgnoreCase);

        public AssetChecker(string gamePath)
        {
            _gamePath = gamePath;
            _basePath = Path.Combine(gamePath, "base");
            Initialize();
        }

        private void Initialize()
        {
            string logPath = Path.Combine(Path.GetTempPath(), "decompiler_debug.log");
            File.WriteAllText(logPath, $"DEBUG: Checking path: {_basePath}{Environment.NewLine}");

            if (!Directory.Exists(_basePath))
            {
                File.AppendAllText(logPath, $"DEBUG: Path does not exist: {_basePath}{Environment.NewLine}");
                return;
            }

            // 2. Scan PK3 files in base/
            var pk3Files = Directory.GetFiles(_basePath, "*.pk3");
            File.AppendAllText(logPath, $"Found {pk3Files.Length} PK3 files in {_basePath}{Environment.NewLine}");
            
            foreach (var pk3 in pk3Files)
            {
                try
                {
                    using (var archive = ZipFile.OpenRead(pk3))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            _availableAssets.Add(entry.FullName.Replace('\\', '/'));
                        }
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(logPath, $"Error reading PK3 {pk3}: {ex.Message}{Environment.NewLine}");
                }
            }
            File.AppendAllText(logPath, $"Indexed {_availableAssets.Count} total assets.{Environment.NewLine}");
        }

        private void ScanDirectory(string path)
        {
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(_basePath, file).Replace('\\', '/');
                _availableAssets.Add(relativePath);
            }
        }

        public bool IsAssetPresent(string relativePath)
        {
            // Normalize path: lowercase, consistent separators
            var normalized = relativePath.Replace('\\', '/').ToLower().TrimStart('/');
            
            // Check for direct match
            if (_availableAssets.Contains(normalized)) return true;

            // Check common extensions if none provided
            if (!Path.HasExtension(normalized))
            {
                var extensions = new[] { ".tga", ".jpg", ".png", ".glm", ".md3", ".shader" };
                foreach (var ext in extensions)
                {
                    if (_availableAssets.Contains(normalized + ext)) return true;
                }
            }

            // Check if it's a shader (sometimes entities reference just the shader name, not the path)
            // We search for any path ending with the asset name
            bool found = _availableAssets.Any(a => a.EndsWith("/" + normalized) || a == normalized);
            if (!found)
            {
                 string logPath = Path.Combine(Path.GetTempPath(), "decompiler_debug.log");
                 File.AppendAllText(logPath, $"DEBUG: Failed to find asset: {normalized}{Environment.NewLine}");
            }
            return found;
        }

        public List<string> GetMissingAssets(BspData data)
        {
            var missing = new List<string>();
            
            // Check Shaders/Textures
            foreach (var shader in data.Shaders)
            {
                // Remove existing 'textures/' or 'shaders/' prefix if it exists
                string cleanName = shader.Name;
                if (cleanName.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) cleanName = cleanName.Substring(9);
                if (cleanName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)) cleanName = cleanName.Substring(8);

                // If it starts with 'models/', it's a model texture.
                // We should check 'models/' directly as well as with 'textures/'/'shaders/' prefixing.
                bool found = IsAssetPresent("textures/" + cleanName) || 
                             IsAssetPresent("shaders/" + cleanName) || 
                             IsAssetPresent(cleanName);

                if (!found)
                {
                    missing.Add($"Texture/Shader: {shader.Name} (Not found in base)");
                }
            }

            // Check Models (extracted from entities)
            foreach (var entity in data.Entities)
            {
                if (entity.KeyValues.TryGetValue("model", out var model) && !model.StartsWith("*"))
                {
                    if (!IsAssetPresent(model))
                    {
                        missing.Add($"Model: {model} (Not found in base)");
                    }
                }
            }

            return missing.Distinct().ToList();
        }
    }
}
