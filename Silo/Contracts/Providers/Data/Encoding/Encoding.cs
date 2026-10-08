using System.IO;
using Telefrag;

namespace Silo.Providers.Data.Encoding;

internal interface IEdgeEncoding : IStreamTransform<IEdgeEncoding>
{
}

public interface IConditionalMod<in TSelf> where TSelf : IConditionalMod<TSelf>
{
    /// <summary>
    ///     Determines whether this transform should apply to the target stream
    ///     based on the current Context stack.
    /// </summary>
    bool ShouldApply(TSelf mod, Context ctx);
}

internal interface IStreamTransform<in TSelf> : IConditionalMod<TSelf> where TSelf : IConditionalMod<TSelf>
{
    /// <summary>
    ///     Applies the transform to an input stream and returns the configured
    ///     output stream.
    /// </summary>
    Stream Transform(Stream inputStream);
}