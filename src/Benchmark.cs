using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Measurement-only harness. It drives the real animation path (JellyLayer) on a
// synthetic desktop grid so the render pipeline can be compared before and after
// a change with an identical workload. Nothing here runs unless --bench is passed.
internal static class Benchmark
{
    static double Ms() { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }

    public static void Run(string[] args)
    {
        if (args.Length < 5) { Environment.ExitCode = 2; return; }
        string prefix = args[1];
        int icons = int.Parse(args[2], CultureInfo.InvariantCulture);
        int fps = int.Parse(args[3], CultureInfo.InvariantCulture);
        int frames = int.Parse(args[4], CultureInfo.InvariantCulture);
        bool mutant = Array.IndexOf(args, "--noclear") >= 0;
        try
        {
            if (mutant)
            {
                FieldInfo field = typeof(JellyLayer).GetField("ForTestDisableClear", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) throw new InvalidOperationException("Mutant switch missing on this build");
                field.SetValue(null, true);
            }
            Execute(prefix, icons, fps, frames, mutant);
        }
        catch (Exception error)
        {
            File.WriteAllText(prefix + ".summary.txt", "ERROR\r\n" + error + "\r\n", Encoding.UTF8);
            Environment.ExitCode = 1;
        }
    }

    static List<DesktopItem> Build(int count)
    {
        const int size = 48, stepX = 112, stepY = 100, left = 30, top = 30, rows = 9;
        List<DesktopItem> data = new List<DesktopItem>();
        for (int i = 0; i < count; i++)
        {
            int column = i / rows, row = i % rows;
            data.Add(new DesktopItem
            {
                Index = i,
                Name = "基准图标" + i,
                Rect = new Rectangle(left + column * stepX, top + row * stepY, size, size),
                Image = SystemIcons.Application.ToBitmap()
            });
        }
        return data;
    }

