using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Serilog;
using Silo.Contracts.ConnectorServices;
using Silo.Providers.Data.Shape;
using Silo.Providers.Data.Source;
using Silo.Providers.Data.Validation;

namespace Silo.Contracts;

/// <summary>
///     One step that turns an item of data into zero, one or many items: a filter drops it, a conversion replaces it, an
///     <c>Explode</c> splits it, an action does something with it (and usually hands it on). Steps are chained into an
///     <see cref="IDataTransformGroup" /> -- the "pre-configured actions" a Batch Processing template runs on each item
///     an <see cref="IObservableDataSource" /> reports.
/// </summary>
/// <remarks>
///     <para>
///         A transform is configuration, not a run: it is built (in code or in a template's XAML), validated, and then
///         shared by every run, so it must not keep per-run state in fields. A transform that has to remember something
///         between items -- how many it has let through, the rows it is sorting -- asks the
///         <see cref="ITransformContext" /> for it (<see cref="ITransformContext.GetState{T}" />) and says so in
///         <see cref="Flags" />.
///     </para>
///     <para>
///         The operators on the wiki's Operators page map onto this directly. <see cref="Operation" /> is the operation's
///         name, <see cref="Category" /> its category, and <see cref="InputShape" /> the shape of the operand that arrives
///         as the item. A binary operation such as <c>Filter(Logical, Table)</c> becomes a transform whose left operand
///         (the predicate) is configured on the transform and whose right operand (the table) is the item; a unary one
///         has only the item.
///     </para>
/// </remarks>
public interface IDataTransform : IDisplayName, IValidatable
{
    /// <summary>
    ///     The operation's name, as the Operators page spells it: <c>Filter</c>, <c>Select</c>, <c>Take</c>, ... (
    ///     <see cref="IDisplayName.DisplayName" /> is the human label for this configured step).
    /// </summary>
    string Operation { get; }

    /// <summary>
    ///     The operator category, e.g. <c>Filtering &amp; Selection</c> or <c>Type Conversion</c>; null if not
    ///     categorized.
    /// </summary>
    string? Category => null;

    /// <summary>What running the transform can have an effect on or depend on; see <see cref="TransformFlags" />.</summary>
    TransformFlags Flags => TransformFlags.None;

    /// <summary>
    ///     The shape of item the transform takes; <see cref="DataShape.Unknown" /> means any (the Operators page's
    ///     "Any").
    /// </summary>
    DataShape InputShape => DataShape.Unknown;

    /// <summary>The shape of item it produces; <see cref="DataShape.Unknown" /> means "the same as it was given".</summary>
    DataShape OutputShape => DataShape.Unknown;

    /// <summary>
    ///     Whether an item of this shape can be given to the transform. An unknown or dynamic shape is accepted (it may
    ///     turn out to fit).
    /// </summary>
    bool Accepts(DataShape input)
    {
        return input is DataShape.Unknown or DataShape.Dynamic || InputShape is DataShape.Unknown or DataShape.Dynamic ||
               InputShape == input;
    }

    /// <summary>
    ///     The shape of what comes out when an item of shape <paramref name="input" /> goes in; lets a pipeline be
    ///     checked before anything runs.
    /// </summary>
    DataShape GetOutputShape(DataShape input)
    {
        return OutputShape == DataShape.Unknown ? input : OutputShape;
    }

    /// <summary>
    ///     Applies the transform to one item. Produces the items that replace it: none to drop it, one to change it (or to
    ///     pass it on untouched, as an action does), several to split it. May produce nothing until
    ///     <see cref="CompleteAsync" /> if it has to see all the items first (<see cref="TransformFlags.Buffers" />).
    ///     A transform marked <see cref="TransformFlags.SideEffects" /> must not act when
    ///     <see cref="ITransformContext.IsDryRun" /> is set, though it still reports what it would have produced.
    /// </summary>
    IAsyncEnumerable<TransformItem> TransformAsync(TransformItem item, ITransformContext context, CancellationToken cancel = default);

    /// <summary>
    ///     Called once when there are no more items in this run, so a transform that held items back (a sort, a grouping,
    ///     a summary) can release them. By default there is nothing to release.
    /// </summary>
    IAsyncEnumerable<TransformItem> CompleteAsync(ITransformContext context, CancellationToken cancel = default)
    {
        return DataTransformExtensions.NoItems();
    }

    /// <summary>
    ///     Checks that the transform is configured sensibly and throws if it is not. By default there is nothing to
    ///     check.
    /// </summary>
    void IValidatable.Validate()
    {
    }
}

