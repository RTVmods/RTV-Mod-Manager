// Embedded ModWorkshop browser with auto-install.
//
// Hosts a WebView2 control pointed at modworkshop.net. Two install
// routes, both feeding the existing install pipeline via the callback
// MainForm supplies (InstallDownloadedModAsync → InstallModFilesAsync):
//
//   1. Download interception — when the page triggers a .vmz download,
//      DownloadStarting redirects it to a temp file; on completion the
//      file is handed to the install callback.
//   2. "Install this mod" button — when the current URL is a mod page,
//      the numeric id is parsed and the latest .vmz is fetched via the
//      public API (ModWorkshopClient.DownloadLatestAsync), then installed.
//
// WebView2 needs the Edge WebView2 Runtime (preinstalled on Win10/11).
// If EnsureCoreWebView2Async fails, we show a themed message pointing at
// the runtime download and close — MainForm's URL-paste fallback still
// works without the embedded browser.

using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ModBrowserDialog : Form
{
    private const string HomeUrl = "https://modworkshop.net/game/roadtovostok";

    private readonly ModWorkshopClient _mw;
    private readonly Func<string, Task> _installCallback;
    /// <summary>Returns true when a mod with the given ModWorkshop id is
    /// already present locally (live mods or the library). Drives the
    /// "Already in library" button state.</summary>
    private readonly Func<int, bool> _ownsModWorkshopId;
    private readonly Settings _settings;

    private WebView2 _web = null!;
    private Button _back = null!;
    private Button _forward = null!;
    private Button _reload = null!;
    private Button _home = null!;
    private Button _installBtn = null!;
    private Label _addressLabel = null!;
    private Label _statusLabel = null!;
    private bool _coreReady;
    private bool _installing;

    /// <summary>Number of mods installed during this browse session, so
    /// the caller could rescan — though the install callback already
    /// drives a full rescan itself.</summary>
    public int InstalledCount { get; private set; }

    public ModBrowserDialog(
        ModWorkshopClient mw,
        Func<string, Task> installCallback,
        Func<int, bool> ownsModWorkshopId,
        Settings settings)
    {
        _mw = mw;
        _installCallback = installCallback;
        _ownsModWorkshopId = ownsModWorkshopId;
        _settings = settings;
        InitUi();
    }

    private void InitUi()
    {
        Text            = "Browse ModWorkshop";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(900, 620);
        Width           = 1280;
        Height          = 860;
        ShowInTaskbar   = false;

        // Restore the last-used browser size/position if we have a valid
        // one on a current screen; otherwise keep the default and just
        // center on the parent. Mirrors MainForm's window-bounds restore.
        var saved = new Rectangle(
            _settings.BrowserLeft, _settings.BrowserTop,
            _settings.BrowserWidth, _settings.BrowserHeight);
        if (saved.Width >= 700 && saved.Height >= 500
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = saved.Location;
            Size = saved.Size;
        }
        if (_settings.BrowserMaximized)
            WindowState = FormWindowState.Maximized;

        DialogSizing.ClampToWorkingArea(this);

        // Persist size/position on close so the browser reopens where the
        // user left it. Use RestoreBounds when maximised/minimised.
        FormClosing += (_, _) =>
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.BrowserLeft = b.Left;
            _settings.BrowserTop = b.Top;
            _settings.BrowserWidth = b.Width;
            _settings.BrowserHeight = b.Height;
            _settings.BrowserMaximized = WindowState == FormWindowState.Maximized;
            try { _settings.Save(); } catch { /* best-effort */ }
        };

        var nav = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(30, 34, 44),
            Padding = new Padding(8, 6, 8, 6),
        };

        _back    = NavButton("◀");
        _forward = NavButton("▶");
        _reload  = NavButton("⟳");
        _home    = NavButton("⌂");
        _back.Left = 8;
        _forward.Left = _back.Right + 4;
        _reload.Left = _forward.Right + 4;
        _home.Left = _reload.Right + 4;
        foreach (var b in new[] { _back, _forward, _reload, _home }) b.Top = 6;

        _back.Click    += (_, _) => { if (_coreReady && _web.CanGoBack) _web.GoBack(); };
        _forward.Click += (_, _) => { if (_coreReady && _web.CanGoForward) _web.GoForward(); };
        _reload.Click  += (_, _) => { if (_coreReady) _web.Reload(); };
        _home.Click    += (_, _) => Navigate(HomeUrl);

        _installBtn = MainForm.ThemedButton("⬇ Install this mod");
        _installBtn.Width = 200;
        _installBtn.Height = 36;
        _installBtn.AutoSize = false;
        _installBtn.BackColor = Color.FromArgb(45, 90, 55);
        _installBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _installBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _installBtn.Enabled = false;
        _installBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _installBtn.Click += async (_, _) => await InstallCurrentModAsync();

        _addressLabel = new Label
        {
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(150, 165, 190),
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleLeft,
            Left = _home.Right + 12,
            Top = 6,
            Height = 36,
        };

        nav.Controls.AddRange(new Control[]
            { _back, _forward, _reload, _home, _addressLabel, _installBtn });
        nav.Resize += (_, _) =>
        {
            _installBtn.Left = nav.Width - _installBtn.Width - 12;
            _addressLabel.Width = Math.Max(50, _installBtn.Left - _addressLabel.Left - 12);
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Color.FromArgb(30, 34, 44),
            ForeColor = Color.FromArgb(170, 185, 210),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "Loading…",
        };

        _web = new WebView2 { Dock = DockStyle.Fill };

        Controls.Add(_web);
        Controls.Add(_statusLabel);
        Controls.Add(nav);

        Load += async (_, _) => await InitWebViewAsync();
    }

    private static Button NavButton(string glyph)
    {
        var b = new Button
        {
            Text = glyph,
            Width = 38,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 55, 70),
            ForeColor = Color.FromArgb(225, 230, 240),
            Font = new Font("Segoe UI", 12f),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(85, 100, 120);
        return b;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Per-app user-data folder under %APPDATA% so the embedded
            // browser's cache/cookies don't litter the install dir.
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VostokModManager", "webview2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: udf);
            await _web.EnsureCoreWebView2Async(env);
            _coreReady = true;

            _web.CoreWebView2.DownloadStarting += OnDownloadStarting;
            _web.CoreWebView2.NavigationCompleted += (_, _) => UpdateChrome();
            _web.CoreWebView2.SourceChanged += (_, _) => UpdateChrome();

            Navigate(HomeUrl);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "WebView2 unavailable.";
            ThemedMessageBox.Show(this,
                "The embedded browser needs the Microsoft Edge WebView2 Runtime, "
                + "which couldn't be started:\n\n" + ex.Message + "\n\n"
                + "It ships with Windows 10/11; if it's missing, install the "
                + "Evergreen runtime from:\n"
                + "https://developer.microsoft.com/microsoft-edge/webview2/\n\n"
                + "You can still install mods via the \"Install from "
                + "ModWorkshop URL…\" option without the browser.",
                "Embedded browser unavailable",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private void Navigate(string url)
    {
        if (!_coreReady) return;
        try { _web.CoreWebView2.Navigate(url); } catch { /* ignore bad url */ }
    }

    private void UpdateChrome()
    {
        if (!_coreReady) return;
        var url = _web.Source?.ToString() ?? "";
        _addressLabel.Text = url;
        _back.Enabled = _web.CanGoBack;
        _forward.Enabled = _web.CanGoForward;

        if (_installing)
            return; // mid-install: leave button/status as set by the install flow

        if (!ModWorkshopUrl.TryParseModId(url, out var id))
        {
            _installBtn.Text = "⬇ Install this mod";
            _installBtn.Enabled = false;
            SetInstallButtonColor(installed: false);
            _statusLabel.Text = "Browse to a mod page to enable one-click install.";
            return;
        }

        var owned = _ownsModWorkshopId(id);
        if (owned)
        {
            _installBtn.Text = "✓ Already in library";
            _installBtn.Enabled = false;
            SetInstallButtonColor(installed: true);
            _statusLabel.Text =
                $"Mod {id} is already in your library — re-download via the "
                + "page's Download button if you want to force-update it.";
        }
        else
        {
            _installBtn.Text = "⬇ Install this mod";
            _installBtn.Enabled = true;
            SetInstallButtonColor(installed: false);
            _statusLabel.Text =
                $"On mod page (id {id}) — click “Install this mod”, or use "
                + "the page's Download button.";
        }
    }

    /// <summary>Green = actionable install; muted grey-green = already
    /// owned (disabled). Keeps the ✓ state visually distinct from the
    /// active ⬇ state rather than just greying out.</summary>
    private void SetInstallButtonColor(bool installed)
    {
        if (installed)
        {
            _installBtn.BackColor = Color.FromArgb(48, 60, 52);
            _installBtn.ForeColor = Color.FromArgb(150, 200, 165);
            _installBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 95, 78);
        }
        else
        {
            _installBtn.BackColor = Color.FromArgb(45, 90, 55);
            _installBtn.ForeColor = Color.FromArgb(225, 240, 230);
            _installBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        }
    }

    // ── Trigger 1: intercept .vmz downloads ──────────────────────────

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var name = e.DownloadOperation.ResultFilePath ?? "";
        var uri = e.DownloadOperation.Uri ?? "";
        var looksVmz = name.EndsWith(".vmz", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains(".vmz", StringComparison.OrdinalIgnoreCase);
        if (!looksVmz) return; // let other downloads behave normally

        // Redirect to a temp path we control, hide the default UI, and
        // install on completion.
        var temp = Path.Combine(
            Path.GetTempPath(),
            "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
        e.ResultFilePath = temp;
        e.Handled = true; // suppress the browser's own download bar

        var op = e.DownloadOperation;
        _statusLabel.Text = "Downloading mod…";
        op.StateChanged += async (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                _statusLabel.Text = "Download complete — installing…";
                await RunInstallAsync(op.ResultFilePath);
            }
            else if (op.State == CoreWebView2DownloadState.Interrupted)
            {
                _statusLabel.Text = "Download interrupted.";
            }
        };
    }

    // ── Trigger 2: Install-this-mod button ───────────────────────────

    private async Task InstallCurrentModAsync()
    {
        var url = _web.Source?.ToString() ?? "";
        if (!ModWorkshopUrl.TryParseModId(url, out var id))
        {
            ThemedMessageBox.Show(this,
                "This page isn't a specific mod page, so there's no mod id to "
                + "install. Open a mod's page first.",
                "No mod on this page",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _installing = true;
        _installBtn.Enabled = false;
        _statusLabel.Text = $"Downloading mod {id}…";
        try
        {
            var temp = Path.Combine(
                Path.GetTempPath(),
                "vmm_dl_" + Guid.NewGuid().ToString("N") + ".vmz");
            await _mw.DownloadLatestAsync(id, temp);
            await RunInstallAsync(temp);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Install failed.";
            ThemedMessageBox.Show(this,
                "Couldn't download or install this mod:\n" + ex.Message,
                "Install failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _installing = false;
            UpdateChrome();
        }
    }

    /// <summary>Hands a downloaded .vmz to MainForm's install pipeline,
    /// then cleans up the temp file. Marshalled to the UI thread since
    /// the download StateChanged handler may fire off-thread.</summary>
    private async Task RunInstallAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _statusLabel.Text = "Downloaded file not found.";
            return;
        }
        try
        {
            await _installCallback(path);
            InstalledCount++;
            _statusLabel.Text = "Installed. Browse for more, or close to return.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Install failed.";
            ThemedMessageBox.Show(this,
                "Install failed:\n" + ex.Message,
                "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _web?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
