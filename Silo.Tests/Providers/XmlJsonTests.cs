using System.IO;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Providers.Format;

namespace Silo.Tests.Providers;

public class XmlJsonTests
{
    private static string Json(string xml) => XmlJson.Convert(XDocument.Parse(xml)).ToString(Formatting.None);

    [Test]
    public void The_root_element_names_the_only_property()
    {
        Assert.That(Json("<a>hi</a>"), Is.EqualTo("""{"a":"hi"}"""));
    }

    [Test]
    public void An_empty_element_is_null()
    {
        Assert.That(Json("<a/>"), Is.EqualTo("""{"a":null}"""));
    }

    [Test]
    public void Attributes_are_properties_with_an_at_sign()
    {
        Assert.That(Json("""<a x="1"/>"""), Is.EqualTo("""{"a":{"@x":"1"}}"""));
    }

    [Test]
    public void Text_beside_attributes_goes_in_hash_text()
    {
        Assert.That(Json("""<a x="1">t</a>"""), Is.EqualTo("""{"a":{"@x":"1","#text":"t"}}"""));
    }

    [Test]
    public void Child_elements_are_properties()
    {
        Assert.That(Json("<a><b>1</b><c/></a>"), Is.EqualTo("""{"a":{"b":"1","c":null}}"""));
    }

    [Test]
    public void Repeated_children_become_an_array_where_the_first_one_was()
    {
        Assert.That(Json("<a><b>1</b><c>x</c><b>2</b></a>"), Is.EqualTo("""{"a":{"b":["1","2"],"c":"x"}}"""));
    }

    [Test]
    public void Indentation_between_elements_is_not_text()
    {
        Assert.That(Json("<a>\n  <b>1</b>\n</a>"), Is.EqualTo("""{"a":{"b":"1"}}"""));
    }

    [Test]
    public void Mixed_content_keeps_its_text_trimmed_and_joined()
    {
        Assert.That(Json("<p>Hello <b>x</b> world</p>"), Is.EqualTo("""{"p":{"b":"x","#text":"Hello world"}}"""));
    }

    [Test]
    public void CDATA_is_text_and_comments_and_instructions_are_left_out()
    {
        Assert.That(Json("<a><![CDATA[<raw>&]]></a>"), Is.EqualTo("""{"a":"<raw>&"}"""));
        Assert.That(Json("<a><!-- c --><?pi x?>t</a>"), Is.EqualTo("""{"a":"t"}"""));
    }

    [Test]
    public void Escaped_characters_are_unescaped()
    {
        Assert.That(Json("<a>&lt;&amp;</a>"), Is.EqualTo("""{"a":"<&"}"""));
    }

    [Test]
    public void Prefixes_are_kept_as_written()
    {
        var json = Json("""<a xmlns="urn:d" xmlns:p="urn:p" p:k="v"><p:b>1</p:b><c/></a>""");
        Assert.That(json, Is.EqualTo("""{"a":{"@xmlns":"urn:d","@xmlns:p":"urn:p","@p:k":"v","p:b":"1","c":null}}"""));
    }

    [Test]
    public void A_namespace_declaration_is_kept_even_on_an_otherwise_empty_element()
    {
        Assert.That(Json("""<a xmlns="urn:x"/>"""), Is.EqualTo("""{"a":{"@xmlns":"urn:x"}}"""));
        Assert.That(Json("""<a xmlns="urn:x">t</a>"""), Is.EqualTo("""{"a":{"@xmlns":"urn:x","#text":"t"}}"""));
    }

    [Test]
    public void Prefixed_attributes_and_elements_keep_their_prefix_and_plain_ones_have_none()
    {
        var json = Json("""<a xmlns:p="urn:p"><p:b p:id="1" plain="2"/><p:b/></a>""");
        Assert.That(json, Is.EqualTo("""{"a":{"@xmlns:p":"urn:p","p:b":[{"@p:id":"1","@plain":"2"},null]}}"""));
    }

    [Test]
    public void The_xml_prefix_and_a_redeclared_default_namespace_are_kept()
    {
        Assert.That(Json("""<a xml:lang="en">hi</a>"""), Is.EqualTo("""{"a":{"@xml:lang":"en","#text":"hi"}}"""));
        Assert.That(Json("""<a xmlns="urn:x"><b xmlns="urn:y" k="v"/></a>"""),
                    Is.EqualTo("""{"a":{"@xmlns":"urn:x","b":{"@xmlns":"urn:y","@k":"v"}}}"""));
    }

    [Test]
    public void A_document_without_a_root_is_an_empty_object()
    {
        Assert.That(XmlJson.Convert(new XDocument()).ToString(Formatting.None), Is.EqualTo("{}"));
    }

    [Test]
    public void Nesting_beyond_the_limit_is_refused_not_a_stack_overflow()
    {
        int    n    = XmlJson.MaxDepth + 1;
        string deep = string.Concat(Enumerable.Repeat("<a>", n)) + string.Concat(Enumerable.Repeat("</a>", n));

        Assert.Throws<InvalidDataException>(() => XmlJson.Convert(XDocument.Parse(deep)));
    }

    [Test]
    public void The_document_reads_through_the_format()
    {
        var path = Path.Combine(Path.GetTempPath(), "silo-xmljson-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<r><i>1</i><i>2</i></r>");
            using var fs  = new FileStream(path, FileMode.Open, FileAccess.Read);
            var       doc = new XmlFormat().Read(fs).Single();

            Assert.That(XmlJson.Convert(doc).ToString(Formatting.None), Is.EqualTo("""{"r":{"i":["1","2"]}}"""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
