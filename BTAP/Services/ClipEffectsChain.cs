using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;
using Windows.UI;
using Microsoft.UI;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Builds a Win2D effect-chain for a TimelineClip's color grading + named effects.
/// Used by both the live preview (VideoCompositorControl) and the export pipeline
/// (ExportFrameCompositor) so what the user sees in the viewport is what gets baked
/// into the export.
///
/// Each named effect reads its parameters from <see cref="ClipEffect.Numbers"/> and
/// <see cref="ClipEffect.Strings"/>, falling back to the legacy
/// <see cref="ClipEffect.Intensity"/> slider when the per-effect parameter isn't set
/// (so saved projects from the single-slider era keep producing the same look).
/// </summary>
public static class ClipEffectsChain
{
    /// <summary>Available video effects, shown in the inspector list and in the
    /// media-library Effects panel. Order here is the order they appear in the UI.</summary>
    public static readonly string[] AvailableVideoEffects =
    {
        "Gaussian Blur", "Sharpen", "Vignette", "Pixelate", "Glow", "Drop Shadow",
        "Chroma Key", "Invert", "Grayscale", "Sepia", "Edge Detect", "Posterize",
        "Emboss", "Hue Rotate",
    };

    /// <summary>Available audio effects, shown in the inspector when an audio clip
    /// is selected. Their parameters are stored on ClipEffect.Numbers but the live
    /// audio pipeline (Tier-2 AudioGraph work) isn't wired yet — same situation as
    /// the per-clip EQ/Pan/Fade properties, which are also saved but not applied
    /// to playback today.</summary>
    public static readonly string[] AvailableAudioEffects =
    {
        "Distortion", "Reverb", "Delay", "Compressor",
        "Low Pass", "High Pass", "Chorus", "Tremolo", "Flanger",
    };

    /// <summary>True for effect names that target audio (vs video) — used by the
    /// inspector to pick the right effect list for a given clip kind.</summary>
    public static bool IsAudioEffect(string name) =>
        Array.IndexOf(AvailableAudioEffects, name) >= 0;

    /// <summary>Schema for an effect parameter, used by the inspector to lay out
    /// sliders/color swatches and by the chain to read defaults consistently.</summary>
    public readonly record struct NumberParam(string Key, string Label, double Min, double Max, double Default);
    public readonly record struct StringParam(string Key, string Label, string Default);

