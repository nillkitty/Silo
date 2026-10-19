using System.Windows.Controls;
using System.Windows.Input;
using Silo.Contracts.Providers.Data;
using Silo.Expressions;
using Silo.Hotkeys;
using Silo.Storage;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>A box that takes the key combination the person presses instead of text: click it, then press Ctrl+Win+J (or any combination).</summary>
internal sealed class KeyCaptureBox : TextBox
{
    private ShortcutKey _value;

    public KeyCaptureBox()
    {
        IsReadOnly              = true;
        IsReadOnlyCaretVisible  = false;
        Cursor                  = Cursors.Arrow;
        MinWidth                = 200;
        ToolTip                 = "Click here, then press the keys. Backspace clears it.";
        InputMethod.SetIsInputMethodEnabled(this, false);
        GotKeyboardFocus  += (_, _) => Show();
        LostKeyboardFocus += (_, _) => Show();
        Show();
    }

    /// <summary>The captured key (<see cref="ShortcutKey.None" /> for none).</summary>
    public ShortcutKey Value
    {
        get => _value;
        set
        {
            _value = value;
            Show();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private void Show(string? partial = null)
    {
        Text = partial ?? (_value.IsSet ? _value.ToString() : IsKeyboardFocused ? "Press the keys…" : "(none: click here, then press the keys)");
    }

    private static ShortcutModifiers CurrentModifiers()
    {
        var m = ShortcutModifiers.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(ModifierKeys.Control)) m |= ShortcutModifiers.Control;
        if (k.HasFlag(ModifierKeys.Alt)) m |= ShortcutModifiers.Alt;
        if (k.HasFlag(ModifierKeys.Shift)) m |= ShortcutModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Windows)) m |= ShortcutModifiers.Windows;
        return m;
    }

    private static string ModifierText(ShortcutModifiers m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        return string.Join('+', parts);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
                  {
                      Key.System            => e.SystemKey,
                      Key.ImeProcessed      => e.ImeProcessedKey,
                      Key.DeadCharProcessed => e.DeadCharProcessedKey,
                      _                     => e.Key
                  };
        var mods = CurrentModifiers();

        // Tab, Escape and Enter on their own belong to the dialog
        if (mods == ShortcutModifiers.None && key is Key.Tab or Key.Escape or Key.Enter)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Show(ModifierText(mods) + (mods == ShortcutModifiers.None ? string.Empty : "+…"));
            return;
        }

        if (mods == ShortcutModifiers.None && key is Key.Back or Key.Delete)
        {
            Value = ShortcutKey.None;
            return;
        }

        var captured = new ShortcutKey(mods, KeyInterop.VirtualKeyFromKey(key));
        if (captured.VirtualKey == 0 || captured.KeyName.StartsWith("VK", StringComparison.Ordinal))
        {
            Show("That key can not be used.");
            return;
        }

        Value = captured;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        Show();
    }
}

/// <summary>Asks for a shortcut key binding: its name, the key, what it does, where it works and whose it is.</summary>
internal sealed class HotkeyDialog : Window
{
    private readonly GlobalShortcutPrefs _prefs;
    private readonly string?             _replacing;
    private readonly IVariableScope      _variables;