    static void Execute(string prefix, int icons, int fps, int frames, bool mutant)
    {
        List<DesktopItem> data = Build(icons);
        uint handlesBefore = Native.GetGuiResources(new IntPtr(-1), 0);
        double cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        StringBuilder report = new StringBuilder();
        List<JellyLayer> layers;
        double step = 1.1 / fps;
        using (Form host = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000),
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1)
        })
        {
            host.Show();
            layers = JellyLayer.CreateGroups(host.Handle, data);
            foreach (JellyLayer layer in layers) { layer.Prepare(); layer.Show(); }

            // Warm up: first frames build caches and prime GDI+ paths.
            for (int i = 0; i < 120; i++)
            {
                foreach (JellyLayer layer in layers) layer.UpdatePhase(i * step, 0.38, false);
                if (i % 16 == 0) Application.DoEvents();
            }

            List<double> paint = new List<double>(frames);
            List<double> submit = new List<double>(frames);
            List<double> total = new List<double>(frames);
            double wallStart = Ms();
            for (int i = 0; i < frames; i++)
            {
                double frameStart = Ms();
                double p = 0, s = 0;
                foreach (JellyLayer layer in layers) { layer.UpdatePhase(i * step, 0.38, false); p += layer.LastPaintMs; s += layer.LastSubmitMs; }
                paint.Add(p); submit.Add(s); total.Add(Ms() - frameStart);
                if (i % 32 == 31) Application.DoEvents();
            }
            double wall = (Ms() - wallStart) / 1000.0;
            double cpuAfter = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            uint handlesAfter = Native.GetGuiResources(new IntPtr(-1), 0);

            WriteSamples(prefix + ".csv", paint, submit, total);

            double pixels = 0, layersPixels = 0;
            foreach (JellyLayer layer in layers) { pixels += layer.PixelCount; layersPixels++; }
            report.AppendLine("icons=" + icons);
            report.AppendLine("layers=" + layers.Count);
            report.AppendLine("surface_pixels=" + pixels.ToString("0", CultureInfo.InvariantCulture));
            report.AppendLine("target_fps=" + fps);
            report.AppendLine("frames=" + frames);
            report.AppendLine("paint_mean_ms=" + Mean(paint).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("paint_p50_ms=" + Quantile(paint, .50).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("paint_p95_ms=" + Quantile(paint, .95).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("submit_mean_ms=" + Mean(submit).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("submit_p95_ms=" + Quantile(submit, .95).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("frame_mean_ms=" + Mean(total).ToString("0.000", CultureInfo.InvariantCulture));
            report.AppendLine("loop_fps=" + (frames / wall).ToString("0.0", CultureInfo.InvariantCulture));
            report.AppendLine("cpu_single_core_pct=" + ((cpuAfter - cpuBefore) / 1000.0 / wall * 100).ToString("0.0", CultureInfo.InvariantCulture));
            report.AppendLine("gdi_handles=" + handlesBefore + "->" + handlesAfter);

            // Appearance oracle: after a tall frame the short frame must leave no stale pixels
            // in the rows the tall sprite occupied.
            foreach (JellyLayer layer in layers) layer.UpdatePhase(0.5, 0.38, false);
            int stale = 0, checkedRows = 0;
            using (Bitmap shot = layers[0].CaptureForTest())
            {
                int limit = 30 + 48 - (int)Math.Round(48 * (1 - 0.38));
                checkedRows = limit;
                for (int y = 0; y < limit && y < shot.Height; y++)
                    for (int x = 0; x < shot.Width; x++)
                        if (shot.GetPixel(x, y).A != 0) stale++;
            }
            report.AppendLine("stale_pixels_above_short_band=" + stale + " (rows<" + checkedRows + ")");
            report.AppendLine("trail_check=" + (stale == 0 ? "PASS" : "FAIL"));

            // Fixed-phase frames for a before/after pixel diff.
            double[] phases = { 0, 0.125, 0.25, 0.375, 0.5 };
            for (int p = 0; p < phases.Length; p++)
            {
                foreach (JellyLayer layer in layers) layer.UpdatePhase(phases[p], 0.38, false);
                for (int l = 0; l < layers.Count; l++)
                    using (Bitmap shot = layers[l].CaptureForTest())
                        shot.Save(prefix + "-phase" + p + "-layer" + l + ".png", ImageFormat.Png);
            }

            foreach (JellyLayer layer in layers) layer.Dispose();
        }
        WriteComponents(prefix + ".components.txt", icons);
        if (mutant) report.AppendLine("mutant=noclear");
        report.AppendLine("verdict=" + (report.ToString().Contains("trail_check=PASS") ? "OK" : "TRAIL_FAIL"));
        File.WriteAllText(prefix + ".summary.txt", report.ToString(), Encoding.UTF8);
    }

    static void WriteSamples(string path, List<double> paint, List<double> submit, List<double> total)
    {
        StringBuilder text = new StringBuilder("frame,paint_ms,submit_ms,frame_ms\r\n");
        for (int i = 0; i < paint.Count; i++)
            text.Append(i).Append(',').Append(paint[i].ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(submit[i].ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                .Append(total[i].ToString("0.000", CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
    }

    // Component-boundary microbenchmarks of the two candidate strategies, measured in
    // one process so the numbers share a fingerprint.
    static void WriteComponents(string path, int icons)
    {
        const int size = 48, stepX = 112, stepY = 100, left = 30, top = 30, rows = 9;
        int columns = (icons + rows - 1) / rows;
        int width = left + (columns - 1) * stepX + 78 + 20;
        int height = top + (rows - 1) * stepY + 139 + 20;
        const int iterations = 40;
        List<Rectangle> tall = new List<Rectangle>();
        List<Rectangle> shortRects = new List<Rectangle>();
        List<Bitmap> scaled = new List<Bitmap>();
        using (Bitmap source = SystemIcons.Application.ToBitmap())
        using (Bitmap surface = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        using (Bitmap labels = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(surface))
        {
            foreach (DesktopItem item in Build(icons))
            {
                double sx, sy, ax, ay;
                Motion.Scale(0, 0.38, out sx, out sy);
                int w = (int)Math.Round(size * sx), h = (int)Math.Round(size * sy);
                int x = (int)Math.Round(item.Rect.Left + item.Rect.Width * .5 - w * .5);
                int y = item.Rect.Bottom - h;
                tall.Add(new Rectangle(x, y, w, h));
                Motion.Scale(0.5, 0.38, out ax, out ay);
                int aw = (int)Math.Round(size * ax), ah = (int)Math.Round(size * ay);
                shortRects.Add(new Rectangle((int)Math.Round(item.Rect.Left + item.Rect.Width * .5 - aw * .5), item.Rect.Bottom - ah, aw, ah));
                Bitmap scaledSprite = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (Graphics sg = Graphics.FromImage(scaledSprite))
                {
                    sg.Clear(Color.Transparent);
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.DrawImage(source, new RectangleF(0, 0, w, h));
                }
                scaled.Add(scaledSprite);
                item.Image.Dispose();
            }

            double a = 0, b = 0, c = 0, d = 0;
            for (int pass = 0; pass < iterations; pass++)
            {
                long t0 = Stopwatch.GetTimestamp();
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(labels, 0, 0);
                a += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                t0 = Stopwatch.GetTimestamp();
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                for (int i = 0; i < tall.Count; i++)
                {
                    Rectangle r = tall[i];
                    g.DrawImage(source, new RectangleF(r.X, r.Y, r.Width, r.Height));
                }
                b += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < tall.Count; i++)
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(labels, tall[i], tall[i], GraphicsUnit.Pixel);
                }
                c += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                t0 = Stopwatch.GetTimestamp();
                g.CompositingMode = CompositingMode.SourceOver;
                for (int i = 0; i < scaled.Count; i++) g.DrawImageUnscaled(scaled[i], tall[i].X, tall[i].Y);
                d += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            foreach (Bitmap sprite in scaled) sprite.Dispose();

            StringBuilder text = new StringBuilder("component,ms_per_frame,note\r\n");
            text.Append("full_frame_label_blit,").Append((a / iterations).ToString("0.000", CultureInfo.InvariantCulture)).Append(",old per-frame clear+labels\r\n");
            text.Append("fractional_scaled_draws,").Append((b / iterations).ToString("0.000", CultureInfo.InvariantCulture)).Append(",old per-frame icon draw (HighQualityBilinear, float rect)\r\n");
            text.Append("dirty_background_restore,").Append((c / iterations).ToString("0.000", CultureInfo.InvariantCulture)).Append(",new per-frame clear (SourceCopy over changed rects)\r\n");
            text.Append("cached_sprite_blits,").Append((d / iterations).ToString("0.000", CultureInfo.InvariantCulture)).Append(",new per-frame icon draw (1:1 blit of cached sprite)\r\n");
            text.Append("surface,").Append(width).Append('x').Append(height).Append(",icons=").Append(icons).Append("\r\n");
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        }
    }

    static double Mean(List<double> values)
    {
        double sum = 0; foreach (double v in values) sum += v;
        return values.Count == 0 ? 0 : sum / values.Count;
    }

    static double Quantile(List<double> values, double q)
    {
        if (values.Count == 0) return 0;
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int index = (int)Math.Round((sorted.Length - 1) * q);
        return sorted[index];
    }
}
