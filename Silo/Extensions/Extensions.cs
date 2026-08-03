using System.Runtime.CompilerServices;

namespace Silo.Extensions;

public static class Extensions
{
    extension(IEnumerable<string> ss)
    {
        public string Join(char c) => string.Join(c, ss);
    }

    extension(string s)
    {
        public bool Nill()  => string.IsNullOrEmpty(s);
        public bool There() => !s.Nill();
    }

    extension<TTarget>(TTarget? o)
    {
        public TTarget Required([CallerMemberName] string paramName = "")
        {
            if (o is null)
                ArgumentNullException.ThrowIfNull(o, paramName);
            return o;
        }
    }
}