    /// <summary>Numeric parameters for an effect. Order is the order they appear in the inspector.</summary>
    public static IReadOnlyList<NumberParam> NumberParams(string name) => name switch
    {
        "Gaussian Blur" => new NumberParam[] { new("Amount", "Amount", 0, 50, 20) },
        "Sharpen"       => new NumberParam[] { new("Amount", "Amount", 0, 10, 4), new("Threshold", "Threshold", 0, 1, 0) },
        "Vignette"      => new NumberParam[] { new("Amount", "Amount", 0, 1, 0.8), new("Curve", "Curve", 0, 1, 0.6) },
        "Pixelate"      => new NumberParam[] { new("BlockSize", "Block size", 1, 40, 12) },
        "Glow"          => new NumberParam[] { new("Amount", "Amount", 0, 1, 0.6), new("Radius", "Radius", 1, 32, 16) },
        "Drop Shadow"   => new NumberParam[]
        {
            new("Blur", "Blur", 0, 30, 12),
            new("OffsetX", "Offset X", -30, 30, 8),
            new("OffsetY", "Offset Y", -30, 30, 8),
            new("Alpha", "Opacity", 0, 1, 0.6),
        },
        "Chroma Key"    => new NumberParam[] { new("Tolerance", "Tolerance", 0, 1, 0.2) },
        "Invert"        => new NumberParam[] { new("Amount", "Amount", 0, 1, 1) },
        "Grayscale"     => new NumberParam[] { new("Amount", "Amount", 0, 1, 1) },
        "Sepia"         => new NumberParam[] { new("Intensity", "Intensity", 0, 1, 0.8) },
        "Edge Detect"   => new NumberParam[] { new("Amount", "Amount", 0, 1, 0.7) },
        "Posterize"     => new NumberParam[] { new("Levels", "Levels", 2, 8, 4) },
        "Emboss"        => new NumberParam[] { new("Amount", "Amount", 0, 10, 5), new("Angle", "Angle", 0, 360, 45) },
        "Hue Rotate"    => new NumberParam[] { new("Angle", "Angle", 0, 360, 180) },

        // ── Audio effect parameter schemas ──────────────────────────────────
        "Distortion"    => new NumberParam[]
        {
            new("Drive", "Drive",   0, 100, 30),
            new("Tone",  "Tone",    0, 100, 50),
            new("Mix",   "Mix",     0, 100, 100),
        },
        "Reverb"        => new NumberParam[]
        {
            new("Mix",       "Wet mix",       0, 100, 25),
            new("Decay",     "Decay (s)",     0.1, 10, 2),
            new("Predelay",  "Pre-delay (ms)", 0, 200, 20),
        },
        "Delay"         => new NumberParam[]
        {
            new("Time",     "Time (ms)",     1, 2000, 350),
            new("Feedback", "Feedback",      0, 95,   40),
            new("Mix",      "Mix",           0, 100,  35),
        },
        "Compressor"    => new NumberParam[]
        {
            new("Threshold", "Threshold (dB)", -60, 0,  -18),
            new("Ratio",     "Ratio",          1,   20, 4),
            new("Attack",    "Attack (ms)",    1,   200, 10),
            new("Release",   "Release (ms)",   10,  1000, 120),
            new("Makeup",    "Makeup (dB)",    0,   24,   3),
        },
        "Low Pass"      => new NumberParam[]
        {
            new("Cutoff",    "Cutoff (Hz)",   20, 20000, 8000),
            new("Resonance", "Resonance",     0,   100,  10),
        },
        "High Pass"     => new NumberParam[]
        {
            new("Cutoff",    "Cutoff (Hz)",   20, 20000, 200),
            new("Resonance", "Resonance",     0,   100,  10),
        },
        "Chorus"        => new NumberParam[]
        {
            new("Rate",  "Rate (Hz)", 0.1, 10,   1.5),
            new("Depth", "Depth",     0,   100,  35),
            new("Mix",   "Mix",       0,   100,  40),
        },
        "Tremolo"       => new NumberParam[]
        {
            new("Rate",  "Rate (Hz)", 0.1, 20,   5),
            new("Depth", "Depth",     0,   100,  60),
        },
        "Flanger"       => new NumberParam[]
        {
            new("Rate",     "Rate (Hz)", 0.05, 5,   0.5),
            new("Depth",    "Depth",     0,    100, 50),
            new("Feedback", "Feedback",  0,    95,  30),
        },

        _               => Array.Empty<NumberParam>(),
    };

    /// <summary>String parameters (currently only colors, stored as #AARRGGBB hex).</summary>
    public static IReadOnlyList<StringParam> StringParams(string name) => name switch
    {
        "Chroma Key" => new StringParam[] { new("Color", "Key color", "#FF00FF00") },
        "Vignette"   => new StringParam[] { new("Color", "Color", "#FF000000") },
        _            => Array.Empty<StringParam>(),
    };

