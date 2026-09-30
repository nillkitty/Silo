namespace Silo.Ui;

// Fully-qualifies the base type (rather than `using System.Windows.Controls;` + a bare
// `ToolBar` base) because this class is itself named ToolBar in a different namespace -
// an unqualified reference here would be ambiguous with itself.
public class ToolBar : System.Windows.Controls.ToolBar
{
}
