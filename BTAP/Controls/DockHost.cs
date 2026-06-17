using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using BTAP.Models;

namespace BTAP.Controls;

/// <summary>
/// Renders a <see cref="DockNode"/> tree as nested Grids with GridSplitters between
/// children. Each leaf gets a draggable header bar; dragging it over another leaf
/// reveals four drop zones (N/S/E/W) and dropping inserts the dragged panel on
/// that side. The host fires <see cref="TreeChanged"/> after every mutation so
/// the page can persist the layout.
///
/// Panels are passed in once via <see cref="Configure"/>; the host re-parents them
/// between ContentPresenters when the layout changes. Each panel keeps its own
/// state (selection, scroll, etc.) — only the tree shape is rebuilt.
/// </summary>
public sealed class DockHost : Grid
{
    private readonly Canvas _overlay = new()
    {
        IsHitTestVisible = false,
        Background       = null,
    };

    private readonly Dictionary<string, FrameworkElement> _panels = new();
    private readonly Dictionary<string, string>           _headers = new();
    private readonly Dictionary<string, ContentPresenter> _presenters = new();
    private readonly Dictionary<string, FrameworkElement> _leafRoots  = new(); // leaf wrapper for hit-testing
    private readonly Dictionary<string, Border>           _leafOutlines = new(); // drop-target glow per leaf
    private readonly Dictionary<string, FrameworkElement> _leafHeaders  = new(); // drag bars by id

    private DockNode _root = new DockLeaf { PanelId = string.Empty };

    /// <summary>Fires after the user reshapes the layout via drag-and-drop.
    /// Argument is the serialised tree string; consumer persists it.</summary>
    public event EventHandler<string>? TreeChanged;

    /// <summary>When false, panels become read-only: drag headers are gone,
    /// splitters don't resize, no DnD. Used in the live editor so the user
    /// can't accidentally rearrange their workspace while editing video.
    /// Set this BEFORE calling <see cref="Configure"/> — Rebuild reads it once.</summary>
    public bool IsLayoutEditable { get; set; } = false;

    // ── DnD state ──────────────────────────────────────────────────────────
    // Click-on-header doesn't start a drag immediately — we wait for the
    // pointer to travel at least DragThresholdPx in any direction before
    // committing to drag visuals. Lets users click headers for context without
    // tripping the layout-edit mode.
    private const double DragThresholdPx = 5.0;

    private bool        _dragging;          // drag visuals are showing
    private bool        _armed;             // pointer is captured, waiting for threshold
    private string?     _dragPanelId;
    private Point       _dragStartPos;
    private string?     _hoverTargetPanelId;
    private DropDirection _hoverDirection;
    private Pointer?    _capturedPointer;
    private FrameworkElement? _dragSource;
    private Border?     _ghost;
    private readonly List<Border> _dropIndicators = new();

