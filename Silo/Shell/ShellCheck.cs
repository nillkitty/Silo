namespace Silo.Shell;

public enum ShellSeverity
{
    Warning,
    Error
}

/// <summary>Something wrong with a Shell package. <see cref="Path" /> is the entry (and, inside a submenu, the verb) it is about; empty for the package.</summary>
public sealed record ShellProblem(ShellSeverity Severity, string Text, int[] Path)
{
    public string Where => Path.Length == 0 ? "package" : string.Join('.', Path.Select(p => p + 1));
}

/// <summary>What is wrong with a Shell package before it is installed: what would fail, and what would be unwise.</summary>
public static class ShellCheck
{
    /// <summary>Protocols the system and every browser depend on: a package does not take them over.</summary>
    private static readonly HashSet<string> ReservedSchemes = new(StringComparer.OrdinalIgnoreCase)
                                                              { "http", "https", "ftp", "file", "mailto", "ms-settings", "ms-windows-store", "search-ms", "ms-help", "res", "about", "javascript" };

    public static IReadOnlyList<ShellProblem> Validate(ShellPackage package)
    {
        var problems = new List<ShellProblem>();

        void Error(string text, params int[] path) => problems.Add(new ShellProblem(ShellSeverity.Error, text, path));
        void Warn(string text, params int[] path) => problems.Add(new ShellProblem(ShellSeverity.Warning, text, path));

        if (string.IsNullOrWhiteSpace(package.Name)) Error("The package has no name.");
        else if (package.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || package.Name.Contains('\\')) Error("The package's name has characters a registry key cannot be named with.");

        if (package.Entries.Count == 0) Warn("The package has nothing in it.");

        for (int i = 0; i < package.Entries.Count; i++)
        {
            var e = package.Entries[i];
            CheckEntry(package, e, [i], false, Error, Warn);
            for (int c = 0; c < e.Children.Count; c++) CheckEntry(package, e.Children[c], [i, c], true, Error, Warn);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < package.Entries.Count; i++)
        {
            var e = package.Entries[i];
            if (!e.Enabled || e.Kind is ShellEntryKind.Registry) continue;

            if (!seen.Add($"{e.Kind}|{e.Target}|{e.Name}")) Warn($"{e.Display} is there twice: the later one overwrites the earlier.", i);
        }

        try
        {
            if (problems.All(p => p.Severity != ShellSeverity.Error)) ShellCompiler.Compile(package);
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }

        return problems;
    }

