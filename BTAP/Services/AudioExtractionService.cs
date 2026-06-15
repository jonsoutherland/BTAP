using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace BTAP.Services;

/// <summary>
/// Extracts PCM audio from any media file Windows can decode. Mirrors WaveformService's
/// transcode-to-temp-WAV approach but produces 16 kHz mono float samples — the exact
/// format Whisper expects — and returns the raw sample buffer instead of bucketed peaks.
/// </summary>
public static class AudioExtractionService
{
    public const int WhisperSampleRate = 16000;

    public sealed class ExtractedAudio
    {
        public required float[] Samples { get; init; }
        public required int SampleRate { get; init; }
        public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
    }

    /// <summary>Transcodes <paramref name="filePath"/> to a temp 16 kHz mono PCM16 WAV,
    /// reads the samples back as float in [-1, 1], deletes the temp file. Returns null
    /// if the source can't be decoded.</summary>
    public static async Task<ExtractedAudio?> ExtractMono16kAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        StorageFile sourceFile;
        try { sourceFile = await StorageFile.GetFileFromPathAsync(filePath); }
        catch { return null; }

        // ApplicationData.Current would throw "operation is not valid due to the
        // current state of the object" for unpackaged WinUI apps (BTAP is one).
        // Use the OS temp folder via a path-based StorageFolder lookup instead so
        // we still don't pollute the source media directory.
        StorageFile tempFile;
        try
        {
            var tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            var tempName   = $".btap_whisper_{Guid.NewGuid():N}.wav";
            tempFile = await tempFolder.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting);
        }
        catch { return null; }

        try
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(WhisperSampleRate, 1, 16);

            var transcoder = new MediaTranscoder();
            var prep = await transcoder.PrepareFileTranscodeAsync(sourceFile, tempFile, profile);
            if (!prep.CanTranscode) return null;
            await prep.TranscodeAsync();

            return ReadWavAsMonoFloat(tempFile.Path);
        }
        catch { return null; }
        finally
        {
            try { await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
        }
    }

    private static ExtractedAudio? ReadWavAsMonoFloat(string wavPath)
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
                reader.ReadInt16();           // audio format
                numChannels   = reader.ReadInt16();
                sampleRate    = reader.ReadInt32();
                reader.ReadInt32();           // byte rate
                reader.ReadInt16();           // block align
                bitsPerSample = reader.ReadInt16();
                stream.Position = fmtEnd;
            }
            else if (chunkId == "data")
            {
                dataStart  = stream.Position;
                dataLength = chunkSize;
                break;
            }
            else stream.Position += chunkSize;
        }

        if (dataStart < 0 || numChannels == 0 || bitsPerSample != 16 || sampleRate <= 0)
            return null;

        int  bytesPerSample = bitsPerSample / 8;
        int  frameStride    = bytesPerSample * numChannels;
        long totalFrames    = dataLength / frameStride;
        if (totalFrames <= 0) return null;

        var samples = new float[totalFrames];
        stream.Position = dataStart;
        var  buffer    = new byte[Math.Min(64 * 1024, dataLength)];
        long frameIdx  = 0;
        long remaining = dataLength;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            toRead -= toRead % frameStride;
            if (toRead <= 0) break;
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;
            remaining -= read;

            for (int off = 0; off + frameStride <= read; off += frameStride)
            {
                // Average channels into mono so a stereo source still yields a single
                // float per frame at index frameIdx.
                int acc = 0;
                for (int c = 0; c < numChannels; c++)
                    acc += BitConverter.ToInt16(buffer, off + c * bytesPerSample);
                float f = (float)acc / numChannels / 32768f;
                if (frameIdx < samples.Length) samples[frameIdx] = f;
                frameIdx++;
            }
        }

        return new ExtractedAudio { Samples = samples, SampleRate = sampleRate };
    }
}
