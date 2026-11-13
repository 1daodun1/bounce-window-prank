using System;
using System.Runtime.InteropServices;
using System.Text;

// 找桌面图标层下面那层 WorkerW——动态壁纸程序都挂在这里。
// 注意：系统里有一堆 136×39 的隐藏 WorkerW（属于任务栏之类），必须按"可见 + 铺满屏幕 + 没有 DefView 子窗口"三条一起筛。
internal static class DesktopHost
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    static IntPtr found = IntPtr.Zero;
    static int screenWidth = 1680;
    static int screenHeight = 1050;

    public static void SetScreenSize(int width, int height)
    {
        screenWidth = width;
        screenHeight = height;
    }

    // 任务栏：Win11 的任务栏按钮不是独立窗口（XAML 合成），只能整条弹。
    // 做法是把 Shell_TrayWnd 按拍的相位往上顶，同时把高度补回去，底边贴着屏幕，看着像整条在呼吸。
    static IntPtr tray = IntPtr.Zero;
    static RECT trayBase;
    static bool traySaved;

    public static void BounceTaskbar(double phase, int amplitude)
    {
        if (tray == IntPtr.Zero)
        {
            tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            GetWindowRect(tray, out trayBase);
            traySaved = true;
        }
        if (!traySaved) return;
        if (!IsWindow(tray)) { tray = IntPtr.Zero; traySaved = false; return; }

        double wave = (1 - Math.Cos(phase * 2 * Math.PI)) * 0.5;
        int dy = (int)Math.Round(amplitude * wave);
        int top = trayBase.Top - dy;
        SetWindowPos(tray, IntPtr.Zero, trayBase.Left, top, trayBase.Right - trayBase.Left, trayBase.Bottom - top,
            0x0004 | 0x0010 | 0x0200 | 0x0400);
    }

    public static void RestoreTaskbar()
    {
        if (!traySaved || tray == IntPtr.Zero) return;
        SetWindowPos(tray, IntPtr.Zero, trayBase.Left, trayBase.Top, trayBase.Right - trayBase.Left, trayBase.Bottom - trayBase.Top,
            0x0004 | 0x0010 | 0x0200 | 0x0400);
        traySaved = false;
        tray = IntPtr.Zero;
    }

    // 视频窗口撤掉之后，桌面那块像素还留着最后一帧。
    // 做法：让 Progman / DefView 失效，然后按注册表里的壁纸路径重新套用一次，
    // 资源管理器会自己把整块桌面重铺回去（要等一两秒，属于正常）。
    public static void RepaintDesktop()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            InvalidateRect(progman, IntPtr.Zero, true);
            RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        }

        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            InvalidateRect(defView, IntPtr.Zero, true);
            RedrawWindow(defView, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        }

        string wallpaper = null;
        using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
        {
            if (key != null)
            {
                object value = key.GetValue("WallPaper");
                if (value != null) wallpaper = value.ToString();
            }
        }

        if (!string.IsNullOrEmpty(wallpaper) && System.IO.File.Exists(wallpaper))
            SystemParametersInfo(0x0014, 0, wallpaper, 0x01 | 0x02);
    }

    public static IntPtr FindWorkerW()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            // 0x052C 让资源管理器把壁纸层拆出一个 WorkerW
            SendMessage(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1));
        }
        found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr param)
        {
            StringBuilder name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "WorkerW") return true;
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return true;
            if (!IsWindowVisible(hwnd)) return true;

            RECT rect = new RECT();
            GetWindowRect(hwnd, out rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < screenWidth || height < screenHeight) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
