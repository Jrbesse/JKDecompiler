using Avalonia;
using Avalonia.ReactiveUI;
using System;

namespace JKDecompiler.UI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2)
        {
            RunCommandLine(args);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunCommandLine(string[] args)
    {
        string inputBsp = args[0];
        string outputMap = args[1];

        Console.WriteLine($"Decompiling {inputBsp} to {outputMap}...");

        try
        {
            var reader = new JKDecompiler.Core.BspReader();
            var data = reader.Read(inputBsp);
            var exporter = new JKDecompiler.Core.MapExporter();
            exporter.Export(data, outputMap);
            exporter.FinalizeExport();
            Console.WriteLine("Decompilation successful.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during decompilation: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
