using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Headless Win2D compositor for the export pipeline. Produces a fully-composited
/// frame at project resolution with every per-clip transform (Scale/Pos/Rotation/
/// Opacity/Crop/Flip), the color-grading effect chain (Exposure/Contrast/Saturation/
/// Temperature/Tint/Blur), and title text rendered with CanvasTextFormat — what the
/// preview shows, baked into pixels for encoding.
/// </summary>
public sealed class ExportFrameCompositor : IDisposable
{
    private readonly Project _project;
    private readonly CanvasRenderTarget _output;     // persistent export-sized RT, reused across frames
    private readonly int _canvasW;
    private readonly int _canvasH;
    private readonly Matrix3x2 _canvasToOutput;   // maps canvas-pixel space → output-pixel space

    public CanvasDevice Device { get; }
    public int Width  => _project.Width;
    public int Height => _project.Height;

    public ExportFrameCompositor(Project project, CanvasDevice device)
    {
        _project = project;
        Device   = device;
        var (cw, ch) = project.GetCanvasSize();
        _canvasW = Math.Max(1, cw);
        _canvasH = Math.Max(1, ch);

        // Persistent single RT, reused across frames. Earlier we briefly
        // switched this to allocate-fresh-per-frame on the theory that
        // cross-frame Win2D state on the reused RT was wedging DrawImage at
        // 60 fps. That didn't help — the bug actually has nothing to do with
        // RT state carryover. Reverting to persistent eliminates one of the
        // per-frame GPU texture allocations (the other was in the sink), so
        // the total alloc churn at 60 fps drops enough for the driver to
        // keep up with VRAM release.
        _output = new CanvasRenderTarget(device, project.Width, project.Height, 96);

        // Compose directly into _output. The whole scene lives in canvas-pixel
        // space (clip PosX/PosY are canvas units, matching the preview), but the
        // export only cares about pixels inside the export window. One transform
        // — translate so the export window's top-left lands at (0,0), then scale
        // it to the output rect — does both the crop and the resize in a single
        // pass at output resolution rather than rasterizing the full canvas.
        //
        // Note: the SinkWriter pipeline hands the output texture directly to
        // the H.264 encoder via an MF DXGI surface buffer, so the encoder reads
        // the pixels in their native top-down GPU layout. No Y-flip needed —
        // the earlier version of this transform negated Y to compensate for
        // GetPixelBytes' top-down byte order vs MF's bottom-up CPU convention,
        // but that whole readback path is gone now.
        var (wx, wy, wW, wH) = project.GetExportWindow();
        float sx = (float)(project.Width  / Math.Max(1, wW));
        float sy = (float)(project.Height / Math.Max(1, wH));
        _canvasToOutput = Matrix3x2.CreateTranslation((float)-wx, (float)-wy)
                       * Matrix3x2.CreateScale(sx, sy);
    }

