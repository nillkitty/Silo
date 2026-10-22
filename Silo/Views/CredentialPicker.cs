using System.Windows.Controls;
using Serilog;
using Silo.Credentials;
using Silo.Model;
using Silo.Providers.Crypto;
using Silo.Ui.Windows;

namespace Silo.Views;

/// <summary>
///     Choose one of the Silo's saved credentials, or make a new one or edit the chosen one without leaving the page. For
///     a connector's
///     setup page (a connector that is <see cref="IAuthenticatable" />): the chosen credential goes to the connection in
///     the
///     <see cref="ConnectionRequest.Metadata" /> of its request. It holds only the choice; the credential is opened when
///     <see cref="OpenSelected" /> is called.
/// </summary>
public sealed class CredentialPicker : UserControl
{
    private readonly ComboBox _combo;
    private readonly Button   _edit;
    private readonly Button   _new;

    private readonly OpenSilo? _silo;
    private          bool      _filling;

    /// <summary>False when the Silo cannot keep credentials; the picker then shows nothing to choose.</summary>
    public bool IsAvailable => _silo?.Credentials is not null;

    /// <summary>True when the connector cannot connect without a credential, so "(none)" is not a choice.</summary>
    public bool Required { get; }

    /// <summary>The id of the chosen credential; null when none is chosen.</summary>
    public int? SelectedId => (_combo.SelectedItem as Entry)?.Id;

    public event EventHandler? SelectionChanged;

    /// <summary>The chosen credential with its password; null if none is chosen.</summary>
    /// <exception cref="SecretException">It cannot be opened here.</exception>
    public Credential? OpenSelected()
    {
        return SelectedId is { } id && _silo?.Credentials is { } service ? service.Get(id) : null;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => Fill(SelectedId)));
    }

    private void Prompt(int? editId)
    {
        if (_silo is null) return;

        if (CredentialWindow.Prompt(_silo, editId) is { } saved) Fill(saved.Id);
    }

    private void Fill(int? select)
    {
        var entries = new List<Entry>();
        if (!Required) entries.Add(new Entry(null, "(none)"));

        try
        {
            if (_silo?.Credentials is { } service)
                entries.AddRange(service.List().Select(c => new Entry(c.Id, c.Account is { Length: > 0 } a ? $"{c.Name}  ({a})" : c.Name)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The credentials could not be listed");
        }

        _filling = true;
        try
        {
            _combo.ItemsSource  = entries;
            _combo.SelectedItem = entries.FirstOrDefault(e => e.Id == select) ?? (Required ? null : entries.FirstOrDefault());
            _combo.IsEnabled    = IsAvailable;
        }
        finally
        {
            _filling = false;
        }

        UpdateButtons();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtons()
    {
        _new.IsEnabled  = IsAvailable;
        _edit.IsEnabled = IsAvailable && SelectedId is not null;
    }

    public CredentialPicker(OpenSilo? silo, bool required)
    {
        _silo    = silo;
        Required = required;

        _combo = new ComboBox { MinWidth = 220, DisplayMemberPath = nameof(Entry.Text), VerticalAlignment = VerticalAlignment.Center };
        _new   = new Button { Content    = "New...", Padding      = new Thickness(10, 2, 10, 2), Margin   = new Thickness(8, 0, 0, 0) };
        _edit  = new Button { Content    = "Edit...", Padding     = new Thickness(10, 2, 10, 2), Margin   = new Thickness(6, 0, 0, 0) };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_combo);
        row.Children.Add(_new);
        row.Children.Add(_edit);
        Content = row;

        _combo.SelectionChanged += (_, _) =>
                                   {
                                       if (_filling) return;

                                       UpdateButtons();
                                       SelectionChanged?.Invoke(this, EventArgs.Empty);
                                   };
        _new.Click  += (_, _) => Prompt(null);
        _edit.Click += (_, _) => Prompt(SelectedId);

        Fill(null);

        Loaded   += (_, _) => CredentialService.Changed += OnChanged;
        Unloaded += (_, _) => CredentialService.Changed -= OnChanged;
    }

    private sealed record Entry(int? Id, string Text);
}