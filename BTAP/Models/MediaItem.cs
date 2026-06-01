using CommunityToolkit.Mvvm.ComponentModel;

namespace BTAP.Models;

public enum MediaType { Video, Audio, Image, Title }

public partial class MediaItem : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private MediaType _type;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private string _resolution = string.Empty;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private double _frameRate;
    [ObservableProperty] private long _fileSizeBytes;
    [ObservableProperty] private bool _hasProxy;
    [ObservableProperty] private string? _proxyPath;

    public string DurationLabel => Duration.TotalSeconds < 3600
        ? Duration.ToString(@"mm\:ss")
        : Duration.ToString(@"hh\:mm\:ss");

    public string SizeLabel => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024):F1} MB",
        _ => $"{FileSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    // Supported import formats
    public static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv"];

    public static readonly string[] AudioExtensions =
        [".wav", ".mp3", ".ogg", ".aac", ".flac", ".m4a"];

    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".heic", ".heif", ".tiff", ".tif", ".gif", ".raw", ".cr2", ".nef", ".arw"];

    public static MediaType DetectType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (Array.Exists(VideoExtensions, e => e == ext)) return MediaType.Video;
        if (Array.Exists(AudioExtensions, e => e == ext)) return MediaType.Audio;
        if (Array.Exists(ImageExtensions, e => e == ext)) return MediaType.Image;
        return MediaType.Video;
    }

    public static string FilePickerFilter =>
        $"Media files ({string.Join(", ", VideoExtensions.Concat(AudioExtensions).Concat(ImageExtensions))})";

    public static MediaItem FromStorageFile(Windows.Storage.StorageFile file) => new()
    {
        Id       = Guid.NewGuid().ToString("N")[..8],
        Name     = file.Name,
        FilePath = file.Path,
        Type     = DetectType(file.Path),
    };

    public static async Task<MediaItem> FromStorageFileAsync(Windows.Storage.StorageFile file)
    {
        var item = FromStorageFile(file);
        try
        {
            var basic = await file.GetBasicPropertiesAsync();
            item.FileSizeBytes = (long)basic.Size;

            var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
            if (Array.Exists(VideoExtensions, e => e == ext))
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                item.Duration   = props.Duration;
                if (props.Width > 0)
                {
                    int w = (int)props.Width;
                    int h = (int)props.Height;
                    // VideoProperties reports raw encoded dimensions; MediaPlayer
                    // applies the rotation flag on playback (NaturalVideoWidth/Height
                    // come back swapped). Match that here or portrait phone clips
                    // get stretched into a landscape canvas.
                    try
                    {
                        var extra = await file.Properties.RetrievePropertiesAsync(
                            new[] { "System.Video.Orientation" });
                        if (extra.TryGetValue("System.Video.Orientation", out var rotObj)
                            && rotObj is uint rot
                            && (rot == 90 || rot == 270))
                        {
                            (w, h) = (h, w);
                        }
                    }
                    catch { }
                    item.Width      = w;
                    item.Height     = h;
                    item.Resolution = $"{w}×{h}";
                }
            }
            else if (Array.Exists(AudioExtensions, e => e == ext))
            {
                var props = await file.Properties.GetMusicPropertiesAsync();
                item.Duration = props.Duration;
            }
        }
        catch { /* metadata read failed — leave defaults */ }
        return item;
    }
}
