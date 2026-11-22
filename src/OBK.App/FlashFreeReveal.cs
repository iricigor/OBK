using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OBK.App;

// Windows shows a new window's blank surface before WPF presents its first frame. Rebuilt language
// windows stay cloaked until rendered, so switching does not flash that default white surface.
internal static class FlashFreeReveal
{
    private const int CloakAttribute = 13;

    public static Task Prepare(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetCloak(handle, true)) return Task.CompletedTask;
        var revealed = new TaskCompletionSource();
        var fallback = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        fallback.Tick += (_, _) => Reveal();
        fallback.Start();
        window.ContentRendered += async (_, _) =>
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            _ = DwmFlush();
            Reveal();
        };
        return revealed.Task;

        void Reveal()
        {
            fallback.Stop();
            if (revealed.Task.IsCompleted) return;
            SetCloak(handle, false);
            revealed.SetResult();
        }
    }

    private static bool SetCloak(IntPtr handle, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(handle, CloakAttribute, ref value, sizeof(int)) >= 0;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();
}
