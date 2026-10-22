namespace Silo.Providers.Shell;

public class ShellWriter
{
    protected IShellPaths             Paths { get; }
    private   ShellConfigurationScope Scope { get; }

    public ShellWriter(ShellConfigurationScope scope)
    {
        Scope = scope;
        Paths = new ShellPaths(scope);
    }
}