using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;
using Silo.Contracts.Core;
using Silo.Contracts.Providers.Data;
using Silo.Expressions;
using Silo.Storage;

namespace Silo.Hotkeys;

/// <summary>
///     Makes the shortcut keys of the active Silo (<see cref="GlobalShortcutPrefs" />) work: the global ones as Windows hot keys, which work
///     in every application, and the application ones as keys pressed in any of Silo's windows. It also supplies what the action verbs do (type into the
///     focused window, copy to the clipboard) and is where the rest of the app finds the Silo's stored values (<see cref="Stored.DefaultStore" />).
/// </summary>
/// <remarks>
///     Registering a hot key can fail (another program has it); the reason is kept in <see cref="Failures" /> and shown where bindings are managed.
///     The service reloads when the active Silo changes and when a binding is saved, changed or removed.
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly Dictionary<int, string>           _idToName      = [];
    private readonly Dictionary<ShortcutKey, string>   _appBindings   = [];
    private readonly Dictionary<string, string>        _failures      = new(StringComparer.OrdinalIgnoreCase);
    private readonly object                            _typingLock    = new();
    private Task                                       _typing        = Task.CompletedTask;
    private HwndSource?                                _window;
    private Dispatcher?                                _dispatcher;
    private bool                                       _started;
    private bool                                       _reloadQueued;
    private int                                        _suspended;
    private bool                                       _classHandlerAdded;

    private GlobalHotkeyService()
    {
        // what the verbs do
        Verbs = new ShortcutVerbs().Add(ShortcutVerbs.Type, TypeText).Add(ShortcutVerbs.Copy, CopyText);
        Prefs = new GlobalShortcutPrefs(() => Stored.DefaultStore(), Verbs, () => YourConstants.Default.AsScope());

        // where the Silo's stored values are
        Stored.DefaultStore = static () => App.Instance?.ActiveSilo is { IsClosing: false } silo ? silo.Data as IStoredStore : null;
    }

    /// <summary>The service (there is one per application).</summary>
    public static GlobalHotkeyService Current { get; } = new();

    /// <summary>The shortcut keys of the active Silo.</summary>
    public GlobalShortcutPrefs Prefs { get; }

    /// <summary>What the action verbs do.</summary>
    public ShortcutVerbs Verbs { get; }

    /// <summary>The shortcuts that could not be registered, by name, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>The last problem typing text into another window (nothing is thrown there: the typing runs behind the key press); null if it went well.</summary>
    public string? LastTypingError { get; private set; }

    /// <summary>Raised after the shortcuts were registered again, or a problem came up while one ran.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Starts watching the Silo and registers the shortcuts. Does nothing when it already started. Call it on the UI thread.</summary>
    public void Start()
    {
        if (_started) return;
        _started    = true;
        _dispatcher = Dispatcher.CurrentDispatcher;

        try
        {
            var parameters = new HwndSourceParameters("SiloHotkeys") { ParentWindow = new IntPtr(NativeKeyboard.HWND_MESSAGE), WindowStyle = 0 };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Global shortcut keys are not available: the message window could not be created");
        }

        if (!_classHandlerAdded)
        {
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnAnyWindowKeyDown));
            _classHandlerAdded = true;
        }

        if (App.Instance is { } app) app.ActiveSiloChanged += OnSourceChanged;
        Prefs.Changed += OnSourceChanged;
        Reload();
    }

    /// <summary>Unregisters the shortcuts and stops watching.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;

        if (App.Instance is { } app) app.ActiveSiloChanged -= OnSourceChanged;
        Prefs.Changed -= OnSourceChanged;
        UnregisterAll();
        _appBindings.Clear();
        _window?.Dispose();
        _window = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    ///     Stops the shortcuts from acting until the returned object is disposed: while a key is being chosen for a new shortcut, pressing the key of
    ///     an existing one must reach the box that is choosing, not run that shortcut.
    /// </summary>
    public IDisposable Suspend()
    {
        _suspended++;
        UnregisterAll();
        return new Resume(this);
    }

    /// <summary>Runs a shortcut now (the Run button where shortcuts are managed).</summary>
    public ShortcutRunResult RunNow(IGlobalShortcutPref pref)
    {
        var result = pref.Run();
        if (!result.Success) Report();
        return result;
    }

    // ---- registering ---------------------------------------------------------------------------------------------

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        // may come from any thread; coalesce a burst of changes into one reload
        if (_reloadQueued) return;
        _reloadQueued = true;
        (_dispatcher ?? Application.Current?.Dispatcher)?.BeginInvoke(() =>
                                                                      {
                                                                          _reloadQueued = false;
                                                                          if (_started) Reload();
                                                                      }, DispatcherPriority.Background);
    }

    /// <summary>Registers the shortcuts of the active Silo again.</summary>
    public void Reload()
    {
        UnregisterAll();
        _appBindings.Clear();
        _failures.Clear();

        if (_suspended == 0)
        {
            IReadOnlyList<IGlobalShortcutPref> all;
            try
            {
                all = Prefs.Shortcuts;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The shortcut keys could not be read");
                all = [];
            }

            int id = 0;
            foreach (var pref in all)
            {
                if (!pref.Enabled || !pref.IsBound) continue;

                if (pref.Scope == ShortcutScope.Application)
                {
                    _appBindings[pref.Shortcut] = pref.Name;
                    continue;
                }

                if (!pref.Shortcut.IsValidGlobal(out string? problem))
                {
                    _failures[pref.Name] = problem!;
                    continue;
                }

                if (_window is null)
                {
                    _failures[pref.Name] = "Global shortcut keys are not available in this session.";
                    continue;
                }

                id++;
                uint mods = NativeKeyboard.ToHotKeyModifiers(pref.Shortcut.Modifiers) | NativeKeyboard.MOD_NOREPEAT;
                if (NativeKeyboard.RegisterHotKey(_window.Handle, id, mods, (uint)pref.Shortcut.VirtualKey))
                    _idToName[id] = pref.Name;
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    _failures[pref.Name] = error == NativeKeyboard.ERROR_HOTKEY_ALREADY_REGISTERED
                                               ? $"{pref.Shortcut} is already used by Windows or another program."
                                               : $"{pref.Shortcut} could not be registered: {new Win32Exception(error).Message}";
                }
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        if (_window is not null)
            foreach (int id in _idToName.Keys) NativeKeyboard.UnregisterHotKey(_window.Handle, id);
        _idToName.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeKeyboard.WM_HOTKEY) return IntPtr.Zero;

        handled = true;
        if (_idToName.TryGetValue(wParam.ToInt32(), out string? name))
            (_dispatcher ?? Application.Current.Dispatcher).BeginInvoke(() => RunByName(name), DispatcherPriority.Input);
        return IntPtr.Zero;
    }

    private void RunByName(string name)
    {
        var result = Prefs.Find(name)?.Run();
        if (result is { Success: false })
        {
            Log.Information("Shortcut {Name} failed: {Error}", name, result.Error);
            Report();
        }
    }

    private void Report() => StatusChanged?.Invoke(this, EventArgs.Empty);

    // ---- keys pressed in Silo's own windows ------------------------------------------------------------------------

    private void OnAnyWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_suspended > 0 || _appBindings.Count == 0 || e.Handled) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;

        var mods = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ShortcutModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= ShortcutModifiers.Windows;

        var pressed = new ShortcutKey(mods, KeyInterop.VirtualKeyFromKey(key));
        if (!_appBindings.TryGetValue(pressed, out string? name)) return;

        // plain typing in a text box is for the text box
        bool strong = (mods & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Windows)) != 0;
        if (!strong && Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return;

        e.Handled = true;
        RunByName(name);
    }

    // ---- the verbs -----------------------------------------------------------------------------------------------

    private void TypeText(string text)
    {
        // typing waits for the hot key to be let go of, so it runs behind the key press, one text after the other
        lock (_typingLock)
            _typing = _typing.ContinueWith(_ =>
                                           {
                                               try
                                               {
                                                   NativeKeyboard.Type(text);
                                                   LastTypingError = null;
                                               }
                                               catch (Exception ex)
                                               {
                                                   LastTypingError = ex.Message;
                                                   Log.Information("Typing a shortcut's text failed: {Error}", ex.Message);
                                                   Report();
                                               }
                                           }, TaskScheduler.Default);
    }

    private static void CopyText(string text)
    {
        var dispatcher = Application.Current.Dispatcher;
        dispatcher.Invoke(() =>
                          {
                              for (int attempt = 0; ; attempt++)
                              {
                                  try
                                  {
                                      Clipboard.SetText(text);
                                      return;
                                  }
                                  catch (ExternalException) when (attempt < 5)
                                  {
                                      Thread.Sleep(20); // another program has the clipboard open for a moment
                                  }
                                  catch (ExternalException)
                                  {
                                      throw new InvalidOperationException("The clipboard is in use by another program.");
                                  }
                              }
                          });
    }

    private sealed class Resume(GlobalHotkeyService owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._suspended--;
            if (owner._suspended == 0 && owner._started) owner.Reload();
        }
    }
}
