using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using BTAP.Models;
using BTAP.Services;
using BTAP.ViewModels;

namespace BTAP.Controls;

/// <summary>
/// Custom video compositor: one CanvasAnimatedControl (single GPU swap chain) draws every
/// active video clip on every frame. Per-clip frames come from MediaPlayer's frame-server
/// path (CopyFrameToVideoSurface) into per-clip CanvasRenderTargets. Layers stack in track
/// order — deeper tracks behind, shallower in front — with each clip's Scale, PosX/Y,
/// Rotation, Opacity, and Crop applied as draw-time transforms.
/// </summary>
public sealed partial class VideoCompositorControl : UserControl
{
    private sealed class Layer
    {
        public Track Track = null!;
        public TimelineClip Clip = null!;
        public MediaPlayer Player = null!;
        public CanvasRenderTarget? Frame;
        public readonly object FrameLock = new();
        public string SourcePath = string.Empty;
        public bool MediaOpened;
        public bool Disposed;
        public long FrameCount;       // diagnostic: per-layer frame arrivals
    }

    private EditorViewModel? _vm;
    private CanvasDevice? _device;
    private readonly List<Layer> _layers = [];   // deepest first → drawn first → on back
    private readonly object _layersLock = new(); // guards _layers across UI / worker / render threads
    private bool _isPlaying;
    private DateTime _lastSyncHeartbeatUtc = DateTime.MinValue;


    // The preview canvas size — derived from the first imported video clip's
    // native resolution, with a fallback to the project's export dimensions when
    // no video has been imported yet. The project's Width/Height define only the
    // EXPORT crop window inside this canvas (rendered as a dashed overlay by
    // EditorPage). Reading live from the VM keeps the letterbox and per-clip
    // transform math in sync with changes to either canvas-source or export size.
    public int OutputWidth  => _vm?.Project.GetCanvasSize().Width  ?? 1920;
    public int OutputHeight => _vm?.Project.GetCanvasSize().Height ?? 1080;

    // When non-null, the clip with this Id is drawn ignoring its CropL/T/R/B so
    // the user can see the full source frame while picking a crop region.
    public string? BypassCropClipId { get; set; }

