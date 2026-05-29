using BTAP.Models;

namespace BTAP.Pages;

public sealed class MediaTileData
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public MediaType Type { get; init; }
    public string Duration { get; init; } = "";
    public string Resolution { get; init; } = "";

    public string TypeIcon => Type switch
    {
        MediaType.Video => "",
        MediaType.Audio => "",
        MediaType.Image => "",
        _ => "",
    };

    public string TypeLabel => Type.ToString().ToUpperInvariant();

    public static MediaTileData FromMediaItem(MediaItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Type = item.Type,
        Duration = item.Duration == TimeSpan.Zero ? "" :
            $"{(int)item.Duration.TotalMinutes:D2}:{item.Duration.Seconds:D2}",
        Resolution = item.Resolution,
    };
}