    private readonly TextBox         _name    = new() { MinWidth = 280 };
    private readonly KeyCaptureBox   _key     = new();
    private readonly TextBox         _action  = new() { MinWidth = 280, FontFamily = UiKit.Mono };
    private readonly TextBlock       _preview = UiKit.Text(string.Empty, wrap: true);
    private readonly TextBlock       _problem = UiKit.Text(string.Empty, wrap: true);
    private readonly RadioButton     _global  = new() { Content = "Everywhere: works in every program (a Windows hot key)", GroupName = "hk-scope", IsChecked = true };
    private readonly RadioButton     _app     = new() { Content = "Only while a Silo window is active", GroupName = "hk-scope" };
    private readonly RadioButton     _mine    = new() { Content = "Just for me", GroupName = "hk-storage", IsChecked = true };
    private readonly RadioButton     _shared  = new() { Content = "For everyone who uses this Silo", GroupName = "hk-storage" };
    private readonly CheckBox        _enabled = new() { Content = "On", IsChecked = true };
    private readonly Button          _ok      = new() { Content = "Save", Padding = new Thickness(16, 4, 16, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };

    private HotkeyDialog(Window? owner, GlobalShortcutPrefs prefs, ShortcutBinding initial, StoredScope storage, string? replacing, IVariableScope variables)
    {
        _prefs     = prefs;
        _replacing = replacing;
        _variables = variables;

        Title                 = replacing is null ? "Bind to a hotkey" : "Edit hotkey";
        SizeToContent         = SizeToContent.WidthAndHeight;
        MaxWidth              = 560;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        Owner                 = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        _name.Text         = initial.Name;
        _key.Value         = initial.Key;
        _action.Text       = initial.Action;
        _global.IsChecked  = initial.Scope == ShortcutScope.Global;
        _app.IsChecked     = initial.Scope == ShortcutScope.Application;
        _mine.IsChecked    = storage == StoredScope.Personal;
        _shared.IsChecked  = storage == StoredScope.Shared;
        _enabled.IsChecked = initial.Enabled;
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        void Add(string label, UIElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = UiKit.Text(label, muted: true);
            l.Margin = new Thickness(0, 6, 12, 6);
            l.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(l, row);
            grid.Children.Add(l);
            if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
            row++;
        }

        Add("Name", _name);
        Add("Key", _key);
        Add("Action", _action);
        Add(string.Empty, _preview);
        Add("Works", Stack(_global, _app));
        Add("Kept", Stack(_mine, _shared));
        Add(string.Empty, _enabled);
        Add(string.Empty, _problem);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_ok);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        buttons.Children.Add(cancel);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        _name.TextChanged   += (_, _) => Update();
        _action.TextChanged += (_, _) => Update();
        _key.ValueChanged   += (_, _) => Update();
        foreach (var r in new[] { _global, _app, _mine, _shared }) r.Checked += (_, _) => Update();
        _enabled.Click += (_, _) => Update();
        _ok.Click      += (_, _) =>
                          {
                              if (Update() is null) return;
                              DialogResult = true;
                          };
        Loaded += (_, _) => (_key.Value.IsSet ? _name : _key).Focus();
        Update();
    }

    private ShortcutBinding Binding { get; set; } = new();

    private StoredScope Storage => _shared.IsChecked == true ? StoredScope.Shared : StoredScope.Personal;

    private static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel();
        foreach (var i in items)
        {
            if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 2);
            p.Children.Add(i);
        }

        return p;
    }

    /// <summary>Reads the fields, shows what is wrong or what the action will do, and enables Save; null when it is not ready.</summary>
    private ShortcutBinding? Update()
    {
        var binding = new ShortcutBinding
                      {
                          Name    = _name.Text.Trim(),
                          Key     = _key.Value,
                          Action  = _action.Text.Trim(),
                          Scope   = _app.IsChecked == true ? ShortcutScope.Application : ShortcutScope.Global,
                          Enabled = _enabled.IsChecked == true
                      };
        Binding = binding;

        // what the action will do, now
        if (ShortcutActionText.TryParse(binding.Action, out var action, out string? actionProblem))
        {
            try
            {
                string text = action!.Template.Evaluate(_variables, new EvaluationOptions());
                _preview.Text = action.Verb == ShortcutVerbs.Type ? $"Types: {text}" : $"Copies: {text}";
            }
            catch (ExpressionException ex)
            {
                _preview.Text = ex.Message;
            }
        }
        else _preview.Text = actionProblem ?? string.Empty;

        bool ok = _prefs.Validate(binding, _replacing, out string? problem);
        _problem.Text = problem ?? string.Empty;
        _ok.IsEnabled = ok;
        return ok ? binding : null;
    }

    /// <summary>Shows the dialog (with the shortcuts switched off while it is up, so pressing an existing shortcut reaches the key box).</summary>
    /// <param name="replacing">The name of the shortcut being edited; null for a new one.</param>
    /// <param name="variables">What <c>${...}</c> in the action can see; the Silo's constants by default.</param>
    public static (ShortcutBinding Binding, StoredScope Storage)? Ask(Window? owner, GlobalShortcutPrefs prefs, ShortcutBinding initial, StoredScope storage,
                                                                      string? replacing, IVariableScope? variables = null)
    {
        var dialog = new HotkeyDialog(owner, prefs, initial, storage, replacing, variables ?? YourConstants.Default.AsScope());
        bool ok;
        using (GlobalHotkeyService.Current.Suspend()) ok = dialog.ShowDialog() == true;
        return ok && dialog.Update() is { } binding ? (binding, dialog.Storage) : null;
    }
}

/// <summary>A scope that knows a constant that is not saved yet, so the action of a new hotkey can be checked before anything is saved.</summary>
internal sealed class PendingConstantScope(IVariableScope inner, string name, string text) : IVariableScope
{
    public bool TryGetVariable(string? prefix, string variable, out object? value)
    {
        if (prefix == YourConstants.Prefix && variable.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = text;
            return true;
        }

        return inner.TryGetVariable(prefix, variable, out value);
    }
}
