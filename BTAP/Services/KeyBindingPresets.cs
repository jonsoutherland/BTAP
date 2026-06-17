using Windows.System;
using BTAP.Models;

namespace BTAP.Services;

/// <summary>Curated keybind sets that ship alongside each layout preset.
/// <list type="bullet">
///   <item><see cref="KeybindPreset.Minimal"/>: just the basics (save / undo /
///   space-to-play) — keeps Simple mode free of memorisation cost.</item>
///   <item><see cref="KeybindPreset.Default"/>: the legacy BTAP defaults.</item>
///   <item><see cref="KeybindPreset.PremiereLike"/>: layers Premiere-flavoured
///   accelerators (Ripple delete, J/K/L, mark in/out) on top of the defaults.</item>
/// </list>
/// Applying a preset replaces the entire binding set — manual customisations
/// made after that point survive until the next preset switch.</summary>
public static class KeyBindingPresets
{
    public static void Apply(KeyBindingsService svc, KeybindPreset preset) =>
        svc.LoadBindings(BuildFor(preset));

    private static IEnumerable<KeyBinding> BuildFor(KeybindPreset preset) => preset switch
    {
        KeybindPreset.Minimal      => Minimal(),
        KeybindPreset.PremiereLike => PremiereLike(),
        _                          => Default(),
    };

    private static KeyBinding B(EditorCommand cmd, VirtualKey key,
                                VirtualKeyModifiers mods = VirtualKeyModifiers.None) =>
        new() { Command = cmd, Key = key, Modifiers = mods };

    private static IEnumerable<KeyBinding> Minimal()
    {
        yield return B(EditorCommand.PlayPause, VirtualKey.Space);
        yield return B(EditorCommand.Save,       (VirtualKey)'S', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Undo,       (VirtualKey)'Z', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Redo,       (VirtualKey)'Y', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.DeleteClip, VirtualKey.Delete);
        yield return B(EditorCommand.Export,     (VirtualKey)'E', VirtualKeyModifiers.Control);
    }

    private static IEnumerable<KeyBinding> Default()
    {
        yield return B(EditorCommand.PlayPause, VirtualKey.Space);
        yield return B(EditorCommand.PlayPause, (VirtualKey)'L');
        yield return B(EditorCommand.StepBack,  (VirtualKey)'J');
        yield return B(EditorCommand.Stop,      (VirtualKey)'K');

        yield return B(EditorCommand.Undo,    (VirtualKey)'Z', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Redo,    (VirtualKey)'Y', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Save,    (VirtualKey)'S', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.NewProject,  (VirtualKey)'N', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.OpenProject, (VirtualKey)'O', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Export,      (VirtualKey)'E', VirtualKeyModifiers.Control);

        yield return B(EditorCommand.DeleteClip,         VirtualKey.Delete);
        yield return B(EditorCommand.DeleteClip,         VirtualKey.Back);
        yield return B(EditorCommand.RippleDelete,       VirtualKey.Delete, VirtualKeyModifiers.Shift);
        yield return B(EditorCommand.RippleDelete,       VirtualKey.Back,   VirtualKeyModifiers.Shift);
        yield return B(EditorCommand.DuplicateClip,      (VirtualKey)'D',   VirtualKeyModifiers.Control);
        yield return B(EditorCommand.SplitAtPlayhead,    (VirtualKey)'B',   VirtualKeyModifiers.Control);
        yield return B(EditorCommand.CopyClip,           (VirtualKey)'C',   VirtualKeyModifiers.Control);
        yield return B(EditorCommand.PasteFromClipboard, (VirtualKey)'V',   VirtualKeyModifiers.Control);

        yield return B(EditorCommand.AddMarker,  (VirtualKey)'M');
        yield return B(EditorCommand.ToggleSnap, (VirtualKey)'S');
        yield return B(EditorCommand.Fullscreen, (VirtualKey)'F');

        yield return B(EditorCommand.ToolCursor, (VirtualKey)'V');
        yield return B(EditorCommand.ToolRazor,  (VirtualKey)'C');
        yield return B(EditorCommand.ToolHand,   (VirtualKey)'H');

        yield return B(EditorCommand.MoveClipUp,   VirtualKey.Up,   VirtualKeyModifiers.Control);
        yield return B(EditorCommand.MoveClipDown, VirtualKey.Down, VirtualKeyModifiers.Control);
        yield return B(EditorCommand.AddTitleAtPlayhead, (VirtualKey)'T');
    }

