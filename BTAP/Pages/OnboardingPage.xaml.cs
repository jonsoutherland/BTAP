using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using BTAP.Pages.Onboarding;
using BTAP.Services;

namespace BTAP.Pages;

/// <summary>
/// Host page for the animated first-run onboarding.
///
/// Two animation primitives are in play, both confirmed to render in this
/// environment:
///   1. ContentThemeTransition with HorizontalOffset (on StepHost) — drives
///      the step-to-step push-wipe. Direction flips for back navigation by
///      mutating HorizontalOffset on the live transition instance.
///   2. Storyboard targeting a XAML-declared ScaleTransform (on the
///      CurtainPanel sibling of the content grid) — drives the curtain rise
///      on "Enter editor". Same recipe as ThemeStep's sun-rays animation:
///      XAML-declared element + XAML-declared transform + SetTarget on the
///      transform. ContentThemeTransition.VerticalOffset and translate-based
///      Storyboards do NOT render here, which is why the curtain uses
///      ScaleY-with-bottom-origin instead of a Y-translate.
/// </summary>
public sealed partial class OnboardingPage : Page
{
    private readonly Func<UserControl>[] _stepFactories;

    private int _currentIndex = -1;
    private UserControl? _currentStep;

    public OnboardingPage()
    {
        InitializeComponent();

        _stepFactories =
        [
            () => new WelcomeStep(),
            () => new SoftwareStep(),
            () => new ColorStep(),
            () => new ThemeStep(),
            () => new ProjectsFolderStep(),
            () => new ExportsFolderStep(),
            () => new OutroStep(),
        ];

        BuildProgressDots();

        Loaded += (_, _) =>
        {
            AnimDebug.Log($"OnboardingPage Loaded. Log path: {AnimDebug.LogPath}");

            Focus(FocusState.Programmatic);
            if (_currentIndex < 0) GoTo(0, forward: true);
        };
    }

    /// <summary>Distance the new step travels during a ContentThemeTransition
    /// slide, in pixels. Sign is flipped for Back navigation so the incoming
    /// step enters from the opposite side.</summary>
    private const double SlideOffset = 600;

