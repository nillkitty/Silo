using System.IO;
using System.Windows.Markup;
using Silo.DbModel;
using Silo.Providers.Data.Validation;

namespace Silo.Contracts.Core;

/// <summary>
///     Exclusive service that provides the New Silo dialog
/// </summary>
public interface INewSiloDialog
{
    NewSiloResult ShowDialog();
}

public interface IPlaceholderPopulationDialog
{
    /// <summary>
    ///     Gets the selected values, keyed by placeholder
    /// </summary>
    IDictionary<Placeholder, object> SelectedValues { get; }

    /// <summary>
    ///     Called once for each placeholder needing to be populated
    /// </summary>
    /// <param name="p">The placeholder definition</param>
    /// <param name="source">The template which defined the placeholder</param>
    /// <param name="isRequired">Whether the placeholder value is required</param>
    void AddPlaceholder(Placeholder p, SiloTemplate source, bool isRequired);

    /// <summary>
    ///     Called to show the dialog box, returns true if OK, false if Cancel
    /// </summary>
    bool? ShowDialog();
}

/// <summary>
///     Result from NewSiloDialog
/// </summary>
/// <param name="Cancel">True if user cancelled out of the dialog</param>
/// <param name="FilePath">The file path to create</param>
/// <param name="Template">The template to use</param>
public record NewSiloResult(bool Cancel, string? FilePath, SiloTemplate? Template);

public interface INewSiloBuilder
{
    /// <summary>
    ///     Create an empty Silo
    /// </summary>
    IDatabaseProvider Build(string path);

    /// <summary>
    ///     Create a Silo populated with items from a template
    /// </summary>
    IDatabaseProvider Build(string path, SiloTemplate template);
}

public interface ISiloLoader
{
    IDatabaseProvider Load(FileInfo fi);
}

[ContentProperty(nameof(Items))]
public class SiloTemplate
{
    public string       Description { get; set; }
    public Uri          Icon        { get; set; }
    public List<object> Items       { get; set; } = [];
    public string       Name        { get; set; }
}