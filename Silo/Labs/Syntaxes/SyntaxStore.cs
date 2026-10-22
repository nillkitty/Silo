using Silo.Providers.Data;
using Serilog;
using Silo.DbModel.Meta;
using Silo.Storage;

namespace Silo.Labs.Syntaxes;

/// <summary>The Silo's syntaxes (what the Lab edits) and the sample strings kept with each one (<c>lab:syntax:book:&lt;name&gt;</c>).</summary>
public static class SyntaxSilo
{
    public const string BookPrefix = "lab:syntax:book:";

    /// <summary>The store of the active Silo, or null when none is open (or it keeps no syntaxes).</summary>
    public static ISyntaxStore? Store => App.Instance?.ActiveSilo?.Data as ISyntaxStore;

    public static bool IsAvailable => Store is not null;

    public static IReadOnlyList<SyntaxRecord> All()
    {
        try
        {
            return Store?.GetSyntaxes() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the syntaxes failed");
            return [];
        }
    }

    public static SyntaxRecord? Find(string nameOrAlias)
    {
        try
        {
            return Store?.FindSyntax(nameOrAlias.Trim());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Looking up the syntax {Name} failed", nameOrAlias);
            return null;
        }
    }

    public static SyntaxBook LoadBook(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new SyntaxBook();

        try
        {
            var item = Stored.At<SyntaxBook>(BookPrefix + name.Trim());
            return item.IsAvailable && item.TryGet(out var book) && book is not null ? book : new SyntaxBook();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the samples of {Name} failed", name);
            return new SyntaxBook();
        }
    }

    public static void SaveBook(string name, SyntaxBook book)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var item = Stored.At<SyntaxBook>(BookPrefix + name.Trim());
        if (!item.IsAvailable) return;

        if (book.IsEmpty) item.Delete();
        else item.Set(book);
    }

    public static void DeleteBook(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        Stored.At<SyntaxBook>(BookPrefix + name.Trim()).Delete();
    }
}
