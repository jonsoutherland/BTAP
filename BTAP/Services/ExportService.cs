using System.IO;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>Builds a Windows.Media.Editing MediaComposition from a BTAP Project.</summary>
public static class ExportService
{
    public sealed class BuildResult
    {
        public MediaComposition? Composition { get; set; }
        public string?           Error       { get; set; }
        public List<string>      Warnings    { get; } = new();
        public int               VideoClips  { get; set; }
        public int               AudioClips  { get; set; }
    }

    /// <summary>
    /// Sequence the bottom-most video track's clips on the main composition
    /// (with black filler in gaps) and overlay every audio-track clip as a
    /// BackgroundAudioTrack at the right delay/trim.
    /// </summary>
    public static async Task<BuildResult> BuildCompositionAsync(Project project, ExportLogger? log = null)
    {
        var result = new BuildResult();
        var comp   = new MediaComposition();

        log?.Log($"Project: \"{project.Name}\" — {project.Width}x{project.Height} @ {project.FrameRate}fps");
        log?.Log($"Tracks: {project.Tracks.Count} (video: {project.Tracks.Count(t => t.Kind == TrackKind.Video)}, audio: {project.Tracks.Count(t => t.Kind == TrackKind.Audio)})");
        log?.Log($"MediaBin: {project.MediaBin.Count} items");

        // The bottom-most non-empty video track is the "main" sequence.
        Track? mainVideo = null;
        for (int i = project.Tracks.Count - 1; i >= 0; i--)
        {
            var t = project.Tracks[i];
            if (t.Kind == TrackKind.Video &&
                t.Clips.Any(c => !string.IsNullOrEmpty(c.SourceId)))
            {
                mainVideo = t;
                break;
            }
        }

        if (mainVideo is null)
        {
            log?.Log("ERROR: no video track contains any source-backed clip");
            return new BuildResult { Error = "Add at least one video clip with a source file before exporting." };
        }

        log?.Log($"Main video track: \"{mainVideo.Label}\" ({mainVideo.Clips.Count} clips)");

        // Order video clips chronologically and lay them down, filling gaps with black
        var videoClips = mainVideo.Clips
            .Where(c => !string.IsNullOrEmpty(c.SourceId))
            .OrderBy(c => c.TimelineStart)
            .ToList();

        // NOTE: We deliberately skip filling gaps with MediaClip.CreateFromColor — those
        // synthetic clips have no codec/dimensions of their own and mixing them with real
        // MediaClips can confuse the renderer enough that it emits a black video stream.
        // Clips concatenate directly; gaps in the timeline collapse during export.
        foreach (var clip in videoClips)
        {
            log?.Log("");
            log?.Log($"-- Video clip \"{clip.Label}\" --");
            log?.Log($"   TimelineStart={clip.TimelineStart}  Duration={clip.Duration}  SourceStart={clip.SourceStart}  Kind={clip.Kind}");

            var media = project.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
            if (media is null || string.IsNullOrEmpty(media.FilePath) || !System.IO.File.Exists(media.FilePath))
            {
                log?.Log($"   SKIPPED — source missing or moved");
                result.Warnings.Add($"Skipped \"{clip.Label}\" — source file missing.");
                continue;
            }

            log?.Log($"   Source file: {media.FilePath}");

            try
            {
                var file      = await StorageFile.GetFileFromPathAsync(media.FilePath);
                var basicProps = await file.GetBasicPropertiesAsync();
                log?.Log($"   File size: {basicProps.Size:N0} bytes  ContentType: {file.ContentType}");

                // Inspect the source's encoding profile to see what codec/dimensions it has
                bool needsPreTranscode = false;
                try
                {
                    var sp = await MediaEncodingProfile.CreateFromFileAsync(file);
                    if (sp?.Container is not null)
                        log?.Log($"   Source container: {sp.Container.Subtype}");
                    if (sp?.Video is not null)
                        log?.Log($"   Source video: subtype={sp.Video.Subtype}, " +
                                 $"{sp.Video.Width}x{sp.Video.Height}, bitrate={sp.Video.Bitrate}, " +
                                 $"fps={sp.Video.FrameRate?.Numerator}/{sp.Video.FrameRate?.Denominator}, " +
                                 $"profile={sp.Video.ProfileId}");
                    else
                        log?.Log("   Source video: <none>");
                    if (sp?.Audio is not null)
                    {
                        log?.Log($"   Source audio: subtype={sp.Audio.Subtype}, " +
                                 $"{sp.Audio.SampleRate}Hz, {sp.Audio.ChannelCount}ch, bitrate={sp.Audio.Bitrate}");
                        // SampleRate==0 means MediaFoundation didn't understand the codec
                        // (typical for iPhone APAC spatial audio). Stream init will fail.
                        if (sp.Audio.SampleRate == 0)
                        {
                            log?.Log("   !! Audio descriptor invalid — pre-transcode required.");
                            needsPreTranscode = true;
                        }
                    }
                }
                catch (Exception spex)
                {
                    log?.Log($"   Could not read source encoding profile: {spex.GetType().Name}: {spex.Message}");
                }

                // If the source's audio descriptor is broken, transcode it to clean
                // H.264+AAC MP4 first; otherwise MediaComposition bails with
                // "Stream is not in a state to handle the request".
                if (needsPreTranscode)
                {
                    file = await EnsureCompatibleSourceAsync(file, log);
                }

                var mediaClip = await MediaClip.CreateFromFileAsync(file);
                log?.Log($"   MediaClip.OriginalDuration = {mediaClip.OriginalDuration}");

                ApplyTrim(mediaClip, clip);
                log?.Log($"   After trim: TrimTimeFromStart={mediaClip.TrimTimeFromStart}, TrimTimeFromEnd={mediaClip.TrimTimeFromEnd}, Volume={mediaClip.Volume}");
                log?.Log($"   Effective trimmed duration: {mediaClip.TrimmedDuration}");

                comp.Clips.Add(mediaClip);
                result.VideoClips++;
            }
            catch (Exception ex)
            {
                log?.Log($"   EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                result.Warnings.Add($"Couldn't import \"{clip.Label}\": {ex.Message}");
            }
        }

        log?.Log("");
        log?.Log($"Video stage complete. Total MediaClips: {comp.Clips.Count}, total composition duration: {comp.Duration}");

        // All audio-track clips become background-audio overlays
        foreach (var track in project.Tracks.Where(t => t.Kind == TrackKind.Audio))
        {
            foreach (var clip in track.Clips.Where(c => !string.IsNullOrEmpty(c.SourceId)))
            {
                log?.Log("");
                log?.Log($"-- Audio clip \"{clip.Label}\" --");

                var media = project.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
                if (media is null || string.IsNullOrEmpty(media.FilePath) || !System.IO.File.Exists(media.FilePath))
                {
                    log?.Log("   SKIPPED — source missing");
                    result.Warnings.Add($"Skipped audio \"{clip.Label}\" — source file missing.");
                    continue;
                }

                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(media.FilePath);
                    var bg   = await BackgroundAudioTrack.CreateFromFileAsync(file);
                    bg.Delay = clip.TimelineStart;
                    if (clip.SourceStart > TimeSpan.Zero)
                        bg.TrimTimeFromStart = clip.SourceStart;

                    // Use the MediaItem's known duration to trim the tail
                    if (media.Duration > TimeSpan.Zero)
                    {
                        var clipSrcEnd = clip.SourceStart + clip.Duration;
                        if (media.Duration > clipSrcEnd)
                            bg.TrimTimeFromEnd = media.Duration - clipSrcEnd;
                    }

                    bg.Volume = clip.Volume;
                    log?.Log($"   Delay={bg.Delay}, TrimStart={bg.TrimTimeFromStart}, TrimEnd={bg.TrimTimeFromEnd}, Volume={bg.Volume}");
                    comp.BackgroundAudioTracks.Add(bg);
                    result.AudioClips++;
                }
                catch (Exception ex)
                {
                    log?.Log($"   EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                    result.Warnings.Add($"Couldn't import audio \"{clip.Label}\": {ex.Message}");
                }
            }
        }

        log?.Log("");
        log?.Log($"BuildCompositionAsync done. VideoClips={result.VideoClips}, AudioClips={result.AudioClips}");
        result.Composition = comp;
        return result;
    }

    private static void ApplyTrim(MediaClip mediaClip, TimelineClip clip)
    {
        // Clamp SourceStart to the source's own duration so we never go negative
        var sourceDur  = mediaClip.OriginalDuration;
        var sourceStart = clip.SourceStart;
        if (sourceStart < TimeSpan.Zero) sourceStart = TimeSpan.Zero;
        if (sourceStart > sourceDur)     sourceStart = sourceDur;

        if (sourceStart > TimeSpan.Zero)
            mediaClip.TrimTimeFromStart = sourceStart;

        var endInSource = sourceStart + clip.Duration;
        if (sourceDur > endInSource)
            mediaClip.TrimTimeFromEnd = sourceDur - endInSource;

        if (clip.Volume is >= 0 and <= 1) mediaClip.Volume = clip.Volume;
    }

    public static MediaEncodingProfile GetEncodingProfile(Project project)
    {
        // Pick the closest preset to the project's frame size and let the preset
        // own frame-rate / bitrate / codec — mutating an in-flight profile after
        // CreateMp4 has been observed to drop the video stream from the output.
        var quality = project.Height switch
        {
            >= 2160 => VideoEncodingQuality.Uhd2160p,
            >= 1080 => VideoEncodingQuality.HD1080p,
            >=  720 => VideoEncodingQuality.HD720p,
            >=  480 => VideoEncodingQuality.Wvga,
            _       => VideoEncodingQuality.Vga,
        };
        return MediaEncodingProfile.CreateMp4(quality);
    }

    /// <summary>
    /// Returns a plain H.264 + AAC + MP4 preset. Do NOT override resolution from the
    /// source profile — source files with rotation metadata report intrinsic (pre-rotation)
    /// dimensions, but the renderer feeds post-rotation frames to the encoder, and the
    /// dimension mismatch causes the encoder to emit a black video stream.
    /// </summary>
    public static Task<MediaEncodingProfile> GetEncodingProfileForProjectAsync(Project project) =>
        Task.FromResult(GetEncodingProfile(project));

    /// <summary>Inspects a finished export to confirm it actually contains a video stream.</summary>
    public static async Task<bool> OutputHasVideoAsync(StorageFile file)
    {
        try
        {
            var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
            return profile?.Video is { Width: > 0, Height: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pre-transcodes a source file to vanilla H.264 + AAC MP4 via MediaTranscoder.
    /// Results are cached in %TEMP%\BTAP-cache keyed by source name + size + mtime so
    /// re-exporting the same project doesn't re-transcode every time. Needed because
    /// MediaComposition can't initialize a render pipeline against exotic codecs
    /// (Apple APAC spatial audio, broken descriptors, etc.).
    /// </summary>
    public static async Task<StorageFile> EnsureCompatibleSourceAsync(StorageFile source, ExportLogger? log = null)
    {
        var info = new FileInfo(source.Path);

        var cacheDir = Path.Combine(Path.GetTempPath(), "BTAP-cache");
        Directory.CreateDirectory(cacheDir);

        var baseName  = Path.GetFileNameWithoutExtension(source.Name);
        var cacheName = $"{baseName}_{info.Length}_{info.LastWriteTimeUtc.Ticks}.mp4";
        var cachePath = Path.Combine(cacheDir, cacheName);

        if (File.Exists(cachePath))
        {
            log?.Log($"   Using cached pre-transcode: {cachePath}");
            return await StorageFile.GetFileFromPathAsync(cachePath);
        }

        log?.Log($"   Pre-transcoding to: {cachePath}");
        var transcodeStart = DateTime.Now;

        var folder = await StorageFolder.GetFolderFromPathAsync(cacheDir);
        var outFile = await folder.CreateFileAsync(cacheName, CreationCollisionOption.ReplaceExisting);

        var transcoder = new MediaTranscoder();
        var targetProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

        var prepared = await transcoder.PrepareFileTranscodeAsync(source, outFile, targetProfile);
        if (!prepared.CanTranscode)
        {
            log?.Log($"   PrepareFileTranscodeAsync FAILED: {prepared.FailureReason}");
            throw new InvalidOperationException(
                $"Source cannot be transcoded by Windows: {prepared.FailureReason}. " +
                $"Convert {source.Name} to standard MP4 (H.264 + AAC) first.");
        }

        await prepared.TranscodeAsync();
        log?.Log($"   Pre-transcode finished in {(DateTime.Now - transcodeStart).TotalSeconds:F1}s");

        return outFile;
    }
}
