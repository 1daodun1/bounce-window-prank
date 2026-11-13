using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 融合版：无界面，双击即开始。
//  - 桌面图标：分层窗口叠加（复用 JellyLayer）
//  - 所有可见窗口：每帧 SetWindowPos 强制改位置和尺寸
//  - 两者相位都由音频的采样位置按 BPM 推算，不靠计时器
internal static class JellyAll
{
    const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOREDRAW = 0x0008,
               SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_NOSENDCHANGING = 0x0400,
               SWP_DEFERERASE = 0x2000;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
    const int WM_HOTKEY = 0x0312;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;
    static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public int time; public int ptX, ptY; }

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int PeekMessage(out MSG msg, IntPtr hwnd, int min, int max, int remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    sealed class Target
    {
        public IntPtr Hwnd;
        public RECT Base;
        public string Title;

        // 每两拍换一次的随机漂移量：只挪几十像素，并且整窗必须留在屏幕内
        public int Slot = int.MinValue;
        public int FromDX, FromDY, DriftX, DriftY;
        public double DriftFrom;
    }

    // 与桌面图标完全同一套：竖轴随 wave 伸缩，横轴反向 0.72 倍（面积近似守恒）。
    static void Scale(double phase, double amount, out double sx, out double sy)
    {
        double wave = Math.Cos(phase * Math.PI * 2);
        sy = 1 + amount * wave;
        sx = 1 - amount * 0.72 * wave;
    }

    static readonly Random random = new Random(20131108);

    static string ClassOf(IntPtr hwnd)
    {
        StringBuilder text = new StringBuilder(256);
        GetClassName(hwnd, text, 256);
        return text.ToString();
    }

    static void CollectWindows(uint ownPid, List<Target> into)
    {
        List<Target> found = new List<Target>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr param)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd)) return true;
            string cls = ClassOf(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid) return true;
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return true;
            if (rect.Right - rect.Left < 160 || rect.Bottom - rect.Top < 90) return true;
            StringBuilder title = new StringBuilder(256);
            GetWindowText(hwnd, title, 256);
            if (title.Length == 0) return true;
            found.Add(new Target { Hwnd = hwnd, Base = rect, Title = title.ToString() });
            return true;
        }, IntPtr.Zero);
        found.Sort(delegate(Target a, Target b) { return a.Base.Top - b.Base.Top; });
        into.Clear(); into.AddRange(found);
    }

    // 只隐藏 SysListView32 不够：桌面那一层不会自己重画，旧图标的像素会留在屏幕上，
    // 看起来就像"原图标还在、上面又叠了一层会弹的"。这里藏完之后强制资源管理器重画一次。
    static void HideIcons(IntPtr list, IntPtr host)
    {
        Native.ShowWindow(list, 0);
        InvalidateRect(host, IntPtr.Zero, true);
        RedrawWindow(host, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
    }

    // 程序跑起来之后新开的窗口也要一起弹，所以名单要定期重扫。
    static void RefreshWindows(List<Target> windows, uint ownPid, StringBuilder log)
    {
        List<Target> current = new List<Target>();
        CollectWindows(ownPid, current);
        List<Target> stale = new List<Target>();
        foreach (Target t in windows) if (!IsWindow(t.Hwnd)) stale.Add(t);
        foreach (Target t in stale) windows.Remove(t);
        int added = 0;
        foreach (Target t in current)
        {
            bool known = false;
            foreach (Target k in windows) if (k.Hwnd == t.Hwnd) { known = true; break; }
            if (known) continue;
            windows.Add(t); added++;
        }
        if (added > 0 && log != null) log.Append("  + 新窗口 " + added + " 个，现在 " + windows.Count + " 个在弹\r\n");
    }

    static void RestoreWindows(List<Target> targets, StringBuilder log)
    {
        int ok = 0, fail = 0;
        foreach (Target t in targets)
        {
            if (!IsWindow(t.Hwnd)) continue;
            if (SetWindowPos(t.Hwnd, IntPtr.Zero, t.Base.Left, t.Base.Top,
                             t.Base.Right - t.Base.Left, t.Base.Bottom - t.Base.Top,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER)) ok++;
            else fail++;
        }
        if (log == null) return;
        Thread.Sleep(400);
        int bad = 0;
        foreach (Target t in targets)
        {
            if (!IsWindow(t.Hwnd)) continue;
            RECT now;
            if (!GetWindowRect(t.Hwnd, out now)) continue;
            if (now.Left == t.Base.Left && now.Top == t.Base.Top && now.Right == t.Base.Right && now.Bottom == t.Base.Bottom) continue;
            bad++;
            log.Append("  未复位：" + t.Title + " 目标 " + t.Base.Left + "," + t.Base.Top + "," + t.Base.Right + "," + t.Base.Bottom
                + " 实际 " + now.Left + "," + now.Top + "," + now.Right + "," + now.Bottom + "\r\n");
        }
        log.Append("窗口还原：成功 " + ok + "，失败 " + fail + "，仍错位 " + bad + "\r\n");
    }

    static void SaveRects(string path, List<Target> targets)
    {
        StringBuilder text = new StringBuilder();
        foreach (Target t in targets)
            text.Append(t.Hwnd.ToInt64()).Append(' ').Append(t.Base.Left).Append(' ')
                .Append(t.Base.Top).Append(' ').Append(t.Base.Right).Append(' ').Append(t.Base.Bottom).Append("\r\n");
        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
    }

    static int RestoreWatch(string[] argv)
    {
        int pid = int.Parse(argv[1]);
        string path = argv[2];
        try { Process.GetProcessById(pid).WaitForExit(); } catch (ArgumentException) { }
        List<Target> targets = new List<Target>();
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                // 文件被写坏半行时只跳过这一行，绝不让一次解析把复位进程整个带崩
                try
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length != 5) continue;
                    Target t = new Target();
                    t.Hwnd = new IntPtr(long.Parse(parts[0]));
                    t.Base = new RECT { Left = int.Parse(parts[1]), Top = int.Parse(parts[2]), Right = int.Parse(parts[3]), Bottom = int.Parse(parts[4]) };
                    targets.Add(t);
                }
                catch (Exception) { }   // 解析失败就跳过这一行
            }
        }
        RestoreWindows(targets, null);
        try { File.Delete(path); } catch (Exception) { }
        return 0;
    }

    static bool Pump()
    {
        MSG msg;
        bool hot = false;
        while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1) != 0)
        {
            if (msg.message == WM_HOTKEY) hot = true;
            TranslateMessage(ref msg); DispatchMessage(ref msg);
        }
        return hot;
    }

    // 不走热键消息，直接查全局按键状态：窗口没焦点、热键被别的软件抢走时也能退出
    static bool QuitKeysDown()
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool q = (GetAsyncKeyState(0x51) & 0x8000) != 0;
        return ctrl && q && (alt || shift);
    }

    // 把自身复制成另一个文件名再启动，作为“负责恢复桌面”的看门狗：
    // 任务管理器里按名字结束“桌面弹弹弹.exe”时，不会把“朋友的酒守护.exe”一起带走。
    static string GuardExe(string dir)
    {
        try
        {
            string me = Environment.GetCommandLineArgs()[0];
            string guard = Path.Combine(dir, "朋友的酒守护.exe");
            bool need = true;
            try
            {
                if (File.Exists(guard) && new FileInfo(guard).Length == new FileInfo(me).Length) need = false;
            }
            catch (Exception) { }
            if (need) File.Copy(me, guard, true);
            return guard;
        }
        catch (Exception)
        {
            return Environment.GetCommandLineArgs()[0];
        }
    }

    [STAThread]
    static int Main(string[] argv)
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
        Guard.Install();
        if (argv.Length >= 3 && argv[0] == "--restore") return RestoreWatch(argv);
        // 看门狗：只等父进程退出再把原图标放回来，绝不能顺手把自己再启动一遍。
        if (argv.Length == 3 && argv[0] == "--watch")
        {
            IntPtr window = new IntPtr(long.Parse(argv[2]));
            uint explorer; GetWindowThreadProcessId(window, out explorer);
            try { Process.GetProcessById(int.Parse(argv[1])).WaitForExit(); } catch (ArgumentException) { }
            uint current; GetWindowThreadProcessId(window, out current);
            if (IsWindow(window) && current == explorer) Native.ShowWindow(window, 5);
            Thread.Sleep(600);
            DesktopHost.RepaintDesktop();
            Thread.Sleep(1200);
            DesktopHost.RepaintDesktop();
            return 0;
        }

        // 自检用：开一个自己最大化的窗口，看防最大化模块能不能把它压回中间大小
        if (argv.Length == 1 && argv[0] == "--guardtest")
        {
            var probe = new System.Windows.Window
            {
                Title = "我要最大化",
                Width = 520,
                Height = 340,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "这个窗口一启动就最大化",
                    FontSize = 24,
                    Margin = new System.Windows.Thickness(24),
                    Foreground = System.Windows.Media.Brushes.White
                },
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 26, 60))
            };
            // 用一个别的进程的窗口来试：记事本，拉成最大化，看会不会被压回来
            Process note = Process.Start("notepad.exe");
            Thread.Sleep(900);
            IntPtr nh = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)note.Id && IsWindowVisible(hwnd)) { nh = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            if (nh != IntPtr.Zero) Native.ShowWindow(nh, 3);
            WindowGuard.Start();
            Thread.Sleep(4500);
            WindowGuard.Shutdown();
            try { note.Kill(); } catch (Exception) { }
            return 0;
        }

        string dir = AppDomain.CurrentDomain.BaseDirectory;

        // 自检用：只画帧序列 10 秒（不弹任何东西），用来确认「帧画面」真的画到屏幕上了
        if (argv.Length >= 1 && argv[0] == "--frametest")
        {
            bool fok = FrameStage.Start(dir);
            long ft0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 250 && fok; i++)
            {
                double sec = (double)(Stopwatch.GetTimestamp() - ft0) / Stopwatch.Frequency;
                FrameStage.Draw(6.0 + sec);
                Thread.Sleep(40);
            }
            FrameStage.Shutdown();
            return fok ? 0 : 4;
        }
        string wav = Path.Combine(dir, "reference-audio.wav");
        double demo = 0;
        string logPath = null;
        bool victimOnly = false;
        for (int i = 0; i < argv.Length; i++)
        {
            // --demo 参数解析兜底：写错数字就当没给，别抛异常
            if (argv[i] == "--demo" && i + 1 < argv.Length)
            {
                try { demo = double.Parse(argv[i + 1]); } catch (Exception) { demo = 0; }
            }
            else if (argv[i] == "--log" && i + 1 < argv.Length) logPath = argv[i + 1];
            else if (argv[i] == "--victim") victimOnly = true;
            else if (argv[i] == "--setuptest") SetupGate.Force = true;
        }

        // 启动前的说明弹窗：不点运行不干活
        if (!victimOnly && demo <= 0)
        {
            if (!Intro.Ask()) return 0;
        }

        if (victimOnly)
        {
            bool shown = VictimWindows.Start(dir);
            for (int i = 0; i < 600 && !QuitKeysDown(); i++) Thread.Sleep(200);
            VictimWindows.Shutdown();
            return shown ? 0 : 3;
        }

        // 全部固定成常量，不再读任何配置文件
        double bpm = 130;             // 每分钟 130 拍
        double beats = 2;             // 两拍一弹
        double iconAmount = 0.38;     // 图标抖动幅度
        double windowAmount = 0.08;   // 窗口抖动幅度
        bool stagger = true;          // 错拍
        bool audioOn = true;          // 播 reference-audio.wav
        bool windowsOn = true;        // 真窗口跟着动
        bool videoOn = true;          // 两个视频小窗
        string videoPath = null;      // 用目录里的 mp4
        bool taskbarOn = false;       // 不折腾任务栏
        int taskbarAmount = 6;
        bool popOn = true;            // 原生对话框
        double popAt = 0;             // 一开始就弹
        double popChance = 0.3;       // 每个拍点 30%
        int popCap = 5;
        string popStyle = "msgbox";
        bool typeOn = true;           // 虚拟按键
        string typeText = "78917891";
        bool sfxOn = true;
        int sfxPx = 4;
        bool appsOn = true;           // 自动打开应用
        double appsChance = 0.35;
        int appsCap = 4;

        StringBuilder log = new StringBuilder();
        log.Append("BPM " + bpm + " · 每 " + beats + " 拍一弹 · 周期 " + (60.0 / bpm * beats).ToString("0.000") + " 秒\r\n");

        // 谁都不许最大化：被最大化的窗口立刻压回屏幕 70% 居中
        WindowGuard.Start();

        // 1. 桌面图标：找 Explorer 的图标窗口，把我们的分层叠加层挂上去
        IntPtr list = Desktop.FindList();

        // 上一次如果是被强行结束（任务管理器结束进程），图标窗口会一直停在隐藏状态。
        // 启动时先把它们放出来，再重新拿一次句柄，免得第二次打开就“找不到桌面图标”。
        if (list != IntPtr.Zero && !Native.IsWindowVisible(list))
        {
            Native.ShowWindow(list, 5);
            Thread.Sleep(250);
            DesktopHost.RepaintDesktop();
            Thread.Sleep(150);
            list = Desktop.FindList();
        }

        if (list == IntPtr.Zero || !Native.IsWindowVisible(list))
        {
            log.Append("没有找到可见的桌面图标（请确认在标准资源管理器桌面、且图标未隐藏）\r\n");
            if (logPath != null) File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return 2;
        }
        List<DesktopItem> items = Desktop.ReadItems(list);
        string guard = GuardExe(dir);

        IntPtr host = Native.GetParent(list);
        for (int i = 0; i < items.Count; i++) items[i].Index = i;
        List<JellyLayer> icons = JellyLayer.CreateGroups(host, items);
        foreach (JellyLayer icon in icons) icon.Prepare();
        // 主进程被杀也会把图标放回来（沿用原有看门狗）
        try
        {
            Process.Start(new ProcessStartInfo(guard, "--watch " + Process.GetCurrentProcess().Id + " " + list.ToInt64())
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception) { }

        // 2. 窗口
        List<Target> windows = new List<Target>();
        uint ownPid = (uint)Process.GetCurrentProcess().Id;
        if (windowsOn) CollectWindows(ownPid, windows);
        string rectFile = Path.Combine(Path.GetTempPath(), "jelly-all-rects.txt");
        if (windows.Count > 0)
        {
            SaveRects(rectFile, windows);
            try
            {
                Process.Start(new ProcessStartInfo(guard, "--restore " + Process.GetCurrentProcess().Id + " " + rectFile)
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception) { }
        }

        // 3. 音频（也是时钟）
        WaveAudio audio = new WaveAudio();
        bool audioOk = false;
        if (audioOn)
        {
            try { audio.Start(wav); audioOk = true; }
            catch (Exception error)
            {
                // 刚启动时音频设备偶尔还占着，隔 300ms 再试一次
                Thread.Sleep(300);
                try { audio.Start(wav); audioOk = true; log.Append("音频首次打开失败，重试成功\r\n"); }
                catch (Exception again) { log.Append("音频未能播放：" + again.Message + "\r\n"); }
            }
        }
        double cycle = 60.0 / bpm * beats;

        // 3.5 视频改成两个置顶小窗（不再挂桌面壁纸层）：只在启动时建一次，之后每两拍换个落点
        bool videoOk = false;
        if (videoOn)
        {
            string mp4 = FindVideo(dir, videoPath);
            if (mp4 == null) log.Append("文件夹里没有 mp4，小窗视频跳过\r\n");
            else if (VideoWindows.Start(mp4, 2, 320, 180))
            {
                videoOk = true;
                log.Append("视频小窗：2 个 320×180 置顶窗，" + Path.GetFileName(mp4) + "\r\n");
            }
            else log.Append("视频小窗失败\r\n");
        }

        // 3.7 跟拍弹窗：原生对话框 与 System32 里无害 GUI 程序，可同时开，互不抢名额
        if (popOn && popStyle != "apps")
        {
            MsgBoxPop.Configure(popAt, 60.0 / bpm, popChance, popCap);
            log.Append("对话框：从 " + popAt + " 秒开始，每个拍点按 " + (popChance * 100).ToString("0") + "% 概率弹原生对话框"
                + "（警告 / 报错 / 问号 / 纯文字，标题正文都是朋友的酒），最多 " + popCap + " 个\r\n");
        }
        if (appsOn)
        {
            PopWindows.Configure(popAt, 60.0 / bpm, appsChance, cycle, appsCap);
            log.Append("自动打开应用：从 " + popAt + " 秒开始，每个拍点按 " + (appsChance * 100).ToString("0") + "% 概率拉起，最多 " + appsCap
                + " 个（白名单 " + PopWindows.PoolSize + " 个）\r\n");
        }
        if (popOn && popStyle == "apps")
        {
            PopWindows.Configure(popAt, 60.0 / bpm, popChance, cycle, popCap);
            log.Append("对话框→应用模式：最多 " + popCap + " 个\r\n");
        }

        // 会一直换配色的 CMD 窗口：关掉一个就顺手结束一个自动打开的程序
        CmdColor.Prepare(dir);
        double cmdAt = 0.6 + random.Next(0, 2);
        log.Append("变色 CMD 窗口：不定时弹出，最多 3 个；关掉一个就结束一个自动打开的程序\r\n");
        log.Append("全特效同时开：抖动 / 反色 / 残影 / 切条（常驻撕裂）每拍一起上，另有每拍 55% 概率“哔”一声（do~高do）\r\n");

        if (typeOn)
        {
            KeySpam.On = true;
            KeySpam.Configure(typeText);
            log.Append("虚拟按键：一直输出（\"" + typeText + "\" 循环，每 0.1 秒一个字符）\r\n");
        }

        // 3.85 图标窗：全程不定时地弹，随时间越来越密，屏上最多 4 个
        double iconAt = 0.8 + random.Next(0, 2);
        int iconCap = 4;

        // 3.9 置顶弹窗：标题栏《朋友的酒》已入侵你的电脑，里面播动态视频，跟着鼠标跑
        string followMp4 = videoOn ? FindVideo(dir, videoPath) : null;
        bool followOk = followMp4 != null && CursorFollow.Start(followMp4);
        log.Append("置顶弹窗：跟随鼠标，播放视频（" + (followOk ? "已创建" : "创建失败") + "）\r\n");

        // 4. 显示叠加层，藏起原始图标
        foreach (JellyLayer icon in icons) icon.Show();
        foreach (JellyLayer icon in icons)
            SetWindowPos(icon.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (items.Count > 0) HideIcons(list, host);
        Thread.Sleep(120);

        // 5. 热键：Ctrl+Alt+Q 退出并全部还原
        IntPtr msgWnd = CreateWindowEx(0, "STATIC", "JellyAll", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        bool hotAlt = RegisterHotKey(msgWnd, 1, MOD_CONTROL | MOD_ALT, (uint)KeysQ());
        bool hotShift = RegisterHotKey(msgWnd, 2, MOD_CONTROL | MOD_SHIFT, (uint)KeysQ());
        log.Append("退出键：Ctrl+Alt+Q / Ctrl+Shift+Q（注册 " + (hotAlt ? "OK" : "失败") + " / " + (hotShift ? "OK" : "失败") + "，另每帧轮询按键状态）\r\n");
        log.Append("图标 " + items.Count + " 个 / 动画层 " + icons.Count + " 个 / 窗口 " + windows.Count + " 个\r\n");

        // 整屏 GDI 特效：每个拍点抖一次，每 8 拍反色一次（与抖动同帧叠加），偏 4 拍来残影
        // NetGDI：反色 / 模糊 / 圆形隧道 / 阴影画刷 / 图案画刷 / 颜色滤镜 / 屏幕融化，七种一起开
        bool ngOk = NetGdiFx.Enable(dir);
        log.Append("NetGDI 七种特效（反色/模糊/圆形隧道/阴影画刷/图案画刷/颜色滤镜/融化）：" + (ngOk ? "全部开启" : "没找到 NetGDI.dll，跳过") + "\r\n");

        // 两套一起上：NetGDI 走它自己的钩子刷屏，本程序的抖动/反色/切条照旧每拍叠一层
        if (sfxOn) ScreenFx.Configure(60.0 / bpm, sfxPx, true, true, true, true);
        if (ngOk) log.Append("NetGDI 宿主进程已拉起，与本地 GDI 特效叠加\r\n");

        // 视频帧：mp4 按 25fps 拆成 jpg（或打包成 frames.dat），直接 BitBlt 到屏幕，和音乐同步，不依赖壁纸层
        bool framesOk = FrameStage.Start(dir);
        FrameStage.SetPositionSource(delegate { return PositionNow; });
        log.Append("桌面帧画面：" + (framesOk ? ("已加载 " + FrameStage.FrameCount + " 帧（25fps，跟音乐同步）") : "没找到 视频帧 / frames.dat，跳过") + "\r\n");

        long start = Stopwatch.GetTimestamp();
        double lastWindowDrive = -1, lastWindowScan = 0, lastVideoSync = -1, lastVideoDrift = 0;
        double lastKeyAt = -1;
        bool videoAligned = false, iconShown = false;
        int lastBeepBeat = -1;
        int frames = 0, windowBatches = 0, batchesInSecond = 0;
        double windowMs = 0, worstWindowMs = 0;
        double lastSecond = 0; int fpsInSecond = 0;
        bool exiting = false, videoFrozen = false, endPhase = false;
        int frameFailures = 0;
        try
        {
            while (!exiting)
            {
            try
            {
                bool hotFired = Pump();
                if (hotFired || QuitKeysDown()) { exiting = true; break; }
                double elapsed = (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency;
                if (demo > 0 && elapsed >= demo) break;
                double position = audioOk ? audio.PositionSeconds() : elapsed;
                double phase = position / cycle;

                // 拍点随机“哔”一声：从 do re mi fa sol la si do 里挑一个音高
                int beatNow = (int)Math.Floor(position / (60.0 / bpm));
                if (beatNow != lastBeepBeat)
                {
                    lastBeepBeat = beatNow;
                    if (beatNow > 0 && random.Next(100) < 55)
                    {
                        int[] notes = { 523, 587, 659, 698, 784, 880, 988, 1047 };
                        int freq = notes[random.Next(notes.Length)];
                        int ms = 90 + random.Next(0, 90);
                        Thread beeper = new Thread(delegate()
                        {
                            try { Console.Beep(freq, ms); } catch (Exception) { }
                        });
                        beeper.IsBackground = true;
                        beeper.Start();
                    }
                }

                // 整首放完：停音乐、把所有弹窗和病毒拉起的程序全部关掉、清场，再进收尾画面
                if (audioOk && !endPhase && audio.DurationSeconds > 0
                    && audio.PlayedSeconds() >= audio.DurationSeconds - 0.15)
                {
                    endPhase = true;
                    log.Append("音乐放完：关闭全部弹窗与外部程序，进入收尾画面\r\n");
                    try { audio.Stop(); } catch (Exception) { }
                    CmdColor.Shutdown();
                    if (popOn && popStyle != "apps") MsgBoxPop.Shutdown();
                    if (appsOn || (popOn && popStyle == "apps")) PopWindows.Shutdown();
                    KeySpam.On = false;
                    IconWindow.Shutdown();
            VictimWindows.Shutdown();
                    CursorFollow.Shutdown();
                    VideoWindows.Shutdown();
                    WpfVideo.Shutdown();
                    NetGdiFx.Disable();
                    ScreenFx.Shutdown();
                    DesktopHost.RepaintDesktop();
                    ScreenFx.RepaintAll();
                    Thread.Sleep(300);
                    DesktopHost.RepaintDesktop();
                    ScreenFx.RepaintAll();

                    // 收尾音：只放一遍，放完就关掉设备
                    string ending = Path.Combine(dir, "ending.wav");
                    if (File.Exists(ending))
                    {
                        try
                        {
                            Thread.Sleep(300);
                            WaveAudio endAudio = new WaveAudio();
                            try { endAudio.Start(ending); }
                            catch (Exception first)
                            {
                                Thread.Sleep(400);
                                endAudio.Start(ending);
                                log.Append("收尾音首次打开失败（" + first.Message + "），重试成功\r\n");
                            }
                            Thread stopper = new Thread(delegate()
                            {
                                Thread.Sleep(4000);
                                try { endAudio.Stop(); } catch (Exception) { }
                            });
                            stopper.IsBackground = true;
                            stopper.Start();
                        }
                        catch (Exception) { }
                    }

                    VictimWindows.Start(dir);

                    // 卡片和收尾音演完：留住收尾画面 30 秒（随时可按退出键），不再切蓝屏
                    Thread.Sleep(3400);
                    for (int i = 0; i < 300 && !QuitKeysDown(); i++) Thread.Sleep(100);
                    VictimWindows.Shutdown();
                    exiting = true;
                    break;
                }

                // 音频是唯一时钟：视频每 0.5 秒跟它比一次，差超过 0.3 秒就拉回来（音频循环重来时也会自动归零）
                if (WpfVideo.Running && elapsed - lastVideoSync >= 0.5)
                {
                    lastVideoSync = elapsed;
                    double videoPosition = WpfVideo.PositionSeconds;
                    lastVideoDrift = position - videoPosition;
                    if (!videoAligned && elapsed >= 1.2)
                    {
                        // 起播那一下画面天生慢 0.1 秒左右，先按实测偏差推一把，之后只做兜底矫正
                        videoAligned = true;
                        if (lastVideoDrift > 0.04) WpfVideo.Seek(position + lastVideoDrift);
                    }
                    else if (Math.Abs(lastVideoDrift) > 0.30) WpfVideo.Seek(position);
                }

                if (sfxOn)
                {
                    ScreenFx.Tick(position, elapsed);
                    ScreenFx.Drive(elapsed);
                    // 撕裂开始后把壁纸视频冻住：它一直在重绘，会把画在屏幕上的切条整片刷掉
                    if (ScreenFx.MeltActive && !videoFrozen) { WpfVideo.Freeze(); videoFrozen = true; }
                }
                // 帧画面铺在最后：哪怕被别的效果盖住，25fps 也会立刻补回来
                PositionNow = position;   // 帧画面线程按这个位置取帧
                if (framesOk) FrameStage.Draw(position);
                if (popOn && popStyle != "apps") MsgBoxPop.Tick(position);
                if (appsOn || (popOn && popStyle == "apps")) PopWindows.Tick(position);
                if (elapsed >= cmdAt && CmdColor.Count < 3)
                {
                    bool cmdOk = CmdColor.Start();
                    log.Append("变色 CMD：第 " + elapsed.ToString("0") + " 秒弹出（" + (cmdOk ? "已开" : "失败") + "），当前 " + CmdColor.Count + " 个\r\n");
                    cmdAt = elapsed + 16 + random.Next(0, 22);
                }
                // 两个小窗每两拍换一次落点，途中平滑滑过去，永远夹在屏幕里
                if (videoOk) VideoWindows.Drive(position, cycle * 2);
                CursorFollow.Drive();
                if (elapsed >= iconAt && IconWindow.Count < iconCap)
                {
                    bool iconOk = IconWindow.Start(dir + "\\app.ico");
                    if (iconOk) iconShown = true;
                    log.Append("图标窗：第 " + elapsed.ToString("0") + " 秒弹出第 " + IconWindow.Count + " 个（" + (iconOk ? "已显示" : "创建失败") + "）\r\n");
                    iconAt = elapsed + 9 + random.Next(0, 16);
                }
                // 打字跟 BPM 无关，就是一直打：每 0.1 秒送一个字符
                if (typeOn && elapsed - lastKeyAt >= 0.1)
                {
                    lastKeyAt = elapsed;
                    KeySpam.TypeOne();
                }

                for (int i = 0; i < icons.Count; i++) icons[i].UpdatePhase(phase, iconAmount, stagger);

                if (windowsOn && elapsed - lastWindowScan >= 0.5)
                {
                    lastWindowScan = elapsed;
                    RefreshWindows(windows, ownPid, log);
                    if (IsWindowVisible(list)) HideIcons(list, host);
                }

                // 窗口改尺寸很贵（每个窗口都要重排），按 20Hz 驱动；相位仍取自音频，
                // 所以节奏不会漂，只是帧更稀。
                // 窗口一多，每次改尺寸的代价翻倍；超过 3 个就把驱动降到 10Hz，先保住图标的帧率
                double driveInterval = windows.Count > 5 ? 0.15 : (windows.Count > 3 ? 0.10 : 0.05);
                if (windows.Count > 0 && windowAmount > 0 && elapsed - lastWindowDrive >= driveInterval)
                {
                    lastWindowDrive = elapsed;
                    long tick = Stopwatch.GetTimestamp();
                    for (int i = 0; i < windows.Count; i++)
                    {
                        Target t = windows[i];
                        if (!IsWindow(t.Hwnd)) continue;
                        if (!IsWindowVisible(t.Hwnd)) continue;
                        if (IsIconic(t.Hwnd)) continue;   // 最小化的窗口不用跟着动
                        double sx, sy;
                        Scale(phase + (stagger ? i * 0.13 : 0), windowAmount, out sx, out sy);
                        int bw = t.Base.Right - t.Base.Left, bh = t.Base.Bottom - t.Base.Top;
                        int w = (int)Math.Round(bw * sx), h = (int)Math.Round(bh * sy);
                        if (w < 80) w = 80; if (h < 40) h = 40;

                        // 每两拍给被选中的窗口换一个随机漂移量，但整窗必须留在屏幕内，绝不顶到边缘外
                        int slot = (int)Math.Floor(position / (cycle * 2));
                        if (t.Slot != slot)
                        {
                            t.Slot = slot;
                            t.FromDX = t.DriftX; t.FromDY = t.DriftY;
                            t.DriftX = random.Next(-120, 121);
                            t.DriftY = random.Next(-90, 91);
                            t.DriftFrom = position;
                        }
                        double dt = (position - t.DriftFrom) / (cycle * 2);
                        if (dt < 0) dt = 0; if (dt > 1) dt = 1;
                        double ease = 1 - Math.Pow(1 - dt, 3);
                        int ddx = t.FromDX + (int)Math.Round((t.DriftX - t.FromDX) * ease);
                        int ddy = t.FromDY + (int)Math.Round((t.DriftY - t.FromDY) * ease);

                        int sw2 = GetSystemMetrics(0), sh2 = GetSystemMetrics(1);
                        int x = t.Base.Left + (bw - w) / 2 + ddx, y = t.Base.Top + (bh - h) / 2 + ddy;
                        if (x < 0) x = 0;
                        if (y < 0) y = 0;
                        if (x + w > sw2) x = sw2 - w;
                        if (y + h > sh2) y = sh2 - h;
                        SetWindowPos(t.Hwnd, IntPtr.Zero, x, y, w, h,
                            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOREDRAW | SWP_NOSENDCHANGING | SWP_DEFERERASE);
                    }
                    double ms = (double)(Stopwatch.GetTimestamp() - tick) * 1000 / Stopwatch.Frequency;
                    windowMs += ms; if (ms > worstWindowMs) worstWindowMs = ms; windowBatches++; batchesInSecond++;
                }

                frames++; fpsInSecond++;
                if (elapsed - lastSecond >= 1)
                {
                    log.Append("t=" + (int)elapsed + "s fps=" + fpsInSecond
                        + " pos=" + (audioOk ? audio.PositionSeconds().ToString("0.000") : "n/a")
                        + " phase=" + (phase - Math.Floor(phase)).ToString("0.000")
                        + " windows=" + windows.Count + " drives=" + batchesInSecond
                        + (WpfVideo.Running ? " vid=" + WpfVideo.PositionSeconds.ToString("0.000") + " drift=" + lastVideoDrift.ToString("0.00") : "")
                        + "\r\n");
                    lastSecond = elapsed; fpsInSecond = 0; batchesInSecond = 0;
                }
                long target = start + (long)((frames + 1) * Stopwatch.Frequency / 60);
                long remain = target - Stopwatch.GetTimestamp();
                if (remain > 0) Thread.Sleep((int)(remain * 1000 / Stopwatch.Frequency));
            }
            catch (Exception frameError)
            {
                frameFailures++;
                try { Guard.NoteFrame(frameError); } catch (Exception) { }
                if (logPath == null || frameFailures <= 5)
                    log.Append("单帧异常（已跳过 " + frameFailures + "）：" + frameError.GetType().Name + " " + frameError.Message + "\r\n");
                try { Thread.Sleep(30); } catch (Exception) { }
            }
            }
        }
        finally
        {
            NetGdiFx.Disable();
            FrameStage.Shutdown();
            if (sfxOn) ScreenFx.Shutdown();
            WindowGuard.Shutdown();
            UnregisterHotKey(msgWnd, 1);
            UnregisterHotKey(msgWnd, 2);
            foreach (JellyLayer icon in icons) icon.Dispose();
            if (Native.IsWindow(list)) Native.ShowWindow(list, 5);
            RestoreWindows(windows, log);
            KeySpam.On = false;
            CmdColor.Shutdown();
            if (popOn && popStyle != "apps") MsgBoxPop.Shutdown();
            if (appsOn || (popOn && popStyle == "apps")) PopWindows.Shutdown();
            DesktopHost.RestoreTaskbar();
            audio.Stop();
            IconWindow.Shutdown();
            CursorFollow.Shutdown();
            VideoWindows.Shutdown();
            WpfVideo.Shutdown();
            // 撤掉视频窗口之后桌面还留着最后一帧。窗口刚销毁时资源管理器还没反应过来，
            // 所以要等一拍再刷，并且刷两次（第一次常常只刷回一部分）。
            Thread.Sleep(500);
            DesktopHost.RepaintDesktop();
            Thread.Sleep(800);
            DesktopHost.RepaintDesktop();
            try { if (File.Exists(rectFile)) File.Delete(rectFile); } catch (Exception) { }
        }

        log.Append("总帧 " + frames + "，平均 " + (frames / Math.Max(0.001, (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency)).ToString("0.0")
            + " FPS；窗口批次 " + windowBatches + "，平均 " + (windowMs / Math.Max(1, windowBatches)).ToString("0.0")
            + " ms，最差 " + worstWindowMs.ToString("0.0") + " ms；音频 " + (audioOk ? "正常" : "未播放")
            + "；视频 " + (videoOk ? "在放" : "未启用") + "；弹窗 " + (popStyle == "apps" ? PopWindows.Total : MsgBoxPop.Opened) + " 个；按键 " + KeySpam.Sent + " 个"
            + (sfxOn ? "；特效 抖动 " + ScreenFx.Shakes + " / 反色 " + ScreenFx.Inverts + "（同帧 " + ScreenFx.BothFrames + " 帧）/ 残影 " + ScreenFx.Ghosts + " / 切条 " + ScreenFx.Slices + "（" + ScreenFx.SliceFrames + " 帧），刷屏失败 " + ScreenFx.BitbltFails : "")
            + "；异常 " + Guard.Notes + " 次，跳过帧 " + frameFailures
            + "\r\n");
        if (logPath != null) File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        return 0;
    }

    // 兜底：任何线程或调度器冒出来的异常都不许变成“未处理的异常”弹窗，更不许把进程直接带走。
    // 能救的救（关特效、放回桌面图标与任务栏），救不回来也至少把桌面复原再退。
    internal static double PositionNow;   // 当前音乐位置，给帧画面线程用

    internal static class Guard
    {
        static int notes;

        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Note(e.ExceptionObject as Exception);
                EmergencyRestore();
            };
        }

        // 挂到每个 WPF 线程的 Dispatcher 上：Timer 回调里抛异常默认会结束整个进程
        public static void HookDispatcher()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException +=
                delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    try { e.Handled = true; } catch (Exception) { }
                    Note(e.Exception);
                };
        }

        static void Note(Exception error)
        {
            if (error == null) return;
            notes++;
            if (notes > 50) return;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + error.GetType().Name + "：" + error.Message
                    + "\r\n" + error.StackTrace + "\r\n\r\n", Encoding.UTF8);
            }
            catch (Exception) { }
        }

        // 单帧异常：只记录，不打断演出
        public static void NoteFrame(Exception error)
        {
            Note(error);
        }

        static void EmergencyRestore()
        {
            try { NetGdiFx.Disable(); } catch (Exception) { }
            try { ScreenFx.Shutdown(); } catch (Exception) { }
            try { KeySpam.On = false; } catch (Exception) { }
            try { WindowGuard.Shutdown(); } catch (Exception) { }
            try { DesktopHost.RestoreTaskbar(); } catch (Exception) { }
            try { DesktopHost.RepaintDesktop(); } catch (Exception) { }
        }

        public static int Notes { get { return notes; } }
    }

    // 视频来源顺序：程序所在文件夹 → 各固定盘根目录（你的 mp4 放在 F 盘根目录）
    static string FindVideo(string dir, string configured)
    {
        if (configured != null && File.Exists(configured)) return configured;
        try
        {
            foreach (string file in Directory.GetFiles(dir, "*.mp4")) return file;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                try
                {
                    foreach (string file in Directory.GetFiles(drive.RootDirectory.FullName, "*.mp4")) return file;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return null;
    }

    static int KeysQ() { return 0x51; }
}
