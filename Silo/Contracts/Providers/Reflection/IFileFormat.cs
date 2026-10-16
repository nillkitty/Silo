namespace Silo.Contracts.Providers.Reflection;

public interface IFileFormat
{
    string ContentType   { get; }
    string DisplayName   { get; }
    string FileExtension { get; }
}