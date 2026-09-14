using System.IO;
using System.Windows;
using Silo.Model;

namespace Silo;

public static class AppIdentity
{
    public static string AppName => BrandedAppIdentity.AppName ?? "Silo";
}

public static class BrandedAppIdentity
{
    public static string? AppName { get; set; }
    public static string? AppIcon { get; set; }
}

public static class Database
{
    public static IDatabaseProvider CreateNewSilo(string path)
    {
        var sl = App.Require<INewSiloBuilder>();
        return sl.Build(path);
    }

    public static IDatabaseProvider Load(SiloFile file)
    {
        file.Required();
        var fi = new FileInfo(file.FilePath);
        if (!fi.Exists)
            throw new FileNotFoundException($"File not found or no access to file:  {file.FilePath}", file.FilePath);

        var sl = App.Require<ISiloLoader>();
        return sl.Load(fi);
    }
}