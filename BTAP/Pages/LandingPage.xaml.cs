using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using BTAP.Models;
using BTAP.Services;
using BTAP.ViewModels;
using WinRT.Interop;

namespace BTAP.Pages;

public sealed partial class LandingPage : Page
{
    private readonly LandingViewModel _vm = new();

    public LandingPage()
    {
        InitializeComponent();
        TemplateList.ItemsSource = _vm.Templates;
        RecentsList.ItemsSource = _vm.FilteredRecents;

        TbDateLine.Text       = $"Today · {DateTime.Now:dddd, MMMM d}";
        GreetingRun.Text      = $"{GetGreeting()}. ";
        TbTemplateCount.Text  = $"{_vm.Templates.Count} presets";

        EmptyRecentsHint.Visibility = _vm.HasRecents ? Visibility.Collapsed : Visibility.Visible;
        RecentsList.Visibility      = _vm.HasRecents ? Visibility.Visible   : Visibility.Collapsed;

        PopulateLocations();
        _ = LoadRecentThumbnailsAsync();
    }

    private async Task LoadRecentThumbnailsAsync()
    {
        foreach (var rp in _vm.Recents.ToList())
        {
            if (rp.Thumbnail is not null) continue;
            var image = await ThumbnailService.GetForProjectAsync(rp.Path);
            if (image is not null) rp.Thumbnail = image;
        }
    }

    private static string GetGreeting()
    {
        var h = DateTime.Now.Hour;
        if (h <  5)  return "Still up";
        if (h < 12)  return "Good morning";
        if (h < 17)  return "Good afternoon";
        if (h < 22)  return "Good evening";
        return "Good night";
    }

    private void PopulateLocations()
    {
        LocationsList.Children.Clear();
        var dirs = _vm.Recents
            .Select(r => string.IsNullOrEmpty(r.Path) ? "" : System.IO.Path.GetDirectoryName(r.Path) ?? "")
            .Where(d => !string.IsNullOrEmpty(d) && System.IO.Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (dirs.Count == 0)
        {
            LocationsList.Children.Add(new TextBlock
            {
                Text = "No saved locations yet",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
                Margin = new Thickness(10, 4, 10, 0),
            });
            return;
        }

        foreach (var dir in dirs)
        {
            var btn = new Button
            {
                Content = "~/" + System.IO.Path.GetFileName(dir),
                Tag = dir,
                Style = (Style)Application.Current.Resources["GhostButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
            };
            ToolTipService.SetToolTip(btn, dir);
            btn.Click += OnLocationClick;
            LocationsList.Children.Add(btn);
        }
    }

    private void OnLocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dir } && System.IO.Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
    }

    private void GoToEditor(Project project) =>
        Frame.Navigate(typeof(EditorPage), project);

    private void OnNavTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SetActiveNav(key);
    }

    private void SetActiveNav(string key)
    {
        bool settings = key == "settings";
        RecentScroll.Visibility  = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroll.Visibility = settings ? Visibility.Visible   : Visibility.Collapsed;

        HighlightNav(BtnRecent,    key == "recent");
        HighlightNav(BtnTemplates, key == "templates");
        HighlightNav(BtnSettings,  key == "settings");
    }

    private static void HighlightNav(Button btn, bool selected)
    {
        if (selected)
        {
            btn.Background = (Brush)Application.Current.Resources["BgElevatedBrush"];
            btn.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        }
        else
        {
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.ForegroundProperty);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        _vm.SearchText = SearchBox.Text;

    private void OnOpenRecentProject(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentProject rp })
        {
            GoToEditor(Project.CreateDefault());
            return;
        }

        // If we have a real file path on disk, load it
        if (!string.IsNullOrEmpty(rp.Path) && System.IO.File.Exists(rp.Path))
        {
            try
            {
                GoToEditor(ProjectSerializer.Load(rp.Path));
                return;
            }
            catch { /* fall through to default */ }
        }

        // Demo entry or file missing — create a stub with the same name
        var project = Project.CreateDefault();
        project.Name = rp.Name;
        GoToEditor(project);
    }

    private void OnOpenTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TemplateItem t }) return;
        var project = Project.CreateDefault();
        project.Name = t.Name;
        (project.Width, project.Height, project.FrameRate) = t.Kind switch
        {
            "short" => (1080, 1920, 60.0),
            "sq"    => (1080, 1080, 30.0),
            "cine"  => (3840, 1606, 24.0),
            "yt"    => (1920, 1080, 30.0),
            _       => (1920, 1080, 24.0),
        };
        GoToEditor(project);
    }

    private void OnNewProject(object sender, RoutedEventArgs e) =>
        GoToEditor(Project.CreateDefault());

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(
            (Application.Current as App)!.GetMainWindow());
        InitializeWithWindow.Initialize(picker, hwnd);

        foreach (var ext in MediaItem.VideoExtensions
            .Concat(MediaItem.AudioExtensions)
            .Concat(MediaItem.ImageExtensions))
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var project = Project.CreateDefault();
        project.Name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        project.FilePath = file.Path;

        var item = new MediaItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = file.Name,
            FilePath = file.Path,
            Type = MediaItem.DetectType(file.Path),
        };
        project.MediaBin.Add(item);
        GoToEditor(project);
    }
}
