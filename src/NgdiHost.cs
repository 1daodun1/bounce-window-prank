using System;
using System.Diagnostics;
using System.Threading;
using NetGDI;

// NetGDI 七种特效宿主：跟示例工程一样直接用类型调，跑在单独进程里。
internal static class NgdiHost
{
    [STAThread]
    static void Main(string[] args)
    {
        int parent = -1;
        if (args.Length > 0) int.TryParse(args[0], out parent);

        NGDISettings.InvertedColors.Enabled = true;
        NGDISettings.BlurEffect.Enabled = true;
        NGDISettings.RoundedTunnelEffect.Enabled = true;
        NGDISettings.HatchBrush.Enabled = true;
        NGDISettings.PatternBrush.Enabled = true;
        NGDISettings.ColorFilters.Enabled = true;
        NGDISettings.MeltingScreen.Enabled = true;

        NGDISettings.InvertedColors.Interval = 70;
        NGDISettings.BlurEffect.Interval = 70;
        NGDISettings.RoundedTunnelEffect.Interval = 70;
        NGDISettings.HatchBrush.Interval = 70;
        NGDISettings.PatternBrush.Interval = 70;
        NGDISettings.ColorFilters.Interval = 70;
        NGDISettings.MeltingScreen.Interval = 70;

        Thread t = new Thread(delegate() { new GDIEffects(); });
        t.IsBackground = true;
        t.Start();

        while (true)
        {
            Thread.Sleep(400);
            if (parent > 0)
            {
                try { Process.GetProcessById(parent); }
                catch (Exception) { break; }
            }
        }

        NGDISettings.InvertedColors.Enabled = false;
        NGDISettings.BlurEffect.Enabled = false;
        NGDISettings.RoundedTunnelEffect.Enabled = false;
        NGDISettings.HatchBrush.Enabled = false;
        NGDISettings.PatternBrush.Enabled = false;
        NGDISettings.ColorFilters.Enabled = false;
        NGDISettings.MeltingScreen.Enabled = false;
    }
}
