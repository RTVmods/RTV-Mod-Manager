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
            // "startup" catches the window mid-startup with the page still
            // empty; "startup-band" once the page has content but the update
            // checks still run.
            var start = shell.StartAsync();
            if (req.Page == "startup") await Task.Delay(30);
            else if (req.Page == "startup-band")
                while (!start.IsCompleted && !shell.ShowStartupBand) await Task.Delay(50);
            else await start;
            Window target = window;
            if (req.Page.StartsWith("startup")) { }
            else if (req.Page == "setup")
            {
                target = new Views.SetupWindow(new SetupViewModel(shell.Session, SetupReason.FromSettings))
                {
                    Width = req.Width, Height = req.Height,
                };
                target.Show();
            }
            else if (req.Page == "library-picker")
            {
                target = new Views.LibraryPickerWindow(new LibraryPickerViewModel(shell.Session))
                {
                    Width = 700, Height = 700,
                };
                target.Show();
            }
            else if (req.Page == "unlinked")
            {
                // The "not linked to VostokMods" review, matches looked up.
                var vm = new LinkReviewViewModel(shell.Session);
                target = new Views.LinkReviewWindow(vm) { Width = 900, Height = 620 };
                target.Show();
                await vm.LoadAsync();
            }
            else if (req.Page == "manager-blink")
            {
                // The Loadout page with a manager update on offer.
                shell.Open("mods");
                shell.ManagerUpdate = "Update to 0.7.8";
            }
            else if (req.Page.StartsWith("changelog:"))
            {
                // A VostokMods mod's changelog window, e.g. changelog:ammo-check.
                var detail = await new VostokModManager.Api.VostokModsClient().GetModAsync(req.Page["changelog:".Length..]);
                target = new Views.ChangelogWindow(detail.Name, ModCard.BuildChangelog(detail.Files));
                target.Show();
            }
            else if (req.Page == "library-open")
            {
                // My Mods with the first mods that keep several versions opened.
                shell.Open("library");
                if (shell.Selected.Page is LibraryViewModel lib)
                    foreach (var m in lib.Mods.Where(m => m.Versions.Count > 1).Take(2)) m.IsExpanded = true;
            }
            else if (req.Page == "manager-update")
            {
                // The self-update's window, frozen mid-download.
                var never = new TaskCompletionSource<VostokModManager.Services.ManagerUpdateResult>();
                _ = Views.ManagerUpdateWindow.Runner(() => window, shell.Session)(p =>
                {
                    p.Report(new VostokModManager.Services.ManagerUpdateProgress("Downloading v0.7.5…", 29_000_000, 72_951_757));
                    return never.Task;
                });
                await Task.Delay(300);
                target = Application.Current.Windows.OfType<Views.MessageWindow>().Last();
            }
            else if (req.Page.StartsWith("launching"))
            {
                // The launch panel mid-launch: `launching:<phase>` (Started by default).
                var phase = req.Page.Contains(':')
                    && Enum.TryParse<VostokModManager.Services.GameLaunchPhase>(req.Page[(req.Page.IndexOf(':') + 1)..], true, out var p)
                    ? p : VostokModManager.Services.GameLaunchPhase.Started;
                shell.Open("mods");
                shell.LaunchStatusText = VostokModManager.Services.ManagerSession.LaunchPhaseText(phase);
            }
            else shell.Open(req.Page.Split(':')[0]);
            // Browse loads its catalogue from VostokMods; let it (and its thumbnails) land.
            // `browse:<slug>` then selects that mod's card.
            if (shell.Selected.Page is BrowseViewModel { Loading: { } loading } browse)
            {
                try { await loading; } catch { /* the page shows the error */ }
                var slug = req.Page.Contains(':') ? req.Page[(req.Page.IndexOf(':') + 1)..] : "";
                if (slug.Length > 0 && browse.Cards.FirstOrDefault(c => c.Slug == slug) is { } card) browse.Selected = card;
                await Task.Delay(4000);
            }
            await Task.Delay(req.Page == "startup" ? 60 : 800);
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
