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
        var (dto, fileExisted) = Load();
        _dto = dto;
        // Sentinel for existing users: if settings.json already exists, we're
        // an upgrade install — don't surprise the user with onboarding. Flip
        // the flag and persist so the next launch sees a real `true`. Fresh
        // installs (no file) get `false` from the DTO default and onboard.
        if (fileExisted && !_dto.HasCompletedOnboarding)
        {
            _dto.HasCompletedOnboarding = true;
            Save();
        }
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

    /// <summary>When true, <see cref="CustomAccentHex"/> is used and <see cref="Accent"/>
    /// is ignored. Lets users pick a colour outside the four curated swatches.</summary>
    public bool UseCustomAccent
    {
        get => _dto.UseCustomAccent;
        set { if (_dto.UseCustomAccent != value) { _dto.UseCustomAccent = value; Save(); } }
    }

    /// <summary>Hex string for the custom accent, format "#AARRGGBB" or "#RRGGBB".
    /// Only consulted when <see cref="UseCustomAccent"/> is true.</summary>
    public string CustomAccentHex
    {
        get => _dto.CustomAccentHex;
        set
        {
            var v = value ?? string.Empty;
            if (_dto.CustomAccentHex != v) { _dto.CustomAccentHex = v; Save(); }
        }
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

    /// <summary>Hex colours users can customise as the default fill for each
    /// clip kind on the timeline. A track without an explicit override falls
    /// back to the value here. Hex strings ("#RRGGBB" or "#AARRGGBB"); the
    /// timeline applies a theme-aware alpha when painting.</summary>
    public string DefaultVideoClipColor
    {
        get => _dto.DefaultVideoClipColor;
        set { var v = value ?? string.Empty;
              if (_dto.DefaultVideoClipColor != v) { _dto.DefaultVideoClipColor = v; Save(); } }
    }
    public string DefaultAudioClipColor
    {
        get => _dto.DefaultAudioClipColor;
        set { var v = value ?? string.Empty;
              if (_dto.DefaultAudioClipColor != v) { _dto.DefaultAudioClipColor = v; Save(); } }
    }
    public string DefaultMusicClipColor
    {
        get => _dto.DefaultMusicClipColor;
        set { var v = value ?? string.Empty;
              if (_dto.DefaultMusicClipColor != v) { _dto.DefaultMusicClipColor = v; Save(); } }
    }
    public string DefaultTitleClipColor
    {
        get => _dto.DefaultTitleClipColor;
        set { var v = value ?? string.Empty;
              if (_dto.DefaultTitleClipColor != v) { _dto.DefaultTitleClipColor = v; Save(); } }
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

    /// <summary>The workspace complexity preset. Simple/Moderate/Complex apply a
    /// curated snapshot (dock layout + density + keybinds); switching to one
    /// resets the affected fields. Stays on whichever value was last applied so
    /// "Reset preset" can re-snap to it.</summary>
    public LayoutPreset LayoutPreset
    {
        get => _dto.LayoutPreset;
        set { if (_dto.LayoutPreset != value) { _dto.LayoutPreset = value; Save(); } }
    }

    /// <summary>Persisted JSON of the user's DockTree (panel positions). Empty
    /// string means "use the preset's default tree". Mutated by DockHost when
    /// the user drags panels around.</summary>
    public string DockTreeJson
    {
        get => _dto.DockTreeJson;
        set
        {
            var v = value ?? string.Empty;
            if (_dto.DockTreeJson != v) { _dto.DockTreeJson = v; Save(); }
        }
    }

    /// <summary>Which keyboard preset is active. Manual edits made in the
    /// keybind customizer do NOT change this — the preset only resets on
    /// preset-switch, so the user's overrides persist after that.</summary>
    public KeybindPreset KeybindPreset
    {
        get => _dto.KeybindPreset;
        set { if (_dto.KeybindPreset != value) { _dto.KeybindPreset = value; Save(); } }
    }

    // ── Onboarding ─────────────────────────────────────────────────────────

    /// <summary>True once the user has finished (or skipped) the animated
    /// first-run intro. The launch gate in MainWindow routes to OnboardingPage
    /// while this is false. Flipped back to false by the "Replay onboarding"
    /// button in Settings.</summary>
    public bool HasCompletedOnboarding
    {
        get => _dto.HasCompletedOnboarding;
        set { if (_dto.HasCompletedOnboarding != value) { _dto.HasCompletedOnboarding = value; Save(); } }
    }

    /// <summary>Folder picked during onboarding (Q4) as the default location
    /// for project files. Empty string = "ask each time" — the project save
    /// flow falls back to a picker without a starting hint.</summary>
    public string DefaultProjectsFolder
    {
        get => _dto.DefaultProjectsFolder;
        set
        {
            var v = value ?? string.Empty;
            if (_dto.DefaultProjectsFolder != v) { _dto.DefaultProjectsFolder = v; Save(); }
        }
    }

    /// <summary>Folder picked during onboarding (Q5) as the default destination
    /// for rendered exports. Empty string = "ask each time".</summary>
    public string DefaultExportsFolder
    {
        get => _dto.DefaultExportsFolder;
        set
        {
            var v = value ?? string.Empty;
            if (_dto.DefaultExportsFolder != v) { _dto.DefaultExportsFolder = v; Save(); }
        }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    /// <summary>Returns the loaded DTO plus whether the settings file existed
    /// before this call — the existence flag is the first-run sentinel the
    /// onboarding gate uses to tell fresh installs apart from upgrades.</summary>
    private static (SettingsDto Dto, bool FileExisted) Load()
    {
        bool existed = System.IO.File.Exists(FilePath);
        if (!existed) return (new SettingsDto(), false);
        try
        {
            var json = System.IO.File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json) ?? new SettingsDto();
            return (dto, true);
        }
        catch { return (new SettingsDto(), true); }
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
        public bool UseCustomAccent  { get; set; } = false;
        public string CustomAccentHex { get; set; } = "#FF7FB069";
        public bool ShowProjectThumbnails { get; set; } = true;
        public bool ReducedMotion         { get; set; } = false;
        public LayoutPreset  LayoutPreset  { get; set; } = LayoutPreset.Moderate;
        public string        DockTreeJson  { get; set; } = string.Empty;
        public KeybindPreset KeybindPreset { get; set; } = KeybindPreset.Default;

        public bool   HasCompletedOnboarding { get; set; } = false;
        public string DefaultProjectsFolder  { get; set; } = string.Empty;
        public string DefaultExportsFolder   { get; set; } = string.Empty;

        public int    DefaultExportBitrateKbps    { get; set; } = 12_000;
        public double DefaultExportFps            { get; set; } = 30.0;
        public bool   DefaultExportLimitFileSize  { get; set; } = false;
        public int    DefaultExportMaxSizeMb      { get; set; } = 100;
        public ExportContainer DefaultExportContainer { get; set; } = ExportContainer.Mp4H264Aac;

        public bool   LibraryPanelVisible   { get; set; } = true;
        public bool   InspectorPanelVisible { get; set; } = true;

        // Defaults match the original GetClipColors palette, full opacity
        // (alpha applied at paint time so dark/light themes can both look right).
        public string DefaultVideoClipColor { get; set; } = "#FF1C5A8C";
        public string DefaultAudioClipColor { get; set; } = "#FF195A32";
        public string DefaultMusicClipColor { get; set; } = "#FF462378";
        public string DefaultTitleClipColor { get; set; } = "#FF5A410F";
    }
}

public enum AppTheme    { System, Dark, Light }
public enum UiDensity   { Comfortable, Compact }
public enum AccentScheme { Sage, Blue, Purple, Amber, Rose, Teal, Coral, Slate }
public enum ExportContainer { Mp4H264Aac, Mp4H265Aac }
public enum LayoutPreset { Simple, Moderate, Complex }
public enum KeybindPreset { Minimal, Default, PremiereLike }
