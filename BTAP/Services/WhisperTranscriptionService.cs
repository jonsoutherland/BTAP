using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace BTAP.Services;

public sealed record TranscribedWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// Wraps Whisper.net (whisper.cpp via ggml). Downloads the model on first use into
/// %LocalAppData%\BTAP\whisper_models, then transcribes 16 kHz mono PCM into a flat
/// list of words with per-word start/end timings.
///
/// Word-level timing comes from configuring the processor with
/// <c>WithSplitOnWord(true)</c> so whisper.cpp emits segments at word boundaries.
/// Multi-word segments (which still happen for tightly-bunched speech) are split
/// post-hoc by distributing the segment's duration across tokens proportional to
/// character length — close enough for caption timing.
/// </summary>
public static class WhisperTranscriptionService
{
    public enum ModelSize { Tiny, Base, Small, Medium }

    private static string ModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BTAP", "whisper_models");

    private static string ModelFileName(ModelSize size) => size switch
    {
        ModelSize.Tiny   => "ggml-tiny.bin",
        ModelSize.Base   => "ggml-base.bin",
        ModelSize.Small  => "ggml-small.bin",
        ModelSize.Medium => "ggml-medium.bin",
        _                => "ggml-base.bin",
    };

    private static GgmlType MapGgml(ModelSize size) => size switch
    {
        ModelSize.Tiny   => GgmlType.Tiny,
        ModelSize.Base   => GgmlType.Base,
        ModelSize.Small  => GgmlType.Small,
        ModelSize.Medium => GgmlType.Medium,
        _                => GgmlType.Base,
    };

    public static bool IsModelDownloaded(ModelSize size) =>
        File.Exists(Path.Combine(ModelsDir, ModelFileName(size)));

    /// <summary>Downloads the ggml model on first call; no-ops on later calls.
    /// Reports raw byte progress so the UI can show transferred MB.</summary>
    public static async Task EnsureModelDownloadedAsync(
        ModelSize size,
        IProgress<long>? bytesProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDir);
        var modelPath = Path.Combine(ModelsDir, ModelFileName(size));
        if (File.Exists(modelPath)) return;

