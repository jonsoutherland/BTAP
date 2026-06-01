using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly Stopwatch _playClock = new();
    private TimeSpan _lastTickElapsed;
    private DispatcherTimer? _statusTimer;
    private DateTime _lastSaved = DateTime.Now;
    private readonly PreviewEffectsService _previewEffects = new();
    private readonly KeyBindingsService _keyBindings = new();
    private Border? _eyedropOverlay;
    private Action<Color>? _eyedropCallback;

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

        // Canvas is derived from the first imported video's native size. When that
        // changes (first video added, last video removed) the preview viewport's
        // aspect must re-fit and the export-window overlay must reposition.
        _vm.Project.MediaBin.CollectionChanged += OnMediaBinChangedForCanvas;

        // Build the page-level KeyboardAccelerators from the user's saved bindings
        // and keep them in sync when the customizer changes anything.
        _keyBindings.Changed -= OnKeyBindingsChanged;
        _keyBindings.Changed += OnKeyBindingsChanged;
        RebuildKeyboardAccelerators();

        // Take keyboard focus so Space / J / K / etc. work immediately
        Loaded += OnPageLoaded;
    }

    private void OnMediaBinChangedForCanvas(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // GetCanvasSize() walks MediaBin and returns the first video's native size,
        // so any add/remove can flip the canvas aspect. Re-fit the viewport, refresh
        // overlays and the inspector (PosX/Y slider range is canvas-bound).
        UpdatePreviewCanvasSize();
        UpdateTransformHandles();
        if (_titleClipAtPlayhead is not null)
            ApplyTitlePosition(_titleClipAtPlayhead);
        if (_vm?.SelectedClip is not null)
            UpdateInspector(_vm.SelectedClip);
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
            // doesn't linger over the placeholder. Sync the compositor too: it
            // caches each layer's last frame and would otherwise keep drawing the
            // deleted clip's frame on top of the transparent clear.
            ClearPreview();
            VideoCompositor.Sync(_vm.Project.Playhead);
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
        {
            _vm.History.Changed -= OnHistoryChangedForPreview;
            _vm.Project.MediaBin.CollectionChanged -= OnMediaBinChangedForCanvas;
        }
        _keyBindings.Changed -= OnKeyBindingsChanged;
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
        // All shortcut handling now flows through KeyboardAccelerators built
        // from _keyBindings (see RebuildKeyboardAccelerators / InvokeCommand).
        // Kept hooked up only so future non-binding key behaviors have a home.
        if (_inCropMode && (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Enter))
        {
            ExitCropMode();
            e.Handled = true;
            return;
        }

        if (_eyedropOverlay is not null && e.Key == VirtualKey.Escape)
        {
            EndEyedrop();
            e.Handled = true;
        }
    }

    // ── Dynamic shortcut machinery ───────────────────────────────────────

    private void OnKeyBindingsChanged(object? sender, EventArgs e) =>
        RebuildKeyboardAccelerators();

    private void RebuildKeyboardAccelerators()
    {
        KeyboardAccelerators.Clear();
        foreach (var binding in _keyBindings.Bindings)
        {
            var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key       = binding.Key,
                Modifiers = binding.Modifiers,
            };
            var cmd = binding.Command;
            acc.Invoked += (_, args) =>
            {
                if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
                InvokeCommand(cmd);
                args.Handled = true;
            };
            KeyboardAccelerators.Add(acc);
        }
    }

    private void InvokeCommand(EditorCommand cmd)
    {
        if (_vm is null) return;
        switch (cmd)
        {
            case EditorCommand.PlayPause:        TogglePlayback(); break;
            case EditorCommand.StepBack:         OnStepBack(this, new RoutedEventArgs()); break;
            case EditorCommand.StepForward:      OnStepFwd(this, new RoutedEventArgs()); break;
            case EditorCommand.Stop:             StopPlayback(); break;

            case EditorCommand.Undo:             OnUndo(this, new RoutedEventArgs()); break;
            case EditorCommand.Redo:             OnRedo(this, new RoutedEventArgs()); break;
            case EditorCommand.Save:             _ = SaveProjectAsync(); break;
            case EditorCommand.NewProject:       OnFileNew(this, new RoutedEventArgs()); break;
            case EditorCommand.OpenProject:      OnFileOpen(this, new RoutedEventArgs()); break;
            case EditorCommand.Export:           OnExport(this, new RoutedEventArgs()); break;

            case EditorCommand.DeleteClip:       OnEditDelete(this, new RoutedEventArgs()); break;
            case EditorCommand.RippleDelete:     OnClipRippleDelete(this, new RoutedEventArgs()); break;
            case EditorCommand.DuplicateClip:    OnEditDuplicate(this, new RoutedEventArgs()); break;
            case EditorCommand.SplitAtPlayhead:  OnEditSplit(this, new RoutedEventArgs()); break;

            case EditorCommand.AddMarker:        _vm.AddMarkerCommand.Execute(null); break;
            case EditorCommand.ToggleSnap:       OnToggleSnap(this, new RoutedEventArgs()); break;
            case EditorCommand.Fullscreen:       OnViewFullscreen(this, new RoutedEventArgs()); break;

            case EditorCommand.ToolCursor:
                SetActiveToolBtn(BtnToolCursor); _vm.ActiveTool = ActiveTool.Cursor; break;
            case EditorCommand.ToolRazor:
                SetActiveToolBtn(BtnToolRazor);  _vm.ActiveTool = ActiveTool.Razor;  break;
            case EditorCommand.ToolHand:
                SetActiveToolBtn(BtnToolHand);   _vm.ActiveTool = ActiveTool.Hand;   break;

            case EditorCommand.MoveClipUp:
                if (Timeline.MoveSelectedClipsByTrack(-1) > 0) _vm.Project.IsModified = true; break;
            case EditorCommand.MoveClipDown:
                if (Timeline.MoveSelectedClipsByTrack(1) > 0)  _vm.Project.IsModified = true; break;

            case EditorCommand.AddTitleAtPlayhead: AddTitleAtPlayheadAndEdit(); break;
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
        // Drive the playhead off wall-clock elapsed time between ticks rather than
        // a fixed 1/FrameRate increment — DispatcherTimer can fire late and never
        // makes up the lost time, so a count-the-ticks approach drifts behind
        // MediaPlayer (which plays in real wall-clock time).
        _lastTickElapsed = TimeSpan.Zero;
        _playClock.Restart();
        _playbackTimer.Start();
    }

    private void StopPlayback()
    {
        if (_vm is null) return;
        _vm.IsPlaying = false;
        PlaybackBar.SetIsPlaying(false);
        _playbackTimer?.Stop();
        _playClock.Stop();
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

    private TimelineClip? FindTitleClipAt(TimeSpan position)
    {
        if (_vm is null) return null;
        foreach (var t in _vm.Tracks)
        {
            if (t.Kind != TrackKind.Title) continue;
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
        // Switching clips while cropping would leave the overlay attached to a
        // clip that's no longer on-screen — commit the current values and exit.
        if (_inCropMode) ExitCropMode();
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

    // ── Title overlay (independent of _presentedClip so it can layer over video) ──

    /// <summary>The title clip currently covering the playhead. Drives TitleClipOverlay
    /// and is the target of inline-edit mode. Independent of <see cref="_presentedClip"/>
    /// so a title can render on top of a video that's also playing.</summary>
    private TimelineClip? _titleClipAtPlayhead;

    private void SetTitleClipAtPlayhead(TimelineClip? clip)
    {
        if (ReferenceEquals(_titleClipAtPlayhead, clip)) return;
        // Switching titles while editing would otherwise commit the in-progress text
        // into the old clip and then strand the editor on top of the new one.
        ExitTitleEditMode(commit: true);
        if (_titleClipAtPlayhead is not null)
            _titleClipAtPlayhead.PropertyChanged -= OnTitleClipPropertyChanged;
        _titleClipAtPlayhead = clip;
        if (_titleClipAtPlayhead is not null)
            _titleClipAtPlayhead.PropertyChanged += OnTitleClipPropertyChanged;
        ApplyTitleOverlay();
    }

    private void OnTitleClipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineClip.Label)
                           or nameof(TimelineClip.PosX)
                           or nameof(TimelineClip.PosY)
                           or nameof(TimelineClip.Scale)
                           or nameof(TimelineClip.Rotation)
                           or nameof(TimelineClip.FontFamily)
                           or nameof(TimelineClip.FontSize)
                           or nameof(TimelineClip.IsBold)
                           or nameof(TimelineClip.IsItalic)
                           or nameof(TimelineClip.IsUnderline)
                           or nameof(TimelineClip.TextColor)
                           or nameof(TimelineClip.TextAlign))
        {
            ApplyTitleOverlay();
        }
    }

    private void ApplyTitleOverlay()
    {
        var clip = _titleClipAtPlayhead;
        if (clip is null)
        {
            ExitTitleEditMode(commit: true);
            TitleClipOverlay.Visibility = Visibility.Collapsed;
            TitleClipBorder.Visibility  = Visibility.Collapsed;
            return;
        }
        ApplyTitleTextStyle(clip);
        TitleClipText.Text          = clip.Label;
        ApplyTitlePosition(clip);
        TitleClipOverlay.Visibility = Visibility.Visible;
        UpdateTitleSelectionAffordance();
        if (!_isEditingTitle)
            TitleClipEditor.Visibility = Visibility.Collapsed;
    }

    /// <summary>Mirror the TransformBox affordance for title clips: the dashed accent
    /// border around the text shows only when the title is the selected/presented clip.</summary>
    private void UpdateTitleSelectionAffordance()
    {
        bool selected = _titleClipAtPlayhead is not null
                     && ReferenceEquals(_presentedClip, _titleClipAtPlayhead);
        TitleClipBorder.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleHandles();
    }

    /// <summary>Position the 4 corner + 1 rotation handle around the rendered title text.
    /// Mirrors UpdateTransformHandles' role for video clips. Handles sit in viewport-pixel
    /// coordinates inside TitleHandles (a Canvas filling TitleClipOverlay), so they stay a
    /// constant visual size regardless of the title's own scale/rotation.</summary>
    private void UpdateTitleHandles()
    {
        bool selected = _titleClipAtPlayhead is not null
                     && ReferenceEquals(_presentedClip, _titleClipAtPlayhead);
        if (!selected || _isEditingTitle || _vm is null
            || TitleClipPositioner.ActualWidth < 4 || TitleClipPositioner.ActualHeight < 4)
        {
            TitleHandles.Visibility = Visibility.Collapsed;
            return;
        }

        var clip = _titleClipAtPlayhead!;

        double scale = Math.Clamp(clip.Scale, 0.1, 10);
        double w = TitleClipPositioner.ActualWidth  * scale;
        double h = TitleClipPositioner.ActualHeight * scale;

        // Title's center in viewport coords: middle of the viewport + PosX/Y mapped to viewport pixels.
        // PosX/PosY are in canvas-pixel units (the preview's working area), NOT the
        // export resolution — see Project.GetCanvasSize().
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        double sx = PreviewViewport.Width  / Math.Max(1, canvasW);
        double sy = PreviewViewport.Height / Math.Max(1, canvasH);
        double cx = PreviewViewport.Width  / 2 + clip.PosX * sx;
        double cy = PreviewViewport.Height / 2 + clip.PosY * sy;

        double x = cx - w / 2;
        double y = cy - h / 2;

        // Box rotation is not reflected in handle positions (matches video clip behavior:
        // the dashed box stays axis-aligned even when the underlying clip is rotated).
        TitleHandles.Visibility = Visibility.Visible;

        PlaceHandle(TitleHandleTL, x,       y);
        PlaceHandle(TitleHandleTR, x + w,   y);
        PlaceHandle(TitleHandleBL, x,       y + h);
        PlaceHandle(TitleHandleBR, x + w,   y + h);

        double rotateHandleY = y - 28;
        PlaceHandle(TitleHandleRotate, cx, rotateHandleY);
        TitleRotateLine.X1 = TitleRotateLine.X2 = cx;
        TitleRotateLine.Y1 = y;
        TitleRotateLine.Y2 = rotateHandleY;

        _currentBoxCenter = new Windows.Foundation.Point(cx, cy);
    }

    /// <summary>Center of the currently-active transform bounding box, in viewport-pixel
    /// coordinates. Set by UpdateTransformHandles (video) and UpdateTitleHandles (title);
    /// read by OnTransformHandlePressed so the scale/rotate math works for both.</summary>
    private Windows.Foundation.Point _currentBoxCenter;

    /// <summary>Recompute which title clip is at the playhead and refresh the overlay.</summary>
    private void RefreshTitleOverlay()
    {
        if (_vm is null) { SetTitleClipAtPlayhead(null); return; }
        SetTitleClipAtPlayhead(FindTitleClipAt(_vm.Project.Playhead));
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
                           or nameof(TimelineClip.FlipY)
                           or nameof(TimelineClip.FontFamily)
                           or nameof(TimelineClip.FontSize)
                           or nameof(TimelineClip.IsBold)
                           or nameof(TimelineClip.IsItalic)
                           or nameof(TimelineClip.IsUnderline)
                           or nameof(TimelineClip.TextColor)
                           or nameof(TimelineClip.TextAlign))
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
            TransformOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Title overlay rendering is independent of _presentedClip; it always reflects
        // the title clip at the playhead. Transform handles only make sense for video
        // clips so we hide them when a title is the selected/presented clip.
        if (clip.Kind == ClipKind.Title)
            TransformOverlay.Visibility = Visibility.Collapsed;

        // The title's own selection-border affordance depends on _presentedClip.
        UpdateTitleSelectionAffordance();

        ApplyClipSpeed(clip);
        UpdateTransformHandles();
    }

    // ── Title text styling & inline editing ──────────────────────────────

    private bool _isEditingTitle;

    private void ApplyTitleTextStyle(TimelineClip clip)
    {
        try { TitleClipText.FontFamily   = new FontFamily(clip.FontFamily); } catch { }
        try { TitleClipEditor.FontFamily = new FontFamily(clip.FontFamily); } catch { }
        var size = Math.Max(6, clip.FontSize);
        TitleClipText.FontSize   = size;
        TitleClipEditor.FontSize = size;

        TitleClipText.FontWeight   = clip.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        TitleClipEditor.FontWeight = clip.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

        var style = clip.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        TitleClipText.FontStyle   = style;
        TitleClipEditor.FontStyle = style;

        TitleClipText.TextDecorations = clip.IsUnderline
            ? Windows.UI.Text.TextDecorations.Underline
            : Windows.UI.Text.TextDecorations.None;

        var brush = ParseColorBrush(clip.TextColor) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        TitleClipText.Foreground   = brush;
        TitleClipEditor.Foreground = brush;

        var align = clip.TextAlign switch
        {
            "Left"  => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _       => TextAlignment.Center,
        };
        TitleClipText.TextAlignment   = align;
        TitleClipEditor.TextAlignment = align;
    }

    private static SolidColorBrush? ParseColorBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return null;
        if (!byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)) return null;
        if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return null;
        if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return null;
        if (!byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private void OnTitleOverlayDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_titleClipAtPlayhead is null) return;
        EnterTitleEditMode();
        e.Handled = true;
    }

    private void EnterTitleEditMode()
    {
        if (_titleClipAtPlayhead is null) return;
        _isEditingTitle = true;
        TitleClipEditor.Text       = _titleClipAtPlayhead.Label;
        TitleClipText.Visibility   = Visibility.Collapsed;
        TitleClipEditor.Visibility = Visibility.Visible;
        TitleClipEditor.Focus(FocusState.Programmatic);
        TitleClipEditor.SelectAll();
        UpdateTitleHandles();
    }

    private void ExitTitleEditMode(bool commit)
    {
        if (!_isEditingTitle) return;
        _isEditingTitle = false;
        if (commit && _titleClipAtPlayhead is not null)
        {
            var newText = TitleClipEditor.Text ?? string.Empty;
            if (newText != _titleClipAtPlayhead.Label)
            {
                _titleClipAtPlayhead.Label = newText;
                if (_vm is not null) _vm.Project.IsModified = true;
                UpdateClipHeader(_titleClipAtPlayhead);
                Timeline.Refresh();
            }
        }
        TitleClipEditor.Visibility = Visibility.Collapsed;
        TitleClipText.Visibility   = Visibility.Visible;
        UpdateTitleHandles();
    }

    private void OnTitleEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            ExitTitleEditMode(commit: false);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && !InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            // Plain Enter commits; Shift+Enter inserts a newline (AcceptsReturn).
            ExitTitleEditMode(commit: true);
            e.Handled = true;
        }
    }

    private void OnTitleEditorLostFocus(object sender, RoutedEventArgs e) =>
        ExitTitleEditMode(commit: true);

    // ── Title drag-to-position in preview ───────────────────────────────

    /// <summary>Map clip.PosX/PosY (project pixels) → preview-viewport pixels and apply
    /// scale + rotation. The CompositeTransform's origin is set to (0.5, 0.5) in XAML so
    /// scale/rotation pivot around the title's own center.</summary>
    private void ApplyTitlePosition(TimelineClip clip)
    {
        if (_vm is null)
        {
            TitleClipTransform.TranslateX = 0;
            TitleClipTransform.TranslateY = 0;
            TitleClipTransform.ScaleX = 1;
            TitleClipTransform.ScaleY = 1;
            TitleClipTransform.Rotation = 0;
            return;
        }
        // PosX/PosY are in canvas-pixel units; the viewport is sized to canvas aspect.
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        double sx = PreviewViewport.Width  / Math.Max(1, canvasW);
        double sy = PreviewViewport.Height / Math.Max(1, canvasH);
        TitleClipTransform.TranslateX = clip.PosX * sx;
        TitleClipTransform.TranslateY = clip.PosY * sy;
        double s = Math.Clamp(clip.Scale, 0.1, 10);
        TitleClipTransform.ScaleX   = s;
        TitleClipTransform.ScaleY   = s;
        TitleClipTransform.Rotation = clip.Rotation;
        UpdateTitleHandles();
    }

    private bool _isDraggingTitle;
    private Windows.Foundation.Point _titleDragStartPointer;
    private double _titleDragStartPosX;
    private double _titleDragStartPosY;

    private void OnTitlePositionerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditingTitle) return;
        if (_titleClipAtPlayhead is null) return;
        var fe = (FrameworkElement)sender;
        _titleDragStartPointer = e.GetCurrentPoint(PreviewViewport).Position;
        _titleDragStartPosX    = _titleClipAtPlayhead.PosX;
        _titleDragStartPosY    = _titleClipAtPlayhead.PosY;
        _isDraggingTitle       = true;
        fe.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnTitlePositionerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTitle || _vm is null || _titleClipAtPlayhead is null) return;
        var pt = e.GetCurrentPoint(PreviewViewport).Position;
        double dxViewport = pt.X - _titleDragStartPointer.X;
        double dyViewport = pt.Y - _titleDragStartPointer.Y;
        // Convert viewport-pixel delta back to canvas-pixel units (PosX/PosY space).
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        double sx = PreviewViewport.Width  > 0 ? canvasW / PreviewViewport.Width  : 1.0;
        double sy = PreviewViewport.Height > 0 ? canvasH / PreviewViewport.Height : 1.0;
        double newX = _titleDragStartPosX + dxViewport * sx;
        double newY = _titleDragStartPosY + dyViewport * sy;

        SnapGuide guideX = SnapGuide.None;
        SnapGuide guideY = SnapGuide.None;

        if (IsCtrlHeld())
        {
            // Snap targets in canvas pixels (same scheme as video clips). Title's
            // half-extents come from its rendered size × Scale, converted to canvas px.
            double titleScale = Math.Clamp(_titleClipAtPlayhead.Scale, 0.1, 10);
            double halfW  = TitleClipPositioner.ActualWidth  * titleScale * sx / 2.0;
            double halfH  = TitleClipPositioner.ActualHeight * titleScale * sy / 2.0;
            double vHalfW = canvasW / 2.0;
            double vHalfH = canvasH / 2.0;

            // 8 viewport-pixel snap radius expressed in project px so the feel is
            // independent of preview-area size or project resolution.
            double snapPx = 8.0;
            double thresholdX = snapPx * sx;
            double thresholdY = snapPx * sy;

            (newX, int ix) = SnapTo(newX, new[]
            {
                0.0,             // 0: title center on vertical centerline
                -halfW,          // 1: title right edge on vertical centerline
                 halfW,          // 2: title left edge on vertical centerline
                 vHalfW - halfW, // 3: title right edge on viewport right
                -vHalfW + halfW, // 4: title left edge on viewport left
            }, thresholdX);
            guideX = ix switch
            {
                0 or 1 or 2 => SnapGuide.Center,
                3           => SnapGuide.Far,
                4           => SnapGuide.Near,
                _           => SnapGuide.None,
            };

            (newY, int iy) = SnapTo(newY, new[]
            {
                0.0,
                -halfH,
                 halfH,
                 vHalfH - halfH,
                -vHalfH + halfH,
            }, thresholdY);
            guideY = iy switch
            {
                0 or 1 or 2 => SnapGuide.Center,
                3           => SnapGuide.Far,
                4           => SnapGuide.Near,
                _           => SnapGuide.None,
            };
        }

        UpdateSnapGuides(guideX, guideY);

        _titleClipAtPlayhead.PosX = newX;
        _titleClipAtPlayhead.PosY = newY;
        _vm.Project.IsModified = true;
        e.Handled = true;
    }

    private void OnTitlePositionerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTitle) return;
        _isDraggingTitle = false;
        ((FrameworkElement)sender).ReleasePointerCapture(e.Pointer);
        HideSnapGuides();
        e.Handled = true;
    }

    private void OnTitlePositionerPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingTitle = false;
        HideSnapGuides();
    }

    /// <summary>Re-position the title transform handles once the positioner's measured
    /// size settles (text content or font changes reflow the layout asynchronously).</summary>
    private void OnTitlePositionerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleHandles();
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
        if (_vm is null) { SetPresentedClip(null); SetTitleClipAtPlayhead(null); return; }

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
        // The title overlay is independent — it always reflects the title at the
        // playhead, even when a video clip is presented.
        SetTitleClipAtPlayhead(FindTitleClipAt(_vm.Project.Playhead));
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
        if (_inCropMode)
        {
            TransformOverlay.Visibility = Visibility.Collapsed;
            UpdateCropOverlay();
            return;
        }
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

        // Preview is laid out at canvas aspect, not at the export's W×H — the
        // export size only controls the dashed crop overlay inside the canvas.
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        int outW = Math.Max(1, canvasW);
        int outH = Math.Max(1, canvasH);
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

        // Mirror the compositor's crop-shrink so handles bound the visible content.
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double fullW = frW * scale;
        double fullH = frH * scale;
        double offX  = clip.PosX / (double)outW * frW;
        double offY  = clip.PosY / (double)outH * frH;
        double fullX = frX + (frW - fullW) / 2 + offX;
        double fullY = frY + (frH - fullH) / 2 + offY;
        double cl = Math.Clamp(clip.CropLeft,   0, 0.95);
        double ct = Math.Clamp(clip.CropTop,    0, 0.95);
        double cr = Math.Clamp(clip.CropRight,  0, 0.95);
        double cb = Math.Clamp(clip.CropBottom, 0, 0.95);
        double dstX = fullX + cl * fullW;
        double dstY = fullY + ct * fullH;
        double dstW = Math.Max(1, (1 - cl - cr) * fullW);
        double dstH = Math.Max(1, (1 - ct - cb) * fullH);

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

        _currentBoxCenter = new Windows.Foundation.Point(cx, y + h / 2);
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

        // Fit the viewport to the canvas aspect (not the export resolution) so the
        // preview shows the working canvas. The export crop overlay lives inside it.
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        double projectAspect = (double)canvasW / canvasH;
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
        UpdateExportWindowOverlay();
    }

    /// <summary>
    /// Position the dashed export-window indicator inside the preview viewport.
    /// Visible only when the canvas and export aspects differ — when they match,
    /// the export covers the full canvas and the overlay would be redundant.
    /// </summary>
    private void UpdateExportWindowOverlay()
    {
        if (_vm is null
            || PreviewViewport.Width  <= 0
            || PreviewViewport.Height <= 0)
        {
            ExportWindowOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        if (canvasW <= 0 || canvasH <= 0)
        {
            ExportWindowOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double canvasAspect = (double)canvasW / canvasH;
        double exportAspect = (double)_vm.Project.Width / Math.Max(1, _vm.Project.Height);
        if (Math.Abs(canvasAspect - exportAspect) < 0.001)
        {
            // Canvas already matches the export aspect — no crop region to mark.
            ExportWindowOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var (_, _, winW, winH) = _vm.Project.GetExportWindow();
        // Convert canvas-pixel dimensions to viewport-pixel dimensions.
        double scale = PreviewViewport.Width / canvasW;
        ExportWindowOverlay.Width  = winW * scale;
        ExportWindowOverlay.Height = winH * scale;
        ExportWindowOverlay.Visibility = Visibility.Visible;
    }

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewCanvasSize();
        // Title position is in project pixels — re-map after the viewport changes size
        // so the text doesn't drift relative to the underlying video frame.
        if (_titleClipAtPlayhead is not null)
            ApplyTitlePosition(_titleClipAtPlayhead);
        if (_inCropMode) UpdateCropOverlay();
    }

    // ── Right-click context menu on the preview ──────────────────────────

    private void OnPreviewContextOpening(object sender, object e)
    {
        // Enable items only when a real (non-title) clip is presented
        bool hasClip = _presentedClip is not null && _presentedClip.Kind != ClipKind.Title;
        foreach (var item in PreviewContextMenu.Items)
            if (item is Control c) c.IsEnabled = hasClip;

        // Crop entry toggles label when already in crop mode so the same menu
        // serves to enter and to commit/exit.
        if (CropMenuItem is not null)
            CropMenuItem.Text = _inCropMode ? "Done cropping" : "Crop";
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
            // Snap targets in canvas pixels (PosX/Y units). Viewport half-extents are
            // simply the canvas resolution / 2; scaled clip half-extents are scale * that.
            var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
            double scale = _presentedClip.Scale;
            double halfW  = scale * canvasW  / 2.0;
            double halfH  = scale * canvasH / 2.0;
            double vHalfW = canvasW / 2.0;
            double vHalfH = canvasH / 2.0;

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
        // Guide lines span the viewport. PreviewPlayer is permanently Collapsed (zero size),
        // so use PreviewViewport which always reflects the visible canvas.
        double w = PreviewViewport.ActualWidth;
        double h = PreviewViewport.ActualHeight;

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
        // Pointer-to-clip math runs in canvas-pixel space, same as PosX/PosY.
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        int outW = Math.Max(1, canvasW);
        int outH = Math.Max(1, canvasH);
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

        // Center of the bounding box in viewport coords (matches TransformOverlay's coord
        // space — both Canvas elements stretch to fill PreviewViewport, so handles from
        // either video TransformOverlay or TitleHandles use the same coord system).
        _gestureBoxCenter = _currentBoxCenter;

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

    // ── Crop mode (right-click → Crop) ────────────────────────────────────

    private enum CropGesture { None, Move, L, R, T, B, TL, TR, BL, BR }
    private CropGesture _cropGesture;
    private bool _inCropMode;
    private TimelineClip? _croppingClip;
    private Windows.Foundation.Point _cropGestureStart;
    private double _cropStartLeft, _cropStartTop, _cropStartRight, _cropStartBottom;
    private Windows.Foundation.Rect _cropDestRect;

    private void OnPreviewEnterCropMode(object sender, RoutedEventArgs e)
    {
        if (_inCropMode) { ExitCropMode(); return; }
        if (_presentedClip is null || _presentedClip.Kind == ClipKind.Title) return;
        EnterCropMode(_presentedClip);
    }

    private void EnterCropMode(TimelineClip clip)
    {
        _inCropMode = true;
        _croppingClip = clip;
        VideoCompositor.BypassCropClipId = clip.Id;
        TransformOverlay.Visibility = Visibility.Collapsed;
        CropOverlay.Visibility = Visibility.Visible;
        if (CropMenuItem is not null) CropMenuItem.Text = "Done cropping";
        UpdateCropOverlay();
    }

    private void ExitCropMode()
    {
        if (!_inCropMode) return;
        _inCropMode = false;
        _croppingClip = null;
        VideoCompositor.BypassCropClipId = null;
        CropOverlay.Visibility = Visibility.Collapsed;
        if (CropMenuItem is not null) CropMenuItem.Text = "Crop";
        UpdateTransformHandles();
        if (_vm is not null) _vm.Project.IsModified = true;
    }

    private void UpdateCropOverlay()
    {
        if (!_inCropMode || _croppingClip is null || _vm is null)
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var clip = _croppingClip;
        double cw = VideoCompositor.ActualWidth;
        double ch = VideoCompositor.ActualHeight;
        if (cw <= 0 || ch <= 0) { CropOverlay.Visibility = Visibility.Collapsed; return; }

        // Replicate the compositor's letterbox math (matches UpdateTransformHandles).
        var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
        int outW = Math.Max(1, canvasW);
        int outH = Math.Max(1, canvasH);
        double outAspect = (double)outW / outH;
        double cAspect = cw / ch;
        double frX, frY, frW, frH;
        if (cAspect > outAspect)
        {
            frH = ch; frW = frH * outAspect;
            frX = (cw - frW) / 2; frY = 0;
        }
        else
        {
            frW = cw; frH = frW / outAspect;
            frX = 0; frY = (ch - frH) / 2;
        }

        // BypassCropClipId makes the compositor draw the FULL source frame into
        // the dest rect, so the dest rect maps 1:1 to the un-cropped source.
        double scale = Math.Clamp(clip.Scale, 0.05, 10);
        double dstW = frW * scale;
        double dstH = frH * scale;
        double offX = clip.PosX / (double)outW * frW;
        double offY = clip.PosY / (double)outH * frH;
        double dstX = frX + (frW - dstW) / 2 + offX;
        double dstY = frY + (frH - dstH) / 2 + offY;

        Windows.Foundation.Point tl, br;
        try
        {
            var ge = VideoCompositor.TransformToVisual(CropOverlay);
            tl = ge.TransformPoint(new Windows.Foundation.Point(dstX, dstY));
            br = ge.TransformPoint(new Windows.Foundation.Point(dstX + dstW, dstY + dstH));
        }
        catch
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        double dx = Math.Min(tl.X, br.X);
        double dy = Math.Min(tl.Y, br.Y);
        double dw = Math.Abs(br.X - tl.X);
        double dh = Math.Abs(br.Y - tl.Y);
        if (dw < 8 || dh < 8) { CropOverlay.Visibility = Visibility.Collapsed; return; }

        _cropDestRect = new Windows.Foundation.Rect(dx, dy, dw, dh);

        double cl = Math.Clamp(clip.CropLeft, 0, 0.95);
        double ct = Math.Clamp(clip.CropTop, 0, 0.95);
        double cr = Math.Clamp(clip.CropRight, 0, 0.95);
        double cb = Math.Clamp(clip.CropBottom, 0, 0.95);
        double bx = dx + cl * dw;
        double by = dy + ct * dh;
        double bw = Math.Max(8, (1 - cl - cr) * dw);
        double bh = Math.Max(8, (1 - ct - cb) * dh);

        Canvas.SetLeft(CropBox, bx);
        Canvas.SetTop(CropBox, by);
        CropBox.Width = bw;
        CropBox.Height = bh;

        SetCropMask(CropMaskLeft,   dx,      dy,       Math.Max(0, bx - dx),                   dh);
        SetCropMask(CropMaskRight,  bx + bw, dy,       Math.Max(0, dx + dw - (bx + bw)),       dh);
        SetCropMask(CropMaskTop,    bx,      dy,       bw,                                     Math.Max(0, by - dy));
        SetCropMask(CropMaskBottom, bx,      by + bh,  bw,                                     Math.Max(0, dy + dh - (by + bh)));

        Canvas.SetLeft(CropEdgeL, bx - CropEdgeL.Width / 2);
        Canvas.SetTop(CropEdgeL, by);
        CropEdgeL.Height = bh;

        Canvas.SetLeft(CropEdgeR, bx + bw - CropEdgeR.Width / 2);
        Canvas.SetTop(CropEdgeR, by);
        CropEdgeR.Height = bh;

        Canvas.SetLeft(CropEdgeT, bx);
        Canvas.SetTop(CropEdgeT, by - CropEdgeT.Height / 2);
        CropEdgeT.Width = bw;

        Canvas.SetLeft(CropEdgeB, bx);
        Canvas.SetTop(CropEdgeB, by + bh - CropEdgeB.Height / 2);
        CropEdgeB.Width = bw;

        PlaceCropCorner(CropHandleTL, bx,      by);
        PlaceCropCorner(CropHandleTR, bx + bw, by);
        PlaceCropCorner(CropHandleBL, bx,      by + bh);
        PlaceCropCorner(CropHandleBR, bx + bw, by + bh);

        // Confirm button: centered under the crop box, clamped inside the dest
        // rect so it stays reachable even when the user crops near the bottom.
        double btnW = CropConfirmBtn.Width;
        double btnH = CropConfirmBtn.Height;
        double btnX = bx + (bw - btnW) / 2;
        double btnY = by + bh + 10;
        double maxY = dy + dh - btnH - 4;
        if (btnY > maxY) btnY = by + bh - btnH - 6;  // tuck inside the box if no room below
        Canvas.SetLeft(CropConfirmBtn, btnX);
        Canvas.SetTop(CropConfirmBtn, btnY);
    }

    private static void SetCropMask(Microsoft.UI.Xaml.Shapes.Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    private static void PlaceCropCorner(Microsoft.UI.Xaml.Shapes.Rectangle r, double cx, double cy)
    {
        Canvas.SetLeft(r, cx - r.Width / 2);
        Canvas.SetTop(r, cy - r.Height / 2);
    }

    private void OnCropBodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_inCropMode || _croppingClip is null) return;
        _cropGesture = CropGesture.Move;
        _cropGestureStart = e.GetCurrentPoint(CropOverlay).Position;
        _cropStartLeft   = _croppingClip.CropLeft;
        _cropStartTop    = _croppingClip.CropTop;
        _cropStartRight  = _croppingClip.CropRight;
        _cropStartBottom = _croppingClip.CropBottom;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCropBodyMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropGesture != CropGesture.Move || _croppingClip is null) return;
        var pt = e.GetCurrentPoint(CropOverlay).Position;
        if (!e.GetCurrentPoint(CropOverlay).Properties.IsLeftButtonPressed) return;

        double dw = _cropDestRect.Width;
        double dh = _cropDestRect.Height;
        if (dw < 1 || dh < 1) return;

        double fxDelta = (pt.X - _cropGestureStart.X) / dw;
        double fyDelta = (pt.Y - _cropGestureStart.Y) / dh;

        double winW = 1 - _cropStartLeft - _cropStartRight;
        double winH = 1 - _cropStartTop  - _cropStartBottom;
        double newLeft = Math.Clamp(_cropStartLeft + fxDelta, 0, Math.Max(0, 1 - winW));
        double newTop  = Math.Clamp(_cropStartTop  + fyDelta, 0, Math.Max(0, 1 - winH));
        _croppingClip.CropLeft   = newLeft;
        _croppingClip.CropRight  = Math.Max(0, 1 - winW - newLeft);
        _croppingClip.CropTop    = newTop;
        _croppingClip.CropBottom = Math.Max(0, 1 - winH - newTop);
        e.Handled = true;
    }

    private void OnCropBodyReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropGesture != CropGesture.Move) return;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (_vm is not null) _vm.Project.IsModified = true;
        _cropGesture = CropGesture.None;
        e.Handled = true;
    }

    private void OnCropBodyCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _cropGesture = CropGesture.None;
    }

    private void OnCropHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_inCropMode || _croppingClip is null || sender is not FrameworkElement fe) return;
        _cropGesture = (fe.Tag as string) switch
        {
            "L"  => CropGesture.L,
            "R"  => CropGesture.R,
            "T"  => CropGesture.T,
            "B"  => CropGesture.B,
            "TL" => CropGesture.TL,
            "TR" => CropGesture.TR,
            "BL" => CropGesture.BL,
            "BR" => CropGesture.BR,
            _    => CropGesture.None,
        };
        if (_cropGesture == CropGesture.None) return;
        _cropGestureStart = e.GetCurrentPoint(CropOverlay).Position;
        _cropStartLeft   = _croppingClip.CropLeft;
        _cropStartTop    = _croppingClip.CropTop;
        _cropStartRight  = _croppingClip.CropRight;
        _cropStartBottom = _croppingClip.CropBottom;
        fe.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCropHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropGesture is CropGesture.None or CropGesture.Move || _croppingClip is null) return;
        var pt = e.GetCurrentPoint(CropOverlay).Position;
        if (!e.GetCurrentPoint(CropOverlay).Properties.IsLeftButtonPressed) return;

        double dw = _cropDestRect.Width;
        double dh = _cropDestRect.Height;
        if (dw < 1 || dh < 1) return;

        double fxDelta = (pt.X - _cropGestureStart.X) / dw;
        double fyDelta = (pt.Y - _cropGestureStart.Y) / dh;

        const double MaxPerSide = 0.95;
        const double MinWindow  = 0.05;

        double l = _cropStartLeft;
        double t = _cropStartTop;
        double r = _cropStartRight;
        double b = _cropStartBottom;

        if (_cropGesture is CropGesture.L or CropGesture.TL or CropGesture.BL)
            l = Math.Clamp(_cropStartLeft + fxDelta, 0, Math.Min(MaxPerSide, 1 - _cropStartRight - MinWindow));
        if (_cropGesture is CropGesture.R or CropGesture.TR or CropGesture.BR)
            r = Math.Clamp(_cropStartRight - fxDelta, 0, Math.Min(MaxPerSide, 1 - _cropStartLeft - MinWindow));
        if (_cropGesture is CropGesture.T or CropGesture.TL or CropGesture.TR)
            t = Math.Clamp(_cropStartTop + fyDelta, 0, Math.Min(MaxPerSide, 1 - _cropStartBottom - MinWindow));
        if (_cropGesture is CropGesture.B or CropGesture.BL or CropGesture.BR)
            b = Math.Clamp(_cropStartBottom - fyDelta, 0, Math.Min(MaxPerSide, 1 - _cropStartTop - MinWindow));

        _croppingClip.CropLeft   = l;
        _croppingClip.CropTop    = t;
        _croppingClip.CropRight  = r;
        _croppingClip.CropBottom = b;
        e.Handled = true;
    }

    private void OnCropHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropGesture == CropGesture.None) return;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (_vm is not null) _vm.Project.IsModified = true;
        _cropGesture = CropGesture.None;
        e.Handled = true;
    }

    private void OnCropHandleCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _cropGesture = CropGesture.None;
    }

    private void OnCropConfirmClick(object sender, RoutedEventArgs e) => ExitCropMode();

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
        // Preview-overlay text was removed; this hook is kept as a no-op so the
        // existing call sites don't need to change.
    }

    private void OnPlaybackTick(object? sender, object e)
    {
        if (_vm is null) return;
        var nowElapsed = _playClock.Elapsed;
        var dt = nowElapsed - _lastTickElapsed;
        _lastTickElapsed = nowElapsed;
        if (dt < TimeSpan.Zero) dt = TimeSpan.Zero;
        _vm.Project.Playhead += dt;

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

        // Title overlay tracks the playhead independently of video.
        SetTitleClipAtPlayhead(FindTitleClipAt(_vm.Project.Playhead));

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
            ("Sharpen",         "Edge enhancement"),
            ("Vignette",        "Edge darken"),
            ("Pixelate",        "Mosaic blocks"),
            ("Glow",            "Soft bloom"),
            ("Drop Shadow",     "Layer shadow"),
            ("Chroma Key",      "Green-screen removal"),
            ("Invert",          "Negative colors"),
            ("Grayscale",       "Desaturate to gray"),
            ("Sepia",           "Warm tone wash"),
            ("Edge Detect",     "Outline edges"),
            ("Posterize",       "Quantize colors"),
            ("Emboss",          "Raised relief"),
            ("Hue Rotate",      "Shift hues 0–360°"),
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

    /// <summary>Find the title track (creating one if the project doesn't have it yet).
    /// New title tracks are inserted at the top of the stack so titles render above video.</summary>
    private Track? GetOrCreateTitleTrack()
    {
        if (_vm is null) return null;
        var track = _vm.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Title);
        if (track is null)
        {
            track = new Track { Label = "T1", Kind = TrackKind.Title };
            _vm.Tracks.Insert(0, track);
        }
        return track;
    }

    private void AddTitleClip(string presetName)
    {
        if (_vm is null) return;
        var track = GetOrCreateTitleTrack();
        if (track is null) return;

        // If a title already covers the playhead, edit it instead of stacking another.
        var existing = track.Clips.FirstOrDefault(c =>
            _vm.Project.Playhead >= c.TimelineStart && _vm.Project.Playhead < c.TimelineEnd);
        if (existing is not null)
        {
            SelectAndEditTitleClip(existing);
            return;
        }

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

    /// <summary>Drop a fresh title clip at the playhead on the title track, select it,
    /// and put the preview overlay straight into edit mode so the user can type
    /// immediately. If a title already covers the playhead, edit that one instead of
    /// creating a second overlapping clip.</summary>
    private void AddTitleAtPlayheadAndEdit()
    {
        if (_vm is null) return;
        var track = GetOrCreateTitleTrack();
        if (track is null) return;

        var existing = track.Clips.FirstOrDefault(c =>
            _vm.Project.Playhead >= c.TimelineStart && _vm.Project.Playhead < c.TimelineEnd);
        if (existing is not null)
        {
            SelectAndEditTitleClip(existing);
            return;
        }

        var clip = new TimelineClip
        {
            Label         = "Title",
            Kind          = ClipKind.Title,
            TimelineStart = _vm.Project.Playhead,
            Duration      = TimeSpan.FromSeconds(3),
            ColorHue      = 30,
        };

        _vm.History.Record(new ClipAddAction(track, clip));
        SelectAndEditTitleClip(clip);
    }

    private void SelectAndEditTitleClip(TimelineClip clip)
    {
        if (_vm is null) return;
        foreach (var tr in _vm.Tracks)
            foreach (var c in tr.Clips)
                c.IsSelected = false;
        clip.IsSelected = true;
        _vm.SelectedClip = clip;

        Timeline.ViewModel = _vm;
        UpdateInspector(clip);
        UpdateClipHeader(clip);
        SetPresentedClip(clip);
        // The title overlay drives edit-mode targeting — make sure it's bound to
        // this clip before we open the editor.
        SetTitleClipAtPlayhead(clip);
        EnterTitleEditMode();
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
        Timeline.Refresh();
        RefreshPresentation();
    }

    private void OnTimelineAddKeyframeRequested(object? sender,
        (TimelineClip Clip, ClipEffect Fx, string ParamKey) args)
    {
        AddKeyframeAtPlayhead(args.Clip, args.Fx, args.ParamKey);
        // Make sure the clip is selected so the inspector reflects the change.
        if (_vm is not null && !ReferenceEquals(_vm.SelectedClip, args.Clip))
        {
            _vm.SelectedClip = args.Clip;
            UpdateClipHeader(args.Clip);
            UpdatePreviewOverlay(args.Clip);
        }
    }

    private void OnTimelineKeyframeSelectionChanged(object? sender, EventArgs e)
    {
        if (_vm?.SelectedClip is { } clip && _vm.InspectorTab == "automations")
            UpdateInspector(clip);
    }

    private void UpdatePreviewOverlay(TimelineClip clip)
    {
        // Preview-overlay text was removed; this hook is kept as a no-op so the
        // existing call sites don't need to change.
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
        // Project dimensions feed the compositor's letterbox and per-clip transform
        // math, the transform-handle overlay, and the title clip's project-pixel→
        // viewport-pixel mapping. None of those refresh on their own when only the
        // project's W/H change (PreviewArea hasn't resized) — kick them by hand.
        UpdateTransformHandles();
        if (_titleClipAtPlayhead is not null)
            ApplyTitlePosition(_titleClipAtPlayhead);

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

    private void ShowInspectorLayout(bool automations)
    {
        InspectorScrollViewer.Visibility = automations ? Visibility.Collapsed : Visibility.Visible;
        AutomationsLayout.Visibility     = automations ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void UpdateInspector(TimelineClip? clip)
    {
        InspectorContent.Children.Clear();
        AutomationsListPanel.Children.Clear();
        AutomationsEditorPanel.Children.Clear();
        if (clip is null || _vm is null)
        {
            ShowInspectorLayout(automations: false);
            return;
        }

        ShowInspectorLayout(automations: _vm.InspectorTab == "automations");

        switch (_vm.InspectorTab)
        {
            case "audio":       BuildInspectorAudio(clip);       break;
            case "effects":     BuildInspectorEffects(clip);     break;
            case "color":       BuildInspectorColor(clip);       break;
            case "automations": BuildInspectorAutomations(clip); break;
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

        if (clip.Kind == ClipKind.Title)
        {
            BuildInspectorTitleText(clip);
        }

        if (clip.Kind is ClipKind.Video or ClipKind.Title)
        {
            InspectorContent.Children.Add(MakeInspSectionHeader("TRANSFORM"));
            // PosX/PosY are in canvas-pixel units. Range to ±canvas dimensions so the
            // slider covers the full movable area regardless of project export size.
            var (canvasW, canvasH) = _vm.Project.GetCanvasSize();
            AddInspSlider("Scale",    clip.Scale    * 100,   10,  300, v => clip.Scale    = v / 100.0);
            AddInspSlider("Pos X",    clip.PosX,        -canvasW, canvasW, v => clip.PosX     = v);
            AddInspSlider("Pos Y",    clip.PosY,        -canvasH, canvasH, v => clip.PosY     = v);
            AddInspSlider("Rotation", clip.Rotation,        -180,  180, v => clip.Rotation = v);
            AddInspSlider("Opacity",  clip.Opacity  * 100,    0,  100, v => clip.Opacity  = v / 100.0);

            InspectorContent.Children.Add(MakeInspSectionHeader("CROP"));
            AddInspSlider("Left",   clip.CropLeft   * 100, 0, 95, v => clip.CropLeft   = v / 100.0);
            AddInspSlider("Top",    clip.CropTop    * 100, 0, 95, v => clip.CropTop    = v / 100.0);
            AddInspSlider("Right",  clip.CropRight  * 100, 0, 95, v => clip.CropRight  = v / 100.0);
            AddInspSlider("Bottom", clip.CropBottom * 100, 0, 95, v => clip.CropBottom = v / 100.0);
        }
    }

    private static readonly string[] FontFamilyChoices =
    {
        "Segoe UI", "Arial", "Calibri", "Cambria", "Consolas", "Courier New",
        "Georgia", "Impact", "Times New Roman", "Trebuchet MS", "Verdana",
    };

    private static readonly (string Label, string Hex)[] TextColorSwatches =
    {
        ("White",  "#FFFFFFFF"),
        ("Black",  "#FF000000"),
        ("Yellow", "#FFFFD93D"),
        ("Red",    "#FFE05858"),
        ("Green",  "#FF7FB069"),
        ("Cyan",   "#FF6EC1E4"),
    };

    private void BuildInspectorTitleText(TimelineClip clip)
    {
        InspectorContent.Children.Add(MakeInspSectionHeader("TEXT"));

        // Text content
        InspectorContent.Children.Add(MakeTextBoxRow(
            "Text", clip.Label, v => { clip.Label = v; Timeline.Refresh(); UpdateClipHeader(clip); }));

        // Font family
        InspectorContent.Children.Add(MakeFontFamilyRow(clip));

        // Font size
        AddInspSlider("Size", clip.FontSize, 8, 240, v => clip.FontSize = v);

        // Bold / Italic / Underline toggles + alignment
        InspectorContent.Children.Add(MakeFontStyleRow(clip));

        // Alignment
        InspectorContent.Children.Add(MakeTextAlignRow(clip));

        // Color swatches
        InspectorContent.Children.Add(MakeColorSwatchRow(clip));
    }

    private UIElement MakeTextBoxRow(string label, string value, Action<string> onChange)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var tb = new TextBox
        {
            Text = value,
            FontSize = 11.5,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        panel.Children.Add(tb);
        return panel;
    }

    private UIElement MakeFontFamilyRow(TimelineClip clip)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Font",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var combo = new ComboBox
        {
            ItemsSource = FontFamilyChoices,
            SelectedItem = FontFamilyChoices.Contains(clip.FontFamily) ? clip.FontFamily : FontFamilyChoices[0],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11.5,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s) clip.FontFamily = s;
        };
        panel.Children.Add(combo);
        return panel;
    }

    private UIElement MakeFontStyleRow(TimelineClip clip)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Style",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var boldBtn = MakeToggle("B", clip.IsBold, v => clip.IsBold = v);
        boldBtn.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        var italicBtn = MakeToggle("I", clip.IsItalic, v => clip.IsItalic = v);
        italicBtn.FontStyle = Windows.UI.Text.FontStyle.Italic;
        var underlineBtn = MakeToggle("U", clip.IsUnderline, v => clip.IsUnderline = v);

        row.Children.Add(boldBtn);
        row.Children.Add(italicBtn);
        row.Children.Add(underlineBtn);
        panel.Children.Add(row);
        return panel;
    }

    private static ToggleButton MakeToggle(string content, bool initial, Action<bool> onChange)
    {
        var t = new ToggleButton
        {
            Content = content,
            IsChecked = initial,
            MinWidth = 32,
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
        };
        t.Checked   += (_, _) => onChange(true);
        t.Unchecked += (_, _) => onChange(false);
        return t;
    }

    private UIElement MakeTextAlignRow(TimelineClip clip)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Align",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        ToggleButton? activeBtn = null;
        ToggleButton MakeAlignBtn(string label, string value)
        {
            var t = new ToggleButton
            {
                Content = label,
                MinWidth = 32,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12,
                IsChecked = clip.TextAlign == value,
            };
            if (t.IsChecked == true) activeBtn = t;
            t.Click += (_, _) =>
            {
                clip.TextAlign = value;
                t.IsChecked = true;
                if (activeBtn is not null && activeBtn != t) activeBtn.IsChecked = false;
                activeBtn = t;
            };
            return t;
        }

        row.Children.Add(MakeAlignBtn("⯇", "Left"));
        row.Children.Add(MakeAlignBtn("≡", "Center"));
        row.Children.Add(MakeAlignBtn("⯈", "Right"));
        panel.Children.Add(row);
        return panel;
    }

    private UIElement MakeColorSwatchRow(TimelineClip clip)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 4, 14, 4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Color",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (lbl, hex) in TextColorSwatches)
        {
            var fill = ParseColorBrush(hex) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
            var swatchBorder = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                Background = fill,
                BorderBrush = string.Equals(clip.TextColor, hex, StringComparison.OrdinalIgnoreCase)
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : (Brush)Application.Current.Resources["HairlineBrush"],
                BorderThickness = new Thickness(string.Equals(clip.TextColor, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
            };
            ToolTipService.SetToolTip(swatchBorder, lbl);
            swatchBorder.Tapped += (_, _) =>
            {
                clip.TextColor = hex;
                UpdateInspector(clip);
            };
            row.Children.Add(swatchBorder);
        }
        panel.Children.Add(row);
        return panel;
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
        // Geometry & dB scale
        const int    Segments    = 40;        // higher resolution than the old 22-segment bar
        const double FloorDb     = -60.0;     // bottom of the meter, in dBFS
        const double GreenTopDb  = -18.0;     // green→amber transition
        const double AmberTopDb  =  -6.0;     // amber→red transition
        const double ClipDb      =  -0.5;     // sample-level "clipping" trigger
        const double PeakHoldSec = 1.5;       // peak-hold dwell before falling
        const double PeakFallDbPerSec = 30;   // peak-hold fall speed once dwell elapses
        const double RmsWindowSec = 0.3;      // ~300 ms RMS window (close to VU)

        // Map any linear amplitude in [0..1] to a [0..1] meter position based on dBFS.
        static double AmpToMeterFraction(double amp)
        {
            if (amp <= 0) return 0;
            double db = 20.0 * Math.Log10(amp);
            if (db <= FloorDb) return 0;
            if (db >= 0)       return 1;
            return (db - FloorDb) / (0 - FloorDb);
        }

        // Color zones use *dB position*, not linear position, so the colors line up
        // with conventional broadcast meters (green up to -18, amber to -6, red above).
        double greenFrac = (GreenTopDb - FloorDb) / (0 - FloorDb);
        double amberFrac = (AmberTopDb - FloorDb) / (0 - FloorDb);

        var greenOn  = new SolidColorBrush(Color.FromArgb(255,  72, 196, 124));
        var amberOn  = new SolidColorBrush(Color.FromArgb(255, 220, 184,  72));
        var redOn    = new SolidColorBrush(Color.FromArgb(255, 224,  88,  88));
        var greenOff = new SolidColorBrush(Color.FromArgb( 48,  72, 196, 124));
        var amberOff = new SolidColorBrush(Color.FromArgb( 48, 220, 184,  72));
        var redOff   = new SolidColorBrush(Color.FromArgb( 48, 224,  88,  88));

        Brush ColorFor(int i, bool on)
        {
            double frac = (i + 0.5) / Segments;
            if (frac <= greenFrac) return on ? greenOn : greenOff;
            if (frac <= amberFrac) return on ? amberOn : amberOff;
            return on ? redOn : redOff;
        }

        // Mono font for all numeric readouts so columns stay aligned across updates.
        var monoFont = new FontFamily("JetBrains Mono, Consolas");

        var cellsL = new Rectangle[Segments];
        var cellsR = new Rectangle[Segments];

        Grid BuildRow(string label, Rectangle[] cells)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(10) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
            };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 9.5,
                FontFamily = monoFont,
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
                    Width = 4,
                    Height = 8,
                    RadiusX = 0.5,
                    RadiusY = 0.5,
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

        // dB tick row underneath the bars: marks 0, -6, -18, -30, -60 dB positions.
        Grid BuildScaleRow()
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(10) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                Margin = new Thickness(0, 1, 0, 0),
            };
            // The bar cells are Width=4 + Spacing=1 = 5px per segment, totaling
            // Segments*5 - 1 px. Place tick labels at fractional positions of that width.
            int totalWidth = Segments * 5 - 1;
            var canvas = new Canvas { Height = 9, Width = totalWidth };
            void Tick(double db, string text)
            {
                double frac = (db - FloorDb) / (0 - FloorDb);
                if (frac < 0 || frac > 1) return;
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 8,
                    FontFamily = monoFont,
                    Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
                };
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double x = frac * totalWidth - tb.DesiredSize.Width / 2;
                Canvas.SetLeft(tb, Math.Max(0, Math.Min(totalWidth - tb.DesiredSize.Width, x)));
                Canvas.SetTop(tb, 0);
                canvas.Children.Add(tb);
            }
            Tick(  0, "0");
            Tick( -6, "-6");
            Tick(-18, "-18");
            Tick(-30, "-30");
            Tick(-60, "-60");
            Grid.SetColumn(canvas, 1);
            grid.Children.Add(canvas);
            return grid;
        }

        // Numeric readouts row: Peak / Hold / RMS dBFS. Tight tabular layout so the
        // values can be read at a glance while audio plays.
        TextBlock MakeStatLabel(string text)
            => new() {
                Text = text,
                FontSize = 9,
                FontFamily = monoFont,
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            };
        TextBlock MakeStatValue()
            => new() {
                Text = "-∞ dB",
                FontSize = 10,
                FontFamily = monoFont,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };

        var peakValue = MakeStatValue();
        var holdValue = MakeStatValue();
        var rmsValue  = MakeStatValue();

        Grid BuildStatsRow()
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                Margin = new Thickness(0, 4, 0, 0),
            };
            void Col(int idx, string title, TextBlock value)
            {
                var sp = new StackPanel { Spacing = 1 };
                sp.Children.Add(MakeStatLabel(title));
                sp.Children.Add(value);
                Grid.SetColumn(sp, idx);
                grid.Children.Add(sp);
            }
            Col(0, "PEAK", peakValue);
            Col(1, "HOLD", holdValue);
            Col(2, "RMS",  rmsValue);
            return grid;
        }

        // Latching clip indicator. The user resets it by tapping the chip.
        var clipBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 224, 88, 88)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 224, 88, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new TextBlock
            {
                Text = "CLIP — click to clear",
                FontSize = 9,
                FontFamily = monoFont,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 88, 88)),
            },
            Visibility = Visibility.Collapsed,
        };

        var container = new StackPanel
        {
            Padding = new Thickness(14, 4, 14, 8),
            Spacing = 3,
        };
        container.Children.Add(BuildRow("L", cellsL));
        container.Children.Add(BuildRow("R", cellsR));
        container.Children.Add(BuildScaleRow());
        container.Children.Add(BuildStatsRow());
        container.Children.Add(clipBorder);

        // Ensure peak data is being extracted so the meter shows the real waveform
        // (instead of zero) as soon as the background job finishes.
        if (_vm is not null && !string.IsNullOrEmpty(clip.SourceId))
        {
            var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
            if (media is not null && !string.IsNullOrEmpty(media.FilePath))
                BTAP.Services.WaveformService.EnsurePeaksAsync(media.FilePath);
        }

        // Meter ballistics state
        double levelL = 0, levelR = 0;           // displayed bar levels (smoothed)
        double peakL  = 0, peakR  = 0;           // peak-hold values (linear amplitude)
        double peakLDwell = 0, peakRDwell = 0;   // seconds-of-hold remaining before fall
        bool   clipped = false;

        // RMS rolling window. At 60Hz tick the window is RmsWindowSec / TickSec samples.
        const double TickMs = 16.0;              // 60Hz update rate
        int rmsWindowSize = Math.Max(1, (int)(RmsWindowSec * 1000.0 / TickMs));
        var rmsBuf = new double[rmsWindowSize];
        int rmsIdx = 0;
        double rmsSumSq = 0;

        DateTime lastTick = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };

        clipBorder.Tapped += (_, _) =>
        {
            clipped = false;
            clipBorder.Visibility = Visibility.Collapsed;
        };

        // Sample the cached peak data at the playhead's offset into the clip's source.
        (double instL, double instR) SamplePeaks()
        {
            if (_vm is null) return (0, 0);
            if (string.IsNullOrEmpty(clip.SourceId)) return (0, 0);
            var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
            if (media is null || string.IsNullOrEmpty(media.FilePath)) return (0, 0);
            var data = BTAP.Services.WaveformService.GetCachedPeaks(media.FilePath);
            if (data is null || data.PeaksL.Length == 0) return (0, 0);

            var playhead = _vm.Project.Playhead;
            if (playhead < clip.TimelineStart || playhead >= clip.TimelineEnd) return (0, 0);

            var srcTime  = clip.SourceStart + (playhead - clip.TimelineStart);
            double srcSec = srcTime.TotalSeconds;
            if (srcSec < 0) return (0, 0);
            int bucket = (int)(srcSec * data.BucketsPerSecond);
            if (bucket < 0 || bucket >= data.PeaksL.Length) return (0, 0);

            double l = data.PeaksL[bucket];
            double r = data.PeaksR[bucket];
            return (l, r);
        }

        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            double dt = (now - lastTick).TotalSeconds;
            lastTick = now;
            if (dt <= 0 || dt > 0.25) dt = TickMs / 1000.0;

            double instL = 0, instR = 0;
            if (_vm?.IsPlaying == true)
            {
                var (sL, sR) = SamplePeaks();
                instL = sL;
                instR = sR;

                // Apply per-clip gain + pan. clip.Volume is the linear gain factor
                // (slider goes 0..2 for ±6 dB). Pan is -1..+1 (constant-power-ish via
                // linear taper here — close enough for a meter, simpler than sqrt).
                double gain = Math.Clamp(clip.Volume, 0, 4);
                instL *= gain;
                instR *= gain;

                double pan = Math.Clamp(clip.Pan, -1, 1);
                instL *= 1.0 - Math.Max(0,  pan);
                instR *= 1.0 - Math.Max(0, -pan);
            }

            // Ballistics: instant attack to the actual sample peak (matches PPM-style
            // behavior; we already integrate over 5 ms via the bucket so no more
            // smoothing is needed on attack). Release falls roughly 20 dB/sec.
            double releasePerTick = Math.Pow(10, -20 * dt / 20.0); // 20 dB/s release
            levelL = Math.Max(instL, levelL * releasePerTick);
            levelR = Math.Max(instR, levelR * releasePerTick);

            // Peak hold: latch instantaneous peak for PeakHoldSec, then fall at
            // PeakFallDbPerSec until it meets the live level.
            void UpdateHold(ref double hold, ref double dwell, double inst)
            {
                if (inst >= hold) { hold = inst; dwell = PeakHoldSec; return; }
                if (dwell > 0)    { dwell -= dt; return; }
                // Dwell elapsed — drop hold at the configured dB-per-second rate.
                double db = hold > 0 ? 20 * Math.Log10(hold) : FloorDb - 1;
                db -= PeakFallDbPerSec * dt;
                hold = db <= FloorDb ? 0 : Math.Pow(10, db / 20.0);
            }
            UpdateHold(ref peakL, ref peakLDwell, instL);
            UpdateHold(ref peakR, ref peakRDwell, instR);

            // Clip-latch: any sample above ClipDb (effectively 0 dBFS) lights and
            // holds the red badge until the user clicks to clear it.
            double maxInst = Math.Max(instL, instR);
            if (maxInst > 0 && 20 * Math.Log10(maxInst) >= ClipDb)
            {
                if (!clipped)
                {
                    clipped = true;
                    clipBorder.Visibility = Visibility.Visible;
                }
            }

            // Update bars from dB position, not linear — so the green/amber/red
            // bands stay visually proportional to the dB labels.
            double fracL = AmpToMeterFraction(levelL);
            double fracR = AmpToMeterFraction(levelR);
            double holdFracL = AmpToMeterFraction(peakL);
            double holdFracR = AmpToMeterFraction(peakR);

            int litL = (int)Math.Round(fracL * Segments);
            int litR = (int)Math.Round(fracR * Segments);
            int pkL  = (int)Math.Round(holdFracL * Segments);
            int pkR  = (int)Math.Round(holdFracR * Segments);

            for (int i = 0; i < Segments; i++)
            {
                bool onL = i < litL || (pkL > 0 && i == pkL - 1);
                bool onR = i < litR || (pkR > 0 && i == pkR - 1);
                cellsL[i].Fill = ColorFor(i, onL);
                cellsR[i].Fill = ColorFor(i, onR);
            }

            // RMS over the rolling window. We feed it the max(L,R) per tick so the
            // displayed RMS reflects whichever channel is loudest — matches the way
            // most NLEs label a single "RMS" readout for a stereo track.
            double rmsSample = Math.Max(instL, instR);
            double rmsSampleSq = rmsSample * rmsSample;
            rmsSumSq += rmsSampleSq - rmsBuf[rmsIdx];
            rmsBuf[rmsIdx] = rmsSampleSq;
            rmsIdx = (rmsIdx + 1) % rmsBuf.Length;
            double rms = Math.Sqrt(Math.Max(0, rmsSumSq) / rmsBuf.Length);

            static string Fmt(double amp) =>
                amp < 1e-5 ? "-∞ dB" : $"{20 * Math.Log10(amp),6:F1} dB";

            double peakInst = Math.Max(levelL, levelR);
            double peakHold = Math.Max(peakL, peakR);
            peakValue.Text = Fmt(peakInst);
            holdValue.Text = Fmt(peakHold);
            rmsValue.Text  = Fmt(rms);

            // Recolor PEAK readout when clipping is "right now" (not just latched)
            peakValue.Foreground = peakInst > 0 && 20 * Math.Log10(peakInst) >= ClipDb
                ? (Brush)new SolidColorBrush(Color.FromArgb(255, 224, 88, 88))
                : (Brush)Application.Current.Resources["TextMutedBrush"];
        };

        // Refresh when peaks land asynchronously (so the meter shows real data the
        // moment WaveformService finishes extracting).
        EventHandler<string>? onPeaks = null;
        onPeaks = (_, path) =>
        {
            if (_vm is null) return;
            var media = _vm.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
            if (media is null || !string.Equals(media.FilePath, path, StringComparison.OrdinalIgnoreCase))
                return;
            // No explicit refresh needed — the next timer tick will read the new cache.
        };

        container.Loaded += (_, _) =>
        {
            lastTick = DateTime.UtcNow;
            BTAP.Services.WaveformService.PeaksReady += onPeaks;
            timer.Start();
        };
        container.Unloaded += (_, _) =>
        {
            timer.Stop();
            BTAP.Services.WaveformService.PeaksReady -= onPeaks;
        };

        return container;
    }

    private void BuildInspectorEffects(TimelineClip clip)
    {
        InspectorContent.Children.Add(MakeInspSectionHeader("EFFECTS"));
        foreach (var name in ClipEffectsChain.AvailableVideoEffects)
            InspectorContent.Children.Add(MakeEffectToggleRow(clip, name));
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

    /// <summary>Flat-list automations browser: every keyframe across every effect
    /// parameter on the clip, sorted by time. Click selects, Ctrl-click toggles
    /// multi-select, selection is shared with the timeline (diamonds on the clip
    /// highlight in sync). The bottom editor panel shows details for the current
    /// selection so the user can adjust the value/time without losing the list.</summary>
    private void BuildInspectorAutomations(TimelineClip clip)
    {
        if (_vm is null) return;

        // Enumerate every (effect, paramKey, keyframe) triple on the clip.
        var rows = new List<(ClipEffect Fx, string Key, EffectKeyframe Kf)>();
        foreach (var fx in clip.Effects)
            foreach (var kv in fx.Keyframes)
                foreach (var kf in kv.Value)
                    rows.Add((fx, kv.Key, kf));
        rows.Sort((a, b) => a.Kf.TimeRel.CompareTo(b.Kf.TimeRel));

        // ── TOP: scrollable list ──────────────────────────────────────────
        AutomationsListPanel.Children.Add(MakeInspSectionHeader("KEYFRAMES"));
        AutomationsListPanel.Children.Add(MakeAutomationsToolbar(clip, rows));

        if (rows.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "No keyframes yet. Right-click the clip on the timeline " +
                       "and choose Add keyframe → [parameter], or use the diamond " +
                       "toggle next to a parameter in the Effects tab.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 8, 14, 10),
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            AutomationsListPanel.Children.Add(hint);
        }
        else
        {
            foreach (var (fx, key, kf) in rows)
                AutomationsListPanel.Children.Add(MakeAutomationRow(clip, fx, key, kf));
        }

        // ── BOTTOM: details / editor for the current selection ────────────
        BuildAutomationDetailsEditor(clip, rows);
    }

    /// <summary>Bottom-panel editor for the keyframe(s) currently selected. Shows
    /// time + value sliders for a single selection; falls back to a bulk-edit
    /// view (set-all + time-shift) for multi-selection; placeholder hint for
    /// empty selection. Lives in <see cref="AutomationsEditorPanel"/> so the
    /// keyframe list scrolls independently above it.</summary>
    private void BuildAutomationDetailsEditor(TimelineClip clip,
        IReadOnlyList<(ClipEffect Fx, string Key, EffectKeyframe Kf)> allRows)
    {
        if (_vm is null) return;

        var sel = _vm.SelectedKeyframes;

        // Map selected keyframes back to their owning (Fx, Key) triples so we can
        // resolve parameter schema (label + min/max) for the editor sliders.
        var selectedTriples = allRows.Where(t => sel.Contains(t.Kf)).ToList();

        AutomationsEditorPanel.Children.Add(MakeInspSectionHeader("DETAILS"));

        if (selectedTriples.Count == 0)
        {
            AutomationsEditorPanel.Children.Add(new TextBlock
            {
                Text = "Select a keyframe above or click a diamond on the clip to edit its value and time.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 8, 14, 10),
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        if (selectedTriples.Count == 1)
        {
            var (fx, key, kf) = selectedTriples[0];
            var schema = ClipEffectsChain.NumberParams(fx.Name).FirstOrDefault(p => p.Key == key);
            var label  = !string.IsNullOrEmpty(schema.Label) ? schema.Label : key;
            double min = !string.IsNullOrEmpty(schema.Key)   ? schema.Min   : 0;
            double max = !string.IsNullOrEmpty(schema.Key)   ? schema.Max   : 1;

            // Header row: hue dot · "Effect · Param"
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 6, 14, 0) };
            header.Children.Add(new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(KeyframeHueFor(fx.Name, key)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{fx.Name} · {label}",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            AutomationsEditorPanel.Children.Add(header);

            // Value slider — bound to the keyframe's value
            AutomationsEditorPanel.Children.Add(MakeNamedSliderRow("Value", min, max, kf.Value, v =>
            {
                kf.Value = v;
                _vm.Project.IsModified = true;
                // Refresh the corresponding row in the list so its value updates.
                UpdateInspector(clip);
            }));

            // Time slider — 0..1 within clip duration. Show absolute timecode beside.
            AutomationsEditorPanel.Children.Add(MakeKeyframeTimeRow(clip, kf));

            // Delete button
            var deleteBtn = new Button
            {
                Content = "Delete this keyframe",
                FontSize = 11,
                Margin = new Thickness(14, 8, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["GhostButtonStyle"],
            };
            deleteBtn.Click += (_, _) => BulkDeleteSelectedKeyframes(clip);
            AutomationsEditorPanel.Children.Add(deleteBtn);
        }
        else
        {
            AutomationsEditorPanel.Children.Add(new TextBlock
            {
                Text = $"{selectedTriples.Count} keyframes selected",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(14, 6, 14, 4),
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            });
            AutomationsEditorPanel.Children.Add(new TextBlock
            {
                Text = "Use the toolbar above to delete, nudge, or set a common value. " +
                       "Pick a single keyframe to edit its value and time precisely.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 0, 14, 10),
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
        }
    }

    /// <summary>Slider row with label + live value readout — shared between the
    /// Value slider and the helpers in the details editor.</summary>
    private UIElement MakeNamedSliderRow(string label, double min, double max,
                                         double current, Action<double> onChange)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 6, 14, 0), Spacing = 1 };
        var head = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };
        var valLbl = new TextBlock
        {
            Text = FormatEffectNumber(current),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        };
        head.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        head.Children.Add(valLbl);
        Grid.SetColumn(valLbl, 1);

        panel.Children.Add(head);
        panel.Children.Add(MakeBareSlider(min, max, current, v =>
        {
            valLbl.Text = FormatEffectNumber(v);
            onChange(v);
        }));
        return panel;
    }

    /// <summary>"Time" row in the keyframe details editor — slider 0..1 with a
    /// live absolute-timecode label beside it so the user can see where on the
    /// timeline the keyframe sits while dragging.</summary>
    private UIElement MakeKeyframeTimeRow(TimelineClip clip, EffectKeyframe kf)
    {
        var panel = new StackPanel { Padding = new Thickness(14, 6, 14, 4), Spacing = 1 };
        var head = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var lbl = new TextBlock
        {
            Text = "Time",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };
        TimeSpan AbsTime(double rel) =>
            clip.TimelineStart + TimeSpan.FromSeconds(rel * clip.Duration.TotalSeconds);
        var valLbl = new TextBlock
        {
            Text = FormatHms(AbsTime(kf.TimeRel)),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        };
        head.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        head.Children.Add(valLbl);
        Grid.SetColumn(valLbl, 1);

        panel.Children.Add(head);
        panel.Children.Add(MakeBareSlider(0, 1, kf.TimeRel, v =>
        {
            kf.TimeRel = Math.Clamp(v, 0, 1);
            valLbl.Text = FormatHms(AbsTime(kf.TimeRel));
            if (_vm is not null) _vm.Project.IsModified = true;
            Timeline.Refresh();
        }));
        return panel;
    }

    private UIElement MakeAutomationsToolbar(TimelineClip clip,
        IReadOnlyList<(ClipEffect Fx, string Key, EffectKeyframe Kf)> rows)
    {
        var selCount = _vm?.SelectedKeyframes.Count ?? 0;

        var summary = new TextBlock
        {
            Text = $"{rows.Count} keyframe(s) · {selCount} selected",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var delBtn = MakeBulkActionButton("Delete", selCount > 0, () => BulkDeleteSelectedKeyframes(clip));
        var nudgeBackBtn = MakeBulkActionButton("◀ 1f", selCount > 0, () => BulkNudgeSelectedKeyframes(clip, -1));
        var nudgeFwdBtn = MakeBulkActionButton("1f ▶", selCount > 0, () => BulkNudgeSelectedKeyframes(clip, +1));
        var setValBtn = MakeBulkActionButton("Set value…", selCount > 0, () => BulkSetSelectedKeyframeValue(clip));
        var clearBtn = MakeBulkActionButton("Clear sel.", selCount > 0, () => { _vm?.ClearKeyframeSelection(); UpdateInspector(clip); Timeline.Refresh(); });

        actions.Children.Add(nudgeBackBtn);
        actions.Children.Add(nudgeFwdBtn);
        actions.Children.Add(setValBtn);
        actions.Children.Add(delBtn);
        actions.Children.Add(clearBtn);

        var grid = new Grid
        {
            Padding = new Thickness(14, 4, 14, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        grid.Children.Add(summary);
        Grid.SetColumn(summary, 0);
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 1);

        var outer = new StackPanel { Spacing = 0 };
        outer.Children.Add(grid);
        return outer;
    }

    private Button MakeBulkActionButton(string label, bool enabled, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 10,
            Padding = new Thickness(6, 1, 6, 1),
            MinWidth = 0,
            IsEnabled = enabled,
            Style = (Style)Application.Current.Resources["GhostButtonStyle"],
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private UIElement MakeAutomationRow(TimelineClip clip, ClipEffect fx, string paramKey, EffectKeyframe kf)
    {
        bool selected = _vm?.SelectedKeyframes.Contains(kf) == true;
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var hairline = (Brush)Application.Current.Resources["HairlineBrush"];

        // Lookup the parameter's label + range so we can display friendly names
        // and constrain the inline value editor.
        var allParams = ClipEffectsChain.NumberParams(fx.Name);
        var paramSchema = allParams.FirstOrDefault(p => p.Key == paramKey);
        string paramLabel = !string.IsNullOrEmpty(paramSchema.Label) ? paramSchema.Label : paramKey;

        // Stable per-parameter accent hue (matches the timeline diamond hue) so
        // a user can connect the row to the marker on the clip visually.
        var hue = KeyframeHueFor(fx.Name, paramKey);

        var accentColor = accent is SolidColorBrush sb
            ? Color.FromArgb(70, sb.Color.R, sb.Color.G, sb.Color.B)
            : Color.FromArgb(70, 127, 176, 105);
        var border = new Border
        {
            Background = selected ? new SolidColorBrush(accentColor) : null,
            BorderBrush = selected ? accent : hairline,
            BorderThickness = new Thickness(selected ? 1.5 : 0, 0, 0, 1),
            Padding = new Thickness(14, 5, 8, 5),
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(10) },                          // hue dot
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },        // effect · param
                new ColumnDefinition { Width = new GridLength(48) },                          // time
                new ColumnDefinition { Width = new GridLength(56) },                          // value
            },
        };

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(hue),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(dot);
        Grid.SetColumn(dot, 0);

        var nameLbl = new TextBlock
        {
            Text = $"{fx.Name} · {paramLabel}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 6, 0),
        };
        row.Children.Add(nameLbl);
        Grid.SetColumn(nameLbl, 1);

        var timeLbl = new TextBlock
        {
            Text = FormatHms(clip.TimelineStart + TimeSpan.FromSeconds(kf.TimeRel * clip.Duration.TotalSeconds)),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        row.Children.Add(timeLbl);
        Grid.SetColumn(timeLbl, 2);

        var valLbl = new TextBlock
        {
            Text = FormatEffectNumber(kf.Value),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
        };
        row.Children.Add(valLbl);
        Grid.SetColumn(valLbl, 3);

        border.Child = row;

        // Click to select. Ctrl/Shift = additive. Also jumps the playhead to the
        // keyframe so the user can see the value applied in the preview.
        border.PointerPressed += (_, e) =>
        {
            if (_vm is null) return;
            var ctrl  = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            _vm.SetKeyframeSelection(kf, ctrl || shift);
            _vm.Project.Playhead = clip.TimelineStart + TimeSpan.FromSeconds(kf.TimeRel * clip.Duration.TotalSeconds);
            UpdateInspector(clip);
            Timeline.Refresh();
            e.Handled = true;
        };

        return border;
    }

    // Hue helper moved to BTAP.Services.KeyframeColors so TimelineControl can
    // share the same palette for the diamond overlays. Local alias for brevity.
    private static Color KeyframeHueFor(string effectName, string paramKey) =>
        KeyframeColors.HueFor(effectName, paramKey);

    private void BulkDeleteSelectedKeyframes(TimelineClip clip)
    {
        if (_vm is null || _vm.SelectedKeyframes.Count == 0) return;
        var targets = _vm.SelectedKeyframes.ToList();
        foreach (var fx in clip.Effects)
        {
            foreach (var key in fx.Keyframes.Keys.ToList())
            {
                var list = fx.Keyframes[key];
                for (int i = list.Count - 1; i >= 0; i--)
                    if (targets.Contains(list[i])) list.RemoveAt(i);
                if (list.Count == 0) fx.Keyframes.Remove(key);
            }
        }
        _vm.SelectedKeyframes.Clear();
        _vm.Project.IsModified = true;
        UpdateInspector(clip);
        Timeline.Refresh();
    }

    private void BulkNudgeSelectedKeyframes(TimelineClip clip, int frames)
    {
        if (_vm is null || _vm.SelectedKeyframes.Count == 0) return;
        if (clip.Duration.TotalSeconds <= 0) return;
        double deltaRel = (frames / Math.Max(1, _vm.Project.FrameRate)) / clip.Duration.TotalSeconds;
        foreach (var kf in _vm.SelectedKeyframes)
            kf.TimeRel = Math.Clamp(kf.TimeRel + deltaRel, 0, 1);
        _vm.Project.IsModified = true;
        UpdateInspector(clip);
        Timeline.Refresh();
    }

    private async void BulkSetSelectedKeyframeValue(TimelineClip clip)
    {
        if (_vm is null || _vm.SelectedKeyframes.Count == 0) return;
        var dialog = new ContentDialog
        {
            Title = "Set value for selected keyframes",
            CloseButtonText = "Cancel",
            PrimaryButtonText = "Apply",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var box = new TextBox
        {
            PlaceholderText = "Numeric value (e.g. 25 or 0.5)",
            Text = FormatEffectNumber(_vm.SelectedKeyframes.First().Value),
        };
        dialog.Content = box;
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        if (!double.TryParse(box.Text, System.Globalization.CultureInfo.InvariantCulture, out double value)) return;
        foreach (var kf in _vm.SelectedKeyframes) kf.Value = value;
        _vm.Project.IsModified = true;
        UpdateInspector(clip);
        Timeline.Refresh();
    }

    /// <summary>Adds a keyframe to the given parameter at the current playhead,
    /// using the currently-interpolated value as the initial keyframe value (so
    /// adding a keyframe doesn't visually change the effect at that moment).</summary>
    public void AddKeyframeAtPlayhead(TimelineClip clip, ClipEffect fx, string paramKey)
    {
        if (_vm is null) return;
        if (clip.Duration.TotalSeconds <= 0) return;
        double timeRel = Math.Clamp((_vm.Project.Playhead - clip.TimelineStart).TotalSeconds / clip.Duration.TotalSeconds, 0, 1);
        var schema = ClipEffectsChain.NumberParams(fx.Name).FirstOrDefault(p => p.Key == paramKey);
        double @default = !string.IsNullOrEmpty(schema.Key) ? schema.Default : 0;
        double value = fx.GetAutomatedNumber(paramKey, timeRel, @default);

        if (!fx.Keyframes.TryGetValue(paramKey, out var list))
        {
            list = new System.Collections.ObjectModel.ObservableCollection<EffectKeyframe>();
            fx.Keyframes[paramKey] = list;
        }
        // Avoid stacking duplicates exactly at the same time — replace instead.
        var existing = list.FirstOrDefault(k => Math.Abs(k.TimeRel - timeRel) < 0.0005);
        if (existing is not null) existing.Value = value;
        else list.Add(new EffectKeyframe { TimeRel = timeRel, Value = value });

        _vm.Project.IsModified = true;
        UpdateInspector(clip);
        Timeline.Refresh();
    }

    /// <summary>Click-to-enable behavior used by the media-library Effects panel
    /// cards. Adds the effect to the selected clip with defaults, or re-enables it
    /// if it was previously toggled off (keeping the user's tuned parameters).</summary>
    private void AddEffectToSelected(string effectName)
    {
        if (_vm?.SelectedClip is not { } clip) return;
        var existing = clip.Effects.FirstOrDefault(fx => fx.Name == effectName);
        if (existing is not null)
            existing.Enabled = true;
        else
            clip.Effects.Add(new ClipEffect { Name = effectName, Enabled = true });
        _vm.Project.IsModified = true;
        UpdateInspector(clip);
    }

    private UIElement MakeEffectToggleRow(TimelineClip clip, string effectName)
    {
        var fx = clip.Effects.FirstOrDefault(f => f.Name == effectName);
        bool isOn = fx is { Enabled: true };

        var outer = new StackPanel { Spacing = 0 };

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Padding = new Thickness(14, 4, 8, 2),
        };

        var label = new TextBlock
        {
            Text = effectName,
            FontSize = 11,
            FontWeight = isOn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = isOn
                ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextDimBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Clear OnContent/OffContent so the switch reads as a compact pill without
        // the default "On"/"Off" text taking ~40px of horizontal space.
        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Toggled += (_, _) =>
        {
            SetEffectEnabledOnClip(clip, effectName, toggle.IsOn);
            UpdateInspector(clip);
        };

        header.Children.Add(label);
        Grid.SetColumn(label, 0);
        header.Children.Add(toggle);
        Grid.SetColumn(toggle, 1);

        outer.Children.Add(header);

        if (isOn && fx is not null)
        {
            var optionsContainer = new Border
            {
                Margin = new Thickness(14, 2, 14, 8),
                Padding = new Thickness(10, 6, 10, 8),
                Background = new SolidColorBrush(Color.FromArgb(40, 14, 22, 28)),
                CornerRadius = new CornerRadius(4),
            };
            var options = new StackPanel { Spacing = 6 };
            AppendEffectOptions(options, fx);
            optionsContainer.Child = options;
            outer.Children.Add(optionsContainer);
        }

        return outer;
    }

    private void SetEffectEnabledOnClip(TimelineClip clip, string effectName, bool enabled)
    {
        var fx = clip.Effects.FirstOrDefault(f => f.Name == effectName);
        if (enabled)
        {
            if (fx is null)
                clip.Effects.Add(new ClipEffect { Name = effectName, Enabled = true });
            else
                fx.Enabled = true;
        }
        else if (fx is not null)
        {
            fx.Enabled = false;
        }
        if (_vm is not null) _vm.Project.IsModified = true;
    }

    private void AppendEffectOptions(StackPanel parent, ClipEffect fx)
    {
        foreach (var sp in ClipEffectsChain.StringParams(fx.Name))
            parent.Children.Add(MakeEffectColorRow(fx, sp));

        foreach (var np in ClipEffectsChain.NumberParams(fx.Name))
            parent.Children.Add(MakeEffectNumberSlider(fx, np));

        if (ClipEffectsChain.NumberParams(fx.Name).Count == 0
            && ClipEffectsChain.StringParams(fx.Name).Count == 0)
        {
            parent.Children.Add(new TextBlock
            {
                Text = "No parameters",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
        }
    }

    /// <summary>Static parameter slider for the Effects tab. Keyframe management
    /// lives entirely in the Automations tab — right-click the clip on the
    /// timeline → Add keyframe at playhead to create one. When a parameter has
    /// keyframes the slider here only edits the fallback (used when no keyframes
    /// exist), so we mute its visuals and append an "Animated" badge so the user
    /// knows the live value comes from the Automations tab.</summary>
    private UIElement MakeEffectNumberSlider(ClipEffect fx, ClipEffectsChain.NumberParam p)
    {
        bool isAnimated = fx.IsAnimated(p.Key);
        var panel = new StackPanel { Spacing = 1 };

        var labelRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var lbl = new TextBlock
        {
            Text = p.Label,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources[
                isAnimated ? "TextFaintBrush" : "TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        double current = fx.GetNumber(p.Key, p.Default);

        var valLbl = new TextBlock
        {
            Text = FormatEffectNumber(current),
            FontSize = 10,
            FontFamily = new FontFamily("JetBrains Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources[
                isAnimated ? "TextFaintBrush" : "TextDimBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, isAnimated ? 6 : 0, 0),
        };
        labelRow.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        labelRow.Children.Add(valLbl);
        Grid.SetColumn(valLbl, 1);

        if (isAnimated)
        {
            // Subtle accent pill so users can tell at a glance that this
            // parameter's live value is being driven by keyframes elsewhere.
            var badge = new Border
            {
                Background = (Brush)Application.Current.Resources["AccentSoftBrush"],
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "ANIMATED",
                    FontSize = 8.5,
                    CharacterSpacing = 80,
                    Foreground = (Brush)Application.Current.Resources["AccentInkBrush"],
                },
            };
            ToolTipService.SetToolTip(badge, "Live value is driven by keyframes — edit them in the Automations tab.");
            labelRow.Children.Add(badge);
            Grid.SetColumn(badge, 2);
        }

        panel.Children.Add(labelRow);

        var slider = MakeBareSlider(p.Min, p.Max, current, v =>
        {
            fx.SetNumber(p.Key, v);
            valLbl.Text = FormatEffectNumber(v);
            if (_vm is not null) _vm.Project.IsModified = true;
        });
        // When the parameter is animated, the static value is a fallback that
        // doesn't affect the rendered frame — dim the slider to signal that.
        if (isAnimated) slider.Opacity = 0.45;
        panel.Children.Add(slider);
        return panel;
    }

    /// <summary>Compact slider with no surrounding label — used as the inner
    /// editor inside MakeEffectNumberSlider and the Automations details rows.</summary>
    private Slider MakeBareSlider(double min, double max, double value, Action<double> onChange)
    {
        double range = max - min;
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value   = value,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            StepFrequency = range > 20 ? 1 : 0.01,
            SmallChange   = range > 20 ? 1 : 0.01,
            Margin = new Thickness(0, -4, 0, -8),
        };
        slider.ValueChanged += (_, ev) => onChange(ev.NewValue);
        return slider;
    }

    private static string FormatEffectNumber(double v) =>
        Math.Abs(v) >= 10 ? v.ToString("F0") : v.ToString("F2");

    private UIElement MakeEffectColorRow(ClipEffect fx, ClipEffectsChain.StringParam p)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = p.Label,
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        string current = fx.GetString(p.Key, p.Default);

        // Current-color swatch (shows whatever's set — wheel/eyedrop pick won't
        // match a preset, so this is the only swatch that always reflects truth).
        var currentBrush = ParseColorBrush(current) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        var currentSwatch = new Border
        {
            Width = 22,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            Background = currentBrush,
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(2),
        };
        ToolTipService.SetToolTip(currentSwatch, current);
        row.Children.Add(currentSwatch);

        // Thin separator between the live swatch and the preset palette.
        row.Children.Add(new Border
        {
            Width = 1,
            Height = 16,
            Background = (Brush)Application.Current.Resources["HairlineBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        });

        foreach (var (lbl, hex) in EffectColorSwatches)
        {
            var fill = ParseColorBrush(hex) ?? new SolidColorBrush(Microsoft.UI.Colors.White);
            bool isSelected = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = fill,
                BorderBrush = isSelected
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : (Brush)Application.Current.Resources["HairlineBrush"],
                BorderThickness = new Thickness(isSelected ? 2 : 1),
            };
            ToolTipService.SetToolTip(swatch, lbl);
            var hexLocal = hex;
            swatch.Tapped += (_, _) =>
            {
                fx.SetString(p.Key, hexLocal);
                if (_vm is not null) _vm.Project.IsModified = true;
                if (_vm?.SelectedClip is { } sel) UpdateInspector(sel);
            };
            row.Children.Add(swatch);
        }

        // Wheel button → opens a Flyout containing the WinUI ColorPicker so the
        // user can pick any color outside the preset palette.
        var wheelBtn = new Button
        {
            Content = MakeColorWheelIcon(),
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 22,
            Height = 20,
            Style = (Style)Application.Current.Resources["GhostButtonStyle"],
        };
        ToolTipService.SetToolTip(wheelBtn, "Color wheel");
        var picker = new ColorPicker
        {
            Color = ParseColor(current),
            IsAlphaEnabled = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsMoreButtonVisible = true,
        };
        picker.ColorChanged += (_, e) =>
        {
            string hex = $"#{e.NewColor.A:X2}{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            fx.SetString(p.Key, hex);
            currentSwatch.Background = new SolidColorBrush(e.NewColor);
            ToolTipService.SetToolTip(currentSwatch, hex);
            if (_vm is not null) _vm.Project.IsModified = true;
        };
        var wheelFlyout = new Flyout
        {
            Content = picker,
            Placement = FlyoutPlacementMode.Bottom,
        };
        wheelFlyout.Closed += (_, _) =>
        {
            if (_vm?.SelectedClip is { } sel) UpdateInspector(sel); // refresh swatch highlight
        };
        wheelBtn.Flyout = wheelFlyout;
        row.Children.Add(wheelBtn);

        // Eye-dropper button → sample a pixel from the live preview by clicking
        // anywhere on it. Lets the user pick the actual green of a chroma-keyed
        // background instead of guessing from presets.
        var eyedropBtn = new Button
        {
            Content = MakeEyedropperIcon(),
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 22,
            Height = 20,
            Style = (Style)Application.Current.Resources["GhostButtonStyle"],
        };
        ToolTipService.SetToolTip(eyedropBtn, "Eye-dropper · click the preview to sample");
        eyedropBtn.Click += (_, _) => BeginEyedrop(color =>
        {
            string hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            fx.SetString(p.Key, hex);
            if (_vm is not null) _vm.Project.IsModified = true;
            if (_vm?.SelectedClip is { } sel) UpdateInspector(sel);
        });
        row.Children.Add(eyedropBtn);

        panel.Children.Add(row);
        return panel;
    }

    private static Color ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Microsoft.UI.Colors.White;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return Microsoft.UI.Colors.White;
        if (!byte.TryParse(s[..2],            System.Globalization.NumberStyles.HexNumber, null, out var a)) return Microsoft.UI.Colors.White;
        if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return Microsoft.UI.Colors.White;
        if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return Microsoft.UI.Colors.White;
        if (!byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return Microsoft.UI.Colors.White;
        return Color.FromArgb(a, r, g, b);
    }

    private static readonly (string Label, string Hex)[] EffectColorSwatches =
    {
        ("Black",   "#FF000000"),
        ("White",   "#FFFFFFFF"),
        ("Green",   "#FF00FF00"),
        ("Blue",    "#FF0000FF"),
        ("Red",     "#FFFF0000"),
        ("Cyan",    "#FF00FFFF"),
        ("Magenta", "#FFFF00FF"),
        ("Yellow",  "#FFFFFF00"),
    };

    /// <summary>Tiny rainbow-circle icon that conveys "pick any color." Drawn from
    /// XAML primitives instead of a font glyph so it renders the same on every
    /// machine regardless of installed icon fonts.</summary>
    private static UIElement MakeColorWheelIcon()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint   = new Windows.Foundation.Point(1, 1),
        };
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255,  80,  80), Offset = 0.00 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 200,  60), Offset = 0.20 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 110, 220, 110), Offset = 0.40 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255,  80, 200, 220), Offset = 0.60 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 110, 110, 230), Offset = 0.80 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 220,  90, 200), Offset = 1.00 });

        return new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = gradient,
            Stroke = (Brush)Application.Current.Resources["HairlineStrongBrush"],
            StrokeThickness = 1,
        };
    }

    /// <summary>Drawn eye-dropper glyph (rotated dropper outline + tip). Same
    /// drawn-from-XAML reasoning as the wheel icon.</summary>
    private static UIElement MakeEyedropperIcon()
    {
        var stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var canvas = new Microsoft.UI.Xaml.Controls.Canvas
        {
            Width = 14,
            Height = 14,
        };

        // Barrel: thick diagonal line from upper-left to lower-right.
        var barrel = new Line
        {
            X1 = 3, Y1 = 3, X2 = 10, Y2 = 10,
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
        };
        canvas.Children.Add(barrel);

        // Bulb: small square on the upper-left end of the barrel, rotated 45° so
        // it reads as the dropper's reservoir.
        var bulb = new Rectangle
        {
            Width = 6,
            Height = 4,
            Fill = stroke,
            RadiusX = 1,
            RadiusY = 1,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = 45 },
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(bulb, -1);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(bulb, 1);
        canvas.Children.Add(bulb);

        // Tip: small accent-colored droplet at the lower-right end.
        var tip = new Ellipse
        {
            Width = 3,
            Height = 3,
            Fill = (Brush)Application.Current.Resources["AccentBrush"],
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(tip, 9.5);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(tip, 9.5);
        canvas.Children.Add(tip);

        return canvas;
    }

    /// <summary>Enter eye-dropper mode: drops a transparent capture overlay over
    /// PreviewArea with a hint banner. The next click samples the pixel at that
    /// position from VideoCompositor's rendered output and invokes
    /// <paramref name="onPicked"/>. Esc cancels.</summary>
    private void BeginEyedrop(Action<Color> onPicked)
    {
        EndEyedrop(); // collapse any prior session so callbacks don't stack

        _eyedropCallback = onPicked;

        var overlay = new Border
        {
            // Near-transparent fill (alpha 1) so the overlay is hit-testable —
            // a fully-Transparent background would let clicks fall through.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            IsHitTestVisible = true,
        };

        var banner = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(220, 14, 22, 28)),
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "💧 Click anywhere on the preview to sample · Esc to cancel",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            },
        };
        overlay.Child = banner;

        overlay.PointerPressed += OnEyedropOverlayPressed;

        PreviewArea.Children.Add(overlay);
        _eyedropOverlay = overlay;
    }

    private void OnEyedropOverlayPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_eyedropOverlay is null || _eyedropCallback is null) { EndEyedrop(); return; }

        // Translate the click from the overlay's coord space to VideoCompositor's
        // so the pixel sample lands on the same point the user clicked.
        Color? color = null;
        try
        {
            var transform = _eyedropOverlay.TransformToVisual(VideoCompositor);
            var ptInComp = transform.TransformPoint(e.GetCurrentPoint(_eyedropOverlay).Position);
            color = VideoCompositor.SamplePixelAt(ptInComp);
        }
        catch { }

        var cb = _eyedropCallback;
        EndEyedrop();
        if (color is { } c) cb?.Invoke(c);
        e.Handled = true;
    }

    private void EndEyedrop()
    {
        if (_eyedropOverlay is not null)
        {
            _eyedropOverlay.PointerPressed -= OnEyedropOverlayPressed;
            PreviewArea.Children.Remove(_eyedropOverlay);
            _eyedropOverlay = null;
        }
        _eyedropCallback = null;
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
        // Insert above other video tracks but below any title tracks so titles stay on top.
        int insertIdx = 0;
        while (insertIdx < _vm.Tracks.Count && _vm.Tracks[insertIdx].Kind == TrackKind.Title)
            insertIdx++;
        _vm.Tracks.Insert(insertIdx, new Track { Label = $"V{n}", Kind = TrackKind.Video });
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

    private async void OnHelpShortcuts(object sender, RoutedEventArgs e)
    {
        var customizer = new BTAP.Controls.KeyboardCustomizerControl();
        customizer.Attach(_keyBindings);
        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = customizer,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        // ContentDialog's default Max{Width,Height} (548 × 756) would crop the
        // visual keyboard. These resource overrides widen the dialog enough to
        // show the unbound column + keyboard + side panel without scrolling.
        dialog.Resources["ContentDialogMaxWidth"]  = 1400d;
        dialog.Resources["ContentDialogMaxHeight"] = 820d;
        dialog.Resources["ContentDialogMinWidth"]  = 1340d;
        dialog.Resources["ContentDialogMinHeight"] = 680d;

        // Page-level KeyboardAccelerators fire window-wide; during the
        // listen-for-key flow they'd hijack the user's keystrokes. Clear them
        // while the dialog is up and rebuild on close.
        KeyboardAccelerators.Clear();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            RebuildKeyboardAccelerators();
        }
    }

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
        // The CompositionEffectBrush shares its DirectX device with the export-time
        // compositor; leaving it active has been observed to silently drop the video stream.
        bool wasEffectsAttached = _previewEffects.IsAttached;
        if (wasEffectsAttached) _previewEffects.Detach();

        // Also tear down the preview compositor so its per-source MediaPlayers release
        // exclusive locks on the source files — the export pool needs to reopen them.
        try { VideoCompositor?.Detach(); } catch { }

        using var logger = new ExportLogger(file.Path);

        try
        {
            await RunCustomExportAsync(file, logger);
        }
        finally
        {
            if (_vm is not null)
            {
                try { VideoCompositor?.Attach(_vm); } catch { }
            }
            if (wasEffectsAttached)
            {
                _previewEffects.Attach(ColorGradingLayer);
                _previewEffects.Apply(_presentedClip);
            }
        }
    }

    private async Task RunCustomExportAsync(StorageFile destination, ExportLogger log)
    {
        if (_vm is null) return;

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
            Children = { pathLabel, progressBar, statusLabel },
        };
        var dialog = new ContentDialog
        {
            Title = "Exporting",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.None,
        };

        var cts = new CancellationTokenSource();
        dialog.CloseButtonClick += (_, _) => { try { cts.Cancel(); } catch { } };

        var progress = new Progress<double>(fraction =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                progressBar.Value = Math.Clamp(fraction * 100.0, 0, 100);
                statusLabel.Text = fraction < 0.85
                    ? $"Rendering video… {fraction * 100:F0}%"
                    : $"Muxing audio… {fraction * 100:F0}%";
            });
        });

        var dialogTask = dialog.ShowAsync().AsTask();
        var renderStart = DateTime.Now;

        ExportRenderer.Result result;
        try
        {
            result = await ExportRenderer.RenderAsync(_vm.Project, destination, progress, log, cts.Token);
        }
        catch (Exception ex)
        {
            log.Log($"Unhandled exception during export: {ex}");
            result = new ExportRenderer.Result { Error = ex.Message };
        }

        log.Log($"Total render time: {(DateTime.Now - renderStart).TotalSeconds:F1}s");

        dialog.Hide();
        await dialogTask;

        if (cts.IsCancellationRequested)
        {
            log.Log("User cancelled — deleting partial file.");
            try { await destination.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            return;
        }

        if (!result.Success)
        {
            await ShowDialog("Export failed", $"{result.Error ?? "Unknown error."}\n\nLog: {log.FilePath}");
            return;
        }

        // Verify the output actually has a video stream
        if (!await ExportService.OutputHasVideoAsync(destination))
        {
            await ShowDialog(
                "Export missing video",
                "The renderer wrote the file but it doesn't contain a video stream. " +
                $"This is unexpected with the custom pipeline. See log: {log.FilePath}");
            return;
        }

        await ShowExportCompleteDialog(destination, result, log.FilePath);
    }

    private async Task ShowExportCompleteDialog(StorageFile file, ExportRenderer.Result build, string? logPath = null)
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
