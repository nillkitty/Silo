using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Providers.Document;
using Silo.Providers.Format;

namespace Silo.Tests.Providers;

public class YamlJsonTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-yamljson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Parses <paramref name="yaml" /> with the real <see cref="YamlFormat" /> and converts every document in it.</summary>
    private JToken Convert(string yaml)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return YamlJson.Convert(new YamlFormat().Read(fs).ToList());
    }

    private string Json(string yaml) => Convert(yaml).ToString(Formatting.None);

    /// <summary>Like <see cref="Json" />, keeping custom tags and comments.</summary>
    private string Annotated(string yaml)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return YamlJson.Convert(new YamlFormat().Read(fs).ToList(), annotate: true).ToString(Formatting.None);
    }

    [TestCase("hello", """{"a":"hello"}""")]
    [TestCase("42", """{"a":42}""")]
    [TestCase("-7", """{"a":-7}""")]
    [TestCase("0x1F", """{"a":31}""")]
    [TestCase("0o17", """{"a":15}""")]
    [TestCase("3.5", """{"a":3.5}""")]
    [TestCase("true", """{"a":true}""")]
    [TestCase("False", """{"a":false}""")]
    [TestCase("null", """{"a":null}""")]
    [TestCase("~", """{"a":null}""")]
    [TestCase("yes", """{"a":"yes"}""")]
    [TestCase("2024-01-02", """{"a":"2024-01-02"}""")]
    [TestCase("1.2.3", """{"a":"1.2.3"}""")]
    [TestCase("12345678901234567890", """{"a":12345678901234567890}""")]
    public void Plain_scalars_take_the_type_the_YAML_core_schema_gives_them(string value, string expected)
    {
        Assert.That(Json("a: " + value), Is.EqualTo(expected));
    }

    [Test]
    public void An_empty_value_is_null()
    {
        Assert.That(Json("a:\n"), Is.EqualTo("""{"a":null}"""));
    }

    [Test]
    public void Infinity_and_not_a_number_are_doubles()
    {
        var o = (JObject)Convert("a: .inf\nb: -.inf\nc: .nan\n");

        Assert.That(o["a"]!.Value<double>(), Is.EqualTo(double.PositiveInfinity));
        Assert.That(o["b"]!.Value<double>(), Is.EqualTo(double.NegativeInfinity));
        Assert.That(double.IsNaN(o["c"]!.Value<double>()));
    }

    [Test]
    public void Quoted_and_block_scalars_stay_strings()
    {
        Assert.That(Json("a: \"42\"\nb: 'true'\nc: |\n  null\n"), Is.EqualTo("""{"a":"42","b":"true","c":"null\n"}"""));
    }

    [Test]
    public void Tags_override_what_the_text_looks_like()
    {
        Assert.That(Json("a: !!str 42\nb: !!int \"42\"\nc: !!float 3\nd: !blah 7\n"),
                    Is.EqualTo("""{"a":"42","b":42,"c":3.0,"d":"7"}"""));
    }

    [Test]
    public void A_tag_the_text_does_not_fit_leaves_the_text()
    {
        Assert.That(Json("a: !!int abc\n"), Is.EqualTo("""{"a":"abc"}"""));
    }

    [Test]
    public void Mappings_and_sequences_nest()
    {
        Assert.That(Json("a:\n  b: [1, x, true]\n  c:\n    - d: 1\n    - d: 2\n"),
                    Is.EqualTo("""{"a":{"b":[1,"x",true],"c":[{"d":1},{"d":2}]}}"""));
    }

    [Test]
    public void The_last_of_a_repeated_key_wins()
    {
        Assert.That(Json("a: 1\na: 2\n"), Is.EqualTo("""{"a":2}"""));
    }

    [Test]
    public void Keys_are_written_as_text()
    {
        Assert.That(Json("1: one\ntrue: yes\n"), Is.EqualTo("""{"1":"one","true":"yes"}"""));
    }

    [Test]
    public void A_key_that_is_a_collection_becomes_its_JSON_text()
    {
        Assert.That(Json("? [a, b]\n: v\n"), Is.EqualTo("""{"[\"a\",\"b\"]":"v"}"""));
    }

    [Test]
    public void An_alias_repeats_the_anchored_value()
    {
        Assert.That(Json("a: &x {k: 1}\nb: *x\n"), Is.EqualTo("""{"a":{"k":1},"b":{"k":1}}"""));
    }

    [Test]
    public void A_merge_key_brings_in_another_mapping_and_own_entries_win()
    {
        var yaml = "base: &b {a: 1, b: 2}\nuse:\n  <<: *b\n  b: 9\n  c: 3\n";
        Assert.That(Json(yaml), Is.EqualTo("""{"base":{"a":1,"b":2},"use":{"a":1,"b":9,"c":3}}"""));
    }

    [Test]
    public void Of_several_merged_mappings_the_earlier_one_wins()
    {
        var yaml = "x: &x {a: 1, b: 2}\ny: &y {a: 10, z: 26}\nuse:\n  <<: [*x, *y]\n";
        Assert.That(Json(yaml), Is.EqualTo("""{"x":{"a":1,"b":2},"y":{"a":10,"z":26},"use":{"a":1,"b":2,"z":26}}"""));
    }

    [Test]
    public void A_quoted_merge_key_is_an_ordinary_key()
    {
        Assert.That(Json("\"<<\": q\n"), Is.EqualTo("""{"<<":"q"}"""));
    }

    [Test]
    public void No_documents_is_an_empty_array_one_is_its_value_several_are_an_array()
    {
        Assert.That(YamlJson.Convert(new List<YamlDocNode>()).ToString(Formatting.None), Is.EqualTo("[]"));
        Assert.That(Json("a: 1\n"), Is.EqualTo("""{"a":1}"""));
        Assert.That(Json("a: 1\n---\nb: 2\n"), Is.EqualTo("""[{"a":1},{"b":2}]"""));
    }

    [Test]
    public void A_document_that_is_a_single_scalar_is_that_value()
    {
        Assert.That(Json("42\n"), Is.EqualTo("42"));
    }

    [Test]
    public void An_alias_bomb_is_refused_rather_than_expanded()
    {
        var lines = new List<string> { "l0: &l0 [x, x, x, x, x, x, x, x, x, x]" };
        for (int i = 1; i <= 9; i++)
        {
            var p = "*l" + (i - 1);
            lines.Add($"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat(p, 10))}]");
        }

        Assert.Throws<InvalidDataException>(() => Convert(string.Join("\n", lines) + "\n"));
    }

    [Test]
    public void Tags_and_comments_are_left_out_unless_asked_for()
    {
        Assert.That(Json("# about\nport: !secret 8080 # prod\n"), Is.EqualTo("""{"port":"8080"}"""));
    }

    [Test]
    public void A_custom_tag_is_kept_in_a_wrapper()
    {
        Assert.That(Annotated("port: !secret 8080\n"), Is.EqualTo("""{"port":{"$tag":"!secret","$value":"8080"}}"""));
    }

    [Test]
    public void A_custom_tag_on_a_mapping_or_sequence_is_kept()
    {
        Assert.That(Annotated("a: !point {x: 1}\nb: !list [1]\n"),
                    Is.EqualTo("""{"a":{"$tag":"!point","$value":{"x":1}},"b":{"$tag":"!list","$value":[1]}}"""));
    }

    [Test]
    public void A_standard_tag_only_decides_the_type()
    {
        Assert.That(Annotated("a: !!int \"42\"\n"), Is.EqualTo("""{"a":42}"""));
    }

    [Test]
    public void Comments_before_an_entry_and_after_it_are_kept_with_its_value()
    {
        var yaml = "name: x\n# the listen port\n# (prod only)\nport: 8080 # tcp\n";
        Assert.That(Annotated(yaml), Is.EqualTo(
                        """{"name":"x","port":{"$comment":"the listen port\n(prod only)","$inlineComment":"tcp","$value":8080}}"""));
    }

    [Test]
    public void A_comment_at_the_top_of_a_document_belongs_to_the_whole_document()
    {
        Assert.That(Annotated("# settings\na: 1\n"), Is.EqualTo("""{"$comment":"settings","$value":{"a":1}}"""));
    }

    [Test]
    public void Comments_on_sequence_items_are_kept()
    {
        Assert.That(Annotated("- a\n# second\n- b # last\n"),
                    Is.EqualTo("""["a",{"$comment":"second","$inlineComment":"last","$value":"b"}]"""));
    }

    [Test]
    public void A_tag_and_comments_together_share_one_wrapper()
    {
        Assert.That(Annotated("a: 1\n# note\nv: !secret 1\n"),
                    Is.EqualTo("""{"a":1,"v":{"$tag":"!secret","$comment":"note","$value":"1"}}"""));
    }

    [Test]
    public void A_merge_still_works_when_annotating()
    {
        var yaml = "base: &b !base {a: 1}\nuse:\n  <<: *b\n  c: 2\n";
        Assert.That(Annotated(yaml), Is.EqualTo(
                        """{"base":{"$tag":"!base","$value":{"a":1}},"use":{"a":1,"c":2}}"""));
    }
}
