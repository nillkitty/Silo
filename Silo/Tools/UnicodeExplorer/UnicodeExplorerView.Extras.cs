using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Controls;
using Microsoft.Win32;
using Silo.Providers.Format;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>The Edit &gt; Preferences form, the Palette menu (Author level and up) and the Developer menu (Developer level and up).</summary>
public sealed partial class UnicodeExplorerView
{
    // ---- Preferences ---------------------------------------------------------------------------------------------

    private void ShowPreferences()
    {
        var scripts = _db?.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var form    = new FormDialog(DialogOwner, "Preferences", "Kept in your Silo.");

        FormDialog.ChoiceRow? script = null;
        if (scripts is not null)
        {
            string automatic = $"Automatic: {FilterLabels.Script(_db, ScriptDefaults.ForCulture(CultureInfo.CurrentUICulture))} (your display language)";
            var    names     = new List<string> { automatic };
            names.AddRange(scripts.Select(s => s.Name));
            int selected = _settings.DefaultScript is { } kept ? Math.Max(0, scripts.FindIndex(s => s.Code == kept) + 1) : 0;
            script = form.AddChoice("Default script", names, selected, "What Script > Default Script lists");
        }

        var color = form.AddChoice("Color applies to", ["Glyph", "Background", "Accent"], (int)_settings.ColorMode, "How a character marked with a color shows it");
        var types = form.AddCheck("A click in a palette types the character", _settings.PaletteTypes, "Otherwise it is only copied: paste it yourself");
        if (!form.Ask()) return;

        if (script is not null && scripts is not null) _settings.DefaultScript = script.Index == 0 ? null : scripts[script.Index - 1].Code;
        _settings.PaletteTypes = types.Value;
        SetColorMode((ColorMode)color.Index); // saves
        SaveSettings();
        Say("Preferences saved.");
    }

    // ---- Palette -------------------------------------------------------------------------------------------------

    private IEnumerable<Control> PaletteItems()
    {
        var service = PaletteService.Current;
        service.Start();
        bool anyTray = _tray.Count > 0;

        yield return UiKit.Item("New Palette…", NewPalette, tip: "An empty palette window; right-click it to add characters");
        yield return UiKit.Item("Create Palette from Collection…", CreatePaletteFromCollection, anyTray, "A palette window with the characters of the collection (the selected ones, or all)");
        yield return UiKit.Rule();

        var open = new List<Control>();
        foreach (var p in service.All)
        {
            var palette = p;
            open.Add(Check($"{palette.Name} ({palette.AllCodePoints().Count():N0})", service.IsOpen(palette), () =>
                                                                                                    {
                                                                                                        service.Select(palette);
                                                                                                        if (service.IsOpen(palette)) service.Close(palette);
                                                                                                        else service.Open(palette);
                                                                                                    }));
        }

        if (open.Count == 0) open.Add(new MenuItem { Header = "No palettes yet", IsEnabled = false });
        yield return Sub("Show Palette", open);

        var targets = DevTargets();
        var add     = new List<Control>();
        foreach (var p in service.All)
        {
            var palette = p;
            add.Add(UiKit.Item(palette.Name, () =>
                                             {
                                                 service.Add(palette, targets);
                                                 Say($"Added {targets.Count:N0} character{(targets.Count == 1 ? "" : "s")} to {palette.Name}.");
                                             }, targets.Count > 0));
        }

        if (add.Count == 0) add.Add(new MenuItem { Header = "No palettes yet", IsEnabled = false });
        yield return Sub("Add Selected Characters to", add, targets.Count > 0);
        yield return UiKit.Rule();

        var current = service.Selected;
        string name = current is null ? string.Empty : $" \"{current.Name}\"";
        yield return UiKit.Item($"Rename Palette{name}…", () => service.Rename(current!, DialogOwner), current is not null);
        yield return Check("Always on Top", current?.AlwaysOnTop == true, () => ChangePalette(current!, p => p.AlwaysOnTop = !p.AlwaysOnTop), current is not null);
        yield return Check("Keep on-screen", current?.KeepOnScreen == true, () => ChangePalette(current!, p => p.KeepOnScreen = !p.KeepOnScreen), current is not null,
                           "Move the window back when it would be off the screen");
        yield return Check("Show on repo load", current?.ShowOnLoad == true, () => ChangePalette(current!, p => p.ShowOnLoad = !p.ShowOnLoad), current is not null,
                           "Open this palette whenever this Silo is loaded");
        yield return UiKit.Rule();
        yield return UiKit.Item($"Delete Palette{name}…", () => service.Delete(current!, DialogOwner), current is not null);
    }

    private static void ChangePalette(PaletteDefinition palette, Action<PaletteDefinition> change)
    {
        change(palette);
        PaletteService.Current.Save();
        if (PaletteService.Current.IsOpen(palette))
        {
            // the window shows the choices that are visual: close and open it again at the same place
            PaletteService.Current.Close(palette);
            PaletteService.Current.Open(palette);
        }
    }

