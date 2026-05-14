// Profile manager dialog — list, save, import, export, delete, and
// apply named mod loadout profiles.
//
// Layout: SplitContainer
//   Left  (240px)  — ListBox of saved profiles + [New] [Import…] [Delete]
//   Right (fill)   — Detail panel: name, description, mod list, [Export…] [Apply ▶]
//
// "Save Current" builds a ModProfile from the live registry and
// prompts for a name. "Import…" loads a JSON file from anywhere.
// "Apply" opens ProfileApplyDialog which handles downloads + cfg writes.

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileManagerDialog : Form
{
    // ── Public outcome ────────────────────────────────────────────────

    /// <summary>True if any profile was Applied and the caller should
    /// rescan / repopulate the mods grid.</summary>
    public bool NeedsRescan { get; private set; }

    // ── Dependencies ──────────────────────────────────────────────────

    private readonly ModRegistry       _registry;
    private readonly ModWorkshopClient _mw;
    private readonly ModConfig         _modConfig;
    private readonly string            _modsDir;

    // ── Data ──────────────────────────────────────────────────────────

    private List<ModProfile> _profiles = new();

    // ── Controls ──────────────────────────────────────────────────────

    private ListBox  _list     = null!;
    private Label    _detName  = null!;
    private Label    _detDesc  = null!;
    private Label    _detMeta  = null!;
    private DataGridView _detGrid = null!;
    private Button   _applyBtn  = null!;
    private Button   _exportBtn = null!;
    private Button   _deleteBtn = null!;
    private Panel    _detailPanel = null!;

    // ── Construction ─────────────────────────────────────────────────

    public ProfileManagerDialog(
        ModRegistry registry,
        ModWorkshopClient mw,
        ModConfig modConfig,
        string modsDir)
    {
        _registry  = registry;
        _mw        = mw;
        _modConfig = modConfig;
        _modsDir   = modsDir;

        InitUi();
        LoadProfiles();
    }

    private void InitUi()
    {
        Text            = "Mod Profiles";
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(26, 30, 40);
        ForeColor       = Color.FromArgb(220, 225, 235);
        Font            = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new Size(880, 560);
        Width  = 1000;
        Height = 660;
        ShowInTaskbar   = false;

        // ── Main title ──────────────────────────────────────────────
        var title = new Label
        {
            Text      = "★  MOD  PROFILES  ★",
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 50, 60),
            Margin    = new Padding(0, 0, 0, 8),
        };

        // ── SplitContainer ──────────────────────────────────────────
        var split = new SplitContainer
        {
            Dock           = DockStyle.Fill,
            Orientation    = Orientation.Vertical,
            SplitterWidth  = 6,
            BackColor      = Color.Transparent,
            SplitterDistance = 240,
        };

        // Left pane
        BuildLeftPane(split.Panel1);

        // Right pane
        _detailPanel = BuildDetailPanel();
        split.Panel2.Controls.Add(_detailPanel);

        // Bottom button row
        var btnRow = BuildBottomRow();

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 3,
            Padding     = new Padding(14, 10, 14, 10),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // split
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // btn row
        root.Controls.Add(title,   0, 0);
        root.Controls.Add(split,   0, 1);
        root.Controls.Add(btnRow,  0, 2);
        Controls.Add(root);
    }

    // ── Left pane ────────────────────────────────────────────────────

    private void BuildLeftPane(SplitterPanel panel)
    {
        _list = new ListBox
        {
            Dock            = DockStyle.Fill,
            BackColor       = Color.FromArgb(18, 22, 30),
            ForeColor       = Color.FromArgb(220, 225, 235),
            Font            = new Font("Segoe UI", 12f),
            BorderStyle     = BorderStyle.FixedSingle,
            SelectionMode   = SelectionMode.One,
            IntegralHeight  = false,
        };
        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        var toolbar = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.Transparent,
        };

        var saveBtn = MainForm.ThemedButton("💾 Save Current");
        saveBtn.Width  = 140;
        saveBtn.Height = 40;
        saveBtn.AutoSize = false;

        var importBtn = MainForm.ThemedButton("📂 Import…");
        importBtn.Width  = 100;
        importBtn.Height = 40;
        importBtn.AutoSize = false;

        _deleteBtn = MainForm.ThemedButton("🗑 Delete");
        _deleteBtn.Width  = 90;
        _deleteBtn.Height = 40;
        _deleteBtn.AutoSize = false;
        _deleteBtn.ForeColor = Color.FromArgb(245, 130, 120);
        _deleteBtn.Enabled   = false;

        toolbar.Resize += (_, _) =>
        {
            saveBtn.Top    = 6;
            importBtn.Top  = 6;
            _deleteBtn.Top = 6;
            saveBtn.Left   = 0;
            importBtn.Left = saveBtn.Right + 4;
            _deleteBtn.Left = importBtn.Right + 4;
        };
        toolbar.Controls.Add(saveBtn);
        toolbar.Controls.Add(importBtn);
        toolbar.Controls.Add(_deleteBtn);

        saveBtn.Click   += (_, _) => SaveCurrentProfile();
        importBtn.Click += (_, _) => ImportProfileFromFile();
        _deleteBtn.Click += (_, _) => DeleteSelected();

        panel.Controls.Add(_list);
        panel.Controls.Add(toolbar);
    }

    // ── Detail pane ──────────────────────────────────────────────────

    private Panel BuildDetailPanel()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding   = new Padding(12, 0, 0, 0),
        };

        _detName = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 225, 235),
            Margin    = new Padding(0, 0, 0, 4),
        };
        _detDesc = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            ForeColor = Color.FromArgb(160, 170, 190),
            Margin    = new Padding(0, 0, 0, 4),
        };
        _detMeta = new Label
        {
            Dock      = DockStyle.Top,
            AutoSize  = true,
            Font      = new Font("Consolas", 11f),
            ForeColor = Color.FromArgb(120, 140, 170),
            Margin    = new Padding(0, 0, 0, 8),
        };

        _detGrid = BuildDetailGrid();

        var detBtnRow = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.Transparent,
        };

        _applyBtn = MainForm.ThemedButton("▶ Apply Profile");
        _applyBtn.Width   = 150;
        _applyBtn.Height  = 40;
        _applyBtn.AutoSize = false;
        _applyBtn.BackColor = Color.FromArgb(45, 90, 55);
        _applyBtn.ForeColor = Color.FromArgb(225, 240, 230);
        _applyBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 160, 100);
        _applyBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 115, 70);
        _applyBtn.Enabled = false;

        _exportBtn = MainForm.ThemedButton("Export…");
        _exportBtn.Width  = 100;
        _exportBtn.Height = 40;
        _exportBtn.AutoSize = false;
        _exportBtn.Enabled  = false;

        detBtnRow.Resize += (_, _) =>
        {
            _applyBtn.Top  = 6;
            _exportBtn.Top = 6;
            _applyBtn.Left  = 0;
            _exportBtn.Left = _applyBtn.Right + 8;
        };
        _applyBtn.Click  += (_, _) => ApplySelected();
        _exportBtn.Click += (_, _) => ExportSelected();

        detBtnRow.Controls.Add(_applyBtn);
        detBtnRow.Controls.Add(_exportBtn);

        // Reverse-add for Dock.Top stacking
        p.Controls.Add(detBtnRow);
        p.Controls.Add(_detGrid);
        p.Controls.Add(_detMeta);
        p.Controls.Add(_detDesc);
        p.Controls.Add(_detName);
        return p;
    }

    private DataGridView BuildDetailGrid()
    {
        var grid = new DataGridView
        {
            Dock  = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows  = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly     = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(18, 22, 30),
            BorderStyle     = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor  = Color.FromArgb(36, 42, 54),
                ForeColor  = Color.FromArgb(220, 225, 235),
                Font       = new Font("Segoe UI", 11f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(36, 42, 54),
            },
            DefaultCellStyle =
            {
                BackColor  = Color.FromArgb(18, 22, 30),
                ForeColor  = Color.FromArgb(200, 210, 230),
                SelectionBackColor = Color.FromArgb(40, 60, 90),
                Font       = new Font("Consolas", 11f),
            },
            GridColor        = Color.FromArgb(40, 46, 58),
            ColumnHeadersHeight = 32,
            RowTemplate      = { Height = 28 },
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "On",
            HeaderText = "On",
            Width      = 36,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name         = "ModName",
            HeaderText   = "Mod",
            ReadOnly     = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 140,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Version",
            HeaderText = "Version",
            Width      = 90,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name       = "Priority",
            HeaderText = "Prio",
            Width      = 60,
            ReadOnly   = true,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });

        foreach (DataGridViewColumn col in grid.Columns)
            col.SortMode = DataGridViewColumnSortMode.NotSortable;

        return grid;
    }

    // ── Bottom row ───────────────────────────────────────────────────

    private Panel BuildBottomRow()
    {
        var p = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 52,
            BackColor = Color.FromArgb(30, 34, 44),
            Margin    = new Padding(0, 8, 0, 0),
        };
        var closeBtn = MainForm.ThemedButton("Close");
        closeBtn.Width  = 100;
        closeBtn.Height = 40;
        closeBtn.AutoSize = false;
        closeBtn.Click += (_, _) => Close();
        p.Resize += (_, _) =>
        {
            closeBtn.Top  = 6;
            closeBtn.Left = p.Width - closeBtn.Width - 16;
        };
        p.Controls.Add(closeBtn);
        CancelButton = closeBtn;
        return p;
    }

    // ── Data loading ─────────────────────────────────────────────────

    private void LoadProfiles()
    {
        _profiles = ModProfile.LoadAll();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _profiles) _list.Items.Add(p.Name);
        _list.EndUpdate();
        ShowSelected();
    }

    private void ShowSelected()
    {
        var profile = SelectedProfile();
        _deleteBtn.Enabled = profile != null;
        _applyBtn.Enabled  = profile != null;
        _exportBtn.Enabled = profile != null;

        if (profile == null)
        {
            _detName.Text = "(no profile selected)";
            _detDesc.Text = "";
            _detMeta.Text = "";
            _detGrid.Rows.Clear();
            return;
        }

        _detName.Text = profile.Name;
        _detDesc.Text = string.IsNullOrEmpty(profile.Description)
            ? "" : $"\"{profile.Description}\"";
        var updated = profile.UpdatedAt.HasValue
            ? $"  ·  updated {profile.UpdatedAt.Value.ToLocalTime():yyyy-MM-dd}"
            : "";
        var bundled = profile.BundledArchivesCount();
        var size    = profile.BundledArchivesSize();
        var bundleNote = bundled > 0
            ? $"  ·  {bundled} archive(s) bundled, {FormatBytes(size)}"
            : "  ·  metadata-only (no archives bundled)";
        _detMeta.Text =
            $"{profile.Mods.Count} mods  ·  "
            + $"created {profile.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}{updated}"
            + bundleNote;

        _detGrid.Rows.Clear();
        foreach (var m in profile.Mods.OrderBy(m => m.Priority).ThenBy(m => m.DisplayName))
        {
            var i = _detGrid.Rows.Add();
            var r = _detGrid.Rows[i];
            r.Cells["On"].Value       = m.IsEnabled ? "✓" : "✗";
            r.Cells["ModName"].Value  = m.DisplayName;
            r.Cells["Version"].Value  = m.Version;
            r.Cells["Priority"].Value = m.Priority;
            // Bundle indicator in tooltip — mods missing an archive
            // will fall back to ModWorkshop on apply.
            var hasBundle = !string.IsNullOrEmpty(profile.ResolveBundledArchive(m));
            r.Cells["ModName"].ToolTipText = hasBundle
                ? $"id: {m.ModId}\nbundled: {m.ArchiveFileName}"
                : $"id: {m.ModId}\nno bundled archive — will fall back to ModWorkshop";
            r.Cells["On"].Style.ForeColor = m.IsEnabled
                ? Color.FromArgb(120, 200, 130)
                : Color.FromArgb(160, 80, 80);
            if (!hasBundle) r.DefaultCellStyle.ForeColor = Color.FromArgb(160, 170, 190);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private ModProfile? SelectedProfile()
    {
        var idx = _list.SelectedIndex;
        return (idx >= 0 && idx < _profiles.Count) ? _profiles[idx] : null;
    }

    // ── Actions ───────────────────────────────────────────────────────

    private void SaveCurrentProfile()
    {
        var name = TextInputDialog.Prompt(this,
            "Save Profile",
            "Profile name:",
            $"Profile {DateTime.Now:yyyy-MM-dd}");
        if (string.IsNullOrWhiteSpace(name)) return;

        var desc = TextInputDialog.Prompt(this,
            "Save Profile — description (optional)",
            "Short description (leave blank to skip):",
            "");

        // Check for existing profile with same name
        var existing = _profiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var dr = MessageBox.Show(this,
                $"A profile named '{name}' already exists.\n\n"
                + "Overwrite it (existing bundled archives will be replaced "
                + "with copies of the currently-installed mods)?",
                "Overwrite?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (dr != DialogResult.Yes) return;
        }

        // Build the profile + bundle archives. Use a wait cursor so a
        // large mod folder (~50 enabled .vmz, hundreds of MB) doesn't
        // look like the UI froze.
        Cursor = Cursors.WaitCursor;
        List<ProfileMod> failed;
        ModProfile profile;
        try
        {
            profile = ModProfile.FromRegistry(name, desc ?? "", _registry.Entries);
            // Only bundle ENABLED mods — disabled ones are still listed
            // in profile.Mods (so apply can restore their cfg state)
            // but copying their archives doubles disk usage without a
            // matching benefit. The apply path falls back to MW for any
            // disabled mod that's gone missing.
            failed = profile.SaveWithBundles(_registry.Entries.Where(e => e.IsEnabled));
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (failed.Count > 0)
        {
            var sample = string.Join("\n  • ",
                failed.Take(6).Select(m => $"{m.DisplayName} ({m.Version})"));
            var more = failed.Count > 6 ? $"\n  … and {failed.Count - 6} more" : "";
            MessageBox.Show(this,
                $"Profile saved, but {failed.Count} mod(s) couldn't be bundled:\n  • {sample}{more}\n\n"
                + "These are typically directory mods (unpacked) or files that "
                + "were locked at copy time. On apply, the manager will fall "
                + "back to downloading them from ModWorkshop if they have a "
                + "linked ID.",
                "Partial bundle",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        LoadProfiles();
        // Select the newly saved profile
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }
    }

    private void ImportProfileFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Import Profile",
            Filter = "Profile bundle (*.vmprofile;*.zip)|*.vmprofile;*.zip"
                   + "|Profile JSON (*.json)|*.json"
                   + "|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        ModProfile? profile;
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        Cursor = Cursors.WaitCursor;
        try
        {
            profile = (ext == ".vmprofile" || ext == ".zip")
                ? ModProfile.LoadFromZip(dlg.FileName)
                : ModProfile.LoadFromJsonFile(dlg.FileName);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        if (profile == null)
        {
            MessageBox.Show(this,
                "Could not parse the selected file as a mod profile.\n\n"
                + "Expected a .vmprofile zip (bundled archives) or a "
                + ".json file (metadata only).",
                "Import failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // If we imported a JSON (no folder), save it through SaveMetadataOnly
        // so it lands in a folder under ProfilesDir. Zip imports already
        // unzip themselves into a folder.
        if (string.IsNullOrEmpty(profile.FolderPath))
        {
            profile.SaveMetadataOnly();
        }

        LoadProfiles();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), profile.Name, StringComparison.OrdinalIgnoreCase))
            { _list.SelectedIndex = i; break; }
        }

        // Ask if they want to apply immediately
        var applyNow = MessageBox.Show(this,
            $"Profile '{profile.Name}' imported "
            + $"({profile.BundledArchivesCount()} archive(s) bundled).\n\n"
            + "Apply it now?",
            "Apply?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (applyNow == DialogResult.Yes) ApplySelected();
    }

    private void ExportSelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;
        using var dlg = new SaveFileDialog
        {
            Title    = "Export Profile",
            Filter   = "Profile bundle (*.vmprofile)|*.vmprofile"
                     + "|Profile JSON only (*.json)|*.json",
            FileName = ModProfile.SafeFileName(profile.Name) + ".vmprofile",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".vmprofile" || ext == ".zip")
            {
                profile.ExportToZip(dlg.FileName);
            }
            else
            {
                // Metadata-only export. Strip ArchiveFileName fields
                // so the recipient knows there are no bundles to look for.
                var copy = new ModProfile
                {
                    Name        = profile.Name,
                    Description = profile.Description,
                    CreatedAt   = profile.CreatedAt,
                    UpdatedAt   = profile.UpdatedAt,
                    Mods = profile.Mods.Select(m => new ProfileMod
                    {
                        ModId = m.ModId,
                        DisplayName = m.DisplayName,
                        Version = m.Version,
                        IsEnabled = m.IsEnabled,
                        Priority = m.Priority,
                        ModWorkshopId = m.ModWorkshopId,
                    }).ToList(),
                };
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(copy,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            MessageBox.Show(this,
                $"Profile exported to:\n{dlg.FileName}",
                "Exported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Export failed:\n{ex.Message}",
                "Export error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void DeleteSelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;
        var dr = MessageBox.Show(this,
            $"Delete profile '{profile.Name}'?\nThis cannot be undone.",
            "Delete profile",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;
        profile.Delete();
        LoadProfiles();
    }

    private void ApplySelected()
    {
        var profile = SelectedProfile();
        if (profile == null) return;

        using var dlg = new ProfileApplyDialog(
            profile, _registry, _mw, _modConfig, _modsDir);
        dlg.ShowDialog(this);
        if (dlg.Applied)
        {
            NeedsRescan = true;
            // Refresh the profile list in case a profile was modified
            LoadProfiles();
        }
    }
}
