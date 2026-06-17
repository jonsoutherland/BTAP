using Microsoft.UI.Xaml;
using BTAP.Services;
using Windows.Storage;

namespace BTAP;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // TEMPORARY: capture last-ditch info before WinUI tears the process down,
        // so the playback log's tail shows the actual exception type/message.
        UnhandledException += (_, e) =>
        {
            try
            {
                PlaybackLogger.Log($"!!! UnhandledException {e.Exception?.GetType().Name}: {e.Message}");
                if (e.Exception is not null)
                    PlaybackLogger.Log("Stack:\n" + e.Exception.ToString());
                PlaybackLogger.Shutdown();
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                PlaybackLogger.Log($"!!! AppDomain Unhandled {ex?.GetType().Name}: {ex?.Message}");
                if (ex is not null) PlaybackLogger.Log("Stack:\n" + ex);
                PlaybackLogger.Shutdown();
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { PlaybackLogger.Log($"!!! UnobservedTask {e.Exception?.GetType().Name}: {e.Exception?.Message}"); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless export mode for debugging without UI:
        //   BTAP.exe --export <project.btap> <output.mp4>
        // Skips MainWindow entirely, runs the export pipeline, exits.
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Length >= 4 && cmd[1].Equals("--export", StringComparison.OrdinalIgnoreCase))
        {
            RunHeadlessExportAndExit(cmd[2], cmd[3]);
            return;
        }

        // Hook the live accent before any window paints — first frame already
        // reflects the user's saved colour instead of flashing the default sage.
        AccentManager.Init();

        _window = new MainWindow();
        _window.Activate();
    }

    public MainWindow GetMainWindow() => _window!;

    private void RunHeadlessExportAndExit(string projectPath, string outputPath)
    {
        // Run synchronously on a worker thread; the export pipeline already
        // does Task.Run + ConfigureAwait(false) internally so it never needs
        // to dispatch back to the UI thread. We block here until it finishes,
        // then exit the process with a status code.
        int exitCode;
        try
        {
            exitCode = Task.Run(async () =>
            {
                if (!System.IO.File.Exists(projectPath))
                {
                    Console.Error.WriteLine($"Project file not found: {projectPath}");
                    return 2;
                }

                var project = ProjectSerializer.Load(projectPath);

                // CreateAsync the output file path (writes a 0-byte stub if it
                // doesn't exist) so we can hand a real StorageFile to the
                // export pipeline.
                var outDir = System.IO.Path.GetDirectoryName(outputPath) ?? ".";
                System.IO.Directory.CreateDirectory(outDir);
                if (!System.IO.File.Exists(outputPath))
                    System.IO.File.WriteAllBytes(outputPath, Array.Empty<byte>());
                var outFile = await StorageFile.GetFileFromPathAsync(outputPath);

                using var log = new ExportLogger(outputPath);
                log.Log("=== HEADLESS CLI EXPORT ===");
                log.Log($"Project: {projectPath}");
                log.Log($"Output:  {outputPath}");

                var result = await ExportRenderer.RenderAsync(
                    project, outFile, progress: null, log: log,
                    ct: System.Threading.CancellationToken.None).ConfigureAwait(false);

                log.Log($"Result: {(result.Success ? "SUCCESS" : "FAILED: " + result.Error)}");
                return result.Success ? 0 : 1;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try
            {
                var errPath = System.IO.Path.ChangeExtension(outputPath, ".error.log");
                System.IO.File.WriteAllText(errPath,
                    $"{DateTime.Now:O}\n{ex.GetType().FullName}: {ex.Message}\n{ex}");
            }
            catch { }
            exitCode = 3;
        }

        Environment.Exit(exitCode);
    }
}
