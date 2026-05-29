using System.Text.Json;
using System.Text.Json.Serialization;
using BTAP.Models;

namespace BTAP.Services;

public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Project save / load ───────────────────────────────────────────────────

    public static void Save(Project project, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(ProjectDto.From(project), Opts);
        File.WriteAllText(path, json);
        project.FilePath     = path;
        project.LastModified = DateTime.Now;
        project.IsModified   = false;
    }

    public static Project Load(string path)
    {
        var json = File.ReadAllText(path);
        var dto  = JsonSerializer.Deserialize<ProjectDto>(json, Opts)
                   ?? throw new InvalidDataException("Invalid project file.");
        return dto.ToProject(path);
    }

    // ── Recent projects (stored in %LocalAppData%\BTAP\recents.json) ─────────

    private static string RecentsPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BTAP", "recents.json");

    public static void AddRecent(string name, string path, string duration = "", string spec = "")
    {
        var list = LoadRecents();
        list.RemoveAll(r => r.Path == path);
        list.Insert(0, new RecentEntry(name, path, DateTime.Now, duration, spec));
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecentsPath)!);
        File.WriteAllText(RecentsPath, JsonSerializer.Serialize(list, Opts));
    }

    public static List<RecentEntry> LoadRecents()
    {
        if (!File.Exists(RecentsPath)) return [];
        try   { return JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(RecentsPath), Opts) ?? []; }
        catch { return []; }
    }
}

public record RecentEntry(string Name, string Path, DateTime LastModified, string Duration = "", string Spec = "");

// ── Data transfer objects (plain classes — no observable boilerplate) ─────────

public class ProjectDto
{
    public string             Id            { get; set; } = "";
    public string             Name          { get; set; } = "";
    public int                Width         { get; set; }
    public int                Height        { get; set; }
    public double             FrameRate     { get; set; }
    public long               DurationTicks { get; set; }
    public List<MediaItemDto> MediaBin      { get; set; } = [];
    public List<TrackDto>     Tracks        { get; set; } = [];
    public List<MarkerDto>    Markers       { get; set; } = [];

    public static ProjectDto From(Project p) => new()
    {
        Id            = p.Id,
        Name          = p.Name,
        Width         = p.Width,
        Height        = p.Height,
        FrameRate     = p.FrameRate,
        DurationTicks = p.Duration.Ticks,
        MediaBin      = [.. p.MediaBin.Select(MediaItemDto.From)],
        Tracks        = [.. p.Tracks.Select(TrackDto.From)],
        Markers       = [.. p.Markers.Select(MarkerDto.From)],
    };

    public Project ToProject(string filePath)
    {
        var p = new Project
        {
            Id           = Id,
            Name         = Name,
            Width        = Width,
            Height       = Height,
            FrameRate    = FrameRate,
            Duration     = TimeSpan.FromTicks(DurationTicks),
            FilePath     = filePath,
            LastModified = File.GetLastWriteTime(filePath),
        };
        foreach (var m in MediaBin) p.MediaBin.Add(m.ToModel());
        foreach (var t in Tracks)   p.Tracks.Add(t.ToModel());
        foreach (var m in Markers)  p.Markers.Add(m.ToModel());
        return p;
    }
}

public class MediaItemDto
{
    public string Id            { get; set; } = "";
    public string Name          { get; set; } = "";
    public string FilePath      { get; set; } = "";
    public string Type          { get; set; } = "";
    public long   DurationTicks { get; set; }
    public string Resolution    { get; set; } = "";
    public double FrameRate     { get; set; }
    public long   FileSizeBytes { get; set; }

    public static MediaItemDto From(MediaItem m) => new()
    {
        Id = m.Id, Name = m.Name, FilePath = m.FilePath,
        Type = m.Type.ToString(), DurationTicks = m.Duration.Ticks,
        Resolution = m.Resolution, FrameRate = m.FrameRate, FileSizeBytes = m.FileSizeBytes,
    };

    public MediaItem ToModel() => new()
    {
        Id = Id, Name = Name, FilePath = FilePath,
        Type = Enum.Parse<MediaType>(Type),
        Duration = TimeSpan.FromTicks(DurationTicks),
        Resolution = Resolution, FrameRate = FrameRate, FileSizeBytes = FileSizeBytes,
    };
}

public class TrackDto
{
    public string        Id        { get; set; } = "";
    public string        Label     { get; set; } = "";
    public string        Kind      { get; set; } = "";
    public bool          IsVisible { get; set; } = true;
    public bool          IsMuted   { get; set; }
    public bool          IsSolo    { get; set; }
    public bool          IsLocked  { get; set; }
    public double        Volume    { get; set; } = 1.0;
    public List<ClipDto> Clips     { get; set; } = [];

    public static TrackDto From(Track t) => new()
    {
        Id = t.Id, Label = t.Label, Kind = t.Kind.ToString(),
        IsVisible = t.IsVisible, IsMuted = t.IsMuted, IsSolo = t.IsSolo,
        IsLocked = t.IsLocked, Volume = t.Volume,
        Clips = [.. t.Clips.Select(ClipDto.From)],
    };

