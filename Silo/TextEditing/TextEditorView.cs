using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Serilog;
using Silo.Commanding;
using Silo.Labs;
using Silo.Model;
using Silo.Theming;
using Silo.Ui;
using Silo.Ui.Documents;
using Silo.Ui.Panes;
using Silo.Ui.Windows;

namespace Silo.TextEditing;

/// <summary>
///     The text editor: a document tab that edits one <see cref="TextResource" /> (a file, a value kept in the Silo, a web
///     response, a new document). JSON, XML and YAML are coloured, checked as they are typed (the problems are underlined
///     and
///     listed in the Problems pane), folded by their structure, and can be formatted, minified and converted into each
///     other.
/// </summary>
/// <remarks>
///     <para>
///         Built on AvalonEdit. Ctrl+S saves, Shift+Alt+F formats, Ctrl+G goes to a line, Ctrl+F finds; the Edit menu
///         has the rest.
///     </para>
///     <para>
///         The checks run on another thread a moment after typing stops (<see cref="TextAnalysis" />); their result is
///         thrown away when
///         the text has changed again meanwhile.
///     </para>
/// </remarks>
public sealed class TextEditorView : UserControl, IDocumentContent, IEditContributor, IDocumentCloseGuard
{
    private static readonly Brush     MatchFill   = LabUi.Frozen(0x3B, 0x82, 0xD6, 0x38);
    private static readonly Brush     CurrentLine = LabUi.Frozen(0x80, 0x80, 0x80, 0x1A);
    private readonly        Debouncer _analyzeSoon;

    private readonly Border _banner = new()
                                      {
                                          Visibility      = Visibility.Collapsed, Padding = new Thickness(10, 5, 10, 5),
                                          BorderThickness = new Thickness(0,                                  0, 0,  1)
                                      };

    private readonly StackPanel _bannerButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly DiagnosticRenderer _diagnostics = new();

    private readonly TextEditor _editor       = new();
    private readonly TextBlock  _encodingText = Status();
    private readonly ToolTip    _hoverTip     = new() { Placement = PlacementMode.Mouse };

    private readonly ComboBox _indentBox =
        new() { MinWidth = 80, Margin = new Thickness(2, 0, 2, 0), ToolTip = "The indentation Format Document uses" };

    private readonly ComboBox _languageBox = new()
                                             {
                                                 MinWidth = 96, Margin = new Thickness(2, 0, 2, 0),
                                                 ToolTip  = "The language of the text: decides the colours, checks and formatting"
                                             };

    private readonly TextBlock                _languageText = Status();
    private readonly TextBlock                _locationText = Status(true);
    private readonly MatchRenderer            _matches      = new() { Fill = MatchFill };
    private readonly TextBlock                _newLineText  = Status();
    private readonly TextBlock                _position     = Status();
    private readonly TextBlock                _problems     = Status();
    private readonly Button                   _saveButton;
    private readonly SearchPanel              _search;
    private readonly ToggleButton             _whitespaceButton;
    private readonly ToggleButton             _wrapButton;
    private          TextAnalysisResult       _analysis = TextAnalysisResult.Empty;
    private          CancellationTokenSource? _analyzing;
    private          int                      _changes;
    private          TextContent              _content = TextContent.Of(string.Empty);
    private          FoldingManager?          _folding;
    private          OpenSilo?                _hookedSilo;
    private          bool                     _loaded;
    private          bool                     _loading;
    private          bool                     _pinned;

    /// <summary>The language the text is treated as.</summary>
    public TextLanguage EditorLanguage { get; private set; } = TextLanguage.PlainText;

    /// <summary>Whether there are changes that are not saved.</summary>
    public bool IsDirty => !_editor.Document.UndoStack.IsOriginalFile;

    // ---- what the document is ------------------------------------------------------------------

    /// <summary>What is being edited.</summary>
    public TextResource Resource { get; private set; }

    /// <summary>The text as it is now.</summary>
    public string Text => _editor.Text;

