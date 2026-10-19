using System.IO;

namespace Silo.Providers.Format;

public class DataSpec
{
}

public record CsvSpec : IDataSpec
{
    public bool? MatchesData(Stream stream)
    {
        long pos = stream.Required().Position;
        try
        {
            while (true)
            {
            }
        }
        finally
        {
            stream.Position = pos;
        }

        return null;
    }
}