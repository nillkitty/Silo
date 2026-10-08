using System.IO;
using System.Windows.Markup;

namespace Silo.Contracts.Core;

public interface INewSiloDialog
{
    NewSiloResult ShowDialog();
}

public record NewSiloResult(bool Cancel, string FilePath, SiloTemplate Template);

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