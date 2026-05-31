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

    /// <summary>
    /// The preview/working canvas size. Derived from the first imported video clip's
    /// native dimensions when one is available; falls back to the project's export
    /// W×H otherwise. The canvas is what the preview displays at; the project's
    /// Width/Height define only the EXPORT crop window centered inside the canvas.
    /// PosX/PosY are interpreted in canvas-pixel units.
    /// </summary>
    public (int Width, int Height) GetCanvasSize()
    {
        foreach (var m in MediaBin)
        {
            if (m.Type == MediaType.Video && m.Width > 0 && m.Height > 0)
                return (m.Width, m.Height);
        }
        return (Width, Height);
    }

    /// <summary>
    /// The rectangle inside the canvas (in canvas-pixel coordinates) that will be
    /// cropped and scaled to <see cref="Width"/>×<see cref="Height"/> for export.
    /// Largest rect with the project's export aspect that fits inside the canvas,
    /// centered. When canvas aspect == export aspect, returns the full canvas.
    /// </summary>
    public (double X, double Y, double W, double H) GetExportWindow()
    {
        var (cw, ch) = GetCanvasSize();
        if (cw <= 0 || ch <= 0 || Width <= 0 || Height <= 0)
            return (0, 0, cw, ch);
        double exportAspect = (double)Width / Height;
        double canvasAspect = (double)cw   / ch;
        double w, h;
        if (exportAspect > canvasAspect)
        {
            // Wider than canvas — width pinned, height shrinks
            w = cw;
            h = cw / exportAspect;
        }
        else
        {
            // Taller (or equal) — height pinned, width shrinks
            h = ch;
            w = ch * exportAspect;
        }
        double x = (cw - w) / 2.0;
        double y = (ch - h) / 2.0;
        return (x, y, w, h);
    }

    public static Project CreateDefault() => new() { Name = "New project" };
}

public record RecentProject(string Name, string LastEdited, string Duration, string Spec, string Path = "");

public partial class Marker : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private string _color = "#7FB069";
}
