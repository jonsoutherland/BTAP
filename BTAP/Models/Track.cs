using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BTAP.Models;

public enum TrackKind { Video, Audio, Title }

public partial class Track : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private TrackKind _kind;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSolo;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private int _height = 44;

    public ObservableCollection<TimelineClip> Clips { get; } = [];
}