    /// <summary>Premiere-flavoured keymap. Reuses defaults as a base and adds
    /// the conventions long-time NLE users expect (Q/W ripple trim, mark in/out
    /// on I/O). Where a key is already taken by the default set, Premiere's
    /// usage wins — that's why this is its own preset, not an override on top.</summary>
    private static IEnumerable<KeyBinding> PremiereLike()
    {
        // J/K/L transport — Premiere-defining.
        yield return B(EditorCommand.PlayPause, (VirtualKey)'L');
        yield return B(EditorCommand.StepBack,  (VirtualKey)'J');
        yield return B(EditorCommand.Stop,      (VirtualKey)'K');
        yield return B(EditorCommand.PlayPause, VirtualKey.Space);

        // Standard edits — same modifier convention.
        yield return B(EditorCommand.Undo,    (VirtualKey)'Z', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Redo,    (VirtualKey)'Z', VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        yield return B(EditorCommand.Redo,    (VirtualKey)'Y', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Save,    (VirtualKey)'S', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.NewProject,  (VirtualKey)'N', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.OpenProject, (VirtualKey)'O', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.Export,      (VirtualKey)'M', VirtualKeyModifiers.Control);  // "media → export"

        // Razor on C — Premiere's razor tool key.
        yield return B(EditorCommand.ToolRazor,  (VirtualKey)'C');
        yield return B(EditorCommand.ToolCursor, (VirtualKey)'V');
        yield return B(EditorCommand.ToolHand,   (VirtualKey)'H');

        // Split at playhead = Ctrl+K (Premiere's "add edit").
        yield return B(EditorCommand.SplitAtPlayhead, (VirtualKey)'K', VirtualKeyModifiers.Control);

        // Delete / ripple delete: Delete = lift, Shift+Delete = ripple.
        yield return B(EditorCommand.DeleteClip,   VirtualKey.Delete);
        yield return B(EditorCommand.DeleteClip,   VirtualKey.Back);
        yield return B(EditorCommand.RippleDelete, VirtualKey.Delete, VirtualKeyModifiers.Shift);
        yield return B(EditorCommand.RippleDelete, VirtualKey.Back,   VirtualKeyModifiers.Shift);

        // Marker on M conflicts with Export above — keep it on Shift+M.
        yield return B(EditorCommand.AddMarker, (VirtualKey)'M', VirtualKeyModifiers.Shift);

        yield return B(EditorCommand.DuplicateClip,      (VirtualKey)'D', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.CopyClip,           (VirtualKey)'C', VirtualKeyModifiers.Control);
        yield return B(EditorCommand.PasteFromClipboard, (VirtualKey)'V', VirtualKeyModifiers.Control);

        yield return B(EditorCommand.Fullscreen,  VirtualKey.Tab, VirtualKeyModifiers.Shift); // Premiere: ` for fullscreen, Tab+Shift here
        yield return B(EditorCommand.ToggleSnap,  (VirtualKey)'S');

        yield return B(EditorCommand.MoveClipUp,   VirtualKey.Up,   VirtualKeyModifiers.Control);
        yield return B(EditorCommand.MoveClipDown, VirtualKey.Down, VirtualKeyModifiers.Control);
        yield return B(EditorCommand.AddTitleAtPlayhead, (VirtualKey)'T');
    }
}
