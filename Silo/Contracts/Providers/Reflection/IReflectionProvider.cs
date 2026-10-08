using System.Windows.Controls;
using Silo.DbModel.Global;

namespace Silo.Contracts.Providers.Reflection;

public interface IAssemblyReference
{
    object GetAssembly();
}

public interface IAssemblyFilter
{
    bool IsIgnore(Assembly a);
}

public interface ITypeFilter
{
    bool IsIgnore(Type t);
}

public interface IPropertyFilter
{
    bool IsIgnore(Type t, string propertyName);
}

public interface ITableFilter
{
    bool IsIgnore(string database, string table);
}

public interface IColumnFilter
{
    bool IsIgnore(string table, string columnName, Type columnType);
}

public interface IReflectionProvider
{
    int                 Initialize(IAssemblyFilter filter);
    Assembly            Load(IAssemblyReference    r);
    List<Type>          GetConstructable<TType>();
    event EventHandler? TypesLoaded;
}

public interface IFileFormat
{
    string DisplayName   { get; }
    string FileExtension { get; }
}

public interface ILaunchable
{
    string? ArgTemplate     { get; }
    string? FilePath        { get; }
    bool    UseShellExecute { get; }
}

public interface IEditor<in TType> : IEditorProvider
{
    new Control? GetEditor(TType o);

    Control? IEditorProvider.GetEditor(object o)
    {
        return o is TType to ? GetEditor(to) : null;
    }
}

public interface IEditorProvider
{
    Control? GetEditor(object o);
}
