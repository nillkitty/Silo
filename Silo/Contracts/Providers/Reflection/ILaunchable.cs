namespace Silo.Providers.Reflection;

public interface ILaunchable
{
    string? ArgTemplate     { get; }
    string? FilePath        { get; }
    bool    UseShellExecute { get; }
}