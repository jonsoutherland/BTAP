using Windows.Media.Audio;
using Windows.Media.Effects;
using Windows.Media.Render;
using Windows.Storage;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Owns the project-wide <see cref="AudioGraph"/> that plays clip audio with effects
/// applied. Replaces the MediaPlayer-direct audio path: VideoCompositorControl keeps
/// using MediaPlayer for video frame decode but mutes its audio output, and instead
/// adds/removes/seeks per-clip <see cref="AudioFileInputNode"/>s through this engine.
///
/// Effect mapping (see <see cref="ApplyEffectsToNode"/>):
///   • Per-clip EQ Low/Mid/High properties  → <see cref="EqualizerEffectDefinition"/> 3-band
///   • "Reverb"     → <see cref="ReverbEffectDefinition"/>
///   • "Delay"      → <see cref="EchoEffectDefinition"/>
///   • "Compressor" → <see cref="LimiterEffectDefinition"/> (limiter is approximate but close)
///   • "Low Pass"   → 4-band Equalizer with sharp cut above cutoff
///   • "High Pass"  → 4-band Equalizer with sharp cut below cutoff
///   • "Distortion" / "Chorus" / "Tremolo" / "Flanger" — not wired in this pass.
///     Custom DSP for those requires AudioFrameInputNode pumping which is a follow-up.
///
/// Fails open: if AudioGraph can't init on this hardware, the engine returns
/// <c>false</c> from <see cref="EnsureInitializedAsync"/> and the compositor knows to
/// leave MediaPlayer audio unmuted so the user still gets sound (without effects).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private AudioGraph? _graph;
    private AudioDeviceOutputNode? _output;
    private readonly Dictionary<string, ClipNode> _clipNodes = new();
    private readonly object _lock = new();
    private bool _initFailed;
    private bool _isPlaying;
    private TimeSpan _lastSeekPlayhead;

    /// <summary>True after a successful EnsureInitializedAsync call. Callers can
    /// check this synchronously to decide whether to mute MediaPlayer audio.</summary>
    public bool IsReady => _graph is not null && !_initFailed;

    private sealed class ClipNode
    {
        public AudioFileInputNode Input = null!;
        public TimelineClip Clip = null!;
        public bool Started; // tracks Start()/Stop() so Pause/Play match the graph state
    }

    public async Task<bool> EnsureInitializedAsync()
    {
        if (_graph is not null) return true;
        if (_initFailed) return false;
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                PlaybackLogger.Log($"AudioEngine graph init FAILED status={graphResult.Status}");
                _initFailed = true;
                return false;
            }
            _graph = graphResult.Graph;

            var outResult = await _graph.CreateDeviceOutputNodeAsync();
            if (outResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                PlaybackLogger.Log($"AudioEngine output init FAILED status={outResult.Status}");
                try { _graph.Dispose(); } catch { }
                _graph = null;
                _initFailed = true;
                return false;
            }
            _output = outResult.DeviceOutputNode;
            _graph.Start();
            PlaybackLogger.Log("AudioEngine ready");
            return true;
        }
        catch (Exception ex)
        {
            PlaybackLogger.Log($"AudioEngine init THREW {ex.GetType().Name}: {ex.Message}");
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Spin up an AudioFileInputNode for a clip and connect it to the master
    /// output. Idempotent — if a node already exists for the clip's Id it's left alone.
    /// Sets initial gain and effect chain. The node starts in Stop state; the caller's
    /// subsequent <see cref="SeekClip"/> and <see cref="Play"/> drive playback.</summary>
    public async Task<bool> AddClipAsync(TimelineClip clip, string filePath)
    {
        if (!await EnsureInitializedAsync() || _graph is null || _output is null) return false;
        lock (_lock) if (_clipNodes.ContainsKey(clip.Id)) return true;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var result = await _graph.CreateFileInputNodeAsync(file);
            if (result.Status != AudioFileNodeCreationStatus.Success)
            {
                PlaybackLogger.Log($"AudioEngine CreateFileInputNode FAILED clip={clip.Id} status={result.Status}");
                return false;
            }
            var input = result.FileInputNode;
            input.AddOutgoingConnection(_output);
            input.OutgoingGain = Math.Clamp(clip.Volume, 0, 2);
            input.LoopCount = 0;
            input.Stop();

            var node = new ClipNode { Input = input, Clip = clip };
            ApplyEffectsToNode(node);

            lock (_lock) _clipNodes[clip.Id] = node;

            // Seek to where the playhead is right now so the clip enters playback at
            // the right offset (otherwise it would start at t=0 of the source file
            // and play out of sync with video).
            SeekClipInternal(node, _lastSeekPlayhead);
            if (_isPlaying)
            {
                try { input.Start(); node.Started = true; }
                catch (Exception ex) { PlaybackLogger.Log($"AudioEngine Start after-add THREW clip={clip.Id} {ex.Message}"); }
            }
            PlaybackLogger.Log($"AudioEngine ADD clip={clip.Id} effects={input.EffectDefinitions.Count}");
            return true;
        }
        catch (Exception ex)
        {
            PlaybackLogger.Log($"AudioEngine AddClip THREW clip={clip.Id} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void RemoveClip(string clipId)
    {
        ClipNode? node;
        lock (_lock)
        {
            if (!_clipNodes.TryGetValue(clipId, out node)) return;
            _clipNodes.Remove(clipId);
        }
        try { node.Input.Stop(); } catch { }
        try { node.Input.Dispose(); } catch { }
        PlaybackLogger.Log($"AudioEngine REMOVE clip={clipId}");
    }

    public void SeekAll(TimeSpan playhead)
    {
        _lastSeekPlayhead = playhead;
        ClipNode[] nodes;
        lock (_lock) nodes = _clipNodes.Values.ToArray();
        foreach (var n in nodes) SeekClipInternal(n, playhead);
    }

    public void SeekClip(string clipId, TimeSpan playhead)
    {
        ClipNode? node;
        lock (_lock) _clipNodes.TryGetValue(clipId, out node);
        if (node is null) return;
        SeekClipInternal(node, playhead);
    }

    private void SeekClipInternal(ClipNode node, TimeSpan playhead)
    {
        var offset = node.Clip.SourceStart + (playhead - node.Clip.TimelineStart);
        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        try { node.Input.Seek(offset); }
        catch (Exception ex) { PlaybackLogger.Log($"AudioEngine Seek THREW clip={node.Clip.Id} off={offset.TotalSeconds:F3}s {ex.Message}"); }
    }

    public void SetClipGain(string clipId, double gain)
    {
        ClipNode? node;
        lock (_lock) _clipNodes.TryGetValue(clipId, out node);
        if (node is null) return;
        try { node.Input.OutgoingGain = Math.Clamp(gain, 0, 2); } catch { }
    }

    public void UpdateClipEffects(string clipId)
    {
        ClipNode? node;
        lock (_lock) _clipNodes.TryGetValue(clipId, out node);
        if (node is null) return;
        ApplyEffectsToNode(node);
    }

    /// <summary>Walk every active clip node and re-apply effects. Used when the
    /// user changes an inspector slider — we don't know which clip's effects
    /// were edited, so we re-run the whole pool. The cost is bounded by the
    /// active layer count (usually 1-3) so this is cheap.</summary>
    public void UpdateAllEffects()
    {
        ClipNode[] nodes;
        lock (_lock) nodes = _clipNodes.Values.ToArray();
        foreach (var n in nodes) ApplyEffectsToNode(n);
    }

    public void Play()
    {
        _isPlaying = true;
        ClipNode[] nodes;
        lock (_lock) nodes = _clipNodes.Values.ToArray();
        foreach (var n in nodes)
        {
            try { n.Input.Start(); n.Started = true; }
            catch (Exception ex) { PlaybackLogger.Log($"AudioEngine Play THREW clip={n.Clip.Id} {ex.Message}"); }
        }
    }

    public void Pause()
    {
        _isPlaying = false;
        ClipNode[] nodes;
        lock (_lock) nodes = _clipNodes.Values.ToArray();
        foreach (var n in nodes)
        {
            try { n.Input.Stop(); n.Started = false; }
            catch (Exception ex) { PlaybackLogger.Log($"AudioEngine Pause THREW clip={n.Clip.Id} {ex.Message}"); }
        }
    }

    private void ApplyEffectsToNode(ClipNode node)
    {
        if (_graph is null) return;
        try { node.Input.EffectDefinitions.Clear(); } catch { }

        // ── 1. Built-in EQ from the clip's EqLow/Mid/High sliders (always applied) ──
        var clip = node.Clip;
        bool anyEq = Math.Abs(clip.EqLow) > 0.01 ||
                     Math.Abs(clip.EqMid) > 0.01 ||
                     Math.Abs(clip.EqHigh) > 0.01;
        if (anyEq)
        {
            try
            {
                // EqualizerEffectDefinition exposes 4 default bands that you mutate
                // in place — the Bands list is read-only on .NET projections, so
                // construct-then-assign isn't possible.
                var eq = new EqualizerEffectDefinition(_graph);
                ConfigureBand(eq.Bands[0], 120,  2.0f, (float)clip.EqLow);
                ConfigureBand(eq.Bands[1], 1000, 1.0f, (float)clip.EqMid);
                ConfigureBand(eq.Bands[2], 6000, 1.0f, (float)clip.EqHigh);
                ConfigureBand(eq.Bands[3], 12000, 1.0f, 0f);
                node.Input.EffectDefinitions.Add(eq);
            }
            catch (Exception ex) { PlaybackLogger.Log($"AudioEngine EQ-build THREW clip={clip.Id} {ex.Message}"); }
        }

        // ── 2. Per-clip named effects (Reverb, Delay, Compressor, LP, HP) ────────
        foreach (var fx in clip.Effects)
        {
            if (!fx.Enabled) continue;
            try
            {
                var def = BuildEffectDefinition(fx);
                if (def is not null) node.Input.EffectDefinitions.Add(def);
            }
            catch (Exception ex)
            {
                PlaybackLogger.Log($"AudioEngine effect-build THREW clip={clip.Id} effect={fx.Name} {ex.Message}");
            }
        }
    }

    private static void ConfigureBand(EqualizerBand band, float freq, float bandwidth, float gain)
    {
        band.FrequencyCenter = freq;
        band.Bandwidth       = bandwidth;
        band.Gain            = gain;
    }

    private IAudioEffectDefinition? BuildEffectDefinition(ClipEffect fx)
    {
        if (_graph is null) return null;
        double N(string k, double d) => fx.GetNumber(k, d);

        switch (fx.Name)
        {
            case "Reverb":
            {
                double mix      = N("Mix", 25);          // 0..100
                double decay    = N("Decay", 2);          // 0.1..10 sec
                double predelay = N("Predelay", 20);      // 0..200 ms
                var def = new ReverbEffectDefinition(_graph)
                {
                    WetDryMix = (float)Math.Clamp(mix, 0, 100),
                    DecayTime = Math.Clamp(decay, 0.001, 3.0),
                    ReverbDelay = (byte)Math.Clamp(predelay, 0, 100),
                };
                return def;
            }

            case "Delay":
            {
                double time     = N("Time", 350);    // ms (1..2000)
                double feedback = N("Feedback", 40); // 0..95
                double mix      = N("Mix", 35);      // 0..100
                var def = new EchoEffectDefinition(_graph)
                {
                    Delay     = Math.Clamp(time, 1, 2000),
                    Feedback  = Math.Clamp(feedback / 100.0, 0, 0.95),
                    WetDryMix = Math.Clamp(mix      / 100.0, 0, 1),
                };
                return def;
            }

            case "Compressor":
            {
                // LimiterEffectDefinition is a peak limiter rather than a
                // ratio/threshold compressor; we approximate by mapping the user's
                // Threshold/Release into the limiter's Loudness/Release.
                double release = N("Release", 120);  // 10..1000 ms
                double thresh  = N("Threshold", -18); // -60..0 dB → louder threshold = harder limit
                // Loudness exposed as uint 1..1800 in the API but as a byte cap in
                // some WindowsAppSDK projections — clamp to 255 to be safe.
                uint loudness = (uint)Math.Clamp(1800.0 - ((thresh + 60.0) / 60.0) * 1799.0, 1, 1800);
                var def = new LimiterEffectDefinition(_graph)
                {
                    Loudness = (uint)Math.Min(loudness, 255),
                    Release  = (uint)Math.Clamp(release, 1, 1000),
                };
                return def;
            }

            case "Low Pass":
            {
                double cutoff = N("Cutoff", 8000); // Hz
                double res    = N("Resonance", 10); // 0..100 (cut steepness)
                float kill = -(float)(8 + res / 100.0 * 16.0); // dB
                var def = new EqualizerEffectDefinition(_graph);
                ConfigureBand(def.Bands[0], (float)Math.Min(cutoff * 0.5, 20000), 2.0f, 0);
                ConfigureBand(def.Bands[1], (float)Math.Min(cutoff * 1.5, 20000), 2.0f, kill);
                ConfigureBand(def.Bands[2], (float)Math.Min(cutoff * 3.0, 20000), 2.0f, kill * 1.5f);
                ConfigureBand(def.Bands[3], (float)Math.Min(cutoff * 6.0, 20000), 2.0f, kill * 2.0f);
                return def;
            }

            case "High Pass":
            {
                double cutoff = N("Cutoff", 200); // Hz
                double res    = N("Resonance", 10);
                float kill = -(float)(8 + res / 100.0 * 16.0);
                var def = new EqualizerEffectDefinition(_graph);
                ConfigureBand(def.Bands[0], (float)Math.Max(cutoff / 6.0, 20), 2.0f, kill * 2.0f);
                ConfigureBand(def.Bands[1], (float)Math.Max(cutoff / 3.0, 20), 2.0f, kill * 1.5f);
                ConfigureBand(def.Bands[2], (float)Math.Max(cutoff / 1.5, 20), 2.0f, kill);
                ConfigureBand(def.Bands[3], (float)Math.Max(cutoff * 2.0, 20), 2.0f, 0);
                return def;
            }

            // Effects not yet implemented in the live preview. Inspector renders a
            // "(preview not active)" badge for these so the user knows to expect no
            // sound change from these sliders for now.
            case "Distortion":
            case "Chorus":
            case "Tremolo":
            case "Flanger":
                return null;
        }
        return null;
    }

    /// <summary>Names of audio effects that currently have no live-preview implementation.
    /// The inspector reads this to badge those rows so the UI doesn't lie about wiring.</summary>
    public static bool IsPreviewActive(string effectName) => effectName switch
    {
        "Reverb" or "Delay" or "Compressor" or "Low Pass" or "High Pass" => true,
        _ => false,
    };

    public void Dispose()
    {
        ClipNode[] nodes;
        lock (_lock) { nodes = _clipNodes.Values.ToArray(); _clipNodes.Clear(); }
        foreach (var n in nodes) try { n.Input.Dispose(); } catch { }
        try { _graph?.Dispose(); } catch { }
        _graph = null;
        _output = null;
        _initFailed = false;
    }
}
