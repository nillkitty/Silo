using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Serilog;
using Silo.Commanding;
using Silo.Storage;

namespace Silo.Labs;

/// <summary>
///     The usual base of a <see cref="ILab" /> that studies values of one type. A Lab says what it is called and which parameters it has, and
///     makes its sessions; everything it accepts, converts and keeps is here. Derive and register with <c>[Singleton(typeof(ILab))]</c>.
/// </summary>
/// <typeparam name="TValue">The kind of value the Lab studies (<see cref="Uri" /> for the Web Lab).</typeparam>
public abstract class LabBase<TValue> : ILab
{
    private LabParameterSet? _parameters;

    public abstract string Id { get; }

    public abstract string Name { get; }

    public virtual string Description => string.Empty;

    public virtual string? IconKey => null;

    public virtual int Order => 100;

    public Type ValueType => typeof(TValue);

    /// <inheritdoc />
    public LabParameterSet Parameters => _parameters ??= MakeParameters();

    /// <summary>The parameters of the Lab; none unless overridden.</summary>
    protected virtual IEnumerable<LabParameter> DefineParameters() => [];

    /// <summary>A window of the Lab.</summary>
    public abstract LabSession<TValue> CreateSession();

    ILabSession ILab.CreateSession() => CreateSession();

    bool ILab.TryAccept(object? candidate, out object? value)
    {
        if (TryConvert(candidate, out var typed) && typed is not null)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    ///     The value <paramref name="candidate" /> stands for: itself when it is a <typeparamref name="TValue" />, the reading of it when it is
    ///     text (<see cref="TryParse" />). A Lab overrides this to accept more -- or less, when only some values are worth opening.
    /// </summary>
    protected virtual bool TryConvert(object? candidate, out TValue? value)
    {
        switch (candidate)
        {
            case TValue typed:
                value = typed;
                return true;
            case string text:
                return TryParse(text, out value);
            default:
                value = default;
                return false;
        }
    }

    /// <summary>Reads text typed by a person as a value. By default only text for a Lab of text.</summary>
    public virtual bool TryParse(string text, out TValue? value)
    {
        if (typeof(TValue) == typeof(string))
        {
            value = (TValue)(object)text;
            return true;
        }

        value = default;
        return false;
    }

    private LabParameterSet MakeParameters()
    {
        var set = new LabParameterSet(Id, DefineParameters());
        try
        {
            set.Load();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the parameters of {Lab} failed", Name);
        }

        return set;
    }
}

/// <summary>
///     The usual base of a Lab's window-session. It holds what every session has -- the title, the status line, change notification, the
///     standard value actions (copy, add to the Silo) -- and leaves to the Lab what is its own: the main view, how to study a value, and the
///     actions, inspectors and terminal commands.
/// </summary>
/// <typeparam name="TValue">The kind of value studied.</typeparam>
public abstract class LabSession<TValue> : ILabSession, ILabModel, ILabReporter, ILabProblems, ILabLibrary, ILabPresets, ILabRecent
{
    private readonly LabBase<TValue> _lab;
    private readonly List<LabProblem> _reported = [];
    private readonly object           _problemGate = new();
    private          string          _status = string.Empty;
    private          bool            _disposed;

    protected LabSession(LabBase<TValue> lab)
    {
        _lab = lab ?? throw new ArgumentNullException(nameof(lab));
    }

    /// <summary>The window this is a session of; null until the window attaches.</summary>
    protected ILabHost? Host { get; private set; }

    /// <summary>The Lab, as the kind of Lab it is.</summary>
    protected LabBase<TValue> LabOf => _lab;

    public ILab Lab => _lab;

    /// <summary>The value being studied now.</summary>
    public abstract TValue? CurrentValue { get; }

    object? ILabSession.Value => CurrentValue;

    public virtual string ValueText => CurrentValue?.ToString() ?? string.Empty;

    public virtual string Title => string.IsNullOrEmpty(ValueText) ? _lab.Name : $"{_lab.Name} - {ValueText}";

