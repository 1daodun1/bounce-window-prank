using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// 随机弹出会一直换配色的 CMD 窗口：里面循环执行 color 命令（背景色也跟着换）。
// 规则：你手动关掉任意一个这样的 CMD 窗口，就顺手结束掉一个"自动打开应用"拉起来的系统程序。
internal static class CmdColor
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
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static readonly List<Process> running = new List<Process>();
    static string batPath;

    public static int Count
    {
        get
        {
            Prune();
            return running.Count;
        }
    }

    static void Prune()
    {
        for (int i = running.Count - 1; i >= 0; i--)
        {
            try { if (running[i].HasExited) running.RemoveAt(i); }
            catch (Exception) { running.RemoveAt(i); }
        }
    }

    // 生成颜色循环脚本（纯 ASCII，省得 cmd 读中文编码出事）
    public static void Prepare(string dir)
    {
        try
        {
            batPath = Path.Combine(dir, "col_loop.bat");
            string body =
                "@echo off\r\n" +
                "title FRIENDS WINE\r\n" +
                ":loop\r\n" +
                "for %%p in (1f 2e 3d 4c 5b 6a 7e 8d 9c ab bc cd de ef f0 e0 d0 c0 b0 a0 9e 8d 7c 6b) do (\r\n" +
                "  color %%p\r\n" +
                "  echo.\r\n" +
                "  echo    FRIENDS  WINE    [ %%p ]\r\n" +
                "  ping -n 2 127.0.0.1 >nul\r\n" +
                ")\r\n" +
                "goto loop\r\n";
            File.WriteAllText(batPath, body, new System.Text.UTF8Encoding(false));
        }
        catch (Exception) { batPath = null; }
    }

    public static bool Start()
    {
        if (batPath == null || !File.Exists(batPath)) Prepare(Path.GetDirectoryName(batPath == null ? "." : batPath));
        if (batPath == null) return false;

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int w = 520, h = 300;
        int x = 60 + (int)(new Random().NextDouble() * Math.Max(1, sw - w - 120));
        int y = 60 + (int)(new Random().NextDouble() * Math.Max(1, sh - h - 160));

        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = 0x00000001 | 0x00000010 | 0x00000040;   // USESHOWWINDOW | USEPOSITION | USESIZE
        si.wShowWindow = 4;                                  // SW_SHOWNOACTIVATE
        si.dwX = x; si.dwY = y; si.dwXSize = w; si.dwYSize = h;

        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string cmdline = "\"" + cmdExe + "\" /c title 朋友的酒 & \"" + batPath + "\"";
        PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
        bool ok = CreateProcess(cmdExe, cmdline, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi);
        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        if (!ok)
        {
            // 万一 CreateProcess 不认，退回普通 Process.Start（位置就没那么准了，但窗口一定有）
            try
            {
                Process fallback = Process.Start(new ProcessStartInfo(cmdExe, "/c title 朋友的酒 & \"" + batPath + "\"")
                { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(batPath) });
                if (fallback == null) return false;
                running.Add(fallback);
                Thread watcher2 = new Thread(delegate()
                {
                    try { fallback.WaitForExit(); } catch (Exception) { }
                    try { PopWindows.KillOne(); } catch (Exception) { }
                    lock (running) { running.Remove(fallback); }
                });
                watcher2.IsBackground = true;
                watcher2.Start();
                return true;
            }
            catch (Exception) { return false; }
        }

        try
        {
            Process p = Process.GetProcessById(pi.dwProcessId);
            running.Add(p);
            Thread watcher = new Thread(delegate()
            {
                try { p.WaitForExit(); } catch (Exception) { }
                // 这个 CMD 被关掉了 → 随手结束一个自动打开的系统程序
                try { PopWindows.KillOne(); } catch (Exception) { }
                lock (running) { running.Remove(p); }
            });
            watcher.IsBackground = true;
            watcher.Start();
            return true;
        }
        catch (Exception) { return false; }
    }

    public static void Shutdown()
    {
        Prune();
        foreach (Process p in running.ToArray())
        {
            try
            {
                if (p.HasExited) continue;
                p.Kill();
            }
            catch (Exception) { }
        }
        running.Clear();
    }
}
