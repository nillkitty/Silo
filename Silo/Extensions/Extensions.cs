using System.Runtime.CompilerServices;
using Telefrag.DI;
using Telefrag.Exceptions;

namespace Silo.Extensions;

public static class Extensions
{
    extension(Container c)
    {
        public TService Require<TService>(string? name = null)
            where TService : class
        {
            return c.Resolve<TService>(name, true) ??
                   throw new
                       RequiredComponentMissingException($"Required component '{typeof(TService)}' is not registered.");
        }
    }

    extension(IEnumerable<string> ss)
    {
        public string Join(char c) => string.Join(c, ss);
    }
}