using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Formatting = Newtonsoft.Json.Formatting;
using Newtonsoft.Json.Linq;
using Serilog;
using Silo.Commanding;
using Silo.Connectors.File;
using Silo.Design;
using Silo.Providers.Format.Containers;
using Silo.TextEditing;
using Silo.Ui.Windows;
using Silo.ViewModel;
using Telefrag.DI;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>
///     A structured data file (JSON, YAML or XML, or a dialect of one) that is open in the Explorer as a tree. It is read once;
///     every change is written back to the file at once and refreshes the parts of the tree it touched.
/// </summary>
/// <remarks>
///     JSON and YAML are held as one JSON tree (YAML is read through <see cref="TextFormatting" />), so a change to a YAML file
///     writes it out again without its comments, anchors or scalar styles; JSON comments are not kept either. XML is held as an
///     <see cref="XDocument" /> and keeps everything but insignificant white space.
/// </remarks>
public sealed class DataDocument
{
    private readonly Dictionary<object, WeakReference<SiloNode>> _nodes = new(ReferenceEqualityComparer.Instance);

    public DataDocument(string path, TextLanguage language)
    {
        if (!language.IsStructured()) throw new ArgumentException("Only JSON, XML and YAML files open as data.", nameof(language));

        Path     = global::System.IO.Path.GetFullPath(path.Required());
        Language = language;
        Load();
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>JSON, YAML or XML.</summary>
    public TextLanguage Language { get; }

    public bool IsXml => Language == TextLanguage.Xml;

    /// <summary>The content of a JSON or YAML file; null for XML.</summary>
    public JToken? Json { get; private set; }

    /// <summary>The content of an XML file; null for JSON and YAML.</summary>
    public XDocument? Xml { get; private set; }

    /// <summary>The item at the top of the tree: the JSON value, or the XML file's root element.</summary>
    public object Root => (object?)Json ?? Xml?.Root ?? throw new InvalidDataException("The XML file has no root element.");

    /// <summary>Reads the file (again).</summary>
    public void Load()
    {
        string text = IOFile.ReadAllText(Path);
        if (IsXml)
        {
            Xml  = XDocument.Parse(text.Trim().Length == 0 ? "<root />" : text);
            Json = null;
            return;
        }

        Json = Parse(text, Language) ?? new JObject();
        Xml  = null;
    }

    /// <summary>Writes the content to the file.</summary>
    public void Save()
    {
        if (Xml is not null)
        {
            Xml.Save(Path);
            return;
        }

        IOFile.WriteAllText(Path, Serialize(Json!, Language));
    }

    /// <summary>The text of a new, empty file in <paramref name="language" />.</summary>
    public static string EmptyText(TextLanguage language) => language switch
                                                              {
                                                                  TextLanguage.Xml  => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root />\n",
                                                                  TextLanguage.Yaml => "{}\n",
                                                                  _                 => "{}\n"
                                                              };

    /// <summary>JSON or YAML text as a JSON tree; null when it is empty.</summary>
    public static JToken? Parse(string text, TextLanguage language)
    {
        if (text.Trim().Length == 0) return null;

        string json = language == TextLanguage.Json ? text : TextFormatting.Convert(language, TextLanguage.Json, text, "  ");
        return JToken.Parse(json, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace });
    }

    /// <summary>A JSON tree written as JSON or YAML.</summary>
    public static string Serialize(JToken token, TextLanguage language)
    {
        string json = token.ToString(Formatting.Indented);
        return language == TextLanguage.Json ? json + Environment.NewLine : TextFormatting.Convert(TextLanguage.Json, language, json, "  ");
    }

    internal void Track(object item, SiloNode node)
    {
        lock (_nodes) _nodes[item] = new WeakReference<SiloNode>(node);
    }

