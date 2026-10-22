namespace Silo.Providers.Shell;

public interface IShellTypeOptions
{
    string            ProtocolFriendlyTypeName { get; }
    ShellConfigAction ProtocolTypeNameAction   { get; }
}