using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Silo.Analyzers;

namespace Silo.Tests.Analyzers;

/// <summary>One thing to compile and what SILO001 should say about it.</summary>
/// <param name="Name">What the case shows.</param>
/// <param name="Source">The code under test (the attribute stubs are added in front of it, unless there is a <paramref name="Library" />).</param>
/// <param name="Reported">The names of the classes that get the warning, in source order.</param>
/// <param name="MessageHas">Text the (first) message must contain.</param>
/// <param name="Library">Code compiled to a separate assembly (with the stubs) that <paramref name="Source" /> references.</param>
public record RequireRegistrationCase(string Name, string Source, string[] Reported, string? MessageHas = null, string? Library = null)
{
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>Compiles a snippet and runs <see cref="RequireRegistrationAnalyzer" /> over it.</summary>
public static class RequireRegistrationHarness
{
    /// <summary>What the analyzer matches by name: the registration attributes and the marker. Declared here so no other assembly is needed.</summary>
    public const string Stubs = """
                                #nullable enable
                                namespace Telefrag.DI
                                {
                                    public class SingletonAttribute : System.Attribute
                                    {
                                        public SingletonAttribute() { }
                                        public SingletonAttribute(System.Type serviceType) { }
                                    }
                                    public class TransientAttribute : System.Attribute
                                    {
                                        public TransientAttribute() { }
                                        public TransientAttribute(System.Type serviceType) { }
                                    }
                                }
                                namespace Silo.Ui
                                {
                                    public class RegisterAttribute : System.Attribute
                                    {
                                        public RegisterAttribute(System.Type type, bool singleton = true, string? key = null) { }
                                    }
                                }
                                namespace Silo.Contracts
                                {
                                    [System.AttributeUsage(System.AttributeTargets.Interface)]
                                    public sealed class RequireRegistrationAttribute : System.Attribute { }
                                }
                                """;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest);

