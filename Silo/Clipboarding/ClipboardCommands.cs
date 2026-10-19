using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Silo.Clipboarding;

/// <summary>
///     Connects Edit ▸ Cut / Copy / Paste (and Ctrl+X / C / V, the toolbar buttons and the Explorer's right-click menu: they are all
///     the routed <see cref="ApplicationCommands" />) to <see cref="AppClipboard" />. The bindings sit on the main window, so they
///     only see a command the focused control did not handle itself: a text box or the table grid still copies its own text.
/// </summary>
public static class ClipboardCommands
{
    private enum Kind
    {
        Copy,
        Cut,
        Paste
    }

    /// <summary>Adds the bindings to <paramref name="target" /> (the main window), and makes Escape abandon a cut that is waiting.</summary>
    public static void Install(UIElement target)
    {
        target.CommandBindings.Add(Bind(ApplicationCommands.Copy, Kind.Copy));
        target.CommandBindings.Add(Bind(ApplicationCommands.Cut, Kind.Cut));
        target.CommandBindings.Add(Bind(ApplicationCommands.Paste, Kind.Paste));
        target.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
    }

    /// <summary>
    ///     Whether Ctrl+<paramref name="key" /> (C, X or V) means a clipboard operation at <paramref name="context" />. A control with
    ///     shortcuts of its own for the same keys (the Explorer's "Copy URI" is Ctrl+C) lets the clipboard have the key then.
    /// </summary>
    public static bool Claims(Key key, ClipboardContext context)
    {
        return key switch
               {
                   Key.C => AppClipboard.CanCopy(context),
                   Key.X => AppClipboard.CanCut(context),
                   Key.V => AppClipboard.CanPaste(context),
                   _     => false
               };
    }

    /// <summary>
    ///     The Cut, Copy and Paste entries a right-click menu offers at <paramref name="context" />, wired to the routed commands with
    ///     <paramref name="commandTarget" /> as the target, so they ask the same bindings the Edit menu does. Only what applies is listed.
    /// </summary>
    public static IEnumerable<MenuItem> MenuItems(IInputElement commandTarget, ClipboardContext context)
    {
        if (context.Selection.Count > 0 && context.Selection.All(s => s is ICuttable)) yield return Item("Cu_t", ApplicationCommands.Cut, commandTarget);
        if (context.Selection.Count > 0) yield return Item("_Copy", ApplicationCommands.Copy, commandTarget);
        if (context.Targets.Count > 0) yield return Item("_Paste", ApplicationCommands.Paste, commandTarget);
    }

    private static MenuItem Item(string header, RoutedCommand command, IInputElement target)
    {
        return new MenuItem { Header = header, Command = command, CommandTarget = target };
    }

    private static CommandBinding Bind(RoutedCommand command, Kind kind)
    {
        return new CommandBinding(command,
                                  (_, e) =>
                                  {
                                      var context = ContextOf(e);
                                      if (!Can(kind, context)) return;

                                      e.Handled = true;
                                      Run(kind, context);
                                  },
                                  (_, e) =>
                                  {
                                      // unhandled when it cannot: the command then simply stays disabled
                                      if (!Can(kind, ContextOf(e))) return;

                                      e.CanExecute = true;
                                      e.Handled    = true;
                                  });
    }

    private static ClipboardContext ContextOf(RoutedEventArgs e)
    {
        return ClipboardFocus.Resolve(e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject);
    }

    private static bool Can(Kind kind, ClipboardContext context)
    {
        return kind switch
               {
                   Kind.Copy => AppClipboard.CanCopy(context),
                   Kind.Cut  => AppClipboard.CanCut(context),
                   _         => AppClipboard.CanPaste(context)
               };
    }

    private static void Run(Kind kind, ClipboardContext context)
    {
        try
        {
            switch (kind)
            {
                case Kind.Copy:
                    AppClipboard.Copy(context);
                    break;
                case Kind.Cut:
                    AppClipboard.Cut(context);
                    break;
                default:
                    AppClipboard.Paste(context);
                    break;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "A clipboard command failed");
            App.Alert(ex, kind.ToString().ToLowerInvariant());
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape means "never mind" to a text box being edited as well: leave the cut for the next one
        if (e.Key != Key.Escape || !AppClipboard.Session.HasPendingCut) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;

        AppClipboard.CancelCut();
    }
}
