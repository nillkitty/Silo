namespace Silo.Providers.Data;

/// <summary>
///     What a data specification says about how its data is best edited: the kind of editor (<see cref="Kind" />, what an
///     <see cref="IDataEditor" /> answers to) and that editor's settings. It is part of the specification, so it is kept in the Silo with it and
///     travels with it.
/// </summary>
public interface IDataEditorSpec
{
    /// <summary>The kind of editor ("sprite-mappings"); an <see cref="IDataEditor" /> with this <see cref="IDataEditor.Kind" /> opens it.</summary>
    string Kind { get; }

    /// <summary>What the editor is called to a person ("Sonic Mappings Editor").</summary>
    string Title { get; }

    /// <summary>
    ///     The editor's own settings, by name: which rule of the specification holds what, which other specifications it uses, the files it last
    ///     had open. What the names mean is the editor's business (<see cref="IDataEditor.DefaultSettings" />).
    /// </summary>
    IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>
///     An editor for the data of a kind of specification, found in the container (<c>[Singleton(typeof(IDataEditor))]</c>) and opened for a
///     specification whose <see cref="IDataSpec.Editor" /> names its <see cref="Kind" />. The editor reads and writes the data through the
///     specification, so one editor serves every specification with the same shape (the same fields under the same names, which
///     <see cref="IDataEditorSpec.Settings" /> maps).
/// </summary>
public interface IDataEditor
{
    /// <summary>The kind of <see cref="IDataEditorSpec" /> this opens.</summary>
    string Kind { get; }

    /// <summary>What it is called to a person.</summary>
    string DisplayName { get; }

    /// <summary>The key of its icon (Resources/NavIcons.xaml).</summary>
    string IconKey => "Edit";

    /// <summary>The settings a specification that wants this editor starts with.</summary>
    IReadOnlyDictionary<string, string> DefaultSettings { get; }

    /// <summary>Whether the editor can work with the specification (its mode, its rules, the fields it needs).</summary>
    bool CanEdit(IDataSpec spec, IDataEditorSpec editor);

    /// <summary>What is wrong with the specification as this editor's: the rules or fields it expects and cannot find; empty when all is well.</summary>
    IReadOnlyList<string> Problems(IDataSpec spec, IDataEditorSpec editor) => [];

    /// <summary>Opens a window that edits data with the specification. It is shown at once.</summary>
    void Open(IDataSpec spec, IDataEditorSpec editor);
}
