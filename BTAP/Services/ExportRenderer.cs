using System.IO;
using Microsoft.Graphics.Canvas;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Top-level orchestrator for the export pipeline that makes the rendered
/// output match the live preview:
///
///   1. Render a video-only intermediate MP4 by piping per-clip frames through
///      a Win2D compositor (handles Scale/Pos/Rotation/Crop/Flip/Opacity,
///      Exposure/Contrast/Saturation/Temperature/Tint/Blur, title text, and the
///      true project aspect ratio).
///   2. Mux the project's audio (all video-clip audio and all audio-track clips)
///      on top of the intermediate via a MediaComposition pass.
/// </summary>
public static class ExportRenderer
{
    public sealed class Result
    {
        public bool             Success      { get; set; }
        public string?          Error        { get; set; }
        public List<string>     Warnings     { get; } = new();
        public TimeSpan         Duration     { get; set; }
        public int              VideoClips   { get; set; }
        public int              AudioClips   { get; set; }
    }

    /// <summary>Computes the project's total duration as the max TimelineEnd across all clips.</summary>
    public static TimeSpan ComputeDuration(Project project)
    {
        TimeSpan max = TimeSpan.Zero;
        foreach (var t in project.Tracks)
            foreach (var c in t.Clips)
                if (c.TimelineEnd > max) max = c.TimelineEnd;
        return max;
    }

