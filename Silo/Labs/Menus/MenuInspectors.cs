using System.Windows.Controls;
using System.Windows.Input;
using Silo.Labs.Web;

namespace Silo.Labs.Menus;

/// <summary>The menu as JSON, which is what the menu is made from: change it here and press Apply (or Ctrl+Enter) to change the menu.</summary>
internal sealed class MenuJsonInspector(MenuLabSession session) : LiveInspector(session)
{
    private TextBox?   _text;
    private TextBlock? _error;

    public override string Id => "json";

    public override string Title => "JSON";

    public override string IconKey => "Coding";

    protected override UIElement Create()
    {
        _text = LabUi.Memo(string.Empty, _ => { });
        _text.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) Apply();
                         };
        _error = LabUi.Label(string.Empty, wrap: true);
        _error.Foreground = LabUi.Red;
        _error.Margin     = new Thickness(8, 0, 8, 4);
        var bar = LabUi.Bar(LabUi.Button("Apply", Apply, "Make the menu of this JSON (Ctrl+Enter)"), LabUi.Button("Revert", () =>
                                                                                                                               {
                                                                                                                                   _text.Text = session.Json;
                                                                                                                                   _error.Text = string.Empty;
                                                                                                                               }, "Go back to the JSON of the menu as it is"),
                            LabUi.Button("Copy", () => LabUi.Copy(session.Json), "Put the menu's JSON on the clipboard"));
        var head = new StackPanel();
        head.Children.Add(bar);
        head.Children.Add(_error);
        return LabUi.Layout(head, _text);
    }

    private void Apply()
    {
        if (_text is null || _error is null) return;

        _error.Text = session.ApplyJson(_text.Text) ?? string.Empty;
    }

    protected override void Refresh()
    {
        // what is typed here is not overwritten while the box has the keyboard
        if (_text is null || _text.IsKeyboardFocusWithin) return;

        string json = session.Json;
        if (_text.Text != json)
        {
            _text.Text = json;
            if (_error is not null) _error.Text = string.Empty;
        }
    }
}

/// <summary>The menu as XAML: one resource, to copy into a resource dictionary.</summary>
internal sealed class MenuXamlInspector(MenuLabSession session) : LiveInspector(session)
{
    private TextBox? _text;

    public override string Id => "xaml";

    public override string Title => "XAML";

    public override string IconKey => "Coding";

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        var bar = LabUi.Bar(LabUi.Button("Copy", () => LabUi.Copy(session.Xaml), "Put the XAML on the clipboard"), LabUi.Label("The Lab's settings choose whether it is a whole resource dictionary.", muted: true));
        return LabUi.Layout(bar, _text);
    }

    protected override void Refresh()
    {
        if (_text is not null) _text.Text = session.Xaml;
    }
}
