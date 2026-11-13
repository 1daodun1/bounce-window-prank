using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 跟着鼠标走的置顶弹窗：标题栏《朋友的酒》已入侵你的电脑，里面循环播放动态视频。
// 没有最小化/最大化/关闭按钮（WS_SYSMENU 被摘掉），只能靠程序的退出键收掉。
internal static class CursorFollow
{
    const int GWL_STYLE = -16;
    const int WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const string Caption = "《朋友的酒》已入侵你的电脑";
    const int W = 360, H = 268;       // 268 = 视频 240 + 标题栏 28

    static IntPtr hwnd = IntPtr.Zero;
    static Thread thread;
    static volatile bool alive;
    static string path;

    public static bool On { get { return hwnd != IntPtr.Zero; } }

    public static bool Start(string mp4)
    {
        path = mp4;
        thread = new Thread(Run);
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        for (int i = 0; i < 40 && !alive; i++) Thread.Sleep(100);
        return alive;
    }

    static void Run()
    {
        try
        {
            var view = new MediaElement();
            view.LoadedBehavior = MediaState.Manual;
            view.UnloadedBehavior = MediaState.Manual;
            view.IsMuted = true;
            view.Stretch = Stretch.UniformToFill;
            if (!string.IsNullOrEmpty(path)) view.Source = new Uri(path);
            view.MediaEnded += delegate { try { view.Position = TimeSpan.Zero; view.Play(); } catch (Exception) { } };

            var win = new Window();
            win.Title = Caption;
            win.Width = W;
            win.Height = H;
            win.WindowStyle = WindowStyle.SingleBorderWindow;
            win.ResizeMode = ResizeMode.NoResize;      // 去掉最小化/最大化
            win.Topmost = true;
            win.ShowInTaskbar = false;
            win.Content = view;

            POINT p;
            if (GetCursorPos(out p)) { win.Left = p.X + 16; win.Top = p.Y + 20; }
            win.Show();
            hwnd = new WindowInteropHelper(win).Handle;
            // 摘掉系统菜单，关闭按钮随之消失
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            view.Play();
            alive = true;
            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
        }
        catch (Exception) { alive = false; }
    }

    // 每帧贴到鼠标右下角，靠近屏幕边界自动内收
    public static void Drive()
    {
        if (hwnd == IntPtr.Zero) return;
        POINT p;
        if (!GetCursorPos(out p)) return;
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int x = p.X + 16, y = p.Y + 20;
        if (x + W > sw) x = sw - W;
        if (y + H > sh) y = sh - H;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, W, H, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public static void Shutdown()
    {
        alive = false;
        if (hwnd != IntPtr.Zero)
        {
            try { Dispatcher.FromThread(thread).InvokeShutdown(); } catch (Exception) { }
            hwnd = IntPtr.Zero;
        }
        if (thread != null) { thread.Join(1000); thread = null; }
    }
}