    /// <summary>
    ///     Writes the file after a change to the content of <paramref name="containers" />, and refreshes their nodes. Call it on
    ///     the UI thread. A failure to write is reported and the file read back, so the tree shows what the file holds.
    /// </summary>
    public void Changed(params object?[] containers)
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Writing {Path} failed", Path);
            App.Status($"{(global::System.IO.Path.GetFileName(Path))} could not be written: {ex.Message}");
            Load();
            containers = [Root];
        }

        foreach (var container in containers.Where(c => c is not null).Distinct(ReferenceEqualityComparer.Instance))
        {
            SiloNode? node;
            lock (_nodes) node = _nodes.TryGetValue(container!, out var weak) && weak.TryGetTarget(out var n) ? n : null;
            if (node is not null) _ = node.ReloadChildrenAsync();
        }
    }
}

/// <summary>
///     One item of a structured data file as a node of the Explorer: a JSON (or YAML) object, array or value, or an XML element,
///     attribute or text. An object, an array or an element lists what is in it as it is expanded.
/// </summary>
/// <remarks>
///     An item can be copied or dragged out (as text in the file's language, as JSON, and as a file of its own), cut or moved,
///     and deleted. An object, an array or an element takes what is pasted or dropped on it: items of any data file (JSON and
///     YAML items into XML become elements, XML elements into JSON become objects), data files (.json, .yaml, .xml, named after
///     the file), and JSON, YAML or XML text.
/// </remarks>
public class DataItemModel : INodeProvider<object>, INodeDetails, INodeViewProvider, ICommandProvider, ICuttable, IPastable
{
    private const string Group = "Data";

    public DataItemModel(DataDocument document, object item, string? key)
    {
        Document = document.Required();
        Item     = item.Required();
        Key      = key;
    }

    public DataDocument Document { get; }

    /// <summary>The item: a <see cref="JToken" /> (the value, never a <see cref="JProperty" />) or an <see cref="XObject" />.</summary>
    public object Item { get; }

    /// <summary>The name of a JSON object's property ("[2]" for an array's item); null for the top of the file and for XML.</summary>
    public string? Key { get; }

    public object Data => Item;

    /// <summary>The top of the file.</summary>
    public bool IsRoot => ReferenceEquals(Item, Document.Root);

    /// <summary>Whether things can be in it: a JSON object or array, or an XML element.</summary>
    public bool IsContainer => Item is JObject or JArray or XElement;

    /// <summary>The name of a JSON object's property this item is the value of (not an array's "[n]").</summary>
    private string? PropertyName => Item is JToken { Parent: JProperty property } ? property.Name : null;

    public string Name => Item switch
                          {
                              XElement e   => e.Name.LocalName,
                              XAttribute a => "@" + a.Name.LocalName,
                              XText        => "#text",
                              _            => IsRoot ? Path.GetFileName(Document.Path) : Key ?? "(value)"
                          };

