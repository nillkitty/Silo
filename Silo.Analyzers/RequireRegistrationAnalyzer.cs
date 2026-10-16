using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Silo.Analyzers;

/// <summary>
///     SILO001: a class implements an interface marked <c>[RequireRegistration]</c> (<c>Silo.Contracts</c>) -- directly,
///     through a base class or through a derived interface -- but neither it nor any of its base classes carries one of the
///     registration attributes (<c>[Singleton]</c>, <c>[Transient]</c> or Silo's <c>[Register]</c>, or anything derived from them).
/// </summary>
/// <remarks>
///     Everything is matched by full name, so the attributes are found wherever they come from (this project cannot reference
///     the app it checks). Abstract and static classes are skipped: they are never registered themselves. An attribute on a
///     base class (abstract or not) counts for every class derived from it, so a family of implementations can be
///     registered once, on the base.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequireRegistrationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SILO001";

    /// <summary>The attribute that marks an interface (full name).</summary>
    public const string RequireRegistrationAttributeName = "Silo.Contracts.RequireRegistrationAttribute";

    /// <summary>The attributes (or their base classes) that register an implementation (full names).</summary>
    public static readonly ImmutableArray<string> RegistrationAttributeNames =
        ImmutableArray.Create("Telefrag.DI.SingletonAttribute", "Telefrag.DI.TransientAttribute", "Silo.Ui.RegisterAttribute");

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Implementation of an interface that requires registration is not registered",
        "'{0}' implements {1} (marked [RequireRegistration]) but neither it nor a base class declares [Singleton], [Transient] or [Register]",
        "Silo.Registration",
        DiagnosticSeverity.Warning,
        true,
        "An interface marked [RequireRegistration] is resolved from the container, so each class that implements it " +
        "must declare how it is registered, on its own definition or on a base class: [Singleton], [Transient] or [Register] (or an attribute derived from them).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsImplicitlyDeclared || type.IsAnonymousType)
            return;

        if (type.Locations.Length == 0 || !type.Locations[0].IsInSource)
            return;

        List<string>? required = null;
        foreach (var iface in type.AllInterfaces)
            if (HasAttribute(iface.OriginalDefinition, RequireRegistrationAttributeName))
                (required ??= []).Add("'" + iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + "'");

        if (required is null) return;

        // [Singleton]/[Transient]/[Register] on the class or on any class it derives from
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (string name in RegistrationAttributeNames)
                if (HasAttribute(t.OriginalDefinition, name))
                    return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name, string.Join(", ", required)));
    }

    /// <summary>Whether the symbol has an attribute that is, or derives from, the one with this full name.</summary>
    internal static bool HasAttribute(ISymbol symbol, string fullName)
    {
        foreach (var attribute in symbol.GetAttributes())
            for (var t = attribute.AttributeClass; t is not null; t = t.BaseType)
                if (FullNameOf(t) == fullName)
                    return true;

        return false;
    }

    private static string FullNameOf(INamedTypeSymbol type)
    {
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;
    }
}