    // ── Progress dots ───────────────────────────────────────────────────────

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int i = 0; i < _stepFactories.Length; i++)
        {
            int idx = i;
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = (Brush)Application.Current.Resources["TextFaintBrush"],
                Opacity = 0.5,
            };
            dot.Tapped += (_, e) =>
            {
                // Jumping to an earlier dot reads as going back — slide in
                // from the left so direction matches user intent.
                if (idx < _currentIndex) GoTo(idx, forward: false);
                e.Handled = true;
            };
            ProgressDots.Children.Add(dot);
        }
    }

    private void RefreshProgressDots()
    {
        for (int i = 0; i < ProgressDots.Children.Count; i++)
        {
            if (ProgressDots.Children[i] is not Ellipse dot) continue;
            bool active = i == _currentIndex;
            bool past   = i < _currentIndex;
            dot.Fill = (Brush)Application.Current.Resources[
                active || past ? "AccentBrush" : "TextFaintBrush"];
            dot.Opacity = active ? 1.0 : (past ? 0.85 : 0.4);
            dot.Width   = active ? 14 : 8;
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void GoTo(int index, bool forward)
    {
        if (index < 0 || index >= _stepFactories.Length) return;

        var newStep = _stepFactories[index]();
        if (newStep is not IOnboardingStep iface)
            throw new InvalidOperationException(
                $"Step {newStep.GetType().Name} does not implement IOnboardingStep");
        iface.StepCompleted += OnStepCompleted;

        // OutroStep's "Enter editor" click doesn't advance to another step —
        // it asks the host to run the curtain slide-up animation and then
        // navigate to LandingPage. Hooked here at construction time so the
        // event handler is in place by the time the user can click.
        if (newStep is OutroStep outro)
        {
            outro.EnterEditorRequested -= OnEnterEditorRequested;
            outro.EnterEditorRequested += OnEnterEditorRequested;
        }

        _currentStep  = newStep;
        _currentIndex = index;

        // Set slide direction BEFORE assigning Content so the live
        // ContentThemeTransition picks up the new HorizontalOffset for this
        // particular transition. +SlideOffset = new content enters from the
        // right; -SlideOffset = enters from the left.
        SetTransitionDirection(forward);

        // Assigning Content fires the StepHost's ContentThemeTransition.
        // The step's opaque background covers the outgoing content as it
        // slides — that's the push-wipe effect.
        StepHost.Content = newStep;

        RefreshProgressDots();
        BtnBack.Visibility = (index > 0 && iface.CanGoBack)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mutates the live <see cref="ContentThemeTransition"/>'s
    /// HorizontalOffset so the next Content assignment triggers a slide in
    /// the requested direction. Mutating the existing transition instance
    /// (rather than swapping the TransitionCollection) keeps WinUI's
    /// transition machinery hooked up — replacing the collection can leave
    /// the next animation un-attached.</summary>
    private void SetTransitionDirection(bool forward)
    {
        if (StepHost.ContentTransitions is { Count: > 0 } transitions
            && transitions[0] is ContentThemeTransition tt)
        {
            tt.HorizontalOffset = forward ? SlideOffset : -SlideOffset;
        }
    }

    private void OnStepCompleted(object? sender, EventArgs e)
    {
        if (sender is IOnboardingStep step && step == (_currentStep as IOnboardingStep))
            step.StepCompleted -= OnStepCompleted;
        GoTo(_currentIndex + 1, forward: true);
    }

    /// <summary>OutroStep asked to enter the editor. Delegates the
    /// curtain-and-navigate sequence to MainWindow, which owns a
    /// window-level CurtainOverlay that survives the OnboardingPage →
    /// LandingPage Frame swap. (Doing the curtain on this Page would
    /// destroy it the moment we navigate, which is what produced the
    /// "flashes to the main menu" bug in v18/v19.)</summary>
    private async void OnEnterEditorRequested(object? sender, EventArgs e)
    {
        AnimDebug.Log("OnEnterEditorRequested ENTER");

        var win = (Application.Current as App)?.GetMainWindow();
        if (win is null)
        {
            AnimDebug.Log("OnEnterEditorRequested: MainWindow null — bailing");
            return;
        }

        if (AppSettingsService.Instance.ReducedMotion)
        {
            AnimDebug.Log("OnEnterEditorRequested: ReducedMotion=true → navigating immediately");
            win.NavigateToLanding();
            return;
        }

        await win.RunLandingCurtainAsync();
    }

    // ── Chrome handlers ─────────────────────────────────────────────────────

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep is IOnboardingStep step) step.RevertPreviewIfNeeded();
        GoTo(_currentIndex - 1, forward: false);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) =>
        ConfirmSkip(sender as FrameworkElement);

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ConfirmSkip(BtnSkip);
            e.Handled = true;
        }
    }

    private void ConfirmSkip(FrameworkElement? anchor)
    {
        anchor ??= BtnSkip;
        var stack = new StackPanel { Spacing = 8, MaxWidth = 260 };
        stack.Children.Add(new TextBlock
        {
            Text = "Skip setup? You can replay it any time from Settings → Display.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                                   HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Keep going",
                                  Style = (Style)Application.Current.Resources["GhostButtonStyle"],
                                  Padding = new Thickness(10, 4, 10, 4) };
        var confirm = new Button { Content = "Skip",
                                   Style = (Style)Application.Current.Resources["BtapButtonStyle"],
                                   Padding = new Thickness(10, 4, 10, 4) };
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        stack.Children.Add(row);

        var flyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.Bottom };
        cancel.Click  += (_, _) => flyout.Hide();
        confirm.Click += (_, _) => { flyout.Hide(); JumpToOutro(); };
        flyout.ShowAt(anchor);
    }

    private void JumpToOutro()
    {
        int outroIndex = _stepFactories.Length - 1;
        if (_currentIndex == outroIndex) return;
        GoTo(outroIndex, forward: true);
    }
}
