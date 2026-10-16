using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Credentials;
using Silo.Crypto;
using Silo.Model;

namespace Silo.Ui.Windows;

/// <summary>
///     The New Credential / Edit Credential dialog (modal): a name, what kind of credential it is (its realm: which of the
///     user name, domain and password it uses), those fields, and notes. It saves
///     the credential
///     itself (sealed by the Silo's encryption) when Save is pressed, and says what is wrong in the dialog if it cannot.
/// </summary>
/// <remarks>
///     The password is hidden. Showing it, or copying it, asks first when it is one that was saved earlier and has not
///     been changed
///     here; a copied password is taken off the clipboard again after <see cref="ClipboardSeconds" /> seconds (if it is
///     still the thing on it).
/// </remarks>
public sealed class CredentialWindow : Window
{
    public const     int ClipboardSeconds = 30;
    private readonly Button _copy = new() { Content = "Copy", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Credential _credential;
    private readonly TextBox _domain = new();
    private readonly ComboBox _realm = new();
    private readonly bool _isNew;

    private readonly TextBlock _message = new()
                                          {
                                              TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                                              Visibility   = Visibility.Collapsed
                                          };

    private readonly TextBox _name = new();

    private readonly TextBox _notes = new()
                                      {
                                          AcceptsReturn               = true, TextWrapping = TextWrapping.Wrap, Height = 72,
                                          VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                                      };

    private readonly PasswordBox _password = new();
    private readonly TextBox     _plain    = new() { Visibility = Visibility.Collapsed };
    private readonly string      _savedPassword;

    private readonly CredentialService _service;

    private readonly CheckBox _show = new()
                                      {
                                          Content = "Show", VerticalAlignment = VerticalAlignment.Center,
                                          Margin  = new Thickness(8, 0, 0, 0)
                                      };

    private readonly Label   _secretLabel = new() { Content = "_Password" };
    private readonly TextBox _user = new();
    private          bool    _revealAllowed;

    /// <summary>The credential as it was saved, once the dialog was closed with Save.</summary>
    public Credential? Saved { get; private set; }

    // ---- the password ----------------------------------------------------------------------------

    private string CurrentPassword => _plain.Visibility == Visibility.Visible ? _plain.Text : _password.Password;

    /// <summary>A password saved earlier, not changed here: showing or copying it needs a yes.</summary>
    private bool NeedsConfirmation => !_isNew && !_revealAllowed && _savedPassword.Length > 0 && CurrentPassword == _savedPassword;

    /// <summary>
    ///     Shows the dialog. With no <paramref name="editId" /> it makes a new credential; with one it edits that credential.
    ///     Returns the credential as saved, or null when cancelled (or when it could not be opened).
    /// </summary>
    public static Credential? Prompt(OpenSilo silo, int? editId = null, string? suggestedName = null)
    {
        if (silo.Credentials is not { } service)
        {
            MessageBox.Show("This Silo's storage cannot keep credentials.", "Credentials", MessageBoxButton.OK,
                            MessageBoxImage.Information);
            return null;
        }

        Credential credential;
        try
        {
            credential = editId is { } id
                             ? service.Get(id) ?? throw new KeyNotFoundException("That credential is no longer in the Silo.")
                             : new Credential { Name = suggestedName ?? string.Empty };
        }
        catch (Exception ex) when (ex is SecretException or KeyNotFoundException)
        {
            MessageBox.Show(ex.Message, "Credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var window = new CredentialWindow(service, credential, editId is null, ProtectorName(service));
        return window.ShowDialog() == true ? window.Saved : null;
    }

    /// <summary>What to call the protector that locks the Silo's secrets: its display name, else its id.</summary>
    public static string ProtectorName(CredentialService service)
    {
        string id = service.ProviderId;
        return SecretProtectors.Available().FirstOrDefault(p => p.Id == id)?.DisplayName ?? id;
    }

    private bool Confirm(string what)
    {
        if (!NeedsConfirmation) return true;

        var (button, _) = PromptDialog.Show(what + " password",
                                            $"{what} the saved password of \"{_credential.Name}\"? Anyone looking at the screen will see it.",
                                            [what, "Cancel"]);
        if (button != 0) return false;

        _revealAllowed = true;
        return true;
    }

    private void SetShown(bool shown)
    {
        if (shown)
        {
            if (!Confirm("Show"))
            {
                _show.IsChecked = false;
                return;
            }

            _plain.Text          = _password.Password;
            _plain.Visibility    = Visibility.Visible;
            _password.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (_plain.Visibility != Visibility.Visible) return; // the Show box was put back after a refusal: nothing was shown

            _password.Password = _plain.Text;
            _plain.Clear();
            _plain.Visibility    = Visibility.Collapsed;
            _password.Visibility = Visibility.Visible;
        }
    }

    private void Copy()
    {
        string text = CurrentPassword;
        if (text.Length == 0 || !Confirm("Copy")) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            ShowMessage("The clipboard is in use by another program; try again.", true);
            return;
        }

        ShowMessage($"Copied. It is removed from the clipboard in {ClipboardSeconds} seconds.", false);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ClipboardSeconds) };
        timer.Tick += (_, _) =>
                      {
                          timer.Stop();
                          try
                          {
                              if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear();
                          }
                          catch (ExternalException)
                          {
                              // somebody else has the clipboard: leave it
                          }
                      };
        timer.Start();
    }

