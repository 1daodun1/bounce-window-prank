using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Merge spatially close icons, splitting sparse layouts so empty desktop pixels
// do not turn a small animation into a full-screen texture upload.
//
// Frame cost follows what actually moved instead of the whole surface: the pixels a
// sprite covered last frame are restored from a static background copy, then the
// sprite is blitted 1:1 out of a per-size cache at integer coordinates. No per-frame
// resampling, no full-surface repaint, and no upload at all when nothing moved.
internal sealed class JellyLayer : NativeWindow, IDisposable
{
    const int SpriteCacheLimit = 64;
    // Windows 11 label fonts: Segoe UI Variable for Latin names, Microsoft YaHei UI for
    // Chinese ones - the same split Explorer uses for desktop icon captions.
    static readonly string LatinFace = PickFace("Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe UI", "Microsoft YaHei UI", "Tahoma");
    static readonly string CjkFace = PickFace("Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial");

    readonly IntPtr host;
    readonly List<DesktopItem> items;
    readonly Rectangle bounds;
    readonly Rectangle[] hits;
    readonly Rectangle[] drawn;
    readonly Rectangle[] touched;
    readonly int[] sizes;
    readonly Dictionary<long, Bitmap>[] sprites;
    IntPtr memory, dib, previous;
    Bitmap frame, background;
    Graphics painter;
    public double LastPaintMs, LastSubmitMs;
    public int LastChangedIcons { get; private set; }
    public int LastRedrawnIcons { get; private set; }
    // Measurement-only switch: tools/bench uses it to prove the trail detector fires.
    internal static bool ForTestDisableClear = false;

    static string PickFace(params string[] candidates)
    {
        List<string> installed = new List<string>();
        using (InstalledFontCollection fonts = new InstalledFontCollection())
            foreach (FontFamily family in fonts.Families) installed.Add(family.Name);
        foreach (string candidate in candidates) if (installed.Contains(candidate)) return candidate;
        return candidates[candidates.Length - 1];
    }
    static bool ContainsWide(string text)
    {
        foreach (char c in text) if (c >= 0x2E80) return true;
        return false;
    }
    public int PixelCount { get { return bounds.Width * bounds.Height; } }
    public Bitmap CaptureForTest() { return frame.Clone(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppPArgb); }

