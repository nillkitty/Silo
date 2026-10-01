namespace Silo.Providers.Format;

/// <summary>
///     Base type for all document types
/// </summary>
public class DocumentBase<TContent> : IDocument<TContent>, IDataSource
{
    public Uri    Uri           { get; }
    public string CanonicalName { get; }

    public string         LocalNamespace  { get; }
    public string         NativeNamespace { get; }
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