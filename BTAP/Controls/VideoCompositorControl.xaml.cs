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

        // Animated GIF: pre-decoded frames + per-frame delays (ms). When set,
        // Frame is rebound at draw time to GifFrames[i] based on the playhead's
        // offset into the clip (mod GifTotalDurationMs). Owning the frames here
        // means DisposeLayer releases every frame, not just the active one.
        public List<(CanvasRenderTarget Rt, int DelayMs)>? GifFrames;
        public int GifTotalDurationMs;
    }

    private EditorViewModel? _vm;
    private CanvasDevice? _device;
    private readonly List<Layer> _layers = [];   // deepest first → drawn first → on back
    private readonly object _layersLock = new(); // guards _layers across UI / worker / render threads
    private bool _isPlaying;
    private bool _pendingInitialSync;            // Attach defers Sync until Canvas device is ready
    private DateTime _lastSyncHeartbeatUtc = DateTime.MinValue;

    // Audio engine: AudioGraph-based pipeline that applies per-clip effects from
    // ClipEffectsChain's audio list. When this initializes successfully the
    // MediaPlayer's audio is muted (audio plays exclusively through the engine);
    // otherwise we fall back to MediaPlayer audio so users still hear sound.
    private readonly AudioEngine _audioEngine = new();
    private bool _audioEngineReady;


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
        Unloaded += (_, _) =>
        {
            // ContentDialog (and ScrollViewer) reparent their content during
            // layout, which fires Unloaded → Loaded transitions even when the
            // control is logically still in use. Externally-managed instances
            // (the export-preview dialog) handle their own teardown, so suppress
            // the auto-dispose to avoid losing the layer pool mid-show.
            if (ExternallyManaged) return;
            DisposeAllLayers();
            try { _audioEngine.Dispose(); } catch { }
        };
    }

    /// <summary>When true, the Unloaded handler will NOT dispose layers or the
    /// audio engine — the host is responsible for explicit teardown via
    /// <see cref="Detach"/>. Used by the export-preview dialog so transient
    /// dialog-layout reparenting doesn't drop frames mid-playback.</summary>
    public bool ExternallyManaged { get; set; }

    /// <summary>When true, only the project's export window is rendered (zoomed
    /// to fill the canvas), instead of the full work canvas with letterbox.
    /// This makes the preview match exactly what comes out of the exporter,
    /// rather than the editor's wider working view. Bounds should be sized to
    /// the project's export aspect (project.Width:project.Height).</summary>
    public bool RenderExportWindowOnly { get; set; }

    public void Attach(EditorViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        DisposeAllLayers();
        _vm = vm;
        // Kick off engine init eagerly so the first Play doesn't pay the latency.
        _ = InitAudioEngineAsync();
        // Materialise the layers at the project's current playhead so the preview
        // shows the first frame of any clip under it on load — without this the
        // viewport stays blank until the user hits Play. Device may not be ready
        // yet when Attach runs (Canvas creates it on first paint), so flag it and
        // let OnCreateResources finish the job.
        _pendingInitialSync = true;
        RunPendingInitialSync();
    }

    private void RunPendingInitialSync()
    {
        if (!_pendingInitialSync || _vm is null || _device is null) return;
        _pendingInitialSync = false;
        var p = _vm.Project.Playhead;
        Sync(p);
        Seek(p);
    }

    private async Task InitAudioEngineAsync()
    {
        if (SuppressAudio) return;
        _audioEngineReady = await _audioEngine.EnsureInitializedAsync();
        PlaybackLogger.Log($"AudioEngine ready={_audioEngineReady}");
    }

    /// <summary>Exposed so the EditorPage inspector can ask the engine to re-build
    /// effect chains after the user moves a slider that changed clip.Effects or
    /// clip.EqLow/Mid/High.</summary>
    public void NotifyAudioParamsChanged()
    {
        if (_audioEngineReady) _audioEngine.UpdateAllEffects();
    }

    /// <summary>When true, audio is suppressed for this instance: MediaPlayer audio
    /// is muted, the AudioEngine is never initialized, and per-clip gain is left
    /// at zero. Used by the export-preview dialog so its mini-compositor doesn't
    /// double-play the soundtrack on top of the editor's audio.</summary>
    public bool SuppressAudio { get; set; }

    /// <summary>Set MediaPlayer.PlaybackRate on every active layer. The preview
    /// dialog drives this to 10× to speed-scrub through the timeline; export and
    /// the editor's main playback both leave it at 1×.</summary>
    public void SetPlaybackRate(double rate)
    {
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot)
        {
            if (l.Disposed) continue;
            try { l.Player.PlaybackSession.PlaybackRate = rate; } catch { }
        }
        _playbackRate = rate;
    }

    private double _playbackRate = 1.0;

    public void Detach()
    {
        DisposeAllLayers();
        _vm = null;
        _pendingInitialSync = false;
    }

    /// <summary>Fires (on the UI thread) when a layer's source frame is first
    /// allocated or its pixel dimensions change. The editor uses this to
    /// recompute the TransformOverlay box now that the real source dims are
    /// known — without it, the box stays stuck at the canvas-fallback size
    /// for clips whose MediaItem metadata wasn't probed/persisted.</summary>
    public event EventHandler<string>? LayerFrameSizeChanged;

    /// <summary>Returns the pixel dimensions of the most recently decoded frame
    /// for <paramref name="clipId"/>, or null if no frame has been received yet
    /// (compositor still warming up, or the clip has no layer). The editor's
    /// TransformOverlay uses this to size the selection box from the same
    /// source dimensions DrawLayer uses — staying correct even when the
    /// MediaItem metadata wasn't probed/persisted (e.g. old project format).</summary>
    public (double Width, double Height)? GetSourceFrameSize(string clipId)
    {
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot)
        {
            if (l.Clip?.Id != clipId) continue;
            lock (l.FrameLock)
            {
                if (l.Disposed || l.Frame is null) return null;
                return (l.Frame.SizeInPixels.Width, l.Frame.SizeInPixels.Height);
            }
        }
        return null;
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
        RunPendingInitialSync();
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
        if (_audioEngineReady) _audioEngine.Play();
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
        if (_audioEngineReady) _audioEngine.Pause();
    }

    public void Seek(TimeSpan playhead)
    {
        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();
        foreach (var l in snapshot) SeekLayer(l, playhead);
        if (_audioEngineReady) _audioEngine.SeekAll(playhead);
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

            // Per-clip gain at the current playhead — sampled from the clip's volume
            // envelope so automation keyframes drive the audible level. When the
            // AudioEngine is ready, audio plays through it (MediaPlayer is muted);
            // otherwise we fall back to MediaPlayer.Volume so the user still gets
            // sound on hardware where AudioGraph couldn't init.
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
                    if (SuppressAudio)
                    {
                        _layers[i].Player.IsMuted = true;
                    }
                    else if (_audioEngineReady)
                    {
                        _layers[i].Player.IsMuted = true;
                        _audioEngine.SetClipGain(clip.Id, vol);
                    }
                    else
                    {
                        _layers[i].Player.IsMuted = false;
                        _layers[i].Player.Volume  = Math.Clamp(vol, 0, 1);
                    }
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

    /// <summary>Loads a still-image clip into a CanvasRenderTarget so the regular
    /// draw loop renders it the same way it renders a video frame. A dummy muted
    /// MediaPlayer is attached so all the Player.* calls in the layer-management
    /// code paths (Pause/SetPlaybackRate/Volume) stay no-op-safe.</summary>
    private async Task AddImageLayerAsync(Track track, TimelineClip clip, MediaItem media)
    {
        if (_vm is null) return;
        var device = _device;
        if (device is null) { PlaybackLogger.Log($"AddLayer SKIP image clip={clip.Id} reason=device-null"); return; }

        MediaPlayer dummy;
        try
        {
            dummy = new MediaPlayer
            {
                AutoPlay                  = false,
                IsMuted                   = true,
                IsVideoFrameServerEnabled = false,
            };
        }
        catch { return; }

        var layer = new Layer
        {
            Track      = track,
            Clip       = clip,
            SourcePath = media.FilePath,
            Player     = dummy,
        };

        lock (_layersLock)
        {
            if (!_vm.Tracks.SelectMany(t => t.Clips).Any(c => ReferenceEquals(c, clip)))
            { TryDisposePlayer(dummy); return; }
            _layers.Add(layer);
        }

        try
        {
            var bitmap = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, media.FilePath);
            if (layer.Disposed) { try { bitmap.Dispose(); } catch { } return; }

            // Bake the bitmap into a render target so Layer.Frame's type
            // (CanvasRenderTarget) matches the video path, and so the bitmap's
            // resources are owned by the layer (not by the load result).
            var w = (int)Math.Max(1, bitmap.SizeInPixels.Width);
            var h = (int)Math.Max(1, bitmap.SizeInPixels.Height);
            var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.DrawImage(bitmap);
            }
            try { bitmap.Dispose(); } catch { }

            lock (layer.FrameLock)
            {
                if (!layer.Disposed) layer.Frame = rt;
                else try { rt.Dispose(); } catch { }
            }
            layer.MediaOpened = true;
            PlaybackLogger.Log($"AddLayer IMAGE clip={clip.Id} src={System.IO.Path.GetFileName(media.FilePath)} {w}x{h}");
        }
        catch (Exception ex)
        {
            PlaybackLogger.Log($"AddLayer image-load THREW clip={clip.Id} {ex.GetType().Name}: {ex.Message}");
            DisposeLayer(layer);
            lock (_layersLock) _layers.Remove(layer);
        }
    }

    /// <summary>Loads every frame of an animated GIF into its own CanvasRenderTarget
    /// along with the per-frame delay metadata, so DrawScene can pick the frame
    /// matching the current playhead. Composites each frame against an accumulator
    /// to honour the GIF disposal model (most optimized GIFs paint only the rect
    /// that changed each frame).</summary>
    private async Task AddGifLayerAsync(Track track, TimelineClip clip, MediaItem media)
    {
        if (_vm is null) return;
        var device = _device;
        if (device is null) { PlaybackLogger.Log($"AddLayer SKIP gif clip={clip.Id} reason=device-null"); return; }

        MediaPlayer dummy;
        try
        {
            dummy = new MediaPlayer
            {
                AutoPlay                  = false,
                IsMuted                   = true,
                IsVideoFrameServerEnabled = false,
            };
        }
        catch { return; }

        var layer = new Layer
        {
            Track      = track,
            Clip       = clip,
            SourcePath = media.FilePath,
            Player     = dummy,
        };

        lock (_layersLock)
        {
            if (!_vm.Tracks.SelectMany(t => t.Clips).Any(c => ReferenceEquals(c, clip)))
            { TryDisposePlayer(dummy); return; }
            _layers.Add(layer);
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapDecoder.GifDecoderId, stream);
            int canvasW = (int)Math.Max(1, decoder.PixelWidth);
            int canvasH = (int)Math.Max(1, decoder.PixelHeight);
            int count   = (int)decoder.FrameCount;
            if (count <= 0) { PlaybackLogger.Log($"AddLayer GIF clip={clip.Id} frameCount=0 — falling back to still image"); }

            // Single-frame GIF: nothing animated to do; route through the still-image path.
            if (count <= 1)
            {
                lock (_layersLock) _layers.Remove(layer);
                TryDisposePlayer(dummy);
                await AddImageLayerAsync(track, clip, media);
                return;
            }

            var frames = new List<(CanvasRenderTarget Rt, int DelayMs)>(count);
            int total = 0;

            // Accumulator holds the composited result so far. Each frame's local
            // patch is drawn into it; the snapshot taken before applying the next
            // frame's disposal becomes the frame the user sees.
            var accum = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, canvasW, canvasH, 96);
            using (var ds = accum.CreateDrawingSession())
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            Microsoft.Graphics.Canvas.CanvasRenderTarget? prevSnapshot = null;

            try
            {
                for (uint i = 0; i < count; i++)
                {
                    if (layer.Disposed) break;

                    var frame = await decoder.GetFrameAsync(i);

                    int left = 0, top = 0;
                    int fw = (int)frame.PixelWidth;
                    int fh = (int)frame.PixelHeight;
                    int disposal = 0;
                    int delayMs  = 0;
                    try
                    {
                        var props = await frame.BitmapProperties.GetPropertiesAsync(new[]
                        {
                            "/imgdesc/Left", "/imgdesc/Top",
                            "/imgdesc/Width", "/imgdesc/Height",
                            "/grctlext/Disposal", "/grctlext/Delay",
                        });
                        if (props.TryGetValue("/imgdesc/Left",     out var v) && v.Value is ushort ul) left = ul;
                        if (props.TryGetValue("/imgdesc/Top",      out v) && v.Value is ushort ut) top  = ut;
                        if (props.TryGetValue("/imgdesc/Width",    out v) && v.Value is ushort uw) fw   = uw;
                        if (props.TryGetValue("/imgdesc/Height",   out v) && v.Value is ushort uh) fh   = uh;
                        if (props.TryGetValue("/grctlext/Disposal",out v) && v.Value is byte   ud) disposal = ud;
                        if (props.TryGetValue("/grctlext/Delay",   out v) && v.Value is ushort ud2) delayMs = ud2 * 10;
                    }
                    catch (Exception ex) { PlaybackLogger.Log($"AddLayer GIF clip={clip.Id} meta-read THREW frame={i} {ex.GetType().Name}: {ex.Message}"); }

                    // Browsers floor very small delays (most treat 0/10/20ms as 100ms)
                    // to keep tiny-delay GIFs from pinning the CPU. Match that so a
                    // 10ms delay doesn't render as a 100Hz strobe in the preview.
                    if (delayMs <= 20) delayMs = 100;

                    var pix = await frame.GetPixelDataAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        new Windows.Graphics.Imaging.BitmapTransform(),
                        Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                        Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                    var bytes = pix.DetachPixelData();

                    // Save accumulator BEFORE patching so disposal=3 (restore-to-previous)
                    // can roll back after we snapshot this frame.
                    if (disposal == 3)
                    {
                        try { prevSnapshot?.Dispose(); } catch { }
                        prevSnapshot = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, canvasW, canvasH, 96);
                        using var ds = prevSnapshot.CreateDrawingSession();
                        ds.DrawImage(accum);
                    }

                    using (var patch = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(
                        device, bytes, fw, fh,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized))
                    {
                        using var ds = accum.CreateDrawingSession();
                        ds.Blend = Microsoft.Graphics.Canvas.CanvasBlend.SourceOver;
                        ds.DrawImage(patch, new Rect(left, top, fw, fh));
                    }

                    // Snapshot the composited result — this is what the draw loop binds to.
                    var snap = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, canvasW, canvasH, 96);
                    using (var ds = snap.CreateDrawingSession())
                    {
                        ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                        ds.DrawImage(accum);
                    }
                    frames.Add((snap, delayMs));
                    total += delayMs;

                    // Apply disposal AFTER snapshotting so this frame's pixels still appear.
                    if (disposal == 2)
                    {
                        using var ds = accum.CreateDrawingSession();
                        ds.Blend = Microsoft.Graphics.Canvas.CanvasBlend.Copy;
                        ds.FillRectangle(new Rect(left, top, fw, fh), Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    }
                    else if (disposal == 3 && prevSnapshot is not null)
                    {
                        using var ds = accum.CreateDrawingSession();
                        ds.Blend = Microsoft.Graphics.Canvas.CanvasBlend.Copy;
                        ds.DrawImage(prevSnapshot);
                    }
                }
            }
            finally
            {
                try { accum.Dispose(); } catch { }
                try { prevSnapshot?.Dispose(); } catch { }
            }

            if (frames.Count == 0)
            {
                PlaybackLogger.Log($"AddLayer GIF clip={clip.Id} decoded 0 frames — bailing");
                DisposeLayer(layer);
                lock (_layersLock) _layers.Remove(layer);
                return;
            }

            lock (layer.FrameLock)
            {
                if (layer.Disposed)
                {
                    foreach (var (rt, _) in frames) try { rt.Dispose(); } catch { }
                    return;
                }
                layer.GifFrames         = frames;
                layer.GifTotalDurationMs = Math.Max(1, total);
                layer.Frame             = frames[0].Rt;
            }
            layer.MediaOpened = true;
            PlaybackLogger.Log($"AddLayer GIF clip={clip.Id} src={System.IO.Path.GetFileName(media.FilePath)} frames={frames.Count} totalMs={total} {canvasW}x{canvasH}");
        }
        catch (Exception ex)
        {
            PlaybackLogger.Log($"AddLayer GIF clip={clip.Id} THREW {ex.GetType().Name}: {ex.Message}");
            DisposeLayer(layer);
            lock (_layersLock) _layers.Remove(layer);
        }
    }

    private async Task AddLayerAsync(Track track, TimelineClip clip, TimeSpan playhead)
    {
        if (_vm is null) return;
        var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
        if (media is null) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=media-missing"); return; }
        if (media.Type != MediaType.Video && media.Type != MediaType.Audio && media.Type != MediaType.Image) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=type={media.Type}"); return; }
        if (!System.IO.File.Exists(media.FilePath)) { PlaybackLogger.Log($"AddLayer SKIP clip={clip.Id} reason=file-missing path={media.FilePath}"); return; }

        // Image layers have no audio and no time-varying video stream — we just
        // load the file into a static CanvasRenderTarget that the regular draw
        // path picks up. MediaPlayer doesn't decode still images, so we route
        // around it entirely instead of dragging a useless player along.
        if (media.Type == MediaType.Image)
        {
            var ext = System.IO.Path.GetExtension(media.FilePath);
            if (string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase))
                await AddGifLayerAsync(track, clip, media);
            else
                await AddImageLayerAsync(track, clip, media);
            return;
        }

        PlaybackLogger.Log($"AddLayer ENTER clip={clip.Id} src={System.IO.Path.GetFileName(media.FilePath)} isVideo={media.Type == MediaType.Video}");

        // Frame server only when this layer should actually contribute video to the
        // compositor. Three independent flags must all be true:
        //   • the source file has a video stream
        //   • the clip's role is Video (Audio-kind clips on an Audio track that
        //     happen to point at a video file — e.g. the result of "Separate
        //     Audio" — must NOT paint a duplicate frame)
        //   • the track is a Video track (an audio track never draws video)
        bool isVideo = media.Type == MediaType.Video
                    && clip.Kind == ClipKind.Video
                    && track.Kind == TrackKind.Video;

        MediaPlayer player;
        try
        {
            player = new MediaPlayer
            {
                AutoPlay                  = false,
                IsMuted                   = SuppressAudio,
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
            // Apply the compositor's current playback rate (preview dialog runs at 10×).
            try { s.PlaybackSession.PlaybackRate = _playbackRate; } catch { }
            if (_isPlaying && !layer.Disposed)
            {
                try { s.Play(); } catch (Exception ex) { PlaybackLogger.Log($"MediaOpened Play() THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
            }
            else if (!layer.Disposed)
            {
                // MediaPlayer's frame-server doesn't push a frame while paused, so
                // after the initial Seek the canvas stays blank until playback
                // starts. A brief muted Play→Pause forces the decoder to emit one
                // frame at the seeked position, so the preview shows the right
                // content on project load and after any paused seek. Sync()
                // overwrites IsMuted on its next pass.
                try { s.IsMuted = true; s.Play(); s.Pause(); }
                catch (Exception ex) { PlaybackLogger.Log($"MediaOpened prime THREW clip={layer.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
            }
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
            return;
        }

        // Mirror the layer in the audio engine so effects apply to its audio.
        // If init failed we leave MediaPlayer audio unmuted (Sync handles the
        // gain). Errors here don't affect the video layer. SuppressAudio (e.g.
        // export-preview) skips this path entirely so the preview compositor
        // doesn't double up on the editor's sound.
        if (SuppressAudio) return;
        if (_audioEngineReady)
        {
            _ = _audioEngine.AddClipAsync(clip, media.FilePath);
        }
        else
        {
            // Init may still be in-flight when the first layer is added. Try again
            // shortly so the first clip's audio routes through effects too.
            _ = Task.Run(async () =>
            {
                bool ready = await _audioEngine.EnsureInitializedAsync();
                if (ready && !layer.Disposed)
                {
                    _audioEngineReady = true;
                    await _audioEngine.AddClipAsync(clip, media.FilePath);
                    if (_isPlaying) _audioEngine.Play();
                }
            });
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

                    // Notify the editor (TransformOverlay sizing) on the UI
                    // thread that this clip's real source dims are now known.
                    var clipId = layer.Clip.Id;
                    try
                    {
                        DispatcherQueue?.TryEnqueue(() => LayerFrameSizeChanged?.Invoke(this, clipId));
                    }
                    catch { /* event is best-effort */ }
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

        // Tell the audio engine this layer's audio node can go away.
        if (_audioEngineReady)
        {
            try { _audioEngine.RemoveClip(l.Clip.Id); } catch { }
        }

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
            // GIF frames own the actual render targets; l.Frame is just whichever
            // one was last picked for drawing, so dispose the list and null Frame
            // without double-disposing it.
            if (l.GifFrames is { } gfs)
            {
                foreach (var (rt, _) in gfs)
                    try { rt?.Dispose(); } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer GifFrame.Dispose THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
                l.GifFrames = null;
                l.Frame     = null;
            }
            else
            {
                try { l.Frame?.Dispose(); } catch (Exception ex) { PlaybackLogger.Log($"DisposeLayer Frame.Dispose THREW clip={l.Clip.Id} {ex.GetType().Name}: {ex.Message}"); }
                l.Frame = null;
            }
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

        // Export-window mode: instead of fitting the full canvas, zoom into the
        // export window so that ONLY the region the exporter actually outputs is
        // visible. Anything outside the export window draws past the control's
        // bounds and gets clipped by Win2D's swap chain — matching the export's
        // crop exactly.
        if (RenderExportWindowOnly && _vm is not null)
        {
            var (ew_x, ew_y, ew_w, ew_h) = _vm.Project.GetExportWindow();
            var (canvasW, canvasH)       = _vm.Project.GetCanvasSize();
            if (ew_w > 0 && ew_h > 0 && canvasW > 0 && canvasH > 0)
            {
                double scaleX = bounds.Width  / ew_w;
                double scaleY = bounds.Height / ew_h;
                frameRect = new Rect(
                    -ew_x * scaleX,
                    -ew_y * scaleY,
                    canvasW * scaleX,
                    canvasH * scaleY);
            }
        }

        Layer[] snapshot;
        lock (_layersLock) snapshot = _layers.ToArray();

        foreach (var layer in snapshot)
        {
            // Hold the per-layer lock for the duration of the draw so the frame
            // can't be disposed mid-DrawImage.
            lock (layer.FrameLock)
            {
                if (layer.Disposed) continue;
                if (layer.GifFrames is { Count: > 0 } gifs && layer.GifTotalDurationMs > 0)
                {
                    var ph = _vm?.Project.Playhead ?? TimeSpan.Zero;
                    // SourceStart lets a trimmed-from-start GIF clip begin mid-loop;
                    // the modulo then keeps it looping for the clip's whole duration.
                    long ms = (long)Math.Max(0, (layer.Clip.SourceStart + (ph - layer.Clip.TimelineStart)).TotalMilliseconds);
                    ms %= layer.GifTotalDurationMs;
                    long acc = 0;
                    var pick = gifs[0].Rt;
                    for (int i = 0; i < gifs.Count; i++)
                    {
                        acc += gifs[i].DelayMs;
                        if (ms < acc) { pick = gifs[i].Rt; break; }
                        pick = gifs[i].Rt;
                    }
                    layer.Frame = pick;
                }
                if (layer.Frame is null) continue;
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
