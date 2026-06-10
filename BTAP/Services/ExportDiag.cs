using System.IO;
using Microsoft.Graphics.Canvas;
using Vortice.Direct3D11;

namespace BTAP.Services;

/// <summary>
/// Diagnostic frame dumps for the export pipeline. When enabled, writes PNGs of
/// intermediate render-target / texture state to a sidecar folder so we can see
/// at which stage the visible content goes missing (source decode, composite,
/// or encoder hand-off).
///
/// Counters are reset at Init time so each export run gets a fresh batch and
/// older PNGs in the folder are overwritten.
/// </summary>
internal static class ExportDiag
{
    public static string? DumpDir { get; private set; }

    private static readonly Dictionary<string, int> s_counters = new();
    private static readonly object s_lock = new();

    public static void Init(string baseLogPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(baseLogPath) ?? Path.GetTempPath(), "btap-diag");
        Directory.CreateDirectory(dir);
        DumpDir = dir;
        lock (s_lock) s_counters.Clear();
    }

    /// <summary>Returns the next index for <paramref name="bucket"/>, or -1 if the
    /// bucket has already produced <paramref name="maxPerBucket"/> dumps (so callers
    /// can skip the dump entirely without paying any cost).</summary>
    /// <summary>When true, ALL dump calls are skipped — used to isolate
    /// whether the SaveAsync GPU readbacks themselves are affecting the
    /// export pipeline's D2D state.</summary>
    public static bool DisableAllDumps { get; set; } = false;

    public static int NextIndex(string bucket, int maxPerBucket)
    {
        if (DumpDir is null) return -1;
        if (DisableAllDumps) return -1;
        lock (s_lock)
        {
            s_counters.TryGetValue(bucket, out int n);
            if (n >= maxPerBucket) return -1;
            s_counters[bucket] = n + 1;
            return n;
        }
    }

    /// <summary>Returns true on a fixed set of frame indices so we can spot-check
    /// content at points spread across the timeline (verifying the pipeline
    /// stays correct deep into the export, not just at the start).</summary>
    public static bool ShouldDumpAtCheckpoint(int frameIndex)
    {
        if (DumpDir is null) return false;
        if (DisableAllDumps) return false;
        // Denser checkpoints near the start so we can localise the cliff
        // (we know composite at frame 60 is good and frame 600 is black).
        return frameIndex is 60 or 90 or 120 or 180 or 240 or 300 or 420 or 600 or 1200 or 1800 or 2400;
    }

    /// <summary>The output-frame index the export loop is currently processing.
    /// Stages downstream of the orchestrator (source pool, compositor) read this
    /// to decide whether to fire a checkpoint dump.</summary>
    public static int CurrentFrame { get; private set; }
    public static void SetCurrentFrame(int frame) => CurrentFrame = frame;

    public static void DumpRT(CanvasBitmap rt, string fileName, ExportLogger? log)
    {
        if (DumpDir is null) return;
        try
        {
            var path = Path.Combine(DumpDir, fileName);
            rt.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png).AsTask().Wait();
            log?.Log($"   [diag] dumped {fileName}");
        }
        catch (Exception ex)
        {
            log?.Log($"   [diag] FAILED to dump {fileName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void DumpTexture(CanvasDevice device, ID3D11Texture2D tex, string fileName, ExportLogger? log)
    {
        if (DumpDir is null) return;
        try
        {
            using var bmp = Win2DInterop.WrapAsCanvasBitmap(device, tex);
            var path = Path.Combine(DumpDir, fileName);
            bmp.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png).AsTask().Wait();
            log?.Log($"   [diag] dumped {fileName}");
        }
        catch (Exception ex)
        {
            log?.Log($"   [diag] FAILED to dump {fileName}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
