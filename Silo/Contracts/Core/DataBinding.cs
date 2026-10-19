namespace Silo.Contracts.Core;

public class DataBinding : List<object>
{
    public TimeSpan? Interval { get; set; }
    public string?   Name     { get; set; }
    public Uri?      Source   { get; set; }
}