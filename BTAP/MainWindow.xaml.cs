using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using BTAP.Pages;
using BTAP.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace BTAP;

public sealed partial class MainWindow : Window
{
    private readonly AppSettingsService _settings = AppSettingsService.Instance;

    public MainWindow()
    {
        InitializeComponent();

        // Configure window chrome
        ExtendsContentIntoTitleBar = true;

        // Set minimum size and start maximized
        AppWindow.Resize(new SizeInt32(1440, 900));
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);

        ApplyThemeFromSettings();
        _settings.Changed += (_, _) => ApplyThemeFromSettings();

        // First-run gate: fresh installs land on the animated onboarding;
        // returning users go straight to LandingPage.
        RootFrame.Navigate(
            _settings.HasCompletedOnboarding ? typeof(LandingPage) : typeof(OnboardingPage),
            null,
            new SuppressNavigationTransitionInfo());
    }

    /// <summary>Pushes the user's theme choice onto the root Frame. AppColors.xaml
    /// holds both light and dark palettes in ThemeDictionaries, so every brush
    /// bound via {ThemeResource} repaints when ActualTheme changes here.</summary>
    private void ApplyThemeFromSettings()
    {
        RootFrame.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };
    }

    public void NavigateToEditor(Models.Project project)
    {
        RootFrame.Navigate(typeof(EditorPage), project);
    }

    public void NavigateToLanding(NavigationTransitionInfo? transition = null)
    {
        RootFrame.Navigate(typeof(LandingPage), null,
            transition ?? new SuppressNavigationTransitionInfo());
        // LandingPage is the app's home; we never want the user to GoBack
        // into an onboarding step or a closed editor from here.
        RootFrame.BackStack.Clear();
    }

    /// <summary>"Roll tape" exit from the onboarding outro. Plays a two-phase
    /// curtain that hides the OnboardingPage → LandingPage navigation behind
    /// a solid panel:
    ///   • Phase 1 — accent-coloured curtain rises from the bottom edge over
    ///     the OnboardingPage. Implemented as a ScaleY 0→1 animation with
    ///     RenderTransformOrigin (0.5, 1).
    ///   • Mid-point — at full coverage, navigate to LandingPage with a
    ///     suppressed transition so the page swap is instantaneous and
    ///     invisible behind the curtain.
    ///   • Phase 2 — RenderTransformOrigin flips to (0.5, 0) and ScaleY
    ///     animates 1→0, so the curtain "lifts off the top" of the screen,
    ///     progressively revealing the LandingPage underneath.
    ///
    /// Lives on MainWindow (not OnboardingPage) so the curtain survives the
    /// Frame navigation between the two phases. ScaleY animation is used
    /// rather than a TranslateY because Storyboards targeting
    /// TranslateTransform.Y haven't been confirmed to render in this
    /// environment, whereas ScaleTransform.ScaleY on a XAML-declared
    /// transform is the same recipe ThemeStep's sun-rays animation uses
    /// and is confirmed to work.</summary>
    public async Task RunLandingCurtainAsync()
    {
        const int riseMs = 480;
        const int liftMs = 520;

        AnimDebug.Log("RunLandingCurtainAsync ENTER — phase 1 (rise) starting");
        CurtainOverlay.RenderTransformOrigin = new Point(0.5, 1);
        CurtainOverlayScale.ScaleY = 0;
        await AnimateScaleYAsync(0, 1, riseMs, new CubicEase { EasingMode = EasingMode.EaseIn });
        AnimDebug.Log("Phase 1 complete — navigating to LandingPage with SuppressNavigationTransitionInfo");

        // Navigate while completely covered. Suppress the Frame's default
        // FromRight slide so the swap is a single frame the user can't see.
        NavigateToLanding(new SuppressNavigationTransitionInfo());

        // One UI tick for LandingPage to compose before we expose it.
        await Task.Delay(40);

        AnimDebug.Log("Phase 2 (lift) starting — origin flipped to (0.5, 0)");
        CurtainOverlay.RenderTransformOrigin = new Point(0.5, 0);
        await AnimateScaleYAsync(1, 0, liftMs, new CubicEase { EasingMode = EasingMode.EaseOut });
        AnimDebug.Log("RunLandingCurtainAsync EXIT — curtain lifted, LandingPage revealed");

        // Reset for any subsequent use.
        CurtainOverlay.RenderTransformOrigin = new Point(0.5, 1);
        CurtainOverlayScale.ScaleY = 0;
    }

    private Task AnimateScaleYAsync(double from, double to, int durationMs, EasingFunctionBase easing)
    {
        var tcs = new TaskCompletionSource<object?>();
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(anim, CurtainOverlayScale);
        Storyboard.SetTargetProperty(anim, "ScaleY");
        sb.Children.Add(anim);
        sb.Completed += (_, _) => tcs.TrySetResult(null);
        sb.Begin();
        return tcs.Task;
    }

    /// <summary>Force-launches the animated onboarding intro, even for users
    /// who have already completed it once. Used by the "Replay onboarding"
    /// button in Settings — direct access to RootFrame is needed because
    /// Window.Content is the outer Grid wrapping the Frame, not the Frame
    /// itself, so callers can't reach it through Content.</summary>
    public void NavigateToOnboarding()
    {
        RootFrame.Navigate(typeof(OnboardingPage), null, new SuppressNavigationTransitionInfo());
        RootFrame.BackStack.Clear();
    }

    /// <summary>Opens the project's GitHub issue tracker in the user's
    /// default browser. Wired to the floating Report bug button in
    /// MainWindow.xaml; visible on every page so the user can flag
    /// problems from anywhere.</summary>
    private async void OnReportBugClick(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://github.com/jonsoutherland/BTAP/issues/new?template=bug_report.md"));
    }

    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.Minimize();
    }

    public void ToggleMaximize()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            if (p.State == OverlappedPresenterState.Maximized)
                p.Restore();
            else
                p.Maximize();
        }
    }
}
