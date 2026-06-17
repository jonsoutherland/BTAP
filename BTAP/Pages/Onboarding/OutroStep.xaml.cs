using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Final step. Clicking "Enter editor" marks onboarding complete and
/// navigates to LandingPage. The host's StepCompleted contract is unused here
/// — there's no next step to advance to.</summary>
public sealed partial class OutroStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;

    /// <summary>Raised when the user clicks "Enter editor". The host page
    /// (OnboardingPage) listens for this so it can run a full-page curtain
    /// slide-up before navigating away — keeping the curtain animation at
    /// the host level (not the step level) means the chrome lifts too.</summary>
    public event EventHandler? EnterEditorRequested;

    public bool CanGoBack => false;
    public void RevertPreviewIfNeeded() { /* nothing to revert */ }

    public OutroStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.Instance.ReducedMotion)
        {
            Wordmark.Opacity = Caption.Opacity = DisclaimerPanel.Opacity = BtnEnter.Opacity = 1;
            WordmarkScale.ScaleX = WordmarkScale.ScaleY = 1.0;
            BtnTx.Y = 0;
            return;
        }
        var sb = new Storyboard();
        sb.Children.Add(MakeDouble(Wordmark,        "Opacity", 0,   1,   500, easeOut: true));
        sb.Children.Add(MakeDouble(WordmarkScale,   "ScaleX",  0.9, 1.0, 600, easeOut: true));
        sb.Children.Add(MakeDouble(WordmarkScale,   "ScaleY",  0.9, 1.0, 600, easeOut: true));
        sb.Children.Add(MakeDouble(Caption,         "Opacity", 0,   1,   500, beginMs: 250, easeOut: true));
        sb.Children.Add(MakeDouble(DisclaimerPanel, "Opacity", 0,   1,   500, beginMs: 450, easeOut: true));
        sb.Children.Add(MakeDouble(BtnEnter,        "Opacity", 0,   1,   500, beginMs: 700, easeOut: true));
        sb.Children.Add(MakeDouble(BtnTx,           "Y",       14,  0,   500, beginMs: 700, easeOut: true));
        sb.Begin();
    }

    /// <summary>No-op: kept so the XAML <c>Checked</c>/<c>Unchecked</c>
    /// hooks don't dangle. The button isn't disabled — the unchecked case
    /// is handled in <see cref="OnEnterClick"/> via a humorous popup.</summary>
    private void OnAckChanged(object sender, RoutedEventArgs e) { }

    /// <summary>Rotating set of humorous nags shown when the user clicks
    /// "Enter editor" without ticking the acknowledgement checkbox. Picked
    /// at random per click so a repeat-clicker gets variety.</summary>
    private static readonly string[] UncheckedPrompts =
    {
        "Someone doesn't like to follow instructions.",
        "The checkbox is right there. We'll wait.",
        "Bold of you to skip the fine print.",
        "An unchecked box is a sad box.",
        "Plot twist: the checkbox is the gate. Tick it to pass.",
        "We see you trying to skip the disclosure. We see you.",
        "Click the box. It just wants to be clicked.",
        "You can't skip the disclosure. It's the only paywall.",
        "Read the fine print — it's the only fine print you'll like.",
        "Two extra clicks. That's the price of admission.",
    };

    private static readonly Random _promptRng = new();

    private void OnEnterClick(object sender, RoutedEventArgs e)
    {
        if (AiAckCheckbox.IsChecked != true)
        {
            ShowUncheckedNag();
            return;
        }

        AppSettingsService.Instance.HasCompletedOnboarding = true;
        // Hand off to the host page so the curtain slide-up animates the
        // whole onboarding (chrome included), not just this step's content.
        // The host is responsible for the actual NavigateToLanding call once
        // its curtain animation finishes.
        if (EnterEditorRequested is { } handler)
        {
            handler(this, EventArgs.Empty);
        }
        else
        {
            // Fallback if no host is subscribed (e.g. step instantiated in a
            // test harness): navigate directly so the click still does
            // something useful.
            (Application.Current as App)?.GetMainWindow()?.NavigateToLanding();
        }
    }

    /// <summary>Shows a small Flyout anchored to the Enter editor button
    /// containing one of <see cref="UncheckedPrompts"/>. Random each time
    /// so a repeat-clicker gets fresh nags. Flyout (not ContentDialog) so
    /// it dismisses on outside-tap and doesn't trap the user.</summary>
    private void ShowUncheckedNag()
    {
        var msg = UncheckedPrompts[_promptRng.Next(UncheckedPrompts.Length)];
        var content = new TextBlock
        {
            Text = msg,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)
                Application.Current.Resources["SerifFont"],
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
        };
        var flyout = new Flyout { Content = content };
        flyout.ShowAt(BtnEnter);
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
