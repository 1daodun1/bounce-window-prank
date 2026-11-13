using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// 桌面视频改成两个置顶小窗：只在程序启动时建一次，各自循环播放 mp4。
// 位置由 BPM 驱动：每两拍换一个随机落点，途中线性滑过去，落点永远夹在屏幕内（留边距）。
internal static class VideoWindows
{
    const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    class Mini
    {
        public IntPtr Host;
        public MediaElement View;
        public int X, Y, W, H;          // 当前位置
        public int FromX, FromY, ToX, ToY;
        public int Slot = int.MinValue;
        public double Duration;
    }

    static readonly List<Mini> minis = new List<Mini>();
    static Thread thread;
    static volatile bool alive;
    static string path;
    static int count, winW, winH;
    static Random rng = new Random(20131108);

    public static bool Running { get { return alive && minis.Count > 0; } }
    public static int Count { get { return minis.Count; } }

    public static bool Start(string mp4, int n, int w, int h)
    {
        path = mp4; count = n; winW = w; winH = h;
        thread = new Thread(Run);
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        for (int i = 0; i < 60 && !alive; i++) Thread.Sleep(100);
        return alive && minis.Count > 0;
    }

    static void Run()
    {
        try
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            for (int i = 0; i < count; i++)
            {
                var p = new HwndSourceParameters("JellyVideoMini" + i)
                {
                    WindowStyle = WS_POPUP,
                    Width = winW,
                    Height = winH,
                    PositionX = (sw - winW) / 2 + i * 40,
                    PositionY = (sh - winH) / 2 + i * 40
                };
                var src = new HwndSource(p);
                IntPtr host = src.Handle;

                var view = new MediaElement();
                view.LoadedBehavior = MediaState.Manual;
                view.UnloadedBehavior = MediaState.Manual;
                view.IsMuted = true;
                view.Stretch = Stretch.UniformToFill;
                view.Source = new Uri(path);
                view.MediaEnded += delegate { try { view.Position = TimeSpan.Zero; view.Play(); } catch (Exception) { } };
                src.RootVisual = view;

                var m = new Mini { Host = host, View = view, W = winW, H = winH };
                minis.Add(m);

                SetWindowPos(host, new IntPtr(-1), (sw - winW) / 2, (sh - winH) / 2 + i * 60, winW, winH,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                // 置顶 + 不进 Alt+Tab
                SetWindowLong(host, -20, GetWindowLong(host, -20) | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                view.Play();
            }
            alive = true;
            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
        }
        catch (Exception) { }
    }

    // 每两拍（slot = position / cycle2）换一次落点，途中平滑滑动，坐标永远夹在屏幕里
    public static void Drive(double position, double cycle2)
    {
        if (!alive || minis.Count == 0 || cycle2 <= 0) return;
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int margin = 16;

        int slot = (int)Math.Floor(position / cycle2);
        double t = (position - slot * cycle2) / cycle2;      // 0..1
        if (t < 0) t = 0; if (t > 1) t = 1;
        // 先快后慢，看得清它是“弹”过去的
        double e = 1 - Math.Pow(1 - t, 3);

        for (int i = 0; i < minis.Count; i++)
        {
            Mini m = minis[i];
            if (m.Slot != slot)
            {
                m.Slot = slot;
                m.FromX = m.X; m.FromY = m.Y;
                int maxX = Math.Max(margin, sw - m.W - margin);
                int maxY = Math.Max(margin, sh - m.H - margin);
                m.ToX = rng.Next(margin, maxX + 1);
                m.ToY = rng.Next(margin, maxY + 1);
            }
            int x = m.FromX + (int)Math.Round((m.ToX - m.FromX) * e);
            int y = m.FromY + (int)Math.Round((m.ToY - m.FromY) * e);
            // 双保险：任何时候都不许出屏
            if (x < margin) x = margin;
            if (y < margin) y = margin;
            if (x > sw - m.W - margin) x = sw - m.W - margin;
            if (y > sh - m.H - margin) y = sh - m.H - margin;
            if (x == m.X && y == m.Y) continue;
            m.X = x; m.Y = y;
            SetWindowPos(m.Host, new IntPtr(-1), x, y, m.W, m.H, SWP_NOACTIVATE | SWP_NOZORDER);
        }
    }

    public static void Shutdown()
    {
        alive = false;
        foreach (Mini m in minis)
        {
            try
            {
                MediaElement v = m.View;
                if (v != null) v.Dispatcher.BeginInvoke(new Action(delegate()
                {
                    try { v.Stop(); v.Source = null; } catch (Exception) { }
                }));
            }
            catch (Exception) { }
        }
        if (thread != null) { thread.Join(1200); thread = null; }
        minis.Clear();
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
