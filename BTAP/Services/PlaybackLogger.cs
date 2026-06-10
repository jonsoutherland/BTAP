using System.IO;
using System.Text;

namespace BTAP.Services;

/// <summary>
/// TEMPORARY diagnostic logger for the multi-player playback path. Writes a
/// timestamped, thread-tagged trace to "&lt;project-file&gt;.playback.log" (or a
/// temp file when no project is saved yet) so we can correlate the sequence of
/// Sync / AddLayer / DisposeLayer / VideoFrameAvailable events leading up to a
/// hard crash when multiple clips overlap.
///
/// Auto-flushes on every line so the tail survives a process-killing AV. Remove
/// the call sites once the multi-clip crash is identified.
/// </summary>
public static class PlaybackLogger
{
    private static readonly object _gate = new();
    private static StreamWriter? _writer;
    private static string? _filePath;

    public static string? FilePath => _filePath;
    public static bool IsEnabled => _writer is not null;

    /// <summary>Opens (or rotates to) a log next to <paramref name="projectFilePath"/>.
    /// Falls back to the system temp directory when the project is unsaved.</summary>
    public static void Initialize(string? projectFilePath)
    {
        lock (_gate)
        {
            try
            {
                Shutdown_NoLock();

                string path;
                if (!string.IsNullOrWhiteSpace(projectFilePath))
                    path = projectFilePath + ".playback.log";
                else
                    path = Path.Combine(Path.GetTempPath(), "btap.playback.log");

                _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
                _filePath = path;

                WriteLine_NoLock("==== BTAP playback log ====");
                WriteLine_NoLock($"Started:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                WriteLine_NoLock($"Project:  {projectFilePath ?? "(unsaved)"}");
                WriteLine_NoLock($"PID:      {Environment.ProcessId}");
                WriteLine_NoLock("");
            }
            catch
            {
                // Logging is best-effort — never let it take the app down.
                _writer = null;
                _filePath = null;
            }
        }
    }

    public static void Log(string message)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try { WriteLine_NoLock(message); } catch { }
        }
    }

    public static void Shutdown()
    {
        lock (_gate) Shutdown_NoLock();
    }

    private static void Shutdown_NoLock()
    {
        if (_writer is null) return;
        try { WriteLine_NoLock("==== End of log ===="); } catch { }
        try { _writer.Dispose(); } catch { }
        _writer = null;
    }

    private static void WriteLine_NoLock(string message)
    {
        _writer!.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId,3}] {message}");
    }
}
