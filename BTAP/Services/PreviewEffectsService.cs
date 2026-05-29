using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics.Effects;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>
/// Manages a Win2D composition-effect chain that runs on the backdrop of an overlay
/// element placed on top of the MediaPlayerElement.  Color-grading slider values from
/// the active TimelineClip drive animatable properties on the resulting brush.
/// </summary>
public sealed class PreviewEffectsService
{
    private CompositionEffectBrush? _brush;
    private SpriteVisual?           _sprite;
    private bool                    _attached;
    private FrameworkElement?       _host;

    public bool IsAttached => _attached;

    public void Attach(FrameworkElement host)
    {
        if (_attached) return;
        _host = host;

        try
        {
            var visual     = ElementCompositionPreview.GetElementVisual(host);
            var compositor = visual.Compositor;
            var backdrop   = compositor.CreateBackdropBrush();

            // Build the effect chain. Each effect's Source is the previous stage's output;
            // the final stage's Source is the named parameter we'll bind backdrop to.
            IGraphicsEffectSource src = new CompositionEffectSourceParameter("backdrop");

            var exposure   = new ExposureEffect          { Name = "Exposure",   Exposure   = 0f,         Source = src       };
            var contrast   = new ContrastEffect          { Name = "Contrast",   Contrast   = 0f,         Source = exposure  };
            var saturation = new SaturationEffect        { Name = "Saturation", Saturation = 1f,         Source = contrast  };
            var wb         = new TemperatureAndTintEffect{ Name = "WB",         Temperature = 0f, Tint = 0f, Source = saturation };
            var blur       = new GaussianBlurEffect      { Name = "Blur",       BlurAmount = 0f,         Source = wb,
                                                           BorderMode = EffectBorderMode.Hard };

            var animatable = new[]
            {
                "Exposure.Exposure",
                "Contrast.Contrast",
                "Saturation.Saturation",
                "WB.Temperature",
                "WB.Tint",
                "Blur.BlurAmount",
            };

            var factory = compositor.CreateEffectFactory(blur, animatable);
            _brush = factory.CreateBrush();
            _brush.SetSourceParameter("backdrop", backdrop);

            _sprite = compositor.CreateSpriteVisual();
            _sprite.Brush = _brush;
            _sprite.RelativeSizeAdjustment = Vector2.One;

            ElementCompositionPreview.SetElementChildVisual(host, _sprite);
            _attached = true;
        }
        catch
        {
            // GPU/composition unavailable — silently no-op so the app keeps working
            _brush  = null;
            _sprite = null;
        }
    }

    public void Apply(TimelineClip? clip)
    {
        if (_brush is null) return;
        if (clip is null)
        {
            Reset();
            return;
        }

        // Map our slider ranges to the effect-parameter ranges
        // ExposureEffect.Exposure:        −2..+2 stops (matches clip.Exposure directly)
        // ContrastEffect.Contrast:        −1..+1 (clip −100..+100 / 100)
        // SaturationEffect.Saturation:     0..2  (clip −100..+100 → 1 + n/100)
        // TemperatureAndTintEffect.Temp:  −1..+1 (clip −100..+100 / 100)
        // TemperatureAndTintEffect.Tint:  −1..+1 (clip −100..+100 / 100)
        _brush.Properties.InsertScalar("Exposure.Exposure",     Clamp((float)clip.Exposure,         -2f, 2f));
        _brush.Properties.InsertScalar("Contrast.Contrast",     Clamp((float)clip.Contrast / 100f, -1f, 1f));
        _brush.Properties.InsertScalar("Saturation.Saturation", Clamp(1f + (float)clip.Saturation / 100f, 0f, 2f));
        _brush.Properties.InsertScalar("WB.Temperature",        Clamp((float)clip.Temperature / 100f, -1f, 1f));
        _brush.Properties.InsertScalar("WB.Tint",               Clamp((float)clip.Tint / 100f, -1f, 1f));

        // Effects list — Gaussian Blur is the only one fully wired in v1.
        // Intensity 0..1 maps to a blur radius up to 50 px.
        float blurAmount = 0f;
        foreach (var fx in clip.Effects)
            if (fx.Enabled && fx.Name == "Gaussian Blur")
                blurAmount = (float)(Math.Clamp(fx.Intensity, 0.0, 1.0) * 50.0);
        _brush.Properties.InsertScalar("Blur.BlurAmount", blurAmount);
    }

    /// <summary>Release the SpriteVisual + effect brush. Call before exporting so the
    /// Win2D effect pipeline doesn't share the DirectX device with the MediaComposition
    /// renderer (which can silently drop the video stream).</summary>
    public void Detach()
    {
        if (!_attached) return;
        try
        {
            if (_host is not null)
                ElementCompositionPreview.SetElementChildVisual(_host, null);
        }
        catch { }
        _sprite = null;
        _brush = null;
        _attached = false;
    }

    public void Reset()
    {
        if (_brush is null) return;
        _brush.Properties.InsertScalar("Exposure.Exposure",     0f);
        _brush.Properties.InsertScalar("Contrast.Contrast",     0f);
        _brush.Properties.InsertScalar("Saturation.Saturation", 1f);
        _brush.Properties.InsertScalar("WB.Temperature",        0f);
        _brush.Properties.InsertScalar("WB.Tint",               0f);
        _brush.Properties.InsertScalar("Blur.BlurAmount",       0f);
    }

    private static float Clamp(float v, float min, float max) =>
        v < min ? min : (v > max ? max : v);
}