    public Track ToModel()
    {
        var t = new Track
        {
            Id = Id, Label = Label, Kind = Enum.Parse<TrackKind>(Kind),
            IsVisible = IsVisible, IsMuted = IsMuted, IsSolo = IsSolo,
            IsLocked = IsLocked, Volume = Volume,
        };
        foreach (var c in Clips) t.Clips.Add(c.ToModel());
        return t;
    }
}

public class ClipDto
{
    public string  Id                 { get; set; } = "";
    public string  Label              { get; set; } = "";
    public string  Kind               { get; set; } = "";
    public long    TimelineStartTicks { get; set; }
    public long    DurationTicks      { get; set; }
    public long    SourceStartTicks   { get; set; }
    public double  Volume             { get; set; } = 1.0;
    public double  Speed              { get; set; } = 1.0;
    public double  Scale              { get; set; } = 1.0;
    public double  PosX               { get; set; }
    public double  PosY               { get; set; }
    public double  Rotation           { get; set; }
    public double  Opacity            { get; set; } = 1.0;
    public int     ColorHue           { get; set; } = 138;
    public string? SourceId           { get; set; }

    // Audio
    public double Pan        { get; set; }
    public double FadeInMs   { get; set; }
    public double FadeOutMs  { get; set; }
    public double EqLow      { get; set; }
    public double EqMid      { get; set; }
    public double EqHigh     { get; set; }

    // Crop
    public double CropLeft   { get; set; }
    public double CropTop    { get; set; }
    public double CropRight  { get; set; }
    public double CropBottom { get; set; }

    // Flip
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }

    // Color
    public double Exposure    { get; set; }
    public double Contrast    { get; set; }
    public double Saturation  { get; set; }
    public double Temperature { get; set; }
    public double Tint        { get; set; }
    public double Lift        { get; set; }
    public double Gamma       { get; set; }
    public double ColorGain   { get; set; }

    public List<ClipEffectDto> Effects { get; set; } = [];

    public static ClipDto From(TimelineClip c) => new()
    {
        Id = c.Id, Label = c.Label, Kind = c.Kind.ToString(),
        TimelineStartTicks = c.TimelineStart.Ticks, DurationTicks = c.Duration.Ticks,
        SourceStartTicks = c.SourceStart.Ticks,
        Volume = c.Volume, Speed = c.Speed, Scale = c.Scale,
        PosX = c.PosX, PosY = c.PosY, Rotation = c.Rotation, Opacity = c.Opacity,
        ColorHue = c.ColorHue, SourceId = c.SourceId,
        CropLeft = c.CropLeft, CropTop = c.CropTop, CropRight = c.CropRight, CropBottom = c.CropBottom,
        FlipX = c.FlipX, FlipY = c.FlipY,
        Pan = c.Pan, FadeInMs = c.FadeInMs, FadeOutMs = c.FadeOutMs,
        EqLow = c.EqLow, EqMid = c.EqMid, EqHigh = c.EqHigh,
        Exposure = c.Exposure, Contrast = c.Contrast, Saturation = c.Saturation,
        Temperature = c.Temperature, Tint = c.Tint,
        Lift = c.Lift, Gamma = c.Gamma, ColorGain = c.ColorGain,
        Effects = [.. c.Effects.Select(ClipEffectDto.From)],
    };

    public TimelineClip ToModel()
    {
        var c = new TimelineClip
        {
            Id = Id, Label = Label, Kind = Enum.Parse<ClipKind>(Kind),
            TimelineStart = TimeSpan.FromTicks(TimelineStartTicks),
            Duration      = TimeSpan.FromTicks(DurationTicks),
            SourceStart   = TimeSpan.FromTicks(SourceStartTicks),
            Volume = Volume, Speed = Speed, Scale = Scale,
            PosX = PosX, PosY = PosY, Rotation = Rotation, Opacity = Opacity,
            ColorHue = ColorHue, SourceId = SourceId,
            CropLeft = CropLeft, CropTop = CropTop, CropRight = CropRight, CropBottom = CropBottom,
            FlipX = FlipX, FlipY = FlipY,
            Pan = Pan, FadeInMs = FadeInMs, FadeOutMs = FadeOutMs,
            EqLow = EqLow, EqMid = EqMid, EqHigh = EqHigh,
            Exposure = Exposure, Contrast = Contrast, Saturation = Saturation,
            Temperature = Temperature, Tint = Tint,
            Lift = Lift, Gamma = Gamma, ColorGain = ColorGain,
        };
        foreach (var fx in Effects) c.Effects.Add(fx.ToModel());
        return c;
    }
}

public class ClipEffectDto
{
    public string Name      { get; set; } = "";
    public double Intensity { get; set; } = 1.0;
    public bool   Enabled   { get; set; } = true;

    public static ClipEffectDto From(ClipEffect e) => new()
    {
        Name = e.Name, Intensity = e.Intensity, Enabled = e.Enabled,
    };

    public ClipEffect ToModel() => new()
    {
        Name = Name, Intensity = Intensity, Enabled = Enabled,
    };
}

public class MarkerDto
{
    public string Label         { get; set; } = "";
    public long   PositionTicks { get; set; }
    public string Color         { get; set; } = "#7FB069";

    public static MarkerDto From(Marker m) => new()
        { Label = m.Label, PositionTicks = m.Position.Ticks, Color = m.Color };

    public Marker ToModel() => new()
        { Label = Label, Position = TimeSpan.FromTicks(PositionTicks), Color = Color };
}