    /// <summary>The text of the node; a connection shows its own name instead.</summary>
    protected virtual string NodeText => Item switch
                                         {
                                             JValue v                                            => $"{Name}: {Short(v.Type == JTokenType.String ? "\"" + v + "\"" : v.Type == JTokenType.Null ? "null" : v.ToString(Formatting.None))}",
                                             XAttribute a                                        => $"{Name} = \"{Short(a.Value)}\"",
                                             XText t                                             => Short(t.Value.Trim()),
                                             XElement e when !e.HasElements && !e.HasAttributes && e.Value.Length > 0 => $"{Name} = {Short(e.Value)}",
                                             JArray a                                            => $"{Name} [{a.Count}]",
                                             _                                                   => Name
                                         };

    public string TypeName => (Document.Language, Item) switch
                              {
                                  (_, XElement)                   => "XML Element",
                                  (_, XAttribute)                 => "XML Attribute",
                                  (_, XText)                      => "XML Text",
                                  (TextLanguage.Yaml, JObject)    => "YAML Mapping",
                                  (TextLanguage.Yaml, JArray)     => "YAML Sequence",
                                  (TextLanguage.Yaml, _)          => "YAML Scalar",
                                  (_, JObject)                    => "JSON Object",
                                  (_, JArray)                     => "JSON Array",
                                  (_, JValue v)                   => "JSON " + v.Type,
                                  _                               => "Data"
                              };

    public Uri Uri => ContainerUris.For(ContainerUris.DataFileScheme, Document.Path, PathInFile);

    /// <summary>Where the item is in the file: a JSON path ("servers[0].name") or an XML path ("/config/servers/server[1]/@name").</summary>
    public string PathInFile => Item switch
                                {
                                    JToken token => token.Path,
                                    XObject x    => XPathOf(x),
                                    _            => string.Empty
                                };

    public IconRef? Icon => IconRef.FromKey(Item switch
                                            {
                                                _ when IsRoot => FileTypeIcons.For(Path.GetExtension(Document.Path), "DataStructure"),
                                                JObject       => "DataStructure",
                                                JArray        => "DataCollection",
                                                XElement      => "Code",
                                                XAttribute    => "IconNameTag",
                                                _             => "Data"
                                            });

    private static string Short(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length > 80 ? text[..77] + "..." : text;
    }

    private static string XPathOf(XObject x)
    {
        switch (x)
        {
            case XAttribute a:
                return (a.Parent is { } p ? XPathOf(p) : string.Empty) + "/@" + a.Name.LocalName;
            case XElement e:
            {
                string step = e.Name.LocalName;
                if (e.Parent is { } parent)
                {
                    var same = parent.Elements(e.Name).ToList();
                    if (same.Count > 1) step += $"[{same.IndexOf(e) + 1}]";
                    return XPathOf(parent) + "/" + step;
                }

                return "/" + step;
            }
            default:
                return (x.Parent is { } owner ? XPathOf(owner) : string.Empty) + "/text()";
        }
    }

    // ---- the tree -----------------------------------------------------------------------------------------------

    public virtual SiloNode? GetNode()
    {
        return CreateNode(0);
    }

    protected SiloNode CreateNode(int? expand)
    {
        var node = new DataNode(this, NodeText, expand);
        if (IsContainer) Document.Track(Item, node);
        return node;
    }

    internal Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        IReadOnlyList<SiloNode> nodes = Children().Select(c => c.CreateNode(0)).ToList();
        return Task.FromResult(nodes);
    }

    /// <summary>What is in the item: an object's properties, an array's items, an element's attributes, then its elements and text.</summary>
    public IEnumerable<DataItemModel> Children()
    {
        switch (Item)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                    yield return new DataItemModel(Document, property.Value, property.Name);
                break;
            case JArray array:
                for (int i = 0; i < array.Count; i++)
                    yield return new DataItemModel(Document, array[i], $"[{i}]");
                break;
            case XElement element:
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    yield return new DataItemModel(Document, attribute, null);
                foreach (var node in element.Nodes())
                    if (node is XElement || (node is XText text && text.Value.Trim().Length > 0))
                        yield return new DataItemModel(Document, node, null);
                break;
        }
    }

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("File", Document.Path);
        yield return ("Type", TypeName);
        if (PathInFile.Length > 0) yield return ("Path", PathInFile);

        switch (Item)
        {
            case JContainer container:
                yield return ("Items", container.Count.ToString("N0"));
                break;
            case JValue value:
                yield return ("Value", value.ToString(Formatting.None));
                break;
            case XElement element:
                yield return ("Attributes", element.Attributes().Count().ToString("N0"));
                yield return ("Elements", element.Elements().Count().ToString("N0"));
                break;
            case XAttribute attribute:
                yield return ("Value", attribute.Value);
                break;
            case XText text:
                yield return ("Value", text.Value);
                break;
        }
    }

    /// <summary>The item written out, in the file's language, as a read-only text.</summary>
    public UIElement? BuildView()
    {
        string text;
        try
        {
            text = Fragment(Document.Language);
            if (text.Length > 200_000) text = text[..200_000] + Environment.NewLine + "...";
        }
        catch (Exception ex)
        {
            text = ex.Message;
        }

        var box = new TextBox
                  {
                      Text                          = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                      TextWrapping                  = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Padding = new Thickness(6)
                  };
        box.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return box;
    }

    // ---- what the item is, written out --------------------------------------------------------------------------

    /// <summary>
    ///     The item as JSON: an object's property as an object of that one property, anything else as its value. An XML element
    ///     becomes an object of one property named after it.
    /// </summary>
    public JToken ToJson()
    {
        return Item switch
               {
                   JToken token when PropertyName is { } name => new JObject(new JProperty(name, token.DeepClone())),
                   JToken token                               => token.DeepClone(),
                   XElement element                           => JObject.Parse(JsonConvert.SerializeXNode(element)),
                   XAttribute attribute                       => new JObject(new JProperty(attribute.Name.LocalName, attribute.Value)),
                   XText text                                 => new JValue(text.Value),
                   _                                          => JValue.CreateNull()
               };
    }

    /// <summary>The item written in <paramref name="language" /> (JSON, YAML or XML).</summary>
    public string Fragment(TextLanguage language)
    {
        if (Item is XObject x)
        {
            if (language == TextLanguage.Xml)
                return x switch
                       {
                           XAttribute a => a.ToString(),
                           XText t      => t.Value,
                           _            => x.ToString() ?? string.Empty
                       };

            return DataDocument.Serialize(ToJson(), language);
        }

        var json = ToJson();
        return language == TextLanguage.Xml ? DataPaste.ToXml(json, PropertyName ?? "item").ToString() : DataDocument.Serialize(json, language);
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        if (IsRoot)
        {
            yield return ProvidedCommand.From("data.texteditor", "Open in _Text Editor", () => TextEditors.OpenFile(Document.Path), "SourceCode",
                                              toolTip: "Edits the whole file as text", group: Group);
            yield return ProvidedCommand.From("data.reload", "Re_load from File", Reload, "Refresh", toolTip: "Reads the file again (after it was changed elsewhere)",
                                              group: Group);
        }

        yield return ProvidedCommand.From("data.copytext", "Copy as _Text", () => Clipboard.SetText(Fragment(Document.Language)), null,
                                          toolTip: "Puts the item, written out, on the clipboard", group: Group);

        if (!IsRoot)
            yield return new ProvidedCommand("data.delete", "_Delete", new DelegateCommand(_ => Delete()), null, null, "Takes this out of the file", Group,
                                             CommandPlacement.Menu, "Delete", IsDestructive: true);
    }

    private void Reload()
    {
        try
        {
            Document.Load();
            Document.Changed(Document.Root); // writes nothing new; refreshes the tree
        }
        catch (Exception ex)
        {
            App.Status($"{Path.GetFileName(Document.Path)} could not be read: {ex.Message}");
        }
    }

    private void Delete()
    {
        var (button, _) = PromptDialog.Show("Delete", $"Take {Name} out of {Path.GetFileName(Document.Path)}?", ["Delete", "Cancel"]);
        if (button == 0) RemoveFromFile();
    }

    /// <summary>Takes the item out of the file (and writes the file).</summary>
    public void RemoveFromFile()
    {
        if (IsRoot) return;

        object? container;
        switch (Item)
        {
            case JToken { Parent: JProperty property } token:
                container = property.Parent;
                property.Remove();
                break;
            case JToken token:
                container = token.Parent;
                token.Remove();
                break;
            case XAttribute attribute:
                container = attribute.Parent;
                attribute.Remove();
                break;
            case XNode node:
                container = node.Parent;
                node.Remove();
                break;
            default:
                return;
        }

        Document.Changed(container);
    }

    // ---- clipboard, and drag and drop -----------------------------------------------------------------------------

    public IReadOnlyCollection<string> CopyFormats => [ClipboardFormats.Text, ClipboardFormats.Json, ClipboardFormats.Files];

    public void Copy(IClipboardWriter clipboard)
    {
        clipboard.SetLazy(ClipboardFormats.Text, () => Fragment(Document.Language));
        clipboard.SetLazy(ClipboardFormats.Json, () => ToJson().ToString(Formatting.Indented));
        if (Item is JToken or XElement) clipboard.SetLazy(ClipboardFormats.Files, () => new[] { WriteFile() });
    }

    /// <summary>The item as a data file of its own, in the file's language, in a temporary folder.</summary>
    private string WriteFile()
    {
        string name = DropFiles.SafeName(IsRoot ? Path.GetFileNameWithoutExtension(Document.Path) : Item is XElement e ? e.Name.LocalName : PropertyName ?? Key?.Trim('[', ']') ?? "item");
        string path = Path.Combine(DropFiles.NewDirectory(), name + "." + Document.Language.DefaultExtension().TrimStart('.'));
        IOFile.WriteAllText(path, Fragment(Document.Language));
        return path;
    }

    public bool CanCut => !IsRoot;

    /// <summary>A move took a copy: the original comes out of the file.</summary>
    public void CutCompleted(PasteResult result)
    {
        if (result.Effect == PasteEffect.Copied) DropFiles.OnUi(RemoveFromFile);
    }

    public IReadOnlyList<string> PasteFormats => [ClipboardFormats.Files, ClipboardFormats.Json, ClipboardFormats.Text];

    public bool CanPaste(IClipboardData data, PasteMode mode)
    {
        if (Item is not (JObject or JArray or XElement)) return false;
        if (mode == PasteMode.Move && data.Operation != ClipboardOperation.Cut) return false;

        if (data.Sources.Count > 0)
            return data.Sources.All(s => s switch
                                         {
                                             DataItemModel item => !(mode == PasteMode.Move && item.Contains(this)) && !(mode == PasteMode.Move && item.IsChildOf(this)),
                                             FileModel file     => ContainerFormats.IsDataFile(file.Path),
                                             ArchiveItemModel a => a.Entry is { IsDirectory: false } entry && ContainerFormats.IsDataFile(entry.Name),
                                             _                  => false
                                         });

        if (data.Contains(ClipboardFormats.Files)) return DropFiles.Read(data).Any(ContainerFormats.IsDataFile);

        return data.Contains(ClipboardFormats.Json) || data.Contains(ClipboardFormats.Text);
    }

    /// <summary>Whether <paramref name="other" /> is this item or inside it (in the same file).</summary>
    private bool Contains(DataItemModel other)
    {
        if (!ReferenceEquals(Document, other.Document)) return false;

        for (object? at = other.Item; at is not null; at = at is JToken t ? t.Parent : (at as XObject)?.Parent)
            if (ReferenceEquals(at, Item))
                return true;

        return false;
    }

    /// <summary>Whether this item is directly in <paramref name="container" /> (where a move would leave it as it is).</summary>
    private bool IsChildOf(DataItemModel container)
    {
        object? parent = Item switch
                         {
                             JToken { Parent: JProperty p } => p.Parent,
                             JToken t                       => t.Parent,
                             XObject x                      => x.Parent,
                             _                              => null
                         };
        return ReferenceEquals(parent, container.Item);
    }

    public PasteResult Paste(IClipboardData data, PasteMode mode)
    {
        if (!CanPaste(data, mode)) return PasteResult.None;

        try
        {
            int added = Item is XElement element ? DataPaste.IntoXml(element, data) : DataPaste.IntoJson((JContainer)Item, data, Document.Language);
            if (added == 0) return PasteResult.None;

            Document.Changed(Item);
            return PasteResult.Copied(Item);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or TextTransformException or IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warning(ex, "Pasting into {Path} failed", Document.Path);
            App.Status($"That cannot go into {Name}: {ex.Message}");
            return PasteResult.None;
        }
    }
}