    public static IReadOnlyList<RequireRegistrationCase> Cases { get; } =
    [
        new("a class that implements a marked interface and is not registered", """
                                                                                 [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                                 public class Foo : IFoo { }
                                                                                 """, ["Foo"], "'Foo' implements 'IFoo'"),
        new("[Singleton] registers it", """
                                        [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                        [Telefrag.DI.Singleton(typeof(IFoo))] public class Foo : IFoo { }
                                        """, []),
        new("[Transient] registers it", """
                                        [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                        [Telefrag.DI.Transient] public class Foo : IFoo { }
                                        """, []),
        new("Silo's [Register] registers it", """
                                              [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                              [Silo.Ui.Register(typeof(IFoo))] public class Foo : IFoo { }
                                              """, []),
        new("an attribute derived from [Singleton] registers it", """
                                                                  [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                  public class ServiceAttribute : Telefrag.DI.SingletonAttribute { }
                                                                  [Service] public class Foo : IFoo { }
                                                                  """, []),
        new("some other attribute does not", """
                                             [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                             [System.Obsolete] public class Foo : IFoo { }
                                             """, ["Foo"]),
        new("an abstract class is not checked", """
                                                [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                public abstract class Foo : IFoo { }
                                                """, []),
        new("a registered base class registers what derives from it", """
                                                                      [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                      [Telefrag.DI.Singleton] public abstract class Foo : IFoo { }
                                                                      public class Bar : Foo { }
                                                                      """, []),
        new("[Transient] on a base class", """
                                           [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                           [Telefrag.DI.Transient] public class Foo : IFoo { }
                                           public class Bar : Foo { }
                                           """, []),
        new("a registration several levels up", """
                                                [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                [Telefrag.DI.Singleton] public class A { }
                                                public class B : A { }
                                                public class C : B, IFoo { }
                                                """, []),
        new("a base class with Silo's [Register]", """
                                                   [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                   [Silo.Ui.Register(typeof(IFoo))] public class Foo : IFoo { }
                                                   public class Bar : Foo { }
                                                   """, []),
        new("a generic base class", """
                                    [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                    [Telefrag.DI.Singleton] public abstract class Base<T> : IFoo { }
                                    public class Bar : Base<int> { }
                                    """, []),
        new("an unregistered base class does not help", """
                                                        [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                        public abstract class Foo : IFoo { }
                                                        public class Bar : Foo { }
                                                        """, ["Bar"]),
        new("a base class that is registered does not cover a sibling branch", """
                                                                               [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                               [Telefrag.DI.Singleton] public class Registered { }
                                                                               public class Other : IFoo { }
                                                                               """, ["Other"]),
        new("a derived interface counts", """
                                          [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                          public interface IBar : IFoo { }
                                          public class Bar : IBar { }
                                          """, ["Bar"], "'IFoo'"),
        new("an interface without the marker is ignored", """
                                                          public interface IFoo { }
                                                          public class Foo : IFoo { }
                                                          """, []),
        new("interfaces and structs are not checked", """
                                                      [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                      public interface IBar : IFoo { }
                                                      public struct S : IFoo { }
                                                      """, []),
        new("every marked interface is named once, in one warning", """
                                                                    [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                    [Silo.Contracts.RequireRegistration] public interface IBar { }
                                                                    public class Both : IFoo, IBar { }
                                                                    """, ["Both"], "'IFoo', 'IBar'"),
        new("a generic interface", """
                                   [Silo.Contracts.RequireRegistration] public interface IHandler<T> { }
                                   public class IntHandler : IHandler<int> { }
                                   """, ["IntHandler"], "'IHandler<int>'"),
        new("the attribute may be on another part of a partial class", """
                                                                       [Silo.Contracts.RequireRegistration] public interface IFoo { }
                                                                       public partial class Foo : IFoo { }
                                                                       [Telefrag.DI.Singleton] public partial class Foo { }
                                                                       """, []),
        new("a nested class", """
                              [Silo.Contracts.RequireRegistration] public interface IFoo { }
                              public class Outer { public class Inner : IFoo { } }
                              """, ["Inner"]),
        new("a record", """
                        [Silo.Contracts.RequireRegistration] public interface IFoo { }
                        public record Foo : IFoo;
                        """, ["Foo"]),
        new("interface and attributes from another assembly: not registered", "public class Foo : IFoo { }", ["Foo"], "'IFoo'",
            Library: "[Silo.Contracts.RequireRegistration] public interface IFoo { }"),
        new("interface and attributes from another assembly: registered", "[Telefrag.DI.Transient] public class Foo : IFoo { }", [],
            Library: "[Silo.Contracts.RequireRegistration] public interface IFoo { }"),
        new("a registered base class from another assembly", "public class Foo : Base { }", [],
            Library: "[Silo.Contracts.RequireRegistration] public interface IFoo { } [Telefrag.DI.Singleton] public abstract class Base : IFoo { }"),
        new("an unregistered base class from another assembly", "public class Foo : Base { }", ["Foo"], "'IFoo'",
            Library: "[Silo.Contracts.RequireRegistration] public interface IFoo { } public abstract class Base : IFoo { }")
    ];

    /// <summary>The SILO001 diagnostics for a case; the code must compile without errors.</summary>
    public static async Task<IReadOnlyList<Diagnostic>> RunAsync(RequireRegistrationCase c)
    {
        var references = TrustedReferences();

        if (c.Library is not null)
        {
            var library = CSharpCompilation.Create("Lib", [CSharpSyntaxTree.ParseText(Stubs + "\n" + c.Library, Parse)], references,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var       emit  = library.Emit(image);
            if (!emit.Success) throw new InvalidOperationException("The library does not compile: " + string.Join("; ", emit.Diagnostics));
            references = references.Add(MetadataReference.CreateFromImage(image.ToArray()));
        }

        string source = c.Library is null ? Stubs + "\n" + c.Source : c.Source;
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(source, Parse)], references,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new InvalidOperationException("The snippet does not compile: " + string.Join("; ", errors));

        var withAnalyzers = compilation.WithAnalyzers([new RequireRegistrationAnalyzer()]);
        var all           = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return all.Where(d => d.Id == RequireRegistrationAnalyzer.DiagnosticId).OrderBy(d => d.Location.SourceSpan.Start).ToList();
    }

    /// <summary>The identifier a diagnostic points at.</summary>
    public static string NameAt(Diagnostic d)
    {
        return d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);
    }

    private static ImmutableArray<MetadataReference> TrustedReferences()
    {
        string paths = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [..paths.Split(Path.PathSeparator).Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll"
                                                              or "System.Collections.dll" or "netstandard.dll")
                       .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }
}
