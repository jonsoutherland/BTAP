using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using BTAP.Pages;
using Windows.Graphics;

namespace BTAP;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Configure window chrome
        ExtendsContentIntoTitleBar = true;

        // Set minimum size and start maximized
        AppWindow.Resize(new SizeInt32(1440, 900));
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);

        // Navigate to landing page
        RootFrame.Navigate(typeof(LandingPage));
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
