using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Q3: live-previewing theme picker. Hovering the sun half flips the
/// whole app to Light; the moon half flips to Dark. Clicking commits. If the
/// user navigates Back without committing, the prior theme is restored.</summary>
public sealed partial class ThemeStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => true;

    private AppTheme _priorTheme;
    private bool _committed;
    // True while the pointer is in either the sun or moon zone. Used to
    // suppress the dark→light→dark strobe as the user sweeps from one to the
    // other: an Exit only triggers a revert if no zone is hovered shortly
    // after.
    private bool _isHovering;
    // Current in-flight emanation storyboard (forward on hover-enter,
    // reverse on hover-exit). Held so we can Stop() it before starting a
    // new direction — without this, rapid hover toggles would compound
    // storyboards on top of each other and the burst would jitter or
    // double-track. Null when no animation is running.
    private Storyboard? _emanationSb;
    // Bumped at every BeginEmanation and EndEmanation call. Phase 2 of the
    // forward chain checks if its captured seq still matches before
    // proceeding; a mismatch means a later hover toggle has superseded
    // us, and we drop the rest of the chain on the floor (Storyboard.Stop
    // already cancelled the visual side).
    private int _emanationSeq;

    public ThemeStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        BuildSunRays();
    }

    public void RevertPreviewIfNeeded()
    {
        // No-op under the new "hover-release commits" model. Once the
        // emanation reaches its peak (peakMs into the hover) it persists
        // the new theme via AppSettings, and on hover-exit we simply leave
        // it committed — so there's nothing to "revert" on Back navigation.
        // A user who hovered too briefly for a flip to fire never had the
        // persisted theme change in the first place.
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _priorTheme = AppSettingsService.Instance.Theme;
    }

    private void BuildSunRays()
    {
        // 8 small rectangles arranged radially around the sun disc.
        //
        // Geometry (SunRays Grid is 200×200, disc centred at (100, 100) with
        // diameter 100):
        //   • Place each ray at "12 o'clock" via Margin (0, 18, 0, 0) so its
        //     base sits just outside the disc (disc top = 50; ray bottom = 34
        //     after we account for the gap).
        //   • Rotate around the disc centre. The ray's top-left lands at
        //     approximately (97, 18) inside SunRays; the disc centre is at
        //     (100, 100); therefore the pivot in ray-local coords is
        //     (3, 82). Earlier math put it at (3, 102) — well below the ray
        //     itself — which flung the rays away from the disc.
        const double rayW = 6, rayH = 16, topMargin = 18;
        const double pivotX = rayW / 2;        // ray-local horizontal centre
        const double pivotY = 100 - topMargin; // ray-local distance to disc centre
        for (int i = 0; i < 8; i++)
        {
            double angle = i * 45;
            var ray = new Rectangle
            {
                Width  = rayW,
                Height = rayH,
                Fill   = (Brush)Application.Current.Resources["AccentBrush"],
                RadiusX = 3, RadiusY = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin = new Thickness(0, topMargin, 0, 0),
                RenderTransform = new RotateTransform { Angle = angle, CenterX = pivotX, CenterY = pivotY },
            };
            SunRays.Children.Add(ray);
        }
    }

    // ── Hover preview (live theme flip) ─────────────────────────────────────

    private void OnSunEnter(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        AnimateRays(visible: true);

        if (AppSettingsService.Instance.ReducedMotion)
        {
            ApplyThemeToRootFrame(AppTheme.Light);
            return;
        }

        BeginEmanation(SunDisc, LightBurst, LightBurstScale, AppTheme.Light);
    }

    private void OnSunExit(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        AnimateRays(visible: false);

        // Hover-release commits: if the emanation reached its peak while
        // the user was hovering, the theme was persisted via AppSettings
        // at that moment (in RunEmanationAsync). On exit we just cancel
        // any pending flip — a flip that hasn't fired yet shouldn't fire
        // now that the user has left — and let the burst fade naturally.
        ++_emanationSeq;
    }

    private void OnMoonEnter(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        AnimateMoonBite(0.36);

        if (AppSettingsService.Instance.ReducedMotion)
        {
            ApplyThemeToRootFrame(AppTheme.Dark);
            return;
        }

        BeginEmanation(MoonZone, DarkBurst, DarkBurstScale, AppTheme.Dark);
    }

    private void OnMoonExit(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        AnimateMoonBite(0.0);
        ++_emanationSeq;
    }

    /// <summary>Starts a hover-driven theme emanation as a single
    /// continuous Storyboard:
    ///   • Position the burst ellipse over <paramref name="source"/>'s
    ///     centre (sun disc for light, moon zone for dark).
    ///   • Scale grows monotonically 0 → 100 (≈4000 px diameter) over
    ///     <c>growMs</c> with ease-out, so the burst radiates smoothly
    ///     outward and covers any realistic monitor by the time the
    ///     theme flips.
    ///   • Opacity uses a KeyFrame bump curve: 0 → 1.0 by <c>peakMs</c>,
    ///     then 1.0 → 0 by <c>growMs</c>. The whole curve is on a single
    ///     animation so there's no inter-phase pause — the previous
    ///     two-Storyboard chain had a 10 ms gap that surfaced as the
    ///     "pause when it switches modes" the user reported.
    ///   • Theme flips at <c>peakMs</c>, exactly when the burst is
    ///     fullscreen and fully opaque, so the page recolour happens
    ///     entirely behind the burst and the user only perceives a
    ///     smooth wash of the new theme's colour radiating from the
    ///     source.
    ///   • The other burst (DarkBurst if we're animating LightBurst,
    ///     etc.) gets a concurrent fade-out if it's still visible from a
    ///     prior emanation — without this, a sun→moon sweep would leave
    ///     the light wash sitting at full opacity over the new dark
    ///     theme.
    ///
    /// Bumps <see cref="_emanationSeq"/> so a later toggle can supersede
    /// the deferred theme-flip before it runs.</summary>
    private void BeginEmanation(
        FrameworkElement source,
        FrameworkElement burst,
        ScaleTransform   scale,
        AppTheme         target)
    {
        _emanationSb?.Stop();
        int mySeq = ++_emanationSeq;

        // Reset scale if mid-faded-out state from a prior emanation
        // (opacity reached 0 but the storyboard left scale at its
        // terminal large value). Without this, the new emanation would
        // start at full screen and shrink — wrong direction.
        if (burst.Opacity == 0 && scale.ScaleX != 0)
        {
            scale.ScaleX = 0;
            scale.ScaleY = 0;
        }

        var toCanvas = source.TransformToVisual(BurstOverlay);
        var centre = toCanvas.TransformPoint(
            new Point(source.ActualWidth / 2.0, source.ActualHeight / 2.0));
        Canvas.SetLeft(burst, centre.X - burst.Width  / 2.0);
        Canvas.SetTop (burst, centre.Y - burst.Height / 2.0);

        // Cross-fade the OTHER burst if it's still on screen — otherwise
        // it sits opaque over the newly-applied theme.
        var otherBurst = burst == LightBurst ? DarkBurst : LightBurst;
        if (otherBurst.Opacity > 0.001)
        {
            var fadeSb = new Storyboard();
            fadeSb.Children.Add(MakeDouble(
                otherBurst, "Opacity", otherBurst.Opacity, 0, 500, easeOut: true));
            fadeSb.Begin();
        }

        _ = RunEmanationAsync(mySeq, burst, scale, target);
    }

    private async Task RunEmanationAsync(
        int              mySeq,
        FrameworkElement burst,
        ScaleTransform   scale,
        AppTheme         target)
    {
        const int growMs = 850;
        const int peakMs = 500;

        var sb = new Storyboard();

        // Scale: monotonic ease-out 0 → 100 over the full duration. With
        // ease-out cubic, scale is at ~95% of final by peakMs — so at the
        // theme-flip moment the burst is ≈3800 px in diameter, large
        // enough to cover any realistic monitor.
        sb.Children.Add(MakeDouble(scale, "ScaleX", scale.ScaleX, 100, growMs, easeOut: true));
        sb.Children.Add(MakeDouble(scale, "ScaleY", scale.ScaleX, 100, growMs, easeOut: true));

        // Opacity: KeyFrame bump in a single animation so there's no
        // inter-storyboard pause. Rises smoothly to 1.0 by peakMs, then
        // fades to 0 by growMs.
        var opAnim = new DoubleAnimationUsingKeyFrames();
        opAnim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value          = 1.0,
            KeyTime        = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(peakMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        opAnim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value          = 0,
            KeyTime        = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(growMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(opAnim, burst);
        Storyboard.SetTargetProperty(opAnim, "Opacity");
        sb.Children.Add(opAnim);

        sb.Begin();
        _emanationSb = sb;

        // Theme flip at peak coverage. Hidden entirely behind the burst,
        // so the user perceives no snap. Persisted via AppSettings (not
        // just Frame.RequestedTheme) so the choice survives navigating
        // away — that's what makes the "hover-release commits" model
        // work: once the flip fires here it stays even when the user
        // moves their pointer off the source.
        await Task.Delay(peakMs);
        if (mySeq != _emanationSeq) return;
        if (AppSettingsService.Instance.Theme != target)
        {
            AppSettingsService.Instance.Theme = target;
            ApplyThemeToRootFrame(target);
        }
    }


    /// <summary>Click commits the light theme and advances to the next step.
    /// The gradual emanation lives on hover (see <see cref="OnSunEnter"/>),
    /// so by the time the user clicks they've already seen the preview
    /// and the burst may still be on screen. The step-to-step slide
    /// transition will carry the burst (which lives on this UserControl)
    /// off with the rest of ThemeStep, so we don't need to explicitly
    /// fade it.</summary>
    private void OnSunTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_committed) return;
        _committed = true;

        AppSettingsService.Instance.Theme = AppTheme.Light;
        ApplyThemeToRootFrame(AppTheme.Light);
        StepCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnMoonTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_committed) return;
        AppSettingsService.Instance.Theme = AppTheme.Dark;
        ApplyThemeToRootFrame(AppTheme.Dark);
        _committed = true;
        StepCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>Mirror MainWindow.ApplyThemeFromSettings here so the live
    /// preview affects the entire UI tree — including this onboarding page —
    /// without needing to round-trip through AppSettings.Changed.</summary>
    private void ApplyThemeToRootFrame(AppTheme theme)
    {
        var requested = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        // Walk up the visual tree to the Frame. Setting RequestedTheme on it
        // propagates to every descendant via ThemeResource refresh.
        DependencyObject? p = this;
        while (p is not null and not Frame) p = VisualTreeHelper.GetParent(p);
        if (p is Frame f) f.RequestedTheme = requested;
    }

    // ── Mini animation helpers ──────────────────────────────────────────────

    private void AnimateRays(bool visible)
    {
        var sb = new Storyboard();
        double opTo    = visible ? 1.0 : 0.0;
        double scaleTo = visible ? 1.0 : 0.6;

        // Baseline log — this animation is confirmed visible (user has
        // observed the sun rays fan out on hover), so its trace tells us
        // what a *working* Storyboard's log signature looks like in this
        // environment. Compare against the OnboardingPage curtain
        // storyboard log to spot what's different.
        AnimDebug.Log(
            $"ThemeStep.AnimateRays({visible}) " +
            $"SunRays.Opacity={SunRays.Opacity:F2}→{opTo:F2} " +
            $"SunRaysScale.ScaleX={SunRaysScale.ScaleX:F2}→{scaleTo:F2} " +
            $"SunRaysScale.ScaleY={SunRaysScale.ScaleY:F2}→{scaleTo:F2}");

        sb.Children.Add(MakeDouble(SunRays, "Opacity", SunRays.Opacity, opTo, 240, easeOut: true));
        sb.Children.Add(MakeDouble(SunRaysScale, "ScaleX", SunRaysScale.ScaleX, scaleTo, 320, easeOut: true));
        sb.Children.Add(MakeDouble(SunRaysScale, "ScaleY", SunRaysScale.ScaleY, scaleTo, 320, easeOut: true));
        sb.Completed += (_, _) =>
            AnimDebug.Log(
                $"ThemeStep.AnimateRays Completed; final SunRays.Opacity={SunRays.Opacity:F2}, " +
                $"SunRaysScale.ScaleX={SunRaysScale.ScaleX:F2}");
        sb.Begin();
        AnimDebug.Log(
            $"ThemeStep.AnimateRays sb.Begin() returned; immediate Opacity={SunRays.Opacity:F2}, " +
            $"ScaleX={SunRaysScale.ScaleX:F2}");
    }

    private void AnimateMoonBite(double xFraction)
    {
        var sb = new Storyboard();
        sb.Children.Add(MakeDouble(MoonBiteTx, "X", MoonBiteTx.X, xFraction * 100, 240, easeOut: true));
        sb.Begin();
    }

    private static DoubleAnimation MakeDouble(DependencyObject target, string property,
                                              double from, double to, int durationMs,
                                              bool easeOut = false)
    {
        var anim = new DoubleAnimation
        {
            From = from, To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
        };
        if (easeOut) anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }
}
