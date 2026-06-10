using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BTAP.Services;

/// <summary>
/// Owns one frame-server <see cref="MediaPlayer"/> per source file. Streams
/// frames forward by alternating Play() (in the consumer) with Pause() (in the
/// handler, immediately after copy). This avoids seeking on every output frame
/// — a per-frame Position=t triggers a keyframe walk on 4K H.264, which is
/// what was making each pool call take 100ms–2.5s.
///
/// The trick that prevents the stutter we hit on the earlier attempt: each
/// successful copy records the source position it captured, and the handler
/// refuses to deliver a frame whose position isn't strictly later than that.
/// MediaPlayer's Pause → Play cycle re-emits the buffered frame at the same
/// position first, then advances; without this guard the consumer would get
/// the same frame for many output frames in a row before the player advanced.
///
/// A seek is still issued on the first call per source, when the consumer
/// rewinds, or when it jumps more than a couple of seconds ahead.
/// </summary>
public sealed class ExportFrameSourcePool : IDisposable
{
    private sealed class Source
    {
        public string Path = "";
        public MediaPlayer Player = null!;
        public CanvasRenderTarget? Frame;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool MediaOpened;
        public TaskCompletionSource<bool> Opened = new();
        public TaskCompletionSource<bool>? TargetReached;
        public TimeSpan Target;
        public TimeSpan LastCopiedPosition = TimeSpan.FromTicks(-1); // -1 == nothing copied yet
        public TimeSpan SourceFrameInterval;   // measured from successive copies
        public bool SourceIntervalMeasured;
        public bool Started;
    }

    private readonly CanvasDevice _device;
    private readonly ConcurrentDictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExportLogger? _log;

    public ExportFrameSourcePool(CanvasDevice device, ExportLogger? log = null)
    {
        _device = device;
        _log    = log;
    }

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

            player.MediaOpened         += (_, _) => { src.MediaOpened = true; src.Opened.TrySetResult(true); };
            player.MediaFailed         += (_, args) => { src.Opened.TrySetException(new InvalidOperationException(args.ErrorMessage ?? "MediaFailed")); };
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

            // Frame-rate-aware sampling: if the source's last copied frame still
            // covers the requested output time (i.e., we're still within one
            // source-frame interval of it), return the cached frame instead of
            // stepping the decoder. Without this, a 30fps source on a 60fps
            // timeline advances by one source frame per output frame → the
            // source plays at 2× speed and looks stuttery. With this, each
            // source frame is held for the right number of output frames.
            if (src.SourceIntervalMeasured
                && src.LastCopiedPosition >= TimeSpan.Zero
                && sourceTime >= src.LastCopiedPosition
                && sourceTime < src.LastCopiedPosition + src.SourceFrameInterval)
            {
                return src.Frame;
            }

            var current = session.Position;
            var diff    = sourceTime - current;

            // Seek when we have to: first call, rewind, or a forward jump big
            // enough that single-stepping would burn many wall-clock seconds.
            bool needSeek = !src.Started
                         || diff < TimeSpan.FromMilliseconds(-50)
                         || diff > TimeSpan.FromSeconds(2);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            src.Target = sourceTime;
            src.TargetReached = tcs;

            if (needSeek)
            {
                try { session.Position = sourceTime; } catch { }
                // After a seek there's no meaningful "last copied position" —
                // accept whatever the next post-seek frame happens to be.
                src.LastCopiedPosition = TimeSpan.FromTicks(-1);
                src.SourceIntervalMeasured = false;
            }

            // PlaybackRate stays at 1.0. At 2× the player over-advances during
            // the brief window between Play() and Pause() taking effect — it
            // emits several frames before actually stopping, the recorded
            // LastCopiedPosition jumps multiple source-frame intervals per
            // cycle, and the measured SourceFrameInterval ends up totally
            // wrong (e.g. ~100ms for a 30fps source). With 1× the player
            // single-steps cleanly. The wall-clock cost is meaningful but the
            // alternative is junk frame timing.
            src.Started = true;