    public VideoCompositorControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisposeAllLayers();
    }

    public void Attach(EditorViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        DisposeAllLayers();
        _vm = vm;
    }

    public void Detach()
    {
        DisposeAllLayers();
        _vm = null;
    }

    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        _device = sender.Device;
        PlaybackLogger.Log($"OnCreateResources device={(_device is null ? "null" : "ok")} reason={args.Reason}");
        try
        {
            _device.DeviceLost += (d, _) =>
            {
                PlaybackLogger.Log("!!! CanvasDevice.DeviceLost fired");
            };
        }
        catch (Exception ex) { PlaybackLogger.Log($"DeviceLost hook THREW {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── Public playback API (called by EditorPage) ────────────────────────

    public void Play()
    {
        _isPlaying = true;
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        PlaybackLogger.Log($"Play layers={snapshot.Length}");
        foreach (var l in snapshot)
        {
            if (l.Disposed) continue;
            try { l.Player.Play(); } catch (Exception ex) { PlaybackLogger.Log($"Play clip={l.Clip.Id} THREW {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public void Pause()
    {
        _isPlaying = false;
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        PlaybackLogger.Log($"Pause layers={snapshot.Length}");
        foreach (var l in snapshot)
        {
            if (l.Disposed) continue;
            try { l.Player.Pause(); } catch (Exception ex) { PlaybackLogger.Log($"Pause clip={l.Clip.Id} THREW {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public void Seek(TimeSpan playhead)
    {
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot) SeekLayer(l, playhead);
    }

    /// <summary>Reconcile the active layer set with the clips currently under the playhead.</summary>
    public void Sync(TimeSpan playhead)
    {
        if (_vm is null) return;

        // Desired layers in deepest→shallowest order. Video tracks contribute video
        // frames + audio; audio tracks contribute audio only (their layers will have
        // no Frame and DrawLayer skips them — but the MediaPlayer plays audio).
        var desired = new List<(Track Track, TimelineClip Clip)>();
        for (int i = _vm.Tracks.Count - 1; i >= 0; i--)
        {
            var t = _vm.Tracks[i];
            if (t.Kind != TrackKind.Video && t.Kind != TrackKind.Audio) continue;
            if (t.IsMuted) continue;
            var clip = FirstClipAt(t, playhead);
            if (clip is null) continue;
            if (clip.Kind == ClipKind.Title) continue;
            if (string.IsNullOrEmpty(clip.SourceId)) continue;
            desired.Add((t, clip));
        }

        List<Layer> toDispose = new();
        List<(Track, TimelineClip)> toAdd = new();

        lock (_layersLock)
        {
            // Remove layers no longer needed
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var l = _layers[i];
                if (!desired.Any(d => ReferenceEquals(d.Clip, l.Clip)))
                {
                    toDispose.Add(l);
                    _layers.RemoveAt(i);
                }
            }

            // Identify layers to add (don't start the async load while holding the lock)
            foreach (var (track, clip) in desired)
                if (!_layers.Any(l => ReferenceEquals(l.Clip, clip)))
                    toAdd.Add((track, clip));

            // Reorder remaining layers to match 'desired'
            _layers.Sort((a, b) =>
            {
                int ia = desired.FindIndex(d => ReferenceEquals(d.Clip, a.Clip));
                int ib = desired.FindIndex(d => ReferenceEquals(d.Clip, b.Clip));
                return ia.CompareTo(ib);
            });

            // Every active video layer plays its audio — the OS mixer sums them at the
            // output. Per-clip Volume from the inspector is honored via Player.Volume,
            // sampled from the clip's volume envelope at the current playhead so
            // automation keyframes drive the audible level (not just the waveform).
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].Disposed) continue;
                var clip = _layers[i].Clip;
                double timeRel = clip.Duration.TotalSeconds > 0
                    ? (playhead - clip.TimelineStart).TotalSeconds / clip.Duration.TotalSeconds
                    : 0;
                double vol = clip.GetVolumeAt(Math.Clamp(timeRel, 0, 1));
                try
                {
                    _layers[i].Player.IsMuted = false;
                    _layers[i].Player.Volume  = Math.Clamp(vol, 0, 1);
                }
                catch { }
            }
        }

        // Dispose & spin up new layers outside the lock
        if (toDispose.Count > 0 || toAdd.Count > 0)
            PlaybackLogger.Log($"Sync RECONCILE playhead={playhead.TotalSeconds:F3}s remove={toDispose.Count} add={toAdd.Count} desired={desired.Count}");
        foreach (var l in toDispose) DisposeLayer(l);
        foreach (var (t, c) in toAdd) _ = AddLayerAsync(t, c, playhead);

        // 1Hz heartbeat — without this a long no-reconcile stretch leaves no
        // markers and we can't tell from the log how close to clip-end the
        // crash happened.
        var now = DateTime.UtcNow;
        if ((now - _lastSyncHeartbeatUtc).TotalSeconds >= 1.0)
        {
            _lastSyncHeartbeatUtc = now;
            PlaybackLogger.Log($"SyncHB playhead={playhead.TotalSeconds:F3}s layers={_layers.Count} desired={desired.Count}");
        }
    }

    private static TimelineClip? FirstClipAt(Track t, TimeSpan p)
    {
        foreach (var c in t.Clips)
            if (p >= c.TimelineStart && p < c.TimelineEnd) return c;
        return null;
    }

    private async Task AddLayerAsync(Track track, TimelineClip clip, TimeSpan playhead)
    {
        if (_vm is null) return;
        var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
        if (media is null) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=media-missing"); return; }
        if (media.Type != MediaType.Video && media.Type != MediaType.Audio) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=type={media.Type}"); return; }
        if (!System.IO.File.Exists(media.FilePath)) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=file-missing path={media.FilePath}"); return; }

        PlaybackLogger.Log($"AddLayer ENTER clip={clip.Id} src={System.IO.Path.GetFileName(media.FilePath)} isVideo={media.Type == MediaType.Video}");

        // Video-frame-server only matters for video media — audio-only sources have
        // no frames and turning it on for them is wasted work.
        bool isVideo = media.Type == MediaType.Video;

        MediaPlayer player;
        try
        {
            player = new MediaPlayer
            {
                AutoPlay                  = false,
                IsMuted                   = false,
                IsVideoFrameServerEnabled = isVideo,
            };
        }
        catch { return; }

        var layer = new Layer
        {
            Track      = track,
            Clip       = clip,
            SourcePath = media.FilePath,
            Player     = player,
        };

        // Reserve a slot immediately so a subsequent Sync() doesn't try to add a duplicate
        lock (_layersLock)
        {
            // Re-check in case the user scrubbed past this clip while we got here
            if (!_vm.Tracks.SelectMany(t => t.Clips).Any(c => ReferenceEquals(c, clip)))
            { TryDisposePlayer(player); return; }
            _layers.Add(layer);
        }

        player.MediaOpened += (s, _) =>
        {
            try
            {
                double natDur = 0;
                try { natDur = s.PlaybackSession.NaturalDuration.TotalSeconds; } catch { }
                var srcEnd = (layer.Clip.SourceStart + layer.Clip.Duration).TotalSeconds;
                PlaybackLogger.Log($"MediaOpened clip={layer.Clip.Id} disposed={layer.Disposed} natDur={natDur:F3}s clipDur={layer.Clip.Duration.TotalSeconds:F3}s srcStart={layer.Clip.SourceStart.TotalSeconds:F3}s srcEnd={srcEnd:F3}s tlStart={layer.Clip.TimelineStart.TotalSeconds:F3}s tlEnd={layer.Clip.TimelineEnd.TotalSeconds:F3}s");
            } catch { }
            if (layer.Disposed) return;
            layer.MediaOpened = true;
            SeekLayer(layer, _vm?.Project.Playhead ?? playhead);
            if (_isPlaying && !layer.Disposed) try { s.Play(); } catch (Exception ex) { PlaybackLogger.Log($"MediaOpened Play() THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
        };
        player.MediaFailed += (s, args) =>
        {
            PlaybackLogger.Log($"MediaFailed clip={layer.Clip.Id} error={args.Error} extError=0x{args.ExtendedErrorCode?.HResult:X8} msg={args.ErrorMessage}");
        };
        player.MediaEnded += (s, _) =>
        {
            double pos = 0; try { pos = s.PlaybackSession.Position.TotalSeconds; } catch { }
            PlaybackLogger.Log($"MediaEnded clip={layer.Clip.Id} pos={pos:F3}s disposed={layer.Disposed}");
        };
        try
        {
            player.PlaybackSession.PlaybackStateChanged += (sess, _) =>
            {
                double pos = 0; try { pos = sess.Position.TotalSeconds; } catch { }
                PlaybackLogger.Log($"PlaybackState clip={layer.Clip.Id} state={sess.PlaybackState} pos={pos:F3}s");
            };
        }
        catch (Exception ex) { PlaybackLogger.Log($"PlaybackSession hook THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
        player.VideoFrameAvailable += OnVideoFrameAvailable;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
            // The user (or a tick) may have removed us in the meantime — bail if so.
            if (layer.Disposed) { PlaybackLogger.Log($"AddLayer ABORT clip={clip.Id} reason=disposed-before-source-assign"); return; }
            var src = MediaSource.CreateFromStorageFile(file);
            player.Source = new MediaPlaybackItem(src);
            PlaybackLogger.Log($"AddLayer SOURCE-SET clip={clip.Id}");
        }
        catch (Exception ex)
        {
            PlaybackLogger.Log($"AddLayer THREW clip={clip.Id} {ex.GetType().Name}: {ex.Message}");
            DisposeLayer(layer);
            lock (_layersLock) _layers.Remove(layer);
        }
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        try
        {
            Layer? layer = null;
            lock (_layersLock)
            {
                for (int i = 0; i < _layers.Count; i++)
                {
                    if (ReferenceEquals(_layers[i].Player, sender))
                    {
                        layer = _layers[i];
                        break;
                    }
                }
            }
            if (layer is null) { PlaybackLogger.Log("OnVideoFrame ORPHAN no-layer-for-sender"); return; }

            var device = _device;
            if (device is null) return;

            // ALL access to sender (the MediaPlayer) and its surface happens under
            // FrameLock. DisposeLayer takes this same lock to flip Disposed before
            // it tears the player down, so observing Disposed=false here means the
            // PlaybackSession + CopyFrameToVideoSurface calls below are safe.
            // Without this gate, an in-flight invocation can read PlaybackSession
            // on a player that's mid-Dispose() and AV the process — the symptom
            // when two clips overlap and one of them leaves the playhead.
            lock (layer.FrameLock)
            {
                if (layer.Disposed) { PlaybackLogger.Log($"OnVideoFrame DISPOSED-RACE clip={layer.Clip.Id}"); return; }

                int w, h;
                double posSec = -1;
                try
                {
                    w = (int)sender.PlaybackSession.NaturalVideoWidth;
                    h = (int)sender.PlaybackSession.NaturalVideoHeight;
                    posSec = sender.PlaybackSession.Position.TotalSeconds;
                }
                catch (Exception ex) { PlaybackLogger.Log($"OnVideoFrame PlaybackSession THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); return; }
                if (w <= 0 || h <= 0) return;

                // Per-layer heartbeat. Tight cadence (every 5 frames, ~6/sec at
                // 30fps) so the last log line sits within ~80ms of the crash and
                // we can tell which player's worker thread died.
                long n = ++layer.FrameCount;
                if (n == 1 || n % 5 == 0)
                    PlaybackLogger.Log($"FrameHB clip={layer.Clip.Id} n={n} pos={posSec:F3}s {w}x{h}");

                if (layer.Frame is null
                    || (int)layer.Frame.SizeInPixels.Width  != w
                    || (int)layer.Frame.SizeInPixels.Height != h)
                {
                    try { layer.Frame?.Dispose(); } catch { }
                    try { layer.Frame = new CanvasRenderTarget(device, w, h, 96); }
                    catch (Exception ex) { PlaybackLogger.Log($"OnVideoFrame RT-alloc THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); layer.Frame = null; return; }
                }
                // Serialize the native MF copy across every player. Without
                // this, two FrameServer pipelines racing into CopyFrameToVideoSurface
                // from worker threads AV the process intermittently (no managed
                // exception, no device-lost — just dead). Shared with the export
                // source pool via NativeFrameCopyGate.
                lock (NativeFrameCopyGate.Lock)
                {
                    try { sender.CopyFrameToVideoSurface(layer.Frame); }
                    catch (Exception ex) { PlaybackLogger.Log($"OnVideoFrame CopyFrame THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}\n{ex}"); }
                }
            }
        }
        catch (Exception ex) { PlaybackLogger.Log($"OnVideoFrame OUTER THREW {ex.GetType().Name}: {ex.Message}\n{ex}"); }
    }

    private void SeekLayer(Layer l, TimeSpan playhead)
    {
        if (l.Disposed || !l.MediaOpened) return;
        TimeSpan offset = l.Clip.SourceStart + (playhead - l.Clip.TimelineStart);
        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        try { l.Player.PlaybackSession.Position = offset; } catch { }
    }

    private void DisposeLayer(Layer l)
    {
        if (l.Disposed) { PlaybackLogger.Log($"DisposeLayer SKIP clip={l.Clip.Id} already-disposed"); return; }

        PlaybackLogger.Log($"DisposeLayer ENTER clip={l.Clip.Id} mediaOpened={l.MediaOpened}");

        // Unhook FIRST so no NEW worker-thread invocations are queued after this
        // point. One invocation may already be in flight; the FrameLock below
        // makes its access to sender.PlaybackSession + the surface cooperative
        // with the teardown sequence.
        try { l.Player.VideoFrameAvailable -= OnVideoFrameAvailable; } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer unhook THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
        PlaybackLogger.Log($"DisposeLayer UNHOOKED clip={l.Clip.Id}");

        // Flip Disposed and release the frame under FrameLock. OnVideoFrameAvailable
        // takes the same lock around its sender access — once we exit this block,
        // any in-flight invocation that subsequently enters the lock will observe
        // Disposed=true and return without touching the MediaPlayer. That makes
        // the player.Dispose() below safe even if a worker thread was about to
        // call CopyFrameToVideoSurface a moment ago.
        lock (l.FrameLock)
        {
            l.Disposed = true;
            try { l.Frame?.Dispose(); } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer Frame.Dispose THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
            l.Frame = null;
        }
        PlaybackLogger.Log($"DisposeLayer FRAME-FREED clip={l.Clip.Id}");

        try { l.Player.Pause();       } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer Pause THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
        try { l.Player.Source = null; } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer Source=null THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
        PlaybackLogger.Log($"DisposeLayer PRE-PLAYER-DISPOSE clip={l.Clip.Id}");
        TryDisposePlayer(l.Player);
        PlaybackLogger.Log($"DisposeLayer DONE clip={l.Clip.Id}");
    }

    private static void TryDisposePlayer(MediaPlayer p)
    {
        try { p.Dispose(); } catch { }
    }

    private void DisposeAllLayers()
    {
        Layer[] snapshot;
        lock (_layersLock)
        {
            snapshot = _layers.ToArray();
            _layers.Clear();
        }
        foreach (var l in snapshot) DisposeLayer(l);
    }

    // ── Draw ──────────────────────────────────────────────────────────────

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        DrawScene(args.DrawingSession, sender.Size);
    }

    /// <summary>Renders every active layer at the current playhead into
    /// <paramref name="ds"/>, sized to <paramref name="bounds"/> (control units).
    /// Pulled out of OnDraw so SamplePixelAt can reuse the same composition.</summary>
    private void DrawScene(CanvasDrawingSession ds, Windows.Foundation.Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (OutputWidth <= 0 || OutputHeight <= 0) return;

        // Letterbox the project frame inside the control bounds, preserving aspect.
        double outAspect = (double)OutputWidth / OutputHeight;
        double cAspect   = bounds.Width / bounds.Height;
        Rect frameRect;
        if (cAspect > outAspect)
        {
            double h = bounds.Height;
            double w = h * outAspect;
            frameRect = new Rect((bounds.Width - w) / 2, 0, w, h);
        }
        else
        {
            double w = bounds.Width;
            double h = w / outAspect;
            frameRect = new Rect(0, (bounds.Height - h) / 2, w, h);
        }

        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();

        foreach (var layer in snapshot)
        {
            // Hold the per-layer lock for the duration of the draw so the frame
            // can't be disposed mid-DrawImage.
            lock (layer.FrameLock)
            {
                if (layer.Disposed || layer.Frame is null) continue;
                try { DrawLayer(ds, layer.Frame, layer.Clip, frameRect); }
                catch (Exception ex) { PlaybackLogger.Log($"DrawLayer THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}\n{ex}"); }
            }
        }
    }

    /// <summary>Samples the rendered pixel color at <paramref name="ptInControl"/>
    /// (control-local coordinates). Re-renders the current scene into a one-pixel
    /// CanvasRenderTarget at the click position so we only pay for the pixel we
    /// actually need. Returns null if the device isn't ready or the point is out
    /// of bounds. Used by the inspector's eye-dropper to pick chroma-key /
    /// vignette colors directly from the video.</summary>
    public Windows.UI.Color? SamplePixelAt(Windows.Foundation.Point ptInControl)
    {
        var device = _device;
        if (device is null) return null;

        var bounds = new Windows.Foundation.Size(Canvas.ActualWidth, Canvas.ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        if (ptInControl.X < 0 || ptInControl.Y < 0) return null;
        if (ptInControl.X >= bounds.Width || ptInControl.Y >= bounds.Height) return null;

        try
        {
            // 1px render target translated so the click point lands at (0,0).
            using var rt = new CanvasRenderTarget(device, 1, 1, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.Transform = Matrix3x2.CreateTranslation((float)-ptInControl.X, (float)-ptInControl.Y);
                DrawScene(ds, bounds);
            }
            var colors = rt.GetPixelColors(0, 0, 1, 1);
            return colors.Length > 0 ? colors[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private void DrawLayer(CanvasDrawingSession ds,
                           CanvasRenderTarget frame, TimelineClip clip, Rect frameRect)
    {
        // Source rect with crop (CropLeft/Top/Right/Bottom are 0..1 fractions).
        // When this clip is the active crop-mode target, render un-cropped so the
        // user can see the full source frame while picking a crop region.
        double srcW = frame.SizeInPixels.Width;
        double srcH = frame.SizeInPixels.Height;
        bool bypassCrop = BypassCropClipId is not null && BypassCropClipId == clip.Id;
        double cl = bypassCrop ? 0 : Math.Clamp(clip.CropLeft,   0, 0.95);
        double ct = bypassCrop ? 0 : Math.Clamp(clip.CropTop,    0, 0.95);
        double cr = bypassCrop ? 0 : Math.Clamp(clip.CropRight,  0, 0.95);
        double cb = bypassCrop ? 0 : Math.Clamp(clip.CropBottom, 0, 0.95);
        var srcRect = new Rect(cl * srcW, ct * srcH,
                               Math.Max(1, (1 - cl - cr) * srcW),
                               Math.Max(1, (1 - ct - cb) * srcH));

        // Destination rect: source frame fitted into frameRect at its native
        // aspect (letterbox/pillarbox so portrait clips don't stretch into a
        // landscape canvas, and vice versa), then per-clip Scale and PosX/Y
        // (PosX/Y are in project-pixel units, e.g. -1920..1920). Then carve
        // out the cropped sub-rect so cropped pixels keep their original
        // on-screen size instead of stretching to fill the un-cropped dest.
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double srcAspect = srcW / srcH;
        double canvasAspect = frameRect.Width / frameRect.Height;
        double fitW, fitH;
        if (srcAspect >= canvasAspect)
        {
            fitW = frameRect.Width;
            fitH = frameRect.Width / srcAspect;
        }
        else
        {
            fitH = frameRect.Height;
            fitW = frameRect.Height * srcAspect;
        }
        double fullW = fitW * scale;
        double fullH = fitH * scale;
        double offX  = clip.PosX / Math.Max(1, OutputWidth)  * frameRect.Width;
        double offY  = clip.PosY / Math.Max(1, OutputHeight) * frameRect.Height;
        double fullX = frameRect.X + (frameRect.Width  - fullW) / 2 + offX;
        double fullY = frameRect.Y + (frameRect.Height - fullH) / 2 + offY;
        double dstX = fullX + cl * fullW;
        double dstY = fullY + ct * fullH;
        double dstW = Math.Max(1, (1 - cl - cr) * fullW);
        double dstH = Math.Max(1, (1 - ct - cb) * fullH);
        var destRect = new Rect(dstX, dstY, dstW, dstH);

        float opacity = (float)Math.Clamp(clip.Opacity, 0, 1);

        // Color-grading + named-effect chain rooted at the source frame. Same builder
        // as the export pipeline, so what the preview shows is what gets baked. The
        // graph is rebuilt per draw and disposed at the end — these are lightweight
        // effect descriptors, not GPU resources.
        double clipTimeRel = 0;
        var playhead = _vm?.Project.Playhead;
        if (playhead is { } p && clip.Duration.TotalSeconds > 0)
            clipTimeRel = Math.Clamp((p - clip.TimelineStart).TotalSeconds / clip.Duration.TotalSeconds, 0, 1);

        var graph = new List<IDisposable>();
        try
        {
            var src = ClipEffectsChain.Build(frame, clip, clipTimeRel, graph);

            // Flip + rotation around the clip's center
            var oldTransform = ds.Transform;
            var t = Matrix3x2.Identity;
            if (clip.FlipX || clip.FlipY)
            {
                var center = new Vector2((float)(dstX + dstW / 2), (float)(dstY + dstH / 2));
                t = Matrix3x2.CreateScale(clip.FlipX ? -1f : 1f, clip.FlipY ? -1f : 1f, center);
            }
            double rot = clip.Rotation * Math.PI / 180.0;
            if (Math.Abs(rot) > 0.0001)
            {
                var center = new Vector2((float)(dstX + dstW / 2), (float)(dstY + dstH / 2));
                t = t * Matrix3x2.CreateRotation((float)rot, center);
            }
            if (t != Matrix3x2.Identity) ds.Transform = t;

            try
            {
                ds.DrawImage((Microsoft.Graphics.Canvas.ICanvasImage)src, destRect, srcRect, opacity);
            }
            finally
            {
                ds.Transform = oldTransform;
            }
        }
        finally
        {
            for (int i = graph.Count - 1; i >= 0; i--)
                try { graph[i].Dispose(); } catch { }
        }
    }
}
