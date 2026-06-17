using System.Text.Json;
using Windows.System;
using BTAP.Models;

namespace BTAP.Services;

public sealed class KeyBinding
{
    public EditorCommand       Command   { get; set; }
    public VirtualKey          Key       { get; set; }
    public VirtualKeyModifiers Modifiers { get; set; }

    public override bool Equals(object? obj) =>
        obj is KeyBinding b && b.Command == Command && b.Key == Key && b.Modifiers == Modifiers;
    public override int GetHashCode() => HashCode.Combine(Command, Key, Modifiers);
}

/// <summary>Persistent map of (Command → key+modifiers). Backs the keyboard-shortcut
/// customizer dialog. Lives in %LocalAppData%\BTAP\keybindings.json.</summary>
public sealed class KeyBindingsService
{
    private readonly List<KeyBinding> _bindings = new();

    public IReadOnlyList<KeyBinding> Bindings => _bindings;
    public event EventHandler? Changed;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BTAP", "keybindings.json");

    public KeyBindingsService() => Load();

    public void Load()
    {
        if (!System.IO.File.Exists(FilePath)) { LoadDefaults(); return; }
        try
        {
            var json = System.IO.File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<List<KeyBindingDto>>(json);
            if (dto is null || dto.Count == 0) { LoadDefaults(); return; }
            _bindings.Clear();
            foreach (var b in dto)
                _bindings.Add(new KeyBinding
                {
                    Command   = b.Command,
                    Key       = (VirtualKey)b.Key,
                    Modifiers = (VirtualKeyModifiers)b.Modifiers,
                });
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { LoadDefaults(); }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var dto = _bindings
                .Select(b => new KeyBindingDto { Command = b.Command, Key = (int)b.Key, Modifiers = (int)b.Modifiers })
                .ToList();
            System.IO.File.WriteAllText(FilePath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort — user can re-bind next session */ }
    }

    /// <summary>Binds <paramref name="cmd"/> to (<paramref name="key"/> + <paramref name="mods"/>),
    /// replacing any other command currently on that exact combo.</summary>
    public void Assign(EditorCommand cmd, VirtualKey key, VirtualKeyModifiers mods)
    {
        if (key == VirtualKey.None) return;
        _bindings.RemoveAll(b => b.Key == key && b.Modifiers == mods);
        _bindings.Add(new KeyBinding { Command = cmd, Key = key, Modifiers = mods });
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Action-first rebind. Removes every existing binding for
    /// <paramref name="cmd"/>, removes any binding owned by a different command on
    /// the same key+mods combo, and writes the new (cmd, key, mods) binding.
    /// The returned <see cref="RebindResult"/> tells the UI what got evicted so it
    /// can show "removed X from action Y" notices.</summary>
    public RebindResult Rebind(EditorCommand cmd, VirtualKey key, VirtualKeyModifiers mods)
    {
        var result = new RebindResult();
        result.RemovedFromAction.AddRange(_bindings.Where(b => b.Command == cmd));
        result.RemovedFromCombo.AddRange(
            _bindings.Where(b => b.Key == key && b.Modifiers == mods && b.Command != cmd));

        _bindings.RemoveAll(b => b.Command == cmd || (b.Key == key && b.Modifiers == mods));
        _bindings.Add(new KeyBinding { Command = cmd, Key = key, Modifiers = mods });

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public IEnumerable<KeyBinding> BindingsForCommand(EditorCommand cmd) =>
        _bindings.Where(b => b.Command == cmd);

    public IEnumerable<CommandDescriptor> UnboundActions()
    {
        var bound = new HashSet<EditorCommand>(_bindings.Select(b => b.Command));
        return EditorCommandRegistry.All.Where(d => !bound.Contains(d.Command));
    }

    public void Remove(KeyBinding binding)
    {
        _bindings.RemoveAll(b => b.Equals(binding));
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefaults()
    {
        LoadDefaults();
        Save();
    }

    /// <summary>Replace the whole binding set in one shot. Used by preset
    /// switching to drop the old keymap and pick up a curated one. Fires
    /// <see cref="Changed"/> once at the end instead of per-binding so consumers
    /// (keyboard accelerators, customizer UI) only rebuild themselves once.</summary>
    public void LoadBindings(IEnumerable<KeyBinding> bindings)
    {
        _bindings.Clear();
        foreach (var b in bindings)
            _bindings.Add(new KeyBinding { Command = b.Command, Key = b.Key, Modifiers = b.Modifiers });
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<KeyBinding> BindingsForKey(VirtualKey key) =>
        _bindings.Where(b => b.Key == key);

    private void LoadDefaults()
    {
        _bindings.Clear();
        void Add(EditorCommand cmd, VirtualKey key, VirtualKeyModifiers mods = VirtualKeyModifiers.None) =>
            _bindings.Add(new KeyBinding { Command = cmd, Key = key, Modifiers = mods });

        Add(EditorCommand.PlayPause,         VirtualKey.Space);
        Add(EditorCommand.PlayPause,         (VirtualKey)'L');
        Add(EditorCommand.StepBack,          (VirtualKey)'J');
        Add(EditorCommand.Stop,              (VirtualKey)'K');

        Add(EditorCommand.Undo,              (VirtualKey)'Z', VirtualKeyModifiers.Control);
        Add(EditorCommand.Redo,              (VirtualKey)'Y', VirtualKeyModifiers.Control);
        Add(EditorCommand.Save,              (VirtualKey)'S', VirtualKeyModifiers.Control);
        Add(EditorCommand.NewProject,        (VirtualKey)'N', VirtualKeyModifiers.Control);
        Add(EditorCommand.OpenProject,       (VirtualKey)'O', VirtualKeyModifiers.Control);
        Add(EditorCommand.Export,            (VirtualKey)'E', VirtualKeyModifiers.Control);

        Add(EditorCommand.DeleteClip,        VirtualKey.Delete);
        Add(EditorCommand.DeleteClip,        VirtualKey.Back);
        Add(EditorCommand.RippleDelete,      VirtualKey.Delete, VirtualKeyModifiers.Shift);
        Add(EditorCommand.RippleDelete,      VirtualKey.Back,   VirtualKeyModifiers.Shift);
        Add(EditorCommand.DuplicateClip,     (VirtualKey)'D',   VirtualKeyModifiers.Control);
        Add(EditorCommand.SplitAtPlayhead,   (VirtualKey)'B',   VirtualKeyModifiers.Control);
        Add(EditorCommand.CopyClip,          (VirtualKey)'C',   VirtualKeyModifiers.Control);
        Add(EditorCommand.PasteFromClipboard,(VirtualKey)'V',   VirtualKeyModifiers.Control);

        Add(EditorCommand.AddMarker,         (VirtualKey)'M');
        Add(EditorCommand.ToggleSnap,        (VirtualKey)'S');
        Add(EditorCommand.Fullscreen,        (VirtualKey)'F');

        Add(EditorCommand.ToolCursor,        (VirtualKey)'V');
        Add(EditorCommand.ToolRazor,         (VirtualKey)'C');
        Add(EditorCommand.ToolHand,          (VirtualKey)'H');

        Add(EditorCommand.MoveClipUp,        VirtualKey.Up,   VirtualKeyModifiers.Control);
        Add(EditorCommand.MoveClipDown,      VirtualKey.Down, VirtualKeyModifiers.Control);
        Add(EditorCommand.AddTitleAtPlayhead,(VirtualKey)'T');

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string FormatShortcut(VirtualKey key, VirtualKeyModifiers mods)
    {
        var sb = new System.Text.StringBuilder();
        if (mods.HasFlag(VirtualKeyModifiers.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(VirtualKeyModifiers.Shift))   sb.Append("Shift+");
        if (mods.HasFlag(VirtualKeyModifiers.Menu))    sb.Append("Alt+");
        sb.Append(KeyName(key));
        return sb.ToString();
    }

    public static string KeyName(VirtualKey key) => key switch
    {
        VirtualKey.Space  => "Space",
        VirtualKey.Back   => "Backspace",
        VirtualKey.Delete => "Delete",
        VirtualKey.Up     => "↑",
        VirtualKey.Down   => "↓",
        VirtualKey.Left   => "←",
        VirtualKey.Right  => "→",
        VirtualKey.Enter  => "Enter",
        VirtualKey.Tab    => "Tab",
        VirtualKey.Escape => "Esc",
        VirtualKey.Home   => "Home",
        VirtualKey.End    => "End",
        _ when key >= (VirtualKey)'A' && key <= (VirtualKey)'Z' => ((char)key).ToString(),
        _ when key >= (VirtualKey)'0' && key <= (VirtualKey)'9' => ((char)key).ToString(),
        _ when key >= VirtualKey.F1 && key <= VirtualKey.F12     => $"F{(int)(key - VirtualKey.F1 + 1)}",
        _ => key.ToString(),
    };
}

/// <summary>Diff returned by <see cref="KeyBindingsService.Rebind"/> so callers can
/// describe to the user what got replaced.</summary>
public sealed class RebindResult
{
    public List<KeyBinding> RemovedFromAction { get; } = new();
    public List<KeyBinding> RemovedFromCombo  { get; } = new();
}

internal sealed class KeyBindingDto
{
    public EditorCommand Command   { get; set; }
    public int           Key       { get; set; }
    public int           Modifiers { get; set; }
}