            // Single-step the player: Play → produces one frame → handler
            // copies it and immediately Pauses. The handler's LastCopiedPosition
            // check makes sure the frame we copy is actually new and not the
            // stale buffer re-emit MediaPlayer fires on resume.
            try { src.Player.Play(); } catch { }

            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(2500, ct)).ConfigureAwait(false);
                bool frameArrived = winner == tcs.Task;
                if (!frameArrived)
                {
                    _log?.Log($"   [pool] frame timeout (2.5s) for {System.IO.Path.GetFileName(filePath)} @ {sourceTime} pos={current}");
                    Interlocked.CompareExchange(ref src.TargetReached, null, tcs);
                    try { src.Player.Pause(); } catch { }
                }
            }

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
            var waiter = src.TargetReached;
            if (waiter is null) return;

            var pos = sender.PlaybackSession.Position;
            if (pos < src.Target) return;

            // Reject the same frame we already delivered last call. After
            // Pause → Play, MediaPlayer's first VideoFrameAvailable re-emits
            // the already-decoded buffer at its current position. Copying it
            // again would give the consumer the same frame twice in a row;
            // wait until the decoder actually advances past the last
            // delivered position.
            if (pos <= src.LastCopiedPosition) return;

            if (src.Frame is null) return;
            int w = (int)sender.PlaybackSession.NaturalVideoWidth;
            int h = (int)sender.PlaybackSession.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return;
            if ((int)src.Frame.SizeInPixels.Width != w || (int)src.Frame.SizeInPixels.Height != h)
                return;

            // Atomically claim the waiter so only this invocation writes into
            // src.Frame. Without it a second VideoFrameAvailable can fire
            // between TrySetResult and the consumer setting TargetReached=null,
            // overwriting src.Frame WHILE the compositor is reading from it.
            if (Interlocked.CompareExchange(ref src.TargetReached, null, waiter) != waiter)
                return;

            try
            {
                // Serialize across every FrameServer player (preview compositor
                // included). Concurrent CopyFrameToVideoSurface AVs MF.
                lock (NativeFrameCopyGate.Lock)
                {
                    sender.CopyFrameToVideoSurface(src.Frame);
                }

                // Measure the source's native frame interval as the MINIMUM
                // observed delta between successive copies. Single-step
                // playback isn't perfectly clean — sometimes the player
                // over-advances by 2+ frames before Pause() takes effect, and
                // we'd see a delta of 2×interval. Tracking the minimum means
                // those bad measurements don't poison the interval value; the
                // true source-frame interval is whatever shows up at least
                // once and is never undershot.
                if (src.LastCopiedPosition >= TimeSpan.Zero)
                {
                    var delta = pos - src.LastCopiedPosition;
                    if (delta > TimeSpan.FromMilliseconds(5) && delta < TimeSpan.FromMilliseconds(200))
                    {
                        if (!src.SourceIntervalMeasured || delta < src.SourceFrameInterval)
                        {
                            src.SourceFrameInterval    = delta;
                            src.SourceIntervalMeasured = true;
                        }
                    }
                }
                src.LastCopiedPosition = pos;

                // Pause immediately — otherwise the player keeps decoding while
                // the consumer is busy composing/encoding, and the next call
                // sees the player far past target → backward seek → keyframe
                // walk → 2.5s timeout. That was the original perf killer.
                try { sender.Pause(); } catch { }
            }
            finally
            {
                waiter.TrySetResult(true);
            }
        }
        catch { /* transient — drop */ }
    }

    public void Dispose()
    {
        foreach (var src in _sources.Values)
        {
            try { src.Player.VideoFrameAvailable -= OnVideoFrameAvailable; } catch { }
            try { src.Player.Pause();             } catch { }
            try { src.Player.Source = null;       } catch { }
            try { src.Player.Dispose();           } catch { }
            try { src.Frame?.Dispose();           } catch { }
            try { src.Gate.Dispose();             } catch { }
        }
        _sources.Clear();
    }
}
