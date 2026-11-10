using System.IO;

namespace Silo.Tasks;

/// <summary>
///     The small helpers file work uses to be a task: loading, reloading, saving, importing and exporting all end the same way -- a file of some
///     size and (often) a number of things in it -- so they say so with <see cref="SizeOf" /> and <see cref="Write" /> rather than each writing the
///     bookkeeping again.
/// </summary>
public static class FileTasks
{
    /// <summary>The size of a file in bytes; null when there is no such file (a URL, a file that went away, a path that cannot be read).</summary>
    public static long? SizeOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The total size of some files; null when none of them could be sized.</summary>
    public static long? SizeOf(IEnumerable<string> paths)
    {
        long? total = null;
        foreach (var p in paths)
            if (SizeOf(p) is { } n)
                total = (total ?? 0) + n;

        return total;
    }

    /// <summary>Runs file work as a task named <paramref name="name" />; a failure is recorded and rethrown, a cancellation recorded and rethrown.</summary>
    public static Task RunAsync(string name, string source, Func<TrackedTask, Task> work, bool canCancel = false)
    {
        return TaskCenter.Run(name, source, work, canCancel);
    }

    /// <summary>
    ///     Writes a file as a task (an export): runs <paramref name="write" /> and records the size the file ended up with and, when given, how many
    ///     things went into it. Runs on the caller's thread; a failure is recorded and rethrown.
    /// </summary>
    /// <param name="path">The file written.</param>
    /// <param name="count">How many results went in (rows, objects, certificates); null when that does not apply.</param>
    public static void Write(string name, string source, string path, Action write, int? count = null)
    {
        var task = TaskCenter.Begin(name, source);
        try
        {
            write();
            task.Succeed(path, count, SizeOf(path));
        }
        catch (Exception ex)
        {
            task.Fail(ex);
            throw;
        }
    }

    /// <summary>Reads a file as a task (a load): runs <paramref name="read" /> and records the file's size and the count it returns.</summary>
    public static T Read<T>(string name, string source, string path, Func<T> read, Func<T, int?>? count = null)
    {
        var task = TaskCenter.Begin(name, source);
        try
        {
            var result = read();
            task.Succeed(path, count?.Invoke(result), SizeOf(path));
            return result;
        }
        catch (Exception ex)
        {
            task.Fail(ex);
            throw;
        }
    }
}