/// <summary>Turning what is pasted or dropped into JSON items or XML nodes, and adding them.</summary>
public static class DataPaste
{
    /// <summary>Adds what <paramref name="data" /> holds to a JSON object or array; returns how many items were added.</summary>
    public static int IntoJson(JContainer target, IClipboardData data, TextLanguage language)
    {
        var incoming = new List<(string? Name, JToken Value)>();

        if (data.Sources.OfType<DataItemModel>().ToList() is { Count: > 0 } items)
        {
            foreach (var item in items)
                incoming.AddRange(Named(item.ToJson(), item.Item is JToken { Parent: JProperty } || item.Item is XObject));
        }
        else if (DropFiles.Read(data) is { Count: > 0 } files)
        {
            foreach (string file in files)
                if (ContainerFormats.DataLanguageOf(file) is { } fileLanguage)
                {
                    var token = ReadFile(file, fileLanguage);
                    incoming.Add((Path.GetFileNameWithoutExtension(file), token));
                }
        }
        else if (data.Get(ClipboardFormats.Json) is string json && json.Trim().Length > 0)
        {
            incoming.AddRange(Named(JToken.Parse(json), target is JObject));
        }
        else if (data.Get(ClipboardFormats.Text) is string text && text.Trim().Length > 0)
        {
            JToken? token = null;
            try
            {
                token = DataDocument.Parse(text, language == TextLanguage.Xml ? TextLanguage.Json : language);
            }
            catch (Exception ex) when (ex is JsonException or TextTransformException)
            {
                if (target is not JArray) throw;
            }

            incoming.AddRange(token is null ? [(null, new JValue(text))] : Named(token, target is JObject));
        }

        foreach (var (name, value) in incoming)
        {
            if (target is JObject obj)
            {
                string unique = DropFiles.Unique(name ?? "item", n => obj.ContainsKey(n));
                obj.Add(new JProperty(unique, value));
            }
            else
            {
                target.Add(value);
            }
        }

        return incoming.Count;
    }

