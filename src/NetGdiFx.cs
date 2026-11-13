using System;
using System.Diagnostics;
using System.IO;

// NetGDI 七种特效（反色 / 模糊 / 圆形隧道 / 阴影画刷 / 图案画刷 / 颜色滤镜 / 屏幕融化）
// 用同目录下的 NgdiRun.exe 单独进程跑：实测放进主进程里会被我们自己的绘制压掉、看不出来。
internal static class NetGdiFx
{
    static Process host;
    static bool active;

    public static bool Active { get { return active; } }

    public static bool Enable(string dir)
    {
        string exe = Path.Combine(dir, "NgdiRun.exe");
        string dll = Path.Combine(dir, "NetGDI.dll");
        if (!File.Exists(exe) || !File.Exists(dll)) return false;
        try
        {
            if (host != null) { try { host.Kill(); } catch (Exception) { } host = null; }
            ProcessStartInfo si = new ProcessStartInfo(exe, Process.GetCurrentProcess().Id.ToString());
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.WorkingDirectory = dir;
            host = Process.Start(si);
            active = host != null;
            return active;
        }
        catch (Exception) { return false; }
    }

    public static void Disable()
    {
        active = false;
        if (host == null) return;
        try { if (!host.HasExited) host.Kill(); } catch (Exception) { }
        try { host.WaitForExit(1500); } catch (Exception) { }
        host = null;
    }
}
