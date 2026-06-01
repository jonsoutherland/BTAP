using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using BTAP.Models;
using BTAP.Services;
using BTAP.ViewModels;
using Windows.System;

namespace BTAP.Controls;

public sealed partial class TimelineControl : UserControl
{
    private const double HeaderWidth = 96.0;
    private const double RulerHeight = 24.0;
    private const double VideoTrackHeight = 54.0;
    private const double AudioTrackHeight = 40.0;
    private const double TitleTrackHeight = 36.0;
    private const double BasePixelsPerSec = 40.0;
    private const double EdgeHitZone = 8.0;
    private const double TrackInsertZone = 14.0;  // drop-here-for-new-track region above & below the stack

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(EditorViewModel),
            typeof(TimelineControl), new PropertyMetadata(null, OnViewModelChanged));

    public EditorViewModel? ViewModel
    {
        get => (EditorViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public event EventHandler<TimelineClip>? ClipTapped;
    public event EventHandler? SelectionCleared;
    public event EventHandler<TimeSpan>? PlayheadChanged;
    public event EventHandler<TimelineClip>? ClipDeleted;
    public event EventHandler<double>? ZoomChanged;

    /// <summary>Raised when the user picks a parameter from the clip's right-click
    /// "Add keyframe at playhead" submenu. EditorPage handles the actual model
    /// mutation so the inspector + history stay in sync.</summary>
    public event EventHandler<(TimelineClip Clip, ClipEffect Fx, string ParamKey)>? AddKeyframeRequested;

    /// <summary>Raised when keyframe selection changes by clicking a diamond — so
    /// EditorPage can refresh the Automations list.</summary>
    public event EventHandler? KeyframeSelectionChanged;

    private double _pixelsPerSec = BasePixelsPerSec;
    private Line? _playheadLine;
    private readonly List<(double Y, double Height)> _trackRects = [];

    private bool _isLoaded;
    // Re-entrancy guard. ClipCanvas.Children.Clear() inside BuildTracks() can
    // synchronously fire PointerCaptureLost on a captured Border, whose handler
    // also calls Rebuild(). Without this flag the outer Rebuild's foreach would
    // resume after the inner finished and double-populate ClipCanvas + _trackRects.
    private bool _isRebuilding;

    // Drag state
    private TimelineClip? _dragClip;
    private Track?         _dragTrack;
    private Border?        _dragBorder;
    private double         _dragOriginX;
    private double         _dragOriginClipSec;
    private TimeSpan       _dragFromStart;
    private bool           _isDragging;

    // Vertical-drag (cross-track) state
    private Track?         _dragOriginalTrack;     // where the clip lived when the drag started
    private int            _dragOriginalIndex;     // its index in OriginalTrack.Clips, for undo
    private Track?         _dragHoverTrack;        // last track the cursor was over (for visual snap)
    private double         _dragOriginalBorderY;   // Canvas.Top of the border at press
    private double         _dragOriginY;           // cursor Y in ClipCanvas coords at press
    private Microsoft.UI.Xaml.Shapes.Rectangle? _dropTargetHighlight; // tinted rect over the hovered track row

    // When the user drags above the topmost track or below the bottommost track, we
    // commit the drop as "create a new track and reparent." -1 / +1 indicate top/bottom.
    private int            _dragInsertNewTrackDir; // 0 = no insert, -1 = above all, +1 = below all

    // Trim state
    private bool           _isTrimMode;
    private bool           _trimFromLeft;
    private TimeSpan       _trimOriginalStart;
    private TimeSpan       _trimOriginalDuration;
    private TimeSpan       _trimOriginalSourceStart;
    // Per-keyframe absolute source-time captured at trim-begin. Each trim move
    // re-derives TimeRel from this so keyframes stay anchored to the underlying
    // source content (and aren't dragged along by a proportional reflow that
    // would shift them when the clip is shortened/lengthened).
    private Dictionary<EffectKeyframe, double>? _trimKfSourceTimes;

    // Keyframe-diamond drag state. Press captures the diamond; if pointer moves
    // past KfDragThresholdPx the drag mutates the kf's TimeRel; release without
    // crossing the threshold falls back to single-click selection semantics.
    private Rectangle?       _kfDragDiamond;
    private EffectKeyframe?  _kfDragKf;
    private TimelineClip?    _kfDragClip;
    private Canvas?          _kfDragCanvas;
    private double           _kfDragStartPointerX;
    private bool             _kfDragMoved;
    private const double     KfDragThresholdPx = 3.0;

    // Drop indicator
    private Line?          _dropIndicator;
    private Line?          _trackInsertIndicator;

    // Razor (slice) state
    private bool           _isRazoring;
    private TimelineClip?  _razorClip;
    private Track?         _razorTrack;
    private Line?          _razorLine;

    // Multi-clip drag — captured at press if the pressed clip belongs to a multi-selection
    private List<(TimelineClip Clip, Border Border, TimeSpan OrigStart)>? _multiDragItems;

    // Marquee (rubber-band) selection state
    private bool       _isMarquee;
    private bool       _marqueeAdditive;
    private double     _marqueeOriginX;
    private double     _marqueeOriginY;
    private Rectangle? _marqueeRect;

    public TimelineControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            BTAP.Services.WaveformService.PeaksReady += OnWaveformPeaksReady;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            BTAP.Services.WaveformService.PeaksReady -= OnWaveformPeaksReady;
        };
    }

    private void OnWaveformPeaksReady(object? sender, string filePath)
    {
        // The peak array is now cached. Rebuild so any clip referencing this file
        // picks it up. Rebuild is on the UI thread; the service fires from any thread.
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isLoaded) return;
            // A drag in progress has the pointer captured by one of ClipCanvas's
            // Border children. Tearing down ClipCanvas.Children mid-drag would
            // synchronously fire PointerCaptureLost into a partially-rebuilt visual
            // tree, re-enter Rebuild, and corrupt _trackRects / the child list.
            // The waveform will be picked up by the natural rebuild on release.
            if (_dragClip is not null || _isRazoring) return;
            Rebuild();
        });
    }

    public void SetZoom(double zoom)
    {
        _pixelsPerSec = BasePixelsPerSec * zoom;
        Rebuild();
    }

    /// <summary>Force a rebuild after the ViewModel's backing data changed.
    /// Needed because re-assigning the same ViewModel reference doesn't fire
    /// the DependencyProperty callback.</summary>
    public void Refresh() => Rebuild();

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TimelineControl ctrl) return;

        if (e.OldValue is EditorViewModel oldVm)
            oldVm.Project.Markers.CollectionChanged -= ctrl.OnMarkersChanged;
        if (e.NewValue is EditorViewModel newVm)
            newVm.Project.Markers.CollectionChanged += ctrl.OnMarkersChanged;

        ctrl.Rebuild();
    }

    private void OnMarkersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        BuildRuler();

    private void Rebuild()
    {
        if (!_isLoaded || ViewModel is null) return;
        if (_isRebuilding) return;
        _isRebuilding = true;
        try
        {
            BuildTracks();
            BuildRuler();
        }
        finally { _isRebuilding = false; }
    }

    private void BuildTracks()
    {
        TrackHeaders.Children.Clear();
        ClipCanvas.Children.Clear();
        _trackRects.Clear();

        var vm = ViewModel!;
        double totalSec = vm.Project.Duration.TotalSeconds;
        double totalWidth = totalSec * _pixelsPerSec + 200;

        // Spacer above the first track — the drop-to-insert zone in the header column
        TrackHeaders.Children.Add(new Border { Height = TrackInsertZone });

        double y = TrackInsertZone;
        foreach (var track in vm.Tracks)
        {
            double h = track.Kind switch
            {
                TrackKind.Video => VideoTrackHeight,
                TrackKind.Title => TitleTrackHeight,
                _               => AudioTrackHeight,
            };
            _trackRects.Add((y, h));

            AddTrackHeader(track, h);
            AddTrackSeparatorLine(y + h, totalWidth);

            foreach (var clip in track.Clips)
                AddClipRect(clip, track, y, h);

            y += h;
        }

        // Spacer below the last track — matches the canvas bottom drop zone
        TrackHeaders.Children.Add(new Border { Height = TrackInsertZone });

        ClipCanvas.Width = totalWidth;
        // Fill the visible timeline area below the ruler so the empty space under the
        // last track still accepts drops. Without this the canvas would be as short as
        // the track stack and an empty project would have a ~28px drop zone — too small
        // to discover, and any drop below it falls outside the canvas entirely.
        double tracksBottom = y + TrackInsertZone;
        double viewportFill = Math.Max(0, MainScroller.ViewportHeight - RulerHeight);
        ClipCanvas.Height = Math.Max(tracksBottom, Math.Max(viewportFill, 1));

        DrawPlayhead(vm.Project.Playhead.TotalSeconds * _pixelsPerSec);
    }

    private void AddTrackHeader(Track track, double height)
    {
        var label = new TextBlock
        {
            Text = track.Label,
            FontSize = 9.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var kindDot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = track.Kind switch
            {
                TrackKind.Video => new SolidColorBrush(Color.FromArgb(255, 45, 110, 180)),
                TrackKind.Title => new SolidColorBrush(Color.FromArgb(255, 200, 120, 60)),
                _               => new SolidColorBrush(Color.FromArgb(255, 45, 140, 80)),
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };

        var topRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Margin = new Thickness(8, 4, 4, 2),
        };
        topRow.Children.Add(kindDot);
        Grid.SetColumn(kindDot, 0);
        topRow.Children.Add(label);
        Grid.SetColumn(label, 1);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 0, 4, 4),
        };
        btnRow.Children.Add(MakeHeaderBtn("M", track.IsMuted, () => { track.IsMuted = !track.IsMuted; Rebuild(); }));
        btnRow.Children.Add(MakeHeaderBtn("S", track.IsSolo,  () => { track.IsSolo  = !track.IsSolo;  Rebuild(); }));
        btnRow.Children.Add(MakeHeaderBtn("L", track.IsLocked,() => { track.IsLocked= !track.IsLocked;Rebuild(); }));

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(topRow);
        stack.Children.Add(btnRow);

        var border = new Border
        {
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 53, 76, 90)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
        TrackHeaders.Children.Add(border);
    }

    private static Button MakeHeaderBtn(string text, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 8.5,
            Padding = new Thickness(4, 1, 4, 1),
            MinWidth = 0,
            MinHeight = 0,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(180, 127, 176, 105))
                : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Foreground = new SolidColorBrush(Color.FromArgb(active ? (byte)255 : (byte)140, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void AddTrackSeparatorLine(double y, double width)
    {
        var line = new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = y,
            Y2 = y,
            Stroke = new SolidColorBrush(Color.FromArgb(60, 53, 76, 90)),
            StrokeThickness = 1,
        };
        ClipCanvas.Children.Add(line);
    }

    private void AddClipRect(TimelineClip clip, Track track, double trackY, double trackHeight)
    {
        double x = clip.TimelineStart.TotalSeconds * _pixelsPerSec;
        double w = Math.Max(clip.Duration.TotalSeconds * _pixelsPerSec, 4);

        var (bgColor, borderColor) = GetClipColors(clip);
        bool isSelected = clip.IsSelected;
        bool isAudible  = IsTrackAudible(track);

        var label = new TextBlock
        {
            Text = clip.Label,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Waveform overlay (above background, below label) for clips that point at a
        // media file with audio. The bitmap is rendered at the clip's actual on-screen
        // pixel width so it stays crisp at any zoom level, and only the played-in slice
        // (SourceStart..SourceStart+Duration) is drawn. The image lives inside a Canvas
        // host so it can be sized larger than the clip's current Border width during a
        // trim drag without the layout system shrinking it; the Border's CornerRadius
        // takes care of clipping the visible overflow.
        var waveformImage = TryCreateWaveformImage(clip, w);

        var contentRoot = new Grid();
        if (waveformImage is not null)
        {
            double waveW = Math.Max(1, w - 4);
            double waveH = Math.Max(1, trackHeight - 11);
            waveformImage.Width  = waveW;
            waveformImage.Height = waveH;
            waveformImage.Margin = new Thickness(0);
            waveformImage.HorizontalAlignment = HorizontalAlignment.Left;
            waveformImage.VerticalAlignment   = VerticalAlignment.Top;
            Canvas.SetLeft(waveformImage, 2);
            Canvas.SetTop(waveformImage, 4);

            var waveformHost = new Canvas { IsHitTestVisible = false };
            waveformHost.Children.Add(waveformImage);
            contentRoot.Children.Add(waveformHost);
        }
        contentRoot.Children.Add(label);

        // Volume-automation overlay: horizontal line + keyframe circles. Sits on top
        // so its hit zones intercept clicks before they bubble up to the clip Border
        // (which would otherwise start a clip drag/trim).
        if (clip.Kind is ClipKind.Video or ClipKind.Audio or ClipKind.Music)
        {
            var envelope = BuildVolumeEnvelopeOverlay(clip, w, trackHeight - 3);
            contentRoot.Children.Add(envelope);
        }

        // Effect-parameter keyframe diamonds along the top edge of the clip — one
        // small rotated square per keyframe. Hue matches the Automations row dot
        // so the user can connect a row to a marker visually. Selected ones get a
        // brighter outline.
        var kfOverlay = BuildKeyframeDiamondOverlay(clip, w, trackHeight - 3);
        if (kfOverlay is not null) contentRoot.Children.Add(kfOverlay);

        var border = new Border
        {
            Width = w,
            Height = trackHeight - 3,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(bgColor),
            BorderBrush = new SolidColorBrush(isSelected
                ? Color.FromArgb(255, 127, 176, 105)
                : borderColor),
            BorderThickness = new Thickness(isSelected ? 1.5 : 1),
            Opacity = isAudible ? 1.0 : 0.35,
            Child = contentRoot,
            Tag = (clip, track),
        };

        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, trackY + 2);

        border.PointerPressed     += OnClipPointerPressed;
        border.PointerMoved       += OnClipPointerMoved;
        border.PointerReleased    += OnClipPointerReleased;
        border.PointerCaptureLost += OnClipPointerCaptureLost;
        border.PointerCanceled    += OnClipPointerCaptureLost;
        border.PointerExited      += OnClipPointerExited;

        // Right-click context menu
        var flyout = new MenuFlyout();
        var miSplit = new MenuFlyoutItem { Text = "Split at Playhead" };
        miSplit.Click += (_, _) => SplitClipAtPlayhead(clip, track);
        var miDup = new MenuFlyoutItem { Text = "Duplicate" };
        miDup.Click += (_, _) => DuplicateClip(clip, track);
        var miDel = new MenuFlyoutItem { Text = "Delete" };
        miDel.Click += (_, _) => DeleteClip(clip, track);
        flyout.Items.Add(miSplit);
        flyout.Items.Add(miDup);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildAddKeyframeSubmenu(clip));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(miDel);
        border.ContextFlyout = flyout;

        ClipCanvas.Children.Add(border);
    }

    // Shared hover cursors — InputSystemCursor.Create allocates a real OS handle on
    // every call so we keep static singletons rather than reallocating on each move.
    private static readonly InputCursor s_resizeEWCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor s_sizeAllCursor  = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor s_arrowCursor    = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    // Border is sealed in WinUI 3 so we can't subclass it to expose ProtectedCursor.
    // The setter is protected on UIElement; reach it via reflection (cached) instead.
    private static readonly MethodInfo? s_setProtectedCursor =
        typeof(UIElement).GetProperty("ProtectedCursor",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetSetMethod(nonPublic: true);

    private static void SetCursor(UIElement element, InputCursor cursor)
    {
        try { s_setProtectedCursor?.Invoke(element, new object[] { cursor }); }
        catch { /* shouldn't happen but never crash the UI over a cursor */ }
    }

    /// <summary>Pick the right cursor for the pointer's position over <paramref name="border"/>:
    /// resize-east-west on the left/right trim zones, size-all on the body, default cursor
    /// when the track is locked or the razor tool is active (which has its own slice affordance).</summary>
    private void UpdateHoverCursor(Border border, TimelineClip clip, Track track, double localX)
    {
        if (track.IsLocked || ViewModel?.ActiveTool == ActiveTool.Razor)
        {
            SetCursor(border, s_arrowCursor);
            return;
        }
        double w = border.Width;
        if (localX <= EdgeHitZone || localX >= w - EdgeHitZone)
            SetCursor(border, s_resizeEWCursor);
        else
            SetCursor(border, s_sizeAllCursor);
    }

    private void OnClipPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Only reset the cursor when the user wasn't mid-drag; if they were, the drag
        // session owns the cursor and will reset it on release.
        if (_dragClip is null && sender is Border b)
            SetCursor(b, s_arrowCursor);
    }

    // ── Trim visual cue ─────────────────────────────────────────────────
    // While trimming, paint a bright stripe on the trimming edge and recolor the
    // clip's outline so the user can tell at a glance that they're resizing the clip
    // rather than dragging it sideways.

    private static readonly Color   TrimAccentColor       = Color.FromArgb(255, 255, 200, 100);
    private static readonly Color   TrimAccentDarkColor   = Color.FromArgb(255, 200, 140, 50);
    private const           double  TrimHandleWidth       = 4.0;
    private Rectangle?              _trimHandleOverlay;
    private Brush?                  _trimOriginalBorderBrush;
    private Thickness?              _trimOriginalBorderThickness;

    // Frozen-waveform state — while a trim is in progress the image's Width stays
    // pinned (because its host Canvas doesn't impose a layout constraint), and a
    // TranslateTransform slides it for left-trims so the trim edges visibly cross an
    // unchanging waveform instead of watching the peaks rubber-band.
    private Image?                  _trimWaveformImage;
    private TranslateTransform?     _trimImageTransform;
    private double                  _trimOriginalBorderLeft;

    private void BeginTrimVisual(Border b, bool fromLeft)
    {
        // Stash the clip's original outline so EndTrimVisual can restore it.
        _trimOriginalBorderBrush     = b.BorderBrush;
        _trimOriginalBorderThickness = b.BorderThickness;
        b.BorderBrush = new SolidColorBrush(TrimAccentColor);
        b.BorderThickness = new Thickness(1.5);
        SetCursor(b, s_resizeEWCursor);

        if (_trimHandleOverlay is null)
        {
            _trimHandleOverlay = new Rectangle
            {
                Fill = new SolidColorBrush(TrimAccentColor),
                Stroke = new SolidColorBrush(TrimAccentDarkColor),
                StrokeThickness = 1,
                IsHitTestVisible = false,
                RadiusX = 1.5,
                RadiusY = 1.5,
            };
            ClipCanvas.Children.Add(_trimHandleOverlay);
        }
        UpdateTrimHandlePosition(b, fromLeft);
        Canvas.SetZIndex(_trimHandleOverlay, 150);

        FreezeWaveform(b);
    }

    private void UpdateTrimHandlePosition(Border b, bool fromLeft)
    {
        if (_trimHandleOverlay is not null)
        {
            double left = Canvas.GetLeft(b);
            double top  = Canvas.GetTop(b);
            double handleX = fromLeft ? left : left + b.Width - TrimHandleWidth;
            _trimHandleOverlay.Width  = TrimHandleWidth;
            _trimHandleOverlay.Height = Math.Max(2, b.Height);
            Canvas.SetLeft(_trimHandleOverlay, handleX);
            Canvas.SetTop(_trimHandleOverlay, top);
        }

        // For left-trim, slide the frozen waveform left inside the border by the
        // exact amount the border moved right, so the peaks stay anchored in
        // canvas-space and the new left edge passes over the unchanged waveform.
        if (_trimImageTransform is not null && fromLeft)
            _trimImageTransform.X = -(Canvas.GetLeft(b) - _trimOriginalBorderLeft);
    }

    private void FreezeWaveform(Border b)
    {
        // The waveform image lives in a Canvas inside the content Grid — Canvas is
        // chosen specifically because it does NOT layout-constrain its children, so
        // the image keeps its explicit Width during a trim even when the parent
        // Border shrinks. The Border's CornerRadius clips overflow visually.
        if (b.Child is not Grid grid) return;
        Image? img = null;
        foreach (var child in grid.Children)
        {
            if (child is Canvas host)
                foreach (var c in host.Children)
                    if (c is Image i) { img = i; break; }
            if (img is not null) break;
        }
        if (img is null) return;

        _trimWaveformImage      = img;
        _trimOriginalBorderLeft = Canvas.GetLeft(b);

        // Re-render the waveform across the FULL range the trim could expose, not just
        // the clip's current source extent. Otherwise the user can trim inward, release,
        // and then trim outward again — and the newly-uncovered portion of the clip has
        // no waveform pixels because the image was rendered at the smaller range.
        RenderExtendedWaveformForTrim(img);

        // Image.Width is already explicit (set in AddClipRect or RenderExtendedWaveformForTrim)
        // so its rendered size is locked. All we need is a TranslateTransform to slide it
        // for left-trim.
        _trimImageTransform = new TranslateTransform();
        img.RenderTransform = _trimImageTransform;
    }

    /// <summary>Replace the in-place waveform image with one rendered over the maximum
    /// span the current trim could reach: source [0, originalEnd] for a left-trim,
    /// or [originalStart, sourceTotalDuration] for a right-trim. Positioned so the
    /// pre-trim source-pixel mapping still aligns at the original edge, so the
    /// canvas-anchored sliding behavior is preserved.</summary>
    private void RenderExtendedWaveformForTrim(Image img)
    {
        if (_dragClip is null || ViewModel is null) return;
        if (string.IsNullOrEmpty(_dragClip.SourceId)) return;
        var media = ViewModel.MediaBin.FirstOrDefault(m => m.Id == _dragClip.SourceId);
        if (media is null || string.IsNullOrEmpty(media.FilePath)) return;
        var peaks = BTAP.Services.WaveformService.GetCachedPeaks(media.FilePath);
        if (peaks is null) return;

        double sourceTotalSec = peaks.Duration.TotalSeconds;
        double extStart, extEnd;
        if (_trimFromLeft)
        {
            extStart = 0;
            extEnd   = _trimOriginalSourceStart.TotalSeconds + _trimOriginalDuration.TotalSeconds;
        }
        else
        {
            extStart = _trimOriginalSourceStart.TotalSeconds;
            extEnd   = sourceTotalSec;
        }
        if (extEnd <= extStart) return;

        double extWidthPx = (extEnd - extStart) * _pixelsPerSec;
        if (extWidthPx < 1) return;

        var color = _dragClip.Kind == ClipKind.Audio || _dragClip.Kind == ClipKind.Music
            ? Color.FromArgb(230, 130, 220, 160)
            : Color.FromArgb(200, 230, 240, 250);

        // Bitmap is keyed off SourceStart/Duration in the rendering routine, so feed
        // it a synthetic clip describing the extended range.
        var synthClip = new TimelineClip
        {
            SourceStart = TimeSpan.FromSeconds(extStart),
            Duration    = TimeSpan.FromSeconds(extEnd - extStart),
        };
        img.Source = RenderWaveformBitmap(peaks, color, synthClip, extWidthPx);
        img.Width  = extWidthPx;

        // Re-anchor: the pixel for the original SourceStart should still sit at the
        // border-relative x=2 gutter (where AddClipRect originally placed the image).
        double pixelForOriginalStart = (_trimOriginalSourceStart.TotalSeconds - extStart) * _pixelsPerSec;
        Canvas.SetLeft(img, 2 - pixelForOriginalStart);
    }

    private void EndTrimVisual()
    {
        if (_dragBorder is Border b && _trimOriginalBorderBrush is not null)
        {
            b.BorderBrush = _trimOriginalBorderBrush;
            if (_trimOriginalBorderThickness.HasValue)
                b.BorderThickness = _trimOriginalBorderThickness.Value;
            SetCursor(b, s_arrowCursor);
        }
        _trimOriginalBorderBrush     = null;
        _trimOriginalBorderThickness = null;
        if (_trimHandleOverlay is not null)
        {
            ClipCanvas.Children.Remove(_trimHandleOverlay);
            _trimHandleOverlay = null;
        }

        if (_trimWaveformImage is not null)
            _trimWaveformImage.RenderTransform = null;
        _trimWaveformImage  = null;
        _trimImageTransform = null;
    }

    private void OnClipPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: (TimelineClip clip, Track track) } b) return;
        if (track.IsLocked) return;

        // Right-click must fall through to the Border's ContextFlyout — capturing
        // the pointer or marking the event handled here would suppress it. We
        // also skip middle-click; only left-clicks initiate drag / trim / select.
        var pressProps = e.GetCurrentPoint(b).Properties;
        if (pressProps.IsRightButtonPressed || pressProps.IsMiddleButtonPressed) return;

        // Razor tool: enter slice mode instead of drag/trim. The user can drag
        // sideways to position the cut line, and the clip is split on release.
        if (ViewModel?.ActiveTool == ActiveTool.Razor)
        {
            _isRazoring = true;
            _razorClip  = clip;
            _razorTrack = track;
            ShowRazorLine(e.GetCurrentPoint(ClipCanvas).Position.X);
            b.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        // Shift/Ctrl+click: toggle this clip in/out of the multi-selection. No drag.
        if (IsShiftOrCtrlDown())
        {
            clip.IsSelected = !clip.IsSelected;
            if (clip.IsSelected)
            {
                if (ViewModel is not null) ViewModel.SelectedClip = clip;
                ClipTapped?.Invoke(this, clip);
            }
            else if (ViewModel?.SelectedClip == clip)
            {
                var newPrimary = GetSelectedClips().FirstOrDefault();
                if (ViewModel is not null) ViewModel.SelectedClip = newPrimary;
                if (newPrimary is not null) ClipTapped?.Invoke(this, newPrimary);
                else SelectionCleared?.Invoke(this, EventArgs.Empty);
            }
            Rebuild();
            e.Handled = true;
            return;
        }

        var pt = e.GetCurrentPoint(b);
        double localX = pt.Position.X;
        double w = b.Width;

        _dragClip = clip;
        _dragTrack = track;
        _dragBorder = b;
        var pressCanvas = e.GetCurrentPoint(ClipCanvas).Position;
        _dragOriginX = pressCanvas.X;
        _dragOriginY = pressCanvas.Y;
        _dragOriginClipSec = clip.TimelineStart.TotalSeconds;
        _dragFromStart = clip.TimelineStart;
        _isDragging = false;
        _dragOriginalTrack   = track;
        _dragOriginalIndex   = track.Clips.IndexOf(clip);
        _dragHoverTrack      = track;
        _dragOriginalBorderY = Canvas.GetTop(b);

        if (localX <= EdgeHitZone)
        {
            _isTrimMode = true;
            _trimFromLeft = true;
            _trimOriginalStart = clip.TimelineStart;
            _trimOriginalDuration = clip.Duration;
            _trimOriginalSourceStart = clip.SourceStart;
            _multiDragItems = null;  // trim is always single-clip
            BeginTrimVisual(b, fromLeft: true);
        }
        else if (localX >= w - EdgeHitZone)
        {
            _isTrimMode = true;
            _trimFromLeft = false;
            _trimOriginalStart = clip.TimelineStart;
            _trimOriginalDuration = clip.Duration;
            _trimOriginalSourceStart = clip.SourceStart;
            _multiDragItems = null;
            BeginTrimVisual(b, fromLeft: false);
        }
        else
        {
            _isTrimMode = false;
            // If this clip is part of a multi-selection, drag will move the whole group.
            _multiDragItems = clip.IsSelected ? CaptureMultiDragItems(clip) : null;
        }

        if (_isTrimMode)
        {
            _trimKfSourceTimes = new Dictionary<EffectKeyframe, double>(ReferenceEqualityComparer.Instance);
            foreach (var fx in clip.Effects)
                foreach (var kv in fx.Keyframes)
                    foreach (var kf in kv.Value)
                        _trimKfSourceTimes[kf] = clip.SourceStart.TotalSeconds + kf.TimeRel * clip.Duration.TotalSeconds;
        }

        b.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Collect all other selected clips (and their current Border + start position) so
    /// they can move alongside <paramref name="primary"/> during a multi-clip drag.</summary>
    private List<(TimelineClip Clip, Border Border, TimeSpan OrigStart)>? CaptureMultiDragItems(TimelineClip primary)
    {
        var items = new List<(TimelineClip, Border, TimeSpan)>();
        foreach (var child in ClipCanvas.Children)
        {
            if (child is not Border b || b.Tag is not (TimelineClip c, Track _)) continue;
            if (c == primary) continue;
            if (!c.IsSelected) continue;
            items.Add((c, b, c.TimelineStart));
        }
        return items.Count > 0 ? items : null;
    }

    private void OnClipPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Razor mode: just slide the slice indicator along
        if (_isRazoring)
        {
            var rp = e.GetCurrentPoint(ClipCanvas);
            if (!rp.Properties.IsLeftButtonPressed) return;
            ShowRazorLine(rp.Position.X);
            e.Handled = true;
            return;
        }

        // Hover-only: no active drag → set the cursor based on which zone of the clip
        // the pointer is over (resize on the edges, move on the body) so the user knows
        // a press will trim vs. drag before they commit to it.
        if (_dragClip is null)
        {
            if (sender is Border hoverBorder && hoverBorder.Tag is (TimelineClip hoverClip, Track hoverTrack))
                UpdateHoverCursor(hoverBorder, hoverClip, hoverTrack, e.GetCurrentPoint(hoverBorder).Position.X);
            return;
        }
        if (_dragBorder is null) return;
        var pt = e.GetCurrentPoint(ClipCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double deltaX = pt.Position.X - _dragOriginX;
        double deltaY = pt.Position.Y - (_dragOriginalBorderY + (_dragBorder.Height / 2));
        if (!_isDragging && Math.Abs(deltaX) < 5 && Math.Abs(deltaY) < 5) return;
        _isDragging = true;

        if (_isTrimMode)
        {
            double deltaSec = deltaX / _pixelsPerSec;
            // Ctrl held mid-drag forces snapping for this drag regardless of the
            // persistent SnapEnabled toggle, matching the move-drag behavior below.
            bool snapActive = IsCtrlDown() || ViewModel?.SnapEnabled == true;
            if (_trimFromLeft)
            {
                // Trim-left changes the clip's in-point: advance both TimelineStart and
                // SourceStart by the same delta so the visible portion of the underlying
                // media shifts forward (rather than just shrinking the display window
                // while replaying the same opening frames).
                double minDelta = Math.Max(
                    -_trimOriginalStart.TotalSeconds,        // TimelineStart ≥ 0
                    -_trimOriginalSourceStart.TotalSeconds); // SourceStart ≥ 0
                double maxDelta = _trimOriginalDuration.TotalSeconds - 0.1; // Duration ≥ 0.1
                double delta = Math.Clamp(deltaSec, minDelta, maxDelta);

                if (snapActive)
                {
                    double candidateEdge = _trimOriginalStart.TotalSeconds + delta;
                    double snappedEdge = FindSnapPosition(candidateEdge, _dragClip);
                    double snappedDelta = snappedEdge - _trimOriginalStart.TotalSeconds;
                    if (snappedDelta >= minDelta && snappedDelta <= maxDelta)
                        delta = snappedDelta;
                }

                double newStartSec = _trimOriginalStart.TotalSeconds       + delta;
                double newDurSec   = _trimOriginalDuration.TotalSeconds    - delta;
                double newSrcSec   = _trimOriginalSourceStart.TotalSeconds + delta;

                _dragClip.TimelineStart = TimeSpan.FromSeconds(newStartSec);
                _dragClip.Duration      = TimeSpan.FromSeconds(newDurSec);
                _dragClip.SourceStart   = TimeSpan.FromSeconds(newSrcSec);
                Canvas.SetLeft(_dragBorder, newStartSec * _pixelsPerSec);
                _dragBorder.Width = Math.Max(newDurSec * _pixelsPerSec, 4);
            }
            else
            {
                double minDelta = 0.1 - _trimOriginalDuration.TotalSeconds; // Duration ≥ 0.1
                double delta = Math.Max(deltaSec, minDelta);

                if (snapActive)
                {
                    double originalEndSec = _trimOriginalStart.TotalSeconds + _trimOriginalDuration.TotalSeconds;
                    double candidateEdge = originalEndSec + delta;
                    double snappedEdge = FindSnapPosition(candidateEdge, _dragClip);
                    double snappedDelta = snappedEdge - originalEndSec;
                    if (snappedDelta >= minDelta)
                        delta = snappedDelta;
                }

                double newDurSec = _trimOriginalDuration.TotalSeconds + delta;
                _dragClip.Duration = TimeSpan.FromSeconds(newDurSec);
                _dragBorder.Width = Math.Max(newDurSec * _pixelsPerSec, 4);
            }
            // Re-anchor every keyframe to the source-time we captured at trim-begin
            // so keyframes stay glued to the underlying content rather than reflowing
            // proportionally with Duration. Falls off the edge → clamped (and will
            // un-clamp if the user reverses the trim within the same gesture, because
            // _trimKfSourceTimes preserves the original anchor for the whole drag).
            if (_trimKfSourceTimes is not null)
            {
                double newSrc = _dragClip.SourceStart.TotalSeconds;
                double newDur = Math.Max(0.001, _dragClip.Duration.TotalSeconds);
                foreach (var kv in _trimKfSourceTimes)
                {
                    double newRel = (kv.Value - newSrc) / newDur;
                    kv.Key.TimeRel = Math.Clamp(newRel, 0, 1);
                }
            }
            UpdateTrimHandlePosition(_dragBorder, _trimFromLeft);

            // Seek the preview to the trim edge so the user sees the actual frame they're
            // landing on. Left-trim → first frame of the (new) clip; right-trim → last
            // frame of the clip (one frame back so the playhead is still inside it; the
            // compositor only renders a layer while playhead < TimelineEnd).
            double fps = Math.Max(1, ViewModel?.Project.FrameRate ?? 24.0);
            var oneFrame = TimeSpan.FromSeconds(1.0 / fps);
            TimeSpan previewPos = _trimFromLeft
                ? _dragClip.TimelineStart
                : _dragClip.TimelineEnd - oneFrame;
            if (previewPos < TimeSpan.Zero) previewPos = TimeSpan.Zero;
            PlayheadChanged?.Invoke(this, previewPos);
        }
        else
        {
            double newStartSec = Math.Max(0, _dragOriginClipSec + deltaX / _pixelsPerSec);
            // Ctrl held mid-drag forces snapping for this drag regardless of the
            // persistent SnapEnabled toggle.
            if (ViewModel?.SnapEnabled == true || IsCtrlDown())
                newStartSec = FindSnapPosition(newStartSec, _dragClip);

            // Multi-drag: clamp delta so no other selected clip would go negative,
            // then apply the same delta to every other selected clip's start.
            if (_multiDragItems is not null)
            {
                double deltaSec = newStartSec - _dragOriginClipSec;
                double minOtherStart = double.MaxValue;
                foreach (var (_, _, origStart) in _multiDragItems)
                    if (origStart.TotalSeconds < minOtherStart) minOtherStart = origStart.TotalSeconds;
                if (minOtherStart + deltaSec < 0)
                    deltaSec = -minOtherStart;
                newStartSec = _dragOriginClipSec + deltaSec;

                foreach (var (other, otherBorder, origStart) in _multiDragItems)
                {
                    double otherSec = origStart.TotalSeconds + deltaSec;
                    other.TimelineStart = TimeSpan.FromSeconds(otherSec);
                    Canvas.SetLeft(otherBorder, otherSec * _pixelsPerSec);
                }
            }

            _dragClip.TimelineStart = TimeSpan.FromSeconds(newStartSec);
            Canvas.SetLeft(_dragBorder, newStartSec * _pixelsPerSec);

            // Vertical free-drag — only when this is a single-clip drag. With a
            // multi-selection we'd need a "preserve relative track offsets" pass that's
            // not worth the complexity for v1; the user can still use Ctrl+Up/Down on a
            // multi-selection. The dragged border follows the cursor freely and the
            // candidate target track is shown as a highlighted row; the actual reparent
            // commits on pointer release.
            if (_multiDragItems is null && _dragOriginalTrack is not null)
            {
                // Float the dragged border above its peers so it visibly "picks up".
                Canvas.SetZIndex(_dragBorder, 100);

                int insertDir = DetectInsertZone(pt.Position.Y);
                if (insertDir != 0)
                {
                    _dragInsertNewTrackDir = insertDir;
                    _dragHoverTrack = null;
                    HideDropTargetHighlight();
                    double indicatorY = insertDir < 0
                        ? TrackInsertZone / 2
                        : ClipCanvas.Height - TrackInsertZone / 2;
                    ShowTrackInsertIndicator(indicatorY);
                }
                else
                {
                    _dragInsertNewTrackDir = 0;
                    HideTrackInsertIndicator();

                    var targetTrack = FindTrackForVerticalDrag(pt.Position.Y, _dragClip.Kind);
                    if (targetTrack is not null)
                    {
                        _dragHoverTrack = targetTrack;
                        int idx = ViewModel!.Tracks.IndexOf(targetTrack);
                        if (idx >= 0 && idx < _trackRects.Count)
                            ShowDropTargetHighlight(_trackRects[idx].Y, _trackRects[idx].Height);
                    }
                    else
                    {
                        // Hovering an incompatible/locked track — no valid drop here.
                        _dragHoverTrack = null;
                        HideDropTargetHighlight();
                    }
                }

                // The border follows the cursor's vertical movement so the user feels
                // they're carrying the clip rather than snapping between rows.
                double freeY = _dragOriginalBorderY + (pt.Position.Y - _dragOriginY);
                // Don't let it wander completely off-canvas; clamp to a generous range.
                double maxY = Math.Max(0, ClipCanvas.Height - _dragBorder.Height);
                freeY = Math.Clamp(freeY, -_dragBorder.Height / 2, maxY + _dragBorder.Height / 2);
                Canvas.SetTop(_dragBorder, freeY);
            }
        }
        e.Handled = true;
    }

    private void ShowDropTargetHighlight(double y, double height)
    {
        if (_dropTargetHighlight is null)
        {
            _dropTargetHighlight = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(48, 127, 176, 105)),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 127, 176, 105)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            ClipCanvas.Children.Add(_dropTargetHighlight);
        }
        _dropTargetHighlight.Width  = Math.Max(1, ClipCanvas.Width);
        _dropTargetHighlight.Height = height;
        Canvas.SetLeft(_dropTargetHighlight, 0);
        Canvas.SetTop(_dropTargetHighlight, y);
        Canvas.SetZIndex(_dropTargetHighlight, 50);
    }

    private void HideDropTargetHighlight()
    {
        if (_dropTargetHighlight is null) return;
        ClipCanvas.Children.Remove(_dropTargetHighlight);
        _dropTargetHighlight = null;
    }

    private const double KeyVerticalParkOffsetTop = 0; // park flush with top edge

    /// <summary>Returns -1 if Y is above all tracks (drop here = new track on top),
    /// +1 if Y is below all tracks (new track on bottom), or 0 if it's over a track row.</summary>
    private int DetectInsertZone(double canvasY)
    {
        if (_trackRects.Count == 0) return 0;
        if (canvasY < _trackRects[0].Y - 2) return -1;
        var (lastY, lastH) = _trackRects[^1];
        if (canvasY > lastY + lastH + 2) return 1;
        return 0;
    }

    /// <summary>Pick the track row under <paramref name="canvasY"/>, but only if it's
    /// unlocked and matches the dragging clip's kind (video clips → video tracks; audio
    /// clips → audio tracks). Returns null when there's no compatible row under the cursor.</summary>
    private Track? FindTrackForVerticalDrag(double canvasY, ClipKind kind)
    {
        if (ViewModel is null) return null;
        var needKind = TrackKindForClip(kind);
        for (int i = 0; i < _trackRects.Count && i < ViewModel.Tracks.Count; i++)
        {
            var (y, h) = _trackRects[i];
            if (canvasY < y || canvasY > y + h) continue;
            var t = ViewModel.Tracks[i];
            if (t.IsLocked) return null;
            return t.Kind == needKind ? t : null;
        }
        return null;
    }

    private static TrackKind TrackKindForClip(ClipKind k) => k switch
    {
        ClipKind.Audio or ClipKind.Music => TrackKind.Audio,
        ClipKind.Title                   => TrackKind.Title,
        _                                => TrackKind.Video,
    };

    private void OnClipPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Razor mode: split the clip at wherever the cursor finally landed
        if (_isRazoring)
        {
            var pt = e.GetCurrentPoint(ClipCanvas);
            (sender as Border)?.ReleasePointerCapture(e.Pointer);

            var razorClip  = _razorClip;
            var razorTrack = _razorTrack;
            ResetRazorState();

            if (razorClip is not null && razorTrack is not null)
            {
                var splitAtInClip = TimeSpan.FromSeconds(
                    pt.Position.X / _pixelsPerSec - razorClip.TimelineStart.TotalSeconds);
                SplitClipAt(razorClip, razorTrack, splitAtInClip);
            }
            e.Handled = true;
            return;
        }

        if (_dragClip is null) return;

        // Snapshot state BEFORE ReleasePointerCapture — that call fires PointerCaptureLost
        // synchronously, which would otherwise null out everything we still need.
        var clip                 = _dragClip;
        var track                = _dragTrack;
        var border               = _dragBorder;
        bool wasDragging         = _isDragging;
        bool wasTrimming         = _isTrimMode;
        var trimOriginalStart       = _trimOriginalStart;
        var trimOriginalDuration    = _trimOriginalDuration;
        var trimOriginalSourceStart = _trimOriginalSourceStart;
        var dragFromStart        = _dragFromStart;
        var multiDragItems       = _multiDragItems;
        var originalTrack        = _dragOriginalTrack;
        var originalIndex        = _dragOriginalIndex;
        var hoverTrack           = _dragHoverTrack;
        var insertNewTrackDir    = _dragInsertNewTrackDir;

        EndTrimVisual();
        ResetDragState();
        HideTrackInsertIndicator();
        HideDropTargetHighlight();
        if (border is not null) Canvas.SetZIndex(border, 0);
        border?.ReleasePointerCapture(e.Pointer);

        if (!wasDragging)
        {
            // Click-without-drag = single-select this clip (collapse multi-selection to just it).
            ClearAllClipSelection();
            clip.IsSelected = true;
            if (ViewModel is not null) ViewModel.SelectedClip = clip;
            ClipTapped?.Invoke(this, clip);
            Rebuild();
        }
        else if (wasTrimming)
        {
            ViewModel?.History.RecordWithoutDo(new ClipTrimAction(
                clip,
                trimOriginalStart, trimOriginalDuration, trimOriginalSourceStart,
                clip.TimelineStart, clip.Duration, clip.SourceStart));
            // Regenerate the clip's Border + waveform Image at the new dimensions.
            // Without this, the Image keeps its press-time Width and intrinsic pixel
            // content, so a subsequent trim that extends the clip back uncovers an
            // un-rendered region on whichever edge was extended.
            Rebuild();
        }
        else
        {
            if (clip.TimelineStart != dragFromStart)
                ViewModel?.History.RecordWithoutDo(new ClipMoveAction(clip, dragFromStart, clip.TimelineStart));
            if (multiDragItems is not null)
            {
                foreach (var (other, _, origStart) in multiDragItems)
                {
                    if (other.TimelineStart != origStart)
                        ViewModel?.History.RecordWithoutDo(new ClipMoveAction(other, origStart, other.TimelineStart));
                }
            }

            // Vertical drag → reparent. We've been mutating the visual border's
            // Canvas.Top during move; commit the data side now.
            if (originalTrack is not null && insertNewTrackDir != 0 && ViewModel is not null)
            {
                // User dragged into the strip above/below all tracks — create a new
                // track in that slot and move the clip onto it.
                var newTrack = BuildTrackForClipKind(clip.Kind);
                int newPos = insertNewTrackDir < 0 ? 0 : ViewModel.Tracks.Count;
                ViewModel.History.Record(new ClipMoveToNewTrackAction(
                    ViewModel.Tracks, originalTrack, originalIndex, clip, newTrack, newPos));
                Rebuild();
            }
            else if (originalTrack is not null && hoverTrack is not null
                  && !ReferenceEquals(originalTrack, hoverTrack))
            {
                ViewModel?.History.Record(new ClipReparentAction(originalTrack, hoverTrack, clip, originalIndex));
                Rebuild();
            }
            else
            {
                // No reparent happened — the user free-dragged the border but dropped
                // it back on the same track (or over an incompatible row). Snap the
                // border back to its original row so it doesn't stay floating mid-air.
                Rebuild();
            }
        }

        e.Handled = true;
    }

    /// <summary>Mint a fresh track of the right kind for <paramref name="clipKind"/>,
    /// labeled with the next free number for that kind ("V3", "A2", …).</summary>
    private Track BuildTrackForClipKind(ClipKind clipKind)
    {
        var kind = TrackKindForClip(clipKind);
        var prefix = kind switch
        {
            TrackKind.Video => "V",
            TrackKind.Title => "T",
            _               => "A",
        };
        int n = (ViewModel?.Tracks.Count(t => t.Kind == kind) ?? 0) + 1;
        return new Track { Label = $"{prefix}{n}", Kind = kind };
    }

    /// <summary>Reset drag/trim state when capture is yanked away (lock screen, Win+L, alt-tab, Esc).</summary>
    private void OnClipPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) Canvas.SetZIndex(b, 0);
        EndTrimVisual();
        HideDropTargetHighlight();
        HideTrackInsertIndicator();
        ResetDragState();
        ResetRazorState();
        // Snap any free-dragged border back to its row.
        if (_isLoaded) Rebuild();
    }

    private void ResetDragState()
    {
        _dragClip = null;
        _dragBorder = null;
        _dragTrack = null;
        _isDragging = false;
        _isTrimMode = false;
        _multiDragItems = null;
        _dragOriginalTrack = null;
        _dragHoverTrack    = null;
        _dragInsertNewTrackDir = 0;
        _trimKfSourceTimes = null;
    }

    private void ResetRazorState()
    {
        _isRazoring = false;
        _razorClip = null;
        _razorTrack = null;
        HideRazorLine();
    }

    private void ShowRazorLine(double x)
    {
        if (_razorLine is null)
        {
            _razorLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 80, 80)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            ClipCanvas.Children.Add(_razorLine);
        }
        _razorLine.X1 = _razorLine.X2 = x;
        _razorLine.Y1 = 0;
        _razorLine.Y2 = ClipCanvas.Height > 0 ? ClipCanvas.Height : 200;
    }

    private void HideRazorLine()
    {
        if (_razorLine is null) return;
        ClipCanvas.Children.Remove(_razorLine);
        _razorLine = null;
    }

    private void SplitClipAt(TimelineClip clip, Track track, TimeSpan splitInClip)
    {
        if (ViewModel is null) return;
        if (splitInClip <= TimeSpan.Zero || splitInClip >= clip.Duration) return;
        ViewModel.History.Record(new ClipSplitAction(track, clip, splitInClip));
        Rebuild();
    }

    private void SplitClipAtPlayhead(TimelineClip clip, Track track)
    {
        if (ViewModel is null) return;
        var splitAt = ViewModel.Project.Playhead - clip.TimelineStart;
        if (splitAt <= TimeSpan.Zero || splitAt >= clip.Duration) return;
        ViewModel.History.Record(new ClipSplitAction(track, clip, splitAt));
        Rebuild();
    }

    private void DuplicateClip(TimelineClip clip, Track track)
    {
        ViewModel?.History.Record(new ClipDuplicateAction(track, clip));
        Rebuild();
    }

    private void DeleteClip(TimelineClip clip, Track track)
    {
        if (ViewModel is null) return;
        var idx = track.Clips.IndexOf(clip);
        ViewModel.History.Record(new ClipDeleteAction(track, idx, clip));
        ClipDeleted?.Invoke(this, clip);
        Rebuild();
    }

    private double FindSnapPosition(double sec, TimelineClip? dragging)
    {
        double snapRadius = 10.0 / _pixelsPerSec;
        double best = sec;
        double bestDist = snapRadius;

        if (ViewModel is null) return sec;
        foreach (var track in ViewModel.Tracks)
            foreach (var clip in track.Clips)
            {
                if (clip == dragging) continue;
                TrySnap(clip.TimelineStart.TotalSeconds);
                TrySnap(clip.TimelineEnd.TotalSeconds);
            }
        TrySnap(ViewModel.Project.Playhead.TotalSeconds);
        return best;

        void TrySnap(double candidate)
        {
            double d = Math.Abs(candidate - sec);
            if (d < bestDist) { bestDist = d; best = candidate; }
        }
    }

    private static (Color Bg, Color Border) GetClipColors(TimelineClip clip)
    {
        return clip.Kind switch
        {
            ClipKind.Video  => (Color.FromArgb(90, 28, 90, 140),  Color.FromArgb(160, 40, 120, 190)),
            ClipKind.Audio  => (Color.FromArgb(90, 25, 90, 50),   Color.FromArgb(160, 35, 140, 75)),
            ClipKind.Music  => (Color.FromArgb(90, 70, 35, 120),  Color.FromArgb(160, 100, 55, 175)),
            ClipKind.Title  => (Color.FromArgb(90, 90, 65, 15),   Color.FromArgb(160, 140, 100, 25)),
            _ => (Color.FromArgb(90, 50, 50, 50), Color.FromArgb(160, 80, 80, 80)),
        };
    }

    private void DrawPlayhead(double x)
    {
        if (_playheadLine is null)
        {
            _playheadLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 127, 176, 105)),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
        }
        if (!ClipCanvas.Children.Contains(_playheadLine))
            ClipCanvas.Children.Add(_playheadLine);

        _playheadLine.X1 = _playheadLine.X2 = x;
        _playheadLine.Y1 = 0;
        _playheadLine.Y2 = ClipCanvas.Height > 0 ? ClipCanvas.Height : 200;

        DrawRulerHead(x);
    }

    private Polygon? _rulerHead;

    private void DrawRulerHead(double x)
    {
        if (_rulerHead is null)
        {
            _rulerHead = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(220, 127, 176, 105)),
                IsHitTestVisible = false,
            };
            _rulerHead.Points.Add(new Point(0, 0));
            _rulerHead.Points.Add(new Point(0, 0));
            _rulerHead.Points.Add(new Point(0, 0));
        }
        if (!RulerCanvas.Children.Contains(_rulerHead))
            RulerCanvas.Children.Add(_rulerHead);

        var pts = _rulerHead.Points;
        pts[0] = new Point(x - 5, 0);
        pts[1] = new Point(x + 5, 0);
        pts[2] = new Point(x, 10);
    }

    public void UpdatePlayhead(TimeSpan position)
    {
        double x = position.TotalSeconds * _pixelsPerSec;
        DrawPlayhead(x);
        ScrollPlayheadIntoView(x);
    }

    private void ScrollPlayheadIntoView(double x)
    {
        if (MainScroller is null) return;
        double viewLeft  = MainScroller.HorizontalOffset;
        double viewRight = viewLeft + MainScroller.ViewportWidth - HeaderWidth;
        if (x < viewLeft || x > viewRight - 40)
            _ = MainScroller.ChangeView(Math.Max(0, x - 80), null, null);
    }

    private void BuildRuler()
    {
        RulerCanvas.Children.Clear();
        // Keep _rulerHead reference; it'll be re-added by DrawRulerHead below

        if (ViewModel is null) return;

        double totalSec = ViewModel.Project.Duration.TotalSeconds;
        double totalWidth = totalSec * _pixelsPerSec + 200;

        RulerCanvas.Width = totalWidth;
        DrawRulerTicks(totalSec, totalWidth);
        DrawMarkers();
        DrawRulerHead(ViewModel.Project.Playhead.TotalSeconds * _pixelsPerSec);
    }

    private void DrawMarkers()
    {
        if (ViewModel is null) return;
        foreach (var marker in ViewModel.Project.Markers)
        {
            double x = marker.Position.TotalSeconds * _pixelsPerSec;
            var color = ParseColor(marker.Color);

            var flag = new Polygon
            {
                Fill = new SolidColorBrush(color),
            };
            flag.Points.Add(new Point(x, 12));
            flag.Points.Add(new Point(x + 8, 16));
            flag.Points.Add(new Point(x, 20));
            ToolTipService.SetToolTip(flag, marker.Label);
            RulerCanvas.Children.Add(flag);

            var stem = new Line
            {
                X1 = x, X2 = x,
                Y1 = 12, Y2 = 24,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1,
            };
            RulerCanvas.Children.Add(stem);
        }
    }

    private static Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 7)
            return Color.FromArgb(255, 127, 176, 105);
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }

    private void DrawRulerTicks(double totalSec, double totalWidth)
    {
        double secPerTick = _pixelsPerSec switch
        {
            >= 160 => 5,
            >= 80  => 10,
            >= 40  => 30,
            >= 20  => 60,
            _      => 120,
        };

        var tickBrush  = new SolidColorBrush(Color.FromArgb(80, 192, 208, 216));
        var labelBrush = new SolidColorBrush(Color.FromArgb(120, 91, 114, 128));

        for (double sec = 0; sec <= totalSec + secPerTick; sec += secPerTick)
        {
            double x = sec * _pixelsPerSec;
            if (x > totalWidth) break;

            bool isMajor = ((int)sec % (int)(secPerTick * 2) == 0);

            var tick = new Line
            {
                X1 = x, X2 = x,
                Y1 = isMajor ? 10 : 16,
                Y2 = 24,
                Stroke = tickBrush,
                StrokeThickness = 1,
            };
            RulerCanvas.Children.Add(tick);

            if (isMajor)
            {
                var ts = TimeSpan.FromSeconds(sec);
                var tb = new TextBlock
                {
                    Text = $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}",
                    FontSize = 9,
                    Foreground = labelBrush,
                    FontFamily = new FontFamily("JetBrains Mono, Consolas"),
                };
                Canvas.SetLeft(tb, x + 3);
                Canvas.SetTop(tb, 2);
                RulerCanvas.Children.Add(tb);
            }
        }
    }

    private void OnRulerSizeChanged(object sender, SizeChangedEventArgs e) =>
        BuildRuler();

    private void OnMainScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The clip canvas is sized to fill the viewport (so the empty area below the
        // tracks can accept media drops as a "new track" gesture). When the viewport
        // grows or shrinks we have to rebuild to update Height.
        if (_isLoaded) BuildTracks();
    }

    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // Slide the fixed-position track-header column to match the main scroller's
        // vertical offset, so headers stay aligned with their tracks as the user
        // wheel-scrolls down to see more tracks.
        if (TrackHeadersTransform is not null)
            TrackHeadersTransform.Y = -MainScroller.VerticalOffset;
    }

    /// <summary>Wheel = vertical scroll, Shift+wheel = horizontal scroll, Ctrl+wheel = zoom.
    /// Attached to the elements *inside* MainScroller so it fires before the ScrollViewer's
    /// built-in handling and we can mark e.Handled to override the default scroll.</summary>
    private void OnTimelineWheel(object sender, PointerRoutedEventArgs e)
    {
        if (MainScroller is null) return;

        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var modifiers = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        bool shift = modifiers(Windows.System.VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl  = modifiers(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl)
        {
            // Zoom — multiplicative step so it feels smooth at any current scale
            double currentZoom = _pixelsPerSec / BasePixelsPerSec;
            double newZoom = delta > 0 ? currentZoom * 1.15 : currentZoom / 1.15;
            newZoom = Math.Clamp(newZoom, 0.1, 4.0);

            if (ViewModel is not null) ViewModel.TimelineZoom = newZoom;
            SetZoom(newZoom);
            ZoomChanged?.Invoke(this, newZoom);
        }
        else if (shift)
        {
            // Horizontal scroll
            MainScroller.ChangeView(MainScroller.HorizontalOffset - delta, null, null, disableAnimation: true);
        }
        else
        {
            // Vertical scroll
            MainScroller.ChangeView(null, MainScroller.VerticalOffset - delta, null, disableAnimation: true);
        }

        e.Handled = true;
    }

    // ── Scrubbing ──────────────────────────────────────────────────────

    private bool _isScrubbing;

    private void OnRulerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var pt = e.GetCurrentPoint(RulerCanvas);

        // Text tool: clicking the ruler adds a title clip at that time
        if (ViewModel.ActiveTool == ActiveTool.Text)
        {
            AddTitleAtX(pt.Position.X);
            e.Handled = true;
            return;
        }

        ScrubTo(pt.Position.X);
        _isScrubbing = true;
        RulerCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnRulerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing) return;
        var pt = e.GetCurrentPoint(RulerCanvas);
        if (!pt.Properties.IsLeftButtonPressed) { _isScrubbing = false; return; }
        ScrubTo(pt.Position.X);
    }

    private void OnRulerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        RulerCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnRulerPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isScrubbing = false;
    }

    private void ScrubTo(double x)
    {
        if (ViewModel is null) return;
        double sec = Math.Max(0, Math.Min(x / _pixelsPerSec, ViewModel.Project.Duration.TotalSeconds));
        var ts = TimeSpan.FromSeconds(sec);
        ViewModel.Project.Playhead = ts;
        DrawPlayhead(ts.TotalSeconds * _pixelsPerSec);
        PlayheadChanged?.Invoke(this, ts);
    }

    private void AddTitleAtX(double x)
    {
        if (ViewModel is null) return;
        double sec = Math.Max(0, x / _pixelsPerSec);
        var firstVideo = ViewModel.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video);
        if (firstVideo is null) return;
        var clip = new TimelineClip
        {
            Label         = "Title",
            Kind          = ClipKind.Title,
            TimelineStart = TimeSpan.FromSeconds(sec),
            Duration      = TimeSpan.FromSeconds(3),
            ColorHue      = 30,
        };
        ViewModel.History.Record(new ClipAddAction(firstVideo, clip));
        Rebuild();
        ClipTapped?.Invoke(this, clip);
    }

    // ── Empty-canvas pointer (Hand pans, Text adds title, otherwise scrub) ──

    private bool _isPanning;
    private double _panStartX;
    private double _panStartHOffset;
    private bool _isCanvasScrubbing;

    private void OnClipCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null) return;

        // Click must be on the empty canvas — clip Borders already handle their own clicks
        var ptScroller = e.GetCurrentPoint(this).Position;
        var ptCanvas   = e.GetCurrentPoint(ClipCanvas).Position;

        if (ViewModel.ActiveTool == ActiveTool.Hand)
        {
            _isPanning = true;
            _panStartX = ptScroller.X;
            _panStartHOffset = MainScroller.HorizontalOffset;
            ClipCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (ViewModel.ActiveTool == ActiveTool.Text)
        {
            AddTitleAtX(ptCanvas.X);
            e.Handled = true;
            return;
        }

        // Cursor / Razor (default): scrub the playhead and arm a possible marquee
        ScrubTo(ptCanvas.X);
        _isCanvasScrubbing = true;
        _marqueeOriginX    = ptCanvas.X;
        _marqueeOriginY    = ptCanvas.Y;
        _marqueeAdditive   = IsShiftOrCtrlDown();
        _isMarquee         = false;
        ClipCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnClipCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            var x = e.GetCurrentPoint(this).Position.X;
            var dx = _panStartX - x;
            MainScroller.ChangeView(_panStartHOffset + dx, null, null, disableAnimation: true);
            e.Handled = true;
            return;
        }
        if (_isCanvasScrubbing || _isMarquee)
        {
            var pt = e.GetCurrentPoint(ClipCanvas);
            if (!pt.Properties.IsLeftButtonPressed)
            {
                _isCanvasScrubbing = false;
                if (_isMarquee) { HideMarqueeRect(); _isMarquee = false; }
                return;
            }
            var pos = pt.Position;

            // Any drag movement past a small threshold promotes scrub → marquee.
            // Quick clicks still scrub via the press handler; continuous scrubbing is
            // available on the ruler.
            if (!_isMarquee)
            {
                double dx = Math.Abs(pos.X - _marqueeOriginX);
                double dy = Math.Abs(pos.Y - _marqueeOriginY);
                if (dx + dy > 4)
                {
                    _isCanvasScrubbing = false;
                    _isMarquee = true;
                    if (!_marqueeAdditive) ClearAllClipSelection();
                    Rebuild();           // reflect cleared selection — wipes ClipCanvas
                    ShowMarqueeRect();   // re-add the marquee on top of fresh canvas
                }
            }

            if (_isMarquee) UpdateMarqueeRect(pos);
            else            ScrubTo(pos.X);

            e.Handled = true;
        }
    }

    private void OnClipCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ClipCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_isMarquee)
        {
            CommitMarqueeSelection();
            HideMarqueeRect();
            _isMarquee = false;
            _isCanvasScrubbing = false;
            ClipCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_isCanvasScrubbing)
        {
            _isCanvasScrubbing = false;
            ClipCanvas.ReleasePointerCapture(e.Pointer);

            // Plain click on empty canvas (no marquee was drawn): deselect everything
            // unless Shift/Ctrl was held to preserve the selection.
            if (!_marqueeAdditive && ViewModel is not null)
            {
                bool hadAnySelected = false;
                foreach (var t in ViewModel.Tracks)
                    foreach (var c in t.Clips)
                        if (c.IsSelected) { hadAnySelected = true; break; }

                if (hadAnySelected)
                {
                    ClearAllClipSelection();
                    ViewModel.SelectedClip = null;
                    SelectionCleared?.Invoke(this, EventArgs.Empty);
                    Rebuild();
                }
            }

            e.Handled = true;
        }
    }

    private void OnClipCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
        _isCanvasScrubbing = false;
        if (_isMarquee) { HideMarqueeRect(); _isMarquee = false; }
    }

    // ── Drag & drop from media bin ─────────────────────────────────────

    private void OnClipCanvasDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null) return;
        if (!e.DataView.Properties.ContainsKey(BTAP.Controls.MediaTileControl.DragDataFormat)
            && !e.DataView.Contains(StandardDataFormats.Text)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible   = false;

        var pos = e.GetPosition(ClipCanvas);

        // No compatible track yet for this media kind → the entire canvas is a
        // "create a new track" zone. This lets the user drop the first video/audio
        // clip anywhere instead of hunting for the 14px insertion strip.
        var media = TryResolveDragMedia(e);
        if (media is not null && !HasCompatibleTrack(media.Type))
        {
            e.DragUIOverride.Caption = media.Type == MediaType.Audio
                ? "Drop to create a new audio track"
                : "Drop to create a new video track";
            HideDropIndicator();
            double indicatorY = _trackRects.Count > 0
                ? _trackRects[^1].Y + _trackRects[^1].Height + (TrackInsertZone / 2)
                : TrackInsertZone / 2;
            ShowTrackInsertIndicator(indicatorY);
            return;
        }

        if (IsInTopInsertZone(pos.Y))
        {
            e.DragUIOverride.Caption = "Drop to add a new track above";
            HideDropIndicator();
            ShowTrackInsertIndicator(TrackInsertZone / 2);
        }
        else if (IsInBottomInsertZone(pos.Y))
        {
            e.DragUIOverride.Caption = "Drop to add a new track below";
            HideDropIndicator();
            double indicatorY = _trackRects.Count > 0
                ? _trackRects[^1].Y + _trackRects[^1].Height + (TrackInsertZone / 2)
                : TrackInsertZone / 2;
            ShowTrackInsertIndicator(indicatorY);
        }
        else
        {
            e.DragUIOverride.Caption = "Drop to add to timeline";
            HideTrackInsertIndicator();
            double sec = ViewModel.SnapEnabled
                ? FindSnapPosition(pos.X / _pixelsPerSec, null)
                : pos.X / _pixelsPerSec;
            sec = Math.Max(0, sec);
            ShowDropIndicator(sec * _pixelsPerSec);
        }
    }

    private void OnClipCanvasDragLeave(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        HideTrackInsertIndicator();
    }

    private bool IsInTopInsertZone(double y) => y < TrackInsertZone;
    private bool IsInBottomInsertZone(double y)
    {
        if (ViewModel is null) return false;
        // Below the last track row (or below the top strip if there are no tracks).
        if (_trackRects.Count == 0) return y >= TrackInsertZone;
        var (lastY, lastH) = _trackRects[^1];
        return y > lastY + lastH;
    }

    /// <summary>True if the project already has an unlocked track that matches
    /// <paramref name="mediaType"/>. Used to decide whether a drop has anywhere to land
    /// without minting a new track first.</summary>
    private bool HasCompatibleTrack(MediaType mediaType)
    {
        if (ViewModel is null) return false;
        var needKind = mediaType == MediaType.Audio ? TrackKind.Audio : TrackKind.Video;
        foreach (var t in ViewModel.Tracks)
            if (!t.IsLocked && t.Kind == needKind) return true;
        return false;
    }

    /// <summary>Resolve the MediaItem behind an in-flight drag synchronously (DragOver
    /// fires before we'd want to do anything async). Returns null if this isn't a media
    /// bin drag or the id no longer matches.</summary>
    private MediaItem? TryResolveDragMedia(DragEventArgs e)
    {
        if (ViewModel is null) return null;
        if (!e.DataView.Properties.TryGetValue(BTAP.Controls.MediaTileControl.DragDataFormat, out var v))
            return null;
        if (v is not string id || string.IsNullOrEmpty(id)) return null;
        return ViewModel.MediaBin.FirstOrDefault(m => m.Id == id);
    }

    private async void OnClipCanvasDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        HideTrackInsertIndicator();
        if (ViewModel is null) return;

        // Extract media id from the data package
        string? mediaId = null;
        if (e.DataView.Properties.TryGetValue(BTAP.Controls.MediaTileControl.DragDataFormat, out var v))
            mediaId = v as string;
        if (string.IsNullOrEmpty(mediaId) && e.DataView.Contains(StandardDataFormats.Text))
            mediaId = await e.DataView.GetTextAsync();
        if (string.IsNullOrEmpty(mediaId)) return;

        var media = ViewModel.MediaBin.FirstOrDefault(m => m.Id == mediaId);
        if (media is null) return;

        // Where did they drop?
        var pos = e.GetPosition(ClipCanvas);
        double startSec = Math.Max(0, pos.X / _pixelsPerSec);
        if (ViewModel.SnapEnabled)
            startSec = Math.Max(0, FindSnapPosition(startSec, null));

        Track? track;
        // If no compatible track exists for this media kind, mint one no matter where
        // the user dropped — matches the "drop anywhere → new track" affordance shown
        // by DragOver in the same situation.
        if (!HasCompatibleTrack(media.Type))
        {
            track = CreateAndInsertTrack(media.Type, insertAtTop: false);
        }
        else if (IsInTopInsertZone(pos.Y))
        {
            track = CreateAndInsertTrack(media.Type, insertAtTop: true);
        }
        else if (IsInBottomInsertZone(pos.Y))
        {
            track = CreateAndInsertTrack(media.Type, insertAtTop: false);
        }
        else
        {
            // Which track row was under the cursor?  Fall back to the first unlocked matching kind.
            track = FindTrackAtY(pos.Y, media.Type);
            if (track is not null && track.IsLocked) track = null;
            track ??= ViewModel.Tracks.FirstOrDefault(t =>
                !t.IsLocked &&
                (media.Type == MediaType.Audio ? t.Kind == TrackKind.Audio : t.Kind == TrackKind.Video));
        }
        if (track is null) return;

        var clip = new TimelineClip
        {
            Label         = System.IO.Path.GetFileNameWithoutExtension(media.Name),
            Kind          = media.Type switch
            {
                MediaType.Audio => ClipKind.Audio,
                MediaType.Title => ClipKind.Title,
                _               => ClipKind.Video,
            },
            TimelineStart = TimeSpan.FromSeconds(startSec),
            Duration      = media.Duration > TimeSpan.Zero ? media.Duration : TimeSpan.FromSeconds(5),
            SourceId      = media.Id,
            ColorHue      = media.Type == MediaType.Audio ? 100 : 168,
        };

        ViewModel.History.Record(new ClipAddAction(track, clip));
        Rebuild();

        // Notify the page so it can select the clip and load its preview
        ClipTapped?.Invoke(this, clip);
    }

    /// <summary>A track is audible if it's not muted and (no other same-kind track is solo'd OR it itself is solo'd).</summary>
    private bool IsTrackAudible(Track track)
    {
        if (track.IsMuted) return false;
        if (ViewModel is null) return true;
        bool anySolo = ViewModel.Tracks.Any(t => t.Kind == track.Kind && t.IsSolo);
        return !anySolo || track.IsSolo;
    }

    /// <summary>Returns the track whose row contains Y, preferring one whose kind matches.</summary>
    private Track? FindTrackAtY(double y, MediaType mediaType)
    {
        if (ViewModel is null) return null;
        for (int i = 0; i < _trackRects.Count && i < ViewModel.Tracks.Count; i++)
        {
            var rect = _trackRects[i];
            if (y >= rect.Y && y <= rect.Y + rect.Height)
            {
                var track = ViewModel.Tracks[i];
                bool kindOk = mediaType == MediaType.Audio
                    ? track.Kind == TrackKind.Audio
                    : track.Kind == TrackKind.Video;
                return kindOk ? track : null;
            }
        }
        return null;
    }

    private void ShowDropIndicator(double x)
    {
        if (_dropIndicator is null)
        {
            _dropIndicator = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 200, 100)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            ClipCanvas.Children.Add(_dropIndicator);
        }
        _dropIndicator.X1 = _dropIndicator.X2 = x;
        _dropIndicator.Y1 = 0;
        _dropIndicator.Y2 = ClipCanvas.Height > 0 ? ClipCanvas.Height : 200;
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator is null) return;
        ClipCanvas.Children.Remove(_dropIndicator);
        _dropIndicator = null;
    }

    private void ShowTrackInsertIndicator(double y)
    {
        if (_trackInsertIndicator is null)
        {
            _trackInsertIndicator = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 127, 176, 105)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            ClipCanvas.Children.Add(_trackInsertIndicator);
        }
        _trackInsertIndicator.X1 = 0;
        _trackInsertIndicator.X2 = ClipCanvas.Width > 0 ? ClipCanvas.Width : 200;
        _trackInsertIndicator.Y1 = _trackInsertIndicator.Y2 = y;
    }

    private void HideTrackInsertIndicator()
    {
        if (_trackInsertIndicator is null) return;
        ClipCanvas.Children.Remove(_trackInsertIndicator);
        _trackInsertIndicator = null;
    }

    // ── Multi-selection helpers ────────────────────────────────────────

    public IReadOnlyList<TimelineClip> GetSelectedClips()
    {
        if (ViewModel is null) return Array.Empty<TimelineClip>();
        var list = new List<TimelineClip>();
        foreach (var t in ViewModel.Tracks)
            foreach (var c in t.Clips)
                if (c.IsSelected) list.Add(c);
        return list;
    }

    private void ClearAllClipSelection()
    {
        if (ViewModel is null) return;
        foreach (var t in ViewModel.Tracks)
            foreach (var c in t.Clips)
                c.IsSelected = false;
    }

    private static bool IsShiftOrCtrlDown()
    {
        var get = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        return get(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || get(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static bool IsCtrlDown()
    {
        var get = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        return get(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    // ── Marquee rectangle ──────────────────────────────────────────────

    private void ShowMarqueeRect()
    {
        if (_marqueeRect is null)
        {
            _marqueeRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 127, 176, 105)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 127, 176, 105)),
                IsHitTestVisible = false,
            };
        }
        if (!ClipCanvas.Children.Contains(_marqueeRect))
            ClipCanvas.Children.Add(_marqueeRect);
    }

    private void UpdateMarqueeRect(Point pt)
    {
        if (_marqueeRect is null) return;
        // Defensive: any Rebuild() during the drag would have cleared ClipCanvas.
        if (!ClipCanvas.Children.Contains(_marqueeRect))
            ClipCanvas.Children.Add(_marqueeRect);

        double x = Math.Min(_marqueeOriginX, pt.X);
        double y = Math.Min(_marqueeOriginY, pt.Y);
        double w = Math.Abs(pt.X - _marqueeOriginX);
        double h = Math.Abs(pt.Y - _marqueeOriginY);
        Canvas.SetLeft(_marqueeRect, x);
        Canvas.SetTop(_marqueeRect, y);
        _marqueeRect.Width  = w;
        _marqueeRect.Height = h;
    }

    private void HideMarqueeRect()
    {
        if (_marqueeRect is null) return;
        if (ClipCanvas.Children.Contains(_marqueeRect))
            ClipCanvas.Children.Remove(_marqueeRect);
    }

    private void CommitMarqueeSelection()
    {
        if (ViewModel is null || _marqueeRect is null) return;

        double mx = Canvas.GetLeft(_marqueeRect);
        double my = Canvas.GetTop(_marqueeRect);
        double mw = _marqueeRect.Width;
        double mh = _marqueeRect.Height;

        for (int i = 0; i < ViewModel.Tracks.Count && i < _trackRects.Count; i++)
        {
            var (ty, th) = _trackRects[i];
            if (ty + th <= my || ty >= my + mh) continue;  // no vertical overlap

            var track = ViewModel.Tracks[i];
            foreach (var clip in track.Clips)
            {
                double cx = clip.TimelineStart.TotalSeconds * _pixelsPerSec;
                double cw = Math.Max(clip.Duration.TotalSeconds * _pixelsPerSec, 4);
                if (cx + cw <= mx || cx >= mx + mw) continue;  // no horizontal overlap
                clip.IsSelected = true;
            }
        }

        // Promote the first selected clip to primary so the inspector & preview update.
        var selected = GetSelectedClips();
        var primary  = selected.FirstOrDefault();
        ViewModel.SelectedClip = primary;
        if (primary is not null) ClipTapped?.Invoke(this, primary);
        else                     SelectionCleared?.Invoke(this, EventArgs.Empty);

        Rebuild();
    }

    // ── Waveform overlay ────────────────────────────────────────────────

    private Image? TryCreateWaveformImage(TimelineClip clip, double clipPxWidth)
    {
        if (ViewModel is null) return null;
        if (string.IsNullOrEmpty(clip.SourceId)) return null;
        if (clip.Kind != ClipKind.Video && clip.Kind != ClipKind.Audio && clip.Kind != ClipKind.Music)
            return null;

        var media = ViewModel.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
        if (media is null) return null;
        if (media.Type != MediaType.Video && media.Type != MediaType.Audio) return null;
        if (string.IsNullOrEmpty(media.FilePath)) return null;
        if (!System.IO.File.Exists(media.FilePath)) return null;

        var img = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
            Opacity = 0.65,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin = new Thickness(2, 4, 2, 4),
        };

        var color = clip.Kind == ClipKind.Audio || clip.Kind == ClipKind.Music
            ? Color.FromArgb(230, 130, 220, 160)
            : Color.FromArgb(200, 230, 240, 250);

        var cached = BTAP.Services.WaveformService.GetCachedPeaks(media.FilePath);
        if (cached is not null)
        {
            img.Source = RenderWaveformBitmap(cached, color, clip, clipPxWidth);
        }
        else
        {
            BTAP.Services.WaveformService.EnsurePeaksAsync(media.FilePath);
        }

        return img;
    }

    /// <summary>Render the visible slice of a clip's source peaks (SourceStart..SourceStart+Duration)
    /// at exactly the clip's on-screen pixel width, with per-pixel peak summarization. This keeps
    /// the waveform crisp at any zoom level and aligned to what plays.</summary>
    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap RenderWaveformBitmap(
        BTAP.Services.PeakData data, Color color, TimelineClip clip, double clipPxWidth)
    {
        const int height = 48;
        int width = Math.Max(1, (int)Math.Round(clipPxWidth));
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);

        var pixels = new byte[width * height * 4];
        int mid = height / 2;

        // Map clip-time → bucket index in the full-file peak array.
        double startBucketF = clip.SourceStart.TotalSeconds * data.BucketsPerSecond;
        double endBucketF   = (clip.SourceStart + clip.Duration).TotalSeconds * data.BucketsPerSecond;
        if (endBucketF <= startBucketF) endBucketF = startBucketF + 1;

        for (int x = 0; x < width; x++)
        {
            // Range of buckets covered by this pixel column
            double t0 = startBucketF + (endBucketF - startBucketF) * (x       / (double)width);
            double t1 = startBucketF + (endBucketF - startBucketF) * ((x + 1) / (double)width);
            int i0 = Math.Max(0, (int)Math.Floor(t0));
            int i1 = Math.Min(data.Peaks.Length - 1, (int)Math.Ceiling(t1));

            float peak = 0;
            for (int i = i0; i <= i1; i++)
                if (data.Peaks[i] > peak) peak = data.Peaks[i];

            double timeRel = (x + 0.5) / width;
            double vol = Math.Max(0, clip.GetVolumeAt(timeRel));
            peak = (float)(peak * vol);

            int peakH = (int)Math.Round(Math.Clamp(peak, 0f, 1f) * (mid - 1));
            int top    = mid - peakH;
            int bottom = mid + peakH;
            for (int y = top; y <= bottom; y++)
            {
                if ((uint)y >= (uint)height) continue;
                int idx = (y * width + x) * 4;
                pixels[idx    ] = color.B;
                pixels[idx + 1] = color.G;
                pixels[idx + 2] = color.R;
                pixels[idx + 3] = color.A;
            }
        }

        using var stream = bmp.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bmp.Invalidate();
        return bmp;
    }

    /// <summary>Move every selected clip to the next compatible (same-kind, unlocked)
    /// track above (<paramref name="direction"/> = -1) or below (+1). Skips clips that
    /// have no eligible neighbor. Returns the number of clips moved.</summary>
    public int MoveSelectedClipsByTrack(int direction)
    {
        if (ViewModel is null || direction == 0) return 0;

        // Snapshot selection so re-parenting (which mutates Tracks[*].Clips) doesn't
        // perturb our iteration.
        var moves = new List<(Track From, TimelineClip Clip, int Index, Track To)>();
        for (int i = 0; i < ViewModel.Tracks.Count; i++)
        {
            var src = ViewModel.Tracks[i];
            if (src.IsLocked) continue;
            for (int ci = 0; ci < src.Clips.Count; ci++)
            {
                var c = src.Clips[ci];
                if (!c.IsSelected) continue;
                var dst = FindAdjacentCompatibleTrack(i, direction, c.Kind);
                if (dst is null) continue;
                moves.Add((src, c, ci, dst));
            }
        }

        if (moves.Count == 0) return 0;
        foreach (var (from, clip, idx, to) in moves)
            ViewModel.History.Record(new ClipReparentAction(from, to, clip, idx));

        Rebuild();
        return moves.Count;
    }

    private Track? FindAdjacentCompatibleTrack(int fromIndex, int direction, ClipKind clipKind)
    {
        if (ViewModel is null) return null;
        var needKind = TrackKindForClip(clipKind);
        for (int i = fromIndex + direction; i >= 0 && i < ViewModel.Tracks.Count; i += direction)
        {
            var t = ViewModel.Tracks[i];
            if (t.IsLocked) continue;
            if (t.Kind == needKind) return t;
        }
        return null;
    }

    // ── Volume-automation overlay ───────────────────────────────────────
    //
    // Each video / audio / music clip carries a Canvas overlay with two polylines
    // (one wide-stroke transparent line for hit-testing, one thin visible line)
    // plus an Ellipse per VolumePoint. Y inside the canvas maps to volume:
    // top = EnvelopeMaxVolume, middle = 1.0 (default/unity), bottom = 0.0, with
    // EnvelopePad pixels of padding at each edge so the line and circles don't
    // collide with the clip's rounded border. The default sits in the middle so
    // the user can drag up to amplify or down to attenuate.
    //
    // Interactions:
    //   • Left-click + drag on the line  →  if no keyframes, drag adjusts clip.Volume
    //                                       (whole flat line moves); if keyframes
    //                                       exist, shifts the segment under the
    //                                       cursor (one or both bracketing points)
    //                                       up/down by the pointer's Y delta.
    //   • Left-click + drag on a circle  →  moves that keyframe in X and Y.
    //   • Right-click on the line        →  inserts a keyframe at (timeRel, vol).
    //   • Right-click on a circle        →  removes that keyframe.

    private const double EnvelopePad = 3.0;
    private const double EnvelopeMaxVolume = 2.0;

    private TimelineClip?  _envDragClip;
    private Canvas?        _envDragCanvas;
    private Polyline?      _envDragHitLine;
    private Polyline?      _envDragVisLine;
    private List<Ellipse>? _envDragCircles;
    private double         _envDragStartPointerY;
    private double         _envDragStartClipVolume;
    // Indices into clip.VolumeEnvelope of the points affected by the current segment-drag,
    // and their start volumes for delta math. Null when 0-keyframe Volume-drag is active.
    private int[]?         _envDragAffectedIdx;
    private double[]?      _envDragAffectedStartVols;

    // Single-point drag state (left-drag on a circle)
    private VolumePoint?   _envDragPoint;
    private double         _envDragPointStartTimeRel;
    private double         _envDragPointStartVolume;
    private double         _envDragPointStartPointerX;
    private double         _envDragPointStartPointerY;

    private Canvas BuildVolumeEnvelopeOverlay(TimelineClip clip, double clipW, double clipH)
    {
        var canvas = new Canvas
        {
            Width  = clipW,
            Height = clipH,
            Background = null,
            Tag = clip,
        };

        var hitLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            StrokeThickness = 14,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
            StrokeLineJoin     = PenLineJoin.Round,
            IsHitTestVisible = true,
        };
        hitLine.Tag = "envHit";
        hitLine.PointerPressed     += OnEnvelopeLinePointerPressed;
        hitLine.PointerMoved       += OnEnvelopeLinePointerMoved;
        hitLine.PointerReleased    += OnEnvelopeLinePointerReleased;
        hitLine.PointerCaptureLost += OnEnvelopeLinePointerCaptureLost;
        canvas.Children.Add(hitLine);

        var visLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 220, 110)),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
            StrokeLineJoin     = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        visLine.Tag = "envVis";
        canvas.Children.Add(visLine);

        RefreshEnvelopeOverlay(canvas, clip);
        return canvas;
    }

    /// <summary>Re-position the line + circles inside <paramref name="canvas"/> to match
    /// <paramref name="clip"/>'s current envelope. Re-uses the existing hit/visible
    /// polylines (so an active pointer capture survives) and only adds/removes Ellipses
    /// to match the point count.</summary>
    private void RefreshEnvelopeOverlay(Canvas canvas, TimelineClip clip)
    {
        double clipW = canvas.Width;
        double clipH = canvas.Height;

        Polyline? hitLine = null;
        Polyline? visLine = null;
        var circles = new List<Ellipse>();
        foreach (var child in canvas.Children)
        {
            if (child is Polyline pl)
            {
                if ((pl.Tag as string) == "envHit") hitLine = pl;
                else if ((pl.Tag as string) == "envVis") visLine = pl;
            }
            else if (child is Ellipse el)
            {
                circles.Add(el);
            }
        }
        if (hitLine is null || visLine is null) return;

        var sorted = clip.VolumeEnvelope.OrderBy(p => p.TimeRel).ToList();
        var linePoints = new PointCollection();
        if (sorted.Count == 0)
        {
            double y = EnvelopeYForVolume(clip.Volume, clipH);
            linePoints.Add(new Point(0, y));
            linePoints.Add(new Point(clipW, y));
        }
        else
        {
            double yFirst = EnvelopeYForVolume(sorted[0].Volume, clipH);
            linePoints.Add(new Point(0, yFirst));
            foreach (var p in sorted)
                linePoints.Add(new Point(EnvelopeXForTime(p.TimeRel, clipW),
                                         EnvelopeYForVolume(p.Volume, clipH)));
            double yLast = EnvelopeYForVolume(sorted[^1].Volume, clipH);
            linePoints.Add(new Point(clipW, yLast));
        }
        hitLine.Points = linePoints;
        var visCopy = new PointCollection();
        foreach (var p in linePoints) visCopy.Add(p);
        visLine.Points = visCopy;

        // Sync ellipse count to envelope point count
        while (circles.Count > clip.VolumeEnvelope.Count)
        {
            var last = circles[^1];
            canvas.Children.Remove(last);
            circles.RemoveAt(circles.Count - 1);
        }
        while (circles.Count < clip.VolumeEnvelope.Count)
        {
            var el = new Ellipse
            {
                Width  = 9,
                Height = 9,
                Fill   = new SolidColorBrush(Color.FromArgb(255, 255, 220, 110)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 80, 60, 10)),
                StrokeThickness = 1,
                IsHitTestVisible = true,
            };
            el.PointerPressed     += OnEnvelopeCirclePointerPressed;
            el.PointerMoved       += OnEnvelopeCirclePointerMoved;
            el.PointerReleased    += OnEnvelopeCirclePointerReleased;
            el.PointerCaptureLost += OnEnvelopeCirclePointerCaptureLost;
            canvas.Children.Add(el);
            circles.Add(el);
        }
        for (int i = 0; i < clip.VolumeEnvelope.Count; i++)
        {
            var pt = clip.VolumeEnvelope[i];
            var el = circles[i];
            el.Tag = (pt, clip, canvas);
            double cx = EnvelopeXForTime(pt.TimeRel, clipW);
            double cy = EnvelopeYForVolume(pt.Volume, clipH);
            Canvas.SetLeft(el, cx - el.Width  / 2);
            Canvas.SetTop (el, cy - el.Height / 2);
        }
    }

    private static double EnvelopeYForVolume(double volume, double clipH)
    {
        double range = Math.Max(1, clipH - 2 * EnvelopePad);
        double frac = Math.Clamp(volume, 0, EnvelopeMaxVolume) / EnvelopeMaxVolume;
        return EnvelopePad + (1 - frac) * range;
    }

    private static double EnvelopeXForTime(double timeRel, double clipW) =>
        Math.Clamp(timeRel, 0, 1) * clipW;

    private static double EnvelopeVolumeForY(double y, double clipH)
    {
        double range = Math.Max(1, clipH - 2 * EnvelopePad);
        double frac = Math.Clamp(1 - (y - EnvelopePad) / range, 0, 1);
        return frac * EnvelopeMaxVolume;
    }

    /// <summary>Find the Border whose Tag references this clip, so we can refresh
    /// the waveform image after the envelope changes without rebuilding the whole
    /// timeline.</summary>
    private Border? FindClipBorder(TimelineClip clip)
    {
        foreach (var child in ClipCanvas.Children)
            if (child is Border b && b.Tag is (TimelineClip c, Track _) && ReferenceEquals(c, clip))
                return b;
        return null;
    }

    /// <summary>Re-render the waveform bitmap for <paramref name="clip"/> in-place so
    /// it reflects the new envelope without tearing down the Border (which would
    /// kill any pointer capture in progress).</summary>
    private void RefreshClipWaveform(TimelineClip clip)
    {
        if (ViewModel is null) return;
        var border = FindClipBorder(clip);
        if (border?.Child is not Grid grid) return;
        Image? img = null;
        foreach (var child in grid.Children)
        {
            if (child is Canvas host)
                foreach (var c in host.Children)
                    if (c is Image i) { img = i; break; }
            if (img is not null) break;
        }
        if (img is null) return;
        if (string.IsNullOrEmpty(clip.SourceId)) return;
        var media = ViewModel.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
        if (media is null || string.IsNullOrEmpty(media.FilePath)) return;
        var peaks = BTAP.Services.WaveformService.GetCachedPeaks(media.FilePath);
        if (peaks is null) return;
        var color = clip.Kind == ClipKind.Audio || clip.Kind == ClipKind.Music
            ? Color.FromArgb(230, 130, 220, 160)
            : Color.FromArgb(200, 230, 240, 250);
        img.Source = RenderWaveformBitmap(peaks, color, clip, img.Width);
    }

    // ── Envelope line: left-drag = adjust segment, right-click = add keyframe ──

    private void OnEnvelopeLinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Polyline line) return;
        if (line.Parent is not Canvas canvas || canvas.Tag is not TimelineClip clip) return;

        var pt = e.GetCurrentPoint(canvas);
        if (pt.Properties.IsRightButtonPressed)
        {
            double clipW = canvas.Width;
            double clipH = canvas.Height;
            double timeRel = Math.Clamp(pt.Position.X / Math.Max(1, clipW), 0, 1);
            double vol     = EnvelopeVolumeForY(pt.Position.Y, clipH);
            // Seed any new point's "vol" with the click position rather than the
            // current interpolated value, so the user gets a keyframe right under
            // their cursor.
            clip.VolumeEnvelope.Add(new VolumePoint { TimeRel = timeRel, Volume = vol });
            if (ViewModel is not null) ViewModel.Project.IsModified = true;
            RefreshEnvelopeOverlay(canvas, clip);
            RefreshClipWaveform(clip);
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        _envDragClip   = clip;
        _envDragCanvas = canvas;
        _envDragHitLine = line;
        _envDragVisLine = canvas.Children.OfType<Polyline>().FirstOrDefault(p => (p.Tag as string) == "envVis");
        _envDragCircles = canvas.Children.OfType<Ellipse>().ToList();
        _envDragStartPointerY  = pt.Position.Y;
        _envDragStartClipVolume = clip.Volume;

        if (clip.VolumeEnvelope.Count == 0)
        {
            _envDragAffectedIdx        = null;
            _envDragAffectedStartVols  = null;
        }
        else
        {
            // Find the bracketing points around pt.Position.X. Either or both may be
            // null when the cursor is outside [firstTimeRel, lastTimeRel].
            double clipW = canvas.Width;
            double timeRel = Math.Clamp(pt.Position.X / Math.Max(1, clipW), 0, 1);
            int prevIdx = -1, nextIdx = -1;
            double prevT = double.NegativeInfinity, nextT = double.PositiveInfinity;
            for (int i = 0; i < clip.VolumeEnvelope.Count; i++)
            {
                var p = clip.VolumeEnvelope[i];
                if (p.TimeRel <= timeRel && p.TimeRel > prevT) { prevT = p.TimeRel; prevIdx = i; }
                if (p.TimeRel >= timeRel && p.TimeRel < nextT) { nextT = p.TimeRel; nextIdx = i; }
            }
            var affected = new List<int>();
            if (prevIdx >= 0) affected.Add(prevIdx);
            if (nextIdx >= 0 && nextIdx != prevIdx) affected.Add(nextIdx);
            _envDragAffectedIdx       = affected.ToArray();
            _envDragAffectedStartVols = affected.Select(i => clip.VolumeEnvelope[i].Volume).ToArray();
        }

        line.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnEnvelopeLinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_envDragClip is null || _envDragCanvas is null) return;
        if (sender is not Polyline) return;
        var pt = e.GetCurrentPoint(_envDragCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double clipH = _envDragCanvas.Height;
        double range = Math.Max(1, clipH - 2 * EnvelopePad);
        double deltaY = pt.Position.Y - _envDragStartPointerY;
        double deltaVol = -deltaY / range * EnvelopeMaxVolume;

        if (_envDragAffectedIdx is null)
        {
            _envDragClip.Volume = Math.Clamp(_envDragStartClipVolume + deltaVol, 0, EnvelopeMaxVolume);
        }
        else
        {
            for (int i = 0; i < _envDragAffectedIdx.Length; i++)
            {
                int idx = _envDragAffectedIdx[i];
                if (idx < 0 || idx >= _envDragClip.VolumeEnvelope.Count) continue;
                _envDragClip.VolumeEnvelope[idx].Volume = Math.Clamp(
                    _envDragAffectedStartVols![i] + deltaVol, 0, EnvelopeMaxVolume);
            }
        }

        RefreshEnvelopeOverlay(_envDragCanvas, _envDragClip);
        RefreshClipWaveform(_envDragClip);
        e.Handled = true;
    }

    private void OnEnvelopeLinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_envDragClip is null) return;
        var clip = _envDragClip;
        var canvas = _envDragCanvas;
        if (sender is Polyline line) line.ReleasePointerCapture(e.Pointer);
        if (ViewModel is not null) ViewModel.Project.IsModified = true;
        EndEnvelopeLineDrag();
        if (canvas is not null) RefreshEnvelopeOverlay(canvas, clip);
        RefreshClipWaveform(clip);
        e.Handled = true;
    }

    private void OnEnvelopeLinePointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndEnvelopeLineDrag();

    private void EndEnvelopeLineDrag()
    {
        _envDragClip = null;
        _envDragCanvas = null;
        _envDragHitLine = null;
        _envDragVisLine = null;
        _envDragCircles = null;
        _envDragAffectedIdx = null;
        _envDragAffectedStartVols = null;
    }

    // ── Envelope circle: left-drag = move, right-click = delete ──

    private void OnEnvelopeCirclePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Ellipse el) return;
        if (el.Tag is not (VolumePoint vp, TimelineClip clip, Canvas canvas)) return;

        var pt = e.GetCurrentPoint(canvas);
        if (pt.Properties.IsRightButtonPressed)
        {
            clip.VolumeEnvelope.Remove(vp);
            if (ViewModel is not null) ViewModel.Project.IsModified = true;
            RefreshEnvelopeOverlay(canvas, clip);
            RefreshClipWaveform(clip);
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;

        _envDragPoint              = vp;
        _envDragClip               = clip;
        _envDragCanvas             = canvas;
        _envDragPointStartTimeRel  = vp.TimeRel;
        _envDragPointStartVolume   = vp.Volume;
        _envDragPointStartPointerX = pt.Position.X;
        _envDragPointStartPointerY = pt.Position.Y;
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnEnvelopeCirclePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_envDragPoint is null || _envDragClip is null || _envDragCanvas is null) return;
        var pt = e.GetCurrentPoint(_envDragCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double clipW = _envDragCanvas.Width;
        double clipH = _envDragCanvas.Height;
        double range = Math.Max(1, clipH - 2 * EnvelopePad);

        double dx = pt.Position.X - _envDragPointStartPointerX;
        double dy = pt.Position.Y - _envDragPointStartPointerY;
        _envDragPoint.TimeRel = Math.Clamp(
            _envDragPointStartTimeRel + dx / Math.Max(1, clipW), 0, 1);
        _envDragPoint.Volume = Math.Clamp(
            _envDragPointStartVolume - dy / range * EnvelopeMaxVolume, 0, EnvelopeMaxVolume);

        RefreshEnvelopeOverlay(_envDragCanvas, _envDragClip);
        RefreshClipWaveform(_envDragClip);
        e.Handled = true;
    }

    private void OnEnvelopeCirclePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_envDragPoint is null) return;
        var clip = _envDragClip;
        var canvas = _envDragCanvas;
        if (sender is Ellipse el) el.ReleasePointerCapture(e.Pointer);
        if (ViewModel is not null) ViewModel.Project.IsModified = true;
        EndEnvelopeCircleDrag();
        if (clip is not null && canvas is not null) RefreshEnvelopeOverlay(canvas, clip);
        if (clip is not null) RefreshClipWaveform(clip);
        e.Handled = true;
    }

    private void OnEnvelopeCirclePointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndEnvelopeCircleDrag();

    private void EndEnvelopeCircleDrag()
    {
        _envDragPoint = null;
        // Don't null _envDragClip / _envDragCanvas here — line-drag uses them too and
        // releases them via EndEnvelopeLineDrag. But when a circle drag is in progress,
        // no line drag is active, so it's safe to also clear them.
        _envDragClip = null;
        _envDragCanvas = null;
    }

    private Track? CreateAndInsertTrack(MediaType mediaType, bool insertAtTop)
    {
        if (ViewModel is null) return null;
        var kind = mediaType == MediaType.Audio ? TrackKind.Audio : TrackKind.Video;
        var prefix = kind == TrackKind.Video ? "V" : "A";
        var n = ViewModel.Tracks.Count(t => t.Kind == kind) + 1;
        var track = new Track { Label = $"{prefix}{n}", Kind = kind };
        if (insertAtTop)
        {
            // Keep title tracks on top — insert below them.
            int idx = 0;
            while (idx < ViewModel.Tracks.Count && ViewModel.Tracks[idx].Kind == TrackKind.Title)
                idx++;
            ViewModel.Tracks.Insert(idx, track);
        }
        else
        {
            ViewModel.Tracks.Add(track);
        }
        ViewModel.Project.IsModified = true;
        return track;
    }

    // ── Keyframe diamond overlay & "Add keyframe" submenu ────────────────

    /// <summary>Submenu inside the clip right-click flyout. Lists every enabled
    /// effect's numeric parameters; picking one fires AddKeyframeRequested for
    /// EditorPage to insert a keyframe at the current playhead.</summary>
    private MenuFlyoutSubItem BuildAddKeyframeSubmenu(TimelineClip clip)
    {
        var sub = new MenuFlyoutSubItem { Text = "Add keyframe at playhead" };

        var enabledEffects = clip.Effects.Where(fx => fx.Enabled).ToList();
        if (enabledEffects.Count == 0)
        {
            var disabled = new MenuFlyoutItem
            {
                Text = "Enable an effect first",
                IsEnabled = false,
            };
            sub.Items.Add(disabled);
            return sub;
        }

        foreach (var fx in enabledEffects)
        {
            var paramList = ClipEffectsChain.NumberParams(fx.Name);
            if (paramList.Count == 0) continue;
            foreach (var p in paramList)
            {
                var item = new MenuFlyoutItem
                {
                    Text = $"{fx.Name} · {p.Label}",
                };
                var fxLocal = fx;
                var keyLocal = p.Key;
                item.Click += (_, _) => AddKeyframeRequested?.Invoke(this, (clip, fxLocal, keyLocal));
                sub.Items.Add(item);
            }
        }

        if (sub.Items.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "No parameters available", IsEnabled = false });
        }
        return sub;
    }

    /// <summary>Diamond markers along the top edge of a clip — one per automation
    /// keyframe across every effect parameter. Clicks select (ctrl/shift for
    /// multi); selected diamonds get a brighter outline. Returns null when the
    /// clip has no keyframes, so we don't add an empty Canvas to the visual tree.</summary>
    private Canvas? BuildKeyframeDiamondOverlay(TimelineClip clip, double clipW, double clipH)
    {
        int totalKfs = 0;
        foreach (var fx in clip.Effects)
            foreach (var kv in fx.Keyframes)
                totalKfs += kv.Value.Count;
        if (totalKfs == 0) return null;

        var canvas = new Canvas
        {
            Width  = clipW,
            Height = clipH,
            Background = null,
            IsHitTestVisible = true,
        };
        var sel = ViewModel?.SelectedKeyframes;

        foreach (var fx in clip.Effects)
        {
            foreach (var kv in fx.Keyframes)
            {
                var hue = KeyframeColors.HueFor(fx.Name, kv.Key);
                foreach (var kf in kv.Value)
                {
                    bool selected = sel?.Contains(kf) == true;
                    var diamond = new Rectangle
                    {
                        Width  = 9,
                        Height = 9,
                        Fill   = new SolidColorBrush(hue),
                        Stroke = new SolidColorBrush(selected ? Microsoft.UI.Colors.White : Color.FromArgb(220, 0, 0, 0)),
                        StrokeThickness = selected ? 2 : 1,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform { Angle = 45 },
                    };
                    double cx = Math.Clamp(kf.TimeRel, 0, 1) * clipW;
                    Canvas.SetLeft(diamond, cx - diamond.Width / 2);
                    Canvas.SetTop(diamond, 2);
                    ToolTipService.SetToolTip(diamond,
                        $"{fx.Name} · {kv.Key} = {kf.Value:0.##}");

                    var clipLocal = clip;
                    var fxLocal = fx;
                    var keyLocal = kv.Key;
                    var kfLocal = kf;
                    var canvasLocal = canvas;
                    diamond.PointerPressed += (_, e) =>
                    {
                        // Don't immediately mutate selection — wait for release so a
                        // drag past the threshold lands as "reposition" without also
                        // changing selection (and so a no-move press still selects).
                        _kfDragDiamond = diamond;
                        _kfDragKf = kfLocal;
                        _kfDragClip = clipLocal;
                        _kfDragCanvas = canvasLocal;
                        _kfDragStartPointerX = e.GetCurrentPoint(canvasLocal).Position.X;
                        _kfDragMoved = false;
                        diamond.CapturePointer(e.Pointer);
                        e.Handled = true;
                    };
                    diamond.PointerMoved += (_, e) =>
                    {
                        if (!ReferenceEquals(_kfDragDiamond, diamond)) return;
                        if (_kfDragKf is null || _kfDragClip is null || _kfDragCanvas is null) return;
                        double curX = e.GetCurrentPoint(_kfDragCanvas).Position.X;
                        if (!_kfDragMoved && Math.Abs(curX - _kfDragStartPointerX) < KfDragThresholdPx)
                            return;
                        _kfDragMoved = true;
                        double newRel = Math.Clamp(curX / Math.Max(1, _kfDragCanvas.Width), 0, 1);
                        _kfDragKf.TimeRel = newRel;
                        Canvas.SetLeft(diamond, newRel * _kfDragCanvas.Width - diamond.Width / 2);
                        e.Handled = true;
                    };
                    void Release(object _, PointerRoutedEventArgs e)
                    {
                        if (!ReferenceEquals(_kfDragDiamond, diamond)) return;
                        try { diamond.ReleasePointerCapture(e.Pointer); } catch { }
                        bool moved = _kfDragMoved;
                        var kf2   = _kfDragKf;
                        var clip2 = _kfDragClip;
                        _kfDragDiamond = null;
                        _kfDragKf = null;
                        _kfDragClip = null;
                        _kfDragCanvas = null;
                        _kfDragMoved = false;

                        var vm = ViewModel;
                        if (vm is null || kf2 is null || clip2 is null) { e.Handled = true; return; }

                        if (!moved)
                        {
                            // Pure click → select + jump playhead.
                            var ctrl  = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                                            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                            var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                                            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                            vm.SetKeyframeSelection(kf2, ctrl || shift);
                            vm.Project.Playhead = clip2.TimelineStart +
                                TimeSpan.FromSeconds(kf2.TimeRel * clip2.Duration.TotalSeconds);
                            PlayheadChanged?.Invoke(this, vm.Project.Playhead);
                            KeyframeSelectionChanged?.Invoke(this, EventArgs.Empty);
                            Rebuild();
                        }
                        else
                        {
                            // Drag committed → mark dirty + sync the Automations list
                            // (the time column needs to re-render).
                            vm.Project.IsModified = true;
                            KeyframeSelectionChanged?.Invoke(this, EventArgs.Empty);
                        }
                        e.Handled = true;
                    }
                    diamond.PointerReleased    += Release;
                    diamond.PointerCaptureLost += Release;
                    canvas.Children.Add(diamond);
                }
            }
        }
        return canvas;
    }
}
