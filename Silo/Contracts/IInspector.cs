using System.Windows.Controls;

namespace Silo.Contracts;

public interface IInspector
{
    Control? GetInspector(object o);
}