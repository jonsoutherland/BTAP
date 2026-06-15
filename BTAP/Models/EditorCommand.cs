namespace BTAP.Models;

/// <summary>Every action the user can bind to a keyboard shortcut. New shortcut-able
/// behaviors go here; EditorPage.InvokeCommand dispatches to the actual handler.</summary>
public enum EditorCommand
{
    PlayPause,
    StepBack,
    StepForward,
    Stop,

    Undo,
    Redo,
    Save,
    NewProject,
    OpenProject,
    Export,

    DeleteClip,
    RippleDelete,
    DuplicateClip,
    SplitAtPlayhead,

    AddMarker,
    ToggleSnap,
    Fullscreen,

    ToolCursor,
    ToolRazor,
    ToolHand,

    MoveClipUp,
    MoveClipDown,
    AddTitleAtPlayhead,

    CopyClip,
    PasteFromClipboard,
}

/// <summary>
/// Metadata for a bindable command.
/// <para><c>Name</c> is the full label shown in the customizer's command picker and
/// the right-hand binding list. <c>Short</c> is the abbreviated label rendered on a
/// keyboard key tile where space is tight (≤ ~7 characters renders cleanly).</para>
/// </summary>
public sealed record CommandDescriptor(EditorCommand Command, string Name, string Short, string Group);

public static class EditorCommandRegistry
{
    public static IReadOnlyList<CommandDescriptor> All { get; } = new List<CommandDescriptor>
    {
        new(EditorCommand.PlayPause,         "Play / Pause",           "Play",    "Playback"),
        new(EditorCommand.StepBack,          "Step back",              "Back",    "Playback"),
        new(EditorCommand.StepForward,       "Step forward",           "Fwd",     "Playback"),
        new(EditorCommand.Stop,              "Stop",                   "Stop",    "Playback"),

        new(EditorCommand.Undo,              "Undo",                   "Undo",    "Edit"),
        new(EditorCommand.Redo,              "Redo",                   "Redo",    "Edit"),
        new(EditorCommand.DeleteClip,        "Delete clip",            "Delete",  "Edit"),
        new(EditorCommand.RippleDelete,      "Ripple delete",          "Ripple",  "Edit"),
        new(EditorCommand.DuplicateClip,     "Duplicate clip",         "Dup",     "Edit"),
        new(EditorCommand.SplitAtPlayhead,   "Split at playhead",      "Split",   "Edit"),
        new(EditorCommand.MoveClipUp,        "Move clip up a track",   "Track↑",  "Edit"),
        new(EditorCommand.MoveClipDown,      "Move clip down a track", "Track↓",  "Edit"),
        new(EditorCommand.AddTitleAtPlayhead,"Add title at playhead",  "Title",   "Edit"),
        new(EditorCommand.CopyClip,          "Copy clip",              "Copy",    "Edit"),
        new(EditorCommand.PasteFromClipboard,"Paste from clipboard",   "Paste",   "Edit"),

        new(EditorCommand.Save,              "Save",                   "Save",    "File"),
        new(EditorCommand.NewProject,        "New project",            "New",     "File"),
        new(EditorCommand.OpenProject,       "Open project",           "Open",    "File"),
        new(EditorCommand.Export,            "Export",                 "Export",  "File"),

        new(EditorCommand.AddMarker,         "Add marker",             "Marker",  "Timeline"),
        new(EditorCommand.ToggleSnap,        "Toggle snap",            "Snap",    "Timeline"),

        new(EditorCommand.Fullscreen,        "Fullscreen preview",     "FullScr", "View"),

        new(EditorCommand.ToolCursor,        "Cursor tool",            "Cursor",  "Tools"),
        new(EditorCommand.ToolRazor,         "Razor tool",             "Razor",   "Tools"),
        new(EditorCommand.ToolHand,          "Hand tool",              "Hand",    "Tools"),
    };

    public static CommandDescriptor For(EditorCommand c) =>
        All.First(d => d.Command == c);
}