/// <summary>
///     An ordered set of transforms that is itself a transform, so groups nest and anywhere that takes a transform takes a
///     group. How the members work together is <see cref="Mode" />: a chain (each member's output feeds the next), a fan
///     out (each member gets the item) or a list of actions (each member acts on the item, which carries on unchanged).
/// </summary>
/// <remarks>
///     Everything but the enumeration and the display name has a working default, so a group is just a list:
///     see <see cref="DataTransformGroup" />. Reports what went wrong with a member according to <see cref="OnError" />.
/// </remarks>
public interface IDataTransformGroup : IDataTransform, IEnumerable<IDataTransform>
{
    /// <summary>How the members are combined.</summary>
    DataTransformGroupMode Mode => DataTransformGroupMode.Sequential;

    /// <summary>What happens when a member throws (cancellation always propagates).</summary>
    TransformErrorPolicy OnError => TransformErrorPolicy.Throw;

    string? IDataTransform.Category => "Pipeline";

    /// <summary>The union of the members' flags.</summary>
    TransformFlags IDataTransform.Flags => this.Aggregate(TransformFlags.None, (flags, t) => flags | t.Flags);

    DataShape IDataTransform.InputShape
    {
        get
        {
            var shapes = this.Select(t => t.InputShape).Distinct().ToList();
            return Mode == DataTransformGroupMode.Sequential ? shapes.FirstOrDefault() : shapes.Count == 1 ? shapes[0] : DataShape.Unknown;
        }
    }

    string IDataTransform.Operation => "Group";

    DataShape IDataTransform.OutputShape => GetOutputShape(DataShape.Unknown);

    DataShape IDataTransform.GetOutputShape(DataShape input)
    {
        switch (Mode)
        {
            case DataTransformGroupMode.Sequential:
                return this.Aggregate(input, (shape, t) => t.GetOutputShape(shape));
            case DataTransformGroupMode.Actions:
                return input;
            default:
                var shapes = this.Select(t => t.GetOutputShape(input)).Distinct().ToList();
                return shapes.Count == 1 ? shapes[0] : DataShape.Unknown;
        }
    }

    /// <summary>Validates every member and, for a chain, that each member can take what the one before produces.</summary>
    void IValidatable.Validate()
    {
        var members = this.ToList();
        foreach (var member in members) member.Validate();

        if (Mode != DataTransformGroupMode.Sequential) return;

        var shape = DataShape.Unknown;
        foreach (var member in members)
        {
            if (!member.Accepts(shape))
                throw new
                    ValidationException($"'{member.DisplayName ?? member.Operation}' takes {member.InputShape} data, but the step before it produces {shape}.");

            shape = member.GetOutputShape(shape);
        }
    }

    IAsyncEnumerable<TransformItem> IDataTransform.TransformAsync(TransformItem item, ITransformContext context, CancellationToken cancel)
    {
        return DataTransformExtensions.RunGroupAsync(this, item, context, cancel);
    }

    IAsyncEnumerable<TransformItem> IDataTransform.CompleteAsync(ITransformContext context, CancellationToken cancel)
    {
        return DataTransformExtensions.CompleteGroupAsync(this, context, cancel);
    }
}

/// <summary>How the members of an <see cref="IDataTransformGroup" /> work together.</summary>
public enum DataTransformGroupMode
{
    /// <summary>
    ///     A pipeline: the item goes to the first member, whatever it produces goes to the second, and so on. A member
    ///     that produces nothing ends that item's journey.
    /// </summary>
    Sequential,

    /// <summary>Every member gets the item; the group produces everything they produce, member by member.</summary>
    FanOut,

    /// <summary>
    ///     Every member gets the item for what it does -- move the file, send the mail, run the script -- and what they
    ///     produce is ignored; the group passes the item on once, unchanged. The mode of "run these actions on each new item".
    /// </summary>
    Actions
}

/// <summary>What a group does when one of its members throws.</summary>
public enum TransformErrorPolicy
{
    /// <summary>The exception propagates and ends the run.</summary>
    Throw,

    /// <summary>
    ///     The failure is reported and the item is dropped (in a chain, from that step on; in actions, no later action
    ///     runs for it and it is not passed on).
    /// </summary>
    SkipItem,

    /// <summary>
    ///     The failure is reported and the member is ignored: in a chain the item skips it, in a fan out it contributes
    ///     nothing, in actions the next action runs.
    /// </summary>
    Continue
}

/// <summary>What a transform needs from its caller, or does to the world.</summary>
[Flags]
public enum TransformFlags
{
    None = 0,

