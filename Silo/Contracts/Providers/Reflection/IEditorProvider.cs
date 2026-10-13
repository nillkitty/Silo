using System.Windows.Controls;

namespace Silo.Contracts.Providers.Reflection;

public interface IEditorProvider
{
    Control? GetEditor(object o);
}

public interface IEditor<in TType> : IEditorProvider
{
    new Control? GetEditor(TType o);

    Control? IEditorProvider.GetEditor(object o)
    {
        return o is TType to ? GetEditor(to) : null;
    }
}