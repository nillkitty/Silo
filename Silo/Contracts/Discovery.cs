namespace Silo.Contracts;

public class Discovery
{
    public bool? AutoReconnectOnUsage      { get; set; }
    public bool? AutoReconnectOnOpen       { get; set; }
    public bool? AutoReconnectBackground   { get; set; }
    public bool? ConnectionTracing         { get; set; }
    public bool? RememberIcmpVisibility    { get; set; }
    public bool? RememberResolvedAddresses { get; set; }
    public bool? RememberSourceAddresseses { get; set; }
    public bool? LogToConnectionHistory    { get; set; }

    public TimeSpan? ConnectionTimeout       { get; set; }
    public TimeSpan? DefaultOperationTimeout { get; set; }
}