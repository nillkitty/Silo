using System.IO;
using System.Windows.Controls;
using Silo.Providers.Data;
using Telefrag.DI;

namespace Silo.Ui.Windows;

/// <summary>
///     A small code-built modal prompt: a message, N buttons and an optional check box ("Don't ask me again"). WPF's
///     <c>MessageBox</c> has no check box, and these prompts need one. The first button is the default; the last one
///     answers Escape (so make it the "Cancel").
/// </summary>
public static class PromptDialog
{
    /// <summary>
    ///     Shows the prompt. Returns the index of the button pressed (the last button if the window is closed) and the
    ///     check box state.
    /// </summary>
    public static (int Button, bool Checked) Show(string title, string message, string[] buttons, string? checkBoxText = null)
    {
        var (button, isChecked, _) = ShowCore(title, message, buttons, checkBoxText, null);
        return (button, isChecked);
    }

    /// <summary>Asks for one line of text. Returns null if cancelled or left empty.</summary>
    public static string? Ask(string title, string message, string okText = "OK", string initial = "")
    {
        var (button, _, text) = ShowCore(title, message, [okText, "Cancel"], null, initial);
        return button == 0 && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    /// <summary>
    ///     The window the prompt belongs to: the one the person is working in (a tool can float in a window of its own,
    ///     possibly kept above the others,
    ///     and a prompt owned by the main window would open behind it), else the main window.
    /// </summary>
    private static Window? PromptOwner()
    {
        var app = Application.Current;
        if (app is null) return null;

        foreach (Window w in app.Windows)
            if (w.IsActive && w.IsVisible)
                return w;

        return app.MainWindow is { IsVisible: true } main ? main : null;
    }

    private static (int Button, bool Checked, string? Text) ShowCore(string  title, string message, string[] buttons, string? checkBoxText,
                                                                     string? input)
    {
        var chosen = buttons.Length - 1;
        var window = new Window
                     {
                         Title                 = title,
                         SizeToContent         = SizeToContent.WidthAndHeight,
                         MaxWidth              = 520,
                         ResizeMode            = ResizeMode.NoResize,
                         ShowInTaskbar         = false,
                         WindowStartupLocation = WindowStartupLocation.CenterOwner,
                         Owner                 = PromptOwner()
                     };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin     = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        TextBox? entry = null;
        if (input is not null)
        {
            entry = new TextBox { Text = input, MinWidth = 380, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(entry);
            window.Loaded += (_, _) =>
                             {
                                 entry.Focus();
                                 entry.SelectAll();
                             };
        }

        CheckBox? box = null;
        if (checkBoxText is not null)
        {
            box = new CheckBox { Content = checkBoxText, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(box);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button
                    {
                        Content   = buttons[i],
                        Padding   = new Thickness(14,             4, 14, 4),
                        Margin    = new Thickness(i == 0 ? 0 : 8, 0, 0,  0),
                        IsDefault = i == 0,
                        IsCancel  = i == buttons.Length - 1
                    };
            b.Click += (_, _) =>
                       {
                           chosen              = index;
                           window.DialogResult = true;
                       };
            row.Children.Add(b);
        }

        panel.Children.Add(row);
        window.Content = panel;
        window.ShowDialog();
        return (chosen, box?.IsChecked == true, entry?.Text);
    }
}

/// <summary>
///     Asks the person what to do when a Silo's file (or its remote copy) changed underneath it. Offers "Don't ask me
///     again", which the caller stores as the author-mode conflict setting (<see cref="Silo.Settings.SiloFileSettings" />
///     ).
/// </summary>
[Singleton(typeof(ISaveConflictResolver))]
public class SaveConflictPrompt : ISaveConflictResolver
{
    private static SaveConflictAnswer Ask(SaveConflict c)
    {
        string   title, message;
        string[] buttons;
        if (c.Kind == SaveConflictKind.FileChangedOnDisk)
        {
            title = "File changed";
            message = $"{Path.GetFileName(c.Location)} was changed by another program" +
                      (c.TheirsUtc is { } t ? $" (modified {t.ToLocalTime():g})" : "") + " after you opened it" +
                      (c.KnownUtc is { } k ? $" (it was last written {k.ToLocalTime():g})" : "") +
                      ".\n\nOverwrite replaces their version with yours. Save a copy keeps their version and saves yours next to it as a " +
                      "\"conflicted copy\", which you then keep working on.";
            buttons = ["Overwrite", "Save a copy", "Cancel"];
        }
        else
        {
            title = "Remote copy changed";
            message =
                $"{c.Location} was changed by someone else since you last synced.\n\nReplace remote uploads your version over theirs. " +
                "Keep mine here leaves the remote copy alone; your changes stay saved on this computer and are not uploaded.";
            buttons = ["Replace remote", "Keep mine here", "Cancel"];
        }

        var (button, remember) =
            PromptDialog.Show(title, message, buttons, "Don't ask me again (change this later in the Author design menu)");
        var choice = button switch
                     {
                         0 => SaveConflictChoice.Overwrite,
                         1 => SaveConflictChoice.SaveCopy,
                         _ => SaveConflictChoice.Cancel
                     };
        return new SaveConflictAnswer(choice, remember);
    }

    public Task<SaveConflictAnswer> ResolveAsync(SaveConflict conflict)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() => Ask(conflict)).Task;
    }
}