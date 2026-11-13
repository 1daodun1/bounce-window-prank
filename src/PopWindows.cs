using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

// 跟着拍点弹窗：从歌曲某个时间点开始，每隔 1~5 秒（吸附到拍点上）随机启动一个
// System32 里的无害 GUI 程序；同时在屏的窗口超过上限就先关掉最早那个。
internal static class PopWindows
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, string command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    // 全是纯 GUI、不带参数也不会改系统的程序（控制台工具、分区/磁盘/BCD 那一类坚决不放）
    // 只留“一打开就有内容、看得见、纯 GUI”的程序。
    // mmc / *.msc / SystemProperties* 这类会开一堆空白控制台窗口，看着像故障，全部剔除。
    static readonly string[] names =
    {
        "notepad.exe", "mspaint.exe", "calc.exe", "charmap.exe", "osk.exe",
        "magnify.exe", "dxdiag.exe", "msinfo32.exe", "winver.exe", "snippingtool.exe",
        "taskmgr.exe", "cleanmgr.exe", "resmon.exe", "regedit.exe", "write.exe",
        "control.exe", "mstsc.exe", "eventvwr.exe", "perfmon.exe"
    };
    static List<string> pool = new List<string>();
    static readonly List<Process> spawned = new List<Process>();
    static readonly List<string> lastSpawned = new List<string>();
    static Random random = new Random();
    static double startAt = 33, beatLength = 0.4615, chance = 0.3, cycle = 0.923;
    static int lastBeat = -1;
    static int maxWindows = 5;
    static int opened;
    static readonly HashSet<IntPtr> baseline = new HashSet<IntPtr>();

    public static int Total { get { return opened; } }
    public static int PoolSize { get { return pool.Count; } }

    public static void Configure(double spawnAt, double beatSeconds, double openChance, double cycleSeconds, int cap)
    {
        startAt = spawnAt;
        beatLength = beatSeconds > 0.05 ? beatSeconds : 0.4615;
        chance = openChance < 0 ? 0 : (openChance > 1 ? 1 : openChance);
        cycle = cycleSeconds > 0.05 ? cycleSeconds : 0.923;
        maxWindows = Math.Max(1, cap);

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        pool.Clear();
        foreach (string name in names)
        {
            string full = Path.Combine(dir, name);
            if (File.Exists(full)) pool.Add(full);
        }

        // 记下开跑前已经开着的同名单窗口（比如你自己打开的记事本），这些不参与计数也不关
        baseline.Clear();
        foreach (IntPtr hwnd in FindPoolWindows()) baseline.Add(hwnd);
    }

    public static void Tick(double position)
    {
        if (pool.Count == 0) return;
        Prune();

        if (position < startAt) return;

        // 决定点就是拍点：每个 BPM 拍掷一次骰子，掷中了才开
        int beat = (int)Math.Floor(position / beatLength);
        if (beat == lastBeat) return;
        lastBeat = beat;
        if (random.NextDouble() >= chance) return;

        // 按"屏幕上还剩几个我们开的窗口"来算，而不是按进程数：
        // 一个 mmc.exe 可能带好几个 msc 窗口，Store 应用又会换进程名
        List<IntPtr> open = FindPoolWindows();
        while (open.Count >= maxWindows)
        {
            IntPtr victim = open[open.Count - 1];   // 枚举顺序是前后叠放，最后一个通常是最早开的
            PostMessage(victim, 0x0010, IntPtr.Zero, IntPtr.Zero);
            open.RemoveAt(open.Count - 1);
            System.Threading.Thread.Sleep(80);
        }
        Spawn();
        opened++;
    }

    public static void Shutdown()
    {
        foreach (Process process in spawned.ToArray())
        {
            try
            {
                if (process.HasExited) continue;
                if (!process.CloseMainWindow())
                {
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception) { }
        }
        spawned.Clear();

        // 有些 UWP/托盘型程序不吃 WM_CLOSE，等半秒后把还在的收掉（explorer 永远不动）
        System.Threading.Thread.Sleep(400);
        foreach (string path in lastSpawned)
        {
            if (path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Process[] procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
                foreach (Process process in procs)
                {
                    try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        lastSpawned.Clear();
    }

    // 找屏幕上属于白名单程序的可见顶层窗口
    static List<IntPtr> FindPoolWindows()
    {
        List<IntPtr> found = new List<IntPtr>();
        string[] poolNames = new string[pool.Count];
        for (int i = 0; i < pool.Count; i++) poolNames[i] = Path.GetFileName(pool[i]).ToLowerInvariant();

        EnumWindows(delegate(IntPtr hwnd, IntPtr param)
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (baseline.Contains(hwnd)) return true;
            System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, 256);
            string c = cls.ToString();
            // 任务栏、桌面、开始菜单这些外壳窗口绝对不能当成"弹窗"去关
            if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "Progman" || c == "WorkerW"
                || c == "Windows.UI.Core.CoreWindow" || c.StartsWith("ApplicationFrame")) return true;
            if (IsWindow(hwnd) && GetWindowTextLength(hwnd) == 0) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return true;
            string name = ExeNameOf((int)pid);
            if (name == null) return true;
            for (int i = 0; i < poolNames.Length; i++)
            {
                if (name == poolNames[i]) { found.Add(hwnd); break; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string ExeNameOf(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            return Path.GetFileName(process.MainModule.FileName).ToLowerInvariant();
        }
        catch (Exception) { return null; }
    }

    static void Prune()
    {
        for (int i = spawned.Count - 1; i >= 0; i--)
        {
            Process process = spawned[i];
            bool dead;
            try { dead = process.HasExited; } catch (Exception) { dead = true; }
            if (dead) spawned.RemoveAt(i);
        }
    }

    static int Alive()
    {
        int count = 0;
        foreach (Process process in spawned)
        {
            try { if (!process.HasExited) count++; } catch (Exception) { }
        }
        return count;
    }

    static void CloseOldest()
    {
        for (int i = 0; i < spawned.Count; i++)
        {
            Process process = spawned[i];
            try
            {
                if (process.HasExited) { spawned.RemoveAt(i); i--; continue; }
                // 不等待退出，免得卡住主循环；UWP 窗口 CloseMainWindow 常返回 false，补一条 WM_CLOSE
                if (!process.CloseMainWindow())
                {
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
                spawned.RemoveAt(i);
                return;
            }
            catch (Exception) { spawned.RemoveAt(i); i--; return; }
        }
    }

    // 关掉一个"自动打开应用"拉起来的程序；给"关掉变色 CMD 就结束一个"这条规则用
    public static bool KillOne()
    {
        Prune();
        for (int i = spawned.Count - 1; i >= 0; i--)
        {
            Process process = spawned[i];
            try
            {
                if (process.HasExited) { spawned.RemoveAt(i); continue; }
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!process.CloseMainWindow() && hwnd == IntPtr.Zero)
                {
                    try { process.Kill(); } catch (Exception) { }
                }
                spawned.RemoveAt(i);
                return true;
            }
            catch (Exception) { spawned.RemoveAt(i); }
        }
        return false;
    }

    static void Spawn()
    {
        string path = pool[random.Next(pool.Count)];
        STARTUPINFO startup = new STARTUPINFO();
        startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        // SW_SHOWNOACTIVATE：窗口照常出现，但不抢焦点，免得你打字被顶掉
        startup.dwFlags = 0x00000001;
        startup.wShowWindow = 4;
        PROCESS_INFORMATION info = new PROCESS_INFORMATION();
        bool ok = CreateProcess(path, null, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out info);
        if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
        if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
        if (!ok) return;
        try
        {
            Process process = Process.GetProcessById(info.dwProcessId);
            spawned.Add(process);
            if (!lastSpawned.Contains(path)) lastSpawned.Add(path);
        }
        catch (Exception) { }
    }
}
