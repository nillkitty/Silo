using Microsoft.Win32;
using Silo.Providers.Reflection;

namespace Silo.Connectors.System;

/// <summary>A registry key or hive as an <see cref="IItemContainer" />: its values are the items (reached by path, as a URI with the value's name).</summary>
public partial class RegistryModel
{
    /// <inheritdoc />
    public bool CanEnumerate => IsContainer && Kind != RegistryItemKind.Root && Data is not null;

    /// <inheritdoc />
    public Uri? ContainerUri => Uri;

    /// <inheritdoc />
    public string ContainerName => Path.Length > 0 ? Path : "Registry";

    /// <inheritdoc />
    public IEnumerable<ContainerItem> EnumerateItems(bool recursive, CancellationToken cancel = default)
    {
        if (!CanEnumerate) yield break;

        var pending = new Stack<(RegistryKey Key, string Path, bool Owned)>();
        pending.Push((Data!, Path, false));
        try
        {
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var (key, path, owned) = pending.Pop();

                string[] values = TryGet(() => key.GetValueNames()) ?? [];
                foreach (string value in values)
                {
                    cancel.ThrowIfCancellationRequested();
                    var item = ValueItem(key, path, value);
                    if (item is not null) yield return item;
                }

                if (recursive)
                {
                    foreach (string sub in TryGet(() => key.GetSubKeyNames()) ?? [])
                        if (TryOpen(() => key.OpenSubKey(sub)) is { } child)
                            pending.Push((child, path + "\\" + sub, true));
                }

                if (owned) key.Dispose();
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                var (key, _, owned) = pending.Pop();
                if (owned) key.Dispose();
            }
        }
    }

    private static T? TryGet<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException or global::System.IO.IOException)
        {
            return null;
        }
    }

    private static ContainerItem? ValueItem(RegistryKey key, string path, string value)
    {
        try
        {
            string signature = ValueSignature(key, value, out object? data, out RegistryValueKind kind);
            var    uri       = RegistryUris.FromKeyName(path, value);

            // a string value that names a file ("C:\Tools\run.exe") is that kind of file; any other value has no name to go by
            string? hint = data is string text && kind is RegistryValueKind.String or RegistryValueKind.ExpandString
                               ? FileHint(text)
                               : null;
            return new ContainerItem(path + "?" + value, uri, false, signature, () => ValueStream(data), hint);
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException or global::System.IO.IOException)
        {
            return null;
        }
    }

    private static string? FileHint(string text)
    {
        string trimmed = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        int    space   = trimmed.IndexOf(' ');
        string first   = space > 0 && !global::System.IO.Path.HasExtension(trimmed) ? trimmed[..space] : trimmed;
        return global::System.IO.Path.HasExtension(first) && first.IndexOfAny(global::System.IO.Path.GetInvalidPathChars()) < 0 ? first : null;
    }

    private static Stream? ValueStream(object? data)
    {
        return data switch
               {
                   byte[] bytes   => new MemoryStream(bytes, false),
                   string text    => new MemoryStream(global::System.Text.Encoding.UTF8.GetBytes(text), false),
                   string[] lines => new MemoryStream(global::System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)), false),
                   null           => null,
                   _              => new MemoryStream(global::System.Text.Encoding.UTF8.GetBytes(Convert.ToString(data, global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty), false)
               };
    }

    /// <summary>The value's kind and data as text: differs whenever the value does.</summary>
    private static string ValueSignature(RegistryKey key, string value, out object? data, out RegistryValueKind kind)
    {
        kind = key.GetValueKind(value);
        data = key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string text = data switch
                      {
                          byte[] bytes   => Convert.ToHexString(bytes),
                          string[] lines => string.Join('\n', lines),
                          null           => string.Empty,
                          _              => Convert.ToString(data, global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                      };
        return kind + ":" + text;
    }
}
