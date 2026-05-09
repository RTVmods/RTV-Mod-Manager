// Modal that shows a mod's ModWorkshop description text. Read-only
// in v1 — Refresh re-fetches /mods/<id> on demand. The caller
// decides whether to pass cached text (instant open, no spinner)
// or empty (forces an immediate fetch).
//
// The body is a RichTextBox with a tiny markdown renderer so the
// `## v1.3` headers, `**bold**` runs, and `- bullet` lists that
// mod authors actually use on ModWorkshop come through styled
// instead of as raw markup characters.

using System.Text.RegularExpressions;
using VostokModManager.Api;
using VostokModManager.Domain;

namespace VostokModManager.Ui;

public class DescriptionDialog : Form
{
    private readonly ModEntry _entry;
    private readonly ModWorkshopClient _client;
    private readonly Action<string>? _onCached;
    private readonly RichTextBox _body;
    private readonly Label _status;
    private readonly Button _refresh;

    private static readonly Color FgDefault = Color.FromArgb(220, 225, 235);
    private static readonly Color FgHeader = Color.FromArgb(255, 220, 120);
    private static readonly Color FgMuted = Color.FromArgb(160, 170, 190);

    private static readonly Font FontBody = new("Segoe UI", 13f);
    private static readonly Font FontBodyBold = new("Segoe UI", 13f, FontStyle.Bold);
    private static readonly Font FontBodyItalic = new("Segoe UI", 13f, FontStyle.Italic);
    private static readonly Font FontBodyStrike = new("Segoe UI", 13f, FontStyle.Strikeout);
    private static readonly Font FontH1 = new("Segoe UI", 17f, FontStyle.Bold);
    private static readonly Font FontH2 = new("Segoe UI", 15f, FontStyle.Bold);
    private static readonly Font FontH3 = new("Segoe UI", 14f, FontStyle.Bold);

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
        ForeColor = FgDefault;
        Font = FontBody;
        FormBorderStyle = FormBorderStyle.Sizable;
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
            ForeColor = FgMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 4, 0, 0),
        };
        _body = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = FgDefault,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontBody,
            DetectUrls = true,
            WordWrap = true,
        };
        // Open links via shell — RichTextBox raises LinkClicked but
        // doesn't navigate by itself.
        _body.LinkClicked += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.LinkText)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.LinkText,
                        UseShellExecute = true,
                    });
            }
            catch { /* best-effort link open */ }
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

        if (string.IsNullOrEmpty(cached))
        {
            RenderRaw("(loading…)");
            Shown += async (_, _) => await FetchAsync();
        }
        else
        {
            RenderMarkdown(cached);
        }
    }

    private FlowLayoutPanel BuildButtonRow(out Button refresh)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0),
        };
        var close = MainForm.ThemedButton("Close (Esc)");
        close.Width = 130;
        close.Height = 40;
        close.AutoSize = false;
        close.DialogResult = DialogResult.OK;
        close.Click += (_, _) => Close();
        refresh = MainForm.ThemedButton("Refresh");
        refresh.Width = 110;
        refresh.Height = 40;
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
            RenderRaw("(no description available — mod isn't linked to ModWorkshop)");
            return;
        }
        _refresh.Enabled = false;
        _status.Text = $"Fetching from /mods/{_entry.ModWorkshopId} …";
        try
        {
            var details = await _client.GetModDetailsAsync(_entry.ModWorkshopId);
            var desc = details.Description?.Trim() ?? "";
            if (string.IsNullOrEmpty(desc))
                RenderRaw("(ModWorkshop returned no description for this mod.)");
            else
                RenderMarkdown(desc);
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

    private void RenderRaw(string text)
    {
        _body.Clear();
        _body.SelectionFont = FontBody;
        _body.SelectionColor = FgMuted;
        _body.AppendText(text);
    }

    /// <summary>Tiny markdown renderer for the subset ModWorkshop
    /// authors actually use:
    ///   - `# / ## / ###` headers (different sizes, accent color)
    ///   - `**bold**` runs
    ///   - `*italic*` runs (single-asterisk pairs only)
    ///   - `~~strikethrough~~` runs
    ///   - `- ` / `* ` bullet lines (rendered with •)
    ///   - `---` horizontal rule
    /// Everything else passes through as plain body text. Inline
    /// formatting markers are stripped after the run is styled.</summary>
    private void RenderMarkdown(string text)
    {
        _body.Clear();
        _body.SuspendLayout();
        try
        {
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine;
                Font lineFont = FontBody;
                Color lineColor = FgDefault;

                if (line.StartsWith("### "))
                {
                    line = line.Substring(4);
                    lineFont = FontH3;
                    lineColor = FgHeader;
                }
                else if (line.StartsWith("## "))
                {
                    line = line.Substring(3);
                    lineFont = FontH2;
                    lineColor = FgHeader;
                }
                else if (line.StartsWith("# "))
                {
                    line = line.Substring(2);
                    lineFont = FontH1;
                    lineColor = FgHeader;
                }
                else if (line.Trim() == "---" || line.Trim() == "***")
                {
                    AppendStyled("──────────────────────────────",
                        FontBody, FgMuted);
                    _body.AppendText("\n");
                    continue;
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    AppendStyled("  •  ", FontBodyBold, FgDefault);
                    AppendInline(line.Substring(2));
                    _body.AppendText("\n");
                    continue;
                }

                if (lineFont != FontBody)
                {
                    // Header line: pad with a blank line above for
                    // visual breathing room (unless we're at the top).
                    if (_body.TextLength > 0) _body.AppendText("\n");
                    AppendStyled(line, lineFont, lineColor);
                    _body.AppendText("\n");
                    continue;
                }

                AppendInline(line);
                _body.AppendText("\n");
            }
            // Scroll back to the top so the user starts reading at
            // the start, not wherever the caret landed during append.
            _body.SelectionStart = 0;
            _body.ScrollToCaret();
        }
        finally
        {
            _body.ResumeLayout();
        }
    }

    private static readonly Regex _inlineRe = new(
        @"(\*\*([^*]+?)\*\*)|(~~([^~]+?)~~)|(\*([^*]+?)\*)",
        RegexOptions.Compiled);

    /// <summary>Appends a single line with inline **bold**, *italic*,
    /// ~~strike~~ markers translated to RTF formatting. Everything
    /// outside the markers is plain body text.</summary>
    private void AppendInline(string line)
    {
        var pos = 0;
        foreach (Match m in _inlineRe.Matches(line))
        {
            if (m.Index > pos)
                AppendStyled(line.Substring(pos, m.Index - pos), FontBody, FgDefault);
            if (m.Groups[1].Success)
                AppendStyled(m.Groups[2].Value, FontBodyBold, FgDefault);
            else if (m.Groups[3].Success)
                AppendStyled(m.Groups[4].Value, FontBodyStrike, FgMuted);
            else if (m.Groups[5].Success)
                AppendStyled(m.Groups[6].Value, FontBodyItalic, FgDefault);
            pos = m.Index + m.Length;
        }
        if (pos < line.Length)
            AppendStyled(line.Substring(pos), FontBody, FgDefault);
    }

    private void AppendStyled(string text, Font font, Color color)
    {
        var start = _body.TextLength;
        _body.AppendText(text);
        _body.Select(start, text.Length);
        _body.SelectionFont = font;
        _body.SelectionColor = color;
        // Reset selection to end so subsequent AppendText doesn't
        // overwrite the selection's caret position.
        _body.Select(_body.TextLength, 0);
    }

    private static string DisplayLabel(ModEntry e)
        => !string.IsNullOrEmpty(e.DisplayName)
            ? e.DisplayName
            : !string.IsNullOrEmpty(e.ModId)
                ? e.ModId
                : Path.GetFileName(e.Path);
}