    /// <summary>
    ///     The same input always gives the same output and nothing else is touched, so the step can be previewed, cached
    ///     or reordered. Not combined with <see cref="SideEffects" />.
    /// </summary>
    Pure = 1,

    /// <summary>
    ///     The transform remembers things between items, kept in the context (
    ///     <see cref="ITransformContext.GetState{T}" />) rather than the transform.
    /// </summary>
    Stateful = 2,

    /// <summary>
    ///     The transform holds items back and produces them from <see cref="IDataTransform.CompleteAsync" />, so a run
    ///     has to finish for it to deliver (a sort, a grouping).
    /// </summary>
    Buffers = 4,

    /// <summary>The transform changes something outside the data (files, mail, processes); it is skipped in a dry run.</summary>
    SideEffects = 8
}

/// <summary>An item travelling through a pipeline.</summary>
/// <param name="Value">
///     The data: a row, a file entry, a feed entry, a document, an <c>IValue</c>... whatever the source
///     and the steps so far have made of it.
/// </param>
/// <param name="Type">What is known of its type (and so its shape); null if nothing is.</param>
/// <param name="Key">
///     The key of the source item it came from (<see cref="DataChange.Key" />), so a later step can say
///     which item it is acting on; it survives transforms that do not set their own.
/// </param>
public readonly record struct TransformItem(object? Value, IType? Type = null, string? Key = null)
{
    /// <summary>The item's shape, or <see cref="DataShape.Unknown" />.</summary>
    public DataShape Shape => Type?.Shape ?? DataShape.Unknown;

    /// <summary>The same item (same key) with a new value and, optionally, a new type (otherwise the current type is kept).</summary>
    public TransformItem With(object? value, IType? type = null)
    {
        return this with { Value = value, Type = type ?? Type };
    }
}

/// <summary>What a run offers the transforms in it.</summary>
public interface ITransformContext
{
    /// <summary>A rehearsal: transforms marked <see cref="TransformFlags.SideEffects" /> must not act.</summary>
    bool IsDryRun { get; }

    /// <summary>The current time, UTC.</summary>
    DateTime Now { get; }

    /// <summary>The source the items come from, if there is one.</summary>
    IDataSource? Source { get; }

    /// <summary>The state <paramref name="owner" /> keeps for this run, created on first use.</summary>
    T GetState<T>(IDataTransform owner, Func<T> create) where T : class;

    /// <summary>Called when a group moves past a member's failure (see <see cref="TransformErrorPolicy" />).</summary>
    void Report(IDataTransform transform, TransformItem item, Exception error);
}

/// <summary>The default <see cref="ITransformContext" />: state in memory for the length of one run, failures to the log.</summary>
public sealed class TransformContext : ITransformContext
{
    private readonly object                     _lock  = new();
    private readonly Dictionary<object, object> _state = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where the current time comes from; replace it to make a run repeatable.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public bool IsDryRun { get; init; }

    public DateTime Now => Clock();

    public IDataSource? Source { get; init; }

    public T GetState<T>(IDataTransform owner, Func<T> create) where T : class
    {
        lock (_lock)
        {
            if (_state.TryGetValue(owner, out var existing)) return (T)existing;

            var made = create();
            _state[owner] = made;
            return made;
        }
    }

    public void Report(IDataTransform transform, TransformItem item, Exception error)
    {
        Log.Warning(error, "Transform {transform} failed on item {key}", transform.DisplayName ?? transform.Operation, item.Key);
    }
}

/// <summary>
///     A plain, ready-to-use <see cref="IDataTransformGroup" />: a list of transforms, and so a natural XAML
///     collection.
/// </summary>
public class DataTransformGroup : List<IDataTransform>, IDataTransformGroup
{
    public DataTransformGroupMode Mode { get; set; } = DataTransformGroupMode.Sequential;

    public TransformErrorPolicy OnError     { get; set; } = TransformErrorPolicy.Throw;
    public string?              DisplayName { get; set; }
}

public static class DataTransformExtensions
{
    /// <summary>An empty sequence of items (what a transform produces when it has nothing to say).</summary>
    public static async IAsyncEnumerable<TransformItem> NoItems()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>The key a change's item travels under.</summary>
    public static TransformItem ToTransformItem(this DataChange change, IType? type = null)
    {
        return new TransformItem(change.Item, type, change.Key);
    }

