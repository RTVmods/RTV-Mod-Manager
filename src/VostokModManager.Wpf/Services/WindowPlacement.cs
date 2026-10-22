using System.IO;
using System.Text.Json;
using System.Windows;

namespace VostokModManager.Wpf.Services;

/// <summary>Remembers the main window's size, position and maximized
/// state in WindowState.json beside settings.json. Kept out of Settings so
/// the shared settings carry no front-end layout.</summary>
public static class WindowPlacement
{
    private sealed record Saved(double Left, double Top, double Width, double Height, bool Maximized);

    private static string FilePath => Path.Combine(AppPaths.ManagerDir, "WindowState.json");

    public static void Restore(Window w)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (s == null || s.Width < 400 || s.Height < 300) return;
            // Only when the saved spot is still on a screen (a monitor may be gone).
            var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!visible.IntersectsWith(new Rect(s.Left, s.Top, s.Width, s.Height))) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = s.Left;
            w.Top = s.Top;
            w.Width = s.Width;
            w.Height = s.Height;
            if (s.Maximized) w.WindowState = WindowState.Maximized;
        }
        catch { /* first start, or a damaged file: the defaults stand */ }
    }

    public static void Save(Window w)
    {
        try
        {
            var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
            Directory.CreateDirectory(AppPaths.ManagerDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Saved(r.Left, r.Top, r.Width, r.Height, w.WindowState == WindowState.Maximized)));
        }
        catch { /* not worth bothering the user about */ }
    }
}
