namespace Silo.Contracts;

/// <summary>
///     Marks an interface whose implementations must be registered with the container: every non-abstract class that
///     implements it (directly, through a base class or through an interface that derives from it) has to be registered:
///     it, or a class it derives from, carries <c>[Singleton]</c>, <c>[Transient]</c> or <c>[Register]</c> (or an
///     attribute derived from one of them). The <c>Silo.Analyzers</c> analyzer reports SILO001, a warning, for each one that is not.
/// </summary>
/// <remarks>It only marks: nothing reads it at run time.</remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class RequireRegistrationAttribute : Attribute
{
}