    /// <summary>
    ///     The items of a pasted JSON value: when it stands for named items (an object whose properties are what was copied),
    ///     each property; otherwise the value itself, unnamed.
    /// </summary>
    private static IEnumerable<(string? Name, JToken Value)> Named(JToken token, bool spread)
    {
        if (spread && token is JObject obj)
            return obj.Properties().Select(p => ((string?)p.Name, p.Value.DeepClone())).ToList();

        return [(null, token)];
    }

    private static JToken ReadFile(string file, TextLanguage language)
    {
        string text = IOFile.ReadAllText(file);
        if (language != TextLanguage.Xml) return DataDocument.Parse(text, language) ?? JValue.CreateNull();

        var root = XDocument.Parse(text).Root ?? throw new XmlException("The XML file has no root element.");
        return JObject.Parse(JsonConvert.SerializeXNode(root, Formatting.None, true));
    }

    /// <summary>Adds what <paramref name="data" /> holds to an XML element; returns how many nodes were added.</summary>
    public static int IntoXml(XElement target, IClipboardData data)
    {
        var incoming = new List<XObject>();

        if (data.Sources.OfType<DataItemModel>().ToList() is { Count: > 0 } items)
        {
            foreach (var item in items)
                switch (item.Item)
                {
                    case XElement e:
                        incoming.Add(new XElement(e));
                        break;
                    case XAttribute a:
                        incoming.Add(new XAttribute(a));
                        break;
                    case XText t:
                        incoming.Add(new XText(t.Value));
                        break;
                    case JToken:
                        incoming.AddRange(FromJson(item.ToJson(), "item"));
                        break;
                }
        }
        else if (DropFiles.Read(data) is { Count: > 0 } files)
        {
            foreach (string file in files)
                switch (ContainerFormats.DataLanguageOf(file))
                {
                    case TextLanguage.Xml:
                        if (XDocument.Load(file).Root is { } root) incoming.Add(new XElement(root));
                        break;
                    case { } language:
                        incoming.Add(ToXml(DataDocument.Parse(IOFile.ReadAllText(file), language) ?? JValue.CreateNull(), Path.GetFileNameWithoutExtension(file)));
                        break;
                }
        }
        else if (data.Get(ClipboardFormats.Text) is string text && text.Trim().Length > 0)
        {
            try
            {
                incoming.AddRange(XElement.Parse("<r>" + text + "</r>").Nodes().Where(n => n is not XText t || t.Value.Trim().Length > 0).ToList());
            }
            catch (XmlException)
            {
                incoming.Add(new XText(text));
            }
        }
        else if (data.Get(ClipboardFormats.Json) is string json && json.Trim().Length > 0)
        {
            incoming.AddRange(FromJson(JToken.Parse(json), "item"));
        }

        foreach (var x in incoming)
        {
            if (x is XAttribute attribute) target.SetAttributeValue(attribute.Name, attribute.Value);
            else target.Add(x);
        }

        return incoming.Count;
    }

