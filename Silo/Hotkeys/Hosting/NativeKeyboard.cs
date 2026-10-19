using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Silo.Hotkeys;

/// <summary>The Windows calls behind global hot keys and typing text into another application.</summary>
internal static class NativeKeyboard
{
    public const int  WM_HOTKEY       = 0x0312;
    public const int  HWND_MESSAGE    = -3;
    public const uint MOD_NOREPEAT    = 0x4000;
    public const int  ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private const uint INPUT_KEYBOARD     = 1;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const uint KEYEVENTF_UNICODE  = 0x0004;

    private const ushort VK_TAB     = 0x09;
    private const ushort VK_RETURN  = 0x0D;
    private const ushort VK_SHIFT   = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU    = 0x12;
    private const ushort VK_LWIN    = 0x5B;
    private const ushort VK_RWIN    = 0x5C;
    private const ushort VK_NONE    = 0xE8; // unassigned; pressing it between Win down and up keeps the Start menu from opening

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint       type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx, dy;
        public uint   mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint   dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>The Windows hot key modifier flags for a shortcut's modifiers (they are the same numbers).</summary>
    public static uint ToHotKeyModifiers(ShortcutModifiers modifiers) => (uint)modifiers;

    /// <summary>Types the text into the window that has the focus, as if typed: characters as Unicode input (so any character works whatever the keyboard layout), line breaks as Enter, tabs as Tab.</summary>
    /// <exception cref="Win32Exception">Windows refused the input (for example the focused program runs as administrator and Silo does not).</exception>
    public static void Type(string text)
    {
        if (text.Length == 0) return;

        ReleaseModifiers();

        var inputs = new List<INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue; // the \n that follows is the line break
                AddKey(inputs, VK_RETURN);
            }
            else if (c == '\n') AddKey(inputs, VK_RETURN);
            else if (c == '\t') AddKey(inputs, VK_TAB);
            else
            {
                inputs.Add(Unicode(c, up: false));
                inputs.Add(Unicode(c, up: true));
            }
        }

        const int batch = 200;
        for (int at = 0; at < inputs.Count; at += batch)
            Send(inputs.GetRange(at, Math.Min(batch, inputs.Count - at)).ToArray());
    }

    /// <summary>
    ///     The hot key that started the typing is usually still held down. Typing while Ctrl or Win is down would make shortcuts of the characters, so
    ///     it waits for the keys to come up (they normally do within a moment) and lets them go itself if they do not.
    /// </summary>
    private static void ReleaseModifiers()
    {
        ushort[] modifiers = [VK_LWIN, VK_RWIN, VK_CONTROL, VK_MENU, VK_SHIFT];

        for (int waited = 0; waited < 400 && modifiers.Any(Held); waited += 10) Thread.Sleep(10);

        var held = modifiers.Where(Held).ToList();
        if (held.Count == 0) return;

        var ups = new List<INPUT>();
        if (held.Contains(VK_LWIN) || held.Contains(VK_RWIN)) AddKey(ups, VK_NONE); // so letting go of Win does not open the Start menu
        foreach (ushort vk in held) ups.Add(Key(vk, up: true));
        Send(ups.ToArray());
    }

    private static bool Held(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void AddKey(List<INPUT> list, ushort vk)
    {
        list.Add(Key(vk, up: false));
        list.Add(Key(vk, up: true));
    }

    private static INPUT Key(ushort vk, bool up) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };

    private static INPUT Unicode(char c, bool up) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } } };

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return;

        int error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, error == 5
                                            ? "Windows did not let Silo type into that window (a program that runs as administrator cannot be typed into from Silo unless Silo does too)."
                                            : "Windows did not accept the typed text.");
    }
}
