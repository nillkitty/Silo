using Silo.Model;

namespace Silo.Commanding;

public static class Enabler
{
    public delegate bool EnablerFunc(object? o);

    public static  EnablerFunc Always => _ => true;
    public static  EnablerFunc Never => _ => false;
    private static OpenSilo?   _active => App.Instance?.ActiveSilo;
    public static  EnablerFunc IsSiloLoaded => _ => _active is { Data: { } };
    public static  EnablerFunc IsSiloAuthorable => _ => _active.IsAuthorable;
}

public static class Checker
{
    public delegate bool CheckerFunc(object? o);

    public static CheckerFunc Always => _ => true;
    public static CheckerFunc Never  => _ => false;

    public static CheckerFunc IsSiloActive =>
        o => App.Instance?.ActiveSilo is { } a && a == o;
}