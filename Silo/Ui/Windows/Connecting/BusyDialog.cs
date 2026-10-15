using System.Windows.Controls;

namespace Silo.Ui.Windows.Connecting;

/// <summary>
///     Runs slow work without freezing the app, the way the management console does: nothing is shown if the work finishes
///     within a second; after that a small progress window (an indeterminate bar and a Cancel button) appears and the
///     window it belongs to is disabled until the work is done or cancelled.
/// </summary>
public static class BusyDialog
{
    /// <summary>How long the work may take before the progress window shows.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Starts <paramref name="work" /> (on the calling thread, up to its first await) and returns its result. Throws
    ///     <see cref="OperationCanceledException" /> when Cancel is pressed: the wait ends at once even if the work itself
    ///     ignores the token (it is left to finish in the background and its outcome is ignored).
    /// </summary>
    public static async Task<T> RunAsync<T>(Window? owner, string title, string message, Func<CancellationToken, Task<T>> work, TimeSpan? delay = null)
    {
        using var source    = new CancellationTokenSource();
        var       cancelled = new TaskCompletionSource();
        using var registration = source.Token.Register(() => cancelled.TrySetResult());

        var     task   = work(source.Token);
        Window? dialog = null;
        bool    ownerWasEnabled = owner?.IsEnabled ?? false;
        try
        {
            if (!task.IsCompleted && await Task.WhenAny(task, Task.Delay(delay ?? DefaultDelay)) != task)
            {
                dialog = Build(owner, title, message, source);
                if (owner is not null) owner.IsEnabled = false;
                dialog.Show();
            }

            if (await Task.WhenAny(task, cancelled.Task) != task)
            {
                // abandoned: nobody will look at its result, so make sure a failure is not reported as unobserved
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new OperationCanceledException(source.Token);
            }

            return await task;
        }
        finally
        {
            if (dialog is not null)
            {
                dialog.Tag = "done"; // closing it from here is not a Cancel
                dialog.Close();
                if (owner is not null)
                {
                    owner.IsEnabled = ownerWasEnabled;
                    owner.Activate();
                }
            }
        }
    }

    private static Window Build(Window? owner, string title, string message, CancellationTokenSource source)
    {
        var text   = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 12) };
        var bar    = new ProgressBar { IsIndeterminate = true, Height = 16, MinWidth = 360, Margin = new Thickness(0, 0, 0, 14) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 80, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Right };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        panel.Children.Add(cancel);

        var window = new Window
                     {
                         Title                 = title,
                         Content               = panel,
                         SizeToContent         = SizeToContent.WidthAndHeight,
                         ResizeMode            = ResizeMode.NoResize,
                         ShowInTaskbar         = false,
                         Owner                 = owner,
                         WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
                     };

        void Cancel()
        {
            cancel.IsEnabled = false;
            text.Text        = "Cancelling...";
            source.Cancel();
        }

        // not a modal window (ShowDialog would block the code that has to close it), so Cancel is a plain button
        cancel.Click += (_, _) => Cancel();
        window.Closing += (_, e) =>
                          {
                              if (window.Tag is "done") return;

                              e.Cancel = true; // the close button asks to cancel; the work's end closes the window
                              Cancel();
                          };
        return window;
    }
}
