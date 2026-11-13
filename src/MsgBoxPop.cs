using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 跟拍弹窗（原生版）：用 Windows 自己的 MessageBox 弹对话框，四种样式轮着来
//   黄色感叹号 / 红色报错 / 蓝色问号 / 无图标纯文字
// 标题和正文都写同一句话，全部含"朋友的酒"。
internal static class MsgBoxPop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr owner, string text, string title, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint MB_OK = 0x00000000;
    const uint MB_ICONERROR = 0x00000010;
    const uint MB_ICONQUESTION = 0x00000020;
    const uint MB_ICONWARNING = 0x00000030;
    const uint MB_SETFOREGROUND = 0x00010000;
    const uint WM_CLOSE = 0x0010;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    // 四种样式：警告 / 报错 / 问号 / 纯文字；标题与正文同字
    static readonly string[] texts =
    {
        "警告 朋友的酒",
        "报错 朋友的酒",
        "问号 朋友的酒",
        "朋友的酒已入侵电脑"
    };
    static readonly uint[] flags = { MB_ICONWARNING, MB_ICONERROR, MB_ICONQUESTION, MB_OK };

    static double startAt = 0, beatLen = 0.4615, chance = 0.3;
    static int cap = 5, lastBeat = -1, next = 0, opened, closed;
    static bool enabled;
    static Random random = new Random();
    static readonly List<IntPtr> live = new List<IntPtr>();

    public static int Opened { get { return opened; } }
    public static int Closed { get { return closed; } }
    public static int Live { get { return live.Count; } }

    public static void Configure(double spawnAt, double beatSeconds, double openChance, int maxOpen)
    {
        startAt = spawnAt < 0 ? 0 : spawnAt;
        beatLen = beatSeconds > 0.05 ? beatSeconds : 0.4615;
        chance = openChance < 0 ? 0 : (openChance > 1 ? 1 : openChance);
        cap = maxOpen < 1 ? 1 : (maxOpen > 30 ? 30 : maxOpen);
        lastBeat = -1;
        enabled = true;
    }

    public static void Tick(double position)
    {
        if (!enabled) return;
        if (position < startAt) return;

        int beat = (int)Math.Floor(position / beatLen);
        if (beat != lastBeat)
        {
            lastBeat = beat;
            if (random.NextDouble() < chance) Spawn();
        }
        Prune();
    }

    static void Spawn()
    {
        int index = next % texts.Length;
        next++;
        string text = texts[index];
        uint style = flags[index] | MB_OK | MB_SETFOREGROUND;

        Thread thread = new Thread(delegate ()
        {
            try { MessageBoxW(IntPtr.Zero, text, text, style); }
            catch (Exception) { }
        });
        thread.IsBackground = true;
        thread.Start();
        opened++;
    }

    // 屏幕上最多留 cap 个：超了就把最早的关掉（找到 #32770 对话框窗口发 WM_CLOSE）
    static void Prune()
    {
        List<IntPtr> now = new List<IntPtr>();
        uint self = GetCurrentProcessId();
        StringBuilder name = new StringBuilder(64);
        EnumWindows(delegate (IntPtr hwnd, IntPtr param)
        {
            if (!IsWindowVisible(hwnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != self) return true;
            name.Length = 0;
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "#32770") now.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        // 保留顺序：老窗口在原位置，新窗口追加到末尾，这样队列头就是最早那个
        List<IntPtr> ordered = new List<IntPtr>(live.Count + now.Count);
        foreach (IntPtr hwnd in live) if (now.Contains(hwnd)) ordered.Add(hwnd);
        foreach (IntPtr hwnd in now)
        {
            if (ordered.Contains(hwnd)) continue;
            ordered.Add(hwnd);
            MoveRandom(hwnd);
        }

        live.Clear();
        live.AddRange(ordered);

        while (live.Count > cap)
        {
            PostMessage(live[0], WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            live.RemoveAt(0);
            closed++;
        }
    }

    // 把刚出现的对话框搬到屏幕上的随机位置：整窗留在屏幕内，留 24 像素边距
    static void MoveRandom(IntPtr hwnd)
    {
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int screenW = GetSystemMetrics(0), screenH = GetSystemMetrics(1);
        int maxX = screenW - w - 24, maxY = screenH - h - 24;
        if (maxX < 24) maxX = 24;
        if (maxY < 24) maxY = 24;
        int x = 24 + random.Next(maxX - 24 + 1);
        int y = 24 + random.Next(maxY - 24 + 1);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    public static void Shutdown()
    {
        enabled = false;
        foreach (IntPtr hwnd in live) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        live.Clear();
    }
}