    /// <summary>
    ///     Runs a transform over a stream of items, then lets it release anything it held back. What a consumer of an
    ///     <see cref="IObservableDataSource" /> does per batch, or over the whole watch.
    /// </summary>
    public static async IAsyncEnumerable<TransformItem> RunAsync(this IDataTransform transform, IAsyncEnumerable<TransformItem> items,
                                                                 ITransformContext context,
                                                                 [EnumeratorCancellation] CancellationToken cancel = default)
    {
        await foreach (var item in items.WithCancellation(cancel).ConfigureAwait(false))
            await foreach (var result in transform.TransformAsync(item, context, cancel).WithCancellation(cancel).ConfigureAwait(false))
                yield return result;

        await foreach (var result in transform.CompleteAsync(context, cancel).WithCancellation(cancel).ConfigureAwait(false))
            yield return result;
    }

    /// <summary>
    ///     The default <see cref="IDataTransform.TransformAsync" /> of a group; call it from a group that overrides the
    ///     rest.
    /// </summary>
    public static async IAsyncEnumerable<TransformItem> RunGroupAsync(IDataTransformGroup                        group, TransformItem item,
                                                                      ITransformContext                          context,
                                                                      [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var steps  = group.ToList();
        var policy = group.OnError;

        switch (group.Mode)
        {
            case DataTransformGroupMode.Sequential:
                await foreach (var result in Chain(steps, 0, item, policy, context, cancel)) yield return result;
                break;

            case DataTransformGroupMode.FanOut:
                foreach (var step in steps)
                {
                    var applied = await Apply(step, item, policy, context, cancel);
                    foreach (var result in applied.Items) yield return result;
                }

                break;

            default: // Actions
                foreach (var step in steps)
                {
                    var applied = await Apply(step, item, policy, context, cancel);
                    if (applied.Failed && policy == TransformErrorPolicy.SkipItem) yield break;
                }

                yield return item;
                break;
        }
    }

    /// <summary>
    ///     The default <see cref="IDataTransform.CompleteAsync" /> of a group: releases each member's held-back items, in
    ///     order, routing them through the members after it in a chain.
    /// </summary>
    public static async IAsyncEnumerable<TransformItem> CompleteGroupAsync(IDataTransformGroup group, ITransformContext context,
                                                                           [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var steps  = group.ToList();
        var policy = group.OnError;

        for (var i = 0; i < steps.Count; i++)
        {
            var released = await Release(steps[i], policy, context, cancel);

            switch (group.Mode)
            {
                case DataTransformGroupMode.Sequential:
                    foreach (var item in released)
                        await foreach (var result in Chain(steps, i + 1, item, policy, context, cancel))
                            yield return result;

                    break;
                case DataTransformGroupMode.FanOut:
                    foreach (var item in released) yield return item;

                    break;
                // Actions: what a member releases is not passed on
            }
        }
    }

    private static async IAsyncEnumerable<TransformItem> Chain(List<IDataTransform> steps, int index, TransformItem item,
                                                               TransformErrorPolicy policy, ITransformContext context,
                                                               [EnumeratorCancellation] CancellationToken cancel)
    {
        if (index == steps.Count)
        {
            yield return item;
            yield break;
        }

        var applied = await Apply(steps[index], item, policy, context, cancel);
        if (applied.Failed)
        {
            if (policy == TransformErrorPolicy.Continue)
                await foreach (var result in Chain(steps, index + 1, item, policy, context, cancel))
                    yield return result;

            yield break;
        }

        foreach (var produced in applied.Items)
            await foreach (var result in Chain(steps, index + 1, produced, policy, context, cancel))
                yield return result;
    }

    private static async Task<Applied> Apply(IDataTransform    step,    TransformItem     item, TransformErrorPolicy policy,
                                             ITransformContext context, CancellationToken cancel)
    {
        var items = new List<TransformItem>();
        try
        {
            await foreach (var result in step.TransformAsync(item, context, cancel).WithCancellation(cancel).ConfigureAwait(false))
                items.Add(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (policy != TransformErrorPolicy.Throw)
        {
            context.Report(step, item, ex);
            return new Applied(true, []);
        }

        return new Applied(false, items);
    }

    private static async Task<List<TransformItem>> Release(IDataTransform    step, TransformErrorPolicy policy, ITransformContext context,
                                                           CancellationToken cancel)
    {
        var items = new List<TransformItem>();
        try
        {
            await foreach (var result in step.CompleteAsync(context, cancel).WithCancellation(cancel).ConfigureAwait(false))
                items.Add(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (policy != TransformErrorPolicy.Throw)
        {
            context.Report(step, default, ex);
            items.Clear();
        }

        return items;
    }

    private readonly record struct Applied(bool Failed, List<TransformItem> Items);
}