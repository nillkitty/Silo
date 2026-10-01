using Microsoft.Win32;

namespace Silo.Connectors.System;

public class RegistryUriProvider : IUriProvider<RegistryPath>
{
    private static bool TryParseHive(string token, out RegistryHive hive)
    {
        RegistryHive? parsed = token?.ToLowerInvariant() switch
                               {
                                   "hkcr" or "hkey_classes_root"     => RegistryHive.ClassesRoot,
                                   "hkcu" or "hkey_current_user"     => RegistryHive.CurrentUser,
                                   "hklm" or "hkey_local_machine"    => RegistryHive.LocalMachine,
                                   "hku" or "hkey_users"             => RegistryHive.Users,
                                   "hkcc" or "hkey_current_config"   => RegistryHive.CurrentConfig,
                                   "hkpd" or "hkey_performance_data" => RegistryHive.PerformanceData,
                                   _                                 => null
                               };

        hive = parsed ?? default;
        return parsed.HasValue;
    }

    public RegistryPath? ParseUri(Uri uri, bool throwErrors)
    {
        var sch  = uri.Required().Scheme;
        var host = uri.DnsSafeHost;
        var rest = uri.PathAndQuery;
        if (!HandlesScheme(sch))
            return throwErrors
                       ? throw new NotSupportedException($"The URI scheme '{sch}' is not supported on the Registry connector.")
                       : null;


        if (sch.Is("reg") && rest.Trim('/').Nill()) return RegistryPath.LocalRoot;

        // format:  hkcu:///software/microsoft/windows/CurrentVersion/Internet Settings?AutoConfigUrl
        //     or:  reg:///hkey_current_user/software/microsoft/windows

        // A host/authority means remote-registry access (OpenRemoteBaseKey), which RegistryPath
        // has no field for yet -- rather than silently drop it, flag it clearly so it isn't
        // mistaken for a local path.
        if (host.There())
            return throwErrors
                       ? throw new
                             NotSupportedException($"Remote registry access (host '{host}') is not supported yet -- RegistryPath has no field for a remote machine name.")
                       : null;

        // AbsolutePath is already %-decoded (e.g. "Internet%20Settings" -> "Internet Settings"),
        // so the segments below don't need a separate Uri.UnescapeDataString pass.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        RegistryHive hive;
        string[]     pathSegments;

        if (sch.Is("reg"))
        {
            // reg:///<hive>/<key path...> -- the hive is named by the first path segment rather
            // than the scheme.
            if (segments.Length == 0)
                return throwErrors
                           ? throw new UriFormatException($"Registry URI '{uri}' is missing a hive (e.g. 'reg:///hkey_current_user/...').")
                           : null;

            if (!TryParseHive(segments[0], out hive))
                return throwErrors ? throw new UriFormatException($"'{segments[0]}' is not a recognized registry hive.") : null;

            pathSegments = segments[1..];
        }
        else
        {
            // hkcu:// / hklm:// / hkcr:// / hku:// -- the scheme itself names the hive.
            if (!TryParseHive(sch, out hive))
                return throwErrors ? throw new UriFormatException($"'{sch}' is not a recognized registry hive scheme.") : null;

            pathSegments = segments;
        }

        // No remaining segments means the hive's own root, same sentinel RegistryPath.LocalRoot uses.
        var path = pathSegments.Length > 0 ? string.Join('\\', pathSegments) : "*";

        // The query string (if any) names the value within the key, e.g. "...?AutoConfigUrl" ->
        // ValueName "AutoConfigUrl" -- not a key=value pair, the whole (unescaped) query is the name.
        var valueName = uri.Query.Length > 1 ? Uri.UnescapeDataString(uri.Query[1..]) : null;

        return new RegistryPath(hive, path, valueName);
    }

    public bool HandlesScheme(string scheme)
    {
        return scheme?.ToLower() switch
               {
                   "hkcu" or "hklm" or "hkcr" or "hku" => true,
                   "reg"                               => true,
                   _                                   => false
               };
    }
}