using Silo.Providers.Reflection;

namespace Silo.Contracts.Core;

[RequireRegistration]
public interface IScriptProvider : IFileFormat, ILaunchable;