using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Markup;
using Serilog;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;
using IoPath = System.IO.Path;

namespace Silo.Contracts.Core;

/// <summary>
///     Finds the <see cref="SiloTemplate" />s a new Silo can start from. Two places are searched:
///     <list type="bullet">
///         <item>
///             The templates compiled into the app -- any XAML page under <c>Resources/Templates*.xaml</c> or
///             <c>Templates/</c> whose root element is a <see cref="SiloTemplate" /> (see <c>Resources/Templates.xaml</c>).
///         </item>
///         <item>
///             The person's own templates: loose <c>*.xaml</c> files in <see cref="UserFolder" />, loaded with
///             <see cref="XamlReader" />. These are trusted the way any file the person puts in their own profile is.
///         </item>
///     </list>
///     A template that fails to load is logged and skipped; it never stops the dialog from opening.
/// </summary>
public static class SiloTemplateCatalog
{
    /// <summary>%APPDATA%\Silo\Templates -- drop a template .xaml here to have it offered in the New Silo dialog.</summary>
    public static string UserFolder => IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "Templates");

    /// <summary>Loads every template that can be found, ordered by name. Never returns null and never throws.</summary>
    public static IReadOnlyList<SiloTemplate> Load()
    {
        var found = new List<SiloTemplate>();
        LoadBuiltIn(found);
        LoadUser(found);
        return found.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void LoadBuiltIn(List<SiloTemplate> found)
    {
        try
        {
            var asm      = typeof(SiloTemplateCatalog).Assembly;
            var asmName  = asm.GetName().Name;
            using var rs = asm.GetManifestResourceStream(asmName + ".g.resources");
            if (rs is null) return;

            using var reader = new ResourceReader(rs);
            foreach (DictionaryEntry entry in reader)
            {
                if (entry.Key is not string key || !key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase)) continue;

                var stem = key[..^".baml".Length];
                if (!IsTemplateResource(stem)) continue;

                try
                {
                    var loaded = Application.LoadComponent(new Uri($"/{asmName};component/{stem}.xaml", UriKind.Relative));
                    Add(found, loaded, stem);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Built-in Silo template '{template}' could not be loaded", stem);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Built-in Silo templates could not be enumerated");
        }
    }

    private static bool IsTemplateResource(string stem)
    {
        return stem.StartsWith("resources/templates", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("templates/", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadUser(List<SiloTemplate> found)
    {
        try
        {
            var folder = UserFolder;
            if (!IoDirectory.Exists(folder)) return;

            foreach (var file in IoDirectory.EnumerateFiles(folder, "*.xaml"))
                try
                {
                    using var stream = IoFile.OpenRead(file);
                    Add(found, XamlReader.Load(stream), IoPath.GetFileNameWithoutExtension(file));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Silo template '{file}' could not be loaded", file);
                }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The Silo templates folder could not be read");
        }
    }

    /// <summary>Accepts a loaded XAML root that is a template, or a sequence of templates; anything else is logged and ignored.</summary>
    private static void Add(List<SiloTemplate> found, object? loaded, string source)
    {
        switch (loaded)
        {
            case SiloTemplate t:
                found.Add(Named(t, source));
                break;
            case IEnumerable<SiloTemplate> many:
                foreach (var t in many) found.Add(Named(t, source));
                break;
            default:
                Log.Warning("'{source}' is not a SiloTemplate (it is {type}); ignored", source, loaded?.GetType().Name ?? "null");
                break;
        }
    }

    private static SiloTemplate Named(SiloTemplate t, string source)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) t.Name = source;
        return t;
    }
}
