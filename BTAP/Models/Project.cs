using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BTAP.Models;

public partial class Project : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "Untitled project";
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private DateTime _lastModified = DateTime.Now;
    [ObservableProperty] private int _width = 1920;
    [ObservableProperty] private int _height = 1080;
    [ObservableProperty] private double _frameRate = 24.0;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private bool _isModified;

    public ObservableCollection<MediaItem> MediaBin { get; } = [];
    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<Marker> Markers { get; } = [];

    public string ResolutionLabel => $"{Width}×{Height}";
    public string FrameRateLabel => $"{FrameRate:G4} fps";

    public static Project CreateDefault()
    {
        var p = new Project { Name = "New project" };

        // Single starter track; additional tracks are created on demand by
        // dragging media into the empty zones above or below the timeline.
        p.Tracks.Add(new Track { Label = "V1", Kind = TrackKind.Video });

        return p;
    }
}

public record RecentProject(string Name, string LastEdited, string Duration, string Spec, string Path = "");

public partial class Marker : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private string _color = "#7FB069";
}
