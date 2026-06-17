using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

public sealed partial class WelcomeStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => false;
    public void RevertPreviewIfNeeded() { /* nothing to revert */ }

    public WelcomeStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.Instance.ReducedMotion)
        {
            WordmarkScale.ScaleX = WordmarkScale.ScaleY = 1.0;
            Wordmark.CharacterSpacing = 80;
            Tagline.Opacity = Subline.Opacity = BtnStart.Opacity = 1;
            BtnTx.Y = 0;
            return;
        }

        // CharacterSpacing is Int32 and WinUI 3 has no Int32Animation. Snap it
        // to the final tight value at the start of the entrance and let the
        // scale animation carry the visible compression instead.
        Wordmark.CharacterSpacing = 80;

        var sb = new Storyboard();
        // Wordmark: shrink 1.2 → 1.0.
        sb.Children.Add(MakeDouble(WordmarkScale, "ScaleX", 1.2, 1.0, 900, easeOut: true));
        sb.Children.Add(MakeDouble(WordmarkScale, "ScaleY", 1.2, 1.0, 900, easeOut: true));

        // Tagline + subline + button fade in with staggered begin times.
        sb.Children.Add(MakeDouble(Tagline, "Opacity", 0, 1, 500, beginMs: 400, easeOut: true));
        sb.Children.Add(MakeDouble(Subline, "Opacity", 0, 1, 500, beginMs: 700, easeOut: true));
        sb.Children.Add(MakeDouble(BtnStart, "Opacity", 0, 1, 500, beginMs: 900, easeOut: true));
        sb.Children.Add(MakeDouble(BtnTx,    "Y",     14, 0, 500, beginMs: 900, easeOut: true));
        sb.Begin();
    }

    private void OnStartClick(object sender, RoutedEventArgs e) =>
        StepCompleted?.Invoke(this, EventArgs.Empty);

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
