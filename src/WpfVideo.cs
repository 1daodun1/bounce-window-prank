using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// 桌面背景视频：WPF MediaElement 解码，画面挂到一个子窗口，塞进资源管理器桌面视图的最底层。
// 声音仍走 reference-audio.wav（这里静音），画面位置每隔一会儿跟音频对一次，保证音画同步。
internal static class WpfVideo
{
    const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    const int SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, int w, int l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);

    static Thread thread;
    static IntPtr host;
    static MediaElement view;
    static Rectangle area;
    static IntPtr parent;
    static string path;
    static volatile bool alive;
    static double published = -1;
    static double duration = -1;

    public static volatile string Error = null;
    public static double PositionSeconds { get { return published; } }
    public static double DurationSeconds { get { return duration; } }
    public static bool Running { get { return alive; } }
    public static IntPtr HostHandle { get { return host; } }

    public static bool Start(IntPtr defView, Rectangle rect, string mp4)
    {
        parent = defView; area = rect; path = mp4;
        thread = new Thread(Run);
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        for (int i = 0; i < 50 && published < 0 && Error == null; i++) Thread.Sleep(100);
        return Error == null && published >= 0;
    }

    public static void Seek(double seconds)
    {
        MediaElement element = view;
        if (element == null) return;
        try
        {
            element.Dispatcher.BeginInvoke(new Action(delegate()
            {
                try { element.Position = TimeSpan.FromSeconds(seconds); element.Play(); } catch (Exception) { }
            }));
        }
        catch (Exception) { }
    }

    // 撕裂阶段把画面冻住：视频窗口还在 60fps 重绘的话，会把直接画到屏幕上的切条整片刷掉
    public static void Freeze()
    {
        MediaElement element = view;
        if (element == null) return;
        try
        {
            element.Dispatcher.BeginInvoke(new Action(delegate()
            {
                try { element.Pause(); } catch (Exception) { }
            }));
        }
        catch (Exception) { }
    }

    public static void Shutdown()
    {
        alive = false;
        MediaElement element = view;
        if (element != null)
        {
            try
            {
                // 必须在窗口线程上做：停播 -> 脱开父窗口 -> 销毁窗口 -> 关掉 Dispatcher。
                // 只解父窗口不够，窗口还活着的话壁纸就被它挡着，重刷也刷不出来。
                element.Dispatcher.Invoke(new Action(delegate()
                {
                    try { element.Stop(); element.Source = null; } catch (Exception) { }
                    if (host != IntPtr.Zero)
                    {
                        SetParent(host, IntPtr.Zero);
                        DestroyWindow(host);
                        host = IntPtr.Zero;
                    }
                }));
                element.Dispatcher.InvokeShutdown();
            }
            catch (Exception) { }
        }
        else if (host != IntPtr.Zero)
        {
            SetParent(host, IntPtr.Zero);
            DestroyWindow(host);
            host = IntPtr.Zero;
        }
        if (thread != null) { thread.Join(1500); thread = null; }
        view = null;
    }

    static void Run()
    {
        try
        {
            var parameters = new HwndSourceParameters("JellyWpfVideo")
            {
                WindowStyle = WS_POPUP,
                Width = area.Width,
                Height = area.Height,
                PositionX = -4000,
                PositionY = -4000
            };
            var source = new HwndSource(parameters);
            host = source.Handle;

            view = new MediaElement();
            view.LoadedBehavior = MediaState.Manual;
            view.UnloadedBehavior = MediaState.Manual;
            view.IsMuted = true;
            view.Stretch = Stretch.UniformToFill;
            view.Source = new Uri(path);
            view.MediaOpened += delegate
            {
                if (view.NaturalDuration != null && view.NaturalDuration.HasTimeSpan)
                    duration = view.NaturalDuration.TimeSpan.TotalSeconds;
            };
            view.MediaEnded += delegate
            {
                try { view.Position = TimeSpan.Zero; view.Play(); } catch (Exception) { }
            };
            view.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
            {
                Error = "视频解码失败：" + (e.ErrorException == null ? "未知" : e.ErrorException.Message);
            };
            source.RootVisual = view;

            if (parent != IntPtr.Zero)
            {
                SetParent(host, parent);
                SetWindowLong(host, -16, GetWindowLong(host, -16) | WS_CHILD | WS_VISIBLE);
                // 挂到壁纸层里之后要压到该层最上面，否则会被别的壁纸窗口盖住
                SetWindowPos(host, IntPtr.Zero, 0, 0, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else
            {
                SetWindowPos(host, new IntPtr(-1), 0, 0, area.Width, area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            view.Play();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += delegate
            {
                try
                {
                    published = view.Position.TotalSeconds;
                    if (duration > 0 && published > duration - 0.25) { view.Position = TimeSpan.Zero; view.Play(); }
                }
                catch (Exception) { }
            };
            timer.Start();
            alive = true;
            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
        }
        catch (Exception error)
        {
            Error = error.GetType().Name + ": " + error.Message;
        }
    }
}
