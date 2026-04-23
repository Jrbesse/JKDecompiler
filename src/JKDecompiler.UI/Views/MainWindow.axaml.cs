using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JKDecompiler.UI.ViewModels;
using System.Linq;

namespace JKDecompiler.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenBsp_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open JKA BSP File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("BSP Files") { Patterns = new[] { "*.bsp" } }
            }
        });

        if (files.Count >= 1)
        {
            var path = files[0].Path.LocalPath;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.LoadBsp(path);
            }
        }
    }

    private async void ExportBsp_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save .map File",
            DefaultExtension = "map",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Map Files") { Patterns = new[] { "*.map" } }
            }
        });

        if (file != null)
        {
            var path = file.Path.LocalPath;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ExportMap(path);
            }
        }
    }

    private async void SetGamePath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select JKA GameData Folder",
            AllowMultiple = false
        });
if (folders.Count >= 1)
{
    if (DataContext is MainWindowViewModel vm)
    {
        vm.SetGamePath(folders[0].Path.LocalPath);
    }
}
}
private void Exit_Click(object? sender, RoutedEventArgs e)
{
Close();
}
}