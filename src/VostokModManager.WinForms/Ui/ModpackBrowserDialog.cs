// Modpacks published on VostokMods.
//
// Lists every modpack the site offers (name, author, mod count, downloads,
// summary) with a filter box, and lets the user install one into the
// active profile. The install itself is MainForm's modpack import (the
// manifest is read, each mod is downloaded at the version the pack
// lists, and the regular import plan is shown), so this dialog only
// picks the pack and hands its slug back.

using VostokModManager.Api;

namespace VostokModManager.Ui;

public class ModpackBrowserDialog : Form
{
    private readonly VostokModsClient _vm;
    private readonly Func<string, Task> _installCallback;
    private readonly string _activeProfileName;

    private DataGridView _grid = null!;
    private Label _headerLabel = null!;
    private TextBox _filter = null!;
    private Button _installBtn = null!;
    private Button _pageBtn = null!;
    private Button _refreshBtn = null!;
    private Button _closeBtn = null!;
    private bool _busy;

    private List<VmModpackSummary> _packs = new();

    /// <summary>True when a pack was installed, so MainForm refreshes
    /// on close.</summary>
    public bool Changed { get; private set; }

    public ModpackBrowserDialog(
        VostokModsClient vm,
        string activeProfileName,
        Func<string, Task> installCallback)
    {
        _vm = vm;
        _activeProfileName = activeProfileName;
        _installCallback = installCallback;
        InitUi();
        Shown += async (_, _) => await ReloadAsync();
    }

    private void InitUi()
    {
        Text            = "VostokMods Modpacks";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(880, 520);
        Width           = 1100;
        Height          = 640;
        ShowInTaskbar   = false;
        DialogSizing.ClampToWorkingArea(this);

        var title = new Label
        {
            Text      = "📦 Modpacks on VostokMods",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };

        _headerLabel = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin    = new Padding(0, 0, 0, 10),
            Text      = "Loading the modpack list …",
        };

        var filterRow = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            ColumnCount = 2,
            BackColor   = Color.Transparent,
            Margin      = new Padding(0, 0, 0, 8),
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        filterRow.Controls.Add(new Label
        {
            Text = "Filter:", AutoSize = true, Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Margin = new Padding(0, 6, 8, 0),
        }, 0, 0);
        _filter = new TextBox
        {
            Dock        = DockStyle.Fill,
            BackColor   = Color.FromArgb(18, 22, 30),
            ForeColor   = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "filter by name, author or description",
        };
        _filter.TextChanged += (_, _) => PopulateGrid();
        filterRow.Controls.Add(_filter, 1, 0);

        _grid = BuildGrid();

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
        content.Controls.Add(title,        0, 0);
        content.Controls.Add(_headerLabel, 0, 1);
        content.Controls.Add(filterRow,    0, 2);
        content.Controls.Add(_grid,        0, 3);

        Controls.Add(content);
        Controls.Add(BuildButtonRow());
    }

    private DataGridView BuildGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 30 },
            GridColor = Color.FromArgb(40, 46, 58),
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 42, 54);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(210, 220, 235);
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Color.FromArgb(18, 22, 30);
        g.DefaultCellStyle.ForeColor = Color.FromArgb(212, 220, 232);
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 60, 90);
        g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(240, 245, 250);
        g.DefaultCellStyle.Font = new Font("Segoe UI", 11f);

        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = "Modpack", Width = 260,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Author", HeaderText = "Author", Width = 150,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mods", HeaderText = "Mods", Width = 70,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Downloads", HeaderText = "Downloads", Width = 100,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Summary", HeaderText = "Summary",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });

        g.SelectionChanged += (_, _) => UpdateButtons();
        g.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await InstallSelectedAsync();
        };
        return g;
    }

    private Panel BuildButtonRow()
    {
        var row = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Padding = new Padding(16, 10, 16, 12),
            BackColor = Color.FromArgb(22, 26, 35),
        };
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent,
        };
        _installBtn = MainForm.ThemedButton("⬇ Install into active profile");
        _installBtn.Width = 260;
        _installBtn.BackColor = Color.FromArgb(45, 90, 55);
        _installBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _installBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _installBtn.Click += async (_, _) => await InstallSelectedAsync();
        _pageBtn = MainForm.ThemedButton("🌐 Open page");
        _pageBtn.Width = 150;
        _pageBtn.Margin = new Padding(8, 0, 0, 0);
        _pageBtn.Click += (_, _) => OpenSelectedPage();
        _refreshBtn = MainForm.ThemedButton("⟳ Refresh");
        _refreshBtn.Width = 130;
        _refreshBtn.Margin = new Padding(8, 0, 0, 0);
        _refreshBtn.Click += async (_, _) => await ReloadAsync();
        left.Controls.Add(_installBtn);
        left.Controls.Add(_pageBtn);
        left.Controls.Add(_refreshBtn);

        _closeBtn = MainForm.ThemedButton("Close");
        _closeBtn.Width = 110;
        _closeBtn.Dock = DockStyle.Right;
        _closeBtn.Click += (_, _) => Close();

        row.Controls.Add(left);
        row.Controls.Add(_closeBtn);
        return row;
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        _headerLabel.Text = "Loading the modpack list …";
        try
        {
            var page = await _vm.ListModpacksAsync();
            _packs = page.Items.ToList();
            _headerLabel.Text = _packs.Count == 0
                ? "VostokMods lists no modpacks yet."
                : $"{_packs.Count} modpack" + (_packs.Count == 1 ? "" : "s")
                  + " on VostokMods. Installing adds every mod of the pack to "
                  + $"'{_activeProfileName}', at the versions the pack lists.";
        }
        catch (Exception ex)
        {
            _packs = new();
            _headerLabel.Text = "Couldn't read the modpack list from VostokMods — " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
        PopulateGrid();
    }

    private void PopulateGrid()
    {
        var q = _filter.Text.Trim();
        _grid.Rows.Clear();
        foreach (var p in _packs)
        {
            if (q.Length > 0
                && !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !p.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !p.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !p.Slug.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            var idx = _grid.Rows.Add(p.Name, p.Author, p.ModCount, p.Downloads, p.Summary);
            _grid.Rows[idx].Tag = p;
        }
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        UpdateButtons();
    }

    private VmModpackSummary? Selected()
        => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as VmModpackSummary : null;

    private void UpdateButtons()
    {
        var has = Selected() != null;
        _installBtn.Enabled = has && !_busy;
        _pageBtn.Enabled = has;
        _refreshBtn.Enabled = !_busy;
    }

    private async Task InstallSelectedAsync()
    {
        var pack = Selected();
        if (pack == null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            await _installCallback(pack.Slug);
            Changed = true;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void OpenSelectedPage()
    {
        var pack = Selected();
        if (pack == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = pack.PageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, ex.Message, "Couldn't open browser",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