    public DockHost()
    {
        Background = null;
        Children.Add(_overlay);
        SetRow(_overlay, 0);
        SetColumn(_overlay, 0);

        // Esc aborts an in-flight drag without applying the move — a familiar
        // bail-out for users who started dragging and changed their mind.
        IsTabStop = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && (_armed || _dragging))
            {
                CancelDrag();
                e.Handled = true;
            }
        };
    }

    /// <summary>Initialises the host. <paramref name="panels"/> maps panel ID to
    /// the content control that renders that panel; <paramref name="headers"/>
    /// maps the same IDs to display titles for the drag bar.</summary>
    public void Configure(IReadOnlyDictionary<string, FrameworkElement> panels,
                          IReadOnlyDictionary<string, string> headers,
                          DockNode root)
    {
        _panels.Clear();
        _headers.Clear();
        foreach (var (k, v) in panels)  _panels[k] = v;
        foreach (var (k, v) in headers) _headers[k] = v;
        _root = root;
        Rebuild();
    }

    /// <summary>Swaps in a new tree (e.g. when a preset is applied) and re-renders.</summary>
    public void ApplyTree(DockNode root)
    {
        _root = root;
        Rebuild();
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    private void Rebuild()
    {
        // Pull every panel out of its presenter so we can re-parent freely.
        foreach (var p in _presenters.Values) p.Content = null;
        _presenters.Clear();
        _leafRoots.Clear();
        _leafOutlines.Clear();
        _leafHeaders.Clear();

        // Wipe everything except the persistent overlay.
        for (int i = Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(Children[i], _overlay)) Children.RemoveAt(i);
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition       { Height = new GridLength(1, GridUnitType.Star) });

        // Build the rendered tree into a single container Grid and add it to *this*.
        var rendered = Render(_root);
        SetRow(rendered, 0);
        SetColumn(rendered, 0);
        Children.Insert(0, rendered); // behind the overlay

        SetRow(_overlay, 0);
        SetColumn(_overlay, 0);
    }

    /// <summary>Builds the visual tree for <paramref name="node"/>. Splits become
    /// 2-cell Grids with a GridSplitter on the boundary; leaves become a
    /// header-bar + ContentPresenter pair.</summary>
    private FrameworkElement Render(DockNode node)
    {
        if (node is DockLeaf leaf) return RenderLeaf(leaf);
        if (node is DockSplit split) return RenderSplit(split);
        return new Border();
    }

    private FrameworkElement RenderLeaf(DockLeaf leaf)
    {
        var holder = new Grid();

        // Panel content fills the leaf; the header overlays on top so the panel
        // doesn't lose vertical real estate when the header is hidden. This is
        // why we don't use RowDefinitions here — header sits in Row 0 of a
        // single-row grid with Top alignment, so it can hide without affecting
        // layout.
        var presenter = new ContentPresenter();
        if (_panels.TryGetValue(leaf.PanelId, out var content))
            presenter.Content = content;
        holder.Children.Add(presenter);

        // Header + drop-outline only exist in editable mode. In the live editor
        // we skip them so the panel is purely content — the user customises
        // layout from Settings → Layout, not in-place here.
        if (IsLayoutEditable)
        {
            var headerText = _headers.TryGetValue(leaf.PanelId, out var t) ? t : leaf.PanelId;
            var header = BuildHeader(leaf.PanelId, headerText);
            header.VerticalAlignment   = VerticalAlignment.Top;
            header.HorizontalAlignment = HorizontalAlignment.Stretch;
            header.Opacity = 0;
            holder.Children.Add(header);

            holder.PointerEntered += (_, _) =>
            {
                if (!_dragging) header.Opacity = 1;
            };
            holder.PointerExited += (_, _) =>
            {
                if (!_dragging && _hoverTargetPanelId != leaf.PanelId) header.Opacity = 0;
            };

            var outline = new Border
            {
                BorderThickness  = new Thickness(1),
                BorderBrush      = (Brush)Application.Current.Resources["AccentBrush"],
                Opacity          = 0,
                IsHitTestVisible = false,
                CornerRadius     = new CornerRadius(2),
                Margin           = new Thickness(2),
            };
            holder.Children.Insert(1, outline);
            _leafOutlines[leaf.PanelId] = outline;
            _leafHeaders[leaf.PanelId]  = header;
        }

        _presenters[leaf.PanelId]   = presenter;
        _leafRoots[leaf.PanelId]    = holder;
        return holder;
    }

    private FrameworkElement BuildHeader(string panelId, string title)
    {
        var bar = new Border
        {
            Height          = 22,
            Background      = (Brush)Application.Current.Resources["BgElevatedBrush"],
            BorderBrush     = (Brush)Application.Current.Resources["HairlineBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(8, 0, 8, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) },
            },
        };
        var grip = new TextBlock
        {
            Text       = "∷",
            FontSize   = 12,
            Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 6, 2),
        };
        SetColumn(grip, 0);
        var name = new TextBlock
        {
            Text       = title,
            FontSize   = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            CharacterSpacing = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetColumn(name, 1);
        grid.Children.Add(grip);
        grid.Children.Add(name);
        bar.Child = grid;

        // Drag source
        bar.Tag = panelId;
        bar.PointerEntered += (_, _) => bar.Background =
            (Brush)Application.Current.Resources["BgSurfaceBrush"];
        bar.PointerExited += (_, _) => bar.Background =
            (Brush)Application.Current.Resources["BgElevatedBrush"];
        bar.PointerPressed += OnHeaderPointerPressed;
        bar.PointerMoved   += OnHeaderPointerMoved;
        bar.PointerReleased += OnHeaderPointerReleased;
        bar.PointerCaptureLost += (_, _) => CancelDrag();
        ToolTipService.SetToolTip(bar, "Drag to move panel");
        return bar;
    }

    private FrameworkElement RenderSplit(DockSplit split)
    {
        var ratio = Math.Clamp(split.Ratio, 0.05, 0.95);
        var grid = new Grid();

        if (split.Orientation == DockOrientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });

            var first  = Render(split.First);
            var second = Render(split.Second);
            SetColumn(first, 0);
            SetColumn(second, 2);

            var splitter = BuildSplitter(grid, split, isHorizontal: true);
            SetColumn(splitter, 1);

            grid.Children.Add(first);
            grid.Children.Add(splitter);
            grid.Children.Add(second);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });

            var first  = Render(split.First);
            var second = Render(split.Second);
            SetRow(first, 0);
            SetRow(second, 2);

            var splitter = BuildSplitter(grid, split, isHorizontal: false);
            SetRow(splitter, 1);

            grid.Children.Add(first);
            grid.Children.Add(splitter);
            grid.Children.Add(second);
        }

        return grid;
    }

    /// <summary>Builds a thin draggable strip between two children of <paramref name="grid"/>.
    /// The visible line is 1 px but the hit zone is 8 px so users don't have to
    /// pixel-hunt to grab it. Captures the pointer on press, updates the
    /// underlying split's Ratio per pointer-move delta, and re-applies it to
    /// the grid's star-sized columns/rows.</summary>
    private Border BuildSplitter(Grid grid, DockSplit split, bool isHorizontal)
    {
        // Outer Border = hit zone (transparent), inner line = visible 1px stroke.
        var bar = new Border
        {
            Background  = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Width  = isHorizontal ? 8 : double.NaN,
            Height = isHorizontal ? double.NaN : 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            ManipulationMode    = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
            Padding             = isHorizontal ? new Thickness(3, 0, 3, 0) : new Thickness(0, 3, 0, 3),
        };
        var line = new Border
        {
            Background = (Brush)Application.Current.Resources["HairlineBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };
        bar.Child = line;

        // In read-only mode the splitter is just a static hairline — no hover,
        // no drag, no hit-test interaction. (We could omit the hit-zone padding
        // too but keeping the structure identical avoids layout shifts between
        // editable and read-only renders.)
        if (!IsLayoutEditable)
        {
            bar.IsHitTestVisible = false;
            return bar;
        }

        // Subtle hover: thicken the line and tint slightly, instead of filling
        // the whole hit zone with the accent — that was visually jarring.
        bar.PointerEntered += (_, _) =>
        {
            if (isHorizontal) line.Width  = 2;
            else              line.Height = 2;
            line.Background = (Brush)Application.Current.Resources["TextFaintBrush"];
        };
        bar.PointerExited += (_, _) =>
        {
            if (isHorizontal) line.Width  = double.NaN;
            else              line.Height = double.NaN;
            line.Background = (Brush)Application.Current.Resources["HairlineBrush"];
        };

        Pointer? captured = null;
        Point start = default;
        double startRatio = split.Ratio;
        bool dragging = false;

        bar.PointerPressed += (s, e) =>
        {
            if (s is not Border b) return;
            captured = e.Pointer;
            b.CapturePointer(e.Pointer);
            start = e.GetCurrentPoint(grid).Position;
            startRatio = split.Ratio;
            dragging = true;
            e.Handled = true;
        };
        bar.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var p = e.GetCurrentPoint(grid).Position;
            double total = isHorizontal ? grid.ActualWidth : grid.ActualHeight;
            if (total <= 1) return;
            double delta = isHorizontal ? (p.X - start.X) : (p.Y - start.Y);
            double newRatio = Math.Clamp(startRatio + delta / total, 0.05, 0.95);
            split.Ratio = newRatio;
            if (isHorizontal)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(newRatio,     GridUnitType.Star);
                grid.ColumnDefinitions[2].Width = new GridLength(1 - newRatio, GridUnitType.Star);
            }
            else
            {
                grid.RowDefinitions[0].Height = new GridLength(newRatio,     GridUnitType.Star);
                grid.RowDefinitions[2].Height = new GridLength(1 - newRatio, GridUnitType.Star);
            }
            e.Handled = true;
        };
        bar.PointerReleased += (s, e) =>
        {
            if (!dragging) return;
            if (s is Border b && captured is not null)
                try { b.ReleasePointerCapture(captured); } catch { }
            dragging = false;
            captured = null;
            EmitTreeChanged();
            e.Handled = true;
        };
        bar.PointerCaptureLost += (_, _) => { dragging = false; captured = null; };

        return bar;
    }

    // ── Drag-and-drop ───────────────────────────────────────────────────────

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string panelId) return;
        // Don't try to drag the only panel; nowhere to drop it.
        if (DockTree.FindLeaf(_root, panelId)?.Parent is null) return;

        // Arm the drag but don't show visuals yet — wait for the pointer to
        // pass the threshold. Avoids spurious "I clicked the header and ghost
        // appeared" moments when the user just wanted to dismiss focus.
        _armed         = true;
        _dragging      = false;
        _dragPanelId   = panelId;
        _dragSource    = fe;
        _capturedPointer = e.Pointer;
        _dragStartPos  = e.GetCurrentPoint(this).Position;
        fe.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_armed && !_dragging) return;
        var pos = e.GetCurrentPoint(this).Position;

        if (_armed && !_dragging)
        {
            double dx = pos.X - _dragStartPos.X;
            double dy = pos.Y - _dragStartPos.Y;
            if (dx * dx + dy * dy < DragThresholdPx * DragThresholdPx) return;
            BeginDragVisuals(pos);
        }

        if (_dragging)
        {
            MoveGhost(pos);
            UpdateHoverTarget(pos);
            e.Handled = true;
        }
    }

    private void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_armed && !_dragging) return;
        e.Handled = true;
        var commitDrag = _dragging;
        var src = _dragPanelId;
        var tgt = _hoverTargetPanelId;
        var dir = _hoverDirection;

        EndDrag();

        if (commitDrag && src is not null && tgt is not null && src != tgt)
        {
            _root = DockTree.Move(_root, src, tgt, dir);
            Rebuild();
            EmitTreeChanged();
        }
    }

    /// <summary>Transition from "armed, waiting for threshold" to "drag in
    /// progress, visuals shown". Splits the work so a click without movement
    /// never lights up the drop-target indicators.</summary>
    private void BeginDragVisuals(Point at)
    {
        _dragging = true;
        if (_dragPanelId is not null) ShowGhost(_dragPanelId, at);

        // Pin every droppable leaf's header open and outline it so the user
        // sees ALL the places they can drop, not just whichever one their
        // pointer happens to be over. Premiere does the same.
        foreach (var (id, header) in _leafHeaders)
            header.Opacity = 1;
        foreach (var (id, outline) in _leafOutlines)
        {
            if (id == _dragPanelId) continue;
            outline.Opacity = 0.35;
        }
    }

    private void CancelDrag()
    {
        if (!_armed && !_dragging) return;
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragSource is not null && _capturedPointer is not null)
        {
            try { _dragSource.ReleasePointerCapture(_capturedPointer); } catch { }
        }
        _armed             = false;
        _dragging          = false;
        _dragPanelId       = null;
        _hoverTargetPanelId = null;
        _dragSource        = null;
        _capturedPointer   = null;
        HideGhost();
        ClearDropIndicators();

        // Re-hide headers and outlines that were forced visible during the drag.
        foreach (var (_, outline) in _leafOutlines) outline.Opacity = 0;
        foreach (var (_, header)  in _leafHeaders)  header.Opacity = 0;
    }

    private void ShowGhost(string panelId, Point at)
    {
        var label = _headers.TryGetValue(panelId, out var t) ? t : panelId;
        _ghost = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 0, 0)),
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 4, 10, 5),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            },
        };
        _overlay.Children.Add(_ghost);
        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost,  at.Y + 12);
    }

    private void MoveGhost(Point at)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost,  at.Y + 12);
    }

    private void HideGhost()
    {
        if (_ghost is not null) _overlay.Children.Remove(_ghost);
        _ghost = null;
    }

    private void ClearDropIndicators()
    {
        foreach (var b in _dropIndicators) _overlay.Children.Remove(b);
        _dropIndicators.Clear();
    }

    /// <summary>Decide which leaf the pointer is over and which edge zone it's
    /// closest to, then redraw the four drop indicators.</summary>
    private void UpdateHoverTarget(Point pos)
    {
        ClearDropIndicators();
        _hoverTargetPanelId = null;

        // Find the leaf whose bounds contain the pointer (excluding the source).
        FrameworkElement? hit = null;
        string? hitId = null;
        foreach (var (id, fe) in _leafRoots)
        {
            if (id == _dragPanelId) continue;
            var rect = BoundsRelativeToHost(fe);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            if (pos.X < rect.X || pos.X > rect.Right || pos.Y < rect.Y || pos.Y > rect.Bottom) continue;
            hit = fe; hitId = id; break;
        }
        if (hit is null || hitId is null) return;

        var bounds = BoundsRelativeToHost(hit);
        _hoverTargetPanelId = hitId;

        // Edge-band size — 28% of the smaller dimension, capped so big panels
        // don't make the center area unreachable.
        double band = Math.Min(Math.Min(bounds.Width, bounds.Height) * 0.28, 80);

        // Decide which zone we're in by distance to each edge.
        double dL = pos.X - bounds.X;
        double dR = bounds.Right - pos.X;
        double dT = pos.Y - bounds.Y;
        double dB = bounds.Bottom - pos.Y;
        double min = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
        if      (min == dL) _hoverDirection = DropDirection.West;
        else if (min == dR) _hoverDirection = DropDirection.East;
        else if (min == dT) _hoverDirection = DropDirection.North;
        else                _hoverDirection = DropDirection.South;

        // Draw a translucent rectangle showing where the dragged panel will land.
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        Rect zone = _hoverDirection switch
        {
            DropDirection.West  => new Rect(bounds.X,               bounds.Y, bounds.Width / 2, bounds.Height),
            DropDirection.East  => new Rect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
            DropDirection.North => new Rect(bounds.X, bounds.Y,               bounds.Width, bounds.Height / 2),
            DropDirection.South => new Rect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
            _ => bounds,
        };

        var fill = new Border
        {
            Background      = accent,
            Opacity         = 0.22,
            BorderBrush     = accent,
            BorderThickness = new Thickness(2),
            CornerRadius    = new CornerRadius(2),
            Width  = Math.Max(1, zone.Width),
            Height = Math.Max(1, zone.Height),
            IsHitTestVisible = false,
        };
        _overlay.Children.Add(fill);
        Canvas.SetLeft(fill, zone.X);
        Canvas.SetTop(fill,  zone.Y);
        _dropIndicators.Add(fill);
    }

    private Rect BoundsRelativeToHost(FrameworkElement fe)
    {
        try
        {
            var transform = fe.TransformToVisual(this);
            var origin    = transform.TransformPoint(new Point(0, 0));
            return new Rect(origin.X, origin.Y, fe.ActualWidth, fe.ActualHeight);
        }
        catch { return new Rect(0, 0, 0, 0); }
    }

    private void EmitTreeChanged()
    {
        try { TreeChanged?.Invoke(this, DockTree.Serialize(_root)); }
        catch { }
    }
}
