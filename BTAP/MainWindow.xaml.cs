using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using BTAP.Pages;
using BTAP.Services;
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

        // Navigate to landing page
        RootFrame.Navigate(typeof(LandingPage));
    }

    /// <summary>Pushes the user's theme choice onto the root Frame. Affects
    /// system-drawn WinUI controls (ComboBox flyouts, NumberBox glyphs, etc.).
    /// The custom dark palette in AppColors.xaml is unaffected — switching to
    /// Light here will lighten built-in controls only.</summary>
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

    public void NavigateToLanding()
    {
        RootFrame.Navigate(typeof(LandingPage));
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
