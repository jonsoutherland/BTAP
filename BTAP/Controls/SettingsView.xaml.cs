using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using BTAP.Models;
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
            SelectComboByTag(CbKeybindPreset, _settings.KeybindPreset.ToString());

            CbShowThumbnails.IsChecked = _settings.ShowProjectThumbnails;
            CbReducedMotion.IsChecked  = _settings.ReducedMotion;

            NbBitrate.Value = _settings.DefaultExportBitrateKbps;
            NbFps.Value     = _settings.DefaultExportFps;
            CbLimitSize.IsChecked = _settings.DefaultExportLimitFileSize;
            NbMaxSize.Value = _settings.DefaultExportMaxSizeMb;
            NbMaxSize.IsEnabled = _settings.DefaultExportLimitFileSize;

            CbShowLibrary.IsChecked   = _settings.LibraryPanelVisible;
            CbShowInspector.IsChecked = _settings.InspectorPanelVisible;

            CbUseCustomAccent.IsChecked = _settings.UseCustomAccent;
            TbCustomAccentHex.Text = _settings.CustomAccentHex;
            TbCustomAccentHex.IsEnabled = _settings.UseCustomAccent;
            RefreshAccentLabel();
            RefreshPresetLabel();
            RefreshClipColorSwatches();
        }
        finally { _loading = false; }
    }

    private void RefreshClipColorSwatches()
    {
        SwatchVideoClip.Background = SwatchFromHex(_settings.DefaultVideoClipColor);
        SwatchAudioClip.Background = SwatchFromHex(_settings.DefaultAudioClipColor);
        SwatchMusicClip.Background = SwatchFromHex(_settings.DefaultMusicClipColor);
        SwatchTitleClip.Background = SwatchFromHex(_settings.DefaultTitleClipColor);
    }

    private static SolidColorBrush SwatchFromHex(string hex) =>
        AccentManager.TryParseHex(hex, out var c)
            ? new SolidColorBrush(c)
            : new SolidColorBrush(Color.FromArgb(255, 80, 80, 80));

    private void OnPickDefaultClipColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind } btn) return;
        string current = kind switch
        {
            "Audio" => _settings.DefaultAudioClipColor,
            "Music" => _settings.DefaultMusicClipColor,
            "Title" => _settings.DefaultTitleClipColor,
            _       => _settings.DefaultVideoClipColor,
        };
        var picker = new ColorPicker
        {
            ColorSpectrumShape   = ColorSpectrumShape.Box,
            IsAlphaEnabled       = false,
            IsAlphaSliderVisible = false,
            IsHexInputVisible    = true,
        };
        if (AccentManager.TryParseHex(current, out var initial)) picker.Color = initial;

        var apply = new Button
        {
            Content = "Apply",
            Style = (Style)Application.Current.Resources["BtapButtonStyle"],
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var content = new StackPanel { Spacing = 6, Children = { picker, apply } };
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };
        apply.Click += (_, _) =>
        {
            var c = picker.Color;
            string hex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
            switch (kind)
            {
                case "Audio": _settings.DefaultAudioClipColor = hex; break;
                case "Music": _settings.DefaultMusicClipColor = hex; break;
                case "Title": _settings.DefaultTitleClipColor = hex; break;
                default:      _settings.DefaultVideoClipColor = hex; break;
            }
            flyout.Hide();
            RefreshClipColorSwatches();
        };
        flyout.ShowAt(btn);
    }

    private void OnResetDefaultClipColors(object sender, RoutedEventArgs e)
    {
        _settings.DefaultVideoClipColor = "#FF1C5A8C";
        _settings.DefaultAudioClipColor = "#FF195A32";
        _settings.DefaultMusicClipColor = "#FF462378";
        _settings.DefaultTitleClipColor = "#FF5A410F";
        RefreshClipColorSwatches();
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
            // Clicking a swatch switches off the custom override — the swatch is
            // the source of truth from here. Mirror that into the UI so the
            // checkbox doesn't lie about what the accent actually came from.
            _settings.UseCustomAccent = false;
            CbUseCustomAccent.IsChecked = false;
            TbCustomAccentHex.IsEnabled = false;
            _settings.Accent = a;
            RefreshAccentLabel();
        }
    }

    private void RefreshAccentLabel()
    {
        TbAccentLabel.Text = _settings.UseCustomAccent
            ? $"Custom · {_settings.CustomAccentHex}"
            : _settings.Accent.ToString();
        CustomAccentSwatch.Background = new SolidColorBrush(AccentManager.ResolveColor());
    }

    private void OnUseCustomAccentChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = CbUseCustomAccent.IsChecked == true;
        _settings.UseCustomAccent = on;
        TbCustomAccentHex.IsEnabled = on;
        RefreshAccentLabel();
    }

    private void OnCustomAccentHexChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyCustomHex();
    }

    private void OnCustomAccentHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_loading) return;
        if (e.Key == Windows.System.VirtualKey.Enter) ApplyCustomHex();
    }

    private void ApplyCustomHex()
    {
        var hex = TbCustomAccentHex.Text?.Trim() ?? string.Empty;
        if (!AccentManager.TryParseHex(hex, out var color))
        {
            // Bad input — restore the last good value so the swatch never goes
            // to "no color" and the brush keeps painting.
            TbCustomAccentHex.Text = _settings.CustomAccentHex;
            return;
        }
        var normalized = AccentManager.FormatHex(color);
        _settings.CustomAccentHex = normalized;
        TbCustomAccentHex.Text = normalized;
        RefreshAccentLabel();
    }

    private void OnPickCustomAccent(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var picker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Box,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsHexInputVisible = true,
        };
        if (AccentManager.TryParseHex(_settings.CustomAccentHex, out var initial))
            picker.Color = initial;

        var done = new Button
        {
            Content = "Apply",
            Style = (Style)Application.Current.Resources["BtapButtonStyle"],
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var content = new StackPanel { Spacing = 6, Children = { picker, done } };

        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };
        done.Click += (_, _) =>
        {
            var c = picker.Color;
            // Force alpha to FF — accent colours shouldn't be translucent.
            var solid = Color.FromArgb(0xFF, c.R, c.G, c.B);
            _settings.UseCustomAccent = true;
            _settings.CustomAccentHex = AccentManager.FormatHex(solid);
            CbUseCustomAccent.IsChecked = true;
            TbCustomAccentHex.IsEnabled = true;
            TbCustomAccentHex.Text = _settings.CustomAccentHex;
            RefreshAccentLabel();
            flyout.Hide();
        };
        flyout.ShowAt(btn);
    }

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

    /// <summary>Re-runs the first-run intro. Flips the gate to false and asks
    /// MainWindow to navigate to the onboarding page; the outro re-sets the
    /// gate and returns to LandingPage on completion.</summary>
    private void OnReplayOnboardingClick(object sender, RoutedEventArgs e)
    {
        _settings.HasCompletedOnboarding = false;
        (Application.Current as App)?.GetMainWindow()?.NavigateToOnboarding();
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
    }

    /// <summary>Open the layout editor at full window size. We size the popup
    /// to the current XamlRoot bounds on every open (the window can be resized
    /// between invocations, so static XAML sizing goes stale), and offset by
    /// the negative of this SettingsView's position in XamlRoot space.
    /// Without that offset, the popup positions relative to SettingsView's
    /// parent — which sits to the right of the LandingPage's left nav rail,
    /// so the popup spills off the right edge of the window.</summary>
    private void OnFullsizeEditClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null) return;
        var bounds = XamlRoot.Size;

        // Pull the popup back to (0,0) of the XamlRoot so it actually covers
        // the whole window. TransformToVisual(null) maps this control to the
        // XamlRoot's coordinate space.
        double offsetX = 0, offsetY = 0;
        try
        {
            var origin = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            offsetX = -origin.X;
            offsetY = -origin.Y;
        }
        catch { /* fall through with zero offsets */ }
        FullsizePopup.HorizontalOffset = offsetX;
        FullsizePopup.VerticalOffset   = offsetY;

        FullsizePopupRoot.Width  = bounds.Width;
        FullsizePopupRoot.Height = bounds.Height;
        FullsizePopup.XamlRoot   = XamlRoot;
        FullsizePopup.IsOpen     = true;
        FullsizePopupRoot.Focus(FocusState.Programmatic);
        FullsizePopupRoot.KeyDown -= OnFullsizeKeyDown;
        FullsizePopupRoot.KeyDown += OnFullsizeKeyDown;
    }

    private void OnFullsizeEditClose(object sender, RoutedEventArgs e) =>
        FullsizePopup.IsOpen = false;

    private void OnFullsizeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            FullsizePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnKeybindPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbKeybindPreset.SelectedItem is not ComboBoxItem { Tag: string t }) return;
        if (!Enum.TryParse<KeybindPreset>(t, out var preset)) return;
        _settings.KeybindPreset = preset;
        KeyBindingPresets.Apply(_keyBindings, preset);
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string t }) return;
        if (!Enum.TryParse<LayoutPreset>(t, out var preset)) return;
        ApplyPreset(preset);
    }

    /// <summary>Snaps every preset-controlled setting (layout, density, keybinds)
    /// to the curated baseline for <paramref name="preset"/>. Resets <see cref="AppSettingsService.DockTreeJson"/>
    /// so the new tree is the source of truth — user DnD edits after this point
    /// will overlay on top of the preset.</summary>
    private void ApplyPreset(LayoutPreset preset)
    {
        DockNode tree;
        UiDensity density;
        KeybindPreset keys;
        bool libraryVisible = true;
        bool inspectorVisible = true;

        switch (preset)
        {
            case LayoutPreset.Simple:
                tree              = DockTree.SimpleTree();
                density           = UiDensity.Comfortable;
                keys              = KeybindPreset.Minimal;
                inspectorVisible  = false;
                break;
            case LayoutPreset.Complex:
                tree              = DockTree.ComplexTree();
                density           = UiDensity.Compact;
                keys              = KeybindPreset.PremiereLike;
                break;
            default:
                tree              = DockTree.DefaultTree();
                density           = UiDensity.Comfortable;
                keys              = KeybindPreset.Default;
                break;
        }

        _settings.LayoutPreset       = preset;
        _settings.DockTreeJson       = DockTree.Serialize(tree);
        _settings.Density            = density;
        _settings.KeybindPreset      = keys;
        _settings.LibraryPanelVisible   = libraryVisible;
        _settings.InspectorPanelVisible = inspectorVisible;

        // Apply the matching keybind set to the active service so the editor
        // picks them up immediately. The customizer's manual edits will still
        // overlay on top (the service merges per-binding).
        KeyBindingPresets.Apply(_keyBindings, keys);

        HydrateFromSettings();
        ShowTab("layout");
    }

    private void RefreshPresetLabel() =>
        TbPresetLabel.Text = _settings.LayoutPreset.ToString();

    /// <summary>Re-apply the currently-selected preset. The button reads
    /// "Reset layout to this preset" and the user reaches for it after they've
    /// DnD-ed themselves into a layout they don't like.</summary>
    private void OnResetToPresetClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(_settings.LayoutPreset);

}