    public string Status
    {
        get => _status;
        protected set
        {
            if (_status == value) return;
            _status = value;
            Raise();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ValueChanged;

    public event EventHandler? ActionsChanged;

    void ILabReporter.Report(string message) => Status = message;

    void ILabSession.Attach(ILabHost host)
    {
        Host = host;
        OnAttached();
    }

    public async Task OpenAsync(object? value)
    {
        if (value is null)
        {
            await OpenDefaultAsync();
            return;
        }

        if (!((ILab)_lab).TryAccept(value, out var accepted) || accepted is not TValue typed)
            throw new ArgumentException($"{_lab.Name} cannot study {value}.", nameof(value));

        await OpenValueAsync(typed);
        if (RecentOnOpen) RememberRecent(ValueText);
    }

    public bool TryParseValue(string text, out object? value)
    {
        if (_lab.TryParse(text, out var typed) && typed is not null)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    public abstract UIElement BuildMainView();

    public virtual IEnumerable<ProvidedCommand> GlobalActions => [];

    public virtual IEnumerable<ProvidedCommand> ContextActions => [];

    /// <summary>The actions on the value: by default Copy and Add to Silo; a Lab adds its own after them.</summary>
    public virtual IEnumerable<ProvidedCommand> ValueActions => StandardValueActions();

    public virtual IEnumerable<LabMenu> Menus => [];

    public virtual UIElement? BuildTopBar() => null;

    public virtual int LayoutVersion => 1;

    public virtual IReadOnlyList<LabInspector> Inspectors => [];

    public virtual IReadOnlyList<LabTerminalCommand> TerminalCommands => [];

    // ---- problems, library and presets: what every window shows in the panes the window adds -------

    /// <inheritdoc />
    public event EventHandler? ProblemsChanged;

    /// <inheritdoc />
    public IReadOnlyList<LabProblem> ProblemList
    {
        get
        {
            var found = new List<LabProblem>();
            try
            {
                found.AddRange(Validate());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Checking the value of {Lab} failed", _lab.Name);
            }

            lock (_problemGate) found.AddRange(_reported);
            return found;
        }
    }

    /// <summary>
    ///     What is wrong with the value as it is now, found by checking it (the menu has an entry without a name, the schema a type that
    ///     does not exist). Asked again whenever the window thinks it changed; call <see cref="RaiseProblemsChanged" /> when it did.
    /// </summary>
    protected virtual IEnumerable<LabProblem> Validate() => [];

    /// <summary>Call when what <see cref="Validate" /> finds may have changed.</summary>
    protected void RaiseProblemsChanged() => ProblemsChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void ReportProblem(LabProblemSeverity severity, string source, string message, string? where = null, Action? go = null)
    {
        lock (_problemGate)
        {
            // the same thing said again and again (a page that fails on every request) is said once
            if (_reported.Any(p => p.Severity == severity && p.Source == source && p.Message == message && p.Where == where)) return;

            _reported.Add(new LabProblem(severity, source, message, where, go));
            while (_reported.Count > 200) _reported.RemoveAt(0);
        }

        RaiseProblemsChanged();
    }

    /// <inheritdoc />
    public void ClearProblems(string? source = null)
    {
        int removed;
        lock (_problemGate) removed = _reported.RemoveAll(p => source is null || p.Source == source);
        if (removed > 0) RaiseProblemsChanged();
    }

    /// <summary>Whether a new window opens with the Library showing: false unless the Lab is mostly about the items it keeps.</summary>
    public virtual bool LibraryOpensWithWindow => false;

    /// <summary>
    ///     Whether opening a value puts its text in the recent list. True for a Lab whose value reads back from its text (a web address); a
    ///     Lab whose values are named items in the Silo says false and remembers the names itself (<see cref="RememberRecent" />).
    /// </summary>
    protected virtual bool RecentOnOpen => true;

    /// <summary>Puts something the person opened in the Lab's recent list (the same in every window and every Silo).</summary>
    protected void RememberRecent(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) LabProfile.Remember(_lab.Id, text);
    }

    /// <summary>Opens again something from the recent list: by default the text read as a value; a Lab whose recent entries are names of saved items overrides.</summary>
    public virtual async Task OpenRecentAsync(string text)
    {
        if (!TryParseValue(text, out var value)) throw new InvalidOperationException($"'{text}' is not something {_lab.Name} can open.");

        await OpenAsync(value);
    }

    /// <summary>The Library pane: by default what the Lab keeps in the Silo as saved values (see <see cref="SavedLibrary" />); a Lab with more to offer overrides.</summary>
    public virtual IReadOnlyList<LabLibrarySection> LibrarySections(string filter) => SavedLibrary(filter);

    /// <summary>The values saved in the Silo with Add to Silo (<c>lab:&lt;id&gt;:saved:&lt;value&gt;</c>): each opens by being read as a value.</summary>
    protected IReadOnlyList<LabLibrarySection> SavedLibrary(string filter)
    {
        string prefix = $"lab:{_lab.Id}:saved:";
        var    store  = Stored.DefaultStore();
        if (store is null) return [new LabLibrarySection("In the Silo", "Database5", [], Note: "No Silo is open")];

        var entries = new List<LabLibraryEntry>();
        try
        {
            foreach (var record in store.ListStored(prefix))
            {
                string name = record.Key[prefix.Length..];
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                entries.Add(new LabLibraryEntry(name, null, null, _lab.IconKey ?? "Folder17", () => OpenSavedValue(name), name == ValueText, ToolTip: "Double-click to open",
                                                Delete: () => Stored.At<TValue>(prefix + name).Delete()));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing what {Lab} keeps failed", _lab.Name);
        }

        return [new LabLibrarySection("In the Silo", "Database5", entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), Note: entries.Count == 0 ? "Nothing kept yet: Add to Silo (Value menu) keeps the value here" : null)];
    }

    private async void OpenSavedValue(string name)
    {
        try
        {
            if (!TryParseValue(name, out var value)) throw new InvalidOperationException($"'{name}' is not something {_lab.Name} can open.");

            await OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Name} in {Lab} failed", name, _lab.Name);
            Status = ex.Message;
        }
    }

    /// <summary>The kinds of settings the Presets pane can keep: by default the Lab's parameters; a Lab adds its filters and tools.</summary>
    public virtual IReadOnlyList<LabPresetKind> PresetKinds => ParameterPresets();

    /// <summary>
    ///     Every list of parameters that has settings as a kind of preset: the Lab's, an aspect's, a tool's, the active part's... (none for a
    ///     list that has only buttons). Presets of a list are filed under its <see cref="LabParameterSet.ScopeKey" />.
    /// </summary>
    protected IReadOnlyList<LabPresetKind> ParameterPresets()
    {
        var kinds = new List<LabPresetKind>();
        foreach (var source in ParameterSources)
        {
            var set = source.Set;
            if (set.All(p => p.Kind == LabParameterKind.Action) || kinds.Any(k => k.Id == set.ScopeKey)) continue;

            string title = set.ScopeKey == LabParameterSet.LabScope ? "Lab parameters" : $"{source.Title} parameters";
            kinds.Add(new LabPresetKind(set.ScopeKey, title, set.Snapshot, set.Restore, "Settings"));
        }

        return kinds;
    }

    /// <summary>Studies the value.</summary>
    protected abstract Task OpenValueAsync(TValue value);

    /// <summary>Studies "nothing in particular" (a blank page, an empty text): what <c>OpenAsync(null)</c> does.</summary>
    protected virtual Task OpenDefaultAsync() => Task.CompletedTask;

    /// <summary>The window is attached: <see cref="Host" /> can be used.</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Call after <see cref="CurrentValue" /> changed (the parts are read again).</summary>
    protected void RaiseValueChanged()
    {
        _parts = null;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Raise(nameof(ValueText));
        Raise(nameof(Title));
        RaiseModelChanged();
    }

    // ---- the model: subject types, parts, aspects, tools, commands and parameters (see ILabModel) ----------

    private IReadOnlyList<LabSubjectType>? _subjectTypes;
    private IReadOnlyList<LabPartType>?    _partTypes;
    private IReadOnlyList<LabPart>?        _parts;
    private IReadOnlyList<LabAspect>?      _aspects;
    private IReadOnlyList<LabTool>?        _tools;
    private string?                        _activePartKey;
    private string?                        _activeToolId;

    /// <inheritdoc />
    public event EventHandler? ModelChanged;

    /// <summary>Call when the parts, the active part or tool, or what applies may have changed (the value changing already does).</summary>
    protected void RaiseModelChanged() => ModelChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Call when the parts changed without the value changing (a part was renamed in the Lab's own view): they are read again.</summary>
    protected void RaisePartsChanged()
    {
        _parts = null;
        RaiseModelChanged();
    }

    /// <summary>What one subject of the Lab is called ("regex", "menu", "page"); lower case, for sentences.</summary>
    protected virtual string SubjectNoun => "item";

    /// <summary>The kinds of subject. By default one, named for the Lab; a Lab adds its prefabs and templates to it.</summary>
    protected virtual IEnumerable<LabSubjectType> DefineSubjectTypes() => [new LabSubjectType(_lab.Id, SubjectNoun.Length == 0 ? "Item" : char.ToUpperInvariant(SubjectNoun[0]) + SubjectNoun[1..], _lab.IconKey, _lab.Description)];

    /// <summary>The kinds of part; none unless overridden.</summary>
    protected virtual IEnumerable<LabPartType> DefinePartTypes() => [];

    /// <summary>The top-level parts of the value as it is now; none unless overridden. Called again after the value changes.</summary>
    protected virtual IEnumerable<LabPart> BuildParts() => [];

    /// <summary>The aspects (their tools and tools' aspects are found through them); none unless overridden. Made once.</summary>
    protected virtual IEnumerable<LabAspect> DefineAspects() => [];

    /// <summary>The tools that do not belong to an aspect; none unless overridden. Made once.</summary>
    protected virtual IEnumerable<LabTool> DefineTools() => [];

    /// <summary>The commands of the Lab, the instance, the subject (type) and the parts (types). Asked each time, so what is checked or enabled is current.</summary>
    protected virtual IEnumerable<LabCommandEntry> DefineModelCommands() => [];

    /// <summary>Parameter lists besides the Lab's, the aspects', the tools' and the active part's: the instance's, the subject's...</summary>
    protected virtual IEnumerable<LabParameterSource> ExtraParameterSources() => [];

    /// <summary>The Lab selected a part itself (the person clicked a rule in the main view): the model follows without calling <see cref="OnPartActivated" />.</summary>
    protected void SetActivePart(string? key)
    {
        if (string.Equals(_activePartKey, key, StringComparison.Ordinal)) return;

        _activePartKey = key;
        RaiseModelChanged();
    }

    /// <summary>The person made <paramref name="part" /> the active part from outside the Lab's main view (the toolbar, the terminal): show it there.</summary>
    protected virtual void OnPartActivated(LabPart? part)
    {
    }

    /// <summary>A new subject from content; by default the content is read as a value and opened. A Lab whose subjects are named records overrides.</summary>
    protected virtual async Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        if (!TryParseValue(content, out var value)) throw new InvalidOperationException($"'{LabUi.Shorten(content, 40)}' is not something {_lab.Name} can open.");

        await OpenAsync(value);
    }

    /// <summary>A new part from content; by default the Lab cannot add parts.</summary>
    protected virtual Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        throw new InvalidOperationException($"{_lab.Name} cannot add a {type.Name.ToLowerInvariant()} this way.");
    }

