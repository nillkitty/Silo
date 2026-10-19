using System.IO;

namespace Silo.Tests.Definitions;

/// <summary>
///     Exercises <see cref="DefinitionMatcher" /> with definitions built in code to mirror Resources/DataDef/BasicFormats.xaml.
/// </summary>
public class DataDefinitionMatcherTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // <silo:DataDefinition x:Key="IniValue">
    private static DataDefinition IniValue()
    {
        return Def(new Capture
                   {
                       To = InstanceNum.Of(0),
                       Items =
                       {
                           new Split
                           {
                               Delimiter = "=", Qualifier = "\\\"", Escape = "\\", MaxTokens = 2,
                               Items =
                               {
                                   new Capture { To = "Key", Arity   = Arity.One, Items      = { new Rest() } },
                                   new Capture { To = "Value", Arity = Arity.Optional, Items = { new Rest() } }
                               }
                           }
                       }
                   });
    }

    // <silo:DataDefinition ContentType="text/ini" x:Key="IniFile">
    private static DataDefinition IniFile()
    {
        var d = Def(new TextDefinition
                    {
                        Items =
                        {
                            new Lines
                            {
                                Newline = NewLine.CrLf | NewLine.Lax, Consolidate = true,
                                Items =
                                {
                                    new Any
                                    {
                                        Items =
                                        {
                                            new Capture { To = "Section[{1}]", Items = { new Span { Start = "[", End = "]" } } },
                                            IniValue()
                                        }
                                    }
                                }
                            }
                        }
                    });
        d.ContentType = "text/ini";
        return d;
    }

    // <silo:TextDefinition x:Key="CsvData">
    private static TextDefinition CsvData()
    {
        return new TextDefinition
               {
                   Items =
                   {
                       new Split
                       {
                           Delimiter = ",", Qualifier = "\\\"", Escape = "\\\\",
                           Items     = { new Capture { To = "Col[{0}]", Items = { new Rest() } } }
                       }
                   }
               };
    }

    // <silo:DataDefinition ContentType="text/csv" x:Key="CsvFile">
    private static DataDefinition CsvFile()
    {
        var d = Def(new TextDefinition
                    {
                        Items =
                        {
                            new Lines
                            {
                                Newline = NewLine.CrLf | NewLine.Lax,
                                Items =
                                {
                                    new DataDefinition
                                    {
                                        Name  = "Header", Arity = Arity.One,
                                        Items = { new Split { Delimiter = ",", Qualifier = "\\\"", Items = { new Capture { To = "Header[{0}]" } } } }
                                    },
                                    new DataDefinition { Name = "Row", Arity = Arity.Any, Items = { CsvData() } }
                                }
                            }
                        }
                    });
        d.ContentType = "text/csv";
        return d;
    }

    private static DataDefinition Def(params IDefPart[] parts)
    {
        var d = new DataDefinition();
        d.Items.AddRange(parts);
        return d;
    }

    [Test]
    public void CsvData_UnquotesAndNumbersColumns()
    {
        var root = CsvData().Deserialize("a,\"b,c\",\"say \\\"hi\\\"\",d\\\\e");

        Assert.That(root.Children.Select(c => c.Name), Is.EqualTo(new[] { "Col[0]", "Col[1]", "Col[2]", "Col[3]" }));
        Assert.That(root["Col[1]"]!.Text, Is.EqualTo("b,c"));
        Assert.That(root["Col[2]"]!.Text, Is.EqualTo("say \"hi\""));
        Assert.That(root["Col[3]"]!.Text, Is.EqualTo("d\\e"));
    }

    [Test]
    public void CsvData_KeepsEmptyCells()
    {
        var root = CsvData().Deserialize("a,,c,");
        Assert.That(root.Children.Select(c => c.Text), Is.EqualTo(new[] { "a", "", "c", "" }));
    }

    [Test]
    public void CsvFile_HeaderThenRows()
    {
        var bytes = Utf8.GetBytes("name,qty\r\nwidget,3\r\n\"gadget, large\",12\r\n");
        var m     = CsvFile().Match(bytes);

        Assert.That(m, Is.Not.Null);
        Assert.That(m!.Length, Is.EqualTo(bytes.Length));
        Assert.That(m.Root.Name, Is.EqualTo("text/csv"));

        var header = m.Root["Header"]!;
        Assert.That(header.Children.Select(c => c.Text), Is.EqualTo(new[] { "name", "qty" }));

        var rows = m.Root.All("Row").ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[1]["Col[0]"]!.Text, Is.EqualTo("gadget, large"));
        Assert.That(rows[1]["Col[1]"]!.Text, Is.EqualTo("12"));
    }

    [Test]
    public void CsvFile_ConsumesUtf8Bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8.GetBytes("a,b\n1,2")).ToArray();
        var m     = CsvFile().Match(bytes);

        Assert.That(m, Is.Not.Null);
        Assert.That(m!.Root["Header"]!["Header[0]"]!.Text, Is.EqualTo("a"));
    }

    [Test]
    public void CsvFile_EmptyIsNotACsv()
    {
        Assert.That(CsvFile().IsMatch(Array.Empty<byte>()), Is.False);
        Assert.That(CsvFile().Probe(Array.Empty<byte>()), Is.Null, "an empty prefix could still become a CSV");
    }

    [Test]
    public void IniValue_KeyAndOptionalValue()
    {
        var root = IniValue().Deserialize("path=\"C:\\\\x=y\"");
        var kv   = root.Children.Single();

        Assert.That(kv.Name, Is.EqualTo("0"));
        Assert.That(kv["Key"]!.Text, Is.EqualTo("path"));
        Assert.That(kv["Value"]!.Text, Is.EqualTo("C:\\x=y"));

        var bare = IniValue().Deserialize("flag").Children.Single();
        Assert.That(bare["Key"]!.Text, Is.EqualTo("flag"));
        Assert.That(bare["Value"], Is.Null);
    }

    [Test]
    public void IniFile_SectionsAndValues()
    {
        var text = "[main]\r\nname=Silo\r\n\r\n[paths]\r\nroot=c:\\silo\r\n";
        var m    = IniFile().Match(Utf8.GetBytes(text));

        Assert.That(m, Is.Not.Null);
        var lines = m!.Root.Children;
        Assert.That(lines.Select(c => c.Name), Is.EqualTo(new[] { "Section[0]", "1", "Section[0]", "3" }));
        Assert.That(lines[0].Text, Is.EqualTo("[main]"));
        Assert.That(lines[3]["Value"]!.Text, Is.EqualTo("c:\\silo"));
    }

    [Test]
    public void InstanceNum_ExpandsByDepth()
    {
        var def = new TextDefinition
                  {
                      Items =
                      {
                          new Lines
                          {
                              Items =
                              {
                                  new Capture
                                  {
                                      To    = "Row[{0}]",
                                      Items = { new Split { Delimiter = ",", Items = { new Capture { To = "Cell[{1}][{0}]" } } } }
                                  }
                              }
                          }
                      }
                  };

        var root = def.Deserialize("a,b\nc,d");
        Assert.That(root["Row[1]"]!.Children.Select(c => c.Name), Is.EqualTo(new[] { "Cell[1][0]", "Cell[1][1]" }));

        var braces = new Split { Delimiter = ",", Items = { new Capture { To = "{{lit}}{0}" } } };
        Assert.That(braces.Deserialize("x,y").Children.Select(c => c.Name), Is.EqualTo(new[] { "{lit}0", "{lit}1" }));
    }

    [Test]
    public void Span_Variants()
    {
        Assert.That(new Span { While = "a-z0-9_" }.IsMatch("abc_9"), Is.True);
        Assert.That(new Span { While = "a-z" }.IsMatch("abC"), Is.False);
        Assert.That(new Span { WhileNot = " \\t" }.IsMatch("x y"), Is.False);
        Assert.That(new Span { Exactly = "GET" }.IsMatch("GET"), Is.True);
        Assert.That(new Span { Start = "<", End = ">", Items = { new Span { While = "a-z" } } }.IsMatch("<abc>"), Is.True);
        Assert.That(Def(new Span { Until = "=" }, new Span { Exactly = "=" }, new Rest()).IsMatch("key=value"), Is.True);
        Assert.That(Def(new Span { Until = "=" }, new Rest()).IsMatch("no equals"), Is.False);
    }

    [Test]
    public void Any_Not_End_Skip()
    {
        var def = Def(new Skip { Values = [' '] },
                      new Any { Items = { new Span { Exactly = "yes" }, new Span { Exactly = "no" } } },
                      new Not { Items = { new Span { Exactly = "!" } } },
                      new Rest());
        Assert.That(def.IsMatch("   no."), Is.True);
        Assert.That(def.IsMatch("   no!"), Is.False);
        Assert.That(def.IsMatch("maybe"), Is.False);
        Assert.That(Def(new Span { Exactly = "x" }, new End()).IsMatch("x"), Is.True);
    }

    [Test]
    public void Binary_MagicLengthPrefixedChunks()
    {
        // A tiny PNG-like layout: magic, then chunks of [u32 BE length][4-byte type][data][u32 crc].
        var chunk = Def(new Capture { To = "Length", Items = { new Int { Size = 4, BigEndian = true, Let = "len" } } },
                        new Capture { To = "Type", Items   = { new Span { While = "a-zA-Z" } } },
                        new Capture { To = "Data", Items   = { new Block { Length = "len", Items = { new Rest() } } } },
                        new Skip { Bytes = 4 });
        chunk.Name  = "Chunk";
        chunk.Arity = Arity.OneOrMore;

        var png = Def(new Bytes { Hex = "89 50 4E 47 0D 0A 1A 0A" }, chunk);

        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
                   .Concat(new byte[] { 0, 0, 0, 2 }).Concat("IHDR"u8.ToArray()).Concat(new byte[] { 7, 8 }).Concat(new byte[4])
                   .Concat(new byte[] { 0, 0, 0, 0 }).Concat("IEND"u8.ToArray()).Concat(new byte[4])
                   .ToArray();

        var root   = png.Deserialize(data);
        var chunks = root.All("Chunk").ToList();
        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks[0]["Length"]!.Value, Is.EqualTo(2L));
        Assert.That(chunks[0]["Type"]!.Text, Is.EqualTo("IHDR"));
        Assert.That(chunks[0]["Data"]!.Value, Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That(chunks[1]["Type"]!.Text, Is.EqualTo("IEND"));

        Assert.That(png.Probe(data[..10]), Is.Null, "truncated data is indeterminate");
        Assert.That(png.Probe(new byte[] { 1, 2, 3 }), Is.False);
    }

    [Test]
    public void Binary_IntConstraints()
    {
        var def = Def(new Bytes { Ascii = "PK\\x03\\x04" }, new Int { Size = 2, Min = 10, Max = 63 }, new Rest());
        Assert.That(def.IsMatch([0x50, 0x4B, 3, 4, 20, 0, 9, 9]), Is.True);
        Assert.That(def.IsMatch([0x50, 0x4B, 3, 4, 200, 0]), Is.False);
    }

    [Test]
    public void Locate_FindsEmbeddedOccurrences()
    {
        var sig  = Def(new Capture { To = "Sig", Items = { new Bytes { Ascii = "PK\\x03\\x04" } } }, new Int { Size = 2, Let = "n" },
                       new Capture { To = "Name", Items = { new Block { Length = "n", Items = { new Span { While = "a-z." } } } } });
        var blob = new byte[] { 1, 2, 3 }.Concat("PK\x03\x04"u8.ToArray()).Concat(new byte[] { 5, 0 }).Concat("a.txt"u8.ToArray())
                   .Concat(new byte[] { 9, 9 }).Concat("PK\x03\x04"u8.ToArray()).Concat(new byte[] { 3, 0 }).Concat("b.c"u8.ToArray())
                   .ToArray();

        var found = sig.Locate(blob).ToList();
        Assert.That(found.Select(f => f.Offset), Is.EqualTo(new[] { 3, 16 }));
        Assert.That(found.Select(f => f.Root["Name"]!.Text), Is.EqualTo(new[] { "a.txt", "b.c" }));
    }

    [Test]
    public void Locate_TextInsideBinary()
    {
        var section = Def(new Capture { To = "Name", Items = { new Span { Start = "[", End = "]", Items = { new Span { While = "a-z" } } } } });
        var blob    = new byte[] { 0, 0xFF }.Concat("[abc]"u8.ToArray()).Concat(new byte[] { 0xFE, 0 }).ToArray();

        var m = section.Locate(blob).Single();
        Assert.That(m.Offset, Is.EqualTo(2));
        Assert.That(m.Length, Is.EqualTo(5));
        Assert.That(m.Root["Name"]!.Text, Is.EqualTo("[abc]"));
    }

    [Test]
    public void Utf16Text_IsDetected()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("x,y\n1,2")).ToArray();
        var m     = CsvFile().Match(bytes);
        Assert.That(m, Is.Not.Null);
        Assert.That(m!.Encoding, Is.InstanceOf<UnicodeEncoding>());
        Assert.That(m.Root.All("Row").Single()["Col[1]"]!.Text, Is.EqualTo("2"));
    }

    [Test]
    public void Stream_MatchesDataAndLeavesPosition()
    {
        using var ms = new MemoryStream(Utf8.GetBytes("xx[a]=1"));
        ms.Position = 2;
        Assert.That(IniValue().MatchesData(ms), Is.True);
        Assert.That(ms.Position, Is.EqualTo(2));
    }

    [Test]
    public void ToDictionary_GroupsRepeatedNames()
    {
        var d = CsvFile().Deserialize(Utf8.GetBytes("h\n1\n2")).ToDictionary();
        Assert.That(d["Row"], Is.InstanceOf<List<object?>>());
        Assert.That(((List<object?>)d["Row"]!).Count, Is.EqualTo(2));
    }

    [Test]
    public void Elements_GiveBackRepetitionsForALaterPart()
    {
        // Every line is a "Row", but the last one must be the footer: the rows give it back.
        var def = new Lines
                  {
                      Items =
                      {
                          new Capture { To = "Row[{0}]", Arity = Arity.Any, Items = { new Span { While = "a-z0-9=" } } },
                          new Capture { To = "Total", Arity  = Arity.One, Items = { new Span { Start = "total=", While = "0-9" } } }
                      }
                  };

        var root = def.Deserialize("a=1\nb=2\ntotal=3\n");
        Assert.That(root.Children.Select(c => c.Name), Is.EqualTo(new[] { "Row[0]", "Row[1]", "Total" }));
    }

    [Test]
    public void Lines_StrictNewline()
    {
        var crlfOnly = new Lines { Newline = NewLine.CrLf, Items = { new Capture { To = "L{0}" } } };
        Assert.That(crlfOnly.Deserialize("a\nb\r\nc").Children.Select(c => c.Text), Is.EqualTo(new[] { "a\nb", "c" }));
    }

    [Test]
    public void Split_Whitespace_And_DoubledQuoteEscape()
    {
        var words = new Split { Items = { new Capture { To = "W{0}" } } };
        Assert.That(words.Deserialize("  one  two\tthree ").Children.Select(c => c.Text), Is.EqualTo(new[] { "one", "two", "three" }));

        var excel = new Split { Delimiter = ",", Qualifier = "\"", Escape = "\"", Items = { new Capture { To = "C{0}" } } };
        Assert.That(excel.Deserialize("\"5\"\" pipe\",x").Children.Select(c => c.Text), Is.EqualTo(new[] { "5\" pipe", "x" }));
    }

    [Test]
    public void Probe_TruncatedText()
    {
        var quoted = new Span { Start = "\"", End = "\"" };
        Assert.That(quoted.Probe(Utf8.GetBytes("\"abc")), Is.Null);
        Assert.That(quoted.Probe(Utf8.GetBytes("\"abc\"")), Is.True);
        Assert.That(quoted.Probe(Utf8.GetBytes("abc")), Is.False);
    }

    [Test]
    public void MisplacedBinaryPart_IsADefinitionError()
    {
        Assert.Throws<DefinitionException>(() => new TextDefinition { Items = { new Int() } }.IsMatch("abcd"));
    }
}
