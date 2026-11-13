using Microsoft.Win32;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// 启动前的“组件安装”检查：只有系统低于 Windows 10 才会弹。
// 进度条走完（约 18 秒）自动关闭，然后照常进入说明弹窗。
internal static class SetupGate
{
    const double SECONDS = 18;

    // 自检用：--setuptest 时不管系统版本都弹一遍
    public static bool Force;

    public static void Ensure()
    {
        if (!Force && !NeedsComponent()) return;

        int code = 0;
        Thread thread = new Thread(delegate() { code = Run(); });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
    }

    // Windows 10 及以上自带 .NET Framework 4.x，不需要这个窗口。
    // 用注册表里的 CurrentMajorVersionNumber 判断，避免没写 Win10 兼容清单时 OSVersion 谎报 6.2。
    static bool NeedsComponent()
    {
        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key != null)
                {
                    object value = key.GetValue("CurrentMajorVersionNumber");
                    if (value != null)
                    {
                        int major = Convert.ToInt32(value);
                        return major < 10;
                    }
                }
            }
        }
        catch (Exception) { }

        return Environment.OSVersion.Version.Major < 10;
    }

    static int Run()
    {
        try
        {
            Window win = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = true,
                Topmost = true,
                Width = 720,
                Height = 430,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Title = "朋友的酒 · 运行组件"
            };

            Border card = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = new LinearGradientBrush(
                    Color.FromRgb(10, 20, 46), Color.FromRgb(4, 8, 22), 90),
                BorderBrush = new SolidColorBrush(Color.FromRgb(52, 96, 190)),
                BorderThickness = new Thickness(1)
            };

            StackPanel stack = new StackPanel { Margin = new Thickness(48, 44, 48, 40) };

            stack.Children.Add(new TextBlock
            {
                Text = "运行组件准备",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 176, 255)),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 18)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "检测到你的电脑低于 Windows 10",
                Foreground = Brushes.White,
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "需要先安装 .NET Framework 4.x 运行组件，装完就能正常启动。\n安装过程大约 18 秒，这个窗口会自动关闭，请不要手动结束它。",
                Foreground = new SolidColorBrush(Color.FromRgb(176, 194, 224)),
                FontSize = 15,
                LineHeight = 26,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 30)
            });

            Border track = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(30, 44, 74)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 560
            };

            Border fill = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new LinearGradientBrush(
                    Color.FromRgb(46, 128, 255), Color.FromRgb(120, 200, 255), 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };

            Grid bar = new Grid { Width = 560, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(track);
            bar.Children.Add(fill);
            stack.Children.Add(bar);

            TextBlock status = new TextBlock
            {
                Text = "正在安装组件… 0%",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 196, 255)),
                FontSize = 14,
                Margin = new Thickness(0, 14, 0, 0)
            };
            stack.Children.Add(status);

            card.Child = stack;
            Border outer = new Border
            {
                CornerRadius = new CornerRadius(18),
                Child = card,
                Effect = null
            };
            win.Content = outer;

            DateTime start = DateTime.Now;
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += delegate
            {
                try
                {
                    double ratio = (DateTime.Now - start).TotalSeconds / SECONDS;
                    if (ratio > 1) ratio = 1;
                    fill.Width = 560 * ratio;
                    int percent = (int)(ratio * 100);
                    status.Text = ratio >= 1
                        ? "组件安装完成，正在启动…"
                        : "正在安装组件… " + percent + "%";
                }
                catch (Exception) { }
            };
            timer.Start();

            DispatcherTimer close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SECONDS + 1) };
            close.Tick += delegate
            {
                try
                {
                    close.Stop();
                    timer.Stop();
                    win.Close();
                }
                catch (Exception) { }
            };
            close.Start();

            win.ShowDialog();
            return 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
