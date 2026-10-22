using System.Diagnostics;
using System.Windows;
using VostokModManager.Services;

namespace VostokModManager.Wpf.Services;

/// <summary>Small helpers shared by the modal windows.</summary>
internal static class PromptOwner
{
    /// <summary>While the returned scope lives, the session's questions
    /// (confirmations, errors) are owned by `window` rather than the main
    /// window, so they open over the dialog that asked them.</summary>
    public static IDisposable Use(ManagerSession session, Window window)
    {
        if (session.Prompt is not DialogPrompt dp) return Scope.None;
        var previous = dp.Owner;
        dp.Owner = window;
        return new Scope(() => dp.Owner = previous);
    }

    /// <summary>Opens an http(s) page in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    /// <summary>Sets the window's owner when there is a usable one,
    /// else centres it on the screen.</summary>
    public static void Attach(Window window, Window? owner)
    {
        if (owner != null && owner.IsLoaded && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private sealed class Scope : IDisposable
    {
        public static readonly Scope None = new(null);
        private Action? _undo;
        public Scope(Action? undo) => _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}
