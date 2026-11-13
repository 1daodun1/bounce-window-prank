using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 随机时刻弹出的图标窗：标题栏「你的电脑被朋友的酒入侵了」，中间只有那张程序图标，
// 没有关闭/最小化/最大化按钮（WS_SYSMENU 摘掉后再刷一次窗口框架）。
// 现在支持同时存在多个，每个都落在屏幕随机位置，位置互相避开。
internal static class IconWindow
{
    const int GWL_STYLE = -16;
    const int WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr inst, string file, int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    const string Caption = "你的电脑被朋友的酒入侵了";
    const int W = 240, H = 260;          // 比早先的 300×320 缩了一圈，不挡屏幕

    static readonly object gate = new object();
    static readonly List<IntPtr> hwnds = new List<IntPtr>();
    static readonly List<Dispatcher> dispatchers = new List<Dispatcher>();
    static readonly List<Rect> placed = new List<Rect>();
    static readonly Random rnd = new Random();
    static string iconFile;
    static int created;

    public static int Count { get { lock (gate) { return hwnds.Count; } } }

    // 屏幕尺寸只取一次
    static int ScreenW { get { return GetSystemMetrics(0); } }
    static int ScreenH { get { return GetSystemMetrics(1); } }

    static Rect PickSpot()
    {
        int sw = ScreenW, sh = ScreenH;
        int left = 0, top = 0;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            left = 30 + rnd.Next(0, Math.Max(1, sw - W - 60));
            top = 30 + rnd.Next(0, Math.Max(1, sh - H - 120));
            bool clash = false;
            foreach (Rect r in placed)
            {
                if (Math.Abs(r.Left - left) < W + 24 && Math.Abs(r.Top - top) < H + 24) { clash = true; break; }
            }
            if (!clash) break;
        }
        Rect spot = new Rect(left, top, W, H);
        placed.Add(spot);
        return spot;
    }

    public static bool Start(string ico)
    {
        iconFile = ico;
        Rect spot = PickSpot();
        int before = Volatile.Read(ref created);
        Thread thread = new Thread(delegate() { Run(spot); });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        for (int i = 0; i < 40 && Volatile.Read(ref created) <= before; i++) Thread.Sleep(50);
        return Volatile.Read(ref created) > before;
    }

    static BitmapSource LoadIcon(string file)
    {
        try
        {
            if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
            {
                var dec = new IconBitmapDecoder(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = null;
                foreach (BitmapFrame f in dec.Frames)
                    if (best == null || f.PixelWidth > best.PixelWidth) best = f;
                if (best != null) return best;
            }
        }
        catch (Exception) { }

        try
        {
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            IntPtr h = ExtractIcon(IntPtr.Zero, exe, 0);
            if (h != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(h);
                return src;
            }
        }
        catch (Exception) { }
        return null;
    }

    static bool Run(Rect spot)
    {
        try
        {
            var img = new Image();
            img.Stretch = Stretch.Uniform;
            img.Margin = new Thickness(8);
            img.Source = LoadIcon(iconFile);

            var win = new Window();
            win.Title = Caption;
            win.Width = spot.Width;
            win.Height = spot.Height;
            win.Left = spot.Left;
            win.Top = spot.Top;
            win.WindowStyle = WindowStyle.SingleBorderWindow;
            win.ResizeMode = ResizeMode.NoResize;
            win.Topmost = true;
            win.ShowInTaskbar = false;
            win.Content = img;
            win.Show();

            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            lock (gate) { hwnds.Add(hwnd); dispatchers.Add(Dispatcher.CurrentDispatcher); }
            Interlocked.Increment(ref created);

            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
            return true;
        }
        catch (Exception) { return false; }
    }

    public static void Shutdown()
    {
        List<IntPtr> closed;
        List<Dispatcher> ds;
        lock (gate)
        {
            closed = new List<IntPtr>(hwnds);
            ds = new List<Dispatcher>(dispatchers);
            hwnds.Clear();
            dispatchers.Clear();
            placed.Clear();
        }
        foreach (IntPtr h in closed) { try { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); } catch (Exception) { } }
        foreach (Dispatcher d in ds) { try { d.InvokeShutdown(); } catch (Exception) { } }
        Thread.Sleep(120);
    }
}
