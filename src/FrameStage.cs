
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// mp4 拆成的 25fps 图片序列，按音乐位置直接画到屏幕上（GDI 特效的做法）。
// 素材优先读程序目录下的「视频帧」文件夹（一张张 jpg），打包版读 frames.dat。
// 画在一层「桌面之上、窗口之下」的穿透分层窗口里：直接 BitBlt 屏幕 DC 会被桌面重绘擦掉，
// 分层窗口由 DWM 自己保存内容，25fps 更新才真的看得见。
internal static class FrameStage
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const uint ULW_ALPHA = 0x00000002;
    const int HWND_BOTTOM = 1;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    const uint SRCCOPY = 0x00CC0020;
    const int HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreateWindowExW(int exStyle, string cls, string name,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc,
        ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int colorKey,
        ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
        IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);

    static string[] files;
    static long[] offsets; static int[] lengths;
    static string datPath;
    static int count;
    static IntPtr screenDc, memDc, hwnd;
    static Bitmap canvas; static Graphics canvasG;
    static int screenW, screenH;
    static double lastDrawAt = -1;
    static int lastIndex = -1;
    static bool ready;
    static int drawn;
    static DateTime lastBottom = DateTime.MinValue;
    static Func<double> positionSource;
    static System.Threading.Thread pump;

    // 帧序列自己跑一条 25fps 的线程：主循环被特效拖到 1fps 时画面也不会跟着卡死
    public static void SetPositionSource(Func<double> source) { positionSource = source; }

    public static bool Running { get { return ready; } }
    public static int FrameCount { get { return count; } }
    public static int Drawn { get { return drawn; } }

    public static bool Start(string dir)
    {
        try
        {
            string folder = Path.Combine(dir, "视频帧");
            if (Directory.Exists(folder))
            {
                List<string> list = new List<string>();
                foreach (string f in Directory.GetFiles(folder, "*.jpg")) list.Add(f);
                if (list.Count > 0)
                {
                    list.Sort(StringComparer.OrdinalIgnoreCase);
                    files = list.ToArray();
                    count = files.Length;
                }
            }

            if (count == 0)
            {
                datPath = Path.Combine(dir, "frames.dat");
                if (!LoadIndex(datPath)) return false;
            }

            screenW = GetSystemMetrics(0);
            screenH = GetSystemMetrics(1);
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return false;

            hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "Static", "朋友的酒帧画面", WS_POPUP, 0, 0, screenW, screenH,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
            {
                canvas = new Bitmap(screenW, screenH, PixelFormat.Format32bppPArgb);
                canvasG = Graphics.FromImage(canvas);
                canvasG.Clear(Color.Black);
                // 挂到壁纸那层 WorkerW 下面：压在壁纸之上、桌面图标与所有窗口之下
                IntPtr worker = DesktopHost.FindWorkerW();
                // 插到壁纸窗口的正上方：压住壁纸，又低于桌面图标和所有普通窗口
                SetWindowPos(hwnd, worker, 0, 0, screenW, screenH, SWP_NOACTIVATE);
                ShowWindow(hwnd, 8);   // SW_SHOWNOACTIVATE
                try
                {
                    RECT r;
                    GetWindowRect(hwnd, out r);
                    File.WriteAllText(@"E:\tb\fs_diag.txt",
                        "hwnd=" + hwnd + " worker=" + worker + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom
                        + " screen=" + screenW + "x" + screenH + "\r\n");
                }
                catch (Exception) { }
            }

            ready = true;
            pump = new System.Threading.Thread(Pump);
            pump.IsBackground = true;
            pump.Name = "frame 25fps";
            pump.Start();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static void Pump()
    {
        while (ready)
        {
            try
            {
                double pos = positionSource != null ? positionSource() : -1;
                if (pos >= 0) Draw(pos);
            }
            catch (Exception) { }
            System.Threading.Thread.Sleep(40);
        }
    }

    static bool LoadIndex(string path)
    {
        if (!File.Exists(path)) return false;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] head = new byte[4];
            if (fs.Read(head, 0, 4) != 4) return false;
            if (head[0] != 'F' || head[1] != 'R' || head[2] != 'M' || head[3] != 'D') return false;
            int n = ReadInt(fs);
            if (n <= 0 || n > 200000) return false;
            offsets = new long[n];
            lengths = new int[n];
            long pos = 8;
            for (int i = 0; i < n; i++)
            {
                int len = ReadInt(fs);
                if (len <= 0) return false;
                offsets[i] = pos + 4;
                lengths[i] = len;
                pos = offsets[i] + len;
                fs.Position = pos;
            }
            count = n;
            return true;
        }
    }

    static int ReadInt(FileStream fs)
    {
        byte[] b = new byte[4];
        if (fs.Read(b, 0, 4) != 4) return -1;
        return BitConverter.ToInt32(b, 0);
    }

    // 按音乐位置取帧：25 帧/秒，取不到就跳过这一拍
    public static void Draw(double position)
    {
        if (!ready) return;
        if (position < 0) return;
        if (position - lastDrawAt < 0.04) return;    // 25fps
        lastDrawAt = position;
        int idx = (int)Math.Floor(position * 25.0) % count;
        if (idx < 0) idx += count;
        if (idx == lastIndex) return;
        lastIndex = idx;

        Bitmap frame = null;
        try
        {
            if (files != null)
            {
                frame = new Bitmap(files[idx]);
            }
            else
            {
                byte[] blob = new byte[lengths[idx]];
                using (FileStream fs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Position = offsets[idx];
                    int got = 0;
                    while (got < blob.Length)
                    {
                        int n = fs.Read(blob, got, blob.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }
                if (blob.Length == 0) return;
                frame = new Bitmap(new MemoryStream(blob));
            }
            if (frame == null) return;

            if (hwnd != IntPtr.Zero && canvas != null)
            {
                canvasG.DrawImage(frame, 0, 0, screenW, screenH);
                Present();
            }
            else
            {
                // 没有分层窗口就退回直接贴屏幕
                IntPtr hbitmap = frame.GetHbitmap();
                IntPtr old = SelectObject(memDc, hbitmap);
                SetStretchBltMode(screenDc, HALFTONE);
                StretchBlt(screenDc, 0, 0, screenW, screenH, memDc, 0, 0, frame.Width, frame.Height, SRCCOPY);
                SelectObject(memDc, old);
                DeleteObject(hbitmap);
            }
            drawn++;
        }
        catch (Exception) { }
        finally
        {
            if (frame != null) frame.Dispose();
        }
    }

    // 把画好的整屏位图交给 DWM，并保持它压在桌面之上、所有窗口之下
    static void Present()
    {
        IntPtr hdc = canvasG.GetHdc();
        try
        {
            POINT dst = new POINT(); SIZE size = new SIZE();
            size.cx = screenW; size.cy = screenH;
            POINT src = new POINT();
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = 0; blend.SourceConstantAlpha = 255; blend.AlphaFormat = 1; // AC_SRC_ALPHA
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, hdc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            canvasG.ReleaseHdc(hdc);
        }

        if ((DateTime.Now - lastBottom).TotalSeconds > 1.0)
        {
            lastBottom = DateTime.Now;
            SetWindowPos(hwnd, DesktopHost.FindWorkerW(), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static void Shutdown()
    {
        ready = false;
        positionSource = null;
        pump = null;
        files = null;
        offsets = null;
        lengths = null;
        count = 0;
        lastIndex = -1;
        lastDrawAt = -1;
        if (canvasG != null) { canvasG.Dispose(); canvasG = null; }
        if (canvas != null) { canvas.Dispose(); canvas = null; }
        if (hwnd != IntPtr.Zero) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        if (memDc != IntPtr.Zero) { DeleteDC(memDc); memDc = IntPtr.Zero; }
        if (screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screenDc); screenDc = IntPtr.Zero; }
    }
}
