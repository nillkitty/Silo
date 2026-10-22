using System.IO;
using Silo.Providers.Data;

namespace Silo.Providers.Format;

public class DataSpec
{
}

public record CsvSpec(string Name) : IDataSpec
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