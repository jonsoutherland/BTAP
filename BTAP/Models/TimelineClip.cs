using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BTAP.Models;

public enum ClipKind { Video, Audio, Music, Title }

public partial class TimelineClip : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private ClipKind _kind;
    [ObservableProperty] private TimeSpan _timelineStart;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _sourceStart;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private string? _sourceId;

    // Transform
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _posX;
    [ObservableProperty] private double _posY;
    [ObservableProperty] private double _rotation;
    [ObservableProperty] private double _opacity = 1.0;
    [ObservableProperty] private bool   _flipX;
    [ObservableProperty] private bool   _flipY;

    // Crop (0..1 fractions of the source frame; 0 = no crop)
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;

    // Audio
    [ObservableProperty] private double _pan;            // -1.0 .. 1.0
    [ObservableProperty] private double _fadeInMs;       // 0..5000
    [ObservableProperty] private double _fadeOutMs;
    [ObservableProperty] private double _eqLow;          // dB, -12..+12
    [ObservableProperty] private double _eqMid;
    [ObservableProperty] private double _eqHigh;

    // Color
    [ObservableProperty] private double _exposure;       // -2..+2 stops
    [ObservableProperty] private double _contrast;       // -100..+100
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _lift;
    [ObservableProperty] private double _gamma;
    [ObservableProperty] private double _colorGain;

    // Title text formatting (only meaningful when Kind == Title)
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _fontSize = 64;
    [ObservableProperty] private bool   _isBold;
    [ObservableProperty] private bool   _isItalic;
    [ObservableProperty] private bool   _isUnderline;
    [ObservableProperty] private string _textColor = "#FFFFFFFF";
    [ObservableProperty] private string _textAlign = "Center"; // Left, Center, Right

    // Effects applied to this clip
    public ObservableCollection<ClipEffect> Effects { get; } = [];

    // Volume automation envelope. Empty = flat at Volume. Each point's TimeRel is a
    // fraction of the clip's Duration (0..1) so trimming the clip leaves the envelope
    // proportionally intact. Points are not required to be sorted in storage —
    // GetVolumeAt sorts on read.
    public ObservableCollection<VolumePoint> VolumeEnvelope { get; } = [];

    public double GetVolumeAt(double timeRel)
    {
        if (VolumeEnvelope.Count == 0) return Volume;
        if (VolumeEnvelope.Count == 1) return VolumeEnvelope[0].Volume;

        VolumePoint? prev = null;
        VolumePoint? next = null;
        foreach (var p in VolumeEnvelope)
        {
            if (p.TimeRel <= timeRel && (prev is null || p.TimeRel > prev.TimeRel)) prev = p;
            if (p.TimeRel >= timeRel && (next is null || p.TimeRel < next.TimeRel)) next = p;
        }
        if (prev is null && next is not null) return next.Volume;
        if (next is null && prev is not null) return prev.Volume;
        if (prev is null || next is null) return Volume;
        if (ReferenceEquals(prev, next)) return prev.Volume;
        double span = next.TimeRel - prev.TimeRel;
        if (span <= 0) return prev.Volume;
        double t = (timeRel - prev.TimeRel) / span;
        return prev.Volume + (next.Volume - prev.Volume) * t;
    }

    // Hue for filmstrip color (0–360)
    public int ColorHue { get; init; } = 138;

    public TimeSpan TimelineEnd => TimelineStart + Duration;
}

public partial class VolumePoint : ObservableObject
{
    [ObservableProperty] private double _timeRel; // 0..1 within clip duration
    [ObservableProperty] private double _volume = 1.0; // 0..1
}

public partial class ClipEffect : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _intensity = 1.0;
    [ObservableProperty] private bool _enabled = true;
}
