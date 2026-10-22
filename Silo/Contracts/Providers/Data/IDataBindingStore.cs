using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.Providers.Data;

/// <summary>
///     Reads and writes the <see cref="DataBinding" />s stored inside a Silo document.
/// </summary>
public interface IDataBindingStore
{
    bool DeleteDataBinding(int id);

    DataBinding? GetDataBinding(int id);

    IReadOnlyList<DataBinding> ListDataBindings();

    /// <summary>Inserts (<c>Id &lt;= 0</c>) or updates a binding and returns it with its id assigned.</summary>
    DataBinding SaveDataBinding(DataBinding binding);
}