using FUPlayer.ServerHost;
using FUPlayer.Core.Localization;

namespace FUPlayer.ServerController;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--controller" or "--worker" or "--devices" or "--devices-json" or "--help"
            or "--version" or "--licenses" or "--extract-licenses" or "--generate-key-file"))
            return ServerApplication.RunAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FUPlayer-DspServer", "Controller");
            string? language = null;
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (name is not ("--settings-dir" or "--language") || ++i >= args.Length)
                    throw new ArgumentException("Use --settings-dir <directory> or --language en|ko.");
                if (name == "--settings-dir") directory = Path.GetFullPath(args[i]); else language = args[i];
            }
            var store = new ControllerSettingsStore(directory);
            ServerSettings settings;
            string? loadError = null;
            try { settings = store.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                settings = new ServerSettings(); loadError = ex.Message;
            }
            Loc.SetLanguage(language ?? settings.Language);
            string executable = Environment.ProcessPath ?? throw new IOException(Loc.T("The DSP server could not be started."));
            var server = new ServerProcess(executable);
            try
            {
                using var form = new MainForm(store, settings, server, loadError);
                Application.Run(form);
            }
            finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            MessageBox.Show(ex.Message, "FUPlayer DSP server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
