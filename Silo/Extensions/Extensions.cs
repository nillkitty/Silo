using System.Runtime.CompilerServices;

namespace Silo.Extensions;

public static class Extensions
{
    extension(IEnumerable<string> ss)
    {
        public string Join(char c) => string.Join(c, ss);
    }
}