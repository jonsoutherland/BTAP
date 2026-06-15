using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BTAP.Services;

namespace BTAP.Controls;

public sealed partial class SettingsView : UserControl
{
    private readonly AppSettingsService _settings = AppSettingsService.Instance;
    private readonly KeyBindingsService _keyBindings = new();
    private bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            HydrateFromSettings();
            KeyboardCustomizer.Attach(_keyBindings);
        };
    }

    // ── Tab switching ──────────────────────────────────────────────────────

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        ShowTab(key);
    }

    private void ShowTab(string key)
    {
        PanelDisplay.Visibility  = key == "display" ? Visibility.Visible : Visibility.Collapsed;
        PanelKeybinds.Visibility = key == "keys"    ? Visibility.Visible : Visibility.Collapsed;
        PanelExport.Visibility   = key == "export"  ? Visibility.Visible : Visibility.Collapsed;
        PanelLayout.Visibility   = key == "layout"  ? Visibility.Visible : Visibility.Collapsed;

        Highlight(TabDisplay,  key == "display");
        Highlight(TabKeybinds, key == "keys");
        Highlight(TabExport,   key == "export");
        Highlight(TabLayout,   key == "layout");
    }

    private static void Highlight(Button btn, bool selected)
    {
        if (selected)
        {
            btn.Background = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["BgElevatedBrush"];
            btn.Foreground = (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["TextPrimaryBrush"];
        }
        else
        {
            btn.ClearValue(BackgroundProperty);
            btn.ClearValue(ForegroundProperty);
        }
    }

    // ── Initial hydration ──────────────────────────────────────────────────

    private void HydrateFromSettings()
    {
        _loading = true;
        try
        {
            SelectComboByTag(CbTheme,     _settings.Theme.ToString());
            SelectComboByTag(CbDensity,   _settings.Density.ToString());
            SelectComboByTag(CbContainer, _settings.DefaultExportContainer.ToString());
            SelectComboByTag(CbLibrarySide, _settings.LibrarySide.ToString());

            CbShowThumbnails.IsChecked = _settings.ShowProjectThumbnails;
            CbReducedMotion.IsChecked  = _settings.ReducedMotion;

            NbBitrate.Value = _settings.DefaultExportBitrateKbps;
            NbFps.Value     = _settings.DefaultExportFps;
            CbLimitSize.IsChecked = _settings.DefaultExportLimitFileSize;
            NbMaxSize.Value = _settings.DefaultExportMaxSizeMb;
            NbMaxSize.IsEnabled = _settings.DefaultExportLimitFileSize;

            CbShowLibrary.IsChecked   = _settings.LibraryPanelVisible;
            CbShowInspector.IsChecked = _settings.InspectorPanelVisible;
            SldLibWidth.Value   = _settings.LibraryPanelWidth;
            SldInsWidth.Value   = _settings.InspectorPanelWidth;
            SldTimelineH.Value  = _settings.TimelinePanelHeight;

            RefreshAccentLabel();
            RefreshLayoutPreview();
        }
        finally { _loading = false; }
    }

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is ComboBoxItem item && item.Tag is string t && t == tag)
            { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    // ── Display ────────────────────────────────────────────────────────────

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbTheme.SelectedItem is ComboBoxItem { Tag: string t }
            && Enum.TryParse<AppTheme>(t, out var theme))
            _settings.Theme = theme;
    }

    private void OnDensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbDensity.SelectedItem is ComboBoxItem { Tag: string t }
            && Enum.TryParse<UiDensity>(t, out var d))
            _settings.Density = d;
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t }) return;
        if (Enum.TryParse<AccentScheme>(t, out var a))
        {
            _settings.Accent = a;
            RefreshAccentLabel();
        }
    }

    private void RefreshAccentLabel() =>
        TbAccentLabel.Text = _settings.Accent.ToString();

    private void OnShowThumbnailsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowProjectThumbnails = CbShowThumbnails.IsChecked == true;
    }

    private void OnReducedMotionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ReducedMotion = CbReducedMotion.IsChecked == true;
    }

    // ── Export ─────────────────────────────────────────────────────────────

    private void OnContainerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbContainer.SelectedItem is ComboBoxItem { Tag: string t }
            && Enum.TryParse<ExportContainer>(t, out var c))
            _settings.DefaultExportContainer = c;
    }

    private void OnBitrateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) return;
        _settings.DefaultExportBitrateKbps = (int)args.NewValue;
    }

    private void OnFpsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) return;
        _settings.DefaultExportFps = args.NewValue;
    }

    private void OnLimitSizeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = CbLimitSize.IsChecked == true;
        _settings.DefaultExportLimitFileSize = on;
        NbMaxSize.IsEnabled = on;
    }

    private void OnMaxSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) return;
        _settings.DefaultExportMaxSizeMb = (int)args.NewValue;
    }

    private void OnResetExportClick(object sender, RoutedEventArgs e)
    {
        _settings.DefaultExportContainer    = ExportContainer.Mp4H264Aac;
        _settings.DefaultExportBitrateKbps  = 12_000;
        _settings.DefaultExportFps          = 30.0;
        _settings.DefaultExportLimitFileSize = false;
        _settings.DefaultExportMaxSizeMb    = 100;
        HydrateFromSettings();
        ShowTab("export");
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void OnLayoutToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LibraryPanelVisible   = CbShowLibrary.IsChecked == true;
        _settings.InspectorPanelVisible = CbShowInspector.IsChecked == true;
        RefreshLayoutPreview();
    }

    private void OnLibrarySideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbLibrarySide.SelectedItem is ComboBoxItem { Tag: string t }
            && Enum.TryParse<PanelSide>(t, out var side))
        {
            _settings.LibrarySide = side;
            RefreshLayoutPreview();
        }
    }

    private void OnLayoutSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.LibraryPanelWidth   = SldLibWidth.Value;
        _settings.InspectorPanelWidth = SldInsWidth.Value;
        _settings.TimelinePanelHeight = SldTimelineH.Value;
        RefreshLayoutPreview();
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        _settings.LibraryPanelWidth   = 256;
        _settings.InspectorPanelWidth = 296;
        _settings.TimelinePanelHeight = 240;
        _settings.LibraryPanelVisible   = true;
        _settings.InspectorPanelVisible = true;
        _settings.LibrarySide = PanelSide.Left;
        HydrateFromSettings();
        ShowTab("layout");
    }

    /// <summary>Mirror the slider values into the schematic preview. The preview
    /// is a simplified Grid whose column/row sizes mimic the editor body so users
    /// see roughly how their numbers translate to real estate.</summary>
    private void RefreshLayoutPreview()
    {
        var libW = _settings.LibraryPanelVisible ? _settings.LibraryPanelWidth : 0;
        var insW = _settings.InspectorPanelVisible ? _settings.InspectorPanelWidth : 0;
        var tlH  = _settings.TimelinePanelHeight;

        // Re-order columns so PvLibrary sits on the chosen side.
        if (_settings.LibrarySide == PanelSide.Left)
        {
            Grid.SetColumn(PvLibrary,   0);
            Grid.SetColumn(PvInspector, 2);
            PvLibrary.BorderThickness   = new Thickness(0, 0, 1, 0);
            PvInspector.BorderThickness = new Thickness(1, 0, 0, 0);
        }
        else
        {
            Grid.SetColumn(PvLibrary,   2);
            Grid.SetColumn(PvInspector, 0);
            PvLibrary.BorderThickness   = new Thickness(1, 0, 0, 0);
            PvInspector.BorderThickness = new Thickness(0, 0, 1, 0);
        }

        LayoutPreviewGrid.ColumnDefinitions[0].Width =
            new GridLength(_settings.LibrarySide == PanelSide.Left ? libW : insW, GridUnitType.Star);
        LayoutPreviewGrid.ColumnDefinitions[1].Width = new GridLength(640, GridUnitType.Star);
        LayoutPreviewGrid.ColumnDefinitions[2].Width =
            new GridLength(_settings.LibrarySide == PanelSide.Left ? insW : libW, GridUnitType.Star);

        LayoutPreviewGrid.RowDefinitions[0].Height = new GridLength(640 - tlH, GridUnitType.Star);
        LayoutPreviewGrid.RowDefinitions[1].Height = new GridLength(tlH, GridUnitType.Star);

        PvLibrary.Visibility   = _settings.LibraryPanelVisible   ? Visibility.Visible : Visibility.Collapsed;
        PvInspector.Visibility = _settings.InspectorPanelVisible ? Visibility.Visible : Visibility.Collapsed;

        TbLibWidth.Text   = $"{(int)SldLibWidth.Value} px";
        TbInsWidth.Text   = $"{(int)SldInsWidth.Value} px";
        TbTimelineH.Text  = $"{(int)SldTimelineH.Value} px";
    }
}
