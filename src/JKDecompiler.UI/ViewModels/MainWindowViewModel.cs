using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using System.IO;
using JKDecompiler.Core;
using ReactiveUI;
using System;

namespace JKDecompiler.UI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
        if (File.Exists(configPath))
        {
            string savedPath = File.ReadAllText(configPath).Trim();
            if (Directory.Exists(savedPath))
            {
                SetGamePath(savedPath);
            }
        }
    }

    private void SaveGamePath(string path)
    {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
        File.WriteAllText(configPath, path);
    }
    private BspData? _bspData;
    public BspData? BspData
    {
        get => _bspData;
        set => this.RaiseAndSetIfChanged(ref _bspData, value);
    }

    private ObservableCollection<BspEntity> _entities = new();
    public ObservableCollection<BspEntity> Entities
    {
        get => _entities;
        set => this.RaiseAndSetIfChanged(ref _entities, value);
    }

    private ObservableCollection<string> _modelIndices = new();
    public ObservableCollection<string> ModelIndices
    {
        get => _modelIndices;
        set => this.RaiseAndSetIfChanged(ref _modelIndices, value);
    }

    private ObservableCollection<string> _scripts = new();
    public ObservableCollection<string> Scripts
    {
        get => _scripts;
        set => this.RaiseAndSetIfChanged(ref _scripts, value);
    }

    private string _gamePath = "";
    public string GamePath
    {
        get => _gamePath;
        set => this.RaiseAndSetIfChanged(ref _gamePath, value);
    }

    private ObservableCollection<string> _missingAssets = new();
    public ObservableCollection<string> MissingAssets
    {
        get => _missingAssets;
        set => this.RaiseAndSetIfChanged(ref _missingAssets, value);
    }

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    public void ExportMap(string outputPath)
    {
        if (BspData == null) return;
        IsExporting = true;
        var exporter = new MapExporter();
        exporter.Export(BspData, outputPath);
        IsExporting = false;
        GamePathStatus = $"Export completed: {Path.GetFileName(outputPath)}";
    }

    public void LoadBsp(string path)
    {
        var reader = new BspReader();
        BspData = reader.Read(path);
        UpdateCollections();
    }

    private string _gamePathStatus = "Please select your JKA GameData folder.";
    public string GamePathStatus
    {
        get => _gamePathStatus;
        set => this.RaiseAndSetIfChanged(ref _gamePathStatus, value);
    }

    public void SetGamePath(string path)
    {
        // Try to find the correct GameData folder
        string[] candidates = { path, Path.Combine(path, "GameData"), Path.GetDirectoryName(path) ?? "" };
        string? validPath = null;

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "jamp.exe")))
            {
                validPath = candidate;
                break;
            }
        }

        if (validPath != null)
        {
            GamePath = validPath;
            GamePathStatus = "Valid JKA GameData path set.";
            SaveGamePath(validPath);
            UpdateCollections();
        }
        else
        {
            GamePathStatus = "Invalid path. Please select the folder containing jamp.exe (GameData).";
        }
    }

    private void UpdateCollections()
    {
        if (BspData == null) return;

        Entities.Clear();
        var scriptSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var entity in BspData.Entities)
        {
            Entities.Add(entity);
            foreach (var script in entity.GetIcarusScripts())
            {
                scriptSet.Add(script);
            }
        }

        ModelIndices.Clear();
        for (int i = 0; i < BspData.Models.Count; i++)
        {
            ModelIndices.Add($"Model {i} (Faces: {BspData.Models[i].NumFaces})");
        }

        Scripts.Clear();
        foreach (var script in scriptSet)
        {
            Scripts.Add(script);
        }

        if (!string.IsNullOrEmpty(GamePath) && System.IO.Directory.Exists(GamePath))
        {
            var checker = new AssetChecker(GamePath);
            var missing = checker.GetMissingAssets(BspData);
            MissingAssets.Clear();
            foreach (var asset in missing)
            {
                MissingAssets.Add(asset);
            }
        }
    }
}
