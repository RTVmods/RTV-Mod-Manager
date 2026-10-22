using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VostokModManager.Domain;
using VostokModManager.Wpf.ViewModels;

namespace VostokModManager.Wpf.Views;

/// <summary>The Browse page. The view model holds the catalogue; this
/// file hosts the Website tab's WebView2, which is created the first time
/// that tab opens (it costs nothing until then) and intercepts mod
/// downloads so they install instead of landing in Downloads.</summary>
public partial class BrowseView : UserControl
{
    private BrowseViewModel? _vm;
    private WebView2? _web;
    private bool _webStarting;

    public BrowseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as BrowseViewModel);
    }

    private void Attach(BrowseViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm?.IsWebsiteTab == true) _ = StartWebAsync();
    }

    /// <summary>The page left the window (another menu entry): the
    /// browser goes with it, and is made again when the tab is next open.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _web?.Dispose(); } catch { /* already gone */ }
        _web = null;
        WebHost.Child = null;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowseViewModel.IsWebsiteTab) && _vm?.IsWebsiteTab == true)
            _ = StartWebAsync();
        else if (e.PropertyName == nameof(BrowseViewModel.ActiveKey) && _vm?.IsModsTab == true)
            ModsScroll.ScrollToTop();
    }

    // ── the More tab's menu ───────────────────────────────────────────

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { ContextMenu: { } menu } tab) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = tab;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnMoreClosed(object sender, RoutedEventArgs e) => _vm?.ResyncTabs();

    // ── the Website tab ───────────────────────────────────────────────

    private async Task StartWebAsync()
    {
        if (_web != null || _webStarting || _vm == null) return;
        _webStarting = true;
        var vm = _vm;
        try
        {
            vm.WebError = "";
            var udf = vm.WebUserDataFolder;
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udf);
            var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1C, 0x1D, 0x1D) };
            WebHost.Child = web;
            await web.EnsureCoreWebView2Async(env);
            _web = web;
            web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            web.CoreWebView2.SourceChanged += (_, _) => vm.OnWebNavigated(web.Source?.ToString() ?? "");
            web.CoreWebView2.NavigationCompleted += (_, _) => vm.OnWebNavigated(web.Source?.ToString() ?? "");
            web.CoreWebView2.Navigate(vm.WebHome);
        }
        catch (Exception ex)
        {
            WebHost.Child = null;
            _web = null;
            vm.WebError = "The embedded browser needs the Microsoft Edge WebView2 Runtime, which couldn't be started ("
                          + ex.Message + "). It ships with Windows 10 and 11; if it's missing, install the Evergreen "
                          + "runtime from developer.microsoft.com/microsoft-edge/webview2. Every other tab here works without it.";
            vm.SetWebStatus("");
        }
        finally
        {
            _webStarting = false;
        }
    }

    /// <summary>A download the page started. Mod files (.vmz, or a .zip
    /// served by vostokmods.net) go to a temp file and are installed when
    /// complete; anything else downloads as normal.</summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var vm = _vm;
        if (vm == null || _web == null) return;
        var name = e.ResultFilePath ?? "";
        var uri = e.DownloadOperation.Uri ?? "";
        var looksVmz = name.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)
                       || uri.Contains(".vmz", StringComparison.OrdinalIgnoreCase);
        // VostokMods also serves mods named .zip. A .zip from any other
        // host is an ordinary download.
        var looksSiteZip = IsVostokModsHost(uri)
                           && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                               || UrlPathEndsWith(uri, ".zip"));
        if (!looksVmz && !looksSiteZip) return;

        // The mod page on screen when the download starts is where the
        // file came from; any other page leaves the source unknown.
        var source = VostokModsUrl.TryParseModPageSlug(_web.Source?.ToString() ?? "", out var slug)
            ? ModSource.ForSlug(slug)
            : ModSource.None;

        var temp = Path.Combine(Path.GetTempPath(), "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
        e.ResultFilePath = temp;
        e.Handled = true;  // no browser download bar

        var op = e.DownloadOperation;
        vm.SetWebStatus("Downloading mod…");
        op.StateChanged += async (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                try
                {
                    await vm.InstallWebDownloadAsync(op.ResultFilePath, source);
                }
                finally
                {
                    try { if (File.Exists(op.ResultFilePath)) File.Delete(op.ResultFilePath); } catch { /* temp */ }
                }
            }
            else if (op.State == CoreWebView2DownloadState.Interrupted)
            {
                vm.SetWebStatus("Download interrupted.");
            }
        };
    }

    /// <summary>vostokmods.net or one of its subdomains (files.vostokmods.net
    /// serves the downloads).</summary>
    private static bool IsVostokModsHost(string url)
    {
        if (!VostokModsUrl.IsVostokMods(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Host.Equals("vostokmods.net", StringComparison.OrdinalIgnoreCase)
               || u.Host.EndsWith(".vostokmods.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UrlPathEndsWith(string url, string ext)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    private void OnWebBack(object sender, RoutedEventArgs e)
    {
        if (_web?.CanGoBack == true) _web.GoBack();
    }

    private void OnWebForward(object sender, RoutedEventArgs e)
    {
        if (_web?.CanGoForward == true) _web.GoForward();
    }

    private void OnWebReload(object sender, RoutedEventArgs e) => _web?.Reload();

    private void OnWebHome(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2 != null && _vm != null) _web.CoreWebView2.Navigate(_vm.WebHome);
    }

    private void OnWebExternal(object sender, RoutedEventArgs e)
    {
        var url = _vm?.WebAddress;
        if (string.IsNullOrEmpty(url)) url = _vm?.WebHome;
        if (string.IsNullOrEmpty(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}
