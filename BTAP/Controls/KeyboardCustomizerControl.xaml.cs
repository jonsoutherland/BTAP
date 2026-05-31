using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using BTAP.Models;
using BTAP.Services;

namespace BTAP.Controls;

public sealed partial class KeyboardCustomizerControl : UserControl
{
    private KeyBindingsService? _service;
    private VirtualKey _selectedKey = VirtualKey.None;
    private readonly Dictionary<VirtualKey, Border> _keyButtons = new();
    private readonly Dictionary<VirtualKey, StackPanel> _keyBindingStacks = new();

    // Listen-for-key state
    private EditorCommand? _listenTarget;
    private XamlRoot?      _capturedRoot;
    private UIElement?     _previousFocus;

    public KeyboardCustomizerControl()
    {
        InitializeComponent();
        IsTabStop = true;
    }

    public void Attach(KeyBindingsService service)
    {
        if (_service is not null) _service.Changed -= OnBindingsChanged;
        _service = service;
        _service.Changed += OnBindingsChanged;
        BuildKeyboard();
        RefreshUnboundList();
        RefreshDetailPanel();
    }

    private void OnBindingsChanged(object? sender, EventArgs e)
    {
        RefreshKeyHighlights();
        RefreshUnboundList();
        RefreshDetailPanel();
    }

    // ── Keyboard layout ────────────────────────────────────────────────────

    private static readonly (string Label, VirtualKey Key, double Width)[][] Layout = new[]
    {
        new (string, VirtualKey, double)[]
        {
            ("Esc", VirtualKey.Escape, 1.0),
            (null!, VirtualKey.None, 0.5),
            ("F1", VirtualKey.F1, 1.0), ("F2", VirtualKey.F2, 1.0), ("F3", VirtualKey.F3, 1.0), ("F4", VirtualKey.F4, 1.0),
            (null!, VirtualKey.None, 0.3),
            ("F5", VirtualKey.F5, 1.0), ("F6", VirtualKey.F6, 1.0), ("F7", VirtualKey.F7, 1.0), ("F8", VirtualKey.F8, 1.0),
            (null!, VirtualKey.None, 0.3),
            ("F9", VirtualKey.F9, 1.0), ("F10", VirtualKey.F10, 1.0), ("F11", VirtualKey.F11, 1.0), ("F12", VirtualKey.F12, 1.0),
        },
        new (string, VirtualKey, double)[]
        {
            ("1", (VirtualKey)'1', 1.0), ("2", (VirtualKey)'2', 1.0), ("3", (VirtualKey)'3', 1.0),
            ("4", (VirtualKey)'4', 1.0), ("5", (VirtualKey)'5', 1.0), ("6", (VirtualKey)'6', 1.0),
            ("7", (VirtualKey)'7', 1.0), ("8", (VirtualKey)'8', 1.0), ("9", (VirtualKey)'9', 1.0),
            ("0", (VirtualKey)'0', 1.0),
            ("⌫", VirtualKey.Back, 1.8),
        },
        new (string, VirtualKey, double)[]
        {
            ("Tab", VirtualKey.Tab, 1.4),
            ("Q", (VirtualKey)'Q', 1.0), ("W", (VirtualKey)'W', 1.0), ("E", (VirtualKey)'E', 1.0),
            ("R", (VirtualKey)'R', 1.0), ("T", (VirtualKey)'T', 1.0), ("Y", (VirtualKey)'Y', 1.0),
            ("U", (VirtualKey)'U', 1.0), ("I", (VirtualKey)'I', 1.0), ("O", (VirtualKey)'O', 1.0),
            ("P", (VirtualKey)'P', 1.0),
        },
        new (string, VirtualKey, double)[]
        {
            (null!, VirtualKey.None, 0.4),
            ("A", (VirtualKey)'A', 1.0), ("S", (VirtualKey)'S', 1.0), ("D", (VirtualKey)'D', 1.0),
            ("F", (VirtualKey)'F', 1.0), ("G", (VirtualKey)'G', 1.0), ("H", (VirtualKey)'H', 1.0),
            ("J", (VirtualKey)'J', 1.0), ("K", (VirtualKey)'K', 1.0), ("L", (VirtualKey)'L', 1.0),
            ("Enter", VirtualKey.Enter, 1.6),
        },
        new (string, VirtualKey, double)[]
        {
            (null!, VirtualKey.None, 0.7),
            ("Z", (VirtualKey)'Z', 1.0), ("X", (VirtualKey)'X', 1.0), ("C", (VirtualKey)'C', 1.0),
            ("V", (VirtualKey)'V', 1.0), ("B", (VirtualKey)'B', 1.0), ("N", (VirtualKey)'N', 1.0),
            ("M", (VirtualKey)'M', 1.0),
        },
        new (string, VirtualKey, double)[]
        {
            (null!, VirtualKey.None, 2.0),
            ("Space", VirtualKey.Space, 5.0),
            (null!, VirtualKey.None, 0.5),
            ("←", VirtualKey.Left, 1.0), ("↑", VirtualKey.Up, 1.0),
            ("↓", VirtualKey.Down, 1.0), ("→", VirtualKey.Right, 1.0),
            (null!, VirtualKey.None, 0.3),
            ("Del", VirtualKey.Delete, 1.2),
        },
    };