    /// <summary>JSON as XML elements: each property of an object becomes an element of its name; anything else one element named <paramref name="name" />.</summary>
    private static IEnumerable<XObject> FromJson(JToken token, string name)
    {
        if (token is JObject obj && obj.Count > 0)
            return obj.Properties().Select(p => (XObject)ToXml(p.Value, p.Name)).ToList();

        return [ToXml(token, name)];
    }

    /// <summary>A JSON value as one XML element named <paramref name="name" /> (made a valid XML name).</summary>
    public static XElement ToXml(JToken token, string name)
    {
        string safe = XmlConvert.EncodeLocalName(name.Length == 0 ? "item" : name);
        var    root = JsonConvert.DeserializeXNode(new JObject(new JProperty(safe, token.DeepClone())).ToString(Formatting.None), "r")!.Root!;
        var    made = root.Elements().ToList();
        return made.Count == 1 ? new XElement(made[0]) : new XElement(safe, made.Select(e => new XElement(e)));
    }
}

/// <summary>An item of a structured data file in the Explorer; an object, array or element reads its children as it is expanded.</summary>
public record DataNode : SiloNode
{
    public DataNode(DataItemModel model, string text, int? expand) : base(model, text, model.Uri, model.Icon, expand)
    {
        TypeName    = model.TypeName;
        ChildLoader = model.IsContainer ? model.LoadChildNodesAsync : null;
    }
}

