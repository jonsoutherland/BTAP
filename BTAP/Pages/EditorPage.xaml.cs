using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using BTAP.Models;
using BTAP.Services;
using BTAP.ViewModels;

namespace BTAP.Pages;

public sealed partial class EditorPage : Page
{
    private EditorViewModel? _vm;
    private Button? _activeModeBtn;
    private Button? _activeLibBtn;
    private Button? _activeInspBtn;
    private Button? _activeToolBtn;
    private readonly ObservableCollection<MediaTileData> _mediaTiles = [];

    private DispatcherTimer? _playbackTimer;
    private DispatcherTimer? _statusTimer;
    private DateTime _lastSaved = DateTime.Now;
    private readonly PreviewEffectsService _previewEffects = new();

    public EditorPage()
    {
        InitializeComponent();
        MediaGrid.ItemsSource = _mediaTiles;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Tear down the compositor's per-clip MediaPlayers from any prior project
        // before swapping VMs.
        VideoCompositor.Detach();

        var project = e.Parameter as Project ?? Project.CreateDefault();
        _vm = new EditorViewModel(project);
        VideoCompositor.Attach(_vm);

        TbFilename.Text = project.Name;

        SetActiveModeBtn(BtnModeEdit);
        SetActiveLibBtn(BtnLibMedia);
        SetActiveInspBtn(BtnInspVideo);
        SetActiveToolBtn(BtnToolCursor);

        PopulateMediaGrid();
        PopulateLibraryPresets();
        WireTimeline();
        WirePlaybackBar();
        if (_vm.SelectedClip is { } preSelected)
        {
            UpdateClipHeader(preSelected);
            UpdatePreviewOverlay(preSelected);
        }
        UpdateInspector(_vm.SelectedClip);
        UpdateProgramInfo();
        UpdateStatusBar();
        RefreshPresentation();

        // Enable keyboard input
        KeyDown += OnEditorKeyDown;

        // Live status bar tick
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        // Re-seek the preview after any clip move/trim/delete/etc.
        _vm.History.Changed += OnHistoryChangedForPreview;

        // Take keyboard focus so Space / J / K / etc. work immediately
        Loaded += OnPageLoaded;
    }

    private void OnHistoryChangedForPreview(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // A clip might have moved out from under the playhead — re-evaluate
        _lastClipAtPlayhead = FindVideoClipAt(_vm.Project.Playhead);
        // Drop any stale reference to a clip that's no longer in the project
        // (e.g. the presented clip was just deleted).
        RefreshPresentation();
        if (_presentedClip is null)
        {
            // Nothing to show — clear the player so the deleted clip's last frame
            // doesn't linger over the placeholder.
            ClearPreview();
        }
        else
        {
            SeekPreviewToPlayhead(_vm.Project.Playhead);
        }
        // Reflect any structural change (clip count, duration, etc.) in the status bar immediately
        UpdateStatusBar();
        // Keep playback bar duration in sync if RecomputeDuration changed it
        PlaybackBar.SetDuration(_vm.DurationLabel);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        Focus(FocusState.Programmatic);
        UpdatePreviewCanvasSize();

        // NOTE: Live color-grading via CompositionEffectBrush + BackdropBrush doesn't
        // work for MediaPlayerElement — its video is rendered through a separate
        // DirectComposition swap-chain that the backdrop brush can't sample, so the
        // SpriteVisual paints solid black on top of the video. Proper real-time
        // grading needs a Win2D CanvasAnimatedControl + MediaPlayer FrameServer
        // pipeline (Tier 1 v2 work). For now the Color inspector sliders save to
        // clip data but don't affect the preview live.
        //
        // _previewEffects.Attach(ColorGradingLayer);
        // if (_vm?.SelectedClip is { } sel) _previewEffects.Apply(sel);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopPlayback();
        _statusTimer?.Stop();
        _statusTimer = null;
        if (_vm is not null)
            _vm.History.Changed -= OnHistoryChangedForPreview;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.MediaOpened -= OnPreviewMediaOpened;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        KeyDown -= OnEditorKeyDown;
    }

    // ── Keyboard shortcuts ───────────────────────────────────────────────

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Space:
                TogglePlayback();
                e.Handled = true;
                break;

            case VirtualKey.J:
                StopPlayback();
                _vm.StepBackCommand.Execute(null);
                RefreshPlayheadUI();
                e.Handled = true;
                break;

            case VirtualKey.K:
                StopPlayback();
                e.Handled = true;
                break;

            case VirtualKey.L:
                TogglePlayback();
                e.Handled = true;
                break;

            case VirtualKey.C:
                SetActiveToolBtn(BtnToolRazor);
                _vm.ActiveTool = ActiveTool.Razor;
                e.Handled = true;
                break;

            case VirtualKey.V:
                SetActiveToolBtn(BtnToolCursor);
                _vm.ActiveTool = ActiveTool.Cursor;
                e.Handled = true;
                break;

            case VirtualKey.H:
                SetActiveToolBtn(BtnToolHand);
                _vm.ActiveTool = ActiveTool.Hand;
                e.Handled = true;
                break;

            case VirtualKey.S when ctrl:
                _ = SaveProjectAsync();
                e.Handled = true;
                break;

            case VirtualKey.S:
                _vm.SnapEnabled = !_vm.SnapEnabled;
                BtnSnap.Foreground = _vm.SnapEnabled
                    ? (Brush)Application.Current.Resources["AccentInkBrush"]
                    : (Brush)Application.Current.Resources["TextMutedBrush"];
                e.Handled = true;
                break;

            case VirtualKey.M:
                _vm.AddMarkerCommand.Execute(null);
                e.Handled = true;
                break;

