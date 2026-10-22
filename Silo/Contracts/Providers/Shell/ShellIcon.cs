namespace Silo.Providers.Shell;

public abstract record ShellIcon
{
    protected abstract void OnWrite(ShellTypeWriter w);

    public virtual void Write(ShellTypeWriter w)
    {
        OnWrite(w);
    }
}