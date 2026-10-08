namespace Silo.Contracts.Resources;

/// <summary>
///     Where a <see cref="ResourceEntry" /> came from.
/// </summary>
public enum ResourceKind
{
    /// <summary>
    ///     A WPF resource compiled into an assembly's <c>&lt;Assembly&gt;.g.resources</c> (a file with
    ///     the <c>Resource</c> build action, or compiled XAML). Addressed by pack URI.
    /// </summary>
    CompiledResource,

    /// <summary>
    ///     A raw manifest resource (a file with the <c>EmbeddedResource</c> build action).
    ///     Addressed by manifest name.
    /// </summary>
    EmbeddedResource,

    /// <summary>
    ///     A string-keyed entry in the application's <see cref="System.Windows.ResourceDictionary" />
    ///     (including everything merged into it), such as the <c>StreamGeometry</c> icons in
    ///     <c>Resources/Paths.xaml</c>. Addressed by <c>{StaticResource Key}</c>.
    /// </summary>
    ResourceDictionaryEntry
}
