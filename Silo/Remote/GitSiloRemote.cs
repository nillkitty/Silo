using Silo.Providers.Data;
using Telefrag.DI;

namespace Silo.Remote;

/// <summary>
///     What a Git-backed Silo will offer on top of <see cref="ISiloRemote" />: the Commit / Branch / Stash semantics of
///     the
///     startup design. <b>Not implemented yet</b> -- <see cref="GitSiloRemote" /> is the placeholder every member of which
///     says so; the contract is here so the UI and the save/close logic can be written against it.
/// </summary>
/// <remarks>
///     Intended behaviour (LibGit2Sharp is already referenced by the project):
///     <list type="bullet">
///         <item>
///             <b>Open</b>: clone the repository into the cache (or fetch + fast-forward the checked-out branch) and
///             open the tracked <c>.silo</c> file.
///         </item>
///         <item><b>Save</b>: the file is rewritten in the working tree; nothing is committed implicitly.</item>
///         <item>
///             <b>Close with changes</b>: Commit (message prompt) / Stash / Discard / Cancel -- "do the right thing" for
///             a Git working tree.
///         </item>
///         <item><b>Branch</b>: list, create and switch; switching reloads the Silo from the branch's version of the file.</item>
///         <item>
///             <b>Push</b>: <see cref="ISiloRemote.PushAsync" /> commits (if the person has not) and pushes the current
///             branch.
///         </item>
///     </list>
/// </remarks>
public interface IGitSiloRemote : ISiloRemote
{
    string CurrentBranch { get; }

    Task<IReadOnlyList<string>> BranchesAsync(CancellationToken ct                            = default);
    Task                        CreateBranchAsync(string        name,    CancellationToken ct = default);
    Task                        SwitchBranchAsync(string        name,    CancellationToken ct = default);
    Task                        CommitAsync(string              message, CancellationToken ct = default);
    Task                        StashAsync(string?              message, CancellationToken ct = default);
    Task                        PopStashAsync(CancellationToken ct = default);
    Task                        PullAsync(CancellationToken     ct = default);
}

/// <summary>Placeholder for the Git-backed remote. Every member throws <see cref="NotSupportedException" />.</summary>
[Transient(typeof(ISiloRemote))]
public sealed class GitSiloRemote : IGitSiloRemote
{
    public const string NotImplementedMessage = "Silos stored in a Git repository are not supported yet. HTTP(S) URLs are.";

    public Uri Uri { get; }

    public string CurrentBranch => throw Nope();

    public bool HasUnpushedChanges
    {
        get => false;
        set { }
    }

    public string Kind => "Git";

    private static NotSupportedException Nope()
    {
        return new NotSupportedException(NotImplementedMessage);
    }

    public GitSiloRemote(Uri uri)
    {
        Uri = uri;
    }

    public Task<IReadOnlyList<string>> BranchesAsync(CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task CreateBranchAsync(string name, CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task SwitchBranchAsync(string name, CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task CommitAsync(string message, CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task StashAsync(string? message, CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task PopStashAsync(CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task PullAsync(CancellationToken ct = default)
    {
        throw Nope();
    }

    public Task PushAsync(string localFile, bool force, CancellationToken ct = default)
    {
        throw Nope();
    }
}