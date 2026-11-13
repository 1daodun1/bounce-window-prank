using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 窗口果冻：把桌面图标的弹弹弹公式搬到真实窗口上。
// 每帧对所有可见、非最小化、非最大化的顶层窗口调 SetWindowPos，只改位置和尺寸，
// 不管被驱动的程序愿不愿意重绘（SWP_NOREDRAW 让它在本次变更里别急着擦）。
// 退出时（包括被强行关闭时由看门狗兜底）把每个窗口原样放回去。
internal static class JellyWindows
{
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOREDRAW = 0x0008,
               SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_NOSENDCHANGING = 0x0400;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
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
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    sealed class Target
    {
        public IntPtr Hwnd;
        public RECT Base;
        public string Title;
    }

    // 与桌面图标完全同一套：竖直方向随 wave 伸缩，水平方向反向 0.72 倍（面积近似守恒）。
    static void Scale(double phase, double amount, out double sx, out double sy)
    {
        double wave = Math.Cos(phase * Math.PI * 2);
        sy = 1 + amount * wave;
        sx = 1 - amount * 0.72 * wave;
    }

    static string ClassOf(IntPtr hwnd)
    {
        StringBuilder text = new StringBuilder(256);
        GetClassName(hwnd, text, 256);
        return text.ToString();
    }

