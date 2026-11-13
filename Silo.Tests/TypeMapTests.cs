using System.Linq;
using System.Xml.Linq;
using Silo.Contracts.Core;
using Silo.Tools.TypeMap;

namespace Silo.Tests;

public class TypeMapTests
{
    [Test]
    public void DocTextFlattensCrefsParagraphsListsAndCode()
    {
        var e = XElement.Parse("""
                               <summary>
                                   Opens a <see cref="T:Silo.Tools.ToolBase" /> or <see langword="null" />, see <paramref name="x" />.
                                   <para>Second.</para>
                                   <list type="bullet"><item>one</item><item><term>two</term><description>2</description></item></list>
                                   <code>
                                       if (a)
                                           b();
                                   </code>
                               </summary>
                               """);

        Assert.That(XmlDocs.Text(e), Is.EqualTo("Opens a ToolBase or null, see x.\n\nSecond.\n\n• one\n• two — 2\n\nif (a)\n    b();"));
    }

    [Test]
    public void CrefNamesAreShort()
    {
        Assert.That(XmlDocs.CrefName("T:Silo.Tools.ToolBase"), Is.EqualTo("ToolBase"));
        Assert.That(XmlDocs.CrefName("M:Silo.App.Resolve``1"), Is.EqualTo("App.Resolve"));
        Assert.That(XmlDocs.CrefName("M:Silo.Drawio.Host.DrawioView.LoadXmlAsync(System.String)"), Is.EqualTo("DrawioView.LoadXmlAsync"));
        Assert.That(XmlDocs.CrefName("T:System.Collections.Generic.List`1"), Is.EqualTo("List"));
    }

    [Test]
    public void KindsAndNamesReadAsCSharp()
    {
        Assert.That(TypeMapModel.KindOf(typeof(ITool)), Is.EqualTo("interface"));
        Assert.That(TypeMapModel.KindOf(typeof(TypeDoc)), Is.EqualTo("record"));
        Assert.That(TypeMapModel.KindOf(typeof(TypeMapModel)), Is.EqualTo("static class"));
        Assert.That(TypeMapModel.KindOf(typeof(Silo.Tools.ToolBase)), Is.EqualTo("abstract class"));
        Assert.That(TypeMapModel.NameOf(typeof(Dictionary<,>)), Is.EqualTo("Dictionary<TKey, TValue>"));
        Assert.That(TypeMapModel.NameOf(typeof(Dictionary<string, int>.KeyCollection)), Is.EqualTo("Dictionary<TKey, TValue>.KeyCollection"));
    }

    [Test]
    public void TheToolIsASingletonITool()
    {
        var r = TypeMapModel.RegistrationOf(typeof(TypeMapTool));
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Service, Is.EqualTo(typeof(ITool)));
        Assert.That(r.Lifetime, Is.EqualTo("singleton"));
    }

    [Test]
    public void TheMapHasALaneOfBoxesJoinedByConnectors()
    {
        var source = new TypeMapDiagramSource();
        source.Options.Filters = ["Silo.Tools.TypeMap.", "Silo.Tools.ToolBase", "Silo.Contracts.Core.ITool"];
        source.Options.External = true;

        var model = source.Build();

        var tool = model.Nodes.Single(n => n.Id == "tm-Silo.Tools.TypeMap.TypeMapTool");
        Assert.That(tool.Parent, Is.EqualTo("tm-ns-Silo.Tools.TypeMap"));
        Assert.That(tool.Label, Does.Contain("singleton as ITool"));
        Assert.That(tool.Data!["kind"], Is.EqualTo("class"));

        // extends ToolBase; ToolBase implements ITool; the tool is registered as ITool
        Assert.That(model.Edges.Any(e => e.SourceId == tool.Id && e.TargetId == "tm-Silo.Tools.ToolBase"));
        Assert.That(model.Edges.Any(e => e.SourceId == tool.Id && e.TargetId == "tm-Silo.Contracts.Core.ITool" && e.Label == "singleton"));
        Assert.That(source.LastSummary, Does.Contain("types in"));
    }
}