    private const double UnitWidth  = 54.0;
    private const double KeyHeight  = 76.0;
    private const double KeySpacing = 4.0;

    private void BuildKeyboard()
    {
        KeyboardRoot.Children.Clear();
        _keyButtons.Clear();
        _keyBindingStacks.Clear();

        foreach (var row in Layout)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = KeySpacing };
            foreach (var (label, key, width) in row)
            {
                double w = width * UnitWidth + (width - 1) * KeySpacing;
                if (key == VirtualKey.None)
                {
                    rowPanel.Children.Add(new Border { Width = w });
                    continue;
                }
                rowPanel.Children.Add(MakeKeyButton(label, key, w));
            }
            KeyboardRoot.Children.Add(rowPanel);
        }

        RefreshKeyHighlights();
    }

    private Border MakeKeyButton(string label, VirtualKey key, double width)
    {
        var keyLabel = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var bindingStack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _keyBindingStacks[key] = bindingStack;

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Padding = new Thickness(3, 5, 3, 5),
        };
        content.Children.Add(keyLabel);
        content.Children.Add(bindingStack);

        var border = new Border
        {
            Width = width,
            Height = KeyHeight,
            CornerRadius = new CornerRadius(5),
            Background = (Brush)Application.Current.Resources["BgSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            BorderThickness = new Thickness(1),
            Child = content,
            Tag = key,
        };
        border.Tapped += OnKeyTapped;
        _keyButtons[key] = border;
        return border;
    }

    private void OnKeyTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not VirtualKey key) return;
        _selectedKey = key;
        ClearStatusBanner();
        RefreshKeyHighlights();
        RefreshDetailPanel();
    }

    private void RefreshKeyHighlights()
    {
        if (_service is null) return;

        var accent       = (Brush)Application.Current.Resources["AccentBrush"];
        var hairline     = (Brush)Application.Current.Resources["HairlineBrush"];
        var surfaceBg    = (Brush)Application.Current.Resources["BgSurfaceBrush"];
        var elevatedBg   = (Brush)Application.Current.Resources["BgElevatedBrush"];
        var boundBorder  = new SolidColorBrush(Color.FromArgb(140, 127, 176, 105));
        var bindingLabelBrush = new SolidColorBrush(Color.FromArgb(220, 127, 176, 105));
        var faint        = (Brush)Application.Current.Resources["TextFaintBrush"];
        var mono         = (FontFamily)Application.Current.Resources["MonoFont"];

        foreach (var (key, border) in _keyButtons)
        {
            bool selected = key == _selectedKey;
            var bindings = _service.BindingsForKey(key).ToList();
            bool bound = bindings.Count > 0;

            border.BorderBrush = selected ? accent : (bound ? boundBorder : hairline);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.Background = bound ? elevatedBg : surfaceBg;

            if (!_keyBindingStacks.TryGetValue(key, out var stack)) continue;
            stack.Children.Clear();

            const int MaxShown = 2;
            for (int i = 0; i < bindings.Count && i < MaxShown; i++)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = FormatBindingForKeyTile(bindings[i]),
                    FontSize = 9,
                    FontFamily = mono,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = bindingLabelBrush,
                });
            }
            if (bindings.Count > MaxShown)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"+{bindings.Count - MaxShown}",
                    FontSize = 9,
                    FontFamily = mono,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = faint,
                });
            }
        }
    }

    private static string FormatBindingForKeyTile(KeyBinding b)
    {
        var info = EditorCommandRegistry.For(b.Command);
        var mod = "";
        if (b.Modifiers.HasFlag(VirtualKeyModifiers.Control)) mod += "⌃";
        if (b.Modifiers.HasFlag(VirtualKeyModifiers.Shift))   mod += "⇧";
        if (b.Modifiers.HasFlag(VirtualKeyModifiers.Menu))    mod += "⌥";
        return mod + info.Short;
    }

    // ── Unbound actions list ──────────────────────────────────────────────

    private void RefreshUnboundList()
    {
        if (_service is null) return;
        UnboundList.Children.Clear();

        var unbound = _service.UnboundActions().ToList();
        if (unbound.Count == 0)
        {
            UnboundList.Children.Add(new TextBlock
            {
                Text = "All actions are bound.",
                FontSize = 11,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            });
            return;
        }

        foreach (var grp in unbound.GroupBy(d => d.Group))
        {
            UnboundList.Children.Add(new TextBlock
            {
                Text = grp.Key.ToUpperInvariant(),
                FontSize = 9,
                CharacterSpacing = 120,
                Margin = new Thickness(2, 8, 0, 2),
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            foreach (var d in grp)
                UnboundList.Children.Add(MakeUnboundActionButton(d));
        }
    }

    private UIElement MakeUnboundActionButton(CommandDescriptor descriptor)
    {
        var btn = new Button
        {
            Content = descriptor.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontSize = 11.5,
            Padding = new Thickness(8, 5, 8, 5),
        };
        btn.Click += (_, _) => StartListen(descriptor.Command, isRebind: false);
        return btn;
    }

    // ── Side panel (selected-key bindings) ────────────────────────────────

    private void RefreshDetailPanel()
    {
        if (_service is null) return;
        BindingsList.Children.Clear();

        if (_selectedKey == VirtualKey.None)
        {
            TbHeader.Text = "Select a key";
            TbHelper.Text = "Click a key on the left to view or change its shortcuts. Use the Unbound list to assign new shortcuts.";
            return;
        }

        TbHeader.Text = $"Key: {KeyBindingsService.KeyName(_selectedKey)}";
        TbHelper.Text = "Each binding can be rebound to a different key combo or removed.";

        var existing = _service.BindingsForKey(_selectedKey).ToList();
        if (existing.Count == 0)
        {
            BindingsList.Children.Add(new TextBlock
            {
                Text = "No shortcuts assigned.",
                FontSize = 11,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = (Brush)Application.Current.Resources["TextFaintBrush"],
            });
        }
        else
        {
            foreach (var b in existing)
                BindingsList.Children.Add(MakeBindingRow(b));
        }
    }

    private UIElement MakeBindingRow(KeyBinding b)
    {
        var info = EditorCommandRegistry.For(b.Command);

        var shortcut = new TextBlock
        {
            Text = KeyBindingsService.FormatShortcut(b.Key, b.Modifiers),
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
        };
        var name = new TextBlock
        {
            Text = info.Name,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var rebindBtn = new Button
        {
            Content = "Rebind",
            FontSize = 10.5,
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 0,
        };
        rebindBtn.Click += (_, _) => StartListen(b.Command, isRebind: true);
        var removeBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
        };
        removeBtn.Click += (_, _) => _service?.Remove(b);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        btnRow.Children.Add(rebindBtn);
        btnRow.Children.Add(removeBtn);

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(80) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
        };
        Grid.SetColumn(shortcut, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(btnRow, 2);
        grid.Children.Add(shortcut);
        grid.Children.Add(name);
        grid.Children.Add(btnRow);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["BgSurfaceBrush"],
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 5, 4, 5),
            Child = grid,
        };
    }

    // ── Listen-for-keystroke ──────────────────────────────────────────────

    /// <summary>Enter capture mode for <paramref name="cmd"/>. Shows the listen overlay,
    /// hooks the XamlRoot's KeyDown until a non-modifier key (or Esc) is pressed.</summary>
    private void StartListen(EditorCommand cmd, bool isRebind)
    {
        if (_service is null) return;
        _listenTarget = cmd;

        var info = EditorCommandRegistry.For(cmd);
        TbListenAction.Text = $"for {info.Name}";

        var existing = _service.BindingsForCommand(cmd).ToList();
        if (existing.Count > 0)
        {
            var combos = string.Join(", ",
                existing.Select(b => KeyBindingsService.FormatShortcut(b.Key, b.Modifiers)));
            TbListenWarn.Text = $"⚠ This action is already bound to {combos}. The new key will replace it.";
            TbListenWarn.Visibility = Visibility.Visible;
        }
        else
        {
            TbListenWarn.Visibility = Visibility.Collapsed;
        }

        TbListenCombo.Text = "Press any key…";
        ListenOverlay.Visibility = Visibility.Visible;

        // Hook keys on the XamlRoot so we capture regardless of inner focus.
        _capturedRoot = XamlRoot;
        if (_capturedRoot?.Content is UIElement root)
        {
            root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListenKeyDown), handledEventsToo: true);
            root.AddHandler(UIElement.KeyUpEvent,   new KeyEventHandler(OnListenKeyUp),   handledEventsToo: true);
        }

        Focus(FocusState.Programmatic);
    }

    private void EndListen()
    {
        ListenOverlay.Visibility = Visibility.Collapsed;
        _listenTarget = null;

        if (_capturedRoot?.Content is UIElement root)
        {
            root.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListenKeyDown));
            root.RemoveHandler(UIElement.KeyUpEvent,   new KeyEventHandler(OnListenKeyUp));
        }
        _capturedRoot = null;
    }

    private void OnListenKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_listenTarget is not { } cmd) return;

        if (e.Key == VirtualKey.Escape)
        {
            EndListen();
            e.Handled = true;
            return;
        }

        // Update the live "current combo" display every keypress so the user sees
        // their modifiers stack up before they commit with a real key.
        var mods = ReadModifiers();
        if (IsModifierKey(e.Key))
        {
            TbListenCombo.Text = mods == VirtualKeyModifiers.None
                ? "Press any key…"
                : KeyBindingsService.FormatShortcut(VirtualKey.None, mods).TrimEnd('+') + " + …";
            e.Handled = true;
            return;
        }

        // Non-modifier key → finalize the binding.
        var result = _service!.Rebind(cmd, e.Key, mods);
        var info = EditorCommandRegistry.For(cmd);
        var combo = KeyBindingsService.FormatShortcut(e.Key, mods);

        var msg = new System.Text.StringBuilder();
        msg.Append($"Bound {combo} to {info.Name}.");
        if (result.RemovedFromAction.Count > 0)
        {
            var prior = string.Join(", ",
                result.RemovedFromAction.Select(b => KeyBindingsService.FormatShortcut(b.Key, b.Modifiers)));
            msg.Append($" Previous binding ({prior}) removed.");
        }
        if (result.RemovedFromCombo.Count > 0)
        {
            var others = string.Join(", ", result.RemovedFromCombo
                .Select(b => EditorCommandRegistry.For(b.Command).Name));
            msg.Append($" Unbound {combo} from: {others}.");
        }
        ShowStatusBanner(msg.ToString(), isWarning: result.RemovedFromCombo.Count > 0);

        EndListen();
        e.Handled = true;
    }

    private void OnListenKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_listenTarget is null) return;
        if (!IsModifierKey(e.Key)) return;
        var mods = ReadModifiers();
        TbListenCombo.Text = mods == VirtualKeyModifiers.None
            ? "Press any key…"
            : KeyBindingsService.FormatShortcut(VirtualKey.None, mods).TrimEnd('+') + " + …";
        e.Handled = true;
    }

    private static bool IsModifierKey(VirtualKey k) => k
        is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift   or VirtualKey.LeftShift   or VirtualKey.RightShift
        or VirtualKey.Menu    or VirtualKey.LeftMenu    or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static VirtualKeyModifiers ReadModifiers()
    {
        var get = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        var m = VirtualKeyModifiers.None;
        if (get(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            m |= VirtualKeyModifiers.Control;
        if (get(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            m |= VirtualKeyModifiers.Shift;
        if (get(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            m |= VirtualKeyModifiers.Menu;
        return m;
    }

    // ── Status banner & reset ─────────────────────────────────────────────

    private void ShowStatusBanner(string text, bool isWarning)
    {
        TbStatusBanner.Text = text;
        StatusBanner.Background = isWarning
            ? new SolidColorBrush(Color.FromArgb(60, 213, 138, 82))
            : new SolidColorBrush(Color.FromArgb(60, 127, 176, 105));
        TbStatusBanner.Foreground = isWarning
            ? new SolidColorBrush(Color.FromArgb(255, 240, 200, 150))
            : (Brush)Application.Current.Resources["TextPrimaryBrush"];
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void ClearStatusBanner()
    {
        StatusBanner.Visibility = Visibility.Collapsed;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _service?.ResetToDefaults();
        ShowStatusBanner("All shortcuts reset to defaults.", isWarning: false);
    }
}
