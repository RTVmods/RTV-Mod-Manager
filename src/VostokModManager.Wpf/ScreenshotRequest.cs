#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf;

/// <summary>DEBUG only: `--screenshot &lt;page&gt; &lt;out.png&gt; [width height]`
/// opens the window on that page, waits for the startup scan, renders it
/// to a PNG and exits. Run it with VMM_APPDATA_ROOT pointing at a sandbox.</summary>
internal sealed record ScreenshotRequest(string Page, string OutPath, int Width, int Height)
{
    public static ScreenshotRequest? Parse(string[] args)
    {
        var i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 2 >= args.Length) return null;
        var w = i + 3 < args.Length && int.TryParse(args[i + 3], out var pw) ? pw : 1440;
        var h = i + 4 < args.Length && int.TryParse(args[i + 4], out var ph) ? ph : 920;
        return new ScreenshotRequest(args[i + 1], Path.GetFullPath(args[i + 2]), w, h);
    }

    public static async Task RunAsync(ScreenshotRequest req, Window window, ShellViewModel shell)
    {
        try
        {
            window.Width = req.Width;
            window.Height = req.Height;
            await shell.StartAsync();
            Window target = window;
            if (req.Page == "setup")
            {
                target = new Views.SetupWindow(new SetupViewModel(shell.Session, SetupReason.FromSettings))
                {
                    Width = req.Width, Height = req.Height,
                };
                target.Show();
            }
            else shell.Open(req.Page);
            await Task.Delay(800);
            target.UpdateLayout();

            var root = (FrameworkElement)target.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            var bmp = new RenderTargetBitmap(
                (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.GetDirectoryName(req.OutPath)!);
            using (var fs = File.Create(req.OutPath)) enc.Save(fs);
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
#endif