    private string Indent => (_indentBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "  ";

    public string? DocumentTitle => Resource.Title + (IsDirty ? " *" : string.Empty);

    // ---- loading -------------------------------------------------------------------------------

    /// <summary>
    ///     Reads the text (again). With <paramref name="ask" />, unsaved changes are only thrown away when the person
    ///     agrees.
    /// </summary>
    private async Task ReloadAsync(bool ask)
    {
        if (ask && IsDirty && !LabUi.Confirm($"Throw away the changes to {Resource.Title} and read it again?", "Reload"))
            return;

        HideBanner();
        TextContent content;
        try
        {
            content = await Task.Run(() => Resource.LoadAsync());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Location} in the text editor failed", Resource.Location);
            ShowBanner("This could not be read: " + ex.Message, ("Try again", () => _ = ReloadAsync(false)));
            _editor.IsReadOnly = true;
            return;
        }

        _loading = true;
        try
        {
            _content              = content;
            _editor.Document.Text = content.Text;
            _editor.Document.UndoStack.ClearAll();
            _editor.Document.UndoStack.MarkAsOriginalFile();
            _editor.IsReadOnly = Resource.Silo is { IsWritable: false };
            if (_editor.IsReadOnly)
                ShowBanner("This Silo is read-only: the text can be read and copied, and saved elsewhere with Save As.");

            var language = content.Language ?? TextLanguages.Detect(Resource.FileName, Resource.ContentType, content.Text);
            SelectLanguage(language);
            ApplyLanguage(language);
            _editor.ScrollToHome();
            _editor.TextArea.Caret.Offset = 0;
        }
        finally
        {
            _loading = false;
        }

        _loaded = true;
        HookSilo();
        UpdateStatus();
        Analyze();
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- typing --------------------------------------------------------------------------------

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;

        _changes++;
        _analyzeSoon.Poke();

        // a preview tab that is typed into is kept (it would otherwise be replaced by the next thing clicked in the Explorer)
        if (!_pinned)
        {
            _pinned = true;
            PinRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;

        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaretMoved()
    {
        UpdatePosition();
        int offset = _editor.TextArea.Caret.Offset;
        MatchPair? pair = _analysis.Pairs.TryGetValue(offset, out var at)                       ? at :
                          offset > 0 && _analysis.Pairs.TryGetValue(offset - 1, out var before) ? before : null;
        _matches.Set(pair, _editor.TextArea.TextView);
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        if (_editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } position) return;

        int offset = _editor.Document.GetOffset(position.Location);
        var here   = _diagnostics.At(offset).Select(m => m.Diagnostic).ToList();
        if (here.Count == 0) return;

        _hoverTip.PlacementTarget = _editor;
        _hoverTip.Content         = string.Join(Environment.NewLine, here.Select(d => $"{d.Severity}: {d.Message}"));
        _hoverTip.IsOpen          = true;
        e.Handled                 = true;
    }

    // ---- checking ------------------------------------------------------------------------------

    /// <summary>Checks the text on another thread and shows what was found, unless the text changed meanwhile.</summary>
    private async void Analyze()
    {
        if (!_loaded) return;

        _analyzing?.Cancel();
        var cancel = new CancellationTokenSource();
        _analyzing = cancel;
        int    at       = _changes;
        string text     = _editor.Text;
        var    language = EditorLanguage;

        TextAnalysisResult result;
        try
        {
            result = await Task.Run(() => TextAnalysis.Run(language, text, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Checking the text of {Title} failed", Resource.Title);
            return;
        }

        // typed into since: another check is on its way
        if (cancel.IsCancellationRequested || at != _changes || language != EditorLanguage) return;

        _analysis = result;
        _diagnostics.Set(_editor.Document, result.Diagnostics, _editor.TextArea.TextView);

        if (_folding is not null)
        {
            var firstError = result.Diagnostics.Where(d => d.Severity == ProblemSeverity.Error)
                                   .Select(d => DiagnosticRenderer.Place(_editor.Document, d).StartOffset)
                                   .DefaultIfEmpty(-1)
                                   .Min();
            try
            {
                _folding.UpdateFoldings(result.Folds.Select(f => new NewFolding(f.Start, f.End) { Name = f.Name }), firstError);
            }
            catch (ArgumentException ex)
            {
                Log.Debug(ex, "The folds of {Title} could not be updated", Resource.Title);
            }
        }

        PublishProblems();
        OnCaretMoved();
        UpdateStatus();
    }

    private void PublishProblems()
    {
        string title = Resource.Title;
        var problems = _analysis.Diagnostics
                                .Select(d => new Problem(d.Severity, d.Message, title, d.Line, d.Column, d.Source,
                                                         () => GoTo(d.Line, d.Column)))
                                .ToList();
        ProblemList.Publish(this, problems);
    }

    /// <summary>Brings this document forward and puts the caret at the line and column (1-based).</summary>
    public void GoTo(int line, int column)
    {
        TextEditors.Reveal(this);
        int l = Math.Clamp(line,   1, _editor.Document.LineCount);
        int c = Math.Clamp(column, 1, _editor.Document.GetLineByNumber(l).Length + 1);
        _editor.TextArea.Caret.Location = new TextLocation(l, c);
        _editor.ScrollTo(l, c);
        _editor.TextArea.Caret.BringCaretToView();
        Dispatcher.BeginInvoke(new Action(() => _editor.TextArea.Focus()), DispatcherPriority.Input);
    }

    private void GoToLine()
    {
        int     lines  = _editor.Document.LineCount;
        string? answer = PromptDialog.Ask("Go to Line", $"Line number (1 to {lines}):", "Go", _editor.TextArea.Caret.Line.ToString());
        if (answer is null) return;

        if (int.TryParse(answer, out int line)) GoTo(line, 1);
        else App.Status("That is not a line number");
    }

    private void OpenFind()
    {
        _editor.TextArea.Focus();
        _search.Open();
    }

    // ---- language ------------------------------------------------------------------------------

    private void SelectLanguage(TextLanguage language)
    {
        foreach (ComboBoxItem item in _languageBox.Items)
            if (item.Tag is TextLanguage l && l == language)
                _languageBox.SelectedItem = item;
    }

    /// <summary>Treats the text as <paramref name="language" /> from now on (the person picked it).</summary>
    public void SetLanguage(TextLanguage language)
    {
        if (language == EditorLanguage && _loaded) return;

        SelectLanguage(language);
        ApplyLanguage(language);
        Analyze();
        UpdateStatus();
    }

    private void ApplyLanguage(TextLanguage language)
    {
        EditorLanguage = language;
        if (Resource is StoredTextResource stored) stored.Language = language;

        _editor.SyntaxHighlighting = TextHighlighting.For(language, AppTheme.Instance.IsEffectivelyDark);

        // plain text has nothing to fold: no fold margin
        if (language == TextLanguage.PlainText)
        {
            if (_folding is not null)
            {
                FoldingManager.Uninstall(_folding);
                _folding = null;
            }
        }
        else
        {
            _folding ??= FoldingManager.Install(_editor.TextArea);
            _folding.Clear();
        }

        _analysis = TextAnalysisResult.Empty;
        _diagnostics.Clear(_editor.TextArea.TextView);
        _matches.Set(null, _editor.TextArea.TextView);
    }

    // ---- format, minify, convert ---------------------------------------------------------------

    /// <summary>
    ///     Whether the text can be restructured: it has a structured language and no errors (said in the status bar when
    ///     not).
    /// </summary>
    private bool ReadyToTransform(string what)
    {
        if (EditorLanguage == TextLanguage.PlainText)
        {
            App.Status($"Choose JSON, XML or YAML to {what}: plain text has no structure");
            return false;
        }

        int errors = TextValidators.Validate(EditorLanguage, _editor.Text).Count(d => d.Severity == ProblemSeverity.Error);
        if (errors == 0) return true;

        App.Status($"Fix the {(errors == 1 ? "error" : errors + " errors")} first: the text cannot be read as {EditorLanguage.DisplayName()}");
        ProblemsTool.Reveal();
        return false;
    }

    private bool YamlCommentsMayGo()
    {
        if (EditorLanguage != TextLanguage.Yaml) return true;

        int comments = TextFormatting.CountYamlComments(_editor.Text);
        return comments == 0 ||
               LabUi.Confirm($"The YAML has {comments} comment(s), and laying it out again removes them. Go ahead? (Edit ? Undo puts them back.)",
                             "Format YAML");
    }

    /// <summary>Lays the whole text out again (Format Document).</summary>
    public void Format()
    {
        if (_editor.IsReadOnly || !ReadyToTransform("format it") || !YamlCommentsMayGo()) return;

        Transform(() => TextFormatting.Format(EditorLanguage, _editor.Text, Indent), "Formatted");
    }

    /// <summary>Takes out all the white space the content does not need.</summary>
    public void Minify()
    {
        if (_editor.IsReadOnly || !ReadyToTransform("minify it") || !YamlCommentsMayGo()) return;

        Transform(() => TextFormatting.Minify(EditorLanguage, _editor.Text), "Minified");
    }

    /// <summary>Opens the content, written in <paramref name="target" />, in a new document (this one stays as it is).</summary>
    public void ConvertTo(TextLanguage target)
    {
        if (!ReadyToTransform("convert it")) return;

        try
        {
            string text = TextFormatting.Convert(EditorLanguage, target, _editor.Text, Indent);
            TextEditors.OpenNew(target, text.Replace("\n", _content.NewLine));
            App.Status($"Converted {Resource.Title} to {target.DisplayName()} in a new document");
        }
        catch (TextTransformException ex)
        {
            App.Status(ex.Message);
        }
    }

    /// <summary>Replaces the text with what <paramref name="make" /> makes of it, as one change that Undo takes back.</summary>
    private void Transform(Func<string> make, string done)
    {
        string result;
        try
        {
            result = make().Replace("\n", _content.NewLine);
        }
        catch (TextTransformException ex)
        {
            App.Status(ex.Message);
            return;
        }

        if (result == _editor.Text)
        {
            App.Status($"{done}: nothing changed");
            return;
        }

        int caret = _editor.TextArea.Caret.Offset;
        _editor.Document.Replace(0, _editor.Document.TextLength, result);
        _editor.TextArea.Caret.Offset = Math.Min(caret, _editor.Document.TextLength);
        _editor.TextArea.Caret.BringCaretToView();
        App.Status(done);
    }

    private void ShowConvertMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var target in TextLanguages.Structured)
        {
            var t = target;
            var item = new MenuItem
                       {
                           Header    = "To " + t.DisplayName(),
                           IsEnabled = EditorLanguage != TextLanguage.PlainText && t != EditorLanguage
                       };
            item.Click += (_, _) => ConvertTo(t);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ---- saving --------------------------------------------------------------------------------

    /// <summary>
    ///     Saves where the text came from; a text that cannot go back there (a new document, a web response) asks where to put
    ///     it.
    ///     Returns false when it was not saved.
    /// </summary>
    public bool Save()
    {
        if (!_loaded) return false;
        if (!Resource.CanSave) return SaveAs();

        if (Resource.ChangedSinceLoad)
        {
            var (button, _) = PromptDialog.Show("Changed elsewhere",
                                                $"{Resource.Title} was changed by another program after it was opened here.\n\nSave over those changes?",
                                                ["Save over them", "Cancel"]);
            if (button != 0) return false;
        }

        if (!ErrorsMayBeSaved()) return false;
        return Write(Resource);
    }

    /// <summary>Asks before saving text that has errors (it is the person's choice: a half-written file can be worth keeping).</summary>
    private bool ErrorsMayBeSaved()
    {
        int errors = _analysis.ErrorCount;
        if (errors == 0) return true;

        var (button, _) = PromptDialog.Show("Save with errors?",
                                            $"{Resource.Title} has {(errors == 1 ? "an error" : errors + " errors")}: it is not valid {EditorLanguage.DisplayName()}. Save it anyway?",
                                            ["Save anyway", "Cancel"]);
        return button == 0;
    }

    private bool Write(TextResource target)
    {
        try
        {
            target.Save(_content with { Text = _editor.Text });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving {Location} failed", target.Location);
            MessageBox.Show($"{target.Title} could not be saved:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK,
                            MessageBoxImage.Warning);
            return false;
        }

        _editor.Document.UndoStack.MarkAsOriginalFile();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        App.Status(target.Silo is { } silo
                       ? $"Saved {target.Title} into {silo.Name ?? "the Silo"} (save the Silo to keep it in its file)"
                       : "Saved " + target.Title);
        return true;
    }

    /// <summary>
    ///     Saves a copy as a file or in the active Silo, and goes on editing that copy. Returns false when nothing was
    ///     saved.
    /// </summary>
    public bool SaveAs()
    {
        if (!_loaded) return false;

        var  silo    = App.Instance?.ActiveSilo;
        bool siloCan = silo is { IsWritable: true } && silo.Data is IStoredStore;

        int choice = 0;
        if (siloCan)
        {
            (choice, _) = PromptDialog.Show("Save As", $"Where should {Resource.Title} be saved?",
                                            ["As a file...", $"In {silo!.Name ?? "the Silo"}", "Cancel"]);
            if (choice == 2) return false;
        }

        TextResource target;
        if (choice == 0)
        {
            string suggested = Path.GetFileNameWithoutExtension(Resource.Title) is { Length: > 0 } stem
                                   ? stem       + EditorLanguage.DefaultExtension()
                                   : "Untitled" + EditorLanguage.DefaultExtension();
            if (Dialogs.SaveFile("Save As", EditorLanguage.FileFilter(), suggested) is not { } path) return false;
            target = new FileTextResource(path);
        }
        else
        {
            string initial = Path.GetFileNameWithoutExtension(Resource.Title);
            if (PromptDialog.Ask("Save in the Silo", "Name of the document:", "Save", initial) is not { } name) return false;

            var store = (IStoredStore)silo!.Data!;
            if (store.GetStored(SiloTextDocument.KeyPrefix + name, StoredScope.Shared) is not null &&
                !LabUi.Confirm($"The Silo already has a document called \"{name}\". Replace it?", "Save in the Silo"))
                return false;

            target                                = StoredTextResource.NewDocument(store, name, silo);
            ((StoredTextResource)target).Language = EditorLanguage;
        }

        if (!ErrorsMayBeSaved() || !Write(target)) return false;

        // from now on this tab edits the copy
        string oldKey = Resource.Key;
        Resource = target;
        TextEditors.Rekey(this, oldKey);
        HookSilo();
        _editor.IsReadOnly = false;
        HideBanner();
        PublishProblems();
        UpdateStatus();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ---- the Silo it lives in ------------------------------------------------------------------

    /// <summary>
    ///     A text kept in a Silo is written into it whenever the Silo is saved (as tables are), so "Save Silo" never leaves an
    ///     edit
    ///     behind. A text that is not valid where it must be (a stored JSON value) is left alone, and stays unsaved.
    /// </summary>
    private void HookSilo()
    {
        var silo = Resource.Silo;
        if (ReferenceEquals(silo, _hookedSilo)) return;

        _hookedSilo?.SaveHooks.Remove(CommitToSilo);
        _hookedSilo = silo;
        silo?.SaveHooks.Add(CommitToSilo);
    }

    private Task CommitToSilo()
    {
        if (!IsDirty || !Resource.CanSave || Resource.Silo is null) return Task.CompletedTask;

        return Dispatcher.InvokeAsync(() =>
                                      {
                                          try
                                          {
                                              Resource.Save(_content with { Text = _editor.Text });
                                              _editor.Document.UndoStack.MarkAsOriginalFile();
                                          }
                                          catch (Exception ex)
                                          {
                                              Log.Warning(ex, "Writing {Title} into the Silo as it was saved failed", Resource.Title);
                                              App.Status($"{Resource.Title} was not saved into the Silo: {ex.Message}");
                                          }
                                      })
                         .Task;
    }

    /// <summary>The tab has gone: lets go of everything this editor hooked into.</summary>
    internal void Release()
    {
        _analyzing?.Cancel();
        _analyzeSoon.Cancel();
        _hookedSilo?.SaveHooks.Remove(CommitToSilo);
        _hookedSilo                       =  null;
        AppTheme.Instance.PropertyChanged -= OnThemeChanged;
        ProblemList.Clear(this);
    }

    private void SetAllFolded(bool folded)
    {
        if (_folding is null) return;

        foreach (var section in _folding.AllFoldings) section.IsFolded = folded;
    }

    // ---- look ----------------------------------------------------------------------------------

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme()
    {
        _editor.SyntaxHighlighting        = TextHighlighting.For(EditorLanguage, AppTheme.Instance.IsEffectivelyDark);
        _editor.TextArea.Caret.CaretBrush = TryFindResource("Theme.Text") as Brush;
        _matches.Outline                  = new Pen(LabUi.Frozen(0x3B, 0x82, 0xD6, 0xA0), 1);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void UpdatePosition()
    {
        var caret    = _editor.TextArea.Caret;
        int selected = _editor.TextArea.Selection.Length;
        _position.Text = $"Ln {caret.Line}, Col {caret.Column}" + (selected > 0 ? $"  ({selected} selected)" : string.Empty);
    }

    private void UpdateStatus()
    {
        UpdatePosition();

        int errors = _analysis.ErrorCount, warnings = _analysis.WarningCount;
        _problems.Text = EditorLanguage    == TextLanguage.PlainText ? string.Empty :
                         errors + warnings == 0                      ? "? No problems" : $"{errors} error(s), {warnings} warning(s)";
        _problems.Foreground = errors   > 0 ? LabUi.Red :
                               warnings > 0 ? LabUi.Amber : TryFindResource("Theme.TextMuted") as Brush ?? Brushes.Gray;

        _languageText.Text    = EditorLanguage.DisplayName();
        _encodingText.Text    = _content.EncodingName;
        _newLineText.Text     = _content.NewLineName;
        _locationText.Text    = Resource.Location + (Resource.CanSave ? string.Empty : "  (Save asks where to put it)");
        _locationText.ToolTip = Resource.Location;
    }

    private void ShowBanner(string text, params (string Label, Action Run)[] buttons)
    {
        _bannerText.Text = text;
        _bannerButtons.Children.Clear();
        foreach (var (label, run) in buttons) _bannerButtons.Children.Add(LabUi.Tool(label, null, run));
        _banner.Visibility = Visibility.Visible;
    }

    private void HideBanner()
    {
        _banner.Visibility = Visibility.Collapsed;
    }

    private static TextBlock Status(bool muted = false)
    {
        var block = LabUi.Label(string.Empty, muted);
        block.Margin = new Thickness(0, 0, 18, 0);
        return block;
    }

    private static Border Gap()
    {
        var line = new Border { Width = 1, Margin = new Thickness(6, 3, 6, 3) };
        line.SetResourceReference(Border.BackgroundProperty, "Theme.Border");
        return line;
    }

    private static ToggleButton Toggle(string text, string icon, string tip, Action<bool> changed)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (LabUi.Icon(icon, 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            content.Children.Add(glyph);
        }

        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new ToggleButton
                     { Content = content, ToolTip = tip, Padding = new Thickness(7, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0) };
        button.Checked   += (_, _) => changed(true);
        button.Unchecked += (_, _) => changed(false);
        return button;
    }

    public TextEditorView(TextResource resource)
    {
        Resource     = resource;
        _analyzeSoon = new Debouncer(TimeSpan.FromMilliseconds(350), Analyze);

        // ---- the editor
        _editor.ShowLineNumbers                                 = true;
        _editor.FontFamily                                      = LabUi.Mono;
        _editor.FontSize                                        = 13;
        _editor.HorizontalScrollBarVisibility                   = ScrollBarVisibility.Auto;
        _editor.VerticalScrollBarVisibility                     = ScrollBarVisibility.Auto;
        _editor.Padding                                         = new Thickness(4, 2, 0, 0);
        _editor.Options.ConvertTabsToSpaces                     = true;
        _editor.Options.IndentationSize                         = 2;
        _editor.Options.HighlightCurrentLine                    = true;
        _editor.Options.EnableHyperlinks                        = true;
        _editor.Options.RequireControlModifierForHyperlinkClick = true;
        _editor.Options.EnableEmailHyperlinks                   = false;
        _editor.Options.AllowScrollBelowDocument                = true;
        _editor.SetResourceReference(BackgroundProperty,                       "Theme.Background");
        _editor.SetResourceReference(ForegroundProperty,                       "Theme.Text");
        _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Theme.TextMuted");
        _editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "Theme.Selection");
        _editor.TextArea.SelectionForeground            = null; // the selected text keeps its colours
        _editor.TextArea.TextView.CurrentLineBackground = CurrentLine;
        _editor.TextArea.TextView.CurrentLineBorder     = new Pen(Brushes.Transparent, 0);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_matches);
        _search = SearchPanel.Install(_editor);

        _editor.TextChanged                        += OnTextChanged;
        _editor.Document.UndoStack.PropertyChanged += OnUndoStackChanged;
        _editor.TextArea.Caret.PositionChanged     += (_, _) => OnCaretMoved();
        _editor.TextArea.SelectionChanged          += (_, _) => UpdatePosition();
        _editor.MouseHover                         += OnMouseHover;
        _editor.MouseHoverStopped                  += (_, _) => _hoverTip.IsOpen = false;

        // ---- the bar above it
        foreach (var language in TextLanguages.All)
            _languageBox.Items.Add(new ComboBoxItem { Content = language.DisplayName(), Tag = language });
        _languageBox.SelectionChanged += (_, _) =>
                                         {
                                             if (!_loading && _languageBox.SelectedItem is ComboBoxItem { Tag: TextLanguage chosen })
                                                 SetLanguage(chosen);
                                         };
        foreach (var (label, text) in new[] { ("2 spaces", "  "), ("4 spaces", "    "), ("Tabs", "\t") })
            _indentBox.Items.Add(new ComboBoxItem { Content = label, Tag = text });
        _indentBox.SelectedIndex = 0;
        _indentBox.SelectionChanged += (_, _) =>
                                       {
                                           string indent = Indent;
                                           _editor.Options.ConvertTabsToSpaces = indent != "\t";
                                           _editor.Options.IndentationSize     = indent == "\t" ? 4 : indent.Length;
                                       };

        _wrapButton = Toggle("Wrap", "Wrap", "Wrap long lines at the edge of the window", on => _editor.WordWrap = on);
        _whitespaceButton = Toggle("Whitespace", "Literal", "Show spaces, tabs and line ends", on =>
                                                                                               {
                                                                                                   _editor.Options.ShowSpaces    = on;
                                                                                                   _editor.Options.ShowTabs      = on;
                                                                                                   _editor.Options.ShowEndOfLine = on;
                                                                                               });
        _saveButton = LabUi.Tool("Save", "TbSave", () => Save(), "Save (Ctrl+S)");

        var convert = LabUi.Tool("Convert", "DataExchange", () => { }, "Write the content in another language, in a new document");
        convert.Click += (_, _) => ShowConvertMenu(convert);

        var bar = LabUi.Bar(_saveButton,
                            LabUi.Tool("Save As", "TbSaveAs", () => SaveAs(), "Save a copy somewhere else: a file, or the Silo"), Gap(),
                            LabUi.Label("Language:", true), _languageBox,
                            LabUi.Tool("Format", "Indent",  () => Format(), "Lay the text out again with even indentation (Shift+Alt+F)"),
                            LabUi.Tool("Minify", "Outdent", () => Minify(), "Take out the white space the content does not need"), convert,
                            Gap(), LabUi.Label("Indent:", true), _indentBox, _wrapButton, _whitespaceButton, Gap(),
                            LabUi.Tool("Find",   "Search",  () => OpenFind(),            "Find in the text (Ctrl+F; F3 finds the next)"),
                            LabUi.Tool("Reload", "Refresh", () => _ = ReloadAsync(true), "Read the text again from where it came from"));

        // ---- the banner (a message about the document: it could not be read, it changed on disk, it is read-only)
        _banner.SetResourceReference(Border.BackgroundProperty,  "Theme.Surface");
        _banner.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        _bannerText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        var bannerRow = new DockPanel();
        DockPanel.SetDock(_bannerButtons, Dock.Right);
        bannerRow.Children.Add(_bannerButtons);
        bannerRow.Children.Add(_bannerText);
        _banner.Child = bannerRow;

        // ---- the status line below it
        _problems.Cursor            =  Cursors.Hand;
        _problems.ToolTip           =  "Show the Problems pane";
        _problems.MouseLeftButtonUp += (_, _) => ProblemsTool.Reveal();
        var status = new DockPanel { Margin = new Thickness(8, 2, 8, 2), LastChildFill = true };
        foreach (var part in new UIElement[] { _position, _problems, _languageText, _encodingText, _newLineText })
        {
            DockPanel.SetDock(part, Dock.Left);
            status.Children.Add(part);
        }

        _locationText.TextTrimming        = TextTrimming.CharacterEllipsis;
        _locationText.HorizontalAlignment = HorizontalAlignment.Right;
        status.Children.Add(_locationText);
        var statusBorder = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0) };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        statusBorder.SetResourceReference(Border.BackgroundProperty,  "Theme.Surface");

        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_banner);
        var root = LabUi.Layout(top, _editor, statusBorder);
        root.SetResourceReference(Panel.BackgroundProperty, "Theme.Background");
        Content = root;

        // ---- keys (they win over the window's own Ctrl+S, which saves the Silo, while the focus is in here)
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => Save()),     Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => SaveAs()),   Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => Format()),   Key.F, ModifierKeys.Shift   | ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(new DelegateCommand(_ => GoToLine()), Key.G, ModifierKeys.Control));

        AppTheme.Instance.PropertyChanged += OnThemeChanged;
        ApplyTheme();
        UpdateStatus();
        _ = ReloadAsync(false);
    }

    // ---- closing -------------------------------------------------------------------------------

    /// <summary>
    ///     Asks about unsaved changes: true to go on closing (saved, or the person does not want them), false to stay
    ///     open.
    /// </summary>
    public bool ConfirmClose()
    {
        if (!IsDirty || !_loaded) return true;

        var (button, _) = PromptDialog.Show("Unsaved changes", $"Save the changes to {Resource.Title}?", ["Save", "Don't save", "Cancel"]);
        return button switch
               {
                   0 => Save(),
                   1 => true,
                   _ => false
               };
    }

    public event EventHandler? PinRequested;

    public event EventHandler? TitleChanged;

    // ---- Edit menu -----------------------------------------------------------------------------

    public IEnumerable<EditEntry> EditEntries()
    {
        bool structured = EditorLanguage != TextLanguage.PlainText;
        bool writable   = !_editor.IsReadOnly && _loaded;

        yield return EditEntry.Item("_Find...",       OpenFind, gesture: "Ctrl+F");
        yield return EditEntry.Item("_Go to Line...", GoToLine, gesture: "Ctrl+G");
        yield return EditEntry.Divider();
        yield return EditEntry.Item("F_ormat Document", Format, structured && writable, "Shift+Alt+F");
        yield return EditEntry.Item("_Minify",          Minify, structured && writable);
        yield return EditEntry.Submenu("Con_vert To",
                                       TextLanguages.Structured
                                                    .Select(t => EditEntry.Item("_" + t.DisplayName() + " (new document)",
                                                                                () => ConvertTo(t), structured && t != EditorLanguage))
                                                    .ToArray());
        yield return EditEntry.Submenu("_Language",
                                       TextLanguages.All
                                                    .Select(l => EditEntry.Item((l == EditorLanguage ? "? " : "    ") + l.DisplayName(),
                                                                                () => SetLanguage(l)))
                                                    .ToArray());
        yield return EditEntry.Divider();
        yield return EditEntry.Item("Fold _All",   () => SetAllFolded(true),  _folding is not null);
        yield return EditEntry.Item("_Unfold All", () => SetAllFolded(false), _folding is not null);
        yield return EditEntry.Item(_editor.WordWrap ? "Don't _Wrap Lines" : "_Wrap Lines",
                                    () => _wrapButton.IsChecked = !_editor.WordWrap);
        yield return EditEntry.Divider();
        yield return EditEntry.Item("_Save " + Resource.Title.Replace("_", "__"), () => Save(),   _loaded, "Ctrl+S");
        yield return EditEntry.Item("Save _As...",                                () => SaveAs(), _loaded, "Ctrl+Shift+S");
    }
}