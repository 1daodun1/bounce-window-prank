using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// 把别人的窗口从“最大化”压回中间大小：程序运行期间一直盯着，
// 谁被最大化就立刻还原成大约屏幕 70% 居中，等于这条规则下没人能最大化。
internal static class WindowGuard
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    struct RECT { public int Left, Top, Right, Bottom; }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    const int SW_RESTORE = 9;

    static Thread thread;
    static volatile bool alive;
    static int shrunk;

    public static int Shrunk { get { return shrunk; } }
    public static bool On { get { return alive; } }

    public static void Start()
    {
        alive = true;
        thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.Start();
    }

    static void Loop()
    {
        while (alive)
        {
            Sweep();
            for (int i = 0; i < 12 && alive; i++) Thread.Sleep(100);
        }
    }

    static void Sweep()
    {
        List<IntPtr> hits = new List<IntPtr>();
        uint own = (uint)Process.GetCurrentProcess().Id;
        IntPtr shell = GetShellWindow();

        EnumWindows(delegate(IntPtr hwnd, IntPtr param)
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (hwnd == shell) return true;
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == own) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
                if (!IsZoomed(hwnd)) return true;
                hits.Add(hwnd);
            }
            catch (Exception) { }
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr hwnd in hits)
        {
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                int w = (int)(sw * 0.70), h = (int)(sh * 0.70);
                int x = (sw - w) / 2, y = (sh - h) / 2;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                shrunk++;
            }
            catch (Exception) { }
        }
    }

    public static void Shutdown()
    {
        alive = false;
        if (thread != null) { thread.Join(600); thread = null; }
    }
}
