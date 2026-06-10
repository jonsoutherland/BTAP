using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// A <see cref="MediaStreamSource"/> that emits one composited video frame per
/// <see cref="MediaStreamSourceSampleRequestedEventArgs"/> call. The renderer
/// delegate produces a fully-composited <see cref="CanvasRenderTarget"/> at the
/// requested composition time; its BGRA bytes are wrapped as a sample.
/// </summary>
public sealed class ExportVideoStreamSource : IDisposable
{
    public delegate Task<CanvasRenderTarget?> FrameRenderer(TimeSpan compositionTime, CancellationToken ct);

    private readonly FrameRenderer _render;
    private readonly TimeSpan      _duration;
    private readonly TimeSpan      _frameDuration;
    private readonly ExportLogger? _log;
    private readonly CancellationTokenSource _cts = new();
    private long _frameIndex;

    // Per-frame the encoder needs a Width*Height*4 byte buffer (Bgra8). At
    // 1080×1920 that's 8 MB, which is on the Large Object Heap — every frame
    // freshly allocated triggers Gen-2 GCs every few seconds and was a major
    // contributor to the slow export. Rotate through a small fixed set of
    // buffers; the MediaTranscoder's pipeline depth is bounded so by the time
    // we're back at buffer index N, sample N has been consumed.
    private const int  PixelBufferCount = 4;
    private readonly byte[]?[] _pixelBuffers = new byte[PixelBufferCount][];
    private int _pixelBufferIdx;

    public MediaStreamSource StreamSource { get; }

    public ExportVideoStreamSource(Project project, TimeSpan duration, FrameRenderer renderer, ExportLogger? log = null)
    {
        _render        = renderer;
        _duration      = duration;
        _frameDuration = TimeSpan.FromSeconds(1.0 / Math.Max(1, project.FrameRate));
        _log           = log;

        // Bgra8 matches what CanvasRenderTarget.GetPixelBytes() returns (BGRA, little-endian).
        // The transcode pipeline converts to NV12 for the H.264 encoder.
        var videoProps = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, (uint)project.Width, (uint)project.Height);
        videoProps.FrameRate.Numerator   = (uint)Math.Max(1, Math.Round(project.FrameRate));
        videoProps.FrameRate.Denominator = 1;
        videoProps.PixelAspectRatio.Numerator   = 1;
        videoProps.PixelAspectRatio.Denominator = 1;
        videoProps.Bitrate = (uint)(project.Width * project.Height * project.FrameRate * 0.1); // rough — final encode profile sets the real bitrate

        var desc = new VideoStreamDescriptor(videoProps);
        StreamSource = new MediaStreamSource(desc) { Duration = duration, CanSeek = false };
        StreamSource.Starting        += OnStarting;
        StreamSource.SampleRequested += OnSampleRequested;
        StreamSource.Closed          += OnClosed;
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        args.Request.SetActualStartPosition(TimeSpan.Zero);
        _frameIndex = 0;
    }

    private DateTime _lastHeartbeat = DateTime.MinValue;

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var req      = args.Request;
        var deferral = req.GetDeferral();
        _ = Task.Run(async () =>
        {
            try
            {
                var time = TimeSpan.FromTicks(_frameIndex * _frameDuration.Ticks);
                if (time >= _duration)
                {
                    req.Sample = null; // EOF
                    return;
                }

                // Heartbeat: log progress no more than once per second so a stalled
                // pipeline is distinguishable from a slow one in the export log.
                var now = DateTime.UtcNow;
                if ((now - _lastHeartbeat).TotalSeconds >= 1.0)
                {
                    _log?.Log($"   [video stream] frame {_frameIndex} @ {time} / {_duration}");
                    _lastHeartbeat = now;
                }

                var frame = await _render(time, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    req.Sample = null;
                    return;
                }

                // ExportFrameCompositor renders the scene upside-down on the GPU
                // (Y-axis negated in its output transform), so reading the RT
                // top-down already produces the bottom-up byte order MF's BGRA8
                // uncompressed-video convention expects.
                //
                // Buffer reuse: rotate through PixelBufferCount fixed buffers so
                // we don't allocate 8 MB per frame on the LOH. The transcoder
                // pipeline holds onto a few samples at a time before consuming,
                // so a small ring is enough; by the time we revisit a slot the
                // encoder has long since released that sample's buffer.
                int w        = (int)frame.SizeInPixels.Width;
                int h        = (int)frame.SizeInPixels.Height;
                int required = w * h * 4;
                int idx      = _pixelBufferIdx;
                _pixelBufferIdx = (idx + 1) % PixelBufferCount;
                var buf = _pixelBuffers[idx];
                if (buf is null || buf.Length != required)
                {
                    buf = new byte[required];
                    _pixelBuffers[idx] = buf;
                }
                var buffer = buf.AsBuffer();
                frame.GetPixelBytes(buffer);
                var sample = MediaStreamSample.CreateFromBuffer(buffer, time);
                sample.Duration = _frameDuration;
                sample.KeyFrame = true;
                req.Sample      = sample;
                _frameIndex++;
            }
            catch (OperationCanceledException)
            {
                req.Sample = null;
            }
            catch (Exception ex)
            {
                _log?.Log($"   [video stream] SampleRequested EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                req.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
    {
        try { _cts.Cancel(); } catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { StreamSource.Starting        -= OnStarting; } catch { }
        try { StreamSource.SampleRequested -= OnSampleRequested; } catch { }
        try { StreamSource.Closed          -= OnClosed; } catch { }
    }
}
