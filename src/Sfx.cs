using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// 自解压启动器：把内嵌的 exe / 音频 / 视频解到 C 盘缓存目录，再从那里启动。
// 空间不够就弹一个报错框：标题和正文都是“朋友的酒 因内存不足而无法启动”。
internal static class Sfx
{
    // C 盘缓存目录：%LOCALAPPDATA%\朋友的酒
    const string CACHE_NAME = "朋友的酒";

    // 解出来的文件名（程序里要按这些名字找音频和视频）
    static readonly string[] OUT_NAMES =
    {
        "桌面弹弹弹.exe",
        "reference-audio.wav",
        "41834646747-1-192.mp4",
        "victim.jpg",
        "ending.wav",
        "TG频道预览\\TgPreview.exe",
        "NetGDI.dll",
        "NgdiRun.exe",
        "frames.dat"
    };

    // 内嵌资源名（编译时用 /resource 挂上来的名字）
    static readonly string[] RES_NAMES = { "app.exe", "audio.wav", "video.mp4", "victim.jpg", "ending.wav", "tgpv.exe", "ngdi.dll", "ngdi.exe", "frames.dat" };

    // 除了负载本身，再留 200 MB 给运行时用
    const long MARGIN_BYTES = 40L * 1024L * 1024L;

    const uint MB_ICONERROR = 0x00000010;
    const uint MB_OK = 0x00000000;
    const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [STAThread]
    static int Main(string[] argv)
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CACHE_NAME);

        Assembly asm = Assembly.GetExecutingAssembly();
        long payloadBytes = 0;
        try
        {
            for (int i = 0; i < RES_NAMES.Length; i++)
            {
                using (Stream s = asm.GetManifestResourceStream(RES_NAMES[i]))
                {
                    if (s == null) continue;
                    string target = Path.Combine(cacheDir, OUT_NAMES[i]);
                    try
                    {
                        FileInfo fi = new FileInfo(target);
                        if (fi.Exists && fi.Length == s.Length) continue;   // 已经解过，不占空间
                    }
                    catch (Exception) { }
                    payloadBytes += s.Length;
                }
            }
        }
        catch (Exception) { }

        if (payloadBytes <= 0) payloadBytes = 60L * 1024L * 1024L;

        // 先看空间，不够就直接报错，别解一半留下残骸
        try
        {
            string root = Path.GetPathRoot(cacheDir);
            if (string.IsNullOrEmpty(root)) root = "C:\\";
            DriveInfo drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < payloadBytes + MARGIN_BYTES)
            {
                Fail();
                return 1;
            }
        }
        catch (Exception)
        {
            // 读不到磁盘信息（比如盘符异常）时不拦，直接往下解，免得误报
        }

        try
        {
            Directory.CreateDirectory(cacheDir);
            for (int i = 0; i < RES_NAMES.Length; i++)
            {
                string target = Path.Combine(cacheDir, OUT_NAMES[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(target));   // 子目录（比如 TG频道预览\）
                using (Stream src = asm.GetManifestResourceStream(RES_NAMES[i]))
                {
                    if (src == null) throw new IOException("缺少内嵌资源：" + RES_NAMES[i]);
                    long expect = src.Length;
                    FileInfo fi = new FileInfo(target);
                    if (fi.Exists && fi.Length == expect) continue;      // 已经解过，跳过

                    using (FileStream dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                    }
                }
            }
        }
        catch (Exception)
        {
            Fail();
            return 2;
        }

        // TG 频道预览那个独立小工具要用自己文件夹里的这两份素材，从缓存根目录各拷一份过去
        try
        {
            string tgDir = Path.Combine(cacheDir, "TG频道预览");
            Directory.CreateDirectory(tgDir);
            foreach (string name in new string[] { "victim.jpg", "ending.wav" })
            {
                string from = Path.Combine(cacheDir, name);
                string to = Path.Combine(tgDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to, false);
            }
        }
        catch (Exception) { }

        try
        {
            string exe = Path.Combine(cacheDir, OUT_NAMES[0]);
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = cacheDir,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            Fail();
            return 3;
        }
        return 0;
    }

    static void Fail()
    {
        string text = "朋友的酒 因内存不足而无法启动";
        MessageBoxW(IntPtr.Zero, text, text, MB_ICONERROR | MB_OK | MB_SETFOREGROUND);
    }
}