    /// <summary>Composes one frame at <paramref name="playhead"/>. Returns the
    /// internal output render target — caller must not dispose it. The
    /// SinkRenderer is expected to copy from this into a fresh per-frame
    /// texture for the encoder.</summary>
    public CanvasRenderTarget RenderFrame(TimeSpan playhead,
                                          IReadOnlyDictionary<TimelineClip, CanvasBitmap> layerFrames,
                                          ExportLogger? log = null)
    {
        using (var ds = _output.CreateDrawingSession())
        {
            ds.Clear(Colors.Black);
            ds.Transform = _canvasToOutput;

            for (int i = _project.Tracks.Count - 1; i >= 0; i--)
            {
                var track = _project.Tracks[i];
                if (track.Kind != TrackKind.Video || track.IsMuted) continue;
                var clip = FirstClipAt(track, playhead);
                if (clip is null) continue;
                if (clip.Kind == ClipKind.Title) continue;
                if (string.IsNullOrEmpty(clip.SourceId)) continue;
                if (!layerFrames.TryGetValue(clip, out var frame) || frame is null) continue;
                DrawVideoLayer(ds, frame, clip, playhead);
            }

            foreach (var track in _project.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip.Kind != ClipKind.Title) continue;
                    if (playhead < clip.TimelineStart || playhead >= clip.TimelineEnd) continue;
                    DrawTitle(ds, clip);
                }
            }
        }

        // Diagnostic: dump the composited output for the first few frames so
        // we can tell whether the compositor produced visible pixels. Plus a
        // checkpoint dump on the orchestrator's checkpoint frames so we can
        // distinguish "compositor went black" from "CopyResource into encoder
        // texture broke" — three-stage triangulation (staging / composite /
        // encoder-input) localises the failure to one stage.
        int diagIdx = ExportDiag.NextIndex("compositor", 3);
        if (diagIdx >= 0)
            ExportDiag.DumpRT(_output, $"2-composite-{diagIdx}.png", log);
        else if (ExportDiag.ShouldDumpAtCheckpoint(ExportDiag.CurrentFrame))
            ExportDiag.DumpRT(_output, $"2-composite-checkpoint-{ExportDiag.CurrentFrame}.png", log);

        return _output;
    }

    private static TimelineClip? FirstClipAt(Track t, TimeSpan p)
    {
        foreach (var c in t.Clips)
            if (p >= c.TimelineStart && p < c.TimelineEnd) return c;
        return null;
    }

    private void DrawVideoLayer(CanvasDrawingSession ds, CanvasBitmap frame, TimelineClip clip, TimeSpan playhead)
    {
        double clipTimeRel = clip.Duration.TotalSeconds > 0
            ? Math.Clamp((playhead - clip.TimelineStart).TotalSeconds / clip.Duration.TotalSeconds, 0, 1)
            : 0;

        // Source rect with crop (CropLeft/Top/Right/Bottom are 0..1 fractions)
        double srcW = frame.SizeInPixels.Width;
        double srcH = frame.SizeInPixels.Height;
        double cl = Math.Clamp(clip.CropLeft,   0, 0.95);
        double ct = Math.Clamp(clip.CropTop,    0, 0.95);
        double cr = Math.Clamp(clip.CropRight,  0, 0.95);
        double cb = Math.Clamp(clip.CropBottom, 0, 0.95);
        var srcRect = new Rect(cl * srcW, ct * srcH,
                               Math.Max(1, (1 - cl - cr) * srcW),
                               Math.Max(1, (1 - ct - cb) * srcH));

        // Destination: source fitted into the canvas at its native aspect
        // (letterbox/pillarbox so portrait clips don't stretch into a landscape
        // canvas, and vice versa), then per-clip Scale and PosX/PosY (canvas-
        // pixel space, matches the preview). Then carve out the cropped sub-rect
        // so cropped pixels keep their original on-screen size instead of
        // stretching to fill the un-cropped dest.
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double srcAspect = srcW / srcH;
        double canvasAspect = (double)_canvasW / _canvasH;
        double fitW, fitH;
        if (srcAspect >= canvasAspect)
        {
            fitW = _canvasW;
            fitH = _canvasW / srcAspect;
        }
        else
        {
            fitH = _canvasH;
            fitW = _canvasH * srcAspect;
        }
        double fullW = fitW * scale;
        double fullH = fitH * scale;
        double fullX = (_canvasW - fullW) / 2 + clip.PosX;
        double fullY = (_canvasH - fullH) / 2 + clip.PosY;
        double dstX = fullX + cl * fullW;
        double dstY = fullY + ct * fullH;
        double dstW = Math.Max(1, (1 - cl - cr) * fullW);
        double dstH = Math.Max(1, (1 - ct - cb) * fullH);
        var destRect = new Rect(dstX, dstY, dstW, dstH);

        float opacity = (float)Math.Clamp(clip.Opacity, 0, 1);

        // Color-grading + named-effect chain rooted at the source frame. Built and
        // disposed per call — these are lightweight ICanvasEffect graphs, not GPU
        // resources themselves. Shared with VideoCompositorControl so the export
        // matches the preview pixel-for-pixel.
        var graph = new List<IDisposable>();
        try
        {
            var src = ClipEffectsChain.Build(frame, clip, clipTimeRel, graph);

            // Flip + rotation around the clip's center. ds.Transform applies to DrawImage's
            // destination, leaving srcRect untouched.
            var center = new Vector2((float)(dstX + dstW / 2), (float)(dstY + dstH / 2));
            var oldTransform = ds.Transform;
            var t = Matrix3x2.Identity;
            if (clip.FlipX || clip.FlipY)
                t = Matrix3x2.CreateScale(clip.FlipX ? -1f : 1f, clip.FlipY ? -1f : 1f, center);
            if (Math.Abs(clip.Rotation) > 0.0001)
                t = t * Matrix3x2.CreateRotation((float)(clip.Rotation * Math.PI / 180.0), center);
            if (t != Matrix3x2.Identity) ds.Transform = t;

            try
            {
                ds.DrawImage((ICanvasImage)src, destRect, srcRect, opacity);
            }
            finally
            {
                ds.Transform = oldTransform;
            }
        }
        finally
        {
            // Dispose effects in reverse order so chained sources release cleanly
            for (int i = graph.Count - 1; i >= 0; i--)
                try { graph[i].Dispose(); } catch { }
        }
    }

    private void DrawTitle(CanvasDrawingSession ds, TimelineClip clip)
    {
        if (string.IsNullOrWhiteSpace(clip.Label)) return;

        var fmt = new CanvasTextFormat
        {
            FontFamily         = clip.FontFamily,
            FontSize           = (float)Math.Max(6, clip.FontSize),
            FontWeight         = clip.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle          = clip.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            HorizontalAlignment = clip.TextAlign switch
            {
                "Left"  => CanvasHorizontalAlignment.Left,
                "Right" => CanvasHorizontalAlignment.Right,
                _       => CanvasHorizontalAlignment.Center,
            },
            VerticalAlignment   = CanvasVerticalAlignment.Center,
            WordWrapping        = CanvasWordWrapping.Wrap,
        };

        try
        {
            // PosX/PosY are canvas-pixel offsets from center, matching the preview's
            // TranslateTransform applied to a text block sized to the preview viewport.
            float layoutW = _canvasW;
            float layoutH = _canvasH;
            using var layout = new CanvasTextLayout(Device, clip.Label, fmt, layoutW, layoutH);
            if (clip.IsUnderline)
                try { layout.SetUnderline(0, clip.Label.Length, true); } catch { }

            float dx = (float)clip.PosX;
            float dy = (float)clip.PosY;
            ds.DrawTextLayout(layout, dx, dy, ParseColor(clip.TextColor));
        }
        finally
        {
            fmt.Dispose();
        }
    }

    private static Color ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Colors.White;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return Colors.White;
        if (!byte.TryParse(s[..2],            System.Globalization.NumberStyles.HexNumber, null, out var a)) return Colors.White;
        if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return Colors.White;
        if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return Colors.White;
        if (!byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return Colors.White;
        return Color.FromArgb(a, r, g, b);
    }

    public void Dispose()
    {
        try { _output.Dispose(); } catch { }
    }
}
