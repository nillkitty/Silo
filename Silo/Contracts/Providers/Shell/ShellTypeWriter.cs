using Microsoft.Win32;
using Silo.Contracts.Core;

namespace Silo.Shell;

public class ShellTypeWriter : ContextBase
{
    public bool        IsProtocolHandler { get; }
    public RegistryKey Key               { get; }
    public string      Name              { get; }

    /// <summary>
    ///     The Shell Type ProgId (e.g. '.xlsx' or 'https')
    /// </summary>
    public string ProgId { get; }

    public ShellConfigurationScope Scope { get; }

    public IShellPaths ShellPaths { get; }

    public ShellTypeWriter(string progId, ShellConfigurationScope scope)
    {
        ShellPaths = new ShellPaths(scope); // the scope decides which hive the paths are in
        ProgId     = progId.Required();
        if (ProgId.RegexMatches(@"^[a-zA-Z][a-zA-Z0-9]+\:$"))
        {
            Name              = ProgId.TrimEnd(':');
            IsProtocolHandler = true;
            Scope             = scope;
        }
        else if (ProgId.StartsWith('.') && ProgId.Length > 1)
        {
            Name              = ProgId;
            IsProtocolHandler = false;
            Scope             = scope;
        }
        else
        {
            throw new NotSupportedException($"The type '{progId}' must be either an extension ('.xlsx') or a scheme ('https:').");
        }

        Key = ShellPaths.ClassesRoot.OpenOrCreate();
        if (Key is null)
            throw new InvalidOperationException("Could not obtain a writable handle to registry key the Classes root.");
    }
}
