using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BTAP.Models;
using BTAP.Services;

namespace BTAP.Controls;

/// <summary>
/// Live, interactive preview of the editor body that users actually drag panels
/// in. Hosts a <see cref="DockHost"/> in editable mode with sample-content
/// stand-ins for Library / Center / Inspector — so the experience matches what
/// will appear in the editor without coupling to a real project.
///
/// Saves layout edits to <see cref="AppSettingsService.DockTreeJson"/>; re-reads
/// when the settings change (e.g., a preset button rewrote the tree).
/// </summary>
public sealed class LayoutPreviewView : Grid
{
    private readonly AppSettingsService _settings = AppSettingsService.Instance;
    private readonly DockHost _dock = new() { IsLayoutEditable = true };

    public LayoutPreviewView()
    {
        Background      = (Brush)Application.Current.Resources["BgPageBrush"];
        BorderBrush     = (Brush)Application.Current.Resources["HairlineBrush"];
        BorderThickness = new Thickness(1);
        CornerRadius    = new CornerRadius(6);

        Children.Add(_dock);

        Loaded   += (_, _) => OnAttached();
        Unloaded += (_, _) => OnDetached();
    }

    private void OnAttached()
    {
        _settings.Changed -= OnSettingsChanged;
        _settings.Changed += OnSettingsChanged;
        Configure();
    }

    private void OnDetached()
    {
        _settings.Changed -= OnSettingsChanged;
        _dock.TreeChanged -= OnDockTreeChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Configure();

    /// <summary>Rebuilds the preview against the current persisted dock tree.
    /// Sample panels are fresh instances per call — they're cheap visual mocks,
    /// so we don't bother caching them between configures.</summary>
    private void Configure()
    {
        var panels = new Dictionary<string, FrameworkElement>
        {
            ["library"]   = LayoutSampleContent.BuildLibrary(),
            ["center"]    = LayoutSampleContent.BuildCenter(),
            ["inspector"] = LayoutSampleContent.BuildInspector(),
        };
        var headers = new Dictionary<string, string>
        {
            ["library"]   = "LIBRARY",
            ["center"]    = "PROGRAM",
            ["inspector"] = "INSPECTOR",
        };

        _dock.TreeChanged -= OnDockTreeChanged;
        _dock.Configure(panels, headers, ResolveTree());
        _dock.TreeChanged += OnDockTreeChanged;
    }

    private DockNode ResolveTree()
    {
        var saved = DockTree.TryDeserialize(_settings.DockTreeJson);
        if (saved is not null) return saved;
        return _settings.LayoutPreset switch
        {
            LayoutPreset.Simple  => DockTree.SimpleTree(),
            LayoutPreset.Complex => DockTree.ComplexTree(),
            _                    => DockTree.DefaultTree(),
        };
    }

    private void OnDockTreeChanged(object? sender, string json)
    {
        // Persisting goes through AppSettings.Changed which would loop us back
        // into Configure(); guard against the redundant rebuild by unsubscribing
        // briefly. The setter no-ops on equal strings so the loop terminates
        // naturally too, but explicit is cheaper than the round-trip.
        _settings.Changed -= OnSettingsChanged;
        _settings.DockTreeJson = json;
        _settings.Changed += OnSettingsChanged;
    }
}