    private static void CheckEntry(ShellPackage package, ShellEntry e, int[] path, bool child, Action<string, int[]> error, Action<string, int[]> warn)
    {
        void Error(string text) => error(text, path);
        void Warn(string text) => warn(text, path);

        string name = e.Display;
        if (child && e.Kind != ShellEntryKind.Verb) Error($"{name}: only verbs go inside a submenu.");
        if (e.Kind != ShellEntryKind.Submenu && e.Children.Count > 0) Warn($"{name}: only a submenu has entries inside it; they are ignored.");

        if (e.Kind is ShellEntryKind.Verb or ShellEntryKind.Submenu or ShellEntryKind.ClassIcon or ShellEntryKind.DefaultVerb && !child && string.IsNullOrWhiteSpace(e.Target))
            Error($"{name}: it belongs to no class (the class is for example * or .txt or Directory).");

        if (e.Kind is ShellEntryKind.Verb or ShellEntryKind.Submenu or ShellEntryKind.DefaultVerb or ShellEntryKind.Protocol or ShellEntryKind.FileType or ShellEntryKind.AppPath or ShellEntryKind.Application
            or ShellEntryKind.SendTo or ShellEntryKind.NewFile && string.IsNullOrWhiteSpace(e.Name))
            Error($"{name}: it needs a name.");

        if (!string.IsNullOrWhiteSpace(e.Name) && e.Kind != ShellEntryKind.Registry && (e.Name.Contains('\\') || e.Name.Contains('/')) && e.Kind != ShellEntryKind.AppPath)
            Error($"{name}: the name cannot have a slash in it.");

        if (e.Kind is ShellEntryKind.Verb or ShellEntryKind.Submenu && string.IsNullOrWhiteSpace(e.Label)) Error($"{name}: it needs the text to show in the menu.");

        if (e.Kind is ShellEntryKind.Verb or ShellEntryKind.Protocol or ShellEntryKind.SendTo or ShellEntryKind.AppPath or ShellEntryKind.Application && string.IsNullOrWhiteSpace(e.Command))
            Error($"{name}: it needs a command.");

        if (e.Kind == ShellEntryKind.ClassIcon && string.IsNullOrWhiteSpace(e.Icon)) Error($"{name}: it needs an icon.");

        if (e.Kind == ShellEntryKind.Protocol && !string.IsNullOrWhiteSpace(e.Name))
        {
            if (!ShellCompiler.SchemePattern().IsMatch(e.Name)) Error($"{name}: a scheme starts with a letter and has only letters, digits, + - and . in it.");
            else if (ReservedSchemes.Contains(e.Name)) Error($"{name}: {e.Name} belongs to the system and the browsers; a package does not take it over.");
        }

        if (e.Kind is ShellEntryKind.Verb or ShellEntryKind.Protocol && !string.IsNullOrWhiteSpace(e.Command))
        {
            string c = e.Command.TrimStart();
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (!c.StartsWith('"') && exe > 0 && c[..exe].Contains(' '))
                Warn($"{name}: put the program's path in quotes, or a path with spaces will not run.");

            if (e.Kind == ShellEntryKind.Protocol && !c.Contains("%1")) Warn($"{name}: the command has no %1, so it will not be given the address.");
            if (e.Kind == ShellEntryKind.Verb && !c.Contains("%1") && !c.Contains("%V") && !c.Contains("%L")) Warn($"{name}: the command has no %1 or %V, so it will not be told what was clicked.");
        }

        if (e.Kind is ShellEntryKind.FileType)
        {
            if (e.Extensions.Count == 0) Warn($"{name}: no extension uses this file type.");
            foreach (string ext in e.Extensions)
                if (string.IsNullOrWhiteSpace(ext) || ext.Trim().Trim('.').Length == 0 || ext.Any(char.IsWhiteSpace))
                    Error($"{name}: '{ext}' is not an extension.");
        }

        if (e.Kind == ShellEntryKind.NewFile)
        {
            string ext = e.Name.Trim();
            bool described = package.Entries.Any(f => f.Enabled && f.Kind == ShellEntryKind.FileType && f.Extensions.Any(x => string.Equals(x.Trim().TrimStart('.'), ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase)));
            if (!described) Warn($"{name}: the New menu shows what the system calls files of this type; if nothing does, add a file type for {ext}.");
        }

        if (e.Kind == ShellEntryKind.SendTo && package.Scope == ShellScope.AllUsers) Warn($"{name}: Send To is always the person's own, whoever the package is for.");

        if (e.Kind == ShellEntryKind.Registry)
        {
            string key = (e.Key ?? string.Empty).Trim().Trim('\\');
            string upper = key.ToUpperInvariant();
            bool user = upper == "HKCU" || upper.StartsWith(@"HKCU\"), machine = upper == "HKLM" || upper.StartsWith(@"HKLM\");
            if (!user && !machine) Error($"{name}: the key starts with HKCU or HKLM.");
            else if (!upper.Contains(@"\SOFTWARE\") && !upper.EndsWith(@"\SOFTWARE")) Error($"{name}: only keys under Software can be written.");
            else if (upper.Contains(@"\SILO\SHELLPACKAGES")) Error($"{name}: that is where the Shell packages are recorded.");
            else if (machine && package.Scope == ShellScope.CurrentUser) Error($"{name}: HKLM is the whole machine; make the package for all users to write there.");
            else if (user && package.Scope == ShellScope.AllUsers) Warn($"{name}: HKCU is only the person who installs it.");

            if (upper.Contains(@"\CURRENTVERSION\RUN")) Warn($"{name}: a Run key starts a program whenever the person signs in.");
            if (e.Data is null) Warn($"{name}: no value is set; only the key is made.");
        }
    }
}
