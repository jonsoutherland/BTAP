using System.IO;

namespace BTAP.Services;

/// <summary>Writes verbose, timestamped export diagnostics to a sidecar .log file.</summary>
public sealed class ExportLogger : IDisposable
{
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    public ExportLogger(string outputFilePath)
    {
        FilePath = Path.ChangeExtension(outputFilePath, ".export.log");
        _writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
        Log("==== BTAP export log ====");
        Log($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Output:  {outputFilePath}");
        Log("");
    }

    public void Log(string message) =>
        _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public void Dispose()
    {
        try { Log("==== End of log ===="); _writer.Dispose(); } catch { }
    }
}
