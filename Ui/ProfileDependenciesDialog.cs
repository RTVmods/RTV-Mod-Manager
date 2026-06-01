// Lists every mod in the currently-active profile that DECLARES
// dependencies in its mod.txt, paired with the live-state of each
// declared dep (✓ enabled / ⚠ disabled / ✗ missing).
//
// Difference from DependenciesDialog: that one is per-mod, opened
// from the grid's right-click menu. This one is a profile-wide
// rollup — useful for sanity-checking before launching the game
// ("does my profile have any missing required deps?"). Read-only;
// the user fixes a row via the main grid (toggle on / install the
// missing mod / remove the dependent).
//
// Empty states:
//   • No active profile  → "Set an active profile first."
//   • Active profile but no mod declares deps → friendly note.
//   • Active profile with deps, all green → still show the list so
//     the user can see WHAT is being satisfied.

using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class ProfileDependenciesDialog : Form
{
    public ProfileDependenciesDialog(
        ModProfile? activeProfile,
        ModRegistry registry,
        IReadOnlyCollection<string> lockedModIds)
    {
        Text = activeProfile != null
            ? $"Dependencies — Profile '{activeProfile.Name}'"
            : "Dependencies — (no active profile)";
        MinimumSize = new Size(820, 540);
        Width  = 1000;
        Height = 640;
        StartPosition  = FormStartPosition.CenterParent;
        BackColor      = Color.FromArgb(26, 30, 40);
        ForeColor      = Color.FromArgb(220, 225, 235);
        Font           = new Font("Segoe UI", 12f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar  = false;

        var root = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            Padding     = new Padding(16, 14, 16, 14),
            BackColor   = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // summary
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // close
        Controls.Add(root);

        var title = new Label
        {
            Text = activeProfile != null
                ? $"Mods in '{activeProfile.Name}' declaring dependencies"
                : "No active profile",
            AutoSize = true,
            Font     = new Font("Segoe UI", 16f, FontStyle.Bold),
            Margin   = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        // ── Build the dataset ────────────────────────────────────────
        // id-keyed view of the registry so dep-state classification is
        // a single dictionary hit per dep, not an n*m linear scan.
        var byId = new Dictionary<string, ModEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in registry.Entries)
        {
            if (string.IsNullOrEmpty(e.ModId)) continue;
            byId.TryAdd(e.ModId, e);
        }
        var locked = new HashSet<string>(
            lockedModIds, StringComparer.OrdinalIgnoreCase);

        // Profile-visible mod ids — what counts as "in the profile" for
        // labeling. Includes locked mods (they bypass the profile
        // filter in the main grid too, by design).
        var profileIds = activeProfile != null
            ? new HashSet<string>(
                activeProfile.Mods.Select(m => m.ModId),
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The mods we care about: those in the active profile (or
        // locked-and-bypassing) that have at least one declared dep.
        var dependents = new List<ModEntry>();
        if (activeProfile != null)
        {
            foreach (var entry in registry.Entries)
            {
                if (string.IsNullOrEmpty(entry.ModId)) continue;
                if (!profileIds.Contains(entry.ModId)
                    && !locked.Contains(entry.ModId)) continue;
                if (entry.RequiredDependencies.Count == 0
                    && entry.OptionalDependencies.Count == 0) continue;
                dependents.Add(entry);
            }
            // Stable, predictable ordering. By display name (case-
            // insensitive) — matches how the user reads them in the
            // main grid filter.
            dependents.Sort((a, b) => string.Compare(
                DisplayLabel(a), DisplayLabel(b),
                StringComparison.OrdinalIgnoreCase));
        }

        // Count missing required deps for the summary — this is the
        // number the user actually cares about ("can I launch?").
        int missingRequired = 0;
        int disabledRequired = 0;
        foreach (var d in dependents)
        {
            foreach (var dep in d.RequiredDependencies)
            {
                if (!byId.TryGetValue(dep, out var live)) missingRequired++;
                else if (!live.IsEnabled) disabledRequired++;
            }
        }

        var summary = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(900, 0),
            ForeColor   = Color.FromArgb(170, 185, 210),
            Margin      = new Padding(0, 0, 0, 10),
        };
        if (activeProfile == null)
        {
            summary.Text =
                "Set an active profile from the Profiles dialog to see "
                + "its dependency rollup.";
        }
        else if (dependents.Count == 0)
        {
            summary.Text =
                "No mods in this profile declare a `[dependencies]` "
                + "section. Mod authors opt in via mod.txt — most "
                + "RTV mods don't yet.";
        }
        else
        {
            var bits = new List<string>
            {
                $"{dependents.Count} mod(s) with declared deps",
            };
            if (missingRequired > 0)
                bits.Add($"{missingRequired} required dep(s) MISSING");
            if (disabledRequired > 0)
                bits.Add($"{disabledRequired} required dep(s) disabled");
            if (missingRequired == 0 && disabledRequired == 0)
                bits.Add("all required deps satisfied ✓");
            summary.Text = string.Join("  ·  ", bits);
            summary.ForeColor = (missingRequired > 0)
                ? Color.FromArgb(245, 130, 120)
                : (disabledRequired > 0)
                    ? Color.FromArgb(255, 200, 80)
                    : Color.FromArgb(120, 220, 140);
        }
        root.Controls.Add(summary, 0, 1);

        // ── List ────────────────────────────────────────────────────
        // ListView in Details view, grouped per dependent mod. Each
        // group's header is the parent mod; the rows under it are
        // its declared deps with state.
        var list = new ListView
        {
            Dock          = DockStyle.Fill,
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            HeaderStyle   = ColumnHeaderStyle.Nonclickable,
            BackColor     = Color.FromArgb(18, 22, 30),
            ForeColor     = Color.FromArgb(220, 225, 235),
            BorderStyle   = BorderStyle.FixedSingle,
            Font          = new Font("Consolas", 12f),
            // Dropped WinForms ShowGroups — its group-header text
            // colour is OS-controlled (system "highlight" blue) and
            // unreadable on our dark background. Group headers are
            // now baked in as styled rows via AddHeaderRow.
            ShowGroups    = false,
        };
        list.Columns.Add("State",    70);
        list.Columns.Add("Dep ID",   260);
        list.Columns.Add("Kind",     90);
        list.Columns.Add("Status",   460);

        foreach (var dependent in dependents)
        {
            AddHeaderRow(list, dependent);
            AddRows(list, dependent.RequiredDependencies, "required", byId);
            AddRows(list, dependent.OptionalDependencies, "optional", byId);
        }
        root.Controls.Add(list, 0, 2);

        // ── Buttons ─────────────────────────────────────────────────
        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Anchor        = AnchorStyles.Right,
            BackColor     = Color.Transparent,
            Margin        = new Padding(0, 12, 0, 0),
        };
        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width  = 130;
        close.Height = 40;
        close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        btnRow.Controls.Add(close);
        root.Controls.Add(btnRow, 0, 3);

        AcceptButton = close;
        CancelButton = close;
    }

    /// <summary>Adds a header row for `dependent` — bold white text
    /// on a slightly-lighter background, the full mod label baked into
    /// the Dep ID column so it spans visually across the row. Tagged
    /// `_header` so future click handlers can skip it.</summary>
    private static void AddHeaderRow(ListView list, ModEntry dependent)
    {
        var label =
            $"▸ {DisplayLabel(dependent)}   (id: {dependent.ModId}"
            + (string.IsNullOrEmpty(dependent.Version)
                ? "" : $", v{dependent.Version}")
            + ")";
        // 4 cells: leave State/Kind/Status empty, put the whole
        // label in Dep ID so the bold text reads across the row
        // even though ListView can't truly span columns.
        var row = new ListViewItem(new[] { "", label, "", "" })
        {
            // High-contrast pale blue — readable on the row's tinted
            // background, distinct from the green/yellow/red the dep
            // rows use, so the eye reads the headers as a separate
            // tier.
            ForeColor = Color.FromArgb(170, 210, 255),
            BackColor = Color.FromArgb(34, 42, 58),
            Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
            UseItemStyleForSubItems = true,
            Tag       = "_header",
        };
        list.Items.Add(row);
    }

    /// <summary>Same shape as DependenciesDialog.AddRows — keeps the
    /// state-classification logic in one place per row format so the
    /// two dialogs render dep state identically.</summary>
    private static void AddRows(
        ListView list,
        List<string> deps,
        string kind,
        IReadOnlyDictionary<string, ModEntry> registry)
    {
        foreach (var depId in deps)
        {
            string state, status;
            Color  color;
            if (registry.TryGetValue(depId, out var dep))
            {
                if (dep.IsEnabled)
                {
                    state  = "✓";
                    status = "Installed and enabled";
                    color  = Color.FromArgb(120, 220, 140);
                }
                else
                {
                    state  = "⚠";
                    status = "Installed but currently disabled";
                    color  = Color.FromArgb(255, 200, 80);
                }
            }
            else
            {
                state = "✗";
                status = kind == "required"
                    ? "Not installed — required, install or disable the dependent"
                    : "Not installed (optional)";
                color = kind == "required"
                    ? Color.FromArgb(245, 130, 120)
                    : Color.FromArgb(150, 160, 180);
            }
            var row = new ListViewItem(new[] { state, depId, kind, status })
            {
                ForeColor = color,
            };
            list.Items.Add(row);
        }
    }

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);
}
