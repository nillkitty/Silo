using System.Windows.Controls;

namespace Silo.Contracts.UI;

public interface IInspector
{
    Control? GetInspector(object o);
}