    static bool Collect(uint ownPid, List<Target> into, bool verbose)
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
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            if (w < 160 || h < 90) return true;
            StringBuilder title = new StringBuilder(256);
            GetWindowText(hwnd, title, 256);
            if (title.Length == 0) return true;
            found.Add(new Target { Hwnd = hwnd, Base = rect, Title = title.ToString() });
            return true;
        }, IntPtr.Zero);
        found.Sort(delegate(Target a, Target b) { return a.Base.Top - b.Base.Top; });
        into.Clear();
        into.AddRange(found);
        if (verbose)
            foreach (Target t in found)
                Console.WriteLine("  " + t.Hwnd + "  " + (t.Base.Right - t.Base.Left) + "x" + (t.Base.Bottom - t.Base.Top) + "  " + t.Title);
        return found.Count > 0;
    }

    static void Restore(List<Target> targets)
    {
        int ok = 0, gone = 0;
        foreach (Target t in targets)
        {
            if (!IsWindow(t.Hwnd)) { gone++; continue; }
            if (SetWindowPos(t.Hwnd, IntPtr.Zero, t.Base.Left, t.Base.Top,
                             t.Base.Right - t.Base.Left, t.Base.Bottom - t.Base.Top,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER)) ok++;
        }
        Console.WriteLine("还原：" + ok + " 个成功， " + gone + " 个窗口已关闭");
    }

    static void SaveRects(string path, List<Target> targets)
    {
        StringBuilder text = new StringBuilder();
        foreach (Target t in targets)
            text.Append(t.Hwnd.ToInt64()).Append(' ').Append(t.Base.Left).Append(' ')
                .Append(t.Base.Top).Append(' ').Append(t.Base.Right).Append(' ').Append(t.Base.Bottom).Append("\r\n");
        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
    }

    // 看门狗：主进程无论怎么死掉，都由这个子进程把窗口放回去。
    static int Watch(string[] args)
    {
        int pid = int.Parse(args[1]);
        string path = args[2];
        try { Process.GetProcessById(pid).WaitForExit(); } catch (ArgumentException) { }
        List<Target> targets = new List<Target>();
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(' ');
                if (parts.Length != 5) continue;
                Target t = new Target();
                t.Hwnd = new IntPtr(long.Parse(parts[0]));
                t.Base = new RECT { Left = int.Parse(parts[1]), Top = int.Parse(parts[2]), Right = int.Parse(parts[3]), Bottom = int.Parse(parts[4]) };
                targets.Add(t);
            }
        }
        Restore(targets);
        return 0;
    }

    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (argv.Length >= 3 && argv[0] == "--watch") return Watch(argv);

        double seconds = 4, amount = 0.08, speed = 1.1;
        int fps = 60;
        Console.WriteLine("窗口果冻 - 同一套弹弹公式作用在所有可见窗口");
        Console.WriteLine("Ctrl+C 立即还原并退出。参数：--demo <秒> --amount <0..0.3> --speed <次/秒> --fps <n>");
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--demo" && i + 1 < argv.Length) seconds = double.Parse(argv[i + 1]);
            else if (argv[i] == "--amount" && i + 1 < argv.Length) amount = double.Parse(argv[i + 1]);
            else if (argv[i] == "--speed" && i + 1 < argv.Length) speed = double.Parse(argv[i + 1]);
            else if (argv[i] == "--fps" && i + 1 < argv.Length) fps = int.Parse(argv[i + 1]);
        }

        uint own; GetWindowThreadProcessId(Process.GetCurrentProcess().MainWindowHandle, out own);
        List<Target> targets = new List<Target>();
        if (!Collect((uint)Process.GetCurrentProcess().Id, targets, true)) { Console.WriteLine("没有可驱动的窗口"); return 2; }
        Console.WriteLine("目标窗口 " + targets.Count + " 个；振幅 " + (amount * 100).ToString("0") + "%；" + speed + " 次/秒；" + fps + " FPS；" + seconds + " 秒后还原");

        string rectFile = Path.Combine(Path.GetTempPath(), "jelly-windows-rects.txt");
        SaveRects(rectFile, targets);
        Process watcher = null;
        try
        {
            watcher = Process.Start(new ProcessStartInfo(Environment.GetCommandLineArgs()[0],
                "--watch " + Process.GetCurrentProcess().Id + " " + rectFile)
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception) { }

        long stopwatchStart = Stopwatch.GetTimestamp();
        double phase = 0;
        int frames = 0, skipped = 0;
        double worstMs = 0, totalMs = 0;
        try
        {
            while (true)
            {
                double elapsed = (double)(Stopwatch.GetTimestamp() - stopwatchStart) / Stopwatch.Frequency;
                if (elapsed >= seconds) break;
                long tick = Stopwatch.GetTimestamp();
                phase += speed * (1.0 / fps);
                double sx, sy; Scale(phase, amount, out sx, out sy);
                int applied = 0;
                foreach (Target t in targets)
                {
                    if (!IsWindow(t.Hwnd)) { skipped++; continue; }
                    int bw = t.Base.Right - t.Base.Left, bh = t.Base.Bottom - t.Base.Top;
                    int w = (int)Math.Round(bw * sx), h = (int)Math.Round(bh * sy);
                    if (w < 80) w = 80; if (h < 40) h = 40;
                    // 以中心为锚点：宽高变化时四边一起让位，看起来像站在原地的果冻。
                    int x = t.Base.Left + (bw - w) / 2, y = t.Base.Top + (bh - h) / 2;
                    if (SetWindowPos(t.Hwnd, IntPtr.Zero, x, y, w, h,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOREDRAW | SWP_NOSENDCHANGING)) applied++;
                }
                frames++;
                double ms = (double)(Stopwatch.GetTimestamp() - tick) * 1000 / Stopwatch.Frequency;
                totalMs += ms; if (ms > worstMs) worstMs = ms;

                // 按固定节拍补sleep，避免把核吃满
                long target = stopwatchStart + (long)((frames + 1) * Stopwatch.Frequency / fps);
                long remain = target - Stopwatch.GetTimestamp();
                if (remain > 0) Thread.Sleep((int)(remain * 1000 / Stopwatch.Frequency));
                if (elapsed > seconds) break;
            }
        }
        finally
        {
            Restore(targets);
            try { if (watcher != null && !watcher.HasExited) watcher.Kill(); } catch (Exception) { }
            try { if (File.Exists(rectFile)) File.Delete(rectFile); } catch (Exception) { }
        }

        Console.WriteLine("帧数 " + frames + "，平均 " + (totalMs / Math.Max(1, frames)).ToString("0.00") + " ms/帧，最差 " + worstMs.ToString("0.00") + " ms，实际 "
            + (frames / seconds).ToString("0.0") + " FPS");
        return 0;
    }
}
