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
