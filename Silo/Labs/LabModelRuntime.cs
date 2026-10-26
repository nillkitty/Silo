using System.Windows.Controls;

namespace Silo.Labs;

/// <summary>What the window does when a prefab or a template is used: asks for the placeholders, then has the session add it.</summary>
internal interface ILabCreator
{
    /// <summary>Adds a new subject (<paramref name="subject" />) or a new part (<paramref name="part" />) from a prefab or a template.</summary>
    Task CreateAsync(LabSubjectType? subject, LabPartType? part, LabPrefab? prefab, LabTemplate? template);
}

/// <summary>
///     The Library pane of a Lab window: what the session lists (its saved values, its ready-made ones), then the prefabs and templates
///     of the subject types and -- for the active part -- the part types that can be added there. Double-click one to add it.
/// </summary>
internal sealed class LabModelLibrary(ILabLibrary? inner, ILabModel model, ILabCreator creator) : ILabLibrary
{
    public bool LibraryOpensWithWindow => inner?.LibraryOpensWithWindow ?? false;

    public IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        var sections = new List<LabLibrarySection>();
        if (inner is not null) sections.AddRange(inner.LibrarySections(filter));

        foreach (var type in model.SubjectTypes)
        {
            Add(sections, $"Prefabs: {type.Name}", type.IconKey, filter, type.PrefabList.Select(p => (p.Group, Entry(p.Name, "Prefab", p.Description, p.IconKey ?? type.IconKey, $"Double-click to start a new {type.Name.ToLowerInvariant()} with it", () => creator.CreateAsync(type, null, p, null)))));
            Add(sections, $"Templates: {type.Name}", type.IconKey, filter, type.TemplateList.Select(t => (t.Group, Entry(t.Name, "Template", t.Description, t.IconKey ?? type.IconKey, $"Double-click to start a new {type.Name.ToLowerInvariant()} from it", () => creator.CreateAsync(type, null, null, t)))));
        }

        var parent = model.ActivePart;
        foreach (var type in LabModelRules.AddableTypes(model, parent))
        {
            string where = parent is null ? "at the top" : $"in {parent.Name}";
            Add(sections, $"Prefabs: add a {type.Name.ToLowerInvariant()}", type.IconKey, filter, type.PrefabList.Select(p => (p.Group, Entry(p.Name, "Prefab", p.Description, p.IconKey ?? type.IconKey, $"Double-click to add it {where}", () => creator.CreateAsync(null, type, p, null)))));
            Add(sections, $"Templates: add a {type.Name.ToLowerInvariant()}", type.IconKey, filter, type.TemplateList.Select(t => (t.Group, Entry(t.Name, "Template", t.Description, t.IconKey ?? type.IconKey, $"Double-click to add it {where}", () => creator.CreateAsync(null, type, null, t)))));
        }

        return sections;
    }

    private static LabLibraryEntry Entry(string name, string kind, string? detail, string? icon, string tip, Func<Task> run)
    {
        return new LabLibraryEntry(name, kind, detail, icon ?? "Template", () => _ = run(), IsTemplate: true, ToolTip: string.IsNullOrEmpty(detail) ? tip : detail + Environment.NewLine + tip);
    }

    private static void Add(List<LabLibrarySection> sections, string title, string? icon, string filter, IEnumerable<(string? Group, LabLibraryEntry Entry)> entries)
    {
        var list = entries.Where(e => filter.Length == 0 || e.Entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (e.Group?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                          .OrderBy(e => e.Group ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                          .Select(e => e.Group is null ? e.Entry : e.Entry with { Kind = e.Group })
                          .ToList();
        if (list.Count > 0) sections.Add(new LabLibrarySection(title, icon, list, IsTemplate: true));
    }
}

/// <summary>Asks for the values of a template's placeholders that no binding gave.</summary>
internal sealed class LabTemplateDialog : Window
{
    private LabTemplateDialog(Window? owner, LabTemplate template, Dictionary<string, string> values, HashSet<string> bound)
    {
        Title                 = $"New from {template.Name}";
        Width                 = 480;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is not null) Owner = owner;

        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        var rows = new List<(string Title, UIElement Control)>();
        foreach (var p in template.Placeholders.Where(p => !bound.Contains(p.Name)))
        {
            string name = p.Name;
            UIElement control = p.Choices is { Count: > 0 }
                                    ? LabUi.Combo(p.Choices, values.GetValueOrDefault(name) is { Length: > 0 } current && p.Choices.Contains(current) ? current : p.Choices[0], v => values[name] = v, 280)
                                    : LabUi.Line(values.GetValueOrDefault(name), v => values[name] = v, 280, p.Prompt);
            if (control is ComboBox combo && combo.SelectedItem is string chosen) values[name] = chosen;
            rows.Add((p.Prompt, control));
        }

        var ok     = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 3, 8, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Padding = new Thickness(8, 3, 8, 3) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel();
        if (!string.IsNullOrEmpty(template.Description)) panel.Children.Add(LabUi.Label(template.Description, muted: true, wrap: true).Also(l => l.Margin = new Thickness(10, 10, 10, 0)));
        panel.Children.Add(LabUi.Form(rows, 130));
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Shows the dialog; false when the person cancelled. <paramref name="values" /> is filled in with their answers.</summary>
    public static bool Ask(Window? owner, LabTemplate template, Dictionary<string, string> values, HashSet<string> bound)
    {
        return new LabTemplateDialog(owner, template, values, bound).ShowDialog() == true;
    }
}
