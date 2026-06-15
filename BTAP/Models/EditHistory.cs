namespace BTAP.Models;

public interface IEditAction
{
    string Description { get; }
    void Do();
    void Undo();
}

/// <summary>Maintains undo and redo stacks for editor operations.</summary>
public sealed class EditHistory
{
    private readonly Stack<IEditAction> _undo = new();
    private readonly Stack<IEditAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.TryPeek(out var a) ? $"Undo {a.Description}" : null;
    public string? RedoLabel => _redo.TryPeek(out var a) ? $"Redo {a.Description}" : null;

    public event EventHandler? Changed;

    public void Record(IEditAction action)
    {
        action.Do();
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordWithoutDo(IEditAction action)
    {
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.TryPop(out var action))
        {
            action.Undo();
            _redo.Push(action);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var action))
        {
            action.Do();
            _undo.Push(action);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

// ── Concrete action types ─────────────────────────────────────────────────────

public sealed class ClipAddAction(Track track, TimelineClip clip) : IEditAction
{
    public string Description => $"Add \"{clip.Label}\"";
    public void Do()   { if (!track.Clips.Contains(clip)) track.Clips.Add(clip); }
    public void Undo() => track.Clips.Remove(clip);
}

public sealed class ClipDeleteAction(Track track, int index, TimelineClip clip) : IEditAction
{
    public string Description => $"Delete \"{clip.Label}\"";
    public void Do()   => track.Clips.Remove(clip);
    public void Undo() => track.Clips.Insert(Math.Min(index, track.Clips.Count), clip);
}

public sealed class ClipMoveAction(TimelineClip clip, TimeSpan from, TimeSpan to) : IEditAction
{
    public string Description => $"Move \"{clip.Label}\"";
    public void Do()   => clip.TimelineStart = to;
    public void Undo() => clip.TimelineStart = from;
}

/// <summary>Move a clip from one track to another, preserving order on the destination.
/// Used by vertical clip drag and the Ctrl+Up/Down "nudge to next track" shortcut.</summary>
public sealed class ClipReparentAction(Track fromTrack, Track toTrack, TimelineClip clip, int fromIndex) : IEditAction
{
    public string Description => $"Move \"{clip.Label}\" to {toTrack.Label}";

    public void Do()
    {
        if (fromTrack.Clips.Contains(clip)) fromTrack.Clips.Remove(clip);
        if (!toTrack.Clips.Contains(clip))  toTrack.Clips.Add(clip);
    }

    public void Undo()
    {
        if (toTrack.Clips.Contains(clip)) toTrack.Clips.Remove(clip);
        if (!fromTrack.Clips.Contains(clip))
            fromTrack.Clips.Insert(Math.Min(fromIndex, fromTrack.Clips.Count), clip);
    }
}

/// <summary>Create a brand-new track at <paramref name="newTrackPosition"/> in the
/// project's track list and move <paramref name="clip"/> onto it. Undo reverses
/// both halves: clip goes back to its old track at <paramref name="fromIndex"/>,
/// new track is removed entirely.</summary>
public sealed class ClipMoveToNewTrackAction(
    System.Collections.ObjectModel.ObservableCollection<Track> tracks,
    Track fromTrack, int fromIndex, TimelineClip clip,
    Track newTrack, int newTrackPosition) : IEditAction
{
    public string Description => $"Move \"{clip.Label}\" to new track {newTrack.Label}";

    public void Do()
    {
        if (!tracks.Contains(newTrack))
            tracks.Insert(Math.Min(newTrackPosition, tracks.Count), newTrack);
        if (fromTrack.Clips.Contains(clip)) fromTrack.Clips.Remove(clip);
        if (!newTrack.Clips.Contains(clip)) newTrack.Clips.Add(clip);
    }

    public void Undo()
    {
        if (newTrack.Clips.Contains(clip)) newTrack.Clips.Remove(clip);
        if (!fromTrack.Clips.Contains(clip))
            fromTrack.Clips.Insert(Math.Min(fromIndex, fromTrack.Clips.Count), clip);
        tracks.Remove(newTrack);
    }
}

public sealed class ClipTrimAction(
    TimelineClip clip,
    TimeSpan oldStart, TimeSpan oldDuration, TimeSpan oldSourceStart,
    TimeSpan newStart, TimeSpan newDuration, TimeSpan newSourceStart) : IEditAction
{
    public string Description => $"Trim \"{clip.Label}\"";
    public void Do()
    {
        clip.TimelineStart = newStart;
        clip.Duration      = newDuration;
        clip.SourceStart   = newSourceStart;
    }
    public void Undo()
    {
        clip.TimelineStart = oldStart;
        clip.Duration      = oldDuration;
        clip.SourceStart   = oldSourceStart;
    }
}

public sealed class ClipSplitAction : IEditAction
{
    private readonly Track        _track;
    private readonly TimelineClip _original;
    private readonly TimeSpan     _originalDuration;
    private readonly TimeSpan     _splitAt;          // relative to clip start
    private TimelineClip?         _second;

    public ClipSplitAction(Track track, TimelineClip original, TimeSpan splitAt)
    {
        _track = track;
        _original = original;
        _originalDuration = original.Duration;
        _splitAt = splitAt;
    }

    public string Description => $"Split \"{_original.Label}\"";

    public void Do()
    {
        _original.Duration = _splitAt;
        if (_second is null)
        {
            _second = new TimelineClip
            {
                Label         = _original.Label + " (cut)",
                Kind          = _original.Kind,
                TimelineStart = _original.TimelineStart + _splitAt,
                Duration      = _originalDuration - _splitAt,
                SourceStart   = _original.SourceStart + _splitAt,
                ColorHue      = _original.ColorHue,
                SourceId      = _original.SourceId,
            };
        }
        if (!_track.Clips.Contains(_second))
            _track.Clips.Insert(_track.Clips.IndexOf(_original) + 1, _second);
    }

    public void Undo()
    {
        _original.Duration = _originalDuration;
        if (_second is not null) _track.Clips.Remove(_second);
    }
}

public sealed class ClipDuplicateAction : IEditAction
{
    private readonly Track        _track;
    private readonly TimelineClip _source;
    private TimelineClip?         _copy;

    public ClipDuplicateAction(Track track, TimelineClip source) { _track = track; _source = source; }
    public string Description => $"Duplicate \"{_source.Label}\"";

    public void Do()
    {
        if (_copy is null)
        {
            _copy = new TimelineClip
            {
                Label         = _source.Label,
                Kind          = _source.Kind,
                TimelineStart = _source.TimelineEnd,
                Duration      = _source.Duration,
                SourceStart   = _source.SourceStart,
                Volume        = _source.Volume,
                Speed         = _source.Speed,
                ColorHue      = _source.ColorHue,
                SourceId      = _source.SourceId,
            };
        }
        if (!_track.Clips.Contains(_copy))
            _track.Clips.Insert(_track.Clips.IndexOf(_source) + 1, _copy);
    }

    public void Undo()
    {
        if (_copy is not null) _track.Clips.Remove(_copy);
    }
}

/// <summary>Pulls the audio out of a video clip onto its own audio track:
/// silences the source video clip and creates a matching audio clip on an audio
/// track positioned just below the video track. If no audio track exists at the
/// right spot, one is created and inserted as part of the action so undo can
/// remove it again.</summary>
public sealed class SeparateAudioAction : IEditAction
{
    private readonly Project        _project;
    private readonly TimelineClip   _videoClip;
    private readonly Track          _targetAudioTrack;   // may be a brand-new track
    private readonly TimelineClip   _audioClip;
    private readonly bool           _trackIsNew;
    private readonly int            _trackInsertIndex;
    private readonly double         _originalVolume;

    public SeparateAudioAction(Project project, Track videoTrack, TimelineClip videoClip,
                               Track? existingAudioTrack, int newTrackInsertIndex)
    {
        _project = project;
        _videoClip = videoClip;
        _originalVolume = videoClip.Volume;

        if (existingAudioTrack is not null)
        {
            _targetAudioTrack = existingAudioTrack;
            _trackIsNew = false;
            _trackInsertIndex = -1;
        }
        else
        {
            int n = project.Tracks.Count(t => t.Kind == TrackKind.Audio) + 1;
            _targetAudioTrack = new Track { Label = $"A{n}", Kind = TrackKind.Audio };
            _trackIsNew = true;
            _trackInsertIndex = newTrackInsertIndex;
        }

        _audioClip = new TimelineClip
        {
            Label         = videoClip.Label,
            Kind          = ClipKind.Audio,
            TimelineStart = videoClip.TimelineStart,
            Duration      = videoClip.Duration,
            SourceStart   = videoClip.SourceStart,
            Volume        = _originalVolume,   // carry the original level over to the new audio clip
            Speed         = videoClip.Speed,
            SourceId      = videoClip.SourceId,
            ColorHue      = 100,               // green-ish, matches the audio palette
        };
    }

    public string Description => $"Separate audio from \"{_videoClip.Label}\"";

    public void Do()
    {
        if (_trackIsNew && !_project.Tracks.Contains(_targetAudioTrack))
        {
            int idx = Math.Clamp(_trackInsertIndex, 0, _project.Tracks.Count);
            _project.Tracks.Insert(idx, _targetAudioTrack);
        }
        if (!_targetAudioTrack.Clips.Contains(_audioClip))
            _targetAudioTrack.Clips.Add(_audioClip);
        _videoClip.Volume = 0;
    }

    public void Undo()
    {
        _videoClip.Volume = _originalVolume;
        _targetAudioTrack.Clips.Remove(_audioClip);
        if (_trackIsNew) _project.Tracks.Remove(_targetAudioTrack);
    }
}

/// <summary>Deletes a clip and shifts every clip on the same track that started after it backward by the deleted clip's duration.</summary>
public sealed class ClipRippleDeleteAction : IEditAction
{
    private readonly Track _track;
    private readonly int   _index;
    private readonly TimelineClip _clip;
    private readonly TimeSpan _gap;
    private readonly List<TimelineClip> _shifted = new();

    public ClipRippleDeleteAction(Track track, int index, TimelineClip clip)
    {
        _track = track;
        _index = index;
        _clip  = clip;
        _gap   = clip.Duration;
    }

    public string Description => $"Ripple-delete \"{_clip.Label}\"";

    public void Do()
    {
        var threshold = _clip.TimelineStart;
        _shifted.Clear();
        foreach (var c in _track.Clips)
            if (c != _clip && c.TimelineStart >= threshold)
                _shifted.Add(c);

        _track.Clips.Remove(_clip);
        foreach (var c in _shifted) c.TimelineStart -= _gap;
    }

    public void Undo()
    {
        foreach (var c in _shifted) c.TimelineStart += _gap;
        _shifted.Clear();
        _track.Clips.Insert(Math.Min(_index, _track.Clips.Count), _clip);
    }
}
