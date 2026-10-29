using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Script;

[Transient(typeof(IScriptProvider))]
public class Cmd : IScriptProvider, ILaunchable
{
    public string? DisplayName       => "Batch file (cmd)";
    public string  FileExtension     => "bat,cmd";
    public string  ContentType       => "text/x-bat";
    public string? ArgTemplate       => null;
    public string? FilePath          => @"%WinDir%\System32\cmd.exe";
    public bool    UseShellExecute   => false;
    public string? InlineArgTemplate => "/K %1";
    public string? EditorLanguage => "Batch";
}