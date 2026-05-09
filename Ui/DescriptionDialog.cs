// Modal that shows a mod's ModWorkshop description text. Read-only
// in v1 — Refresh re-fetches /mods/<id> on demand. The caller
// decides whether to pass cached text (instant open, no spinner)
// or empty (forces an immediate fetch).

using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class DescriptionDialog : Form
{
    private readonly ModEntry _entry;
    private readonly ModWorkshopClient _client;
    private readonly Action<string>? _onCached;
    private readonly TextBox _body;
    private readonly Label _status;
    private readonly Button _refresh;

    /// <summary>`cached` is the previously-saved description (may be
    /// empty). `onCached` is called whenever a fresh fetch
    /// succeeds, with the new text — caller persists it to settings
    /// so subsequent opens are instant.</summary>
    public DescriptionDialog(
        ModEntry entry,
        ModWorkshopClient client,
        string cached,
        Action<string>? onCached = null)
    {
        _entry = entry;
        _client = client;
        _onCached = onCached;

        Text = $"Description — {DisplayLabel(entry)}";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
        Font = new Font("Segoe UI", 13f);
        FormBorderStyle = FormBorderStyle.Sizable;
        // Re-sized for the +2pt-bumped fonts. Old 560 height clipped
        // the bottom button row.
        MinimumSize = new Size(720, 540);
        Width = 860;
        Height = 660;
        ShowInTaskbar = false;
        Padding = new Padding(16, 14, 16, 14);

        var btnRow = BuildButtonRow(out _refresh);
        _status = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 22,
            ForeColor = Color.FromArgb(160, 170, 190),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 4, 0, 0),
        };
        _body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(220, 225, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 13f),
            WordWrap = true,
            Text = string.IsNullOrEmpty(cached)
                ? "(loading…)"
                : cached,
        };
        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Text = $"{DisplayLabel(entry)} (v{entry.Version})"
                + (entry.ModWorkshopId > 0
                    ? $"  ·  ModWorkshop {entry.ModWorkshopId}"
                    : "  ·  no ModWorkshop link"),
        };

        // Reverse-add for Dock layout: bottom-most rows first.
        Controls.Add(btnRow);
        Controls.Add(_status);
        Controls.Add(_body);
        Controls.Add(header);

        // If we have nothing cached, kick off a fetch as soon as
        // the form is visible. Otherwise just show the cached text
        // and let the user click Refresh if they want fresh.
        if (string.IsNullOrEmpty(cached))
            Shown += async (_, _) => await FetchAsync();
    }

    private FlowLayoutPanel BuildButtonRow(out Button refresh)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            // 56 fits the bumped-font buttons without clipping
            // their top/bottom borders.
            Height = 56,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0),
        };
        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width = 110;
        close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        refresh = MainForm.ThemedButton("Refresh");
        refresh.Width = 100;
        refresh.AutoSize = false;
        refresh.Click += async (_, _) => await FetchAsync();
        row.Controls.Add(close);
        row.Controls.Add(refresh);
        AcceptButton = close;
        CancelButton = close;
        return row;
    }

    private async Task FetchAsync()
    {
        if (_entry.ModWorkshopId <= 0)
        {
            _status.Text = "No ModWorkshop ID linked — set one via right-click → Set ModWorkshop ID.";
            _body.Text = string.IsNullOrEmpty(_body.Text)
                ? "(no description available — mod isn't linked to ModWorkshop)"
                : _body.Text;
            return;
        }
        _refresh.Enabled = false;
        _status.Text = $"Fetching from /mods/{_entry.ModWorkshopId} …";
        try
        {
            var details = await _client.GetModDetailsAsync(_entry.ModWorkshopId);
            var desc = details.Description?.Trim() ?? "";
            _body.Text = string.IsNullOrEmpty(desc)
                ? "(ModWorkshop returned no description for this mod.)"
                : desc;
            _status.Text = $"Last fetched just now"
                + (string.IsNullOrEmpty(details.Author) ? "" : $" · author: {details.Author}");
            _onCached?.Invoke(desc);
        }
        catch (Exception ex)
        {
            _status.Text = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);
}
