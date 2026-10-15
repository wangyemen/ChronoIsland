using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsland.Native;
using ChronoIsland.UI;

public static class IconGallery
{
    [STAThread]
    public static void Run()
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "cards");
        Directory.CreateDirectory(outDir);

        var app = new Application();
        var win = new IslandWindow();

        foreach (var dark in new[] { true, false })
        {
            var t = dark ? ThemeTokens.Dark : ThemeTokens.Light;
            var stack = new StackPanel { Background = t.WindowSurface, Margin = new Thickness(18) };

            stack.Children.Add(new TextBlock
            {
                Text = dark ? "DARK" : "LIGHT", FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = t.TextPrimary, Margin = new Thickness(0, 0, 0, 12)
            });

            foreach (var sig in new[] { "sys", "camera", "unlock", "media" })
            {
                var probe = win.ProbeCard(sig, t);
                stack.Children.Add(new TextBlock
                {
                    Text = sig, FontSize = 10, Foreground = t.TextTertiary
                });
                stack.Children.Add(new Border
                {
                    Background = t.IslandTop, CornerRadius = new CornerRadius(20),
                    Margin = new Thickness(0, 4, 0, 14), Child = probe
                });
            }

            var root = new Border
            {
                Background = t.WindowSurface,
                Width = 470,
                Height = 150 * 4 + 90,
                Child = new ScrollViewer { Content = stack }
            };
            root.Measure(new Size(470, 700));
            root.Arrange(new Rect(0, 0, 470, 700));
            root.UpdateLayout();

            var rtb = new RenderTargetBitmap(470, 700, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fsx = File.Create(Path.Combine(outDir, (dark ? "dark" : "light") + ".png"));
            enc.Save(fsx);
        }
        Console.WriteLine("cards done");
        Environment.Exit(0);
    }
}
