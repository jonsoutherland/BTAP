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

                // CanvasRenderTarget hands us BGRA top-down (Y=0 at the top of the
                // image). MediaFoundation's BGRA8 uncompressed-video convention is
                // bottom-up — feeding top-down bytes makes the encoder flip the frame
                // vertically. Reverse the rows so the output reads as expected.
                var bytes  = frame.GetPixelBytes();
                int w      = (int)frame.SizeInPixels.Width;
                int h      = (int)frame.SizeInPixels.Height;
                int stride = w * 4;
                var flipped = new byte[bytes.Length];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(bytes, y * stride, flipped, (h - 1 - y) * stride, stride);

                var buffer = flipped.AsBuffer();
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
