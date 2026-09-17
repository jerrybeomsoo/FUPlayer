using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using FUPlayer.Core.Decoding;
using FUPlayer.Core.Decoding.FFmpeg;
using FUPlayer.Core.Dsp.Restoration;
using FUPlayer.Core.Settings;

namespace FUPlayer.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);

        if (args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            WriteDiagnostics();
            return;
        }

        // Somebody told to put a model in a folder should find the folder there.
        ModelLibrary.EnsureDirectory();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Writes what the player can see, and stops.
    ///
    /// A window program cannot say anything to a console, so when the interface reports that nothing
    /// is installed there is no way to ask it which folder it looked in. This answers that from the
    /// same process, with the same user and the same environment, which is the only place the answer
    /// means anything: run FUPlayer.exe --diagnostics and read the file it names.
    /// </summary>
    private static void WriteDiagnostics()
    {
        var report = new StringBuilder();
        report.AppendLine($"FUPlayer diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"  program        {Environment.ProcessPath}");
        report.AppendLine($"  working dir    {Environment.CurrentDirectory}");
        report.AppendLine($"  user           {Environment.UserDomainName} / {Environment.UserName}");
        report.AppendLine($"  runtime        {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.RuntimeIdentifier}");
        report.AppendLine($"  APPDATA        {Environment.GetEnvironmentVariable("APPDATA")}");
        report.AppendLine($"  ApplicationData {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
        report.AppendLine($"  settings       {SettingsStore.DefaultDirectory}");

        // Every folder, not only the one it would write to. A program inside an application container
        // has its %APPDATA% redirected into the container, so two programs on one machine can be
        // looking at two different folders while both report the same path. Listing what is actually
        // in each of them is what tells the two apart.
        foreach (string directory in ModelLibrary.SearchDirectories())
        {
            report.AppendLine($"  models folder  {directory}  ({(Directory.Exists(directory) ? "exists" : "not there")})");
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    report.AppendLine($"    file         {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.AppendLine($"    unreadable   {ex.Message}");
            }
        }

        report.AppendLine($"  upscaler       {ModelLibrary.DescribeUpscaler()}");
        report.AppendLine($"  FFmpeg         {(FFmpegLibrary.IsAvailable ? FFmpegLibrary.VersionDescription : FFmpegLibrary.LoadError)}");
        report.AppendLine($"  plays          {string.Join(" ", DecoderFactory.PlayableExtensions)}");

        string path = Path.Combine(SettingsStore.DefaultDirectory, "diagnostics.txt");
        try
        {
            Directory.CreateDirectory(SettingsStore.DefaultDirectory);
            File.WriteAllText(path, report.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = Path.Combine(AppContext.BaseDirectory, "diagnostics.txt");
            File.WriteAllText(path, report.ToString() + Environment.NewLine + "(could not write beside the settings: " + ex.Message + ")");
        }

        Console.WriteLine(report.ToString());
        Console.WriteLine($"Written to {path}");
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SettingsStore.DefaultDirectory);
            File.AppendAllText(
                Path.Combine(SettingsStore.DefaultDirectory, "crash.log"),
                $"[{DateTime.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to report to.
        }
    }
}