            case VirtualKey.Delete when shift:
            case VirtualKey.Back when shift:
                OnClipRippleDelete(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.Delete:
            case VirtualKey.Back:
                OnEditDelete(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.Z when ctrl:
                _vm.Undo();
                Timeline.ViewModel = _vm;
                UpdateInspector(_vm.SelectedClip);
                e.Handled = true;
                break;

            case VirtualKey.Y when ctrl:
                _vm.Redo();
                Timeline.ViewModel = _vm;
                UpdateInspector(_vm.SelectedClip);
                e.Handled = true;
                break;

            case VirtualKey.D when ctrl:
                OnEditDuplicate(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.B when ctrl:
                OnEditSplit(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.N when ctrl:
                OnFileNew(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.O when ctrl:
                OnFileOpen(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.E when ctrl:
                OnExport(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.F:
                OnViewFullscreen(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    // ── Playback ────────────────────────────────────────────────────────

    private void TogglePlayback()
    {
        if (_vm is null) return;
        if (_vm.IsPlaying) StopPlayback();
        else               StartPlayback();
    }

    private void StartPlayback()
    {
        if (_vm is null) return;
        _vm.IsPlaying = true;
        PlaybackBar.SetIsPlaying(true);

        _lastClipAtPlayhead = FindVideoClipAt(_vm.Project.Playhead);
        VideoCompositor.Sync(_vm.Project.Playhead);
        VideoCompositor.Seek(_vm.Project.Playhead);
        VideoCompositor.Play();

        _playbackTimer ??= new DispatcherTimer();
        _playbackTimer.Interval = TimeSpan.FromSeconds(1.0 / _vm.Project.FrameRate);
        _playbackTimer.Tick -= OnPlaybackTick;
        _playbackTimer.Tick += OnPlaybackTick;
        _playbackTimer.Start();
    }

    private void StopPlayback()
    {
        if (_vm is null) return;
        _vm.IsPlaying = false;
        PlaybackBar.SetIsPlaying(false);
        _playbackTimer?.Stop();
        VideoCompositor.Pause();
    }

    // ── Preview loading ────────────────────────────────────────────────

    private string? _currentPreviewPath;
    private MediaPlayer? _mediaPlayer;
    private TimeSpan? _pendingSeek;
    private TimelineClip? _lastClipAtPlayhead;

    private MediaPlayer EnsureMediaPlayer()
    {
        if (_mediaPlayer is not null) return _mediaPlayer;
        // Legacy MediaPlayer kept for code paths that still call LoadPreviewFromPath etc.
        // Not attached to PreviewPlayer — VideoCompositor handles all video rendering,
        // and a SwapChainPanel attachment here would produce a ghost overlay video that
        // ignores XAML Opacity. Muted so it doesn't double the compositor's audio.
        _mediaPlayer = new MediaPlayer { AutoPlay = false, IsMuted = true };
        _mediaPlayer.MediaOpened += OnPreviewMediaOpened;
        return _mediaPlayer;
    }

    private void OnPreviewMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_pendingSeek is { } seek)
            {
                _pendingSeek = null;
                try { sender.PlaybackSession.Position = seek; }
                catch { /* session may already be in a transitional state */ }
            }
            // If the timeline is mid-playback (e.g. we just swapped sources because the
            // playhead crossed into a clip on a different track), kick the new source
            // into Play — AutoPlay is off so it would otherwise sit paused.
            if (_vm?.IsPlaying == true)
            {
                try { sender.Play(); }
                catch { /* ignore transitional errors */ }
            }
        });
    }

    // (Previous multi-MediaPlayerElement compositing approach removed; multi-track video
    // is now handled by the Win2D-based VideoCompositorControl which owns its own
    // MediaPlayer-per-clip pool and single render surface.)

    private async void LoadPreviewFromPath(string? filePath, MediaType type)
    {
        if (string.IsNullOrEmpty(filePath)) { ClearPreview(); return; }
        if (string.Equals(filePath, _currentPreviewPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (!System.IO.File.Exists(filePath)) { ClearPreview(); return; }
        if (type == MediaType.Image)
        {
            // MediaPlayerElement doesn't render still images well — leave placeholder
            ClearPreview();
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var source = MediaSource.CreateFromStorageFile(file);
            var mp = EnsureMediaPlayer();
            mp.Source = new MediaPlaybackItem(source);
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            _currentPreviewPath = filePath;
        }
        catch
        {
            ClearPreview();
        }
    }

    private void ClearPreview()
    {
        if (_mediaPlayer is not null) _mediaPlayer.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        _currentPreviewPath = null;
        _pendingSeek = null;
        if (_vm is null || _vm.SelectedClip is null)
        {
            TbPreviewTitle.Text    = "No clip selected";
            TbPreviewSubtitle.Text = "— Drop media here or pick a clip from the timeline";
        }
    }

    private void LoadPreviewForClip(TimelineClip clip)
    {
        if (_vm is null) return;
        if (string.IsNullOrEmpty(clip.SourceId)) { ClearPreview(); return; }
        var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
        if (media is null) { ClearPreview(); return; }
        LoadPreviewFromPath(media.FilePath, media.Type);
    }

    private TimelineClip? FindVideoClipAt(TimeSpan position)
    {
        if (_vm is null) return null;
        foreach (var t in _vm.Tracks)
        {
            if (t.Kind != TrackKind.Video) continue;
            foreach (var c in t.Clips)
                if (position >= c.TimelineStart && position < c.TimelineEnd)
                    return c;
        }
        return null;
    }

    // ── Live preview presentation (transform, opacity, title text, speed) ────────

    private TimelineClip? _presentedClip;

    /// <summary>
    /// Tracks which clip's transform/opacity/title we're rendering. Subscribes to
    /// its PropertyChanged so inspector sliders update the preview live.
    /// </summary>
    private void SetPresentedClip(TimelineClip? clip)
    {
        if (ReferenceEquals(_presentedClip, clip)) return;
        if (_presentedClip is not null)
        {
            _presentedClip.PropertyChanged -= OnPresentedClipPropertyChanged;
            _presentedClip.Effects.CollectionChanged -= OnPresentedClipEffectsChanged;
            foreach (var fx in _presentedClip.Effects)
                fx.PropertyChanged -= OnPresentedClipEffectPropertyChanged;
        }
        _presentedClip = clip;
        if (_presentedClip is not null)
        {
            _presentedClip.PropertyChanged += OnPresentedClipPropertyChanged;
            _presentedClip.Effects.CollectionChanged += OnPresentedClipEffectsChanged;
            foreach (var fx in _presentedClip.Effects)
                fx.PropertyChanged += OnPresentedClipEffectPropertyChanged;
        }
        ApplyPresentation();
    }

    private void OnPresentedClipEffectsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ClipEffect fx in e.OldItems)
                fx.PropertyChanged -= OnPresentedClipEffectPropertyChanged;
        if (e.NewItems is not null)
            foreach (ClipEffect fx in e.NewItems)
                fx.PropertyChanged += OnPresentedClipEffectPropertyChanged;
        _previewEffects.Apply(_presentedClip);
    }

    private void OnPresentedClipEffectPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        _previewEffects.Apply(_presentedClip);

    private void OnPresentedClipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineClip.Scale)
                           or nameof(TimelineClip.PosX)
                           or nameof(TimelineClip.PosY)
                           or nameof(TimelineClip.Rotation)
                           or nameof(TimelineClip.Opacity)
                           or nameof(TimelineClip.Label)
                           or nameof(TimelineClip.Kind)
                           or nameof(TimelineClip.Speed)
                           or nameof(TimelineClip.CropLeft)
                           or nameof(TimelineClip.CropTop)
                           or nameof(TimelineClip.CropRight)
                           or nameof(TimelineClip.CropBottom)
                           or nameof(TimelineClip.FlipX)
                           or nameof(TimelineClip.FlipY))
        {
            ApplyPresentation();
        }
        // Color-grading properties (Exposure / Contrast / Saturation / Temperature / Tint)
        // intentionally do nothing in the preview here — the live effect chain is disabled.
    }

    private void ApplyPresentation()
    {
        var clip = _presentedClip;
        if (clip is null)
        {
            TitleClipOverlay.Visibility = Visibility.Collapsed;
            TransformOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (clip.Kind == ClipKind.Title)
        {
            TitleClipText.Text          = clip.Label;
            TitleClipOverlay.Visibility = Visibility.Visible;
            TransformOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleClipOverlay.Visibility = Visibility.Collapsed;
            // Video rendering + per-clip transforms are owned by VideoCompositor now;
            // the legacy PreviewPlayer/PreviewTransform manipulations have been removed
            // because PreviewPlayer is permanently collapsed (its SwapChainPanel would
            // otherwise paint a ghost layer that ignores XAML opacity).
        }

        ApplyClipSpeed(clip);
        UpdateTransformHandles();
    }

    /// <summary>Apply CropLeft/CropTop/CropRight/CropBottom as a RectangleGeometry on the
    /// MediaPlayerElement's Clip so the visible region matches the crop.</summary>
    private void ApplyClipCrop(TimelineClip clip)
    {
        double l = Math.Clamp(clip.CropLeft,   0, 0.95);
        double t = Math.Clamp(clip.CropTop,    0, 0.95);
        double r = Math.Clamp(clip.CropRight,  0, 0.95);
        double b = Math.Clamp(clip.CropBottom, 0, 0.95);

        if (l + r >= 1) { l = 0; r = 0; }
        if (t + b >= 1) { t = 0; b = 0; }

        if (l == 0 && t == 0 && r == 0 && b == 0)
        {
            PreviewPlayer.Clip = null;
            return;
        }

        double w = PreviewPlayer.ActualWidth;
        double h = PreviewPlayer.ActualHeight;
        if (w <= 0 || h <= 0) return;

        PreviewPlayer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(
                l * w,
                t * h,
                w * (1 - l - r),
                h * (1 - t - b)),
        };
    }

    private void ApplyClipSpeed(TimelineClip? clip)
    {
        if (_mediaPlayer?.PlaybackSession is not { } session) return;
        var rate = clip?.Speed ?? 1.0;
        if (rate < 0.1) rate = 0.1;
        if (rate > 4.0) rate = 4.0;
        try { session.PlaybackRate = rate; }
        catch { /* may throw before media opens */ }
    }

    /// <summary>
    /// Resolves which clip should drive the preview (the one at the playhead,
    /// or the user's selected clip when the playhead is in empty space).
    /// </summary>
    private void RefreshPresentation()
    {
        if (_vm is null) { SetPresentedClip(null); return; }

        // Prefer the explicitly-selected clip so the transform overlay edits whatever
        // the user clicked (including lower-track clips beneath a topmost one). Fall
        // back to the topmost clip at the playhead only when there's no selection.
        var clip = _vm.SelectedClip ?? FindVideoClipAt(_vm.Project.Playhead);

        // SelectedClip can be an orphan momentarily after a delete (the timeline's
        // context-menu delete fires History.Changed before nulling SelectedClip).
        // Don't keep an edit target that's no longer in any track.
        if (clip is not null && !IsClipInProject(clip))
            clip = FindVideoClipAt(_vm.Project.Playhead);

        SetPresentedClip(clip);
    }

    private bool IsClipInProject(TimelineClip clip)
    {
        if (_vm is null) return false;
        foreach (var t in _vm.Tracks)
            if (t.Clips.Contains(clip)) return true;
        return false;
    }

    // ── Figma-style transform handles ─────────────────────────────────────

    private enum TransformGesture { None, Move, ScaleTL, ScaleTR, ScaleBL, ScaleBR, Rotate }
    private TransformGesture _gesture;
    private Microsoft.UI.Xaml.Shapes.Shape? _activeHandle;

    private Windows.Foundation.Point _gestureStart;
    private double _gestureStartScale;
    private double _gestureStartRotation;
    private double _gestureStartPosX;
    private double _gestureStartPosY;
    private double _gestureStartDistFromCenter;
    private double _gestureStartAngleRad;
    private Windows.Foundation.Point _gestureBoxCenter;

    /// <summary>Recompute the position of the bounding box and every handle
    /// from the current presented clip's transform values + the MediaPlayerElement's
    /// rendered bounds.  Called on selection change, slider drag, or after a
    /// handle interaction.</summary>
    private void UpdateTransformHandles()
    {
        if (_vm is null || _presentedClip is null || _presentedClip.Kind == ClipKind.Title)
        {
            TransformOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var clip = _presentedClip;

        // The video is rendered by VideoCompositor, not PreviewPlayer. Replicate the
        // compositor's letterbox + per-clip transform math here so the overlay box
        // tracks exactly where each frame lands on screen.
        double cw = VideoCompositor.ActualWidth;
        double ch = VideoCompositor.ActualHeight;
        if (cw <= 0 || ch <= 0)
        {
            TransformOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        int outW = Math.Max(1, _vm.Project.Width);
        int outH = Math.Max(1, _vm.Project.Height);
        double outAspect = (double)outW / outH;
        double cAspect   = cw / ch;
        double frX, frY, frW, frH;
        if (cAspect > outAspect)
        {
            frH = ch;
            frW = frH * outAspect;
            frX = (cw - frW) / 2;
            frY = 0;
        }
        else
        {
            frW = cw;
            frH = frW / outAspect;
            frX = 0;
            frY = (ch - frH) / 2;
        }

        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double dstW  = frW * scale;
        double dstH  = frH * scale;
        double offX  = clip.PosX / (double)outW * frW;
        double offY  = clip.PosY / (double)outH * frH;
        double dstX  = frX + (frW - dstW) / 2 + offX;
        double dstY  = frY + (frH - dstH) / 2 + offY;

        // Map from VideoCompositor's coordinate space to TransformOverlay's so the
        // handles overlay the actual rendered frame even though they live under
        // PreviewViewport (a sibling element with its own transform).
        Windows.Foundation.Point tl, br;
        try
        {
            var ge = VideoCompositor.TransformToVisual(TransformOverlay);
            tl = ge.TransformPoint(new Windows.Foundation.Point(dstX, dstY));
            br = ge.TransformPoint(new Windows.Foundation.Point(dstX + dstW, dstY + dstH));
        }
        catch
        {
            TransformOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double x = Math.Min(tl.X, br.X);
        double y = Math.Min(tl.Y, br.Y);
        double w = Math.Abs(br.X - tl.X);
        double h = Math.Abs(br.Y - tl.Y);
        if (w < 4 || h < 4) { TransformOverlay.Visibility = Visibility.Collapsed; return; }

        TransformOverlay.Visibility = Visibility.Visible;

        Canvas.SetLeft(TransformBox, x);
        Canvas.SetTop (TransformBox, y);
        TransformBox.Width  = w;
        TransformBox.Height = h;

        PlaceHandle(HandleTL, x,       y);
        PlaceHandle(HandleTR, x + w,   y);
        PlaceHandle(HandleBL, x,       y + h);
        PlaceHandle(HandleBR, x + w,   y + h);

        double cx = x + w / 2;
        double rotateHandleY = y - 28;
        PlaceHandle(HandleRotate, cx, rotateHandleY);
        RotateLine.X1 = RotateLine.X2 = cx;
        RotateLine.Y1 = y;
        RotateLine.Y2 = rotateHandleY;
    }

    private static void PlaceHandle(Microsoft.UI.Xaml.Shapes.Shape handle, double centerX, double centerY)
    {
        Canvas.SetLeft(handle, centerX - handle.Width  / 2);
        Canvas.SetTop (handle, centerY - handle.Height / 2);
    }

    private void OnPreviewPlayerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Legacy (PreviewPlayer is permanently collapsed). Harmless to leave wired.
        UpdateTransformHandles();
    }

    private void OnVideoCompositorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTransformHandles();
    }

    // ── Preview viewport zoom (Ctrl+wheel) ───────────────────────────────

    private double _previewZoom = 1.0;

    private void OnPreviewAreaWheel(object sender, PointerRoutedEventArgs e)
    {
        // Only react to Ctrl+wheel — leave plain wheel alone so it doesn't fight
        // the timeline scrolling when the cursor happens to pass over the preview.
        if (!IsCtrlHeld()) return;

        int delta = e.GetCurrentPoint(PreviewArea).Properties.MouseWheelDelta;
        if (delta == 0) return;

        double factor  = delta > 0 ? 1.10 : 1.0 / 1.10;
        double newZoom = Math.Clamp(_previewZoom * factor, 0.2, 2.0);
        SetPreviewZoom(newZoom);
        e.Handled = true;
    }

    private void SetPreviewZoom(double zoom)
    {
        _previewZoom = zoom;
        PreviewViewportTransform.ScaleX = zoom;
        PreviewViewportTransform.ScaleY = zoom;
        // The video itself now renders in VideoCompositor (a sibling of PreviewViewport),
        // so apply the same scale to it or zoom would only move the (invisible) overlays.
        VideoCompositorTransform.ScaleX = zoom;
        VideoCompositorTransform.ScaleY = zoom;

        // Frame-edge guide only shows when we're zoomed away from 1.0
        FrameBoundary.Visibility = Math.Abs(zoom - 1.0) > 0.01
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Size the PreviewViewport so it matches the project's aspect ratio.
    /// It fits inside the available preview area (letterbox or pillarbox).
    /// </summary>
    private void UpdatePreviewCanvasSize()
    {
        if (_vm is null) return;
        double areaW = PreviewArea.ActualWidth;
        double areaH = PreviewArea.ActualHeight;
        if (areaW <= 0 || areaH <= 0) return;

        double projectAspect = (double)_vm.Project.Width / _vm.Project.Height;
        double areaAspect    = areaW / areaH;

        double w, h;
        if (projectAspect > areaAspect)
        {
            // Project is wider than the area — full width, height pinned to aspect
            w = areaW;
            h = areaW / projectAspect;
        }
        else
        {
            // Project is taller (or equal) — full height, width pinned to aspect
            h = areaH;
            w = areaH * projectAspect;
        }

        PreviewViewport.Width  = w;
        PreviewViewport.Height = h;
    }

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePreviewCanvasSize();

    // ── Right-click context menu on the preview ──────────────────────────

    private void OnPreviewContextOpening(object sender, object e)
    {
        // Enable items only when a real (non-title) clip is presented
        bool hasClip = _presentedClip is not null && _presentedClip.Kind != ClipKind.Title;
        foreach (var item in PreviewContextMenu.Items)
            if (item is Control c) c.IsEnabled = hasClip;
    }

    private void OnPreviewCenterAlign(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.PosX = 0;
        _presentedClip.PosY = 0;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewFitToFrame(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.Scale = 1.0;
        _presentedClip.PosX  = 0;
        _presentedClip.PosY  = 0;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewResetTransform(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.Scale    = 1.0;
        _presentedClip.PosX     = 0;
        _presentedClip.PosY     = 0;
        _presentedClip.Rotation = 0;
        _presentedClip.Opacity  = 1.0;
        _presentedClip.FlipX    = false;
        _presentedClip.FlipY    = false;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewResetCrop(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.CropLeft   = 0;
        _presentedClip.CropTop    = 0;
        _presentedClip.CropRight  = 0;
        _presentedClip.CropBottom = 0;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewFlipH(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.FlipX = !_presentedClip.FlipX;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewFlipV(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        _presentedClip.FlipY = !_presentedClip.FlipY;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewRotateCw(object sender, RoutedEventArgs e)  => BumpRotation( 90);
    private void OnPreviewRotateCcw(object sender, RoutedEventArgs e) => BumpRotation(-90);
    private void OnPreviewRotate180(object sender, RoutedEventArgs e) => BumpRotation(180);

    private void BumpRotation(double degrees)
    {
        if (_presentedClip is null || _vm is null) return;
        double r = _presentedClip.Rotation + degrees;
        while (r >  180) r -= 360;
        while (r < -180) r += 360;
        _presentedClip.Rotation = r;
        _vm.Project.IsModified = true;
    }

    private void OnPreviewSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        if (sender is MenuFlyoutItem item &&
            double.TryParse(item.Tag as string,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var speed))
        {
            _presentedClip.Speed = speed;
            _vm.Project.IsModified = true;
        }
    }

    private void OnPreviewDuplicate(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        var track = _vm.Tracks.FirstOrDefault(t => t.Clips.Contains(_presentedClip));
        if (track is null) return;
        _vm.History.Record(new ClipDuplicateAction(track, _presentedClip));
        Timeline.ViewModel = _vm;
    }

    private void OnPreviewDeleteClip(object sender, RoutedEventArgs e)
    {
        if (_presentedClip is null || _vm is null) return;
        var track = _vm.Tracks.FirstOrDefault(t => t.Clips.Contains(_presentedClip));
        if (track is null) return;
        var idx = track.Clips.IndexOf(_presentedClip);
        var clip = _presentedClip;
        if (_vm.SelectedClip == clip) _vm.SelectedClip = null;
        _vm.History.Record(new ClipDeleteAction(track, idx, clip));
        Timeline.Refresh();
        UpdateInspector(null);
    }

    // ── Body drag = move ──────────────────────────────────────────────────

    private void OnTransformBodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_presentedClip is null) return;
        _gesture = TransformGesture.Move;
        _gestureStart    = e.GetCurrentPoint(TransformOverlay).Position;
        _gestureStartPosX = _presentedClip.PosX;
        _gestureStartPosY = _presentedClip.PosY;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnTransformBodyMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture != TransformGesture.Move || _presentedClip is null || _vm is null) return;
        var pt = e.GetCurrentPoint(TransformOverlay).Position;
        if (!e.GetCurrentPoint(TransformOverlay).Properties.IsLeftButtonPressed) return;

        // PosX/PosY are in project pixels (range matches inspector slider, e.g. -1920..1920),
        // but the pointer delta is in overlay/screen pixels. The compositor draws each clip
        // with on-screen offset = PosX * (frameRect.Width / OutputWidth), so a 1:1 cursor-to-
        // clip drag requires multiplying the pointer delta by (OutputWidth / frameRect.Width).
        var (sx, sy) = ProjectPxPerOverlayPx();
        double newX = _gestureStartPosX + (pt.X - _gestureStart.X) * sx;
        double newY = _gestureStartPosY + (pt.Y - _gestureStart.Y) * sy;

        SnapGuide guideX = SnapGuide.None;
        SnapGuide guideY = SnapGuide.None;

        if (IsCtrlHeld())
        {
            // Snap targets in project pixels (PosX/Y units). Viewport half-extents are
            // simply the project resolution / 2; scaled clip half-extents are scale * that.
            double scale = _presentedClip.Scale;
            double halfW  = scale * _vm.Project.Width  / 2.0;
            double halfH  = scale * _vm.Project.Height / 2.0;
            double vHalfW = _vm.Project.Width  / 2.0;
            double vHalfH = _vm.Project.Height / 2.0;

            // 8 screen-pixel snap radius expressed in project pixels (so the feel is
            // the same regardless of preview-area size or project resolution).
            double snapPx = 8.0;
            double thresholdX = snapPx * sx;
            double thresholdY = snapPx * sy;

            (newX, int ix) = SnapTo(newX, new[]
            {
                0.0,             // 0: clip center on vertical centerline
                -halfW,          // 1: clip right edge on vertical centerline
                 halfW,          // 2: clip left edge on vertical centerline
                 vHalfW - halfW, // 3: clip right edge on viewport right
                -vHalfW + halfW, // 4: clip left edge on viewport left
            }, thresholdX);
            guideX = ix switch
            {
                0 or 1 or 2 => SnapGuide.Center,
                3           => SnapGuide.Far,    // right edge
                4           => SnapGuide.Near,   // left edge
                _           => SnapGuide.None,
            };

            (newY, int iy) = SnapTo(newY, new[]
            {
                0.0,             // 0: clip center on horizontal centerline
                -halfH,          // 1: clip bottom edge on horizontal centerline
                 halfH,          // 2: clip top edge on horizontal centerline
                 vHalfH - halfH, // 3: clip bottom edge on viewport bottom
                -vHalfH + halfH, // 4: clip top edge on viewport top
            }, thresholdY);
            guideY = iy switch
            {
                0 or 1 or 2 => SnapGuide.Center,
                3           => SnapGuide.Far,    // bottom edge
                4           => SnapGuide.Near,   // top edge
                _           => SnapGuide.None,
            };
        }

        UpdateSnapGuides(guideX, guideY);

        _presentedClip.PosX = newX;
        _presentedClip.PosY = newY;
        e.Handled = true;
    }

    private enum SnapGuide { None, Near, Center, Far }

    private static (double value, int index) SnapTo(double value, double[] targets, double threshold = 8.0)
    {
        double best = value;
        double bestDist = threshold;
        int bestIdx = -1;
        for (int i = 0; i < targets.Length; i++)
        {
            double d = Math.Abs(value - targets[i]);
            if (d < bestDist) { bestDist = d; best = targets[i]; bestIdx = i; }
        }
        return (best, bestIdx);
    }

    private void UpdateSnapGuides(SnapGuide x, SnapGuide y)
    {
        double w = PreviewPlayer.ActualWidth;
        double h = PreviewPlayer.ActualHeight;

        SetVLine(SnapGuideVLeft,   0,     h);
        SetVLine(SnapGuideVCenter, w / 2, h);
        SetVLine(SnapGuideVRight,  w,     h);
        SetHLine(SnapGuideHTop,    0,     w);
        SetHLine(SnapGuideHCenter, h / 2, w);
        SetHLine(SnapGuideHBottom, h,     w);

        SnapGuideVLeft.Visibility   = x == SnapGuide.Near   ? Visibility.Visible : Visibility.Collapsed;
        SnapGuideVCenter.Visibility = x == SnapGuide.Center ? Visibility.Visible : Visibility.Collapsed;
        SnapGuideVRight.Visibility  = x == SnapGuide.Far    ? Visibility.Visible : Visibility.Collapsed;
        SnapGuideHTop.Visibility    = y == SnapGuide.Near   ? Visibility.Visible : Visibility.Collapsed;
        SnapGuideHCenter.Visibility = y == SnapGuide.Center ? Visibility.Visible : Visibility.Collapsed;
        SnapGuideHBottom.Visibility = y == SnapGuide.Far    ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetVLine(Microsoft.UI.Xaml.Shapes.Line line, double x, double h)
    {
        line.X1 = x; line.X2 = x; line.Y1 = 0; line.Y2 = h;
    }

    private static void SetHLine(Microsoft.UI.Xaml.Shapes.Line line, double y, double w)
    {
        line.X1 = 0; line.X2 = w; line.Y1 = y; line.Y2 = y;
    }

    private void HideSnapGuides() => UpdateSnapGuides(SnapGuide.None, SnapGuide.None);

    private static bool IsCtrlHeld() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>How many project-pixels each overlay-pixel of pointer movement corresponds to,
    /// given the compositor's current letterbox. Used so transform-box drags translate the
    /// clip 1:1 with the cursor.</summary>
    private (double sx, double sy) ProjectPxPerOverlayPx()
    {
        if (_vm is null) return (1, 1);
        double cw = VideoCompositor.ActualWidth;
        double ch = VideoCompositor.ActualHeight;
        int outW = Math.Max(1, _vm.Project.Width);
        int outH = Math.Max(1, _vm.Project.Height);
        if (cw <= 0 || ch <= 0) return (1, 1);

        double outAspect = (double)outW / outH;
        double cAspect   = cw / ch;
        double frW, frH;
        if (cAspect > outAspect) { frH = ch; frW = frH * outAspect; }
        else                     { frW = cw; frH = frW / outAspect; }
        if (frW <= 0 || frH <= 0) return (1, 1);

        return ((double)outW / frW, (double)outH / frH);
    }

    private void OnTransformBodyReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture != TransformGesture.Move) return;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (_vm is not null) _vm.Project.IsModified = true;
        _gesture = TransformGesture.None;
        HideSnapGuides();
        e.Handled = true;
    }

    private void OnTransformBodyCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _gesture = TransformGesture.None;
        HideSnapGuides();
    }

    // ── Corner handles = scale, top knob = rotate ─────────────────────────

    private void OnTransformHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_presentedClip is null || sender is not Microsoft.UI.Xaml.Shapes.Shape s) return;

        _activeHandle = s;
        _gesture = (s.Tag as string) switch
        {
            "TL"  => TransformGesture.ScaleTL,
            "TR"  => TransformGesture.ScaleTR,
            "BL"  => TransformGesture.ScaleBL,
            "BR"  => TransformGesture.ScaleBR,
            "ROT" => TransformGesture.Rotate,
            _     => TransformGesture.None,
        };
        if (_gesture == TransformGesture.None) return;

        _gestureStart         = e.GetCurrentPoint(TransformOverlay).Position;
        _gestureStartScale    = _presentedClip.Scale;
        _gestureStartRotation = _presentedClip.Rotation;
        _gestureStartPosX     = _presentedClip.PosX;
        _gestureStartPosY     = _presentedClip.PosY;

        // Center of the bounding box in overlay coordinates
        _gestureBoxCenter = new Windows.Foundation.Point(
            Canvas.GetLeft(TransformBox) + TransformBox.Width  / 2,
            Canvas.GetTop (TransformBox) + TransformBox.Height / 2);

        var dx = _gestureStart.X - _gestureBoxCenter.X;
        var dy = _gestureStart.Y - _gestureBoxCenter.Y;
        _gestureStartDistFromCenter = Math.Sqrt(dx * dx + dy * dy);
        _gestureStartAngleRad       = Math.Atan2(dy, dx);

        s.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnTransformHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is TransformGesture.None or TransformGesture.Move) return;
        if (_presentedClip is null) return;
        var pt = e.GetCurrentPoint(TransformOverlay).Position;
        if (!e.GetCurrentPoint(TransformOverlay).Properties.IsLeftButtonPressed) return;

        var dx = pt.X - _gestureBoxCenter.X;
        var dy = pt.Y - _gestureBoxCenter.Y;

        if (_gesture == TransformGesture.Rotate)
        {
            double currentAngle = Math.Atan2(dy, dx);
            double deltaDeg = (currentAngle - _gestureStartAngleRad) * 180.0 / Math.PI;
            double newRot = _gestureStartRotation + deltaDeg;
            // Wrap to [-180, 180]
            while (newRot > 180)  newRot -= 360;
            while (newRot < -180) newRot += 360;
            if (IsCtrlHeld())
                newRot = Math.Round(newRot / 15.0) * 15.0;  // 15° increments
            _presentedClip.Rotation = newRot;
        }
        else
        {
            // Scale based on how far the cursor moved relative to the box center
            double currentDist = Math.Sqrt(dx * dx + dy * dy);
            if (_gestureStartDistFromCenter < 1) return;
            double ratio = currentDist / _gestureStartDistFromCenter;
            double newScale = Math.Clamp(_gestureStartScale * ratio, 0.1, 3.0);
            if (IsCtrlHeld())
                newScale = Math.Round(newScale * 4.0) / 4.0;  // 25% increments
            _presentedClip.Scale = newScale;
        }
        e.Handled = true;
    }

