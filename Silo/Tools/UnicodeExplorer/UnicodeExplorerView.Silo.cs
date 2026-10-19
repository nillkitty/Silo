using System.IO;
using System.Windows.Controls;
using Silo.Contracts.Providers.Data;
using Silo.Expressions;
using Silo.Hotkeys;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>The part of the explorer that keeps things in the Silo: characters as <c>your:</c> constants, and hotkeys that type them.</summary>
public sealed partial class UnicodeExplorerView
{
    private bool _siloHooked;

    private void HookSilo()
    {
        if (_siloHooked) return;
        _siloHooked = true;

        GlobalHotkeyService.Current.Start(); // also tells Stored where the active Silo is
        if (App.Instance is { } app) app.ActiveSiloChanged += OnActiveSiloChanged;
        LoadTray();
        _saved.Attach();
    }

    private void UnhookSilo()
    {
        if (!_siloHooked) return;
        _siloHooked = false;
        _saved.Detach();
        if (App.Instance is { } app) app.ActiveSiloChanged -= OnActiveSiloChanged;
    }

    private void OnActiveSiloChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
                               {
                                   LoadAlwaysOnTopChoice();
                                   ApplyAlwaysOnTop();
                                   LoadTray();
                                   _saved.RefreshSoon();
                               });
    }

    private Window? DialogOwner => FloatingWindow ?? Window.GetWindow(this);

    // ---- the Silo menu -------------------------------------------------------------------------------------------

    private IEnumerable<Control> SiloMenuItems()
    {
        bool any = TrayTarget().Count > 0;
        string which = _traySel.Count > 0 ? "selected" : "all";
        yield return UiKit.Item($"Save chars… ({which})", SaveChars, any, "Keep the characters together as one constant, like your:mychars");
        yield return UiKit.Item("Save as Global Constant…", SaveAsConstants, any, "Keep a character as a constant of its own, like your:green (a labelled character is saved under its label)");
        yield return UiKit.Item("Bind to global hotkey…", BindHotkey, any, "Choose a key that types the characters into whatever you are working in");
        yield return UiKit.Rule();
        yield return UiKit.Item("Show what I saved", ShowSaved, tip: "Switch to the Saved tab: your characters, strings and hotkeys");
        yield return UiKit.Item("Manage constants and hotkeys…", ManageSilo, tip: "The same list in a window of its own");
    }

    private bool EnsureSilo()
    {
        if (!YourConstants.Default.IsAvailable)
        {
            PromptDialog.Show("Silo", "Open a Silo first: your constants and hotkeys are kept in it.", ["OK"]);
            return false;
        }

        if (App.Instance?.ActiveSilo is { IsWritable: false })
        {
            PromptDialog.Show("Silo", "The Silo that is open cannot be changed.", ["OK"]);
            return false;
        }

        return true;
    }

    private static bool Try(Func<string> action, string failTitle)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or StoredDataException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            PromptDialog.Show(failTitle, ex.Message, ["OK"]);
            return false;
        }
    }

    /// <summary>A name to offer for a constant: the person's label for the character, else its Unicode name written like a variable (<c>large_green_circle</c>).</summary>
    private string SuggestName(IReadOnlyList<CollectedCharacter> items)
    {
        if (items.Count != 1) return "chars";

        if (items[0].Label is { } label && YourConstants.IsValidName(label)) return label;

        string name = _db?.Get(items[0].CodePoint).Name ?? string.Empty;
        string slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        if (slug.Length > 0 && char.IsAsciiDigit(slug[0])) slug = "_" + slug;
        return YourConstants.IsValidName(slug) ? slug : "chars";
    }

    /// <summary>Asks what to call a constant; null if cancelled. A constant that exists with other contents is only replaced when the person agrees.</summary>
    private string? AskConstantName(string title, string intro, string suggestion, string newText)
    {
        string initial = suggestion;
        while (true)
        {
            string? name = PromptDialog.Ask(title, intro + "\n\nName (letters, digits and _, like green or my_chars):", "Next", initial);
            if (name is null) return null;

            name = name.Trim();
            if (name.StartsWith(YourConstants.Prefix + ":", StringComparison.OrdinalIgnoreCase)) name = name[(YourConstants.Prefix.Length + 1)..];
            if (!YourConstants.IsValidName(name))
            {
                PromptDialog.Show(title, $"\"{name}\" cannot be a constant's name: use letters, digits and _, not starting with a digit, up to 64 characters.", ["OK"]);
                initial = name;
                continue;
            }

            var existing = YourConstants.Default.Get(name);
            if (existing is null || existing.Text == newText) return name;

            var (button, _) = PromptDialog.Show(title, $"your:{name} is already {Shorten(existing.Text, 40)}. Replace it with {Shorten(newText, 40)}?", ["Replace", "Choose another name", "Cancel"]);
            if (button == 0) return name;
            if (button == 2 || button < 0) return null;
            initial = name;
        }
    }

    // ---- constants -----------------------------------------------------------------------------------------------

    /// <summary>"Save chars": the characters as one list constant, <c>your:mychars = '▶√®™▼▽'</c>.</summary>
    private void SaveChars()
    {
        var items = TrayTarget();
        if (items.Count == 0 || !EnsureSilo()) return;

        string text = CodePoint.ToText(items.Select(c => c.CodePoint));
        string? name = AskConstantName("Save chars", $"Keep {items.Count:N0} character{(items.Count == 1 ? "" : "s")} ({Shorten(text, 30)}) as one constant in your Silo.", SuggestName(items), text);
        if (name is null) return;

        if (Try(() =>
                {
                    YourConstants.Default.SetCodePoints(name, items.Select(c => c.CodePoint));
                    return name;
                }, "Save chars"))
            Say($"Saved your:{name} = '{Shorten(text, 40)}'");
    }

    /// <summary>"Save as Global Constant": one character as <c>your:green = '🟢'</c>; several labelled characters as one constant each, named by their labels.</summary>
    private void SaveAsConstants()
    {
        var items = TrayTarget();
        if (items.Count == 0 || !EnsureSilo()) return;

        if (items.Count == 1)
        {
            var one  = items[0];
            string? name = AskConstantName("Save as Global Constant", $"Keep {one.CodePoint.Hex} {one.CodePoint.Text} as a constant in your Silo, to use as your:name in expressions and hotkeys.", SuggestName(items), one.CodePoint.Text);
            if (name is null) return;

            if (Try(() =>
                    {
                        YourConstants.Default.SetCodePoint(name, one.CodePoint);
                        return name;
                    }, "Save as Global Constant"))
                Say($"Saved your:{name} = '{one.CodePoint.Text}'");
            return;
        }

        var named = items.Where(i => YourConstants.IsValidName(i.Label)).ToList();
        if (named.Count == 0)
        {
            PromptDialog.Show("Save as Global Constant",
                              "Give each character a name first: double-click it in the collection and type a label such as green or blue. Each one is then saved as a constant of that name.\n\nTo keep them together as one list instead, use Save chars.",
                              ["OK"]);
            return;
        }

        var repeated = named.GroupBy(i => i.Label!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
        {
            PromptDialog.Show("Save as Global Constant", $"Two characters are labelled \"{repeated.Key}\". Give each its own label.", ["OK"]);
            return;
        }

        var replacing = named.Where(i => YourConstants.Default.Get(i.Label!) is { } old && old.Text != i.CodePoint.Text).Select(i => "your:" + i.Label).ToList();
        string message = $"Save {named.Count} constants ({string.Join(", ", named.Take(6).Select(i => "your:" + i.Label))}{(named.Count > 6 ? ", …" : string.Empty)})?"
                         + (named.Count < items.Count ? $"\n\n{items.Count - named.Count} character{(items.Count - named.Count == 1 ? " has" : "s have")} no usable label and will be skipped." : string.Empty)
                         + (replacing.Count > 0 ? $"\n\nThese exist with other contents and will be replaced: {string.Join(", ", replacing)}." : string.Empty);
        var (button, _) = PromptDialog.Show("Save as Global Constant", message, ["Save", "Cancel"]);
        if (button != 0) return;

        if (Try(() =>
                {
                    foreach (var i in named) YourConstants.Default.SetCodePoint(i.Label!, i.CodePoint);
                    return string.Empty;
                }, "Save as Global Constant"))
            Say($"Saved {named.Count} constants: {string.Join(", ", named.Take(6).Select(i => "your:" + i.Label))}{(named.Count > 6 ? ", …" : string.Empty)}");
    }

    // ---- hotkeys -------------------------------------------------------------------------------------------------

    /// <summary>"Bind to global hotkey": a key that types the constant, <c>Ctrl+Win+J = type ${your:blue}</c>.</summary>
    private void BindHotkey()
    {
        var items = TrayTarget();
        if (items.Count == 0 || !EnsureSilo()) return;

        string text = CodePoint.ToText(items.Select(c => c.CodePoint));
        string? name = AskConstantName("Bind to hotkey", $"The hotkey types {Shorten(text, 30)}. It is kept as a constant in your Silo, so it can be changed later without touching the hotkey.", SuggestName(items), text);
        if (name is null) return;

        var prefs   = GlobalHotkeyService.Current.Prefs;
        var initial = new ShortcutBinding { Name = NewBindingName(prefs, name), Action = $"type ${{{YourConstants.Prefix}:{name}}}", Scope = ShortcutScope.Global };
        var scope   = new PendingConstantScope(YourConstants.Default.AsScope(), name, text);

        if (HotkeyDialog.Ask(DialogOwner, prefs, initial, StoredScope.Personal, null, scope) is not { } chosen) return;
        var (binding, storage) = chosen;

        if (Try(() =>
                {
                    // the constant first: the hotkey's action refers to it
                    if (items.Count == 1) YourConstants.Default.SetCodePoint(name, items[0].CodePoint);
                    else YourConstants.Default.SetCodePoints(name, items.Select(c => c.CodePoint));
                    prefs.Save(binding, storage);
                    return name;
                }, "Bind to hotkey"))
        {
            string why = GlobalHotkeyService.Current.Failures.TryGetValue(binding.Name, out string? problem) ? " But " + problem : string.Empty;
            Say($"{binding.Key} now runs {binding.Action}.{why}");
        }
    }

    private static string NewBindingName(GlobalShortcutPrefs prefs, string wanted)
    {
        if (prefs.Find(wanted) is null) return wanted;
        for (int i = 2; ; i++)
            if (prefs.Find(wanted + i) is null) return wanted + i;
    }

    private void ManageSilo()
    {
        if (!YourConstants.Default.IsAvailable)
        {
            PromptDialog.Show("Silo", "Open a Silo first: your constants and hotkeys are kept in it.", ["OK"]);
            return;
        }

        ShortcutManagerWindow.ShowFor(DialogOwner, CollectSaved);
    }

    // ---- the Saved tab ---------------------------------------------------------------------------------------------

    private SavedItemsPanel BuildSaved()
    {
        var panel = new SavedItemsPanel(() => DialogOwner, CollectSaved, text => LookUp(text), Say);
        panel.CountChanged += (_, _) => _savedTab.Header = panel.Count == 0 ? "Saved" : $"Saved ({panel.Count:N0})";
        return panel;
    }

    private void ShowSaved() => _bottomTabs.SelectedItem = _savedTab;

    /// <summary>Puts what was saved in the collection, and shows the collection.</summary>
    private void CollectSaved(string what, IReadOnlyList<CodePoint> codePoints)
    {
        _bottomTabs.SelectedItem = _collectionTab;
        AddMany(codePoints.Select(c => c.Value), what);
    }
}
