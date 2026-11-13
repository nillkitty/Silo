using System.IO;
using Silo.Providers.Archive;

namespace Silo.Providers.Format.Containers;

/// <summary>
///     A container format that cannot be changed in place (a tar, an ISO image, a cabinet): a change takes everything out into
///     a folder, makes the changes there and builds the container again from the folder (see <see cref="CreateFrom" />). The
///     new container is written beside the old one and only then takes its place, so a change that fails leaves the old one
///     as it was.
/// </summary>
public abstract class RebuiltContainerFormat(string displayName, string fileExtension, string contentType) : IContainerFormat
{
    public string DisplayName   { get; } = displayName;
    public string FileExtension { get; } = fileExtension;
    public string ContentType   { get; } = contentType;

    public virtual bool CanWrite => true;

    public abstract IReadOnlyList<ContainerEntry> List(string container, CancellationToken cancel = default);

    public abstract Stream OpenEntry(string container, string entry);

    public abstract void CreateFrom(string container, string directory, CancellationToken cancel = default);

    public void Create(string container)
    {
        string empty = Staging.NewDirectory();
        try
        {
            CreateFrom(container, empty);
        }
        finally
        {
            Staging.Delete(empty);
        }
    }

    public void Update(string container, IReadOnlyList<ContainerChange> changes, CancellationToken cancel = default)
    {
        if (!CanWrite) throw new NotSupportedException($"{DisplayName} files cannot be changed.");

        string stage = Staging.NewDirectory();
        string next  = Staging.BesideFile(container);
        try
        {
            ((IContainerFormat)this).ExtractTo(container, stage, null, cancel);
            Staging.Apply(stage, changes, cancel);
            CreateFrom(next, stage, cancel);
            File.Move(next, container, true);
        }
        finally
        {
            Staging.Delete(stage);
            if (File.Exists(next)) File.Delete(next);
        }
    }

    public override string ToString()
    {
        return DisplayName;
    }
}

/// <summary>The folders a container is taken out into while it is changed or converted.</summary>
public static class Staging
{
    /// <summary>Where they go.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "Silo", "staging");

    /// <summary>A new, empty folder of its own.</summary>
    public static string NewDirectory(string? under = null)
    {
        string path = Path.Combine(under ?? Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A name for a file in the same folder as <paramref name="file" /> (so it can replace it in one move), not yet used.</summary>
    public static string BesideFile(string file)
    {
        string full = Path.GetFullPath(file);
        return Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Deletes a staging folder, if it can; one left behind is only taking up room in the temporary folder.</summary>
    public static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Could not delete the staging folder {Folder}", directory);
        }
    }

    /// <summary>Makes container changes to a folder that holds what the container holds.</summary>
    public static void Apply(string root, IEnumerable<ContainerChange> changes, CancellationToken cancel = default)
    {
        foreach (var change in changes)
        {
            cancel.ThrowIfCancellationRequested();
            switch (change)
            {
                case ContainerChange.AddFile add:
                {
                    string target = ContainerPaths.Resolve(root, add.Path) ?? throw new IOException($"\"{add.Path}\" is not a usable path.");
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(add.SourceFile, target, true);
                    break;
                }
                case ContainerChange.AddDirectory dir:
                {
                    string target = ContainerPaths.Resolve(root, dir.Path) ?? throw new IOException($"\"{dir.Path}\" is not a usable path.");
                    if (File.Exists(target)) File.Delete(target);
                    Directory.CreateDirectory(target);
                    break;
                }
                case ContainerChange.Remove remove:
                {
                    if (ContainerPaths.Resolve(root, remove.Path) is not { } target) break;
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    else if (File.Exists(target)) File.Delete(target);
                    break;
                }
            }
        }
    }
}