    private void NewPalette()
    {
        string? name = PromptDialog.Ask("New palette", "What is the palette called?", "Create", "Palette");
        if (name is null) return;

        PaletteService.Current.Start();
        PaletteService.Current.Create(name, []);
    }

    private void CreatePaletteFromCollection()
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        string? name = PromptDialog.Ask("Create palette", $"A palette of {items.Count:N0} character{(items.Count == 1 ? "" : "s")}. What is it called?", "Create", "Palette");
        if (name is null) return;

        PaletteService.Current.Start();
        PaletteService.Current.Create(name, items.Select(c => c.CodePoint.Value));
    }

    // ---- Developer -----------------------------------------------------------------------------------------------

    /// <summary>What the Developer menu acts on: the selected results, else the character shown in the details.</summary>
    private IReadOnlyList<int> DevTargets()
    {
        if (_sel.Count > 0) return SelectedCodePoints();
        return Inspected is { } shown ? [shown.Value] : [];
    }

    private IEnumerable<Control> DeveloperItems()
    {
        bool any = DevTargets().Count > 0;
        string what = _sel.Count > 1 ? "selected characters" : "the character shown";

        yield return UiKit.Item("Copy Codepoint", () => CopyAs("code point", cp => cp.Hex), any, $"U+XXXX of {what}");
        yield return UiKit.Item("Copy Alt Code", () => CopyAs("Alt code", CharacterCopyFormats.AltCode), any, "The keys that type it in Windows");
        yield return UiKit.Item("Copy as C/C#/C++ Escaped Char ('\\u1017')", () => CopyAs("escaped character", CharacterCopyFormats.CEscape), any, "A char literal; above U+FFFF a string with \\U and eight digits");
        yield return UiKit.Item("Copy as XML/HTML Entity", () => CopyAs("entity", CharacterCopyFormats.Entity, ""), any);
        yield return UiKit.Item("Copy as PowerShell", () => CopyAs("PowerShell", CharacterCopyFormats.PowerShell), any);
        yield return UiKit.Item("Copy as VB", () => CopyAs("Visual Basic", CharacterCopyFormats.VisualBasic), any);
        yield return UiKit.Item("Copy as colored HTML", () => CopyAs("colored HTML", cp => CharacterCopyFormats.ColoredHtml(cp, _colors.ColorOf(cp.Value)?.Hex, _settings.ColorMode), ""), any,
                                "A span with the character's mark color, shown the way View > Color Applies to says");
        yield return UiKit.Item("Copy as colored XAML", () => CopyAs("colored XAML", cp => CharacterCopyFormats.ColoredXaml(cp, _colors.ColorOf(cp.Value)?.Hex, _settings.ColorMode), "\n"), any);
        yield return UiKit.Rule();
        yield return UiKit.Item("Dump collection glyphs to file…", DumpCollectionGlyphs, _tray.Count > 0, "A text file with a line per character: code point, glyph, name");
        yield return UiKit.Rule();
        yield return UiKit.Item("Copy Glyphs All in Collection", CopyGlyphsInCollection, _tray.Count > 0, "The glyphs of the whole collection, as shown (control characters as their pictures), not the raw characters");
    }

    private void CopyAs(string what, Func<CodePoint, string> format, string separator = " ")
    {
        var targets = DevTargets();
        if (targets.Count == 0)
        {
            Say("Select a character first.");
            return;
        }

        Copy(string.Join(separator, targets.Select(cp => format(new CodePoint(cp)))), what);
    }

    private void DumpCollectionGlyphs()
    {
        if (_tray.Count == 0) return;

        var textFormat = Formats.Of<PlainTextFormat>();
        var dialog     = new SaveFileDialog { Title = "Dump the collection's glyphs", Filter = textFormat.Filter(), FileName = "glyphs", DefaultExt = textFormat.DefaultExtension(), AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;

        var text = new StringBuilder();
        foreach (var item in _tray)
        {
            var info = _db?.Get(item.CodePoint);
            text.Append(item.CodePoint.Hex).Append('\t').Append(info?.Glyph ?? item.CodePoint.Text).Append('\t').Append(info?.Name ?? string.Empty).Append("\r\n");
        }

        try
        {
            File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
            Say($"Wrote {_tray.Count:N0} lines to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PromptDialog.Show("Dump glyphs", "The file could not be written: " + ex.Message, ["OK"]);
        }
    }

    private void CopyGlyphsInCollection()
    {
        if (_tray.Count == 0) return;

        Copy(string.Concat(_tray.Select(c => _db?.Get(c.CodePoint).Glyph ?? c.CodePoint.Text)), $"{_tray.Count:N0} glyphs");
    }
}
