using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BTAP.Models;

namespace BTAP.ViewModels;

public enum EditorMode { Cut, Edit, Color, Audio, Export }
public enum ActiveTool { Cursor, Razor, Text, Hand, Mask, Crop }

public partial class EditorViewModel : ObservableObject
{
    private readonly Project _project;

    public EditorViewModel(Project project)
    {
        _project = project;

        // Initial minimum sequence length so an empty timeline still scrolls
        if (_project.Duration < MinDuration) _project.Duration = MinDuration;

        History.Changed += OnHistoryChanged;
        WireClipObservers();
    }

    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(30);

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _project.IsModified = true;
        RecomputeDuration();
    }

    /// <summary>Re-subscribe to clip collection changes whenever the project loads/changes.</summary>
    private void WireClipObservers()
    {
        foreach (var t in _project.Tracks) WireTrackClips(t);

        _project.Tracks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (Track t in e.NewItems) WireTrackClips(t);
            RecomputeDuration();
        };
    }

    private void WireTrackClips(Track t) =>
        t.Clips.CollectionChanged += (_, _) => RecomputeDuration();

    /// <summary>Sets Project.Duration to max clip end + 10s buffer, or MinDuration if empty.
    /// Also clamps Playhead to the new duration if it's now out of range.</summary>
    public void RecomputeDuration()
    {
        TimeSpan max = TimeSpan.Zero;
        foreach (var t in _project.Tracks)
            foreach (var c in t.Clips)
                if (c.TimelineEnd > max) max = c.TimelineEnd;

        var target = max + TimeSpan.FromSeconds(10);
        if (target < MinDuration) target = MinDuration;
        if (target != _project.Duration)
        {
            _project.Duration = target;
            OnPropertyChanged(nameof(DurationLabel));
        }

        // Whether or not Duration changed, the playhead may now be past the end
        if (_project.Playhead > _project.Duration)
        {
            _project.Playhead = _project.Duration;
            OnPropertyChanged(nameof(PlayheadLabel));
        }
    }

    public Project Project => _project;
    public ObservableCollection<Track> Tracks => _project.Tracks;
    public ObservableCollection<MediaItem> MediaBin => _project.MediaBin;
    public EditHistory History { get; } = new();

    /// <summary>The set of automation keyframes currently selected, shared between
    /// the Automations inspector list and the timeline diamond overlays so click
    /// state stays in sync from either side. ReferenceEqualityComparer keeps
    /// selection keyed to the actual ObservableObject instance, which is what
    /// both sides reference.</summary>
    public HashSet<EffectKeyframe> SelectedKeyframes { get; } =
        new HashSet<EffectKeyframe>(ReferenceEqualityComparer.Instance);

    public event EventHandler? SelectedKeyframesChanged;

    public void SetKeyframeSelection(EffectKeyframe kf, bool additive)
    {
        if (!additive)
        {
            SelectedKeyframes.Clear();
            SelectedKeyframes.Add(kf);
        }
        else if (!SelectedKeyframes.Add(kf))
        {
            SelectedKeyframes.Remove(kf);
        }
        SelectedKeyframesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearKeyframeSelection()
    {
        if (SelectedKeyframes.Count == 0) return;
        SelectedKeyframes.Clear();
        SelectedKeyframesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseSelectedKeyframesChanged() =>
        SelectedKeyframesChanged?.Invoke(this, EventArgs.Empty);

    [ObservableProperty] private EditorMode _mode = EditorMode.Edit;
    [ObservableProperty] private ActiveTool _activeTool = ActiveTool.Cursor;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private bool _snapEnabled = true;
    [ObservableProperty] private bool _magneticTimeline = true;
    [ObservableProperty] private double _timelineZoom = 1.0;    // 0.25 – 4.0
    [ObservableProperty] private TimeSpan _playhead;
    [ObservableProperty] private TimelineClip? _selectedClip;
    [ObservableProperty] private MediaItem? _selectedMedia;
    [ObservableProperty] private string _mediaLibraryTab = "media";
    [ObservableProperty] private string _inspectorTab = "properties";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private bool _fullscreenPreview;

    // Derived
    public string PlayheadLabel => FormatTimecode(_project.Playhead);
    public string DurationLabel => FormatTimecode(_project.Duration);

    private static string FormatTimecode(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}:{(int)(ts.Milliseconds / 41.667):D2}";

    [RelayCommand]
    private void TogglePlay()
    {
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    private void StepForward()
    {
        _project.Playhead += TimeSpan.FromSeconds(1.0 / _project.FrameRate);
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    [RelayCommand]
    private void StepBack()
    {
        var frame = TimeSpan.FromSeconds(1.0 / _project.FrameRate);
        _project.Playhead = _project.Playhead > frame ? _project.Playhead - frame : TimeSpan.Zero;
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        if (SelectedClip is null) return;
        var track = _project.Tracks.FirstOrDefault(t => t.Clips.Contains(SelectedClip));
        if (track is null) return;
        var splitAt = _project.Playhead - SelectedClip.TimelineStart;
        if (splitAt <= TimeSpan.Zero || splitAt >= SelectedClip.Duration) return;
        History.Record(new ClipSplitAction(track, SelectedClip, splitAt));
    }

    public void Undo()
    {
        History.Undo();
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    public void Redo()
    {
        History.Redo();
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    public void DeleteSelectedClip()
    {
        if (SelectedClip is null) return;
        var track = _project.Tracks.FirstOrDefault(t => t.Clips.Contains(SelectedClip));
        if (track is null) return;
        var idx = track.Clips.IndexOf(SelectedClip);
        var clip = SelectedClip;
        SelectedClip = null;
        History.Record(new ClipDeleteAction(track, idx, clip));
    }

    [RelayCommand]
    private void AddMarker()
    {
        _project.Markers.Add(new Marker { Label = "Marker", Position = _project.Playhead });
    }

    [RelayCommand]
    private void ZoomIn() => TimelineZoom = Math.Min(4.0, TimelineZoom * 1.25);

    [RelayCommand]
    private void ZoomOut() => TimelineZoom = Math.Max(0.1, TimelineZoom / 1.25);

}