    private void OnTransformHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture is TransformGesture.None or TransformGesture.Move) return;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (_vm is not null) _vm.Project.IsModified = true;
        _gesture = TransformGesture.None;
        _activeHandle = null;
        e.Handled = true;
    }

    private void OnTransformHandleCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _gesture = TransformGesture.None;
        _activeHandle = null;
    }

    // ── Drag onto preview area ─────────────────────────────────────────

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        if (e.DataView.Properties.ContainsKey(BTAP.Controls.MediaTileControl.DragDataFormat)
            || e.DataView.Contains(StandardDataFormats.Text)
            || e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to preview";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;

        // 1) Tile dragged from the media bin → load by ID
        string? mediaId = null;
        if (e.DataView.Properties.TryGetValue(BTAP.Controls.MediaTileControl.DragDataFormat, out var v))
            mediaId = v as string;
        if (string.IsNullOrEmpty(mediaId) && e.DataView.Contains(StandardDataFormats.Text))
            mediaId = await e.DataView.GetTextAsync();
        if (!string.IsNullOrEmpty(mediaId))
        {
            var media = _vm.MediaBin.FirstOrDefault(m => m.Id == mediaId);
            if (media is not null)
            {
                LoadPreviewFromPath(media.FilePath, media.Type);
                UpdatePreviewOverlayForMedia(media);
            }
            return;
        }

        // 2) File from OS dragged in → import it into the bin and preview
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var it in items)
            {
                if (it is not StorageFile sf) continue;
                var item = await MediaItem.FromStorageFileAsync(sf);
                if (_vm.MediaBin.Any(m => m.FilePath == item.FilePath)) continue;
                _vm.MediaBin.Add(item);
                _mediaTiles.Add(MediaTileData.FromMediaItem(item));
                LoadPreviewFromPath(item.FilePath, item.Type);
                UpdatePreviewOverlayForMedia(item);
            }
            TbBinCount.Text = $"· {_mediaTiles.Count}";
            UpdateEmptyMediaHint();
        }
    }

    private void UpdatePreviewOverlayForMedia(MediaItem media)
    {
        TbPreviewTitle.Text = System.IO.Path.GetFileNameWithoutExtension(media.Name);
        var dur = media.Duration > TimeSpan.Zero ? FormatHms(media.Duration) : "—";
        TbPreviewSubtitle.Text = $"— {media.Type.ToString().ToUpperInvariant()} · {dur}";
    }

    private void OnPlaybackTick(object? sender, object e)
    {
        if (_vm is null) return;
        var frame = TimeSpan.FromSeconds(1.0 / _vm.Project.FrameRate);
        _vm.Project.Playhead += frame;

        if (_vm.Project.Playhead >= _vm.Project.Duration)
        {
            if (_vm.IsLooping)
                _vm.Project.Playhead = TimeSpan.Zero;
            else
            {
                _vm.Project.Playhead = _vm.Project.Duration;
                StopPlayback();
                RefreshPlayheadUI();
                return;
            }
        }

        // When the playhead crosses into / out of a clip, sync the preview overlay state.
        var clipNow = FindVideoClipAt(_vm.Project.Playhead);
        if (clipNow != _lastClipAtPlayhead)
        {
            _lastClipAtPlayhead = clipNow;
            SetPresentedClip(clipNow ?? _vm.SelectedClip);

            if (clipNow is null && !HasVideoContentAfter(_vm.Project.Playhead) && !_vm.IsLooping)
            {
                StopPlayback();
                RefreshPlayheadUI();
                return;
            }
        }

        // Every tick: let the compositor reconcile its layer pool so newly-active or
        // newly-finished clips on any track appear/disappear in the swap chain.
        VideoCompositor.Sync(_vm.Project.Playhead);

        RefreshPlayheadUI();
    }

    private bool HasVideoContentAfter(TimeSpan position)
    {
        if (_vm is null) return false;
        foreach (var t in _vm.Tracks)
        {
            if (t.Kind != TrackKind.Video) continue;
            foreach (var c in t.Clips)
                if (c.TimelineStart >= position) return true;
        }
        return false;
    }

    private void RefreshPlayheadUI()
    {
        if (_vm is null) return;
        PlaybackBar.SetPlayhead(_vm.PlayheadLabel);
        Timeline.UpdatePlayhead(_vm.Project.Playhead);
    }

    // ── Media grid ──────────────────────────────────────────────────────

    private void PopulateMediaGrid()
    {
        _mediaTiles.Clear();
        if (_vm is null) return;
        foreach (var item in _vm.MediaBin)
            _mediaTiles.Add(MediaTileData.FromMediaItem(item));
        TbBinCount.Text = $"· {_mediaTiles.Count}";
        UpdateEmptyMediaHint();
    }

    private void UpdateEmptyMediaHint()
    {
        EmptyMediaHint.Visibility = _mediaTiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnMediaTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_vm is null || sender is not BTAP.Controls.MediaTileControl { Data: { } data })
            return;
        var media = _vm.MediaBin.FirstOrDefault(m => m.Id == data.Id);
        _vm.SelectedMedia = media;
        if (media is not null)
        {
            LoadPreviewFromPath(media.FilePath, media.Type);
            UpdatePreviewOverlayForMedia(media);
        }
    }

    private void OnMediaTileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_vm is null || sender is not BTAP.Controls.MediaTileControl { Data: { } data }) return;
        var media = _vm.MediaBin.FirstOrDefault(m => m.Id == data.Id);
        if (media is null) return;

        bool isAudio = media.Type == MediaType.Audio;
        var track = _vm.Tracks.FirstOrDefault(t =>
            isAudio ? t.Kind == TrackKind.Audio : t.Kind == TrackKind.Video);
        if (track is null) return;

        var start = track.Clips.Count > 0
            ? track.Clips.Max(c => c.TimelineEnd)
            : TimeSpan.Zero;

        var clip = new TimelineClip
        {
            Label = System.IO.Path.GetFileNameWithoutExtension(media.Name),
            Kind = media.Type switch
            {
                MediaType.Audio => ClipKind.Audio,
                _               => ClipKind.Video,
            },
            TimelineStart = start,
            Duration = media.Duration > TimeSpan.Zero ? media.Duration : TimeSpan.FromSeconds(5),
            SourceId = media.Id,
            ColorHue = media.Type == MediaType.Audio ? 100 : 168,
        };

        _vm.History.Record(new ClipAddAction(track, clip));
        Timeline.ViewModel = _vm;

        // Select the new clip and load its preview (clear any multi-selection first)
        foreach (var tr in _vm.Tracks)
            foreach (var c in tr.Clips)
                c.IsSelected = false;
        clip.IsSelected = true;
        _vm.SelectedClip = clip;
        UpdateInspector(clip);
        UpdateClipHeader(clip);
        UpdatePreviewOverlay(clip);
        LoadPreviewFromPath(media.FilePath, media.Type);
    }

    private async void OnImportMedia(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(
            (Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);

        foreach (var ext in MediaItem.VideoExtensions
            .Concat(MediaItem.AudioExtensions)
            .Concat(MediaItem.ImageExtensions))
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            var item = await MediaItem.FromStorageFileAsync(file);
            if (_vm.MediaBin.Any(m => m.FilePath == item.FilePath)) continue;
            _vm.MediaBin.Add(item);
            _mediaTiles.Add(MediaTileData.FromMediaItem(item));
        }
        TbBinCount.Text = $"· {_mediaTiles.Count}";
        UpdateEmptyMediaHint();
    }

    private void OnLibTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        SetActiveLibBtn(btn);
        var tag = (string?)btn.Tag ?? "media";
        _vm.MediaLibraryTab = tag;

        MediaScroll.Visibility   = tag == "media"   ? Visibility.Visible : Visibility.Collapsed;
        TitlesScroll.Visibility  = tag == "titles"  ? Visibility.Visible : Visibility.Collapsed;
        EffectsScroll.Visibility = tag == "fx"      ? Visibility.Visible : Visibility.Collapsed;
        AudioScroll.Visibility   = tag == "audio"   ? Visibility.Visible : Visibility.Collapsed;

        TbBinCount.Text = tag switch
        {
            "titles" => $"· {TitlesList.Children.Count}",
            "fx"     => $"· {EffectsList.Children.Count}",
            "audio"  => $"· {AudioFxList.Children.Count}",
            _        => $"· {_mediaTiles.Count}",
        };
    }

    private void PopulateLibraryPresets()
    {
        TitlesList.Children.Clear();
        foreach (var (name, kind) in new (string, string)[]
        {
            ("Lower Third",  "Aki Yamamoto · Director"),
            ("Chapter Card", "Chapter 01 · Establishing"),
            ("End Slate",    "Credits · Roll"),
            ("Quote",        "Pull quote · Centered"),
            ("Subtitle",     "Bottom-aligned subtitle"),
            ("Headline",     "Bold serif headline"),
        })
        {
            TitlesList.Children.Add(MakePresetCard(name, kind, () => AddTitleClip(name)));
        }

        EffectsList.Children.Clear();
        foreach (var (name, kind) in new (string, string)[]
        {
            ("Gaussian Blur",   "Blur · 0–50px"),
            ("Cross Dissolve",  "Transition · 1.0s"),
            ("Chroma Key",      "Green screen removal"),
            ("Sharpen",         "Edge enhancement"),
            ("Vignette",        "Edge darken"),
            ("Pixelate",        "Mosaic effect"),
            ("Glow",            "Soft bloom"),
            ("Drop Shadow",     "Layer shadow"),
            ("Mirror",          "Reflection"),
        })
        {
            var effectName = name;
            EffectsList.Children.Add(MakePresetCard(name, kind, () => AddEffectToSelected(effectName)));
        }

        AudioFxList.Children.Clear();
        foreach (var (name, kind) in new (string, string)[]
        {
            ("EQ — Voice",      "Vocal preset"),
            ("EQ — Music",      "Full-range preset"),
            ("Compressor",      "Dynamic range"),
            ("De-esser",        "Sibilance reduction"),
            ("Reverb — Room",   "Small space"),
            ("Reverb — Hall",   "Large space"),
            ("Noise Gate",      "Silence threshold"),
            ("Fade In/Out",     "Linear envelope"),
        })
        {
            var effectName = name;
            AudioFxList.Children.Add(MakePresetCard(name, kind, () => AddEffectToSelected(effectName)));
        }
    }

    private UIElement MakePresetCard(string name, string sub, Action? onDoubleClick)
    {
        var nameLbl = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        var subLbl = new TextBlock
        {
            Text = sub,
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            Margin = new Thickness(0, 2, 0, 0),
        };
        var stack = new StackPanel { Padding = new Thickness(10, 8, 10, 8) };
        stack.Children.Add(nameLbl);
        stack.Children.Add(subLbl);

        var border = new Border
        {
            Background = (Brush)Application.Current.Resources["BgSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = stack,
        };

        if (onDoubleClick is not null)
            border.DoubleTapped += (_, _) => onDoubleClick();

        return border;
    }

    private void AddTitleClip(string presetName)
    {
        if (_vm is null) return;
        var track = _vm.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        if (track is null) return;

        var start = track.Clips.Count > 0
            ? track.Clips.Max(c => c.TimelineEnd)
            : TimeSpan.Zero;

        var clip = new TimelineClip
        {
            Label         = presetName,
            Kind          = ClipKind.Title,
            TimelineStart = start,
            Duration      = TimeSpan.FromSeconds(4),
            ColorHue      = 30,
        };

        _vm.History.Record(new ClipAddAction(track, clip));
        Timeline.ViewModel = _vm;
    }

    private void OnLibSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;
        var query = LibSearchBox.Text.Trim().ToLowerInvariant();
        _mediaTiles.Clear();
        foreach (var item in _vm.MediaBin)
        {
            if (query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                _mediaTiles.Add(MediaTileData.FromMediaItem(item));
        }
        TbBinCount.Text = $"· {_mediaTiles.Count}";
        UpdateEmptyMediaHint();
    }

    // ── Timeline ─────────────────────────────────────────────────────────

    private void WireTimeline()
    {
        if (_vm is null) return;
        Timeline.ViewModel = _vm;
    }

    private void OnTimelineClipTapped(object? sender, TimelineClip clip)
    {
        if (_vm is null) return;
        // The TimelineControl already manages IsSelected for multi-select; we just
        // mirror the primary selection here and refresh the per-clip UI panels.
        _vm.SelectedClip = clip;
        UpdateInspector(clip);
        UpdateClipHeader(clip);
        UpdatePreviewOverlay(clip);
        LoadPreviewForClip(clip);
        Timeline.Refresh();
        RefreshPresentation();
    }

    private void OnTimelineSelectionCleared(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedClip = null;
        UpdateInspector(null);
        TbPreviewTitle.Text = "No clip selected";
        TbPreviewSubtitle.Text = "— Drop media here or pick a clip from the timeline";
        Timeline.Refresh();
        RefreshPresentation();
    }

    private void UpdatePreviewOverlay(TimelineClip clip)
    {
        TbPreviewTitle.Text = clip.Label;
        TbPreviewSubtitle.Text = $"— {clip.Kind.ToString().ToUpperInvariant()} · {FormatHms(clip.Duration)}";
    }

    private void UpdateProgramInfo()
    {
        if (_vm is null) return;
        var p = _vm.Project;
        TbProgramInfo.Text = $"PROGRAM · {p.Width} × {p.Height} · {p.FrameRate:G3}p";
    }

    // ── Program-info flyout: change resolution / frame rate ──────────────────

    private void OnProgramInfoTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            FlyoutBase.ShowAttachedFlyout(fe);
    }

    private void OnProgramInfoHover(object sender, PointerRoutedEventArgs e)
    {
        BorderProgramInfo.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    }

    private void OnProgramInfoUnhover(object sender, PointerRoutedEventArgs e)
    {
        BorderProgramInfo.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    private void OnProgramInfoFlyoutOpening(object? sender, object e)
    {
        if (_vm is null) return;
        var p = _vm.Project;
        NbWidth.Value  = p.Width;
        NbHeight.Value = p.Height;
        NbFps.Value    = p.FrameRate;
        SelectPresetByTag(CmbResPreset, $"{p.Width},{p.Height}");
        SelectFpsPreset(p.FrameRate);
    }

    private void OnResPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbResPreset.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string;
        if (tag is null or "custom") return;
        var parts = tag.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) &&
            int.TryParse(parts[1], out var h))
        {
            NbWidth.Value  = w;
            NbHeight.Value = h;
        }
    }

    private void OnFpsPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbFpsPreset.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string;
        if (tag is null or "custom") return;
        if (double.TryParse(tag, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var fps))
        {
            NbFps.Value = fps;
        }
    }

    private void OnProgramInfoApply(object sender, RoutedEventArgs e)
    {
        if (_vm is null) { ProgramInfoFlyout.Hide(); return; }

        int   w   = (int)Math.Round(double.IsNaN(NbWidth.Value)  ? _vm.Project.Width  : NbWidth.Value);
        int   h   = (int)Math.Round(double.IsNaN(NbHeight.Value) ? _vm.Project.Height : NbHeight.Value);
        double fps = double.IsNaN(NbFps.Value) ? _vm.Project.FrameRate : NbFps.Value;

        if (w < 64 || h < 64 || fps < 1) { ProgramInfoFlyout.Hide(); return; }

        // Round to even (H.264 requires multiples of 2)
        if (w % 2 != 0) w++;
        if (h % 2 != 0) h++;

        bool changed = w != _vm.Project.Width || h != _vm.Project.Height
                    || Math.Abs(fps - _vm.Project.FrameRate) > 0.001;

        _vm.Project.Width     = w;
        _vm.Project.Height    = h;
        _vm.Project.FrameRate = fps;
        if (changed) _vm.Project.IsModified = true;

        // Re-tick the playback timer at the new rate if it's running
        if (_playbackTimer is not null)
            _playbackTimer.Interval = TimeSpan.FromSeconds(1.0 / fps);

        UpdateProgramInfo();
        UpdateStatusBar();
        UpdatePreviewCanvasSize();

        ProgramInfoFlyout.Hide();
    }

    private void OnProgramInfoCancel(object sender, RoutedEventArgs e) =>
        ProgramInfoFlyout.Hide();

    private static void SelectPresetByTag(ComboBox combo, string targetTag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == targetTag)
            { combo.SelectedIndex = i; return; }
        }
        // Fall back to the "custom" item
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == "custom")
            { combo.SelectedIndex = i; return; }
        }
        combo.SelectedIndex = -1;
    }

    private void SelectFpsPreset(double fps)
    {
        for (int i = 0; i < CmbFpsPreset.Items.Count; i++)
        {
            if (CmbFpsPreset.Items[i] is ComboBoxItem item &&
                double.TryParse(item.Tag as string,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var itemFps) &&
                Math.Abs(itemFps - fps) < 0.01)
            {
                CmbFpsPreset.SelectedIndex = i;
                return;
            }
        }
        // Fall back to the "custom" item
        for (int i = 0; i < CmbFpsPreset.Items.Count; i++)
        {
            if (CmbFpsPreset.Items[i] is ComboBoxItem item && (item.Tag as string) == "custom")
            { CmbFpsPreset.SelectedIndex = i; return; }
        }
    }

    private void OnTimelineClipDeleted(object? sender, TimelineClip clip)
    {
        if (_vm is null) return;
        if (_vm.SelectedClip == clip)
        {
            _vm.SelectedClip = null;
            UpdateInspector(null);
            TbPreviewTitle.Text = "No clip selected";
            TbPreviewSubtitle.Text = "— Drop media here or pick a clip from the timeline";
        }
    }

    private void OnPlayheadChanged(object? sender, TimeSpan position)
    {
        if (_vm is null) return;
        _vm.Playhead = position;
        PlaybackBar.SetPlayhead(_vm.PlayheadLabel);
        SeekPreviewToPlayhead(position);
        RefreshPresentation();
    }

    /// <summary>
    /// Find the video clip under the playhead and seek MediaPlayer to its in-source offset.
    /// If the playhead is in empty space, pause without changing the source so the last
    /// rendered frame stays visible (instead of replaying the source from zero).
    /// </summary>
    private void SeekPreviewToPlayhead(TimeSpan position)
    {
        if (_vm is null) return;
        // Let the compositor reconcile which clips are active at this playhead,
        // then issue a per-layer seek so every active video jumps to its offset.
        VideoCompositor.Sync(position);
        VideoCompositor.Seek(position);
    }

    // ── Playback bar ─────────────────────────────────────────────────────

    private void WirePlaybackBar()
    {
        if (_vm is null) return;
        PlaybackBar.SetPlayhead(_vm.PlayheadLabel);
        PlaybackBar.SetDuration(_vm.DurationLabel);
        PlaybackBar.LoopClicked       += OnLoop;
        PlaybackBar.MarkerClicked     += OnMarker;
        PlaybackBar.FullscreenClicked += OnViewFullscreen;
    }

    private void OnLoop(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.IsLooping = !_vm.IsLooping;
        PlaybackBar.SetIsLooping(_vm.IsLooping);
    }

    private void OnMarker(object sender, RoutedEventArgs e) =>
        _vm?.AddMarkerCommand.Execute(null);

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
        Focus(FocusState.Programmatic);  // keep Space-bar shortcut working
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        StopPlayback();
        _vm.StepBackCommand.Execute(null);
        RefreshPlayheadUI();
        SeekPreviewToPlayhead(_vm.Project.Playhead);
    }

    private void OnStepFwd(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        StopPlayback();
        _vm.StepForwardCommand.Execute(null);
        RefreshPlayheadUI();
        SeekPreviewToPlayhead(_vm.Project.Playhead);
    }

    // ── Tool / mode / zoom ───────────────────────────────────────────────

    private void OnModeChange(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        SetActiveModeBtn(btn);
        var tag = (string?)btn.Tag ?? "edit";

        _vm.Mode = tag switch
        {
            "cut"    => EditorMode.Cut,
            "color"  => EditorMode.Color,
            "audio"  => EditorMode.Audio,
            "export" => EditorMode.Export,
            _        => EditorMode.Edit,
        };

        switch (tag)
        {
            case "color":
                SetActiveInspBtn(BtnInspColor);
                _vm.InspectorTab = "color";
                UpdateInspector(_vm.SelectedClip);
                break;

            case "audio":
                SetActiveInspBtn(BtnInspAudio);
                _vm.InspectorTab = "audio";
                UpdateInspector(_vm.SelectedClip);
                // Highlight audio library tab too
                SetActiveLibBtn(BtnLibAudio);
                OnLibTabClick(BtnLibAudio, new RoutedEventArgs());
                break;

            case "cut":
                // Cut mode: hide inspector to maximize timeline real estate
                InspectorPanel.Visibility = Visibility.Collapsed;
                BodyGrid.ColumnDefinitions[2].Width = new GridLength(0);
                return;

            case "export":
                OnExport(this, new RoutedEventArgs());
                break;
        }

        // Restore inspector for any non-cut mode
        InspectorPanel.Visibility = Visibility.Visible;
        BodyGrid.ColumnDefinitions[2].Width = new GridLength(296);
    }

    private void OnToolChange(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        SetActiveToolBtn(btn);
        _vm.ActiveTool = (string?)btn.Tag switch
        {
            "razor" => ActiveTool.Razor,
            "text"  => ActiveTool.Text,
            "hand"  => ActiveTool.Hand,
            _       => ActiveTool.Cursor,
        };
    }

    private void OnToggleSnap(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SnapEnabled = !_vm.SnapEnabled;
        BtnSnap.Foreground = _vm.SnapEnabled
            ? (Brush)Application.Current.Resources["AccentInkBrush"]
            : (Brush)Application.Current.Resources["TextMutedBrush"];
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ZoomInCommand.Execute(null);
        ZoomSlider.Value = _vm.TimelineZoom * 100;
        Timeline.SetZoom(_vm.TimelineZoom);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ZoomOutCommand.Execute(null);
        ZoomSlider.Value = _vm.TimelineZoom * 100;
        Timeline.SetZoom(_vm.TimelineZoom);
    }

    private void OnZoomChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.TimelineZoom = e.NewValue / 100.0;
        Timeline.SetZoom(_vm.TimelineZoom);
    }

    /// <summary>
    /// Wheel-zoom inside the TimelineControl raises ZoomChanged; mirror it onto the
    /// toolbar slider so the displayed % stays in sync with what the user actually has.
    /// </summary>
    private void OnTimelineZoomChanged(object? sender, double newZoom)
    {
        var sliderValue = newZoom * 100.0;
        if (Math.Abs(ZoomSlider.Value - sliderValue) > 0.01)
            ZoomSlider.Value = sliderValue;
    }

    // ── Inspector ────────────────────────────────────────────────────────

    private void OnInspTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        SetActiveInspBtn(btn);
        _vm.InspectorTab = (string?)btn.Tag ?? "video";
        UpdateInspector(_vm.SelectedClip);
    }

    private void UpdateClipHeader(TimelineClip clip)
    {
        TbSelectedClipName.Text = clip.Label;
        var start = clip.TimelineStart;
        var end = clip.TimelineStart + clip.Duration;
        TbSelectedClipMeta.Text =
            $"{FormatHms(start)} — {FormatHms(end)}";
    }

    private static string FormatHms(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}:{(int)(ts.Milliseconds / 41.667):D2}";

    private void UpdateInspector(TimelineClip? clip)
    {
        InspectorContent.Children.Clear();
        if (clip is null || _vm is null) return;

        switch (_vm.InspectorTab)
        {
            case "audio":   BuildInspectorAudio(clip);   break;
            case "effects": BuildInspectorEffects(clip); break;
            case "color":   BuildInspectorColor(clip);   break;
            default:        BuildInspectorVideo(clip);   break;
        }
    }

    private void BuildInspectorVideo(TimelineClip clip)
    {
        AddInspField("Position",  FormatHms(clip.TimelineStart));
        AddInspField("Duration",  FormatHms(clip.Duration));
        AddInspField("Source In", FormatHms(clip.SourceStart));

        InspectorContent.Children.Add(MakeInspSectionHeader("CLIP"));
        AddInspSlider("Volume", clip.Volume * 100,  0,  100, v => clip.Volume = v / 100.0);
        AddInspSlider("Speed",  clip.Speed  * 100, 10,  400, v => clip.Speed  = v / 100.0);

        if (clip.Kind is ClipKind.Video or ClipKind.Title)
        {
            InspectorContent.Children.Add(MakeInspSectionHeader("TRANSFORM"));
            AddInspSlider("Scale",    clip.Scale    * 100,   10,  300, v => clip.Scale    = v / 100.0);
            AddInspSlider("Pos X",    clip.PosX,          -1920, 1920, v => clip.PosX     = v);
            AddInspSlider("Pos Y",    clip.PosY,          -1080, 1080, v => clip.PosY     = v);
            AddInspSlider("Rotation", clip.Rotation,        -180,  180, v => clip.Rotation = v);
            AddInspSlider("Opacity",  clip.Opacity  * 100,    0,  100, v => clip.Opacity  = v / 100.0);

            InspectorContent.Children.Add(MakeInspSectionHeader("CROP"));
            AddInspSlider("Left",   clip.CropLeft   * 100, 0, 95, v => clip.CropLeft   = v / 100.0);
            AddInspSlider("Top",    clip.CropTop    * 100, 0, 95, v => clip.CropTop    = v / 100.0);
            AddInspSlider("Right",  clip.CropRight  * 100, 0, 95, v => clip.CropRight  = v / 100.0);
            AddInspSlider("Bottom", clip.CropBottom * 100, 0, 95, v => clip.CropBottom = v / 100.0);
        }
    }

    private void BuildInspectorAudio(TimelineClip clip)
    {
        AddInspField("Position", FormatHms(clip.TimelineStart));
        AddInspField("Duration", FormatHms(clip.Duration));

        InspectorContent.Children.Add(MakeInspSectionHeader("LEVELS"));
        InspectorContent.Children.Add(MakeAudioLevelMeter(clip));
        AddInspSlider("Gain", clip.Volume * 100, 0,    200, v => clip.Volume = v / 100.0);
        AddInspSlider("Pan",  clip.Pan    * 100, -100, 100, v => clip.Pan    = v / 100.0);

        InspectorContent.Children.Add(MakeInspSectionHeader("ENVELOPE"));
        AddInspSlider("Fade In",  clip.FadeInMs,  0, 5000, v => clip.FadeInMs  = v);
        AddInspSlider("Fade Out", clip.FadeOutMs, 0, 5000, v => clip.FadeOutMs = v);

        InspectorContent.Children.Add(MakeInspSectionHeader("PROCESSING"));
        AddInspSlider("EQ Low",  clip.EqLow,  -12, 12, v => clip.EqLow  = v);
        AddInspSlider("EQ Mid",  clip.EqMid,  -12, 12, v => clip.EqMid  = v);
        AddInspSlider("EQ High", clip.EqHigh, -12, 12, v => clip.EqHigh = v);
    }

    private UIElement MakeAudioLevelMeter(TimelineClip clip)
    {
        const int Segments = 22;
        const double GreenStop = 0.68;
        const double AmberStop = 0.88;

        var greenOn  = new SolidColorBrush(Color.FromArgb(255,  72, 196, 124));
        var amberOn  = new SolidColorBrush(Color.FromArgb(255, 220, 184,  72));
        var redOn    = new SolidColorBrush(Color.FromArgb(255, 224,  88,  88));
        var greenOff = new SolidColorBrush(Color.FromArgb( 48,  72, 196, 124));
        var amberOff = new SolidColorBrush(Color.FromArgb( 48, 220, 184,  72));
        var redOff   = new SolidColorBrush(Color.FromArgb( 48, 224,  88,  88));

        Brush ColorFor(int i, bool on)
        {
            double frac = (i + 1) / (double)Segments;
            if (frac <= GreenStop) return on ? greenOn : greenOff;
            if (frac <= AmberStop) return on ? amberOn : amberOff;
            return on ? redOn : redOff;
        }

        var cellsL = new Rectangle[Segments];
        var cellsR = new Rectangle[Segments];

        Grid BuildRow(string label, Rectangle[] cells)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(12) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
            };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 9.5,
                FontFamily = new FontFamily("JetBrains Mono, Consolas"),
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cellPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            for (int i = 0; i < Segments; i++)
            {
                var cell = new Rectangle
                {
                    Width = 7,
                    Height = 7,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = ColorFor(i, false),
                };
                cells[i] = cell;
                cellPanel.Children.Add(cell);
            }
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(cellPanel, 1);
            grid.Children.Add(lbl);
            grid.Children.Add(cellPanel);
            return grid;
        }

        var dbText = new TextBlock
        {
            Text = "-∞ dBFS",
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var container = new StackPanel
        {
            Padding = new Thickness(14, 4, 14, 8),
            Spacing = 3,
        };
        container.Children.Add(dbText);
        container.Children.Add(BuildRow("L", cellsL));
        container.Children.Add(BuildRow("R", cellsR));

        double levelL = 0, levelR = 0, peakL = 0, peakR = 0, phase = 0;
        var rng = new System.Random();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

        timer.Tick += (_, _) =>
        {
            double target = 0;
            if (_vm?.IsPlaying == true)
            {
                phase += 0.22;
                double envelope = Math.Clamp(clip.Volume, 0, 2);
                double pulse = 0.55 + 0.30 * Math.Sin(phase) + 0.15 * Math.Sin(phase * 2.7 + 1.1);
                double noise = (rng.NextDouble() - 0.5) * 0.12;
                target = Math.Clamp((pulse + noise) * envelope * 0.55, 0, 1);
            }

            double pan = Math.Clamp(clip.Pan, -1, 1);
            double tL = target * (1.0 - Math.Max(0,  pan));
            double tR = target * (1.0 - Math.Max(0, -pan));

            levelL += (tL - levelL) * (tL > levelL ? 0.55 : 0.22);
            levelR += (tR - levelR) * (tR > levelR ? 0.55 : 0.22);

            peakL = Math.Max(peakL * 0.95, levelL);
            peakR = Math.Max(peakR * 0.95, levelR);

            int litL = (int)Math.Round(levelL * Segments);
            int litR = (int)Math.Round(levelR * Segments);
            int pkL  = (int)Math.Round(peakL  * Segments);
            int pkR  = (int)Math.Round(peakR  * Segments);

            for (int i = 0; i < Segments; i++)
            {
                bool onL = i < litL || (peakL > 0.02 && i == pkL - 1);
                bool onR = i < litR || (peakR > 0.02 && i == pkR - 1);
                cellsL[i].Fill = ColorFor(i, onL);
                cellsR[i].Fill = ColorFor(i, onR);
            }

            double peak = Math.Max(peakL, peakR);
            dbText.Text = peak < 0.001
                ? "-∞ dBFS"
                : $"{20 * Math.Log10(peak),6:F1} dBFS";
        };

        container.Loaded   += (_, _) => timer.Start();
        container.Unloaded += (_, _) => timer.Stop();

        return container;
    }

    private void BuildInspectorEffects(TimelineClip clip)
    {
        InspectorContent.Children.Add(MakeInspSectionHeader("APPLIED"));
        if (clip.Effects.Count == 0)
            AddInspField("Effects", "None");
        else
            foreach (var fx in clip.Effects)
                InspectorContent.Children.Add(MakeAppliedEffectRow(clip, fx));

        InspectorContent.Children.Add(MakeInspSectionHeader("AVAILABLE"));
        foreach (var name in AvailableVideoEffects)
            InspectorContent.Children.Add(MakeEffectRow(name, () => AddEffectToSelected(name)));
    }

    private void BuildInspectorColor(TimelineClip clip)
    {
        InspectorContent.Children.Add(MakeInspSectionHeader("BASIC"));
        AddInspSlider("Exposure",   clip.Exposure,   -2,   2,   v => clip.Exposure   = v);
        AddInspSlider("Contrast",   clip.Contrast,   -100, 100, v => clip.Contrast   = v);
        AddInspSlider("Saturation", clip.Saturation, -100, 100, v => clip.Saturation = v);

        InspectorContent.Children.Add(MakeInspSectionHeader("WHITE BALANCE"));
        AddInspSlider("Temperature", clip.Temperature, -100, 100, v => clip.Temperature = v);
        AddInspSlider("Tint",        clip.Tint,        -100, 100, v => clip.Tint        = v);

        InspectorContent.Children.Add(MakeInspSectionHeader("WHEELS"));
        AddInspSlider("Lift",  clip.Lift,      -50, 50, v => clip.Lift      = v);
        AddInspSlider("Gamma", clip.Gamma,     -50, 50, v => clip.Gamma     = v);
        AddInspSlider("Gain",  clip.ColorGain, -50, 50, v => clip.ColorGain = v);
    }

    private static readonly string[] AvailableVideoEffects =
    {
        "Gaussian Blur", "Cross Dissolve", "Chroma Key",
        "Sharpen", "Vignette", "Pixelate", "Glow", "Drop Shadow", "Mirror",
    };

    private void AddEffectToSelected(string effectName)
    {
        if (_vm?.SelectedClip is not { } clip) return;
        if (clip.Effects.Any(fx => fx.Name == effectName)) return; // dedupe
        clip.Effects.Add(new ClipEffect { Name = effectName });
        _vm.Project.IsModified = true;
        UpdateInspector(clip);
    }

    private UIElement MakeEffectRow(string name, Action onAdd)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Padding = new Thickness(14, 5, 14, 5),
        };
        var lbl = new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var addBtn = new Button
        {
            Content = "＋",
            Padding = new Thickness(8, 1, 8, 1),
            FontSize = 11,
            Style = (Style)Application.Current.Resources["GhostButtonStyle"],
        };
        addBtn.Click += (_, _) => onAdd();
        grid.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(addBtn);
        Grid.SetColumn(addBtn, 1);
        return grid;
    }

    private UIElement MakeAppliedEffectRow(TimelineClip clip, ClipEffect fx)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 6), Spacing = 3 };

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var name = new TextBlock
        {
            Text = fx.Name,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var removeBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 0),
            Style = (Style)Application.Current.Resources["GhostButtonStyle"],
        };
        removeBtn.Click += (_, _) =>
        {
            clip.Effects.Remove(fx);
            if (_vm is not null) _vm.Project.IsModified = true;
            UpdateInspector(clip);
        };
        header.Children.Add(name);
        Grid.SetColumn(name, 0);
        header.Children.Add(removeBtn);
        Grid.SetColumn(removeBtn, 1);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value   = fx.Intensity * 100,
            Style   = (Style)Application.Current.Resources["BtapSliderStyle"],
        };
        slider.ValueChanged += (_, ev) => fx.Intensity = ev.NewValue / 100.0;

        panel.Children.Add(header);
        panel.Children.Add(slider);
        return panel;
    }

    private void AddInspField(string label, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(96) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Padding = new Thickness(14, 5, 14, 5),
        };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock
        {
            Text = value,
            FontSize = 10.5,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        grid.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(val);
        Grid.SetColumn(val, 1);

        InspectorContent.Children.Add(grid);
    }

    private void AddInspSlider(string label, double value, double min, double max, Action<double>? onChange = null)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Style = (Style)Application.Current.Resources["BtapSliderStyle"],
        };

        if (onChange is not null)
            slider.ValueChanged += (_, e) => onChange(e.NewValue);

        panel.Children.Add(lbl);
        panel.Children.Add(slider);
        InspectorContent.Children.Add(panel);
    }

    private static UIElement MakeInspSectionHeader(string title)
    {
        return new Border
        {
            Padding = new Thickness(14, 10, 14, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 53, 76, 90)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 10,
                CharacterSpacing = 120,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 91, 114, 128)),
            },
        };
    }

    // ── Title bar actions ─────────────────────────────────────────────────

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Undo();
        Timeline.ViewModel = _vm;
        UpdateInspector(_vm.SelectedClip);
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Redo();
        Timeline.ViewModel = _vm;
        UpdateInspector(_vm.SelectedClip);
    }

    // Scope-wide keyboard accelerators — fire even when focus is in a TextBox / button.
    private void OnUndoAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                   Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        OnUndo(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnRedoAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                   Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        OnRedo(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnSaveAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
                                   Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = SaveProjectAsync();
        args.Handled = true;
    }

    // ── File menu ─────────────────────────────────────────────────────────

    /// <summary>Returns true if the caller may proceed (no unsaved changes, or user chose Save / Discard).</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_vm is null || !_vm.Project.IsModified) return true;

        var dialog = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = new TextBlock
            {
                Text = $"Save changes to \"{_vm.Project.Name}\" before continuing?",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            },
            PrimaryButtonText   = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Primary,
            XamlRoot            = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await SaveProjectAsync();
            // Save was cancelled if IsModified is still true
            return !_vm.Project.IsModified;
        }
        return result == ContentDialogResult.Secondary;  // Discard
    }

    private async void OnFileNew(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        Frame.Navigate(typeof(LandingPage));
    }

    private async void OnFileOpen(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(
            (Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".btap");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var project = ProjectSerializer.Load(file.Path);
            Frame.Navigate(typeof(EditorPage), project);
        }
        catch (Exception ex)
        {
            await ShowDialog("Couldn't open project", ex.Message);
        }
    }

    private void OnFileSave(object sender, RoutedEventArgs e) => _ = SaveProjectAsync();

    private async void OnFileSaveAs(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var original = _vm.Project.FilePath;
        _vm.Project.FilePath = string.Empty;   // force picker
        await SaveProjectAsync();
        // If the user cancelled the picker, restore the original path
        if (string.IsNullOrEmpty(_vm.Project.FilePath))
            _vm.Project.FilePath = original;
    }

    private async void OnFileClose(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        Frame.Navigate(typeof(LandingPage));
    }

    // ── Edit / Clip menu ──────────────────────────────────────────────────

    private void OnEditDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        // Collect (track, clip, index) for every selected clip. Snapshotting before
        // we start mutating avoids index shifts as the first deletions take effect.
        var targets = new List<(Track Track, TimelineClip Clip, int Index)>();
        foreach (var t in _vm.Tracks)
            for (int i = 0; i < t.Clips.Count; i++)
                if (t.Clips[i].IsSelected) targets.Add((t, t.Clips[i], i));

        if (targets.Count == 0) return;

        _vm.SelectedClip = null;
        foreach (var (track, clip, _) in targets)
        {
            var liveIdx = track.Clips.IndexOf(clip);
            if (liveIdx < 0) continue;
            _vm.History.Record(new ClipDeleteAction(track, liveIdx, clip));
        }

        Timeline.Refresh();
        UpdateInspector(null);
    }

    private void OnEditDuplicate(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var targets = new List<(Track Track, TimelineClip Clip)>();
        foreach (var t in _vm.Tracks)
            foreach (var c in t.Clips)
                if (c.IsSelected) targets.Add((t, c));

        if (targets.Count == 0) return;

        foreach (var (track, clip) in targets)
            _vm.History.Record(new ClipDuplicateAction(track, clip));

        Timeline.Refresh();
    }

    private void OnEditSplit(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SplitAtPlayheadCommand.Execute(null);
        Timeline.ViewModel = _vm;
    }

    private void OnClipRippleDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedClip is null) return;
        var track = _vm.Tracks.FirstOrDefault(t => t.Clips.Contains(_vm.SelectedClip));
        if (track is null) return;
        var idx = track.Clips.IndexOf(_vm.SelectedClip);
        var clip = _vm.SelectedClip;
        _vm.SelectedClip = null;
        _vm.History.Record(new ClipRippleDeleteAction(track, idx, clip));
        Timeline.ViewModel = _vm;
        UpdateInspector(null);
    }

    // ── View menu ─────────────────────────────────────────────────────────

    private void OnViewFit(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.TimelineZoom = 1.0;
        ZoomSlider.Value = 100;
        Timeline.SetZoom(1.0);
    }

    private void OnViewToggleLoop(object sender, RoutedEventArgs e) =>
        OnLoop(sender, e);

    private bool _isFullscreen;

    private void OnViewFullscreen(object sender, RoutedEventArgs e)
    {
        _isFullscreen = !_isFullscreen;
        var hidden = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
        LibraryPanel.Visibility   = hidden;
        InspectorPanel.Visibility = hidden;
        MenuBar.Visibility        = hidden;

        if (_isFullscreen)
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(0);
            BodyGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
        else
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(256);
            BodyGrid.ColumnDefinitions[2].Width = new GridLength(296);
        }
    }

    // ── Timeline menu ─────────────────────────────────────────────────────

    private void OnTimelineAddVideoTrack(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var n = _vm.Tracks.Count(t => t.Kind == TrackKind.Video) + 1;
        _vm.Tracks.Insert(0, new Track { Label = $"V{n}", Kind = TrackKind.Video });
        Timeline.ViewModel = _vm;
        _vm.Project.IsModified = true;
        UpdateStatusBar();
    }

    private void OnTimelineAddAudioTrack(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var n = _vm.Tracks.Count(t => t.Kind == TrackKind.Audio) + 1;
        _vm.Tracks.Add(new Track { Label = $"A{n}", Kind = TrackKind.Audio });
        Timeline.ViewModel = _vm;
        _vm.Project.IsModified = true;
        UpdateStatusBar();
    }

    private void OnTimelineClearMarkers(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Project.Markers.Clear();
        _vm.Project.IsModified = true;
        Timeline.ViewModel = _vm;
        UpdateStatusBar();
    }

    // ── Help menu ─────────────────────────────────────────────────────────

    private async void OnHelpShortcuts(object sender, RoutedEventArgs e) =>
        await ShowDialog("Keyboard shortcuts",
            "Space — Play / Pause\n" +
            "J / L — Step back / Play\n" +
            "K — Stop\n" +
            "C — Razor tool      V — Selection      H — Hand\n" +
            "S — Toggle snap     M — Add marker\n" +
            "Delete — Delete clip   Shift+Delete — Ripple delete\n" +
            "Ctrl+Z / Ctrl+Y — Undo / Redo\n" +
            "Ctrl+S — Save     Ctrl+Shift+S — Save As\n" +
            "Ctrl+D — Duplicate clip    Ctrl+B — Split at playhead\n" +
            "Ctrl+N — New     Ctrl+O — Open     Ctrl+E — Export");

    private async void OnHelpAbout(object sender, RoutedEventArgs e) =>
        await ShowDialog("About BTAP",
            "BTAP — Better Than Adobe Premiere\nVersion 0.1 (dev)\nWinUI 3 · .NET 8\nOpen source · GPLv3");

    // ── Share ─────────────────────────────────────────────────────────────

    private async void OnShare(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var path = _vm.Project.FilePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            await ShowDialog("Share", "Save the project first to share its location.");
            return;
        }

        // Reveal in Explorer
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    private Task ShowDialog(string title, string message) =>
        new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        }.ShowAsync().AsTask();

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var picker = new FileSavePicker();
        var hwnd = WindowNative.GetWindowHandle(
            (Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedFileName = _vm.Project.Name;
        picker.FileTypeChoices.Add("MP4 Video", new List<string> { ".mp4" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Stop preview playback and release the source file so the renderer can read it
        StopPlayback();
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Source = null;
            _currentPreviewPath = null;
        }

        // Tear down the Win2D composition-effects layer for the duration of the export.
        // The CompositionEffectBrush shares its DirectX device with the MediaComposition
        // renderer; leaving it active has been observed to silently drop the video stream.
        bool wasEffectsAttached = _previewEffects.IsAttached;
        if (wasEffectsAttached) _previewEffects.Detach();

        using var logger = new ExportLogger(file.Path);

        try
        {
            // Build the composition
            var build = await ExportService.BuildCompositionAsync(_vm.Project, logger);
            if (build.Composition is null)
            {
                await ShowDialog("Nothing to export", build.Error ?? "No clips found.");
                return;
            }

            // Derive the encoding profile from the first source file so the codec matches
            // (avoids the HEVC-MOV → H.264 silent-no-video bug)
            var profile = await ExportService.GetEncodingProfileForProjectAsync(_vm.Project);
            LogProfile(logger, "Output encoding profile", profile);
            await RunExportAsync(build, file, profile, logger);
        }
        finally
        {
            // Re-attach the effects layer and re-apply the current clip's grade
            if (wasEffectsAttached)
            {
                _previewEffects.Attach(ColorGradingLayer);
                _previewEffects.Apply(_presentedClip);
            }
        }
    }

    private static void LogProfile(ExportLogger log, string label, MediaEncodingProfile profile)
    {
        log.Log("");
        log.Log($"== {label} ==");
        if (profile.Container is not null) log.Log($"   Container: {profile.Container.Subtype}");
        if (profile.Video is not null)
            log.Log($"   Video: subtype={profile.Video.Subtype}, {profile.Video.Width}x{profile.Video.Height}, " +
                    $"bitrate={profile.Video.Bitrate}, fps={profile.Video.FrameRate?.Numerator}/{profile.Video.FrameRate?.Denominator}, " +
                    $"profile={profile.Video.ProfileId}");
        else log.Log("   Video: <none>");
        if (profile.Audio is not null)
            log.Log($"   Audio: subtype={profile.Audio.Subtype}, {profile.Audio.SampleRate}Hz, " +
                    $"{profile.Audio.ChannelCount}ch, bitrate={profile.Audio.Bitrate}");
        else log.Log("   Audio: <none>");
    }

    private async Task RunExportAsync(ExportService.BuildResult build, StorageFile destination, MediaEncodingProfile profile, ExportLogger log)
    {
        var composition = build.Composition!;

        // ── Progress dialog ──────────────────────────────────────────────
        var progressBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Width = 380, Height = 6,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
        };
        var statusLabel = new TextBlock
        {
            Text = "Preparing…",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };
        var detailLabel = new TextBlock
        {
            Text = $"{build.VideoClips} video clip(s) · {build.AudioClips} audio clip(s)",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
        };
        var pathLabel = new TextBlock
        {
            Text = destination.Path,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 380,
            Children = { pathLabel, progressBar, statusLabel, detailLabel },
        };

        var dialog = new ContentDialog
        {
            Title = "Exporting",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.None,
        };

        log.Log("");
        log.Log($"Calling RenderToFileAsync(MediaTrimmingPreference.Fast)…");
        var renderStart = DateTime.Now;
        var operation = composition.RenderToFileAsync(destination, MediaTrimmingPreference.Fast, profile);

        double lastLoggedPct = -10;
        operation.Progress = (_, percent) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                progressBar.Value = percent;
                statusLabel.Text = $"Rendering… {percent:F0}%";
            });
            if (percent - lastLoggedPct >= 10)
            {
                log.Log($"   progress: {percent:F1}%");
                lastLoggedPct = percent;
            }
        };

        bool cancelled = false;
        dialog.CloseButtonClick += (_, _) =>
        {
            cancelled = true;
            try { operation.Cancel(); } catch { /* already done */ }
        };

        var dialogTask = dialog.ShowAsync().AsTask();
        var renderTask = operation.AsTask();

        TranscodeFailureReason result = TranscodeFailureReason.None;
        Exception? renderError = null;
        try
        {
            result = await renderTask;
        }
        catch (TaskCanceledException) { cancelled = true; }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { renderError = ex; }

        log.Log($"Render finished in {(DateTime.Now - renderStart).TotalSeconds:F1}s — cancelled={cancelled}, result={result}, exception={renderError?.GetType().Name}");
        if (renderError is not null) log.Log($"Exception details: {renderError.Message}");

        dialog.Hide();
        await dialogTask;

        if (cancelled)
        {
            log.Log("User cancelled — deleting partial file.");
            try { await destination.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            return;
        }

        if (renderError is not null)
        {
            await ShowDialog("Export failed", $"{renderError.Message}\n\nLog: {log.FilePath}");
            return;
        }

        if (result != TranscodeFailureReason.None)
        {
            await ShowDialog("Export failed", $"Renderer returned: {result}\n\nLog: {log.FilePath}");
            return;
        }

        // Inspect the output file
        try
        {
            var outputProfile = await MediaEncodingProfile.CreateFromFileAsync(destination);
            log.Log("");
            log.Log("== OUTPUT file profile (as read back from disk) ==");
            if (outputProfile?.Container is not null) log.Log($"   Container: {outputProfile.Container.Subtype}");
            if (outputProfile?.Video is not null)
                log.Log($"   Video: subtype={outputProfile.Video.Subtype}, " +
                        $"{outputProfile.Video.Width}x{outputProfile.Video.Height}, " +
                        $"bitrate={outputProfile.Video.Bitrate}, " +
                        $"fps={outputProfile.Video.FrameRate?.Numerator}/{outputProfile.Video.FrameRate?.Denominator}");
            else log.Log("   Video: <none>  ← THIS IS THE BUG, NO VIDEO STREAM WAS WRITTEN");
            if (outputProfile?.Audio is not null)
                log.Log($"   Audio: subtype={outputProfile.Audio.Subtype}, " +
                        $"{outputProfile.Audio.SampleRate}Hz, {outputProfile.Audio.ChannelCount}ch");
            var props = await destination.GetBasicPropertiesAsync();
            log.Log($"   File size: {props.Size:N0} bytes");
        }
        catch (Exception inspectEx)
        {
            log.Log($"Couldn't inspect output file: {inspectEx.GetType().Name}: {inspectEx.Message}");
        }

        // Sanity-check: did the output actually contain a video stream?
        if (!await ExportService.OutputHasVideoAsync(destination))
        {
            await ShowDialog(
                "Export missing video",
                "The renderer wrote the file but it doesn't contain a video stream. " +
                "This usually means the source codec couldn't be transcoded — most " +
                "commonly an iPhone HEVC .MOV without the HEVC Video Extensions " +
                "installed.\n\n" +
                "Try:\n" +
                "  • Install \"HEVC Video Extensions\" from the Microsoft Store, or\n" +
                "  • Set iPhone Camera → Formats → \"Most Compatible\" (records as H.264), or\n" +
                "  • Convert the source to .mp4 first.\n\n" +
                $"A detailed log was written to:\n{log.FilePath}");
            return;
        }

        await ShowExportCompleteDialog(destination, build, log.FilePath);
    }

    private async Task ShowExportCompleteDialog(StorageFile file, ExportService.BuildResult build, string? logPath = null)
    {
        var props = await file.GetBasicPropertiesAsync();
        var sizeMb = props.Size / (1024.0 * 1024.0);

        var lines = new StackPanel { Spacing = 6 };
        lines.Children.Add(new TextBlock { Text = "Saved to:", FontSize = 11.5 });
        lines.Children.Add(new TextBlock
        {
            Text = file.Path,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        });
        lines.Children.Add(new TextBlock
        {
            Text = $"{sizeMb:F1} MB",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            Margin = new Thickness(0, 4, 0, 0),
        });

        if (build.Warnings.Count > 0)
        {
            lines.Children.Add(new TextBlock
            {
                Text = "Warnings:",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 160, 60)),
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var w in build.Warnings.Take(5))
            {
                lines.Children.Add(new TextBlock
                {
                    Text = "• " + w,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
                });
            }
        }

        if (!string.IsNullOrEmpty(logPath))
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"Diagnostic log: {logPath}",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Export complete",
            Content = lines,
            PrimaryButtonText = "Show in Explorer",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var r = await dialog.ShowAsync();
        if (r == ContentDialogResult.Primary)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{file.Path}\"",
                UseShellExecute = true,
            });
        }
    }

    private async Task SaveProjectAsync()
    {
        if (_vm is null) return;

        string? path = _vm.Project.FilePath;

        if (string.IsNullOrEmpty(path))
        {
            var picker = new FileSavePicker();
            var hwnd = WindowNative.GetWindowHandle(
                (Application.Current as App)!.GetMainWindow());
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedFileName = _vm.Project.Name;
            picker.FileTypeChoices.Add("BTAP Project", [".btap"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            path = file.Path;
        }

        ProjectSerializer.Save(_vm.Project, path);
        var dur = $"{(int)_vm.Project.Duration.TotalMinutes:D2}:{_vm.Project.Duration.Seconds:D2}";
        var spec = $"{_vm.Project.Height}p · {_vm.Project.FrameRate:G3}fps";
        ProjectSerializer.AddRecent(_vm.Project.Name, path, dur, spec);

        _lastSaved = DateTime.Now;
        TbFilename.Text = _vm.Project.Name;
        UpdateStatusBar();
    }

    // ── Status bar ────────────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        if (_vm is null) return;
        var p = _vm.Project;
        TbStatusDuration.Text   = $"{_vm.DurationLabel} sequence";
        TbStatusResolution.Text = $"{p.Width}×{p.Height}";
        TbStatusMedia.Text      = $" · {p.MediaBin.Count} media";
        TbTrackCount.Text       = $"{p.Tracks.Count} tracks";
        TbBinLabel.Text         = $"Bin · {p.Name}";

        if (p.IsModified)
        {
            TbStatusSaved.Text = "Modified · unsaved";
            TbStatusSaved.Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 160, 60));
            TbSavedHint.Text = " · unsaved changes";
        }
        else
        {
            var elapsed = DateTime.Now - _lastSaved;
            TbStatusSaved.Text = elapsed.TotalSeconds < 60
                ? $"Saved · {(int)elapsed.TotalSeconds}s ago"
                : $"Saved · {(int)elapsed.TotalMinutes}m ago";
            TbStatusSaved.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            TbSavedHint.Text = " · auto-saved";
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        var window = (Application.Current as App)?.GetMainWindow();
        window?.Minimize();
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        var window = (Application.Current as App)?.GetMainWindow();
        window?.ToggleMaximize();
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        Frame.Navigate(typeof(LandingPage));
    }

    // ── Tab state helpers ─────────────────────────────────────────────────

    private void SetActiveModeBtn(Button btn)
    {
        if (_activeModeBtn is not null)
        {
            _activeModeBtn.Background = null;
            _activeModeBtn.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        }
        _activeModeBtn = btn;
        btn.Background = (Brush)Application.Current.Resources["BgElevatedBrush"];
        btn.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void SetActiveLibBtn(Button btn)
    {
        if (_activeLibBtn is not null)
        {
            _activeLibBtn.BorderThickness = new Thickness(0);
            _activeLibBtn.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        }
        _activeLibBtn = btn;
        btn.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
        btn.BorderThickness = new Thickness(0, 0, 0, 2);
        btn.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void SetActiveInspBtn(Button btn)
    {
        if (_activeInspBtn is not null)
        {
            _activeInspBtn.Background = null;
            _activeInspBtn.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        }
        _activeInspBtn = btn;
        btn.Background = (Brush)Application.Current.Resources["BgElevatedBrush"];
        btn.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void SetActiveToolBtn(Button btn)
    {
        if (_activeToolBtn is not null)
            _activeToolBtn.Background = null;
        _activeToolBtn = btn;
        btn.Background = (Brush)Application.Current.Resources["BgElevatedBrush"];
    }
}