    /// <summary>Build a Win2D effect chain that applies the clip's color-grading
    /// sliders and per-clip named effects to <paramref name="source"/>. Effects are
    /// appended to <paramref name="disposables"/> so the caller can release them
    /// after the draw completes (in LIFO order). <paramref name="clipTimeRel"/> is
    /// the playhead position within the clip as a 0..1 fraction of clip duration,
    /// used to interpolate parameter automation keyframes.</summary>
    public static IGraphicsEffectSource Build(IGraphicsEffectSource source,
                                              TimelineClip clip,
                                              double clipTimeRel,
                                              List<IDisposable> disposables)
    {
        IGraphicsEffectSource src = source;

        if (Math.Abs(clip.Exposure) > 0.0001)
        {
            var e = new ExposureEffect { Source = src, Exposure = Clamp((float)clip.Exposure, -2f, 2f) };
            disposables.Add(e); src = e;
        }
        if (Math.Abs(clip.Contrast) > 0.0001)
        {
            var e = new ContrastEffect { Source = src, Contrast = Clamp((float)clip.Contrast / 100f, -1f, 1f) };
            disposables.Add(e); src = e;
        }
        if (Math.Abs(clip.Saturation) > 0.0001)
        {
            var e = new SaturationEffect { Source = src, Saturation = Clamp(1f + (float)clip.Saturation / 100f, 0f, 2f) };
            disposables.Add(e); src = e;
        }
        if (Math.Abs(clip.Temperature) > 0.0001 || Math.Abs(clip.Tint) > 0.0001)
        {
            var e = new TemperatureAndTintEffect
            {
                Source = src,
                Temperature = Clamp((float)clip.Temperature / 100f, -1f, 1f),
                Tint        = Clamp((float)clip.Tint        / 100f, -1f, 1f),
            };
            disposables.Add(e); src = e;
        }

        // Lift + Gain combined into a single LinearTransferEffect (output = source * slope + offset).
        // Lift slider (-50..+50) → offset in [-0.5, +0.5]; Gain slider (-50..+50) → slope in [0.5, 1.5].
        if (Math.Abs(clip.Lift) > 0.0001 || Math.Abs(clip.ColorGain) > 0.0001)
        {
            float offset = Clamp((float)clip.Lift      / 100f, -0.5f, 0.5f);
            float slope  = Clamp(1f + (float)clip.ColorGain / 100f, 0.5f, 1.5f);
            var e = new LinearTransferEffect
            {
                Source = src,
                RedSlope    = slope, RedOffset    = offset,
                GreenSlope  = slope, GreenOffset  = offset,
                BlueSlope   = slope, BlueOffset   = offset,
            };
            disposables.Add(e); src = e;
        }

        // Gamma slider (-50..+50): exponent = 2^(-gamma/50). 0 → 1.0 (no change),
        // +50 → 0.5 (brighter midtones), -50 → 2.0 (darker midtones).
        if (Math.Abs(clip.Gamma) > 0.0001)
        {
            float exponent = Clamp((float)Math.Pow(2.0, -clip.Gamma / 50.0), 0.1f, 10f);
            var e = new GammaTransferEffect
            {
                Source = src,
                RedAmplitude   = 1f, RedExponent   = exponent, RedOffset   = 0f,
                GreenAmplitude = 1f, GreenExponent = exponent, GreenOffset = 0f,
                BlueAmplitude  = 1f, BlueExponent  = exponent, BlueOffset  = 0f,
            };
            disposables.Add(e); src = e;
        }

        // Color overlay: alpha-blend the source with a flat color. The overlay's
        // alpha channel (0..255) drives how strongly the tint replaces the image
        // (#00000000 = no-op, #FFRRGGBB = fully replaced). Implemented as a
        // ColorMatrixEffect so the result keeps the source's bounds (a
        // ColorSourceEffect would be infinite and spill outside transformed clips).
        //   R' = R*(1-a) + (R_overlay/255)*a   (and same for G, B; A passed through)
        var overlay = ParseColor(clip.ColorOverlay, Colors.Transparent);
        if (overlay.A > 0)
        {
            float a = overlay.A / 255f;
            float inv = 1f - a;
            float r = (overlay.R / 255f) * a;
            float g = (overlay.G / 255f) * a;
            float b = (overlay.B / 255f) * a;
            var e = new ColorMatrixEffect
            {
                Source = src,
                ColorMatrix = new Matrix5x4
                {
                    M11 = inv, M12 = 0,   M13 = 0,   M14 = 0,
                    M21 = 0,   M22 = inv, M23 = 0,   M24 = 0,
                    M31 = 0,   M32 = 0,   M33 = inv, M34 = 0,
                    M41 = 0,   M42 = 0,   M43 = 0,   M44 = 1,
                    M51 = r,   M52 = g,   M53 = b,   M54 = 0,
                },
            };
            disposables.Add(e); src = e;
        }

        foreach (var fx in clip.Effects)
        {
            if (!fx.Enabled) continue;
            var next = AppendNamedEffect(fx, src, clipTimeRel, disposables);
            if (next is not null) src = next;
        }

        return src;
    }

