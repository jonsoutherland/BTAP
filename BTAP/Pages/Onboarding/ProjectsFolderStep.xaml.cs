using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Windows.Storage.Pickers;
using WinRT.Interop;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Q4: pick a default folder for project files. Skippable —
/// "Ask me each time" leaves <see cref="AppSettingsService.DefaultProjectsFolder"/>
/// empty, and the regular save flow falls back to its current behavior.</summary>
public sealed partial class ProjectsFolderStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => true;
    public void RevertPreviewIfNeeded() { /* nothing to revert; only writes on commit */ }

    public ProjectsFolderStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show whatever was already picked (replay path) or a tasteful default.
        var existing = AppSettingsService.Instance.DefaultProjectsFolder;
        var initial  = !string.IsNullOrWhiteSpace(existing)
            ? existing
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "BTAP Projects");
        await TypeOutPathAsync(initial);
    }

    /// <summary>Reveal <paramref name="path"/> character by character so the
    /// step has a small bit of motion even before the user touches anything.
    /// Skipped under ReducedMotion.</summary>
    private async Task TypeOutPathAsync(string path)
    {
        if (AppSettingsService.Instance.ReducedMotion)
        {
            PathDisplay.Text = path;
            return;
        }
        PathDisplay.Text = string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in path)
        {
            sb.Append(ch);
            PathDisplay.Text = sb.ToString();
            // Vary the cadence very slightly so it feels typed, not metered.
            await Task.Delay(ch is ' ' or '\\' ? 24 : 14);
        }
    }

    private async void OnPickClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");

        // Unpackaged WinUI 3 requires HWND association — same pattern as
        // LandingPage.xaml.cs:192.
        var hwnd = WindowNative.GetWindowHandle((Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);

        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return; // user cancelled
            AppSettingsService.Instance.DefaultProjectsFolder = folder.Path;
            PathDisplay.Text = folder.Path;
            StepCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Picker can throw on some unpackaged setups; treat as skip.
            StepCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Instance.DefaultProjectsFolder = string.Empty;
        StepCompleted?.Invoke(this, EventArgs.Empty);
    }
}
