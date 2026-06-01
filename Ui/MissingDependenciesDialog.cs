// "These mods declared deps that aren't installed" prompt. Triggered
// after Install mod… / Import list… / drag-drop install finishes — if
// the freshly-installed mods declare any [dependencies] entries that
// aren't satisfied by the live registry, the user gets this dialog
// so they can resolve the gap right then (rather than discovering
// the broken dep mid-launch).
//
// What the dialog can do:
//   • Per-row paste a ModWorkshop URL or numeric id → click Download
//     → packager fetches via the existing ModWorkshopClient and
//     installs into the mods folder.
//   • Click "Find on MW" → opens modworkshop.net in the browser with
//     the dep slug as search input, so the user can locate the mod
//     and copy its URL.
//   • Skip individual rows or just close the dialog.
//
// What the dialog can't do (intentionally):
//   • Auto-resolve dep slugs → MW ids. There's no public MW lookup
//     for "find the mod whose manifest id is X", so the user has to
//     paste the URL themselves. Once they do, download + library-
//     capture is fully automatic.
//
// Library-resolvable deps are NOT shown here — the caller copies
// them in automatically before opening this dialog and only passes
// the genuinely-missing ones through.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class MissingDependenciesDialog : Form
{
    public class MissingDep
    {
        public string ModId      { get; init; } = "";
        public string ParentName { get; init; } = "";  // mod that declared this dep
        public bool   Required   { get; init; }
    }

    /// <summary>True when at least one dep was successfully
    /// downloaded — caller rescans the registry on close.</summary>
    public bool AnyDownloaded { get; private set; }

    private readonly ModWorkshopClient _mw;
    private readonly string            _modsDir;
    private readonly List<MissingDep>  _deps;

    private DataGridView _grid = null!;
    private TextBox      _log  = null!;

    /// <summary>Per-dep MW id hints sourced from the parent mod's
    /// `[dependency_sources]` section. Pre-fills the MW input
    /// column so the user can just click Download All instead of
    /// pasting URLs for each dep that the packager already
    /// recorded sources for. Keyed by dep mod_id (case-insensitive).
    /// </summary>
    private readonly Dictionary<string, int> _knownMwIds;

    public MissingDependenciesDialog(
        ModWorkshopClient mw,
        string            modsDir,
        IEnumerable<MissingDep> missing,
        IReadOnlyDictionary<string, int>? knownMwIds = null)
    {
        _mw      = mw;
        _modsDir = modsDir;
        _knownMwIds = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        if (knownMwIds != null)
            foreach (var kvp in knownMwIds)
                _knownMwIds[kvp.Key] = kvp.Value;
        _deps    = missing
            // Required first (the user cares more), then alpha by id.
            .OrderByDescending(d => d.Required)
            .ThenBy(d => d.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Text = $"Missing dependencies ({_deps.Count})";
        MinimumSize = new Size(900, 520);
        Width  = 1050;
        Height = 620;
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        ShowInTaskbar  = false;

        BuildUi();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // intro
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65f)); // grid
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35f)); // log
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // buttons
        Controls.Add(root);

        var title = new Label
        {
            Text     = "🧩 Missing dependencies",
            AutoSize = true,
            Font     = new Font("Segoe UI", 18f, FontStyle.Bold),
            Margin   = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(title, 0, 0);

        int req = _deps.Count(d => d.Required);
        int opt = _deps.Count - req;
        var intro = new Label
        {
            Text = $"{req} required + {opt} optional dependency entries weren't found in your "
                 + "installed mods or your local library. Paste a ModWorkshop URL or numeric id "
                 + "into the row and click Download. Use “Find on MW” to open a browser search "
                 + "if you don't have the URL handy.",
            AutoSize = true,
            MaximumSize = new Size(950, 0),
            ForeColor = Color.FromArgb(170, 185, 210),
            Margin = new Padding(0, 0, 0, 10),
        };
        root.Controls.Add(intro, 0, 1);

        _grid = BuildGrid();
        root.Controls.Add(_grid, 0, 2);

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(180, 220, 160),
            Font = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 10, 0, 0),
        };
        root.Controls.Add(_log, 0, 3);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 10, 0, 0),
        };
        var close = MainForm.ThemedButton("Close");
        close.Width = 110; close.Height = 40; close.AutoSize = false;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close);

        var dlAll = MainForm.ThemedButton("⬇ Download all filled rows");
        dlAll.Width = 260; dlAll.Height = 40; dlAll.AutoSize = false;
        dlAll.BackColor = Color.FromArgb(45, 70, 100);
        dlAll.ForeColor = Color.FromArgb(225, 240, 255);
        dlAll.FlatAppearance.BorderColor = Color.FromArgb(90, 140, 200);
        dlAll.Click += async (_, _) => await DownloadAllAsync();
        btnRow.Controls.Add(dlAll);
        root.Controls.Add(btnRow, 0, 4);

        CancelButton = close;
    }

    private DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(36, 42, 54),
                ForeColor = Color.FromArgb(220, 225, 235),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor = Color.FromArgb(18, 22, 30),
                ForeColor = Color.FromArgb(220, 225, 235),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                SelectionForeColor = Color.FromArgb(255, 255, 255),
                Font = new Font("Consolas", 11f),
            },
            GridColor = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 34,
            RowTemplate = { Height = 32 },
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Kind", HeaderText = "Kind", Width = 90, ReadOnly = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ModId", HeaderText = "mod_id", Width = 220, ReadOnly = true,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Parent", HeaderText = "Needed by", Width = 200, ReadOnly = true,
            DefaultCellStyle = { ForeColor = Color.FromArgb(170, 185, 210) },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MwInput", HeaderText = "MW URL or id",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 200,
        });
        // Status text column updated by Download / DownloadAll.
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status", HeaderText = "Status", Width = 160, ReadOnly = true,
            DefaultCellStyle = { ForeColor = Color.FromArgb(170, 185, 210) },
        });
        // Per-row action buttons.
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "FindBtn", HeaderText = "Find", Width = 90,
            Text = "Find on MW", UseColumnTextForButtonValue = true,
        });
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "DlBtn", HeaderText = "Download", Width = 100,
            Text = "Download", UseColumnTextForButtonValue = true,
        });
        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        for (int i = 0; i < _deps.Count; i++)
        {
            var d = _deps[i];
            var idx = grid.Rows.Add();
            var row = grid.Rows[idx];
            row.Cells["Kind"].Value     = d.Required ? "required" : "optional";
            row.Cells["Kind"].Style.ForeColor = d.Required
                ? Color.FromArgb(245, 180, 110)
                : Color.FromArgb(170, 200, 240);
            row.Cells["ModId"].Value    = d.ModId;
            row.Cells["Parent"].Value   = d.ParentName;
            // Pre-fill MW id if the parent mod's
            // [dependency_sources] section recorded one — author
            // packaged this dep with downloadable metadata.
            if (_knownMwIds.TryGetValue(d.ModId, out var knownMw)
                && knownMw > 0)
            {
                row.Cells["MwInput"].Value = knownMw.ToString();
                row.Cells["Status"].Value  = "Ready (auto-filled)";
                row.Cells["Status"].Style.ForeColor =
                    Color.FromArgb(170, 200, 240);
            }
            else
            {
                row.Cells["MwInput"].Value = "";
                row.Cells["Status"].Value  = "Pending";
            }
        }

        grid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _deps.Count) return;
            var col = grid.Columns[e.ColumnIndex].Name;
            if (col == "FindBtn")
                OpenMwSearch(_deps[e.RowIndex].ModId);
            else if (col == "DlBtn")
                await DownloadRowAsync(e.RowIndex);
        };

        return grid;
    }

    // ── Actions ───────────────────────────────────────────────────

    private static void OpenMwSearch(string slug)
    {
        // ModWorkshop search by free text. The slug often differs
        // from the display name, but it's a useful starting term —
        // landing the user on a search page is faster than them
        // opening a browser and typing it themselves.
        var url = "https://modworkshop.net/g/roadtovostok?q="
                + Uri.EscapeDataString(slug);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch { /* browser launch failures are common in odd envs; user can retry */ }
    }

    private async Task DownloadRowAsync(int rowIdx)
    {
        var row = _grid.Rows[rowIdx];
        var input = row.Cells["MwInput"].Value as string ?? "";
        var mwId  = ManifestEditor.ParseModWorkshopIdInput(input);
        if (mwId <= 0)
        {
            row.Cells["Status"].Value = "(paste MW URL or id first)";
            row.Cells["Status"].Style.ForeColor = Color.FromArgb(245, 180, 110);
            return;
        }
        await DoDownloadAsync(rowIdx, mwId);
    }

    private async Task DownloadAllAsync()
    {
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            var input = _grid.Rows[i].Cells["MwInput"].Value as string ?? "";
            var mwId  = ManifestEditor.ParseModWorkshopIdInput(input);
            if (mwId <= 0) continue;
            await DoDownloadAsync(i, mwId);
        }
    }

    private async Task DoDownloadAsync(int rowIdx, int mwId)
    {
        var row = _grid.Rows[rowIdx];
        var dep = _deps[rowIdx];
        row.Cells["Status"].Value = "Downloading…";
        row.Cells["Status"].Style.ForeColor = Color.FromArgb(170, 200, 240);
        Log($"⬇ {dep.ModId} (MW {mwId}) …");
        try
        {
            // Use the dep slug as the live filename so the resulting
            // file is identifiable in the mods folder without
            // depending on whatever the author named the upload.
            var fileName = $"{ModProfile.SafeFileName(dep.ModId)}.vmz";
            var dest     = Path.Combine(_modsDir, fileName);
            if (File.Exists(dest))
            {
                // Don't clobber whatever's there — uncommon edge
                // (the dep might be partially-resolved between
                // Apply and this dialog). Bump to a unique name.
                dest = Path.Combine(_modsDir, MakeUniqueName(fileName));
            }
            await Task.Run(() => _mw.DownloadLatestAsync(mwId, dest));

            // Library-snapshot so future profile switches don't
            // have to redownload.
            try { ModLibrary.Add(_modsDir, dest); } catch { }

            row.Cells["Status"].Value = "✓ Installed";
            row.Cells["Status"].Style.ForeColor = Color.FromArgb(120, 220, 140);
            AnyDownloaded = true;
            Log($"  ✓ → {Path.GetFileName(dest)}");
        }
        catch (Exception ex)
        {
            row.Cells["Status"].Value = "✗ Failed";
            row.Cells["Status"].Style.ForeColor = Color.FromArgb(245, 130, 120);
            Log($"  ✗ {ex.Message}");
        }
    }

    private string MakeUniqueName(string filename)
    {
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}{ext}";
            if (!File.Exists(Path.Combine(_modsDir, candidate))) return candidate;
        }
        return $"{baseName}_{Guid.NewGuid():N}{ext}";
    }

    private void Log(string m)
    {
        _log.AppendText(m + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }
}
