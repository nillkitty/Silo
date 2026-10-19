using Silo.Expressions;

namespace Silo.Hotkeys;

/// <summary>
///     The text of a shortcut's action, <c>verb template</c>: the verb says what to do (<see cref="ShortcutVerbs.Type" />, <see cref="ShortcutVerbs.Copy" />)
///     and the template (see <see cref="Silo.Expressions.Template" />) is the text to do it with.
/// </summary>
/// <param name="Verb">The verb in lower case.</param>
/// <param name="Template">The template that follows the verb.</param>
public sealed record ShortcutActionText(string Verb, Template Template)
{
    /// <summary>Reads an action.</summary>
    /// <param name="text">For example <c>type ${your:blue}</c>.</param>
    /// <param name="action">The action, when it reads.</param>
    /// <param name="problem">When false, what is wrong, in words for the person.</param>
    public static bool TryParse(string? text, out ShortcutActionText? action, out string? problem)
    {
        action  = null;
        problem = null;

        string t = (text ?? string.Empty).Trim();
        int    n = 0;
        while (n < t.Length && char.IsAsciiLetter(t[n])) n++;

        if (n == 0)
        {
            problem = "Start the action with what to do: " + string.Join(" or ", ShortcutVerbs.Known) + ".";
            return false;
        }

        string verb = t[..n].ToLowerInvariant();
        if (!ShortcutVerbs.Known.Contains(verb))
        {
            problem = $"\"{t[..n]}\" is not something a shortcut can do; use {string.Join(" or ", ShortcutVerbs.Known)}.";
            return false;
        }

        string rest = t[n..].TrimStart();
        if (rest.Length == 0)
        {
            problem = $"Say what to {verb}, for example {verb} ${{your:blue}}.";
            return false;
        }

        if (!Template.TryParse(rest, out var template, out var error))
        {
            problem = error!.Describe();
            return false;
        }

        action = new ShortcutActionText(verb, template!);
        return true;
    }
}

/// <summary>What happened when a shortcut's action ran.</summary>
/// <param name="Success">Whether it did what it says.</param>
/// <param name="Output">The text the template came to; null when it never got that far.</param>
/// <param name="Error">When it failed, why, in words for the person.</param>
public sealed record ShortcutRunResult(bool Success, string? Output, string? Error);

/// <summary>The things a shortcut can do, by verb. The verbs that touch the machine (typing, the clipboard) are supplied by the application.</summary>
public sealed class ShortcutVerbs
{
    /// <summary>The verb that types the text into whatever window has the focus.</summary>
    public const string Type = "type";

    /// <summary>The verb that puts the text on the clipboard.</summary>
    public const string Copy = "copy";

    /// <summary>The verbs an action may start with.</summary>
    public static IReadOnlyList<string> Known { get; } = [Type, Copy];

    private readonly Dictionary<string, Action<string>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets what a verb does with its text.</summary>
    public ShortcutVerbs Add(string verb, Action<string> handler)
    {
        _handlers[verb] = handler;
        return this;
    }

    /// <summary>Whether the verb has a handler.</summary>
    public bool Has(string verb) => _handlers.ContainsKey(verb);

    /// <summary>Works out the text of <paramref name="action" /> in <paramref name="scope" /> and hands it to the verb. Nothing is thrown: a failure is the result.</summary>
    public ShortcutRunResult Run(string? action, IVariableScope scope, EvaluationOptions? options = null)
    {
        if (!ShortcutActionText.TryParse(action, out var parsed, out string? problem)) return new ShortcutRunResult(false, null, problem);

        string text;
        try
        {
            text = parsed!.Template.Evaluate(scope, options ?? new EvaluationOptions());
        }
        catch (ExpressionException ex)
        {
            return new ShortcutRunResult(false, null, ex.Message);
        }

        if (!_handlers.TryGetValue(parsed.Verb, out var handler)) return new ShortcutRunResult(false, text, $"Nothing here knows how to {parsed.Verb}.");

        try
        {
            handler(text);
            return new ShortcutRunResult(true, text, null);
        }
        catch (Exception ex)
        {
            return new ShortcutRunResult(false, text, ex.Message);
        }
    }
}
