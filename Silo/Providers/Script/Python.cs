using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Script;

[Transient(typeof(IScriptProvider))]
public class Python : IScriptProvider, ILaunchable
{
    public string? DisplayName     => "Python";
    public string  FileExtension   => "py";
    public string  ContentType     => "text/x-python";
    public string? ArgTemplate     => null;
    public string? FilePath        => GetPythonExePath();
    public bool    UseShellExecute { get; set; }

    public string InlineArgTemplate => "-c \"%1\"";
    public string? EditorLanguage => "Python";

    public static string? GetPythonExePath()
    {
        return AppsManager.GetExePathFor("python");
    }
}