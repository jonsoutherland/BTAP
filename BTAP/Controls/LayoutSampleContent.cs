using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BTAP.Controls;

/// <summary>
/// Builds lightweight visual mocks of the editor's three dockable panels for
/// use inside <see cref="LayoutPreviewView"/>. These aren't functional — they
/// don't bind to any project state — but they look enough like the real panels
/// that a user can recognise what they're rearranging. Sample clip / media /
/// inspector data is hard-coded so the preview is consistent regardless of
/// what's open in the editor.
/// </summary>
internal static class LayoutSampleContent
{
    private static Brush Res(string key) =>
        (Brush)Application.Current.Resources[key];

    private static SolidColorBrush ArgbBrush(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));

    // ── Library ────────────────────────────────────────────────────────────

    public static FrameworkElement BuildLibrary()
    {
        var root = new Grid
        {
            Background = Res("BgPageBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };

        // Tab strip
        var tabs = new Border
        {
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(10, 6, 10, 0),
        };
        var tabStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        tabStack.Children.Add(SampleTab("Media",   active: true));
        tabStack.Children.Add(SampleTab("Titles",  active: false));
        tabStack.Children.Add(SampleTab("Effects", active: false));
        tabStack.Children.Add(SampleTab("Audio",   active: false));
        tabs.Child = tabStack;
        Grid.SetRow(tabs, 0);
        root.Children.Add(tabs);

        // Search box
        var search = new Border
        {
            Margin = new Thickness(12, 10, 12, 8),
            Background      = Res("BgInputBrush"),
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(5),
            Padding         = new Thickness(8, 5, 8, 5),
            Child = new TextBlock
            {
                Text = "Search media…", FontSize = 11.5,
                Foreground = Res("TextFaintBrush"),
            },
        };
        Grid.SetRow(search, 1);
        root.Children.Add(search);

        // Bin label
        var label = new Border
        {
            Padding = new Thickness(14, 0, 14, 6),
            Child = new TextBlock
            {
                Text = "Bin · 4",
                FontSize = 10.5, CharacterSpacing = 140,
                Foreground = Res("TextFaintBrush"),
            },
        };
        Grid.SetRow(label, 2);
        root.Children.Add(label);

        // Tile grid: 2 columns, 2 rows of media tiles.
        var grid = new Grid
        {
            Margin = new Thickness(12, 0, 12, 12),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
            RowDefinitions    = { new RowDefinition(), new RowDefinition() },
            ColumnSpacing = 8,
            RowSpacing    = 8,
        };
        AddTile(grid, 0, 0, "interview_01", "00:01:22", Color.FromArgb(255, 0x3A, 0x4F, 0x7A));
        AddTile(grid, 1, 0, "broll_park",   "00:00:18", Color.FromArgb(255, 0x6B, 0x8C, 0x4E));
        AddTile(grid, 0, 1, "voiceover",    "00:00:42", Color.FromArgb(255, 0x7A, 0x4F, 0x6B));
        AddTile(grid, 1, 1, "title_card",   "00:00:05", Color.FromArgb(255, 0x4F, 0x6B, 0x7A));
        Grid.SetRow(grid, 3);
        root.Children.Add(grid);

        return root;
    }

    private static FrameworkElement SampleTab(string label, bool active)
    {
        var b = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = active ? Res("BgElevatedBrush") : null,
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            Child = new TextBlock
            {
                Text       = label,
                FontSize   = 11.5,
                FontWeight = active ? FontWeights.Medium : FontWeights.Normal,
                Foreground = active ? Res("TextPrimaryBrush") : Res("TextMutedBrush"),
            },
        };
        return b;
    }

    private static void AddTile(Grid g, int col, int row, string name, string dur, Color fill)
    {
        var card = new Border
        {
            Background      = Res("BgSurfaceBrush"),
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
        };
        var inner = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        var thumb = new Rectangle
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint   = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = fill, Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255,
                                            (byte)(fill.R / 2), (byte)(fill.G / 2), (byte)(fill.B / 2)),
                                       Offset = 1 },
                },
            },
            RadiusX = 3, RadiusY = 3,
            Margin = new Thickness(6, 6, 6, 0),
            MinHeight = 36,
        };
        Grid.SetRow(thumb, 0);
        inner.Children.Add(thumb);

        var meta = new StackPanel { Margin = new Thickness(8, 4, 8, 6), Spacing = 1 };
        meta.Children.Add(new TextBlock
        {
            Text = name, FontSize = 10.5, FontWeight = FontWeights.Medium,
            Foreground = Res("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        meta.Children.Add(new TextBlock
        {
            Text = dur, FontSize = 9.5,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            Foreground = Res("TextFaintBrush"),
        });
        Grid.SetRow(meta, 1);
        inner.Children.Add(meta);
        card.Child = inner;

        Grid.SetColumn(card, col); Grid.SetRow(card, row);
        g.Children.Add(card);
    }

    // ── Center (Program + transport + timeline) ────────────────────────────

    public static FrameworkElement BuildCenter()
    {
        var root = new Grid
        {
            Background = Res("BgPageBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(140) },
            },
        };

        // Program monitor mock — dark canvas with a centred frame.
        var preview = new Border
        {
            Background      = ArgbBrush(255, 0x0A, 0x12, 0x18),
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(14),
        };
        var sample = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            CornerRadius        = new CornerRadius(2),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint   = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 0x2A, 0x55, 0x88), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 0x10, 0x1B, 0x33), Offset = 1 },
                },
            },
            Width = 220, Height = 124,
            Child = new TextBlock
            {
                Text = "Sample preview",
                FontSize = 11, FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = ArgbBrush(180, 0xFF, 0xFF, 0xFF),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };
        preview.Child = sample;
        Grid.SetRow(preview, 0);
        root.Children.Add(preview);

        // Transport bar mock
        var transport = new Border
        {
            Padding         = new Thickness(14, 8, 14, 8),
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 1),
        };
        var transportStack = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        transportStack.Children.Add(new TextBlock
        {
            Text = "⏮", FontSize = 12,
            Foreground = Res("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        transportStack.Children.Add(new Border
        {
            Background = Res("AccentBrush"),
            CornerRadius = new CornerRadius(12),
            Width = 24, Height = 24,
            Child = new TextBlock
            {
                Text = "▶", FontSize = 11,
                Foreground = Res("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        transportStack.Children.Add(new TextBlock
        {
            Text = "⏭", FontSize = 12,
            Foreground = Res("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        transportStack.Children.Add(new TextBlock
        {
            Text = "00:00:12 / 00:00:48",
            FontSize = 10.5,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            Foreground = Res("TextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        transport.Child = transportStack;
        Grid.SetRow(transport, 1);
        root.Children.Add(transport);

        // Timeline mock — ruler + two tracks with sample clips.
        var timeline = new Grid
        {
            Background = Res("BgSurfaceBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(18) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };
        var ruler = new Border
        {
            Background = Res("BgElevatedBrush"),
            BorderBrush = Res("HairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                Text = "0s    5s    10s   15s   20s   25s   30s",
                FontSize = 9, CharacterSpacing = 80,
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                Foreground = Res("TextFaintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetRow(ruler, 0);
        timeline.Children.Add(ruler);

        var v1 = TrackRow(new (double left, double width, Color color, string label)[]
        {
            (8, 120, Color.FromArgb(255, 0x3A, 0x4F, 0x7A), "interview_01"),
            (140, 60, Color.FromArgb(255, 0x6B, 0x8C, 0x4E), "broll"),
            (208, 50, Color.FromArgb(255, 0x4F, 0x6B, 0x7A), "title"),
        });
        Grid.SetRow(v1, 1);
        timeline.Children.Add(v1);

        var a1 = TrackRow(new (double left, double width, Color color, string label)[]
        {
            (8, 252, Color.FromArgb(255, 0x4A, 0x6A, 0x4A), "voiceover"),
        });
        Grid.SetRow(a1, 2);
        timeline.Children.Add(a1);

        Grid.SetRow(timeline, 2);
        root.Children.Add(timeline);

        return root;
    }

    private static FrameworkElement TrackRow((double left, double width, Color color, string label)[] clips)
    {
        var c = new Canvas
        {
            Background = Res("BgPageBrush"),
            Height = 36,
        };
        foreach (var (left, width, color, label) in clips)
        {
            var border = new Border
            {
                Width = width, Height = 26,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = label, FontSize = 9.5,
                    Foreground = ArgbBrush(255, 240, 240, 240),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, 5);
            c.Children.Add(border);
        }
        return c;
    }

    // ── Inspector ──────────────────────────────────────────────────────────

    public static FrameworkElement BuildInspector()
    {
        var root = new Grid
        {
            Background = Res("BgPageBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };

        // Selected clip header
        var header = new Border
        {
            Padding         = new Thickness(14, 12, 14, 10),
            BorderBrush     = Res("HairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        var hStack = new StackPanel { Spacing = 8 };
        var clipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        clipRow.Children.Add(new Border
        {
            Width = 32, Height = 20, CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint   = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x6A, 0x1A, 0x30, 0x40), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x6A, 0x0A, 0x18, 0x30), Offset = 1 },
                },
            },
        });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "interview_01", FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = Res("TextPrimaryBrush"),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "V1 · 00:00:08:00 — 00:00:22:00",
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 10, Foreground = Res("TextFaintBrush"),
        });
        clipRow.Children.Add(titleStack);
        hStack.Children.Add(clipRow);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        tabs.Children.Add(SampleTab("Video",   true));
        tabs.Children.Add(SampleTab("Audio",   false));
        tabs.Children.Add(SampleTab("Effects", false));
        tabs.Children.Add(SampleTab("Color",   false));
        hStack.Children.Add(tabs);
        header.Child = hStack;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Scrollable inspector contents — labeled sliders.
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        AddInspectorRow(content, "POSITION",     "0, 0",        0.5);
        AddInspectorRow(content, "SCALE",        "1.00×",       0.5);
        AddInspectorRow(content, "ROTATION",     "0°",          0.5);
        AddInspectorRow(content, "OPACITY",      "100%",        1.0);
        AddInspectorRow(content, "BLUR",         "0 px",        0.0);
        scroll.Content = content;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        return root;
    }

    private static void AddInspectorRow(StackPanel host, string label, string valueText, double pos)
    {
        var row = new StackPanel { Spacing = 4 };
        var labelRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var labelTb = new TextBlock
        {
            Text = label, FontSize = 10, CharacterSpacing = 140,
            Foreground = Res("TextMutedBrush"),
        };
        Grid.SetColumn(labelTb, 0);
        labelRow.Children.Add(labelTb);
        var valueTb = new TextBlock
        {
            Text = valueText, FontSize = 10.5,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            Foreground = Res("TextFaintBrush"),
        };
        Grid.SetColumn(valueTb, 1);
        labelRow.Children.Add(valueTb);
        row.Children.Add(labelRow);

        // Mock slider — a thin track with a knob at the given position.
        var track = new Grid { Height = 12 };
        track.Children.Add(new Border
        {
            Background = Res("BgInputBrush"),
            Height = 3,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1.5),
        });
        track.Children.Add(new Border
        {
            Background = Res("AccentBrush"),
            Width = 10, Height = 10,
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
            // Margin used to position the knob along the track; outer SizeChanged
            // could compute a precise X, but a percentage-of-fixed-width inline
            // looks correct enough for the mock.
            Margin = new Thickness(pos * 220, 0, 0, 0),
        });
        row.Children.Add(track);

        host.Children.Add(row);
    }
}
