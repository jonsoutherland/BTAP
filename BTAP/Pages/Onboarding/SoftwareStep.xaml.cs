using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Q1: "What are you coming from?" — maps the user's prior NLE to a
/// matched (LayoutPreset, KeybindPreset) pair. Selecting a row commits both
/// and raises <see cref="StepCompleted"/>.</summary>
public sealed partial class SoftwareStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => true;
    public void RevertPreviewIfNeeded() { /* settings are committed on click only */ }

    private readonly (string Label, string Subtitle, LayoutPreset Layout, KeybindPreset Keys)[] _options =
    [
        ("Premiere Pro", "complex layout · Premiere-style keys", LayoutPreset.Complex,  KeybindPreset.PremiereLike),
        ("Final Cut Pro","moderate layout · keep it minimal",    LayoutPreset.Moderate, KeybindPreset.Default),
        ("DaVinci Resolve","complex layout · default keys",      LayoutPreset.Complex,  KeybindPreset.Default),
        ("Nothing fancy", "simple layout · minimal keys",        LayoutPreset.Simple,   KeybindPreset.Minimal),
    ];

    public SoftwareStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OptionsHost.Children.Clear();
        for (int i = 0; i < _options.Length; i++)
        {
            var opt = _options[i];
            var row = BuildOptionRow(opt.Label, opt.Subtitle);
            row.Tag = i;
            row.PointerEntered += OnRowHoverEnter;
            row.PointerExited  += OnRowHoverExit;
            row.Tapped         += OnRowTapped;
            OptionsHost.Children.Add(row);
        }
        AnimateOptionsIn();
    }

    private static Border BuildOptionRow(string label, string subtitle)
    {
        var underline = new Rectangle
        {
            Height = 1,
            Fill   = (Brush)Application.Current.Resources["AccentBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0,
        };
        var name = new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)Application.Current.Resources["SerifFont"],
            FontStyle  = Windows.UI.Text.FontStyle.Italic,
            FontSize   = 28,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        var meta = new TextBlock
        {
            Text = subtitle,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize   = 10.5,
            CharacterSpacing = 100,
            Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            Margin = new Thickness(0, 2, 0, 0),
        };
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(name);
        stack.Children.Add(meta);
        stack.Children.Add(underline);

        var border = new Border
        {
            Padding = new Thickness(20, 12, 20, 12),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 460,
            RenderTransform = new TranslateTransform(),
        };
        border.RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
        return border;
    }

    private void OnRowHoverEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row) return;
        // Slide right + reveal underline. Subtle but clearly interactive.
        AnimateY(row, byProperty: "X", from: ((TranslateTransform)row.RenderTransform).X,
                 to: 14, ms: 220, easeOut: true);
        if (row.Child is StackPanel s && s.Children.Count >= 3 && s.Children[2] is Rectangle r)
            AnimateOpacity(r, r.Opacity, 1, ms: 220, easeOut: true);
        row.Background = (Brush)Application.Current.Resources["BgSurfaceBrush"];
    }

    private void OnRowHoverExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row) return;
        AnimateY(row, byProperty: "X", from: ((TranslateTransform)row.RenderTransform).X,
                 to: 0, ms: 220, easeOut: true);
        if (row.Child is StackPanel s && s.Children.Count >= 3 && s.Children[2] is Rectangle r)
            AnimateOpacity(r, r.Opacity, 0, ms: 220, easeOut: true);
        row.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int idx) return;
        var opt = _options[idx];

        // Commit immediately. Both preset systems are no-ops if the user picks
        // the same values; KeyBindingPresets writes through to the on-disk
        // keybindings.json so the EditorPage picks it up on next nav.
        var settings = AppSettingsService.Instance;
        settings.LayoutPreset  = opt.Layout;
        settings.KeybindPreset = opt.Keys;
        KeyBindingPresets.Apply(new KeyBindingsService(), opt.Keys);

        // Tiny "fly up" before the host transitions. Subtle — too much and the
        // step transition feels stuttery.
        var tx = (TranslateTransform)row.RenderTransform;
        AnimateY(row, byProperty: "Y", from: tx.Y, to: -10, ms: 160, easeOut: true);
        DispatcherQueue?.TryEnqueue(() =>
        {
            StepCompleted?.Invoke(this, EventArgs.Empty);
        });
        e.Handled = true;
    }

    private void AnimateOptionsIn()
    {
        if (AppSettingsService.Instance.ReducedMotion) return;
        // Stagger each row in from the left.
        for (int i = 0; i < OptionsHost.Children.Count; i++)
        {
            if (OptionsHost.Children[i] is not Border row) continue;
            if (row.RenderTransform is not TranslateTransform tx) continue;
            tx.X = -40;
            row.Opacity = 0;
            var sb = new Storyboard();
            sb.Children.Add(MakeDouble(row,  "Opacity",  0, 1, 320, beginMs: 80 + i * 60));
            sb.Children.Add(MakeDouble(tx,   "X",       -40, 0, 360, beginMs: 80 + i * 60, easeOut: true));
            sb.Begin();
        }
    }

    // ── Small animation helpers (local — keep step files self-contained) ────

    private static void AnimateY(FrameworkElement target, string byProperty,
                                 double from, double to, int ms, bool easeOut = false)
    {
        if (target.RenderTransform is not TranslateTransform tx) return;
        var anim = MakeDouble(tx, byProperty, from, to, ms, easeOut: easeOut);
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private static void AnimateOpacity(UIElement target, double from, double to,
                                       int ms, bool easeOut = false)
    {
        var anim = MakeDouble(target, "Opacity", from, to, ms, easeOut: easeOut);
        var sb = new Storyboard();
        sb.Children.Add(anim);
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
