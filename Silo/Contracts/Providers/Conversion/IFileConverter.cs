using Silo.Providers.Reflection;

namespace Silo.Providers.Conversion;

/// <summary>What a conversion made.</summary>
/// <param name="Output">The file written.</param>
/// <param name="Notes">Anything worth knowing about how it went, one line each (what was left out, what looked odd).</param>
public sealed record FileConversion(string Output, IReadOnlyList<string> Notes);

/// <summary>
///     Turns files of one <see cref="IFileFormat" /> into files of another: an SMD dump into a ROM image, one kind of archive
///     into another, JSON into YAML. The File Converter tool lists every converter and offers, for each file, the formats some
///     converter can make of it.
/// </summary>
/// <remarks>Register an implementation with <c>[Singleton(typeof(IFileConverter))]</c>; it must be safe to call from any thread.</remarks>
[RequireRegistration]
public interface IFileConverter
{
    /// <summary>What the converter does, in a few words ("SMD to ROM image").</summary>
    string DisplayName { get; }

    /// <summary>
    ///     The format the file is in, as this converter sees it, or null when it cannot read the file. May look inside the
    ///     file, so it can be slow for a large one.
    /// </summary>
    IFileFormat? SourceFormat(string input);

    /// <summary>The formats the file can be turned into; empty when it cannot read the file.</summary>
    IReadOnlyList<IFileFormat> TargetsFor(string input);

    /// <summary>What the converter makes of the file, as lines to show before it is converted (what was looked at, and what that showed).</summary>
    IReadOnlyList<string> Inspect(string input)
    {
        return [];
    }

    /// <summary>Writes the file in <paramref name="target" /> format to <paramref name="output" /> (replacing any file there).</summary>
    /// <exception cref="InvalidDataException">The file cannot be read as what it was taken for.</exception>
    FileConversion Convert(string input, IFileFormat target, string output, CancellationToken cancel = default);
}
