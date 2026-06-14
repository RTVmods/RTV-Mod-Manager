// Collect Support Package dialog.
//
// One-click: snapshot the active profile (as a directly-importable
// .vmprofile bundle + loose metadata), bundle the entire Godot logs
// directory, and write it all to a .vmzlog the user saves wherever they
// like. The heavy lifting lives in Domain/SupportPackage; this dialog is
// just the themed front-end + progress reporting.
//
// Mirrors ProfileApplyDialog's dark palette + ProgressBar/log panel and
// uses the same SaveFileDialog convention as ProfileManagerDialog's
// Export and ThemedMessageBox for completion/error.

using System.Diagnostics;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class SupportPackageDialog : Form
{
    private readonly ModProfile?  _activeProfile;
    private readonly ModRegistry  _registry;
    private readonly string       _modsDir;

    private Label       _summaryLabel = null!;
    private CheckBox    _includeRootLogs = null!;
    private ProgressBar _progressBar = null!;
    private TextBox     _log = null!;
    private Panel       _progressPanel = null!;
    private Button      _buildBtn = null!;
    private Button      _closeBtn = null!;
    private bool        _running;

    public SupportPackageDialog(
        ModProfile? activeProfile, ModRegistry registry, string modsDir)
    {
        _activeProfile = activeProfile;
        _registry      = registry;
        _modsDir       = modsDir;
        InitUi();
    }

    private void InitUi()
    {
        Text            = "Collect Support Package";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(720, 520);
        Width           = 820;
        Height          = 600;
        ShowInTaskbar   = false;
        DialogSizing.ClampToWorkingArea(this);

        var title = new Label
        {
            Text      = "📦 Collect Support Package",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        _summaryLabel = new Label
        {
            Text      = BuildSummaryText(),
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
        };

        _includeRootLogs = new CheckBox
        {
            Text      = "Include other mod logs from the game folder "
                      + "(modloader_filescope.log, etc.)",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Checked   = true,
            ForeColor = Color.FromArgb(200, 210, 225),
            Margin    = new Padding(0, 0, 0, 10),
        };

        _progressPanel = BuildProgressPanel();
        _progressPanel.Visible = false;

        var btnRow = BuildButtonRow();

        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 4,
            Padding     = new Padding(16, 12, 16, 12),
            BackColor   = Color.Transparent,
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        content.Controls.Add(title,            0, 0);
        content.Controls.Add(_summaryLabel,    0, 1);
        content.Controls.Add(_includeRootLogs, 0, 2);
        content.Controls.Add(_progressPanel,   0, 3);

        Controls.Add(content);
        Controls.Add(btnRow); // docked bottom
    }

    private string BuildSummaryText()
    {
        var profileName = _activeProfile?.Name;
        var modCount    = _activeProfile?.Mods.Count ?? _registry.Entries.Count;
        var logs        = SupportPackage.CountLogFiles();

        var profileLine = string.IsNullOrEmpty(profileName)
            ? $"No active profile — a snapshot of all {modCount} installed mods will be bundled."
            : $"Active profile: \"{profileName}\"  ({modCount} mods)";

        var logsLine = logs > 0
            ? $"Godot logs found: {logs} file(s) in {SupportPackage.GodotLogsDir}"
            : $"⚠ No Godot logs found at {SupportPackage.GodotLogsDir}";

        return profileLine + Environment.NewLine
            + logsLine + Environment.NewLine + Environment.NewLine
            + "The package (.vmzlog) will contain:" + Environment.NewLine
            + "  • profile.vmprofile — load it straight into the Profiles Manager" + Environment.NewLine
            + "  • profile.json — quick-read mod list" + Environment.NewLine
            + "  • logs/ — the full Godot log directory" + Environment.NewLine
            + "  • mod_config.cfg — the in-game loader's state" + Environment.NewLine
            + "  • support-info.json — manager + MML versions and counts";
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding   = new Padding(0, 0, 0, 10),
        };
        _progressBar = new ProgressBar
        {
            Dock    = DockStyle.Top,
            Height  = 24,
            Minimum = 0,
            Maximum = 100,
            Style   = ProgressBarStyle.Continuous,
            BackColor = Color.FromArgb(30, 36, 48),
            ForeColor = Color.FromArgb(90, 160, 100),
        };
        _log = new TextBox
        {
            Dock      = DockStyle.Fill,
            Multiline = true,
            ReadOnly  = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(180, 220, 160),
            Font      = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.None,
        };
        p.Controls.Add(_log);
        p.Controls.Add(_progressBar);
        return p;
    }

    private Panel BuildButtonRow()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 56,
            BackColor = Color.FromArgb(30, 34, 44),
        };
        _buildBtn = MainForm.ThemedButton("💾 Save & Build…");
        _buildBtn.Width  = 170;
        _buildBtn.Height = 40;
        _buildBtn.AutoSize  = false;
        _buildBtn.BackColor = Color.FromArgb(45, 90, 55);
        _buildBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _buildBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _buildBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _buildBtn.Anchor  = AnchorStyles.Right | AnchorStyles.Top;
        _buildBtn.Click  += async (_, _) => await RunBuildAsync();

        _closeBtn = MainForm.ThemedButton("Close");
        _closeBtn.Width  = 100;
        _closeBtn.Height = 40;
        _closeBtn.AutoSize = false;
        _closeBtn.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
        _closeBtn.Click   += (_, _) => Close();

        p.Resize += (_, _) =>
        {
            _closeBtn.Top  = 8;
            _buildBtn.Top  = 8;
            _closeBtn.Left = p.Width - _closeBtn.Width - 16;
            _buildBtn.Left = _closeBtn.Left - _buildBtn.Width - 8;
        };
        p.Controls.Add(_buildBtn);
        p.Controls.Add(_closeBtn);
        return p;
    }

    private async Task RunBuildAsync()
    {
        if (_running) return;

        var profileName = _activeProfile?.Name ?? "Support Snapshot";
        var suggested   = SupportPackage.SuggestedFileName(profileName, DateTime.UtcNow);

        string outPath;
        using (var dlg = new SaveFileDialog
        {
            Title           = "Save support package",
            Filter          = "Vostok support package (*.vmzlog)|*.vmzlog",
            FileName        = suggested,
            OverwritePrompt = true,
        })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            outPath = dlg.FileName;
        }

        _running = true;
        _buildBtn.Enabled = false;
        _closeBtn.Enabled = false;
        _progressPanel.Visible = true;

        void Log(string m)
        {
            _log.AppendText(m + Environment.NewLine);
            _log.SelectionStart  = _log.TextLength;
            _log.SelectionLength = 0;
            _log.ScrollToCaret();
        }

        // Snapshot the live entries on the UI thread before going async —
        // ModEntry is a plain data object so this list is safe to read
        // from the background task.
        var entries        = _registry.Entries.ToList();
        var includeRoot    = _includeRootLogs.Checked;
        var activeProfile  = _activeProfile;
        var modsDir        = _modsDir;

        var progress = new Progress<(string msg, int pct)>(t =>
        {
            _progressBar.Value = Math.Clamp(t.pct, 0, 100);
            Log(t.msg);
        });

        try
        {
            var result = await Task.Run(() =>
                SupportPackage.Build(
                    activeProfile, entries, modsDir, outPath, includeRoot,
                    (msg, pct) => ((IProgress<(string, int)>)progress).Report((msg, pct))));

            var sizeMb = result.BytesWritten / (1024.0 * 1024.0);
            Log("");
            Log($"Package written: {result.BytesWritten:N0} bytes ({sizeMb:F1} MB)");
            Log($"Mods bundled: {result.ModCount}   Log files: {result.LogFileCount}");
            if (result.Warnings.Count > 0)
            {
                Log("");
                Log($"Warnings ({result.Warnings.Count}):");
                foreach (var w in result.Warnings) Log("  • " + w);
            }

            var msg = $"Support package saved to:\n{outPath}\n\n"
                    + $"{sizeMb:F1} MB · {result.ModCount} mods · "
                    + $"{result.LogFileCount} log file(s)";
            if (result.Warnings.Count > 0)
                msg += $"\n\n{result.Warnings.Count} warning(s) — see the log above "
                     + "(the package was still created).";

            var pick = ThemedMessageBox.Show(
                this, msg + "\n\nReveal it in Explorer?",
                "Support package created",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (pick == DialogResult.Yes)
            {
                try { Process.Start("explorer.exe", "/select,\"" + outPath + "\""); }
                catch { /* non-fatal — the file is already on disk */ }
            }
        }
        catch (Exception ex)
        {
            Log("");
            Log("FAILED: " + ex.Message);
            ThemedMessageBox.Show(
                this, "Failed to build the support package:\n" + ex.Message,
                "Support package error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _running = false;
            _buildBtn.Enabled = true;
            _closeBtn.Enabled = true;
        }
    }
}