    public JellyLayer(IntPtr parent, List<DesktopItem> data)
    {
        host = parent; items = data; bounds = Union(data);
        hits = new Rectangle[data.Count]; drawn = new Rectangle[data.Count];
        touched = new Rectangle[data.Count]; sizes = new int[data.Count];
        sprites = new Dictionary<long, Bitmap>[data.Count];
        for (int i = 0; i < data.Count; i++) { sizes[i] = Size(data[i]); sprites[i] = new Dictionary<long, Bitmap>(32); }
    }
    // Explorer hands back the icon rect, which pads the real glyph by about 2 px on each
    // side (a 48 px desktop icon reports 52 px). Drawing the glyph itself keeps the bounce
    // the same size as the icon it replaces instead of slightly larger.
    const int IconPadding = 2;
    static int Size(DesktopItem item) { return Math.Max(16, Math.Min(256, item.Rect.Height - IconPadding * 2)); }
    static Rectangle SpriteBounds(DesktopItem item)
    {
        int size = Size(item), width = Math.Max(160, (int)Math.Ceiling(size * 1.468) + 8), high = (int)Math.Ceiling(size * 1.65) + 4;
        return new Rectangle(item.Rect.Left + item.Rect.Width / 2 - width / 2, item.Rect.Bottom - high, width, high + 56);
    }
    static Rectangle Union(List<DesktopItem> data)
    {
        Rectangle result = SpriteBounds(data[0]);
        for (int i = 1; i < data.Count; i++) result = Rectangle.Union(result, SpriteBounds(data[i]));
        return result;
    }
    public static List<JellyLayer> CreateGroups(IntPtr host, List<DesktopItem> data)
    {
        List<JellyLayer> result = new List<JellyLayer>();
        if (data.Count > 0) Split(host, new List<DesktopItem>(data), result);
        return result;
    }
    static void Split(IntPtr host, List<DesktopItem> data, List<JellyLayer> result)
    {
        Rectangle all = Union(data); long occupied = 0;
        foreach (DesktopItem item in data) { Rectangle r = SpriteBounds(item); occupied += (long)r.Width * r.Height; }
        if (data.Count == 1 || ((long)all.Width * all.Height <= occupied * 1.25 && (long)all.Width * all.Height <= 2500000))
        { result.Add(new JellyLayer(host, data)); return; }
        bool horizontal = all.Width >= all.Height;
        data.Sort(delegate(DesktopItem a, DesktopItem b) { return horizontal ? a.Rect.Left.CompareTo(b.Rect.Left) : a.Rect.Top.CompareTo(b.Rect.Top); });
        int half = data.Count / 2;
        Split(host, data.GetRange(0, half), result); Split(host, data.GetRange(half, data.Count - half), result);
    }
    public void Prepare()
    {
        CreateHandle(new CreateParams { Caption = "DesktopJellyBatch", Parent = host, Style = 0x40000000, ExStyle = 0x08080080, ClassStyle = 8,
            X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height });
        Native.BITMAPINFO info = new Native.BITMAPINFO();
        info.Header.Size = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)); info.Header.Width = bounds.Width;
        info.Header.Height = -bounds.Height; info.Header.Planes = 1; info.Header.BitCount = 32;
        IntPtr pixels; dib = Native.CreateDIBSection(IntPtr.Zero, ref info, 0, out pixels, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        memory = Native.CreateCompatibleDC(IntPtr.Zero);
        if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        previous = Native.SelectObject(memory, dib);
        frame = new Bitmap(bounds.Width, bounds.Height, bounds.Width * 4, PixelFormat.Format32bppPArgb, pixels);
        painter = Graphics.FromImage(frame);
        painter.InterpolationMode = InterpolationMode.HighQualityBilinear; painter.PixelOffsetMode = PixelOffsetMode.HighQuality;
        background = RenderLabels(bounds);
        painter.CompositingMode = CompositingMode.SourceCopy;
        painter.DrawImageUnscaled(background, 0, 0);
        painter.CompositingMode = CompositingMode.SourceOver;
        UpdatePhase(0, 0, false);
    }
    static readonly Point[] RimOffset = { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) };
    static Point[] RimShifts() { return RimOffset; }
    // GDI+ reserves room for the ellipsis on every line, so a 76 px cell wrapped the sixth
    // character onto the next line even though it still fits. Explorer breaks after the
    // last character that fits and only adds the ellipsis when the name really does not
    // fit in two lines, so the line list is built here by measurement.
    List<string> TokenizeCaption(string text)
    {
        List<string> tokens = new List<string>(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { tokens.Add(" "); i++; continue; }
            if (text[i] >= 0x2E80) { tokens.Add(text.Substring(i, 1)); i++; continue; }
            int start = i;
            while (i < text.Length && text[i] != ' ' && text[i] < 0x2E80) i++;
            tokens.Add(text.Substring(start, i - start));
        }
        return tokens;
    }
    string[] WrapCaption(Graphics g, string text, Font font, StringFormat measure, float width)
    {
        List<string> tokens = TokenizeCaption(text);
        List<string> lines = new List<string>(2);
        string line = "";
        int t = 0;
        while (t < tokens.Count)
        {
            string token = tokens[t];
            if (line.Length > 0 && g.MeasureString(line + token, font, new PointF(0, 0), measure).Width > width)
            {
                lines.Add(line.TrimEnd());
                line = "";
                if (token == " ") { t++; }
                if (lines.Count == 2) break;
                continue;
            }
            line += token;
            t++;
        }
        if (line.Length > 0 && lines.Count < 2) lines.Add(line.TrimEnd());
        if (lines.Count == 0) lines.Add("");
        if (t < tokens.Count)
        {
            int last = lines.Count - 1;
            string cut = lines[last].TrimEnd();
            while (cut.Length > 0 && g.MeasureString(cut + "…", font, new PointF(0, 0), measure).Width > width)
                cut = cut.Substring(0, cut.Length - 1);
            lines[last] = cut.TrimEnd() + "…";
        }
        return lines.ToArray();
    }
    Bitmap RenderLabels(Rectangle area)
    {
        Bitmap labels = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(labels))
        // Windows draws its desktop captions at one em of 12 px (9 pt at 96 dpi).
        // Asking GDI+ for points lets a non-96 dpi surface inflate the glyphs, which pushed
        // six-character names onto a third line; pixels pin it to the desktop's own size.
        using (Font latin = new Font(LatinFace, 12, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font cjk = new Font(CjkFace, 12, FontStyle.Regular, GraphicsUnit.Pixel))
        using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        using (StringFormat measure = StringFormat.GenericTypographic)
        {
            // Explorer draws its desktop captions with ClearType plus a 1 px dark rim.
            // A layered window cannot use ClearType (it needs a solid backdrop) and grid-fit
            // hinting makes 12 px glyphs look bolder than the real ones, so plain anti-alias
            // plus the same rim is the closest match.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (SolidBrush rim = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            foreach (DesktopItem item in items)
            {
                // Caption width follows the desktop cell, not the wider sprite box, so long
                // names wrap and ellipsize the way Explorer does instead of running over the
                // neighbouring cell.
                int caption = Math.Max(40, item.Rect.Width);
                int captionLeft = item.Rect.Left - area.Left + (item.Rect.Width - caption) / 2;
                Font font = ContainsWide(item.Name) ? cjk : latin;
                string[] lines = WrapCaption(g, item.Name, font, measure, caption);
                for (int n = 0; n < lines.Length; n++)
                {
                    // GDI+ reserves about 8 px of padding inside a layout box and silently drops
                    // the character that no longer fits, so the box is 8 px wider than the cell
                    // while the wrap decision above still uses the cell width. The extra 4 px
                    // on each side also keeps the real caption's ink 4 px under the icon and the
                    // two lines 13 px apart.
                    RectangleF label = new RectangleF(captionLeft - 4, item.Rect.Bottom - area.Top + 2 + n * 13, caption + 8, 16);
                    foreach (Point shift in RimShifts())
                    {
                        RectangleF at = label; at.Offset(shift.X, shift.Y);
                        g.DrawString(lines[n], font, rim, at, format);
                    }
                    g.DrawString(lines[n], font, Brushes.White, label, format);
                }
            }
        }
        return labels;
    }
    public void Show()
    {
        Native.ShowWindow(Handle, 4); Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x53); UpdatePhase(0, 0, false);
        Native.RECT r;
        if (!Native.IsWindowVisible(Handle) || Native.GetParent(Handle) != host || !Native.GetWindowRect(Handle, out r) || r.Right - r.Left != bounds.Width || r.Bottom - r.Top != bounds.Height)
            throw new Exception("动画层显示检查失败，已保留原图标。");
    }
    public void UpdatePhase(double phase, double amount, bool stagger)
    {
        long started = Stopwatch.GetTimestamp();
        int count = items.Count, changed = 0;
        for (int i = 0; i < count; i++)
        {
            DesktopItem item = items[i];
            double sx, sy;
            Motion.Scale(phase + (stagger ? item.Index * .13 : 0), amount, out sx, out sy);
            int iconSize = sizes[i];
            int w = (int)Math.Round(iconSize * sx); int h = (int)Math.Round(iconSize * sy);
            if (w < 1) w = 1; if (h < 1) h = 1;
            int x = (int)Math.Round(item.Rect.Left + item.Rect.Width * .5 - bounds.Left - w * .5);
            int y = item.Rect.Bottom - bounds.Top - IconPadding - h;
            Rectangle next = new Rectangle(x, y, w, h);
            hits[i] = next;
            if (next != drawn[i]) changed++;
        }
        LastChangedIcons = changed;
        if (changed == 0) { LastPaintMs = 0; LastSubmitMs = 0; LastRedrawnIcons = 0; return; }

        // Restore the union of old and new footprints, then repaint every sprite that
        // touches one of those rectangles. Sprites overlap by a few pixels, so the ones
        // that happen to sit under a restored area must be painted again in queue order.
        int touchedCount = 0;
        painter.CompositingMode = CompositingMode.SourceCopy;
        for (int i = 0; i < count; i++)
        {
            if (drawn[i].Width == 0 || hits[i] == drawn[i]) continue;
            Rectangle dirty = Rectangle.Union(drawn[i], hits[i]);
            dirty.Intersect(new Rectangle(0, 0, bounds.Width, bounds.Height));
            if (dirty.Width <= 0 || dirty.Height <= 0) continue;
            touched[touchedCount++] = dirty;
            if (!ForTestDisableClear) painter.DrawImage(background, dirty, dirty, GraphicsUnit.Pixel);
        }
        painter.CompositingMode = CompositingMode.SourceOver;
        int redrawn = 0;
        for (int i = 0; i < count; i++)
        {
            Rectangle r = hits[i];
            bool needed = r != drawn[i];
            if (!needed) for (int k = 0; k < touchedCount; k++) if (r.IntersectsWith(touched[k])) { needed = true; break; }
            if (needed) { painter.DrawImageUnscaled(Sprite(i, r.Width, r.Height), r.X, r.Y); redrawn++; }
            drawn[i] = r;
        }
        LastRedrawnIcons = redrawn;
        painter.Flush(FlushIntention.Sync);
        long submitted = Stopwatch.GetTimestamp();
        Native.SIZE size = new Native.SIZE(bounds.Width, bounds.Height); Native.POINT origin = new Native.POINT(0, 0);
        Native.BLEND blend = new Native.BLEND { SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception();
        LastPaintMs = (submitted - started) * 1000.0 / Stopwatch.Frequency;
        LastSubmitMs = (Stopwatch.GetTimestamp() - submitted) * 1000.0 / Stopwatch.Frequency;
    }
    // One scaled copy per distinct pixel size, rendered once and reused across cycles.
    Bitmap Sprite(int index, int w, int h)
    {
        long key = ((long)w << 32) | (uint)h;
        Dictionary<long, Bitmap> cache = sprites[index];
        Bitmap cached;
        if (cache.TryGetValue(key, out cached)) return cached;
        if (cache.Count >= SpriteCacheLimit) { foreach (Bitmap stale in cache.Values) stale.Dispose(); cache.Clear(); }
        cached = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(cached))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(items[index].Image, new RectangleF(0, 0, w, h));
        }
        cache[key] = cached;
        return cached;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        if (m.Msg == 0x203)
        {
            long xy = m.LParam.ToInt64(); Point p = new Point((short)(xy & 65535), (short)((xy >> 16) & 65535));
            for (int i = items.Count - 1; i >= 0; i--)
            {
                int caption = Math.Max(40, items[i].Rect.Width - 2);
                Rectangle label = new Rectangle(items[i].Rect.Left - bounds.Left + (items[i].Rect.Width - caption) / 2, items[i].Rect.Bottom - bounds.Top, caption, 53);
                if (!hits[i].Contains(p) && !label.Contains(p)) continue;
                string path = items[i].Path;
                try { if (!String.IsNullOrEmpty(path)) { if (path.StartsWith("::")) Process.Start("explorer.exe", "shell:" + path); else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } }
                catch (Exception error) { MessageBox.Show(error.Message, "无法打开图标"); }
                break;
            }
            return;
        }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
        if (painter != null) { painter.Dispose(); painter = null; }
        if (frame != null) { frame.Dispose(); frame = null; }
        if (background != null) { background.Dispose(); background = null; }
        if (sprites != null) foreach (Dictionary<long, Bitmap> cache in sprites) { foreach (Bitmap sprite in cache.Values) sprite.Dispose(); cache.Clear(); }
        if (previous != IntPtr.Zero) { Native.SelectObject(memory, previous); previous = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
        if (memory != IntPtr.Zero) { Native.DeleteDC(memory); memory = IntPtr.Zero; }
        foreach (DesktopItem item in items) if (item.Image != null) { item.Image.Dispose(); item.Image = null; }
    }
}
