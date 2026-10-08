namespace Silo.Shell;

public record IconFrom(string Name, string Value, bool UseRegSzExpand) : ShellIcon
{
    protected override void OnWrite(ShellTypeWriter w)
    {
        if (Name.Nill())
        {
            w.Logger.Warning("Skipping write of Icon ({me}) for scope ({scope}): " + "name of RegistryValue to write was null", this,
                             w.ToString());
            return;
        }


        w.Key.SetValue(Name, Value);
    }
}

public record IconFromPathAndNint(string IcoFilePath, nint Id = 0) : IconFrom("DefaultIcon", @$"""{IcoFilePath}"",{Id}", false);

public record IconFromFile(string IcoFilePath, int Index = 0) : IconFromPathAndNint(IcoFilePath, Index >= 0 ? Index : 0);

public record IconFromResource(string CompiledFilePath, int? Index = null, nint? IconId = null)
    : IconFromPathAndNint(CompiledFilePath, IconId is nint n ? -n : Index is int i and > 0 ? i : 0);

public record IconFromSelf(int? Index = null, nint? IconId = null) : ShellIcon
{
    protected override void OnWrite(ShellTypeWriter w)
    {
    }
}
