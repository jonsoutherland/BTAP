using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using BTAP.Models;
using BTAP.ViewModels;

namespace BTAP.Controls;

public sealed partial class TimelineControl : UserControl
{
    private const double HeaderWidth = 96.0;
    private const double RulerHeight = 24.0;
    private const double VideoTrackHeight = 54.0;
    private const double AudioTrackHeight = 40.0;
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

    private double _pixelsPerSec = BasePixelsPerSec;
    private Line? _playheadLine;
    private readonly List<(double Y, double Height)> _trackRects = [];

    private bool _isLoaded;

    // Drag state
    private TimelineClip? _dragClip;
    private Track?         _dragTrack;
    private Border?        _dragBorder;
    private double         _dragOriginX;
    private double         _dragOriginClipSec;
    private TimeSpan       _dragFromStart;
    private bool           _isDragging;

    // Trim state
    private bool           _isTrimMode;
    private bool           _trimFromLeft;
    private TimeSpan       _trimOriginalStart;
    private TimeSpan       _trimOriginalDuration;

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
            if (_isLoaded) Rebuild();
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
        BuildTracks();
        BuildRuler();
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
            double h = track.Kind == TrackKind.Video ? VideoTrackHeight : AudioTrackHeight;
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
        ClipCanvas.Height = Math.Max(y + TrackInsertZone, 1);

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
            Fill = track.Kind == TrackKind.Video
                ? new SolidColorBrush(Color.FromArgb(255, 45, 110, 180))
                : new SolidColorBrush(Color.FromArgb(255, 45, 140, 80)),
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
        // (SourceStart..SourceStart+Duration) is drawn.
        var waveformImage = TryCreateWaveformImage(clip, w);

        var contentRoot = new Grid();
        if (waveformImage is not null) contentRoot.Children.Add(waveformImage);
        contentRoot.Children.Add(label);

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
        flyout.Items.Add(miDel);
        border.ContextFlyout = flyout;

        ClipCanvas.Children.Add(border);
    }

    private void OnClipPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: (TimelineClip clip, Track track) } b) return;
        if (track.IsLocked) return;

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
        _dragOriginX = e.GetCurrentPoint(ClipCanvas).Position.X;
        _dragOriginClipSec = clip.TimelineStart.TotalSeconds;
        _dragFromStart = clip.TimelineStart;
        _isDragging = false;

        if (localX <= EdgeHitZone)
        {
            _isTrimMode = true;
            _trimFromLeft = true;
            _trimOriginalStart = clip.TimelineStart;
            _trimOriginalDuration = clip.Duration;
            _multiDragItems = null;  // trim is always single-clip
        }
        else if (localX >= w - EdgeHitZone)
        {
            _isTrimMode = true;
            _trimFromLeft = false;
            _trimOriginalStart = clip.TimelineStart;
            _trimOriginalDuration = clip.Duration;
            _multiDragItems = null;
        }
        else
        {
            _isTrimMode = false;
            // If this clip is part of a multi-selection, drag will move the whole group.
            _multiDragItems = clip.IsSelected ? CaptureMultiDragItems(clip) : null;
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

        if (_dragClip is null || _dragBorder is null) return;
        var pt = e.GetCurrentPoint(ClipCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;

        double deltaX = pt.Position.X - _dragOriginX;
        if (!_isDragging && Math.Abs(deltaX) < 5) return;
        _isDragging = true;

        if (_isTrimMode)
        {
            double deltaSec = deltaX / _pixelsPerSec;
            if (_trimFromLeft)
            {
                double newStartSec = Math.Max(0, _trimOriginalStart.TotalSeconds + deltaSec);
                double newDurSec   = _trimOriginalDuration.TotalSeconds - (newStartSec - _trimOriginalStart.TotalSeconds);
                if (newDurSec < 0.1) return;
                _dragClip.TimelineStart = TimeSpan.FromSeconds(newStartSec);
                _dragClip.Duration      = TimeSpan.FromSeconds(newDurSec);
                Canvas.SetLeft(_dragBorder, newStartSec * _pixelsPerSec);
                _dragBorder.Width = Math.Max(newDurSec * _pixelsPerSec, 4);
            }
            else
            {
                double newDurSec = Math.Max(0.1, _trimOriginalDuration.TotalSeconds + deltaSec);
                _dragClip.Duration = TimeSpan.FromSeconds(newDurSec);
                _dragBorder.Width = Math.Max(newDurSec * _pixelsPerSec, 4);
            }
        }
        else
        {
            double newStartSec = Math.Max(0, _dragOriginClipSec + deltaX / _pixelsPerSec);
            if (ViewModel?.SnapEnabled == true)
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
        }
        e.Handled = true;
    }

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
        var trimOriginalStart    = _trimOriginalStart;
        var trimOriginalDuration = _trimOriginalDuration;
        var dragFromStart        = _dragFromStart;
        var multiDragItems       = _multiDragItems;

        ResetDragState();
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
                trimOriginalStart, trimOriginalDuration,
                clip.TimelineStart, clip.Duration));
        }
        else
        {
            ViewModel?.History.RecordWithoutDo(new ClipMoveAction(clip, dragFromStart, clip.TimelineStart));
            if (multiDragItems is not null)
            {
                foreach (var (other, _, origStart) in multiDragItems)
                {
                    if (other.TimelineStart != origStart)
                        ViewModel?.History.RecordWithoutDo(new ClipMoveAction(other, origStart, other.TimelineStart));
                }
            }
        }

        e.Handled = true;
    }

    /// <summary>Reset drag/trim state when capture is yanked away (lock screen, Win+L, alt-tab, Esc).</summary>
    private void OnClipPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetDragState();
        ResetRazorState();
    }

    private void ResetDragState()
    {
        _dragClip = null;
        _dragBorder = null;
        _dragTrack = null;
        _isDragging = false;
        _isTrimMode = false;
        _multiDragItems = null;
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
            ShowTrackInsertIndicator(ClipCanvas.Height - TrackInsertZone / 2);
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
    private bool IsInBottomInsertZone(double y) =>
        ViewModel is not null && y > ClipCanvas.Height - TrackInsertZone;

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
        if (IsInTopInsertZone(pos.Y))
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

    private Track? CreateAndInsertTrack(MediaType mediaType, bool insertAtTop)
    {
        if (ViewModel is null) return null;
        var kind = mediaType == MediaType.Audio ? TrackKind.Audio : TrackKind.Video;
        var prefix = kind == TrackKind.Video ? "V" : "A";
        var n = ViewModel.Tracks.Count(t => t.Kind == kind) + 1;
        var track = new Track { Label = $"{prefix}{n}", Kind = kind };
        if (insertAtTop) ViewModel.Tracks.Insert(0, track);
        else             ViewModel.Tracks.Add(track);
        ViewModel.Project.IsModified = true;
        return track;
    }
}
