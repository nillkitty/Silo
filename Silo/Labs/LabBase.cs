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
public abstract class LabSession<TValue> : ILabSession, ILabReporter
{
    private readonly LabBase<TValue> _lab;
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

    public virtual IReadOnlyList<LabInspector> Inspectors => [];

    public virtual IReadOnlyList<LabTerminalCommand> TerminalCommands => [];

    /// <summary>Studies the value.</summary>
    protected abstract Task OpenValueAsync(TValue value);

    /// <summary>Studies "nothing in particular" (a blank page, an empty text): what <c>OpenAsync(null)</c> does.</summary>
    protected virtual Task OpenDefaultAsync() => Task.CompletedTask;

    /// <summary>The window is attached: <see cref="Host" /> can be used.</summary>
    protected virtual void OnAttached()
    {
    }

    /// <summary>Call after <see cref="CurrentValue" /> changed.</summary>
    protected void RaiseValueChanged()
    {
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Raise(nameof(ValueText));
        Raise(nameof(Title));
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
