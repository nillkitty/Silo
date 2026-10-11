using Serilog;
using Silo.Contracts.Providers.Data;

namespace Silo.Settings;

/// <summary>
///     Applies the author-mode <see cref="SiloFileSettings.ConflictPolicy" /> to a <see cref="SaveConflict" />: acts on a
///     stored policy, otherwise asks the <see cref="ISaveConflictResolver" /> and, when the person ticks "Don't ask me
///     again", stores the answer as the new policy.
/// </summary>
public static class SaveConflictPolicy
{
    public static async Task<SaveConflictChoice> DecideAsync(ISaveConflictResolver? resolver, SaveConflict conflict)
    {
        var settings = SiloFileSettings.Instance;
        switch (settings.ConflictPolicy)
        {
            case ExternalChangePolicy.Overwrite:
                Log.Information("{Kind} at {Location}: overwriting, per the conflict setting", conflict.Kind, conflict.Location);
                return SaveConflictChoice.Overwrite;

            case ExternalChangePolicy.SaveCopy:
                Log.Information("{Kind} at {Location}: saving a conflicted copy, per the conflict setting", conflict.Kind, conflict.Location);
                return SaveConflictChoice.SaveCopy;
        }

        if (resolver is null)
        {
            // Nobody to ask (headless/tests): the safe answer is the one that destroys nothing.
            Log.Warning("{Kind} at {Location}: no resolver available; saving a conflicted copy", conflict.Kind, conflict.Location);
            return SaveConflictChoice.SaveCopy;
        }

        var answer = await resolver.ResolveAsync(conflict);
        if (answer.Remember && answer.Choice != SaveConflictChoice.Cancel)
            settings.ConflictPolicy = answer.Choice == SaveConflictChoice.Overwrite ? ExternalChangePolicy.Overwrite : ExternalChangePolicy.SaveCopy;

        return answer.Choice;
    }
}
