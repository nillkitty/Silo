namespace Silo.Contracts.Providers.Reflection;

public interface IFileFormat
{
    string DisplayName   { get; }
    string FileExtension { get; }
}