    private static IGraphicsEffectSource? AppendNamedEffect(ClipEffect fx,
                                                            IGraphicsEffectSource src,
                                                            double timeRel,
                                                            List<IDisposable> disposables)
    {
        // Legacy fallback: when Numbers is empty, derive the primary parameter from
        // the old Intensity slider so saves from before per-effect params look the
        // same. New UI writes Numbers directly and intensity is no longer touched.
        double legacyT = Math.Clamp(fx.Intensity, 0.0, 1.0);
        // Local helper - animated value at the current playhead, falling back to
        // the static Numbers value when no keyframes are set for that key.
        double N(string key, double @default) => fx.GetAutomatedNumber(key, timeRel, @default);

        switch (fx.Name)
        {
            case "Gaussian Blur":
            {
                float amount = (float)N("Amount", legacyT * 50.0);
                if (amount < 0.01f) return null;
                var e = new GaussianBlurEffect
                {
                    Source = src,
                    BlurAmount = Math.Clamp(amount, 0, 250),
                    BorderMode = EffectBorderMode.Hard,
                };
                disposables.Add(e);
                return e;
            }

            case "Sharpen":
            {
                float amount = (float)N("Amount", legacyT * 10.0);
                float threshold = (float)N("Threshold", 0.0);
                if (amount < 0.01f) return null;
                var e = new SharpenEffect
                {
                    Source = src,
                    Amount = Math.Clamp(amount, 0f, 10f),
                    Threshold = Math.Clamp(threshold, 0f, 1f),
                };
                disposables.Add(e);
                return e;
            }

            case "Vignette":
            {
                float amount = (float)N("Amount", legacyT);
                float curve  = (float)N("Curve", 0.6);
                if (amount < 0.001f) return null;
                var e = new VignetteEffect
                {
                    Source = src,
                    Amount = Math.Clamp(amount, 0f, 1f),
                    Curve  = Math.Clamp(curve,  0f, 1f),
                    Color  = ParseColor(fx.GetString("Color", "#FF000000"), Colors.Black),
                };
                disposables.Add(e);
                return e;
            }

            case "Pixelate":
            {
                float block = (float)N("BlockSize", 1.0 + legacyT * 39.0);
                if (block < 1.5f) return null;
                float invBlock = 1f / block;
                var down = new ScaleEffect
                {
                    Source = src,
                    Scale = new Vector2(invBlock, invBlock),
                    InterpolationMode = CanvasImageInterpolation.NearestNeighbor,
                };
                disposables.Add(down);
                var up = new ScaleEffect
                {
                    Source = down,
                    Scale = new Vector2(block, block),
                    InterpolationMode = CanvasImageInterpolation.NearestNeighbor,
                };
                disposables.Add(up);
                return up;
            }

            case "Glow":
            {
                float amount = (float)N("Amount", legacyT);
                float radius = (float)N("Radius", 8.0 + legacyT * 24.0);
                if (amount < 0.001f) return null;
                var brighten = new LinearTransferEffect
                {
                    Source = src,
                    RedSlope = 1.2f, RedOffset = -0.1f,
                    GreenSlope = 1.2f, GreenOffset = -0.1f,
                    BlueSlope = 1.2f, BlueOffset = -0.1f,
                };
                disposables.Add(brighten);
                var blur = new GaussianBlurEffect
                {
                    Source = brighten,
                    BlurAmount = Math.Clamp(radius, 0f, 250f),
                    BorderMode = EffectBorderMode.Hard,
                };
                disposables.Add(blur);
                var add = new CompositeEffect { Mode = CanvasComposite.Add };
                add.Sources.Add(src);
                add.Sources.Add(blur);
                disposables.Add(add);
                var cross = new CrossFadeEffect { Source1 = src, Source2 = add, CrossFade = Math.Clamp(amount, 0f, 1f) };
                disposables.Add(cross);
                return cross;
            }

            case "Drop Shadow":
            {
                float blurAmount = (float)N("Blur", 4.0 + legacyT * 16.0);
                float ox         = (float)N("OffsetX", legacyT * 12.0);
                float oy         = (float)N("OffsetY", legacyT * 12.0);
                float alpha      = (float)N("Alpha", 0.7 * legacyT);
                if (alpha < 0.001f) return null;
                var shadow = new ShadowEffect
                {
                    Source = src,
                    BlurAmount = Math.Clamp(blurAmount, 0f, 250f),
                    ShadowColor = Color.FromArgb((byte)Math.Clamp(alpha * 255f, 0, 255), 0, 0, 0),
                };
                disposables.Add(shadow);
                var offset = new Transform2DEffect
                {
                    Source = shadow,
                    TransformMatrix = Matrix3x2.CreateTranslation(ox, oy),
                };
                disposables.Add(offset);
                var combined = new CompositeEffect { Mode = CanvasComposite.SourceOver };
                combined.Sources.Add(offset);
                combined.Sources.Add(src);
                disposables.Add(combined);
                return combined;
            }

            case "Chroma Key":
            {
                float tolerance = (float)N("Tolerance", 0.05 + 0.4 * legacyT);
                if (tolerance < 0.001f) return null;
                var key = new ChromaKeyEffect
                {
                    Source = src,
                    Color = ParseColor(fx.GetString("Color", "#FF00FF00"), Color.FromArgb(255, 0, 255, 0)),
                    Tolerance = Math.Clamp(tolerance, 0f, 1f),
                    Feather = true,
                };
                disposables.Add(key);
                return key;
            }

            case "Invert":
            {
                float amount = (float)N("Amount", legacyT);
                if (amount < 0.001f) return null;
                var inv = new InvertEffect { Source = src };
                disposables.Add(inv);
                var cross = new CrossFadeEffect { Source1 = src, Source2 = inv, CrossFade = Math.Clamp(amount, 0f, 1f) };
                disposables.Add(cross);
                return cross;
            }

            case "Grayscale":
            {
                float amount = (float)N("Amount", legacyT);
                if (amount < 0.001f) return null;
                var gs = new GrayscaleEffect { Source = src };
                disposables.Add(gs);
                var cross = new CrossFadeEffect { Source1 = src, Source2 = gs, CrossFade = Math.Clamp(amount, 0f, 1f) };
                disposables.Add(cross);
                return cross;
            }

            case "Sepia":
            {
                float intensity = (float)N("Intensity", legacyT);
                if (intensity < 0.001f) return null;
                var sep = new SepiaEffect
                {
                    Source = src,
                    Intensity = Math.Clamp(intensity, 0f, 1f),
                };
                disposables.Add(sep);
                return sep;
            }

            case "Edge Detect":
            {
                float amount = (float)N("Amount", legacyT);
                if (amount < 0.001f) return null;
                var ed = new EdgeDetectionEffect
                {
                    Source = src,
                    Amount = Math.Clamp(0.3f + 0.7f * amount, 0f, 1f),
                    BlurAmount = 0f,
                    OverlayEdges = false,
                };
                disposables.Add(ed);
                var cross = new CrossFadeEffect { Source1 = src, Source2 = ed, CrossFade = Math.Clamp(amount, 0f, 1f) };
                disposables.Add(cross);
                return cross;
            }

            case "Posterize":
            {
                int levels = (int)Math.Round(N("Levels", 8.0 - 6.0 * legacyT));
                levels = Math.Clamp(levels, 2, 8);
                var table = new float[levels];
                for (int i = 0; i < levels; i++) table[i] = (float)i / (levels - 1);
                var e = new DiscreteTransferEffect
                {
                    Source = src,
                    RedTable = table,
                    GreenTable = table,
                    BlueTable = table,
                };
                disposables.Add(e);
                return e;
            }

            case "Emboss":
            {
                float amount = (float)N("Amount", 1.0 + legacyT * 9.0);
                float angleDeg = (float)N("Angle", 45.0);
                if (amount < 0.01f) return null;
                double rad = angleDeg * Math.PI / 180.0;
                float cos = (float)Math.Cos(rad);
                float sin = (float)Math.Sin(rad);
                float a = Math.Clamp(amount, 0f, 10f);
                // Directional emboss kernel â€” gradient along (cos, sin) lifts edges in
                // that direction while keeping the surrounding mid-grey baseline.
                var conv = new ConvolveMatrixEffect
                {
                    Source = src,
                    KernelMatrix = new float[]
                    {
                        -a*cos - a*sin, -a*sin,        a*cos - a*sin,
                        -a*cos,           1f,           a*cos,
                        -a*cos + a*sin,   a*sin,         a*cos + a*sin,
                    },
                    KernelWidth = 3,
                    KernelHeight = 3,
                };
                disposables.Add(conv);
                return conv;
            }

            case "Hue Rotate":
            {
                float angleDeg = (float)N("Angle", legacyT * 360.0);
                if (Math.Abs(angleDeg) < 0.1f) return null;
                var h = new HueRotationEffect
                {
                    Source = src,
                    Angle = (float)(angleDeg * Math.PI / 180.0),
                };
                disposables.Add(h);
                return h;
            }

            default:
                return null;
        }
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return fallback;
        if (!byte.TryParse(s[..2],            System.Globalization.NumberStyles.HexNumber, null, out var a)) return fallback;
        if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)) return fallback;
        if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)) return fallback;
        if (!byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return fallback;
        return Color.FromArgb(a, r, g, b);
    }

    private static float Clamp(float v, float min, float max) =>
        v < min ? min : (v > max ? max : v);
}

