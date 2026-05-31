using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BTAP.Services;

/// <summary>
/// Owns one paused, frame-server-mode <see cref="MediaPlayer"/> per source file
/// referenced by the timeline. Exposes a single async API to fetch the decoded
/// frame at an exact source timestamp into a per-source <see cref="CanvasRenderTarget"/>.
/// Requests are serialized per source so a seek can't race the frame copy.
/// </summary>
public sealed class ExportFrameSourcePool : IDisposable
{
    private sealed class Source
    {
        public string Path = "";
        public MediaPlayer Player = null!;
        public CanvasRenderTarget? Frame;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public TaskCompletionSource<bool>? PendingFrame;
        public bool MediaOpened;
        public TaskCompletionSource<bool> Opened = new();
        public TimeSpan? LastDecodedTime;
    }

    private readonly CanvasDevice _device;
    private readonly ConcurrentDictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExportLogger? _log;

    public ExportFrameSourcePool(CanvasDevice device, ExportLogger? log = null)
    {
        _device = device;
        _log    = log;
    }

    /// <summary>Pre-opens a source file. Safe to call multiple times for the same path.</summary>
    public async Task PrepareAsync(string filePath)
    {
        if (_sources.ContainsKey(filePath)) return;

        var src = new Source { Path = filePath };
        if (!_sources.TryAdd(filePath, src)) return;

        try
        {
            var player = new MediaPlayer
            {
                AutoPlay                  = false,
                IsMuted                   = true,
                IsVideoFrameServerEnabled = true,
            };
            src.Player = player;

            player.MediaOpened       += (_, _) => { src.MediaOpened = true; src.Opened.TrySetResult(true); };
            player.MediaFailed       += (_, args) => { src.Opened.TrySetException(new InvalidOperationException(args.ErrorMessage ?? "MediaFailed")); };
            player.VideoFrameAvailable += OnVideoFrameAvailable;

            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var ms   = MediaSource.CreateFromStorageFile(file);
            player.Source = new MediaPlaybackItem(ms);

            await src.Opened.Task.ConfigureAwait(false);
            _log?.Log($"   [pool] opened {System.IO.Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            _log?.Log($"   [pool] FAILED to open {filePath}: {ex.GetType().Name}: {ex.Message}");
            _sources.TryRemove(filePath, out _);
            throw;
        }
    }

    /// <summary>Fetches the frame at <paramref name="sourceTime"/>. The returned
    /// <see cref="CanvasBitmap"/> is owned by the pool; it's reused on the next call
    /// for the same source. Must be consumed before the next call for that source.</summary>
    public async Task<CanvasBitmap?> GetFrameAsync(string filePath, TimeSpan sourceTime,
                                                   CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(filePath, out var src)) return null;
        if (!src.MediaOpened) await src.Opened.Task.ConfigureAwait(false);

        await src.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = src.Player.PlaybackSession;
            if (sourceTime < TimeSpan.Zero) sourceTime = TimeSpan.Zero;
            if (session.NaturalDuration > TimeSpan.Zero && sourceTime > session.NaturalDuration)
                sourceTime = session.NaturalDuration;

            int w = (int)session.NaturalVideoWidth;
            int h = (int)session.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return null;

            if (src.Frame is null
                || (int)src.Frame.SizeInPixels.Width  != w
                || (int)src.Frame.SizeInPixels.Height != h)
            {
                try { src.Frame?.Dispose(); } catch { }
                src.Frame = new CanvasRenderTarget(_device, w, h, 96);
            }

            // Position assignment resets the decoder and walks from the nearest
            // keyframe. It's the only primitive on MediaPlaybackSession that
            // reliably fires VideoFrameAvailable in our frame-server-paused setup
            // (StepForwardOneFrame does NOT — tried, it silently no-ops here).
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            src.PendingFrame = tcs;
            try { session.Position = sourceTime; } catch { }

            bool frameArrived;
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(2500, ct)).ConfigureAwait(false);
                frameArrived = winner == tcs.Task;
                if (!frameArrived)
                    _log?.Log($"   [pool] frame timeout (2.5s) for {System.IO.Path.GetFileName(filePath)} @ {sourceTime} — using last decoded frame");
            }
            src.PendingFrame = null;
            if (frameArrived) src.LastDecodedTime = sourceTime;

            return src.Frame;
        }
        finally
        {
            src.Gate.Release();
        }
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        Source? src = null;
        foreach (var kv in _sources)
            if (ReferenceEquals(kv.Value.Player, sender)) { src = kv.Value; break; }
        if (src is null) return;

        try
        {
            if (src.Frame is null) return;
            int w = (int)sender.PlaybackSession.NaturalVideoWidth;
            int h = (int)sender.PlaybackSession.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return;
            if ((int)src.Frame.SizeInPixels.Width != w || (int)src.Frame.SizeInPixels.Height != h)
                return; // wait for the next call to resize
            sender.CopyFrameToVideoSurface(src.Frame);
        }
        catch { /* transient — drop */ }
        finally
        {
            src.PendingFrame?.TrySetResult(true);
        }
    }

    public void Dispose()
    {
        foreach (var src in _sources.Values)
        {
            try { src.Player.VideoFrameAvailable -= OnVideoFrameAvailable; } catch { }
            try { src.Player.Source = null; } catch { }
            try { src.Player.Dispose(); } catch { }
            try { src.Frame?.Dispose(); } catch { }
            try { src.Gate.Dispose(); } catch { }
        }
        _sources.Clear();
    }
}
