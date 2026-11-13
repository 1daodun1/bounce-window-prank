using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// 启动前那张说明弹窗：800×600，写着这是玩笑程序、会被 360 误报，
// 右下角一个「运行」按钮，Esc 取消。
internal static class Intro
{
    static Window win;
    static ManualResetEvent done;
    static bool run;

    public static bool Ask()
    {
        run = false;
        done = new ManualResetEvent(false);

        Thread thread = new Thread(Run);
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        done.WaitOne(600000);
        return run;
    }

    static SolidColorBrush Rgb(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromRgb(r, g, b)); }

    static void Run()
    {
        try
        {
            win = new Window
            {
                Width = 800,
                Height = 600,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Topmost = true
            };

            var card = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(252, 10, 16, 44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 96, 112, 226)),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid { Margin = new Thickness(42, 34, 42, 30) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textStack = new StackPanel();
            textStack.Children.Add(new TextBlock
            {
                Text = "朋友的酒",
                Foreground = Rgb(255, 150, 60),
                FontSize = 30,
                FontWeight = FontWeights.Bold
            });
            textStack.Children.Add(new TextBlock
            {
                Text = "这是一个玩笑程序 · 请先读下面的说明再点运行",
                Foreground = Rgb(150, 165, 225),
                FontSize = 14,
                Margin = new Thickness(0, 8, 0, 26)
            });

            var body = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 30,
                Foreground = Rgb(214, 226, 255),
                FontSize = 15
            };
            body.Inlines.Add(new System.Windows.Documents.Run("这是个玩笑程序。") { FontWeight = FontWeights.Bold, Foreground = Rgb(255, 210, 120) });
            body.Inlines.Add(new System.Windows.Documents.Run(
                "里面带了自解压的东西，第一次运行会把自己解到缓存目录再启动，所以会被 360 当成木马误报。"));
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.Run("本应用无害。") { FontWeight = FontWeights.Bold, Foreground = Rgb(130, 235, 140) });
            body.Inlines.Add(new System.Windows.Documents.Run(
                "它只会动桌面图标、弹窗和画面，不改系统文件、不碰你的数据、不联网上传。"));
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.Run("二创需艾特原创。") { FontWeight = FontWeights.Bold, Foreground = Rgb(255, 210, 120) });
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.LineBreak());
            body.Inlines.Add(new System.Windows.Documents.Run("由豆包赞助开发。") { Foreground = Rgb(150, 165, 225) });
            textStack.Children.Add(body);

            Grid.SetRow(textStack, 0);
            grid.Children.Add(textStack);

            var runBtn = new Button
            {
                Content = "运  行",
                Width = 168,
                Height = 46,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(58, 92, 214)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 150, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var runTemplate = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(Button.ContentProperty));
            label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Button.ForegroundProperty));
            label.SetValue(TextBlock.FontSizeProperty, new TemplateBindingExtension(Button.FontSizeProperty));
            label.SetValue(TextBlock.FontWeightProperty, new TemplateBindingExtension(Button.FontWeightProperty));
            label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(label);
            runTemplate.VisualTree = border;
            runBtn.Template = runTemplate;
            runBtn.Click += delegate { Accept(true); };

            Grid.SetRow(runBtn, 1);
            grid.Children.Add(runBtn);

            card.Child = grid;
            win.Content = card;
            win.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Accept(false);
            };
            win.Show();
            win.Activate();
            JellyAll.Guard.HookDispatcher();
            Dispatcher.Run();
        }
        catch (Exception error)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intro_err.txt"), error.ToString()); }
            catch (Exception) { }
            run = false;
            done.Set();
        }
    }

    static void Accept(bool go)
    {
        run = go;
        done.Set();
        try { win.Close(); } catch (Exception) { }
        try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch (Exception) { }
    }
}
