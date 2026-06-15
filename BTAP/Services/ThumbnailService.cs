using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Caches first-frame thumbnails for video files and pixel data for image files using
/// the Windows shell's thumbnail cache. Call from the UI thread — the returned
/// <see cref="BitmapImage"/> objects are XAML elements tied to that thread.
/// </summary>
public static class ThumbnailService
{
    private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> _projectPreviewPath = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BitmapImage?> GetAsync(string? filePath, uint requestedSize = 256)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        if (_cache.TryGetValue(filePath, out var cached))
            return cached;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, requestedSize, ThumbnailOptions.UseCurrentScale);
            if (thumb is null || thumb.Size == 0) return null;

            var image = new BitmapImage();
            await image.SetSourceAsync(thumb);
            _cache[filePath] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a project file just far enough to find the first previewable media item
    /// (video or image) and returns a thumbnail for it. Returns null when the project
    /// has no media that can be previewed.
    /// </summary>
    public static async Task<BitmapImage?> GetForProjectAsync(string? projectPath, uint requestedSize = 256)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return null;

        if (!_projectPreviewPath.TryGetValue(projectPath, out var previewPath))
        {
            previewPath = await Task.Run(() => ResolvePreviewPath(projectPath));
            _projectPreviewPath[projectPath] = previewPath;
        }

        return await GetAsync(previewPath, requestedSize);
    }

    private static string? ResolvePreviewPath(string projectPath)
    {
        try
        {
            using var stream = File.OpenRead(projectPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("MediaBin", out var bin) ||
                bin.ValueKind != JsonValueKind.Array)
                return null;

            string? firstAny = null;
            foreach (var entry in bin.EnumerateArray())
            {
                if (!entry.TryGetProperty("FilePath", out var fp) || fp.ValueKind != JsonValueKind.String)
                    continue;
                var path = fp.GetString();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                var type = entry.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";

                if (string.Equals(type, nameof(MediaType.Video), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, nameof(MediaType.Image), StringComparison.OrdinalIgnoreCase))
                    return path;

                firstAny ??= path;
            }
            return firstAny;
        }
        catch
        {
            return null;
        }
    }
}
