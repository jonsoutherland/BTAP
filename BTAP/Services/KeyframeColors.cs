using Windows.UI;

namespace BTAP.Services;

/// <summary>Stable per-parameter color for keyframe diamonds. Used by both the
/// Automations inspector row dots and the timeline clip diamond overlays so the
/// user can visually connect a row to a marker on the clip.</summary>
public static class KeyframeColors
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 255, 200, 110),  // amber
        Color.FromArgb(255, 130, 200, 255),  // sky
        Color.FromArgb(255, 255, 130, 220),  // pink
        Color.FromArgb(255, 130, 255, 180),  // mint
        Color.FromArgb(255, 255, 160, 110),  // orange
        Color.FromArgb(255, 200, 130, 255),  // lavender
        Color.FromArgb(255, 110, 230, 230),  // teal
        Color.FromArgb(255, 240, 240, 110),  // yellow
    };

    public static Color HueFor(string effectName, string paramKey)
    {
        int h = (effectName + ":" + paramKey).GetHashCode();
        return Palette[Math.Abs(h) % Palette.Length];
    }
}
