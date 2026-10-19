using System.Windows.Controls;
using Silo.Hotkeys;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>Lists what the person has saved (<c>your:</c> constants and hotkeys) in a window of its own; the same list as the explorer's "Saved" tab.</summary>
internal sealed class ShortcutManagerWindow : Window
{
    private static ShortcutManagerWindow? _open;

    private readonly SavedItemsPanel _panel;
    private readonly TextBlock       _status = UiKit.Text(string.Empty, muted: true, wrap: true);

    private ShortcutManagerWindow(Window? owner, Action<string, IReadOnlyList<CodePoint>> collect)
    {
        Title                 = "Constants and hotkeys";
        Width                 = 760;
        Height                = 520;
        MinWidth              = 520;
        MinHeight             = 300;
        ShowInTaskbar         = false;
        Owner                 = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        _panel = new SavedItemsPanel(() => this, collect, null, message => _status.Text = message);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_panel);

        _status.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Content = root;

        Loaded += (_, _) => _panel.Attach();
        Closed += (_, _) =>
                  {
                      _panel.Detach();
                      if (ReferenceEquals(_open, this)) _open = null;
                  };
    }

    /// <summary>Shows the window (the one that is open already is brought forward).</summary>
    /// <param name="owner">The window it belongs to.</param>
    /// <param name="collect">Puts characters in the explorer's collection: what they are called, and the characters.</param>
    public static void ShowFor(Window? owner, Action<string, IReadOnlyList<CodePoint>> collect)
    {
        if (_open is { } open)
        {
            open.Activate();
            return;
        }

        _open = new ShortcutManagerWindow(owner, collect);
        _open.Show();
    }
}
