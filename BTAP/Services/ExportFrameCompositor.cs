using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.Effects;
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
    private readonly CanvasRenderTarget _canvas;  // canvas-sized intermediate (clip layout space)
    private readonly CanvasRenderTarget _output;  // export-sized final (cropped + scaled)
    private readonly int _canvasW;
    private readonly int _canvasH;
    private readonly bool _needsCropPass;

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

        // When canvas dimensions match the export resolution exactly, the second
        // pass is identity work — skip allocating a separate intermediate.
        _needsCropPass = _canvasW != project.Width || _canvasH != project.Height;

        _output = new CanvasRenderTarget(device, project.Width, project.Height, 96);
        _canvas = _needsCropPass
            ? new CanvasRenderTarget(device, _canvasW, _canvasH, 96)
            : _output;
    }

    /// <summary>Composes one frame at <paramref name="playhead"/>. Returns the
    /// internal output render target — caller must not dispose it.</summary>
    public CanvasRenderTarget RenderFrame(TimeSpan playhead,
                                          IReadOnlyDictionary<TimelineClip, CanvasBitmap> layerFrames)
    {
        // Stage 1: render the scene onto the canvas (clip PosX/PosY are in
        // canvas-pixel units, layout matches what the preview shows).
        using (var ds = _canvas.CreateDrawingSession())
        {
            ds.Clear(Colors.Black);

            for (int i = _project.Tracks.Count - 1; i >= 0; i--)
            {
                var track = _project.Tracks[i];
                if (track.Kind != TrackKind.Video || track.IsMuted) continue;
                var clip = FirstClipAt(track, playhead);
                if (clip is null) continue;
                if (clip.Kind == ClipKind.Title) continue;
                if (string.IsNullOrEmpty(clip.SourceId)) continue;
                if (!layerFrames.TryGetValue(clip, out var frame) || frame is null) continue;
                DrawVideoLayer(ds, frame, clip);
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

        // Stage 2: crop the export window out of the canvas and scale to the
        // export resolution. Skipped when the canvas already matches the export
        // (then _canvas IS _output and the scene drew straight into it).
        if (_needsCropPass)
        {
            var (wx, wy, wW, wH) = _project.GetExportWindow();
            using var ds2 = _output.CreateDrawingSession();
            ds2.Clear(Colors.Black);
            var srcRect = new Rect(wx, wy, Math.Max(1, wW), Math.Max(1, wH));
            var dstRect = new Rect(0, 0, _project.Width, _project.Height);
            ds2.DrawImage(_canvas, dstRect, srcRect);
        }

        return _output;
    }

    private static TimelineClip? FirstClipAt(Track t, TimeSpan p)
    {
        foreach (var c in t.Clips)
            if (p >= c.TimelineStart && p < c.TimelineEnd) return c;
        return null;
    }

    private void DrawVideoLayer(CanvasDrawingSession ds, CanvasBitmap frame, TimelineClip clip)
    {
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

        // Destination: canvas frame, per-clip scale, per-clip offset, then carve
        // out the cropped sub-rect so cropped pixels keep their original on-screen
        // size instead of stretching to fill the un-cropped dest. PosX/PosY are
        // in canvas-pixel space (matches the preview).
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double fullW = _canvasW * scale;
        double fullH = _canvasH * scale;
        double fullX = (_canvasW - fullW) / 2 + clip.PosX;
        double fullY = (_canvasH - fullH) / 2 + clip.PosY;
        double dstX = fullX + cl * fullW;
        double dstY = fullY + ct * fullH;
        double dstW = Math.Max(1, (1 - cl - cr) * fullW);
        double dstH = Math.Max(1, (1 - ct - cb) * fullH);
        var destRect = new Rect(dstX, dstY, dstW, dstH);

        float opacity = (float)Math.Clamp(clip.Opacity, 0, 1);

        // Color-grading effect chain rooted at the source frame. Built and disposed per
        // call — these are lightweight ICanvasEffect graphs, not GPU resources themselves.
        var graph = new List<IDisposable>();
        try
        {
            IGraphicsEffectSource src = frame;

            if (Math.Abs(clip.Exposure) > 0.0001)
            {
                var e = new ExposureEffect { Source = src, Exposure = Clamp((float)clip.Exposure, -2f, 2f) };
                graph.Add(e); src = e;
            }
            if (Math.Abs(clip.Contrast) > 0.0001)
            {
                var e = new ContrastEffect { Source = src, Contrast = Clamp((float)clip.Contrast / 100f, -1f, 1f) };
                graph.Add(e); src = e;
            }
            if (Math.Abs(clip.Saturation) > 0.0001)
            {
                var e = new SaturationEffect { Source = src, Saturation = Clamp(1f + (float)clip.Saturation / 100f, 0f, 2f) };
                graph.Add(e); src = e;
            }
            if (Math.Abs(clip.Temperature) > 0.0001 || Math.Abs(clip.Tint) > 0.0001)
            {
                var e = new TemperatureAndTintEffect
                {
                    Source = src,
                    Temperature = Clamp((float)clip.Temperature / 100f, -1f, 1f),
                    Tint        = Clamp((float)clip.Tint        / 100f, -1f, 1f),
                };
                graph.Add(e); src = e;
            }
            foreach (var fx in clip.Effects)
            {
                if (!fx.Enabled) continue;
                if (fx.Name == "Gaussian Blur")
                {
                    float blur = (float)(Math.Clamp(fx.Intensity, 0.0, 1.0) * 50.0);
                    if (blur > 0.01f)
                    {
                        var e = new GaussianBlurEffect { Source = src, BlurAmount = blur, BorderMode = EffectBorderMode.Hard };
                        graph.Add(e); src = e;
                    }
                }
            }

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

    private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

    public void Dispose()
    {
        try { _output.Dispose(); } catch { }
        // Only dispose _canvas when it's a distinct render target. When the canvas
        // matches the export size we alias _canvas to _output above, so disposing
        // both would double-dispose the same surface.
        if (_needsCropPass)
            try { _canvas.Dispose(); } catch { }
    }
}
