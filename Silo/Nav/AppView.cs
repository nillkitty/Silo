using System.Windows;

namespace Silo.Nav;

public abstract record AppView(string Name, Func<UIElement> ContentBuilder);