    // ---- save ------------------------------------------------------------------------------------

    private void Save()
    {
        var c = _credential.Clone();
        var realm = SelectedRealm;
        var uses  = CredentialRules.Uses(realm);
        c.Name     = _name.Text.Trim();
        c.Realm    = realm;
        c.Username = uses.UserName ? Clean(_user.Text) : null; // what the realm does not use is not kept
        c.Domain   = uses.Domain ? Clean(_domain.Text) : null;
        c.Password = !uses.Secret || CurrentPassword.Length == 0 ? null : CurrentPassword;
        c.Notes    = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text;

        if (_service.Validate(c) is { } problem)
        {
            ShowMessage(problem, true);
            return;
        }

        try
        {
            Saved        = _service.Save(c);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is SecretException or InvalidOperationException or KeyNotFoundException)
        {
            ShowMessage(ex.Message, true);
        }
    }

    private AuthRealm SelectedRealm => _realm.SelectedValue is AuthRealm r ? r : AuthRealm.Conventional;

    /// <summary>Enables the boxes the realm signs in with and leaves the others alone (their text is kept until Save in case the person changes their mind).</summary>
    private void ApplyRealm()
    {
        var uses = CredentialRules.Uses(SelectedRealm);
        _user.IsEnabled     = uses.UserName;
        _domain.IsEnabled   = uses.Domain;
        _password.IsEnabled = uses.Secret;
        _plain.IsEnabled    = uses.Secret;
        _show.IsEnabled     = uses.Secret;
        _copy.IsEnabled     = uses.Secret;
        _secretLabel.Content = "_" + char.ToUpperInvariant(CredentialRules.SecretLabel(SelectedRealm)[0]) + CredentialRules.SecretLabel(SelectedRealm)[1..];
    }

    private static string? Clean(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private void ShowMessage(string text, bool isProblem)
    {
        _message.Text       = text;
        _message.Visibility = Visibility.Visible;
        _message.Opacity    = isProblem ? 1 : 0.7;
        if (isProblem)
            _message.Foreground = Brushes.IndianRed;
        else
            _message.ClearValue(TextBlock.ForegroundProperty);
    }

    private static void AddRow(Grid grid, int row, string label, UIElement field)
    {
        AddRow(grid, row, new Label { Content = label }, field);
    }

    private static void AddRow(Grid grid, int row, Label text, UIElement field)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        text.Target            = field is Panel ? null : field;
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Padding           = new Thickness(0, 3, 12, 0);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);

        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);

        grid.Children.Add(text);
        grid.Children.Add(field);
    }

    private CredentialWindow(CredentialService service, Credential credential, bool isNew, string protectedBy)
    {
        _service       = service;
        _credential    = credential;
        _isNew         = isNew;
        _savedPassword = credential.Password ?? string.Empty;

        Title                 = isNew ? "New credential" : "Edit credential";
        Width                 = 440;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _realm.ItemsSource       = Enum.GetValues<AuthRealm>().Select(r => new { Realm = r, Text = CredentialRules.Describe(r) }).ToList();
        _realm.SelectedValuePath = "Realm";
        _realm.DisplayMemberPath = "Text";
        _realm.SelectedValue     = credential.Realm;

        _name.Text         = credential.Name;
        _user.Text         = credential.Username ?? string.Empty;
        _domain.Text       = credential.Domain   ?? string.Empty;
        _password.Password = _savedPassword;
        _notes.Text        = credential.Notes ?? string.Empty;

        var grid = new Grid { Margin                            = new Thickness(16, 16, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var passwordBox = new DockPanel();
        DockPanel.SetDock(_copy, Dock.Right);
        DockPanel.SetDock(_show, Dock.Right);
        passwordBox.Children.Add(_copy);
        passwordBox.Children.Add(_show);
        passwordBox.Children.Add(_password);
        passwordBox.Children.Add(_plain);

        AddRow(grid, 0, "_Name",      _name);
        AddRow(grid, 1, "_Type",      _realm);
        AddRow(grid, 2, "_User name", _user);
        AddRow(grid, 3, "_Domain",    _domain);
        AddRow(grid, 4, _secretLabel, passwordBox);
        AddRow(grid, 5, "N_otes",     _notes);

        var hint = new TextBlock
                   {
                       Text         = $"The password and notes are encrypted in the Silo. Protected by: {protectedBy}.",
                       TextWrapping = TextWrapping.Wrap,
                       Opacity      = 0.65,
                       Margin       = new Thickness(0, 10, 0, 0)
                   };

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button
                     {
                         Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4),
                         Margin  = new Thickness(8,                                                      0, 0,  0)
                     };
        save.Click += (_, _) => Save();

        var buttons = new StackPanel
                      {
                          Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                          Margin      = new Thickness(0, 14, 0, 0)
                      };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(grid);
        root.Children.Add(new StackPanel { Margin = new Thickness(16, 0, 16, 8), Children = { _message, hint, buttons } });
        Content = root;

        _realm.SelectionChanged += (_, _) => ApplyRealm();
        ApplyRealm();

        _show.Checked   += (_, _) => SetShown(true);
        _show.Unchecked += (_, _) => SetShown(false);
        _copy.Click     += (_, _) => Copy();

        Loaded += (_, _) => (_isNew ? (Control)_name : _password).Focus();
        Closed += (_, _) =>
                  {
                      _password.Clear();
                      _plain.Clear();
                  };
    }
}