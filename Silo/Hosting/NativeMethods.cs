using System.Runtime.InteropServices;

namespace Silo.Hosting;

internal static class NativeMethods
{
    /// <summary>Lets any process take the foreground (the stock instance must be able to come to the front when asked).</summary>
    public const int AsfwAny = -1;

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);
}
