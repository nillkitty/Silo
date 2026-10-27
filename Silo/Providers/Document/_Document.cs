using Silo.Providers.Data.Source;

namespace Silo.Providers.Document;

/// <summary>
///     Base type for all document types
/// </summary>
public class DocumentBase<TContent> : IDocument<TContent>, IDataSource
{
    public Uri     DataSourceUri { get; } = null!;
    public Uri     Uri           { get; }
    public string  CanonicalName { get; } = null!;
    public string? DefaultAlias  { get; }

    public string         LocalNamespace  { get; } = null!;
    public string         NativeNamespace { get; } = null!;
    public IDataProvider? Provider        { get; }

    public DocumentBase(IDocumentProvider<TContent> provider, Uri uri)
    {
        Provider = provider.Required();
        Uri      = uri.Required();

        var dnp = App.Resolve<IDocumentNameProvider>();
        if (dnp is null)
            // split URI into Local (path minus name)
            //      native (name and extension)
            // and canonical names ('.' or document name)
            return;

        (LocalNamespace, NativeNamespace, CanonicalName) = dnp.ResolveDocumentName(Uri, this) ?? (null, null, null)!;
    }
}