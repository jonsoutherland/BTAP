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
    [ObservableProperty] private double _lift;           // -50..+50, shifts shadows
    [ObservableProperty] private double _gamma;          // -50..+50, midtone gamma
    [ObservableProperty] private double _colorGain;      // -50..+50, scales highlights
    // Color overlay / tint. Stored as #AARRGGBB; the alpha channel drives the
    // blend amount so #00000000 = no overlay. Cross-faded over the source after
    // the lift/gamma/gain pass so the user sees the tint on top of their grade.
    [ObservableProperty] private string _colorOverlay = "#00000000";

    // Title text formatting (only meaningful when Kind == Title)
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _fontSize = 64;
    [ObservableProperty] private bool   _isBold;
    [ObservableProperty] private bool   _isItalic;
    [ObservableProperty] private bool   _isUnderline;
    [ObservableProperty] private string _textColor = "#FFFFFFFF";
    [ObservableProperty] private string _textAlign = "Center"; // Left, Center, Right
    // Background fill behind the rendered text. Default fully transparent so existing
    // titles look unchanged; any non-zero alpha paints a colored box behind the text.
    [ObservableProperty] private string _textBackground = "#00000000";

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

    // Per-effect parameters. Numbers cover sliders (amount, angle, radius, etc.);
    // Strings cover colors and other non-numeric values (stored as #AARRGGBB hex).
    // Bag-style storage so the model stays effect-agnostic — ClipEffectsChain owns
    // the parameter names and defaults for each effect.
    public Dictionary<string, double> Numbers { get; init; } = new();
    public Dictionary<string, string> Strings { get; init; } = new();

    // Per-parameter automation: list of keyframes interpolated linearly between.
    // When a parameter has any keyframes, GetAutomatedNumber returns the
    // interpolated value at the given clip-relative time (0..1) and Numbers is
    // ignored for that key. Empty/missing = use static Numbers value.
    public Dictionary<string, ObservableCollection<EffectKeyframe>> Keyframes { get; init; } = new();

    public double GetNumber(string key, double @default) =>
        Numbers.TryGetValue(key, out var v) ? v : @default;

    public string GetString(string key, string @default) =>
        Strings.TryGetValue(key, out var v) ? v : @default;

    public void SetNumber(string key, double value)
    {
        Numbers[key] = value;
        OnPropertyChanged(nameof(Numbers));
    }

    public void SetString(string key, string value)
    {
        Strings[key] = value;
        OnPropertyChanged(nameof(Strings));
    }

    /// <summary>Returns the parameter's value at the given clip-relative time,
    /// linearly interpolating between automation keyframes. Falls through to the
    /// static <see cref="GetNumber"/> when no keyframes exist for this key.</summary>
    public double GetAutomatedNumber(string key, double timeRel, double @default)
    {
        if (!Keyframes.TryGetValue(key, out var kfs) || kfs.Count == 0)
            return GetNumber(key, @default);
        if (kfs.Count == 1) return kfs[0].Value;

        EffectKeyframe? prev = null;
        EffectKeyframe? next = null;
        foreach (var k in kfs)
        {
            if (k.TimeRel <= timeRel && (prev is null || k.TimeRel > prev.TimeRel)) prev = k;
            if (k.TimeRel >= timeRel && (next is null || k.TimeRel < next.TimeRel)) next = k;
        }
        if (prev is null && next is not null) return next.Value;
        if (next is null && prev is not null) return prev.Value;
        if (prev is null || next is null) return @default;
        if (ReferenceEquals(prev, next)) return prev.Value;
        double span = next.TimeRel - prev.TimeRel;
        if (span <= 0) return prev.Value;
        double t = (timeRel - prev.TimeRel) / span;
        return prev.Value + (next.Value - prev.Value) * t;
    }

    public bool IsAnimated(string key) =>
        Keyframes.TryGetValue(key, out var kfs) && kfs.Count > 0;

    /// <summary>Initialize a simple start→end animation for a parameter. The
    /// inspector can edit each keyframe's value afterwards.</summary>
    public void StartAnimation(string key, double startValue, double endValue)
    {
        Keyframes[key] = new ObservableCollection<EffectKeyframe>
        {
            new() { TimeRel = 0, Value = startValue },
            new() { TimeRel = 1, Value = endValue },
        };
        OnPropertyChanged(nameof(Keyframes));
    }

    public void StopAnimation(string key)
    {
        if (Keyframes.Remove(key))
            OnPropertyChanged(nameof(Keyframes));
    }
}

public partial class EffectKeyframe : ObservableObject
{
    [ObservableProperty] private double _timeRel; // 0..1 within clip duration
    [ObservableProperty] private double _value;
}
