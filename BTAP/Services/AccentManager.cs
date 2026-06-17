using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BTAP.Services;

/// <summary>
/// Watches <see cref="AppSettingsService"/> for accent changes and applies them
/// to the shared <c>AccentBrush</c> resource at runtime. Because XAML's
/// <c>StaticResource</c> resolves to the brush *instance*, mutating its
/// <see cref="SolidColorBrush.Color"/> propagates everywhere the brush is bound.
/// </summary>
public static class AccentManager
{
    private static bool _wired;

    public static void Init()
    {
        if (_wired) return;
        _wired = true;
        AppSettingsService.Instance.Changed += (_, _) => Apply();
        Apply();
    }

    public static void Apply()
    {
        try
        {
            if (Application.Current?.Resources is null) return;
            if (Application.Current.Resources["AccentBrush"] is not SolidColorBrush brush) return;
            brush.Color = ResolveColor();
        }
        catch { /* applying accent is best-effort */ }
    }

    /// <summary>Resolves the currently-effective accent colour: the custom hex
    /// when <see cref="AppSettingsService.UseCustomAccent"/> is on, otherwise the
    /// curated swatch for the saved <see cref="AccentScheme"/>.</summary>
    public static Color ResolveColor()
    {
        var s = AppSettingsService.Instance;
        if (s.UseCustomAccent && TryParseHex(s.CustomAccentHex, out var c)) return c;
        return s.Accent switch
        {
            AccentScheme.Blue   => Color.FromArgb(0xFF, 0x5B, 0x9F, 0xE3),
            AccentScheme.Purple => Color.FromArgb(0xFF, 0xA6, 0x7F, 0xE0),
            AccentScheme.Amber  => Color.FromArgb(0xFF, 0xE0, 0xA3, 0x52),
            AccentScheme.Rose   => Color.FromArgb(0xFF, 0xD2, 0x7F, 0xA8),
            AccentScheme.Teal   => Color.FromArgb(0xFF, 0x5B, 0xB8, 0xB0),
            AccentScheme.Coral  => Color.FromArgb(0xFF, 0xE0, 0x80, 0x70),
            AccentScheme.Slate  => Color.FromArgb(0xFF, 0x80, 0x90, 0xA8),
            _                   => Color.FromArgb(0xFF, 0x7F, 0xB0, 0x69), // Sage default
        };
    }

    /// <summary>Parses #RRGGBB or #AARRGGBB. Returns false (no exception) on bad
    /// input so the consumer falls back to the curated swatch.</summary>
    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length != 6 && s.Length != 8) return false;
        if (!ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        byte a, r, g, b;
        if (s.Length == 8) { a = (byte)((v >> 24) & 0xFF); r = (byte)((v >> 16) & 0xFF); g = (byte)((v >> 8) & 0xFF); b = (byte)(v & 0xFF); }
        else               { a = 0xFF;                       r = (byte)((v >> 16) & 0xFF); g = (byte)((v >> 8) & 0xFF); b = (byte)(v & 0xFF); }
        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    public static string FormatHex(Color c) =>
        $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
