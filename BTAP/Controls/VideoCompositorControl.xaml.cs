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
    }

    private EditorViewModel? _vm;
    private CanvasDevice? _device;
    private readonly List<Layer> _layers = [];   // deepest first → drawn first → on back
    private readonly object _layersLock = new(); // guards _layers across UI / worker / render threads
    private bool _isPlaying;

    // The preview canvas size — derived from the first imported video clip's
    // native resolution, with a fallback to the project's export dimensions when
    // no video has been imported yet. The project's Width/Height define only the
    // EXPORT crop window inside this canvas (rendered as a dashed overlay by
    // EditorPage). Reading live from the VM keeps the letterbox and per-clip
    // transform math in sync with changes to either canvas-source or export size.
    public int OutputWidth  => _vm?.Project.GetCanvasSize().Width  ?? 1920;
    public int OutputHeight => _vm?.Project.GetCanvasSize().Height ?? 1080;

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
    }

    // ── Public playback API (called by EditorPage) ────────────────────────

    public void Play()
    {
        _isPlaying = true;
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot)
        {
            if (l.Disposed) continue;
            try { l.Player.Play(); } catch { }
        }
    }

    public void Pause()
    {
        _isPlaying = false;
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot)
        {
            if (l.Disposed) continue;
            try { l.Player.Pause(); } catch { }
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
            // output. Per-clip Volume from the inspector is honored via Player.Volume.
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].Disposed) continue;
                try
                {
                    _layers[i].Player.IsMuted = false;
                    _layers[i].Player.Volume  = Math.Clamp(_layers[i].Clip.Volume, 0, 1);
                }
                catch { }
            }
        }

        // Dispose & spin up new layers outside the lock
        foreach (var l in toDispose) DisposeLayer(l);
        foreach (var (t, c) in toAdd) _ = AddLayerAsync(t, c, playhead);
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
        if (media is null) return;
        if (media.Type != MediaType.Video && media.Type != MediaType.Audio) return;
        if (!System.IO.File.Exists(media.FilePath)) return;

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
            if (layer.Disposed) return;
            layer.MediaOpened = true;
            SeekLayer(layer, _vm?.Project.Playhead ?? playhead);
            if (_isPlaying && !layer.Disposed) try { s.Play(); } catch { }
        };
        player.VideoFrameAvailable += OnVideoFrameAvailable;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
            // The user (or a tick) may have removed us in the meantime — bail if so.
            if (layer.Disposed) return;
            var src = MediaSource.CreateFromStorageFile(file);
            player.Source = new MediaPlaybackItem(src);
        }
        catch
        {
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
            if (layer is null || layer.Disposed) return;

            var device = _device;
            if (device is null) return;

            int w = (int)sender.PlaybackSession.NaturalVideoWidth;
            int h = (int)sender.PlaybackSession.NaturalVideoHeight;
            if (w <= 0 || h <= 0) return;

            lock (layer.FrameLock)
            {
                if (layer.Disposed) return;
                if (layer.Frame is null
                    || (int)layer.Frame.SizeInPixels.Width  != w
                    || (int)layer.Frame.SizeInPixels.Height != h)
                {
                    try { layer.Frame?.Dispose(); } catch { }
                    try { layer.Frame = new CanvasRenderTarget(device, w, h, 96); }
                    catch { layer.Frame = null; return; }
                }
                try { sender.CopyFrameToVideoSurface(layer.Frame); }
                catch { /* device lost or transitional state — drop the frame */ }
            }
        }
        catch { /* never let this handler throw — it'd kill the media pipeline */ }
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
        if (l.Disposed) return;
        l.Disposed = true;

        // Unhook the frame callback FIRST so no further worker-thread invocations
        // race with disposal.
        try { l.Player.VideoFrameAvailable -= OnVideoFrameAvailable; } catch { }

        try { l.Player.Pause(); } catch { }

        lock (l.FrameLock)
        {
            try { l.Frame?.Dispose(); } catch { }
            l.Frame = null;
        }

        try { l.Player.Source = null; } catch { }
        TryDisposePlayer(l.Player);
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
        var ds = args.DrawingSession;
        var bounds = sender.Size;

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
                catch { /* defensive — disposed frame, device loss, etc */ }
            }
        }
    }

    private void DrawLayer(CanvasDrawingSession ds,
                           CanvasRenderTarget frame, TimelineClip clip, Rect frameRect)
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

        // Destination rect: project frame fitted into frameRect, then per-clip
        // Scale and PosX/Y (PosX/Y are in project-pixel units, e.g. -1920..1920).
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double dstW  = frameRect.Width  * scale;
        double dstH  = frameRect.Height * scale;
        double offX  = clip.PosX / Math.Max(1, OutputWidth)  * frameRect.Width;
        double offY  = clip.PosY / Math.Max(1, OutputHeight) * frameRect.Height;
        double dstX  = frameRect.X + (frameRect.Width  - dstW) / 2 + offX;
        double dstY  = frameRect.Y + (frameRect.Height - dstH) / 2 + offY;
        var destRect = new Rect(dstX, dstY, dstW, dstH);

        float opacity = (float)Math.Clamp(clip.Opacity, 0, 1);

        // Rotation around the clip's center
        var oldTransform = ds.Transform;
        double rot = clip.Rotation * Math.PI / 180.0;
        if (Math.Abs(rot) > 0.0001)
        {
            var center = new Vector2((float)(dstX + dstW / 2), (float)(dstY + dstH / 2));
            ds.Transform = Matrix3x2.CreateRotation((float)rot, center);
        }

        ds.DrawImage(frame, destRect, srcRect, opacity);

        ds.Transform = oldTransform;
    }
}
