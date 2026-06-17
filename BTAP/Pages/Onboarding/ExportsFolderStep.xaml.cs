using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using BTAP.Services;

namespace BTAP.Pages.Onboarding;

/// <summary>Q5: pick a default folder for rendered exports. Same skip-able
/// pattern as Q4.</summary>
public sealed partial class ExportsFolderStep : UserControl, IOnboardingStep
{
    public event EventHandler? StepCompleted;
    public bool CanGoBack => true;
    public void RevertPreviewIfNeeded() { /* nothing to revert */ }

    public ExportsFolderStep()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var existing = AppSettingsService.Instance.DefaultExportsFolder;
        var initial  = !string.IsNullOrWhiteSpace(existing)
            ? existing
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "BTAP Exports");
        await TypeOutPathAsync(initial);
    }

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
            await Task.Delay(ch is ' ' or '\\' ? 24 : 14);
        }
    }

    private async void OnPickClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle((Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            AppSettingsService.Instance.DefaultExportsFolder = folder.Path;
            PathDisplay.Text = folder.Path;
            StepCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            StepCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Instance.DefaultExportsFolder = string.Empty;
        StepCompleted?.Invoke(this, EventArgs.Empty);
    }
}
