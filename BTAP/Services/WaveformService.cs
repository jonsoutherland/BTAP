using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace BTAP.Services;

/// <summary>Per-file peak data: amplitude buckets sampled at a fixed time resolution.</summary>
public sealed class PeakData
{
    public required float[] Peaks            { get; init; }
    public required int     BucketsPerSecond { get; init; }
    public required TimeSpan Duration        { get; init; }
}

/// <summary>
/// Background-extracts audio peak amplitude arrays from media files. Uses MediaTranscoder
/// to convert the source to a small temp WAV at offline (max) speed, then reads PCM samples
/// directly to compute peaks at a fixed time resolution. Cached per file path.
/// </summary>
public static class WaveformService
{
    /// <summary>Fires when peaks (or null on failure) become available for a file.</summary>
    public static event EventHandler<string>? PeaksReady;

    /// <summary>Per-second bucket count. 200 buckets/sec = 5ms resolution. Plenty for
    /// visual fidelity; one minute of audio = 12 000 buckets = ~48 KB.</summary>
    public const int BucketsPerSecond = 200;

    private static readonly Dictionary<string, PeakData?> _cache    = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string>               _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object                        _lock     = new();

    public static PeakData? GetCachedPeaks(string filePath)
    {
        lock (_lock) return _cache.TryGetValue(filePath, out var p) ? p : null;
    }

    public static void EnsurePeaksAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        lock (_lock)
        {
            if (_cache.ContainsKey(filePath)) return;
            if (_inFlight.Contains(filePath)) return;
            _inFlight.Add(filePath);
        }
        _ = Task.Run(() => ExtractAsync(filePath));
    }

    private static async Task ExtractAsync(string filePath)
    {
        PeakData? result = null;
        try
        {
            result = await ExtractViaTranscodeAsync(filePath);
            System.Diagnostics.Debug.WriteLine(
                $"[Waveform] {Path.GetFileName(filePath)}: " +
                (result is null ? "FAILED" : $"{result.Peaks.Length} buckets " +
                 $"({result.Duration.TotalSeconds:F1}s @ {result.BucketsPerSecond}/s), " +
                 $"peak {result.Peaks.Max():F3}"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Waveform] {filePath} threw: {ex.Message}");
            result = null;
        }

        lock (_lock)
        {
            _cache[filePath] = result;
            _inFlight.Remove(filePath);
        }
        try { PeaksReady?.Invoke(null, filePath); } catch { }
    }

    private static async Task<PeakData?> ExtractViaTranscodeAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        StorageFile sourceFile;
        try { sourceFile = await StorageFile.GetFileFromPathAsync(filePath); }
        catch { return null; }

        var parent = await sourceFile.GetParentAsync();
        if (parent is null) return null;

        var tempName = $".btap_waveform_{Guid.NewGuid():N}.wav";
        StorageFile tempFile;
        try
        {
            tempFile = await parent.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting);
        }
        catch { return null; }

        try
        {
            // 22.05 kHz mono PCM16: small temp file, ample amplitude resolution for a meter.
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(22050, 1, 16);

            var transcoder = new MediaTranscoder();
            var prep = await transcoder.PrepareFileTranscodeAsync(sourceFile, tempFile, profile);
            if (!prep.CanTranscode)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Waveform] PrepareFileTranscodeAsync failed: {prep.FailureReason}");
                return null;
            }

            await prep.TranscodeAsync();

            return ReadPeaksFromWav(tempFile.Path);
        }
        finally
        {
            try { await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
        }
    }

    /// <summary>Parses a PCM-16 WAV and computes peaks at <see cref="BucketsPerSecond"/>.</summary>
    private static PeakData? ReadPeaksFromWav(string wavPath)
    {
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF") return null;
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") return null;

        short numChannels   = 0;
        int   sampleRate    = 0;
        short bitsPerSample = 0;
        long  dataStart     = -1;
        int   dataLength    = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId   = new string(reader.ReadChars(4));
            int    chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                long fmtEnd = stream.Position + chunkSize;
                reader.ReadInt16();          // audio format
                numChannels   = reader.ReadInt16();
                sampleRate    = reader.ReadInt32();
                reader.ReadInt32();          // byte rate
                reader.ReadInt16();          // block align
                bitsPerSample = reader.ReadInt16();
                stream.Position = fmtEnd;
            }
            else if (chunkId == "data")
            {
                dataStart  = stream.Position;
                dataLength = chunkSize;
                break;
            }
            else
            {
                stream.Position += chunkSize;
            }
        }

        if (dataStart < 0 || numChannels == 0 || bitsPerSample != 16 || sampleRate <= 0) return null;

        int  bytesPerSample = bitsPerSample / 8;
        int  frameStride    = bytesPerSample * numChannels;
        long totalFrames    = dataLength / frameStride;
        if (totalFrames <= 0) return null;

        double durationSec  = (double)totalFrames / sampleRate;
        int    totalBuckets = Math.Max(1, (int)Math.Ceiling(durationSec * BucketsPerSecond));
        double framesPerBucket = (double)totalFrames / totalBuckets;

        var peaks = new float[totalBuckets];

        stream.Position = dataStart;
        var  buffer    = new byte[Math.Min(64 * 1024, dataLength)];
        long frameIdx  = 0;
        long remaining = dataLength;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            toRead -= toRead % frameStride;  // never split a frame
            if (toRead <= 0) break;
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;
            remaining -= read;

            for (int off = 0; off + frameStride <= read; off += frameStride)
            {
                float maxSample = 0;
                for (int c = 0; c < numChannels; c++)
                {
                    short s = BitConverter.ToInt16(buffer, off + c * bytesPerSample);
                    float f = Math.Abs(s) / 32768f;
                    if (f > maxSample) maxSample = f;
                }
                int bucket = (int)(frameIdx / framesPerBucket);
                if ((uint)bucket < (uint)peaks.Length && maxSample > peaks[bucket])
                    peaks[bucket] = maxSample;
                frameIdx++;
            }
        }

        return new PeakData
        {
            Peaks            = peaks,
            BucketsPerSecond = BucketsPerSecond,
            Duration         = TimeSpan.FromSeconds(durationSec),
        };
    }
}