    /// <inheritdoc />
    public IReadOnlyList<LabSubjectType> SubjectTypes => _subjectTypes ??= Safe(DefineSubjectTypes).ToList();

    /// <inheritdoc />
    public virtual LabSubjectType? SubjectType => SubjectTypes.FirstOrDefault();

    /// <inheritdoc />
    public IReadOnlyList<LabPartType> PartTypes => _partTypes ??= Safe(DefinePartTypes).ToList();

    /// <inheritdoc />
    public IReadOnlyList<LabPart> Parts => _parts ??= Safe(BuildParts).ToList();

    /// <inheritdoc />
    public LabPart? ActivePart => _activePartKey is null ? null : Parts.SelectMany(p => p.Self()).FirstOrDefault(p => p.Key == _activePartKey);

    /// <inheritdoc />
    public void ActivatePart(LabPart? part)
    {
        _activePartKey = part?.Key;
        try
        {
            OnPartActivated(part);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Showing a part of {Lab} failed", _lab.Name);
        }

        RaiseModelChanged();
        RaiseActionsChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<LabAspect> Aspects => _aspects ??= Flatten().Aspects;

    /// <inheritdoc />
    public IReadOnlyList<LabTool> Tools => _tools ??= Flatten().Tools;

    /// <summary>All aspects and tools: the declared ones, and those of aspects and tools (each once, by id).</summary>
    private (List<LabAspect> Aspects, List<LabTool> Tools) Flatten()
    {
        var aspects = new List<LabAspect>();
        var tools   = new List<LabTool>();

        void VisitAspect(LabAspect aspect)
        {
            if (aspects.Any(a => a.Id == aspect.Id)) return;

            aspects.Add(aspect);
            foreach (var tool in aspect.Tools) VisitTool(tool);
        }

        void VisitTool(LabTool tool)
        {
            if (tools.Any(t => t.Id == tool.Id)) return;

            tools.Add(tool);
            foreach (var aspect in tool.Aspects) VisitAspect(aspect);
        }

        foreach (var aspect in Safe(DefineAspects)) VisitAspect(aspect);
        foreach (var tool in Safe(DefineTools)) VisitTool(tool);
        return (aspects, tools);
    }

    /// <inheritdoc />
    public LabTool? ActiveTool => _activeToolId is null ? null : Tools.FirstOrDefault(t => t.Id == _activeToolId && LabModelRules.IsActive(this, t.Owner));

    /// <inheritdoc />
    public void ActivateTool(LabTool? tool)
    {
        var before = ActiveTool;
        if (ReferenceEquals(before, tool)) return;

        _activeToolId = tool?.Id;
        try
        {
            before?.Activated?.Invoke(false);
            tool?.Activated?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Switching tools in {Lab} failed", _lab.Name);
        }

        RaiseModelChanged();
        RaiseActionsChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<LabCommandEntry> ModelCommands => Safe(DefineModelCommands).ToList();

    /// <inheritdoc />
    public IReadOnlyList<LabParameterSource> ParameterSources
    {
        get
        {
            var list = new List<LabParameterSource>();
            if (_lab.Parameters.Count > 0) list.Add(new LabParameterSource(LabOwner.Lab, _lab.Name, _lab.Parameters, true));

            list.AddRange(Safe(ExtraParameterSources));
            foreach (var aspect in Aspects)
                if (aspect.Parameters is { Count: > 0 } aspectSet)
                    list.Add(new LabParameterSource(LabOwner.OfAspect(aspect.Id), aspect.Title, aspectSet, true));

            foreach (var tool in Tools)
                if (tool.Parameters is { Count: > 0 } toolSet)
                    list.Add(new LabParameterSource(LabOwner.OfTool(tool.Id), tool.Name, toolSet, true));

            if (ActivePart is { Parameters: { Count: > 0 } partSet } part) list.Add(new LabParameterSource(LabOwner.Part, part.Name, partSet, false));
            return list;
        }
    }

    /// <inheritdoc />
    public Task NewSubjectAsync(LabSubjectType type, string content) => OnNewSubjectAsync(type, content);

    /// <inheritdoc />
    public Task AddPartAsync(LabPartType type, LabPart? parent, string content) => OnAddPartAsync(type, parent, content);

    private IEnumerable<T> Safe<T>(Func<IEnumerable<T>> get)
    {
        try
        {
            return get().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking {Lab} for its model failed", _lab.Name);
            return [];
        }
    }

    /// <summary>Call after the context actions, or whether an action can run, changed.</summary>
    protected void RaiseActionsChanged()
    {
        ActionsChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Copy the value as text, and keep it in the Silo.</summary>
    protected IEnumerable<ProvidedCommand> StandardValueActions()
    {
        yield return ProvidedCommand.From("copy-value", "_Copy Value", CopyValue, "TbCopy", () => CurrentValue is not null, "Copy the value to the clipboard as text");
        yield return ProvidedCommand.From("add-to-silo", "_Add to Silo", AddToSilo, "Data1", () => CurrentValue is not null && Stored.DefaultStore() is not null,
                                          "Keep the value in the Silo, with the other saved things (listed in the Explorer under Saved)");
    }

    private void CopyValue()
    {
        try
        {
            Clipboard.SetText(ValueText);
            Status = "Copied";
        }
        catch (Exception ex)
        {
            Status = "Could not copy: " + ex.Message;
        }
    }

    /// <summary>Keeps the value in the Silo, personal and permanent, under <c>lab:&lt;id&gt;:saved:&lt;value&gt;</c>.</summary>
    protected void AddToSilo()
    {
        if (CurrentValue is not { } value) return;

        try
        {
            var item = Stored.At<TValue>($"lab:{_lab.Id}:saved:{ValueText}");
            if (!item.IsAvailable)
            {
                Status = "Open a Silo to add this to.";
                return;
            }

            item.Set(value);
            Status = "Added to the Silo";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Adding a value of {Lab} to the Silo failed", _lab.Name);
            Status = "Could not add it to the Silo: " + ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        OnDispose();
    }

    /// <summary>The window closed: let go of what the session holds.</summary>
    protected virtual void OnDispose()
    {
    }
}