/// <summary>A structured data file opened as a connection: the top of its tree, expanded.</summary>
public class DataFileConnection : DataItemModel, IConnection
{
    public DataFileConnection(IConnector connector, DataDocument document, string? displayText) : base(document, document.Root, null)
    {
        Connector   = connector.Required();
        DisplayText = displayText;
    }

    protected override string NodeText => DisplayText is { Length: > 0 } name ? name : Path.GetFileName(Document.Path);

    public bool?        ConnectionState  => null;
    public IConnector   Connector        { get; }
    public string?      DisplayText      { get; set; }
    public bool         IsInTree         => Node?.IsVisible ?? false;
    public List<object> Metadata         { get; } = [];
    public SiloNode?    Node             { get; private set; }
    public string?      PersistentConfig => null;
    public object?      State            => null;
    public string?      TempConfig       => null;
    public Container    Components       { get; } = new(Guid.NewGuid().ToString());

    public override SiloNode? GetNode()
    {
        return Node ??= CreateNode(1);
    }

    public string? GetHostname()
    {
        return Uri.TryCreate(Document.Path, UriKind.Absolute, out var uri) && uri.IsUnc ? uri.Host : ".";
    }

    public Task<bool> Connect(CancellationToken cancel = default) => Task.FromResult(true);

    public Task<bool> Disconnect() => Task.FromResult(false);

    public Task<bool> Remove() => Task.FromResult(true);

    public Window? BuildWindow() => null;

    public Task InitAsync(ConnectContext context) => Task.CompletedTask;

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }
}
