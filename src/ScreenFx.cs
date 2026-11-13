using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

// 屏幕级 GDI 特效：整屏抖动 / 反色 / 残影，抖动与反色可以同时存在。
// 做法跟 BV1xEKB6KEvH 里的 pywin32 例子一致：GetDC(0) 拿整块屏幕的 DC，再用 BitBlt / PatBlt / AlphaBlend 直接刷。
internal static class ScreenFx
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int xs, int ys, uint rop);
    [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, int planes, int bits, IntPtr bits2);
    [DllImport("gdi32.dll")] static extern int SetBkColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr flicker, uint flags);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
    [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION fn);

    const uint SRCCOPY = 0x00CC0020;
    const uint SRCAND = 0x008800C6;
    const uint SRCPAINT = 0x00EE0086;
    const uint DSTINVERT = 0x00550009;
    const uint RDW_INVALIDATE = 0x0001;
    const uint RDW_ERASE = 0x0004;
    const uint RDW_ALLCHILDREN = 0x0080;
    const uint RDW_UPDATENOW = 0x0100;

    const double SHAKE_SEC = 0.12;
    const double INVERT_SEC = 0.12;
    const double GHOST_SEC = 0.30;
    const double SLICE_SEC = 0.22;      // 切条错位持续多久
    const int DI_NORMAL = 0x0003;
    const int DI_MASK = 0x0001;
    const int DI_IMAGE = 0x0002;
    const double TEAR_AT_SEC = 1.2;    // 一开场就进入常驻撕裂，不再等 47 秒
    const int STRIP_TARGET = 14;        // 想切成多少条，高度按屏幕算

    static bool enabled, shakeOn, invertOn, ghostOn, sliceOn;
    static int px = 3;
    static double beatLen = 0.4615;

    static int w, h;
    static IntPtr screenDc, memDc, memBmp, oldBmp;     // 干净快照
    static IntPtr invDc, invBmp, oldInvBmp;            // 反色快照（从干净快照翻出来）

    static int lastBeat = -1;
    static double lastStamp;   // 上次往快照上盖图标的时间
    static double shakeUntil, invertUntil, ghostUntil, sliceUntil;
    static bool dirty;          // 屏幕上此刻有我们画的脏东西，结束时要贴回干净快照
    static double lastApply;
    static int shakes, inverts, ghosts, slices, lastSign = 1;
    static int sliceFrames;
    static int applies, bitbltFails, invertOk, invertFail, bothFrames;
    static bool invReady;
    static bool melt, meltReady;   // 撕裂模式：只拍一次快照，之后一直切条，不整屏还原
    static readonly Random random = new Random();
    // 预渲染的透明图标块：每个图标一张彩色图 + 一张单色掩码图，贴的时候 SRCAND + SRCPAINT 两遍，背景透出去
    static readonly int TILE = 96;
    static IntPtr[] tileColorDc, tileMaskDc, tileColorBmp, tileMaskBmp, tileColorOld, tileMaskOld;
    static int tileCount;
    static IntPtr whiteBrush, blackBrush;
    static List<IntPtr> noiseIcons;  // System32\imageres.dll、shell32.dll 里的系统图标，当彩色噪点的“像素”
    static IntPtr[] noiseBrushes;    // 彩色小方块用的刷子

    public static bool Acquired { get { return screenDc != IntPtr.Zero; } }
    public static int Applies { get { return applies; } }
    public static int BitbltFails { get { return bitbltFails; } }
    public static int InvertOk { get { return invertOk; } }
    public static int InvertFail { get { return invertFail; } }
    public static int BothFrames { get { return bothFrames; } }

    public static int Shakes { get { return shakes; } }
    public static int Inverts { get { return inverts; } }
    public static int Ghosts { get { return ghosts; } }
    public static int Slices { get { return slices; } }
    public static int SliceFrames { get { return sliceFrames; } }

    public static void Configure(double beat, int pixels, bool shake, bool invert, bool ghost, bool slice)
    {
        beatLen = beat > 0.05 ? beat : 0.4615;
        px = pixels < 1 ? 1 : (pixels > 12 ? 12 : pixels);
        shakeOn = shake;
        invertOn = invert;
        ghostOn = ghost;
        sliceOn = slice;
        melt = slice;
        enabled = shake || invert || ghost || slice;
        lastBeat = -1;
    }

    static void Acquire()
    {
        if (screenDc != IntPtr.Zero) return;
        w = GetSystemMetrics(0);
        h = GetSystemMetrics(1);
        screenDc = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screenDc);
        memBmp = CreateCompatibleBitmap(screenDc, w, h);
        oldBmp = SelectObject(memDc, memBmp);
        invDc = CreateCompatibleDC(screenDc);
        invBmp = CreateCompatibleBitmap(screenDc, w, h);
        oldInvBmp = SelectObject(invDc, invBmp);
    }

    // 主循环每帧调用：按拍点决定这一拍要叠加哪几件事
    public static bool MeltActive { get { return melt && meltReady; } }

    public static void Tick(double position, double elapsed)
    {
        if (!enabled) return;
        Acquire();
        if (screenDc == IntPtr.Zero) return;

        if (melt && meltReady) return;      // 已进入常驻撕裂：不再还原、不再排班

        if (melt && position >= TEAR_AT_SEC && BurstDone(elapsed))
        {
            // 到点，且屏幕此刻没有未完的抖动，先把画面还原成干净的，再拍撕裂快照
            if (dirty) Restore();
            if (BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY))
            {
                invReady = BitBlt(invDc, 0, 0, w, h, memDc, 0, 0, SRCCOPY)
                    && PatBlt(invDc, 0, 0, w, h, DSTINVERT);
            }
            else bitbltFails++;
            meltReady = true;
            dirty = false;
            slices++;
            // 快照上先铺满系统图标：切条时它们会跟着条带一起左右漂，全屏都是
            StampNoise(90);
            lastStamp = elapsed;
            return;
        }

        int beat = (int)Math.Floor(position / beatLen);
        if (beat != lastBeat)
        {
            lastBeat = beat;
            if (BurstDone(elapsed))
            {
                // 屏幕此刻是干净的，先拍一张快照，再从快照翻出一张反色版
                if (BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY))
                {
                    invReady = BitBlt(invDc, 0, 0, w, h, memDc, 0, 0, SRCCOPY)
                        && PatBlt(invDc, 0, 0, w, h, DSTINVERT);
                }
                else bitbltFails++;

                dirty = false;
                lastApply = -1;
                if (shakeOn) { shakeUntil = elapsed + SHAKE_SEC; shakes++; }
                if (invertOn) { invertUntil = elapsed + INVERT_SEC; inverts++; if (invReady) invertOk++; else invertFail++; }
                if (ghostOn) { ghostUntil = elapsed + GHOST_SEC; ghosts++; }
                if (sliceOn) { sliceUntil = elapsed + SLICE_SEC; slices++; }
            }
        }

        if (dirty && BurstDone(elapsed)) Restore();
    }

    static bool BurstDone(double elapsed)
    {
        return elapsed >= shakeUntil && elapsed >= invertUntil && elapsed >= ghostUntil;
    }

    static void Restore()
    {
        // 把拍摄的干净画面按原位置贴回去，再让窗口重画一遍
        BitBlt(screenDc, 0, 0, w, h, memDc, 0, 0, SRCCOPY);
        Repaint();
        dirty = false;
    }

    static void Repaint()
    {
        RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
        RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    // 收尾时把整屏所有窗口标脏重画：特效直接写过屏幕，被盖住的窗口不会自己重画，
    // 不主动刷一次就会留下黑块。
    public static void RepaintAll()
    {
        RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | 0x0080 | RDW_UPDATENOW);
        RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | 0x0080 | RDW_UPDATENOW);
    }

    // 每帧驱动：抖动和反色可以在同一帧叠加（抖动的源换成反色快照）
    public static void Drive(double elapsed)
    {
        if (!enabled || screenDc == IntPtr.Zero) return;

        if (melt && meltReady)
        {
            if (elapsed - lastStamp > 0.8)
            {
                StampNoise(14);         // 持续往快照里加图标，桌面越来越“脏”
                lastStamp = elapsed;
            }
            dirty = true;
            ChaosFrame();
            applies++; sliceFrames++;
            return;
        }

        bool inv = invertOn && elapsed < invertUntil;
        bool sh = shakeOn && elapsed < shakeUntil;
        bool gh = ghostOn && elapsed < ghostUntil;
        bool sl = sliceOn && elapsed < sliceUntil;
        if (!inv && !sh && !gh && !sl) return;

        if (sl)
        {
            lastApply = elapsed;
            dirty = true;
            ChaosFrame();
            applies++; sliceFrames++;
            return;
        }

        if (elapsed - lastApply < 0.04) return;
        lastApply = elapsed;

        IntPtr src = inv ? invDc : memDc;
        dirty = true;

        if (sh)
        {
            lastSign = -lastSign;
            int dx = lastSign * px, dy = (lastSign > 0 ? px : -px);
            if (BitBlt(screenDc, dx, dy, w, h, src, 0, 0, SRCCOPY)) applies++; else bitbltFails++;
            if (inv) bothFrames++;
        }
        else if (inv)
        {
            if (BitBlt(screenDc, 0, 0, w, h, src, 0, 0, SRCCOPY)) applies++; else bitbltFails++;
        }

        if (gh)
        {
            BLENDFUNCTION fn = new BLENDFUNCTION();
            fn.BlendOp = 0; fn.BlendFlags = 0; fn.SourceConstantAlpha = 190; fn.AlphaFormat = 0;
            if (AlphaBlend(screenDc, px, 0, w, h, src, 0, 0, w, h, fn)) applies++;
        }
    }

    // 块状乱码：把快照切成一块块随机大小的矩形，随机搬到屏幕上别的位置，
    // 部分块用反色快照，部分块拿系统图标（imageres.dll / shell32.dll）平铺成花纹。
    // 弹窗（前台窗口）那块保持原样，所以弹窗不会被撕碎。
    static void ChaosFrame()
    {
        InitNoise();
        RECT fg;
        bool fgOk = ForegroundRect(out fg);

        for (int y = 0; y < h; )
        {
            int bh = random.Next(46, 132);
            if (y + bh > h) bh = h - y;

            for (int x = 0; x < w; )
            {
                int bw = random.Next(56, 172);
                if (x + bw > w) bw = w - x;

                bool overFg = fgOk && x < fg.Right && x + bw > fg.Left && y < fg.Bottom && y + bh > fg.Top;
                if (!overFg)
                {
                    int mode = random.Next(100);
                    if (mode < 34 && tileCount > 0)
                    {
                        TileIconPatch(x, y, bw, bh);       // 图标平铺块
                    }
                    else
                    {
                        bool inv = invReady && mode < 90;
                        IntPtr src = inv ? invDc : memDc;
                        int sx = random.Next(0, Math.Max(1, w - bw));
                        int sy = random.Next(0, Math.Max(1, h - bh));
                        if (!BitBlt(screenDc, x, y, bw, bh, src, sx, sy, SRCCOPY)) bitbltFails++;
                    }
                }
                x += bw;
            }
            y += bh;
        }
    }

    // 一块地方用系统图标平铺满，做成那种彩色花纹的样子
    static void TileIconPatch(int x, int y, int bw, int bh)
    {
        const int size = 20;                      // 固定 20 像素，抽搐时也不放大
        for (int yy = y; yy < y + bh; yy += size)
            for (int xx = x; xx < x + bw; xx += size)
            {
                int idx = random.Next(tileCount);
                DrawIconEx(screenDc, xx, yy, noiseIcons[idx], size, size, 0, IntPtr.Zero, DI_NORMAL);
            }
    }

    // 一帧撕裂：把干净快照横切成若干条，每条单独左右偏一点，部分条用反色快照；
    // 偏出去露出来的空白用系统图标（警告/报错/问号/信息）+ 彩色小方块填成噪点。
    static void SliceFrame()
    {
        InitNoise();
        int baseH = Math.Max(16, h / 18);
        int maxShift = px * 12;

        RECT fg;
        bool fgOk = ForegroundRect(out fg);

        for (int y = 0; y < h; )
        {
            int height = Math.Min(baseH + random.Next(0, baseH + 8), h - y);
            int dx = random.Next(-maxShift, maxShift + 1);
            if (random.Next(3) == 0) dx = 0;
            bool invertStrip = invReady && random.Next(100) < 30;
            IntPtr src = invertStrip ? invDc : memDc;

            // 弹窗（前台窗口）占着的那些行：只贴它左右两边，弹窗自己保持原样
            if (fgOk && y < fg.Bottom && y + height > fg.Top)
            {
                BlitPart(src, 0, y, fg.Left, height, dx);
                BlitPart(src, fg.Right, y, w - fg.Right, height, dx);
            }
            else BlitPart(src, 0, y, w, height, dx);

            // 缝隙留着露底（图标已经在快照里，会跟着条带一起漂），这里不再贴掩码图标
            if (false)
            {
                if (dx > 0) Noise(0, y, dx, height);
                else if (dx < 0) Noise(w + dx, y, -dx, height);
            }

            y += height;
        }

        // 透明图标已经盖进快照里，跟着条带一起漂；不再逐帧往屏幕上撒（屏幕 DC 掩码贴太慢）
    }

    // 往快照（memDc）上随机位置盖图标：盖进去的图标会跟着条带一起漂移，不会被每帧重刷抹掉
    static void StampNoise(int count)
    {
        if (tileCount == 0) return;

        RECT fg;
        bool fgOk = ForegroundRect(out fg);

        for (int i = 0; i < count; i++)
        {
            const int size = 20;                      // 固定小号，抽搐时不变
            if (size > w || size > h) continue;
            int dx = random.Next(w - size);
            int dy = random.Next(h - size);

            if (fgOk && dx + size > fg.Left && dx < fg.Right && dy + size > fg.Top && dy < fg.Bottom) continue;

            DrawTile(memDc, dx, dy, 0, 0, w, h, random.Next(tileCount), size);
        }
    }

    // 随机位置撒图标块：这就是“GUI 病毒铺满桌面”的那种分布
    static void ScatterIcons(RECT fg, bool fgOk)
    {
        if (tileCount == 0) return;
        int count = 20 + random.Next(20);
        for (int i = 0; i < count; i++)
        {
            const int size = 20;                      // 固定小号
            if (size > w || size > h) continue;
            int dx = random.Next(w - size);
            int dy = random.Next(h - size);

            if (fgOk)
            {
                int left = dx, top = dy, right = dx + size, bottom = dy + size;
                if (right > fg.Left && left < fg.Right && bottom > fg.Top && top < fg.Bottom) continue;
            }

            DrawTile(screenDc, dx, dy, 0, 0, w, h, random.Next(tileCount), size);
        }
    }

    // 前台窗口（弹窗）的矩形；拿不到就返回 false
    static bool ForegroundRect(out RECT rect)
    {
        rect = new RECT();
        IntPtr fore = GetForegroundWindow();
        if (fore == IntPtr.Zero) return false;
        GetWindowRect(fore, out rect);
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

        // 前台窗口几乎铺满整屏（最大化、全屏浏览器）：左右两边剩不下任何像素，
        // 再按它切两半就等于什么都不贴，所以这种情况直接不当弹窗处理。
        if (rect.Right - rect.Left > w * 0.88 && rect.Bottom - rect.Top > h * 0.88) return false;
        return true;
    }

    // 只贴 [x, x+nw) 这一段，避开前台弹窗，也处理偏移出屏幕的边
    static void BlitPart(IntPtr src, int x, int y, int nw, int height, int dx)
    {
        if (nw <= 0) return;
        int targetX = x + dx, sourceX = x, width = nw;
        if (targetX < 0) { sourceX -= targetX; width += targetX; targetX = 0; }
        if (targetX + width > w) width = w - targetX;
        if (width <= 0) return;
        if (!BitBlt(screenDc, targetX, y, width, height, src, sourceX, y, SRCCOPY)) bitbltFails++;
    }

    static void InitNoise()
    {
        if (noiseIcons != null) return;
        noiseIcons = new List<IntPtr>();

        // 系统自带图标：imageres.dll（磁盘清理、默认程序等），再补 shell32.dll
        string systemDir = Environment.SystemDirectory;
        AddIcons(Path.Combine(systemDir, "imageres.dll"), 0, 72);
        AddIcons(Path.Combine(systemDir, "shell32.dll"), 0, 32);

        if (noiseIcons.Count == 0)
        {
            int[] ids = { 32515, 32513, 32514, 32516 };
            for (int i = 0; i < ids.Length; i++)
            {
                IntPtr icon = LoadIcon(IntPtr.Zero, new IntPtr(ids[i]));
                if (icon != IntPtr.Zero) noiseIcons.Add(icon);
            }
        }
        if (noiseIcons.Count == 0) return;

        whiteBrush = CreateSolidBrush(unchecked((int)0x00FFFFFF));
        blackBrush = CreateSolidBrush(unchecked((int)0x00000000));

        // 每个图标做两张图：彩色图 + 单色掩码图。贴的时候先 SRCAND 再 SRCPAINT，
        // 黑色和透明的地方都被掩掉，只留下图标本身，底下的画面照样透出来。
        int n = Math.Min(noiseIcons.Count, 64);
        tileCount = n;
        tileColorDc = new IntPtr[n];
        tileMaskDc = new IntPtr[n];
        tileColorBmp = new IntPtr[n];
        tileMaskBmp = new IntPtr[n];
        tileColorOld = new IntPtr[n];
        tileMaskOld = new IntPtr[n];

        RECT all;
        all.Left = 0; all.Top = 0; all.Right = TILE; all.Bottom = TILE;

        for (int i = 0; i < n; i++)
        {
            IntPtr icon = noiseIcons[i];

            tileColorDc[i] = CreateCompatibleDC(screenDc);
            tileColorBmp[i] = CreateCompatibleBitmap(screenDc, TILE, TILE);
            tileColorOld[i] = SelectObject(tileColorDc[i], tileColorBmp[i]);
            FillRect(tileColorDc[i], ref all, blackBrush);
            SetBkColor(tileColorDc[i], unchecked((int)0x00000000));
            DrawIconEx(tileColorDc[i], 0, 0, icon, TILE, TILE, 0, IntPtr.Zero, DI_NORMAL);

            tileMaskDc[i] = CreateCompatibleDC(screenDc);
            tileMaskBmp[i] = CreateBitmap(TILE, TILE, 1, 1, IntPtr.Zero);
            tileMaskOld[i] = SelectObject(tileMaskDc[i], tileMaskBmp[i]);
            FillRect(tileMaskDc[i], ref all, whiteBrush);
            SetBkColor(tileMaskDc[i], unchecked((int)0x00FFFFFF));
            DrawIconEx(tileMaskDc[i], 0, 0, icon, TILE, TILE, 0, IntPtr.Zero, DI_MASK);
        }
    }

    // 把第 ti 个图标贴到 dst，按 [clipL, clipR)×[clipT, clipB) 裁剪；背景透明
    static void DrawTile(IntPtr dst, int x, int y, int clipL, int clipT, int clipR, int clipB, int ti, int size)
    {
        if (ti < 0 || ti >= tileCount) return;

        int sx = 0, sy = 0, dx = x, dy = y, ww = size, hh = size;   // 按调用方给的尺寸画
        if (dx < clipL) { int cut = clipL - dx; sx += cut; ww -= cut; dx = clipL; }
        if (dy < clipT) { int cut = clipT - dy; sy += cut; hh -= cut; dy = clipT; }
        if (dx + ww > clipR) ww = clipR - dx;
        if (dy + hh > clipB) hh = clipB - dy;
        if (ww <= 0 || hh <= 0) return;

        // 直接用 DrawIconEx 的 DI_NORMAL：系统自己按图标的掩码抠底，不会贴出彩色方块
        DrawIconEx(dst, dx, dy, noiseIcons[ti], ww, hh, 0, IntPtr.Zero, DI_NORMAL);
    }

    // 条带偏移露出来的空白：只在这个矩形里贴透明图标
    static void Noise(int x, int y, int nw, int nh)
    {
        if (nw <= 0 || nh <= 0 || tileCount == 0) return;

        int step = Math.Max(40, nh / 2);
        for (int py = y; py < y + nh && py - y < 130; py += step)
        {
            const int size = 20;
            int ox = x - size / 3 + random.Next(Math.Max(1, size));
            int oy = py + random.Next(Math.Max(1, step));
            DrawTile(screenDc, ox, oy, x, y, x + nw, y + nh, random.Next(tileCount), size);
        }
    }

    static void AddIcons(string file, int from, int to)
    {
        if (!File.Exists(file)) return;
        for (int i = from; i <= to; i++)
        {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                if (ExtractIconEx(file, i, out large, out small, 1) <= 0) continue;
            }
            catch (Exception) { continue; }
            if (large != IntPtr.Zero) noiseIcons.Add(large);
            else if (small != IntPtr.Zero) noiseIcons.Add(small);
        }
    }

    public static void Shutdown()
    {
        if (!enabled) return;
        if (dirty) Restore();
        Repaint();
        if (oldBmp != IntPtr.Zero) { SelectObject(memDc, oldBmp); oldBmp = IntPtr.Zero; }
        if (memBmp != IntPtr.Zero) { DeleteObject(memBmp); memBmp = IntPtr.Zero; }
        if (memDc != IntPtr.Zero) { DeleteDC(memDc); memDc = IntPtr.Zero; }
        for (int i = 0; i < tileCount; i++)
        {
            if (tileColorOld[i] != IntPtr.Zero) { SelectObject(tileColorDc[i], tileColorOld[i]); tileColorOld[i] = IntPtr.Zero; }
            if (tileMaskOld[i] != IntPtr.Zero) { SelectObject(tileMaskDc[i], tileMaskOld[i]); tileMaskOld[i] = IntPtr.Zero; }
            if (tileColorBmp[i] != IntPtr.Zero) { DeleteObject(tileColorBmp[i]); tileColorBmp[i] = IntPtr.Zero; }
            if (tileMaskBmp[i] != IntPtr.Zero) { DeleteObject(tileMaskBmp[i]); tileMaskBmp[i] = IntPtr.Zero; }
            if (tileColorDc[i] != IntPtr.Zero) { DeleteDC(tileColorDc[i]); tileColorDc[i] = IntPtr.Zero; }
            if (tileMaskDc[i] != IntPtr.Zero) { DeleteDC(tileMaskDc[i]); tileMaskDc[i] = IntPtr.Zero; }
        }
        tileCount = 0;
        if (whiteBrush != IntPtr.Zero) { DeleteObject(whiteBrush); whiteBrush = IntPtr.Zero; }
        if (blackBrush != IntPtr.Zero) { DeleteObject(blackBrush); blackBrush = IntPtr.Zero; }
        if (oldInvBmp != IntPtr.Zero) { SelectObject(invDc, oldInvBmp); oldInvBmp = IntPtr.Zero; }
        if (invBmp != IntPtr.Zero) { DeleteObject(invBmp); invBmp = IntPtr.Zero; }
        if (invDc != IntPtr.Zero) { DeleteDC(invDc); invDc = IntPtr.Zero; }
        if (screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screenDc); screenDc = IntPtr.Zero; }
        enabled = false;
    }
}