    public static async Task<Result> RenderAsync(
        Project project,
        StorageFile destination,
        IProgress<double>? progress,
        ExportLogger? log,
        CancellationToken ct)
    {
        var result = new Result();

        log?.Log("");
        log?.Log("=== CUSTOM EXPORT PIPELINE ===");
        log?.Log($"Project: {project.Name} — {project.Width}x{project.Height} @ {project.FrameRate}fps");

        var duration = ComputeDuration(project);
        result.Duration = duration;
        if (duration <= TimeSpan.Zero)
        {
            result.Error = "Project is empty — add at least one clip before exporting.";
            log?.Log($"ERROR: {result.Error}");
            return result;
        }
        log?.Log($"Total composition duration: {duration}");

        // Collect every distinct video source path the timeline references.
        var videoSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalVideoClips = 0;
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Video) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.Kind == ClipKind.Title) continue;
                if (string.IsNullOrEmpty(clip.SourceId)) continue;
                var media = project.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
                if (media is null || string.IsNullOrEmpty(media.FilePath) || !File.Exists(media.FilePath))
                    continue;
                if (media.Type != MediaType.Video) continue;
                videoSources.Add(media.FilePath);
                totalVideoClips++;
            }
        }
        log?.Log($"Distinct video sources to open: {videoSources.Count}  (across {totalVideoClips} timeline clips)");

        if (videoSources.Count == 0)
        {
            result.Error = "No video clips with a source file — nothing to render.";
            log?.Log($"ERROR: {result.Error}");
            return result;
        }
        result.VideoClips = totalVideoClips;

        var device = CanvasDevice.GetSharedDevice();
        using var pool       = new ExportFrameSourcePool(device, log);
        using var compositor = new ExportFrameCompositor(project, device);

        foreach (var path in videoSources)
        {
            ct.ThrowIfCancellationRequested();
            try { await pool.PrepareAsync(path).ConfigureAwait(false); }
            catch (Exception ex)
            {
                result.Warnings.Add($"Couldn't open source \"{Path.GetFileName(path)}\": {ex.Message}");
                log?.Log($"   WARNING: skipping {path} — {ex.Message}");
            }
        }

        StorageFile? tempVideo = null;
        try
        {
            // ── Pass 1: custom video transcode to a temp MP4 ─────────────────
            tempVideo = await CreateTempFileAsync("btap_video_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
            log?.Log($"Stage 1: rendering video-only intermediate to {tempVideo.Path}");

            await RenderCustomVideoAsync(project, duration, pool, compositor,
                tempVideo, p => progress?.Report(p * 0.85), log, ct).ConfigureAwait(false);

            log?.Log("Stage 1 complete.");

            // ── Pass 2: mux audio on top via a small MediaComposition ────────
            log?.Log("Stage 2: muxing project audio on top of the video intermediate.");
            int audioClipCount = await MuxAudioAsync(project, tempVideo, destination,
                p => progress?.Report(0.85 + p * 0.15), log, ct).ConfigureAwait(false);
            result.AudioClips = audioClipCount;
            log?.Log($"Stage 2 complete. Audio overlays muxed: {audioClipCount}");

            result.Success = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Export cancelled.";
            log?.Log("Export cancelled by user.");
            return result;
        }
        catch (Exception ex)
        {
            var hr = (ex is System.Runtime.InteropServices.COMException ce)
                ? $" HRESULT=0x{ce.HResult:X8}"
                : $" HRESULT=0x{ex.HResult:X8}";
            var msg = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            result.Error = $"{ex.GetType().Name}: {msg}{hr}";
            log?.Log($"FATAL: {result.Error}");
            log?.Log($"Stack:{Environment.NewLine}{ex}");
            return result;
        }
        finally
        {
            if (tempVideo is not null)
            {
                try { await tempVideo.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                catch { /* best-effort */ }
            }
        }
    }

    // ── Stage 1: custom video transcode ──────────────────────────────────────

    private static async Task RenderCustomVideoAsync(
        Project project, TimeSpan duration,
        ExportFrameSourcePool pool, ExportFrameCompositor compositor,
        StorageFile output, Action<double>? progress, ExportLogger? log, CancellationToken ct)
    {
        // Renderer delegate: for each composition time, ask the pool for each
        // active video clip's source frame, then compose.
        var streamSource = new ExportVideoStreamSource(project, duration, async (t, innerCt) =>
        {
            var frameMap = new Dictionary<TimelineClip, CanvasBitmap>(ReferenceEqualityComparer.Instance);
            for (int i = project.Tracks.Count - 1; i >= 0; i--)
            {
                var track = project.Tracks[i];
                if (track.Kind != TrackKind.Video || track.IsMuted) continue;
                var clip = FirstClipAt(track, t);
                if (clip is null || clip.Kind == ClipKind.Title) continue;
                if (string.IsNullOrEmpty(clip.SourceId)) continue;
                var media = project.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
                if (media is null || media.Type != MediaType.Video) continue;
                var srcTime = clip.SourceStart + (t - clip.TimelineStart);
                var bmp = await pool.GetFrameAsync(media.FilePath, srcTime, innerCt).ConfigureAwait(false);
                if (bmp is not null) frameMap[clip] = bmp;
            }
            return compositor.RenderFrame(t, frameMap);
        }, log);

        try
        {
            var profile = BuildVideoOnlyProfile(project);
            LogProfile(log, "Stage-1 encoding profile", profile);

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            using var outStream = await output.OpenAsync(FileAccessMode.ReadWrite).AsTask(ct).ConfigureAwait(false);
            PrepareTranscodeResult prep;
            try
            {
                prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                                            streamSource.StreamSource, outStream, profile)
                                       .AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Log($"   [stage 1] PrepareMediaStreamSourceTranscodeAsync threw {ex.GetType().Name}: " +
                         $"{ex.Message} HRESULT=0x{ex.HResult:X8}");
                throw;
            }
            if (!prep.CanTranscode)
            {
                throw new InvalidOperationException($"Stage-1 transcode preparation failed: {prep.FailureReason}");
            }

            var op = prep.TranscodeAsync();
            double lastLogged = -10;
            op.Progress = (_, pct) =>
            {
                progress?.Invoke(pct / 100.0);
                if (pct - lastLogged >= 10)
                {
                    log?.Log($"   [stage 1] progress {pct:F0}%");
                    lastLogged = pct;
                }
            };
            using (ct.Register(() => { try { op.Cancel(); } catch { } }))
            {
                await op.AsTask(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            streamSource.Dispose();
        }
    }

    // ── Stage 2: mux audio over the rendered video ───────────────────────────

    private static async Task<int> MuxAudioAsync(
        Project project, StorageFile videoIntermediate, StorageFile destination,
        Action<double>? progress, ExportLogger? log, CancellationToken ct)
    {
        var comp = new MediaComposition();
        comp.Clips.Add(await MediaClip.CreateFromFileAsync(videoIntermediate));

        // BackgroundAudioTrack.CreateFromFileAsync only reliably accepts audio-only
        // files — pointed at an MP4 with both video and audio it silently produces
        // no audio. So we extract the audio stream from each video source to a temp
        // M4A first (cached per source path, since one source can back many clips).
        var extracted    = new Dictionary<string, StorageFile?>(StringComparer.OrdinalIgnoreCase);
        var tempAudioFiles = new List<StorageFile>();
        int audioCount = 0;

        try
        {
            foreach (var track in project.Tracks)
            {
                if (track.Kind == TrackKind.Title || track.IsMuted) continue;
                foreach (var clip in track.Clips)
                {
                    if (clip.Kind == ClipKind.Title) continue;
                    if (string.IsNullOrEmpty(clip.SourceId)) continue;
                    var media = project.MediaBin.FirstOrDefault(m => m.Id == clip.SourceId);
                    if (media is null || string.IsNullOrEmpty(media.FilePath) || !File.Exists(media.FilePath))
                        continue;
                    if (media.Type != MediaType.Video && media.Type != MediaType.Audio) continue;

                    StorageFile? audioFile;
                    if (media.Type == MediaType.Audio)
                    {
                        try { audioFile = await StorageFile.GetFileFromPathAsync(media.FilePath); }
                        catch (Exception ex)
                        {
                            log?.Log($"   [mux] couldn't open audio source {media.FilePath}: {ex.Message}");
                            continue;
                        }
                    }
                    else
                    {
                        if (!extracted.TryGetValue(media.FilePath, out audioFile))
                        {
                            audioFile = await ExtractAudioToTempAsync(media.FilePath, log, ct).ConfigureAwait(false);
                            extracted[media.FilePath] = audioFile;
                            if (audioFile is not null) tempAudioFiles.Add(audioFile);
                        }
                        if (audioFile is null)
                        {
                            log?.Log($"   [mux] no usable audio in {Path.GetFileName(media.FilePath)} — clip \"{clip.Label}\" silent");
                            continue;
                        }
                    }

                    try
                    {
                        var bg = await BackgroundAudioTrack.CreateFromFileAsync(audioFile);
                        bg.Delay = clip.TimelineStart;
                        if (clip.SourceStart > TimeSpan.Zero)
                            bg.TrimTimeFromStart = clip.SourceStart;
                        if (media.Duration > TimeSpan.Zero)
                        {
                            var clipSrcEnd = clip.SourceStart + clip.Duration;
                            if (media.Duration > clipSrcEnd)
                                bg.TrimTimeFromEnd = media.Duration - clipSrcEnd;
                        }
                        bg.Volume = Math.Clamp(clip.Volume, 0, 1);
                        comp.BackgroundAudioTracks.Add(bg);
                        audioCount++;
                        log?.Log($"   [mux] +audio \"{clip.Label}\" delay={bg.Delay} trimStart={bg.TrimTimeFromStart} " +
                                 $"trimEnd={bg.TrimTimeFromEnd} vol={bg.Volume:F2}");
                    }
                    catch (Exception ex)
                    {
                        log?.Log($"   [mux] skipped audio for \"{clip.Label}\" — {ex.Message}");
                    }
                }
            }

            var profile = await MediaEncodingProfile.CreateFromFileAsync(videoIntermediate);
            if (profile.Audio is null)
                profile.Audio = AudioEncodingProperties.CreateAac(48000, 2, 192000);
            LogProfile(log, "Stage-2 final mux profile", profile);
            log?.Log($"   [mux] adding {audioCount} background audio track(s) over 1 video clip");

            var op = comp.RenderToFileAsync(destination, MediaTrimmingPreference.Fast, profile);
            double lastLogged = -10;
            op.Progress = (_, pct) =>
            {
                progress?.Invoke(pct / 100.0);
                if (pct - lastLogged >= 10)
                {
                    log?.Log($"   [stage 2] progress {pct:F0}%");
                    lastLogged = pct;
                }
            };
            using (ct.Register(() => { try { op.Cancel(); } catch { } }))
            {
                var failure = await op.AsTask(ct).ConfigureAwait(false);
                if (failure != TranscodeFailureReason.None)
                    throw new InvalidOperationException($"Stage-2 mux failed: {failure}");
            }

            // Inspect the actual output so we can tell whether audio survived
            try
            {
                var outProf = await MediaEncodingProfile.CreateFromFileAsync(destination);
                if (outProf?.Audio is not null)
                    log?.Log($"   [mux] output audio: {outProf.Audio.Subtype}, {outProf.Audio.SampleRate}Hz, " +
                             $"{outProf.Audio.ChannelCount}ch, bitrate={outProf.Audio.Bitrate}");
                else
                    log?.Log("   [mux] WARNING: output file has no audio stream");
            }
            catch { }

            return audioCount;
        }
        finally
        {
            foreach (var f in tempAudioFiles)
            {
                try { await f.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Transcodes the audio stream of a source media file to a temp M4A. Returns
    /// null if the source has no audio or can't be transcoded.
    /// </summary>
    private static async Task<StorageFile?> ExtractAudioToTempAsync(string sourcePath, ExportLogger? log, CancellationToken ct)
    {
        StorageFile src;
        try { src = await StorageFile.GetFileFromPathAsync(sourcePath); }
        catch { return null; }

        try
        {
            var srcProfile = await MediaEncodingProfile.CreateFromFileAsync(src);
            if (srcProfile?.Audio is null || srcProfile.Audio.SampleRate == 0)
            {
                log?.Log($"   [extract] {Path.GetFileName(sourcePath)} has no usable audio stream");
                return null;
            }
        }
        catch (Exception ex)
        {
            log?.Log($"   [extract] couldn't read source profile for {sourcePath}: {ex.Message}");
            return null;
        }

        StorageFile? outFile = null;
        try
        {
            outFile = await CreateTempFileAsync("btap_audio_" + Guid.NewGuid().ToString("N")[..8] + ".m4a");
            var profile    = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            var transcoder = new MediaTranscoder();
            var prep       = await transcoder.PrepareFileTranscodeAsync(src, outFile, profile).AsTask(ct).ConfigureAwait(false);
            if (!prep.CanTranscode)
            {
                log?.Log($"   [extract] PrepareFileTranscodeAsync failed for {Path.GetFileName(sourcePath)}: {prep.FailureReason}");
                try { await outFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                return null;
            }
            await prep.TranscodeAsync().AsTask(ct).ConfigureAwait(false);
            log?.Log($"   [extract] {Path.GetFileName(sourcePath)} → {outFile.Path}");
            return outFile;
        }
        catch (Exception ex)
        {
            log?.Log($"   [extract] EXCEPTION extracting audio from {Path.GetFileName(sourcePath)}: {ex.GetType().Name}: {ex.Message}");
            if (outFile is not null)
                try { await outFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TimelineClip? FirstClipAt(Track t, TimeSpan p)
    {
        foreach (var c in t.Clips)
            if (p >= c.TimelineStart && p < c.TimelineEnd) return c;
        return null;
    }

    private static MediaEncodingProfile BuildVideoOnlyProfile(Project project)
    {
        // Start from the closest preset (so the codec/subtype is a known-good
        // "H264" instead of "H264ES" returned by VideoEncodingProperties.CreateH264()
        // — elementary-stream subtypes get rejected by the MP4 container at
        // PrepareMediaStreamSourceTranscodeAsync time). Then override the
        // dimensions/framerate to honor the project's true aspect ratio.
        var preset = project.Height switch
        {
            >= 2160 => VideoEncodingQuality.Uhd2160p,
            >= 1080 => VideoEncodingQuality.HD1080p,
            >=  720 => VideoEncodingQuality.HD720p,
            _       => VideoEncodingQuality.Wvga,
        };
        var profile = MediaEncodingProfile.CreateMp4(preset);

        // Strip the audio descriptor — stage 1 is video-only.
        profile.Audio = null;

        profile.Video.Width  = (uint)Math.Max(2, project.Width);
        profile.Video.Height = (uint)Math.Max(2, project.Height);
        profile.Video.FrameRate.Numerator   = (uint)Math.Max(1, Math.Round(project.FrameRate));
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator   = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Max(2_000_000,
            project.Width * project.Height * Math.Max(1, project.FrameRate) * 0.1);
        return profile;
    }

    private static async Task<StorageFile> CreateTempFileAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "BTAP-export");
        Directory.CreateDirectory(dir);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
    }

    private static void LogProfile(ExportLogger? log, string label, MediaEncodingProfile profile)
    {
        if (log is null) return;
        log.Log("");
        log.Log($"== {label} ==");
        if (profile.Container is not null) log.Log($"   Container: {profile.Container.Subtype}");
        if (profile.Video is not null)
            log.Log($"   Video: {profile.Video.Subtype}, {profile.Video.Width}x{profile.Video.Height}, " +
                    $"bitrate={profile.Video.Bitrate}, fps={profile.Video.FrameRate?.Numerator}/{profile.Video.FrameRate?.Denominator}");
        else log.Log("   Video: <none>");
        if (profile.Audio is not null)
            log.Log($"   Audio: {profile.Audio.Subtype}, {profile.Audio.SampleRate}Hz, " +
                    $"{profile.Audio.ChannelCount}ch, bitrate={profile.Audio.Bitrate}");
        else log.Log("   Audio: <none>");
    }
}
