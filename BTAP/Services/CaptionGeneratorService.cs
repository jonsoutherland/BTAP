using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Turns a flat word-timestamp list (from Whisper) into a sequence of Title clips
/// on a dedicated "Captions" track. Each clip groups exactly <c>wordsPerCaption</c>
/// spoken words and spans the exact wall-clock interval from the first word's start
/// to the last word's end — so the on-screen caption appears and disappears
/// perfectly in sync with the video's audio.
/// </summary>
public static class CaptionGeneratorService
{
    public const string CaptionTrackLabel = "Captions";

    /// <summary>Find the project's caption track, or create one at the top of the
    /// track stack so captions render above all video layers.</summary>
    public static Track GetOrCreateCaptionTrack(Project project)
    {
        // Identify by label first (idempotent across regenerations) then fall back
        // to the first title track if the user renamed it.
        var existing = project.Tracks.FirstOrDefault(t =>
            t.Kind == TrackKind.Title &&
            string.Equals(t.Label, CaptionTrackLabel, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var track = new Track { Label = CaptionTrackLabel, Kind = TrackKind.Title };
        project.Tracks.Insert(0, track);
        return track;
    }

    /// <summary>
    /// Removes any prior caption clips on the project's caption track. Used so
    /// "regenerate" with a different words-per-caption value cleanly replaces the
    /// existing captions instead of stacking duplicates.
    /// </summary>
    public static void ClearCaptionsForRange(Project project, TimeSpan rangeStart, TimeSpan rangeEnd)
    {
        var track = project.Tracks.FirstOrDefault(t =>
            t.Kind == TrackKind.Title &&
            string.Equals(t.Label, CaptionTrackLabel, StringComparison.OrdinalIgnoreCase));
        if (track is null) return;
        for (int i = track.Clips.Count - 1; i >= 0; i--)
        {
            var c = track.Clips[i];
            // Anything that overlaps the regenerated range gets removed.
            if (c.TimelineEnd > rangeStart && c.TimelineStart < rangeEnd)
                track.Clips.RemoveAt(i);
        }
    }

    /// <summary>
    /// Materialize Title clips on the caption track. Word timestamps are interpreted
    /// as offsets within the source clip and shifted by <paramref name="audioOriginInTimeline"/>
    /// so callers can generate captions from any video on the timeline.
    /// </summary>
    /// <param name="wordsPerCaption">How many words per on-screen caption (1-20).</param>
    /// <returns>The created clips, in timeline order.</returns>
    public static List<TimelineClip> BuildCaptionClips(
        Project project,
        IReadOnlyList<TranscribedWord> words,
        TimeSpan audioOriginInTimeline,
        int wordsPerCaption,
        double fontSize = 56,
        string textColor = "#FFFFFFFF")
    {
        var result = new List<TimelineClip>();
        if (words.Count == 0) return result;
        wordsPerCaption = Math.Clamp(wordsPerCaption, 1, 20);

        var track = GetOrCreateCaptionTrack(project);

        // Pre-compute each chunk's natural start/end first, then extend each chunk's
        // end forward to the next chunk's start. Standard subtitle behavior — every
        // moment on the timeline shows exactly one caption, with no gaps. Without
        // this, 1-word captions blink off and on as the speaker pauses between words.
        var chunks = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        for (int i = 0; i < words.Count; i += wordsPerCaption)
        {
            int end = Math.Min(i + wordsPerCaption, words.Count);
            var first = words[i];
            var last  = words[end - 1];
            var text  = string.Join(' ', words.Skip(i).Take(end - i).Select(w => w.Text));
            chunks.Add((first.Start, last.End, text));
        }

        // Cap the gap-fill: holding a caption for >2.5s past its last word looks like
        // the system froze when the speaker takes a long pause. Beyond that we
        // honor the caption's natural end and let the gap stay empty.
        var maxHold = TimeSpan.FromSeconds(2.5);

        for (int i = 0; i < chunks.Count; i++)
        {
            var (start, naturalEnd, text) = chunks[i];
            TimeSpan effectiveEnd = naturalEnd;
            if (i + 1 < chunks.Count)
            {
                var nextStart = chunks[i + 1].Start;
                // Extend to just before the next caption, but only up to maxHold.
                var hold = nextStart - naturalEnd;
                if (hold > TimeSpan.Zero)
                    effectiveEnd = naturalEnd + (hold < maxHold ? hold : maxHold);
                if (effectiveEnd > nextStart) effectiveEnd = nextStart;
            }

            var timelineStart = audioOriginInTimeline + start;
            var duration      = effectiveEnd - start;
            // Whisper occasionally rounds tokens to the same timestamp on very short
            // utterances; guarantee a non-zero duration so the clip is visible.
            if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMilliseconds(120);
            if (timelineStart < TimeSpan.Zero) timelineStart = TimeSpan.Zero;

            var clip = new TimelineClip
            {
                Label         = text,
                Kind          = ClipKind.Title,
                TimelineStart = timelineStart,
                Duration      = duration,
                ColorHue      = 200,
                FontSize      = fontSize,
                IsBold        = true,
                TextColor     = textColor,
                TextAlign     = "Center",
                // Anchor toward the lower-third by default so captions don't cover faces.
                PosY          = project.GetCanvasSize().Height * 0.32,
            };
            track.Clips.Add(clip);
            result.Add(clip);
        }
        return result;
    }
}
