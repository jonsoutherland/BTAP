using System.Text.Json;

namespace BTAP.Services;

/// <summary>Persistent app-wide preferences (theme, density, export defaults,
/// editor layout). Lives in %LocalAppData%\BTAP\settings.json. Mutators raise
/// <see cref="Changed"/> after writing so live consumers (LandingPage layout
/// preview, EditorPage layout applicator) can refresh.</summary>
public sealed class AppSettingsService
{
    private static readonly Lazy<AppSettingsService> _instance = new(() => new AppSettingsService());
    public static AppSettingsService Instance => _instance.Value;

    public event EventHandler? Changed;

    private SettingsDto _dto;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BTAP", "settings.json");

    private AppSettingsService()
    {
        _dto = Load();
    }

    // ── Display ────────────────────────────────────────────────────────────

    public AppTheme Theme
    {
        get => _dto.Theme;
        set { if (_dto.Theme != value) { _dto.Theme = value; Save(); } }
    }

    public UiDensity Density
    {
        get => _dto.Density;
        set { if (_dto.Density != value) { _dto.Density = value; Save(); } }
    }

    public AccentScheme Accent
    {
        get => _dto.Accent;
        set { if (_dto.Accent != value) { _dto.Accent = value; Save(); } }
    }

    public bool ShowProjectThumbnails
    {
        get => _dto.ShowProjectThumbnails;
        set { if (_dto.ShowProjectThumbnails != value) { _dto.ShowProjectThumbnails = value; Save(); } }
    }

    public bool ReducedMotion
    {
        get => _dto.ReducedMotion;
        set { if (_dto.ReducedMotion != value) { _dto.ReducedMotion = value; Save(); } }
    }

    // ── Export defaults ────────────────────────────────────────────────────

    public int DefaultExportBitrateKbps
    {
        get => _dto.DefaultExportBitrateKbps;
        set { var c = Math.Clamp(value, 300, 200_000);
              if (_dto.DefaultExportBitrateKbps != c) { _dto.DefaultExportBitrateKbps = c; Save(); } }
    }

    public double DefaultExportFps
    {
        get => _dto.DefaultExportFps;
        set { var c = Math.Clamp(value, 15.0, 120.0);
              if (Math.Abs(_dto.DefaultExportFps - c) > 0.01) { _dto.DefaultExportFps = c; Save(); } }
    }

    public bool DefaultExportLimitFileSize
    {
        get => _dto.DefaultExportLimitFileSize;
        set { if (_dto.DefaultExportLimitFileSize != value) { _dto.DefaultExportLimitFileSize = value; Save(); } }
    }

    public int DefaultExportMaxSizeMb
    {
        get => _dto.DefaultExportMaxSizeMb;
        set { var c = Math.Clamp(value, 1, 100_000);
              if (_dto.DefaultExportMaxSizeMb != c) { _dto.DefaultExportMaxSizeMb = c; Save(); } }
    }

    public ExportContainer DefaultExportContainer
    {
        get => _dto.DefaultExportContainer;
        set { if (_dto.DefaultExportContainer != value) { _dto.DefaultExportContainer = value; Save(); } }
    }

    // ── Editor layout ──────────────────────────────────────────────────────

    public double LibraryPanelWidth
    {
        get => _dto.LibraryPanelWidth;
        set { var c = Math.Clamp(value, 180.0, 480.0);
              if (Math.Abs(_dto.LibraryPanelWidth - c) > 0.5) { _dto.LibraryPanelWidth = c; Save(); } }
    }

    public double InspectorPanelWidth
    {
        get => _dto.InspectorPanelWidth;
        set { var c = Math.Clamp(value, 200.0, 520.0);
              if (Math.Abs(_dto.InspectorPanelWidth - c) > 0.5) { _dto.InspectorPanelWidth = c; Save(); } }
    }

    public double TimelinePanelHeight
    {
        get => _dto.TimelinePanelHeight;
        set { var c = Math.Clamp(value, 160.0, 520.0);
              if (Math.Abs(_dto.TimelinePanelHeight - c) > 0.5) { _dto.TimelinePanelHeight = c; Save(); } }
    }

    public bool LibraryPanelVisible
    {
        get => _dto.LibraryPanelVisible;
        set { if (_dto.LibraryPanelVisible != value) { _dto.LibraryPanelVisible = value; Save(); } }
    }

    public bool InspectorPanelVisible
    {
        get => _dto.InspectorPanelVisible;
        set { if (_dto.InspectorPanelVisible != value) { _dto.InspectorPanelVisible = value; Save(); } }
    }

    public PanelSide LibrarySide
    {
        get => _dto.LibrarySide;
        set { if (_dto.LibrarySide != value) { _dto.LibrarySide = value; Save(); } }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private static SettingsDto Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return new SettingsDto();
            var json = System.IO.File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<SettingsDto>(json) ?? new SettingsDto();
        }
        catch { return new SettingsDto(); }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefaults()
    {
        _dto = new SettingsDto();
        Save();
    }

    private sealed class SettingsDto
    {
        public AppTheme  Theme    { get; set; } = AppTheme.System;
        public UiDensity Density  { get; set; } = UiDensity.Comfortable;
        public AccentScheme Accent { get; set; } = AccentScheme.Sage;
        public bool ShowProjectThumbnails { get; set; } = true;
        public bool ReducedMotion         { get; set; } = false;

        public int    DefaultExportBitrateKbps    { get; set; } = 12_000;
        public double DefaultExportFps            { get; set; } = 30.0;
        public bool   DefaultExportLimitFileSize  { get; set; } = false;
        public int    DefaultExportMaxSizeMb      { get; set; } = 100;
        public ExportContainer DefaultExportContainer { get; set; } = ExportContainer.Mp4H264Aac;

        public double LibraryPanelWidth   { get; set; } = 256;
        public double InspectorPanelWidth { get; set; } = 296;
        public double TimelinePanelHeight { get; set; } = 240;
        public bool   LibraryPanelVisible   { get; set; } = true;
        public bool   InspectorPanelVisible { get; set; } = true;
        public PanelSide LibrarySide { get; set; } = PanelSide.Left;
    }
}

public enum AppTheme    { System, Dark, Light }
public enum UiDensity   { Comfortable, Compact }
public enum AccentScheme { Sage, Blue, Purple, Amber }
public enum ExportContainer { Mp4H264Aac, Mp4H265Aac }
public enum PanelSide   { Left, Right }