        var tempPath = modelPath + ".part";
        try
        {
            await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(MapGgml(size));
            await using (var fs = File.Create(tempPath))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    int read = await modelStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read <= 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    bytesProgress?.Report(total);
                }
            }
            File.Move(tempPath, modelPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>Runs Whisper over a flat 16 kHz mono float buffer and returns one entry
    /// per spoken word with absolute start/end times measured from t=0 of the buffer.
    /// Uses Whisper's token-level timestamps (enabled via <c>WithTokenTimestamps</c>)
    /// for tight word-by-word alignment, instead of distributing segment time across
    /// characters.</summary>
    public static async Task<List<TranscribedWord>> TranscribeAsync(
        float[] samples,
        ModelSize size = ModelSize.Base,
        string language = "en",
        IProgress<int>? percentProgress = null,
        CancellationToken ct = default)
    {
        if (samples.Length == 0) return new();

        var modelPath = Path.Combine(ModelsDir, ModelFileName(size));
        if (!File.Exists(modelPath))
            throw new InvalidOperationException(
                $"Whisper model '{ModelFileName(size)}' is missing. Call EnsureModelDownloadedAsync first.");

        using var factory = WhisperFactory.FromPath(modelPath);
        // Token timestamps give per-BPE-token start/end times. Grouping tokens by
        // the leading-space convention reconstructs word-level timings with ~20 ms
        // granularity — much tighter than dividing a segment's duration across
        // characters. SplitOnWord is still useful so segment boundaries also land
        // at word boundaries, avoiding tokens being orphaned across segments.
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .SplitOnWord()
            .Build();

        double totalSeconds = (double)samples.Length / AudioExtractionService.WhisperSampleRate;
        var words = new List<TranscribedWord>();

        await foreach (var segment in processor.ProcessAsync(samples).WithCancellation(ct))
        {
            if (percentProgress is not null && totalSeconds > 0)
            {
                int pct = Math.Clamp((int)Math.Round(segment.End.TotalSeconds / totalSeconds * 100), 0, 100);
                percentProgress.Report(pct);
            }
            ExtractWordsFromSegment(segment, words);
        }

        percentProgress?.Report(100);
        return words;
    }

    /// <summary>Whisper token timestamps are reported in centiseconds (10 ms units).
    /// Convert to a TimeSpan.</summary>
    private static TimeSpan TokenTimeToSpan(long centiseconds) =>
        TimeSpan.FromMilliseconds(centiseconds * 10.0);

    /// <summary>
    /// Reconstructs words from a segment's token stream. Whisper's BPE tokens that
    /// begin a new word start with a space character (' the', ' world'); continuation
    /// tokens within a word do not. We accumulate tokens until a space-prefixed token
    /// arrives, then flush the accumulated text + first/last token timestamps as one
    /// word.
    ///
    /// Special tokens (timestamps, language ID, transcript markers) come back with
    /// negative IDs or with text wrapped in <c>[_…]</c> / <c>&lt;|…|&gt;</c>; they
    /// must be skipped so they don't poison word timing.
    ///
    /// As a safety net, if a segment has no usable tokens (e.g. token stream is
    /// empty), we fall back to splitting the segment's text on whitespace and
    /// distributing the segment time proportionally — same behavior as before.
    /// </summary>
    private static void ExtractWordsFromSegment(SegmentData segment, List<TranscribedWord> words)
    {
        var tokens = segment.Tokens;
        if (tokens is not null && tokens.Length > 0)
        {
            string currentWord = string.Empty;
            TimeSpan currentStart = segment.Start;
            TimeSpan currentEnd   = segment.Start;
            bool inWord = false;

            void Flush()
            {
                var trimmed = currentWord.Trim();
                if (trimmed.Length > 0)
                    words.Add(new TranscribedWord(trimmed, currentStart, currentEnd));
                currentWord = string.Empty;
                inWord = false;
            }

            int usedTokens = 0;
            foreach (var t in tokens)
            {
                var text = t.Text ?? string.Empty;
                if (string.IsNullOrEmpty(text)) continue;
                if (text.StartsWith("[_") || (text.StartsWith("<|") && text.EndsWith("|>"))) continue;
                if (t.Id < 0) continue; // special / negative-id tokens

                bool startsNewWord = text.StartsWith(' ');
                if (startsNewWord && inWord) Flush();

                var tokStart = TokenTimeToSpan(t.Start);
                var tokEnd   = TokenTimeToSpan(t.End);
                // Whisper occasionally emits zero-length tokens; keep the word's
                // running end at least at the token start so timings stay monotonic.
                if (tokEnd < tokStart) tokEnd = tokStart;

                if (!inWord)
                {
                    currentStart = tokStart;
                    inWord = true;
                }
                currentWord += text;
                if (tokEnd > currentEnd) currentEnd = tokEnd;
                if (!inWord || currentEnd < tokEnd) currentEnd = tokEnd;
                usedTokens++;
            }
            if (inWord) Flush();
            if (usedTokens > 0) return;
        }

        // Fallback: no usable tokens — split text and spread the segment's duration
        // across words proportionally to character length.
        var rawText = segment.Text?.Trim() ?? string.Empty;
        if (rawText.Length == 0) return;
        var parts = rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        if (parts.Length == 1)
        {
            words.Add(new TranscribedWord(parts[0], segment.Start, segment.End));
            return;
        }
        double segDurMs = (segment.End - segment.Start).TotalMilliseconds;
        if (segDurMs <= 0) segDurMs = parts.Length * 200;
        int totalChars = parts.Sum(p => p.Length);
        if (totalChars <= 0) return;
        double cursor = 0;
        foreach (var p in parts)
        {
            double share  = (double)p.Length / totalChars;
            double startMs = cursor;
            double endMs   = cursor + share * segDurMs;
            cursor = endMs;
            words.Add(new TranscribedWord(
                p,
                segment.Start + TimeSpan.FromMilliseconds(startMs),
                segment.Start + TimeSpan.FromMilliseconds(endMs)));
        }
    }
}
