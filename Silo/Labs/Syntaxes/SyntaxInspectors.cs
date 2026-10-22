using Silo.Providers.Data;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Providers.Definitions;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;
using Silo.Labs.Web;

namespace Silo.Labs.Syntaxes;

/// <summary>The syntax as the Silo would keep it (JSON) and what it is made of.</summary>
internal sealed class SyntaxDetailsInspector(SyntaxLabSession session) : LiveInspector(session)
{
    private TextBox? _text;

    public override string Id => "details";

    public override string Title => "Details";

    public override string IconKey => "Analysis";

    protected override UIElement Create()
    {
        _text = LabUi.Output(wrap: true);
        var copy = LabUi.Button("Copy JSON", () => LabUi.Copy(SyntaxTrial.ToJson(session.Draft)), "Put the syntax as a JSON document on the clipboard");
        return LabUi.Layout(null, _text, LabUi.Bar(copy));
    }

    protected override void Refresh()
    {
        if (_text is null) return;

        var d     = session.Draft;
        var build = session.Build;
        var text  = new StringBuilder();
        text.AppendLine(session.IsSaved ? $"In the Silo as #{d.Id}." : "Not in the Silo yet.");
        if (build.Syntax is { } syntax) text.AppendLine($"Makes a {syntax.ValueType.Name}.");
        var (passed, expected) = session.Score;
        if (expected > 0) text.AppendLine($"{passed} of {expected} strings came out as expected.");
        if (session.Results.Count(r => r.IsValid && !r.RoundTrips) is > 0 and var odd) text.AppendLine($"{odd} valid string(s) do not round trip.");
        text.AppendLine();
        if (build.Problems.Count > 0)
        {
            text.AppendLine("Problems:");
            foreach (string p in build.Problems) text.AppendLine("  " + p);
            text.AppendLine();
        }

        text.Append(SyntaxTrial.ToJson(d));
        _text.Text = text.ToString();
    }
}
