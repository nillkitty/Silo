using System.Windows.Data;

namespace Silo.Contracts.Adapters;

public class ValueConverterAdapter<TConverter>(Type? targetType = null, bool throwErrors = true)
    : IAdapter<object, object> where TConverter : IValueConverter, new()
{
    public IValueConverter Converter   { get; }      = new TConverter();
    public Type?           TargetType  { get; set; } = targetType;
    public bool            ThrowErrors { get; set; } = throwErrors;

    public async Task<object?> ConvertAsync(object inObj)
    {
        return Converter.Convert(inObj, TargetType ?? typeof(object), null, null);
    }
}