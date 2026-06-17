using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Q2: "What's your favorite color?" — four curated swatches plus a
/// "pick something weirder" link that opens a free ColorPicker. Selecting a
/// swatch commits the accent immediately (so the marble background tints
/// live); user clicks "That'll do" to advance.
///
/// Snapshots prior accent state on entry; <see cref="RevertPreviewIfNeeded"/>
/// restores on Back-without-commit so the user doesn't lose their previous
/// pick by hovering this step.</summary>
public sealed partial class ColorStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => true;

    private readonly (string Name, AccentScheme Scheme, Color Color)[] _swatches =
    [
        ("sage",   AccentScheme.Sage,   Color.FromArgb(0xFF, 0x7F, 0xB0, 0x69)),
        ("blue",   AccentScheme.Blue,   Color.FromArgb(0xFF, 0x5B, 0x9F, 0xE3)),
        ("purple", AccentScheme.Purple, Color.FromArgb(0xFF, 0xA6, 0x7F, 0xE0)),
        ("amber",  AccentScheme.Amber,  Color.FromArgb(0xFF, 0xE0, 0xA3, 0x52)),
        ("rose",   AccentScheme.Rose,   Color.FromArgb(0xFF, 0xD2, 0x7F, 0xA8)),
        ("teal",   AccentScheme.Teal,   Color.FromArgb(0xFF, 0x5B, 0xB8, 0xB0)),
        ("coral",  AccentScheme.Coral,  Color.FromArgb(0xFF, 0xE0, 0x80, 0x70)),
        ("slate",  AccentScheme.Slate,  Color.FromArgb(0xFF, 0x80, 0x90, 0xA8)),
    ];

    // Pre-entry snapshot for revert-on-back. Captured once in Loaded.
    private AccentScheme _priorScheme;
    private bool         _priorUseCustom;
    private string       _priorCustomHex = string.Empty;
    private bool         _committed;

    public ColorStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void RevertPreviewIfNeeded()
    {
        if (_committed) return;
        var s = AppSettingsService.Instance;
        s.Accent          = _priorScheme;
        s.UseCustomAccent = _priorUseCustom;
        s.CustomAccentHex = _priorCustomHex;
        AccentManager.Apply();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = AppSettingsService.Instance;
        _priorScheme    = s.Accent;
        _priorUseCustom = s.UseCustomAccent;
        _priorCustomHex = s.CustomAccentHex;

        BuildSwatches();
    }

    private void BuildSwatches()
    {
        SwatchRow.Children.Clear();
        for (int i = 0; i < _swatches.Length; i++)
        {
            var sw = _swatches[i];
            // Shrunk from 64→52 to fit 8 swatches inside the parent
            // Grid's MaxWidth=640 with hover-scale headroom.
            var border = new Border
            {
                Width = 52, Height = 52,
                Background      = new SolidColorBrush(sw.Color),
                CornerRadius    = new CornerRadius(26),
                BorderBrush     = (Brush)Application.Current.Resources["HairlineBrush"],
                BorderThickness = new Thickness(1),
                RenderTransform = new ScaleTransform { ScaleX = 0.5, ScaleY = 0.5 },
                Opacity         = 0,
                Tag             = sw,
            };
            border.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            border.PointerEntered += OnSwatchHoverEnter;
            border.PointerExited  += OnSwatchHoverExit;
            border.Tapped         += OnSwatchTapped;
            SwatchRow.Children.Add(border);

            // Staggered "pop into existence" entrance.
            if (AppSettingsService.Instance.ReducedMotion)
            {
                border.Opacity = 1;
                ((ScaleTransform)border.RenderTransform).ScaleX = 1;
                ((ScaleTransform)border.RenderTransform).ScaleY = 1;
                continue;
            }
            var sb = new Storyboard();
            sb.Children.Add(MakeDouble(border, "Opacity", 0, 1, 360, beginMs: 100 + i * 80));
            sb.Children.Add(MakeDouble((ScaleTransform)border.RenderTransform, "ScaleX",
                                       0.5, 1.0, 420, beginMs: 100 + i * 80, easeOut: true));
            sb.Children.Add(MakeDouble((ScaleTransform)border.RenderTransform, "ScaleY",
                                       0.5, 1.0, 420, beginMs: 100 + i * 80, easeOut: true));
            sb.Begin();
        }
    }

    private void OnSwatchHoverEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b) return;
        AnimateScale(b, 1.15);
    }

    private void OnSwatchHoverExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border b) return;
        AnimateScale(b, 1.0);
    }

    private void OnSwatchTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border b ||
            b.Tag is not ValueTuple<string, AccentScheme, Color> picked) return;

        var s = AppSettingsService.Instance;
        s.UseCustomAccent = false;
        s.Accent          = picked.Item2;
        AccentManager.Apply();
        _committed = true;

        PlaySplashAt(b, picked.Item3);
        HighlightSelected(b);
        e.Handled = true;
    }

    /// <summary>Splash effect on swatch tap: the shared <c>Splash</c> ellipse
    /// is positioned at the tapped swatch's centre (in <c>SplashOverlay</c>
    /// canvas coords via <see cref="UIElement.TransformToVisual"/>), filled
    /// with the swatch colour, then scaled up while fading out — a coloured
    /// burst emanating from the click point. Uses Storyboards targeting a
    /// XAML-declared ScaleTransform, the only animation recipe confirmed to
    /// render in this environment.</summary>
    private void PlaySplashAt(Border swatch, Color color)
    {
        if (AppSettingsService.Instance.ReducedMotion) return;

        var toCanvas = swatch.TransformToVisual(SplashOverlay);
        var centre = toCanvas.TransformPoint(
            new Windows.Foundation.Point(swatch.ActualWidth / 2.0, swatch.ActualHeight / 2.0));

        // Splash is 40 px diameter at scale 1; position so its centre lands
        // on the swatch centre, then scale up from 0.
        Canvas.SetLeft(Splash, centre.X - Splash.Width / 2.0);
        Canvas.SetTop (Splash, centre.Y - Splash.Height / 2.0);
        Splash.Fill = new SolidColorBrush(color);
        Splash.Opacity = 0.9;
        SplashScale.ScaleX = 0;
        SplashScale.ScaleY = 0;

        // Scale 0 → 60 over 700 ms means the splash ends up roughly 2400 px
        // wide (40 × 60). That covers any realistic window size — wider than
        // a 4K monitor's short edge — without us having to know the actual
        // window bounds.
        var sb = new Storyboard();
        sb.Children.Add(MakeDouble(SplashScale, "ScaleX", 0,   60, 700, easeOut: true));
        sb.Children.Add(MakeDouble(SplashScale, "ScaleY", 0,   60, 700, easeOut: true));
        sb.Children.Add(MakeDouble(Splash,      "Opacity", 0.9, 0, 700, easeOut: true));
        sb.Begin();
    }

    private void HighlightSelected(Border picked)
    {
        foreach (var child in SwatchRow.Children)
        {
            if (child is not Border b) continue;
            b.BorderBrush     = b == picked
                ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                : (Brush)Application.Current.Resources["HairlineBrush"];
            b.BorderThickness = new Thickness(b == picked ? 3 : 1);
        }
    }

    private void OnPickWeirderClick(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            ColorSpectrumShape   = ColorSpectrumShape.Box,
            IsAlphaEnabled       = false,
            IsAlphaSliderVisible = false,
            IsHexInputVisible    = true,
        };
        var s = AppSettingsService.Instance;
        if (AccentManager.TryParseHex(s.CustomAccentHex, out var initial)) picker.Color = initial;
        else                                                                picker.Color = AccentManager.ResolveColor();

        var apply = new Button
        {
            Content = "Apply",
            Style = (Style)Application.Current.Resources["BtapButtonStyle"],
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(picker);
        content.Children.Add(apply);
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };

        apply.Click += (_, _) =>
        {
            var c = picker.Color;
            s.UseCustomAccent = true;
            s.CustomAccentHex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
            AccentManager.Apply();
            _committed = true;
            flyout.Hide();
            // Clear curated-swatch highlight since we're now on a custom colour.
            foreach (var child in SwatchRow.Children)
                if (child is Border b)
                {
                    b.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
                    b.BorderThickness = new Thickness(1);
                }
        };
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        // If they never picked anything, leave whatever was already in
        // settings — but still mark as committed so RevertPreviewIfNeeded
        // doesn't trample what was already there.
        _committed = true;
        StepCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ── Local animation helpers ─────────────────────────────────────────────

    private static void AnimateScale(Border target, double to)
    {
        if (target.RenderTransform is not ScaleTransform tx) return;
        var sb = new Storyboard();
        sb.Children.Add(MakeDouble(tx, "ScaleX", tx.ScaleX, to, 180, easeOut: true));
        sb.Children.Add(MakeDouble(tx, "ScaleY", tx.ScaleY, to, 180, easeOut: true));
        sb.Begin();
    }

    private static DoubleAnimation MakeDouble(DependencyObject target, string property,
                                              double from, double to, int durationMs,
                                              int beginMs = 0, bool easeOut = false)
    {
        var anim = new DoubleAnimation
        {
            From = from, To = to,
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            Duration  = TimeSpan.FromMilliseconds(durationMs),
        };
        if (easeOut) anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }
}
