using System.Diagnostics;
using System.IO;

namespace BTAP.Services;

/// <summary>Lightweight file logger for animation debugging.
///
/// We've tried nearly every WinUI animation primitive and only a narrow
/// set visibly renders in this environment (ContentThemeTransition with
/// HorizontalOffset, Storyboards targeting ThemeStep's sun-rays). Logging
/// gives us a side-by-side trace of a confirmed-working animation versus
/// a failing one, so we can spot what's different (storyboard never fires
/// Completed? ScaleY doesn't change? Element ActualHeight is zero?).
///
/// Writes to <c>%TEMP%\btap-anim.log</c> AND <c>Debug.WriteLine</c>. The
/// file is opened append-mode each call (no FileStream held open) so it
/// survives app crashes and is trivially tail-able from another window.
/// </summary>
public static class AnimDebug
{
    private static readonly object _lock = new();
    private static readonly string _path =
        Path.Combine(Path.GetTempPath(), "btap-anim.log");
    private static bool _bannerWritten;

    public static string LogPath => _path;

    public static void Log(string msg)
    {
        // Logging must never throw into UI-thread call sites — wrap the
        // whole body in catch-all. A failing log line is strictly less bad
        // than a crashed app.
        try
        {
            var stamped = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            Debug.WriteLine($"[ANIM] {stamped}");
            lock (_lock)
            {
                if (!_bannerWritten)
                {
                    File.AppendAllText(_path,
                        $"{Environment.NewLine}=== btap-anim log opened " +
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ===" +
                        Environment.NewLine);
                    _bannerWritten = true;
                }
                File.AppendAllText(_path, stamped + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
