// Lightweight markdown → RichTextBox renderer. Handles the
// subset of CommonMark that mod CHANGELOGs / READMEs actually
// use:
//
//   • # / ## / ### headers (bolded, size-scaled)
//   • **bold** and *italic* / _italic_
//   • `inline code` (monospace) and ``` fenced code blocks ```
//   • - / * bullet lists (indented; nested lists kept simple)
//   • 1. numbered lists
//   • > blockquotes (left-bar + indented)
//   • --- horizontal rules
//   • Auto-links: bare URLs become coloured text (no click yet
//     — the RTB is read-only and we don't want to pull in a link
//     interceptor for this small win)
//
// What we deliberately DON'T handle: tables, images, footnotes,
// HTML pass-through. CHANGELOGs that need those are rare in this
// modding scene; the raw text fallback is fine.
//
// The renderer is one-shot: call Render(rtb, body) on a fresh
// RichTextBox. It clears existing content, walks the text line-
// by-line, and inserts styled runs.

using System.Text.RegularExpressions;

namespace VostokModManager.Ui;

public static class MarkdownRenderer
{
    private static readonly Color FgDefault    = Color.FromArgb(220, 225, 235);
    private static readonly Color FgHeading    = Color.FromArgb(255, 235, 200);
    private static readonly Color FgCode       = Color.FromArgb(170, 230, 200);
    private static readonly Color FgCodeBg     = Color.FromArgb(22, 28, 38);
    private static readonly Color FgQuote      = Color.FromArgb(170, 185, 210);
    private static readonly Color FgLink       = Color.FromArgb(120, 180, 255);
    private static readonly Color FgRule       = Color.FromArgb(80, 90, 110);

    private static readonly Font  BodyFont     = new("Segoe UI",  11f);
    private static readonly Font  BodyBold     = new("Segoe UI",  11f, FontStyle.Bold);
    private static readonly Font  BodyItalic   = new("Segoe UI",  11f, FontStyle.Italic);
    private static readonly Font  BodyBoldIt   = new("Segoe UI",  11f, FontStyle.Bold | FontStyle.Italic);
    private static readonly Font  CodeFont     = new("Consolas",  11f);
    private static readonly Font  CodeBlockFont= new("Consolas",  10.5f);
    private static readonly Font  H1Font       = new("Segoe UI",  18f, FontStyle.Bold);
    private static readonly Font  H2Font       = new("Segoe UI",  15f, FontStyle.Bold);
    private static readonly Font  H3Font       = new("Segoe UI",  13f, FontStyle.Bold);

    public static void Render(RichTextBox rtb, string body)
    {
        rtb.SuspendLayout();
        rtb.Clear();
        rtb.Font = BodyFont;
        rtb.SelectionColor = FgDefault;

        if (string.IsNullOrEmpty(body)) { rtb.ResumeLayout(); return; }
        var lines = body.Replace("\r\n", "\n").Split('\n');

        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Fenced code blocks ``` ... ```
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                AppendNewline(rtb);
                continue;
            }
            if (inFence)
            {
                AppendStyled(rtb, line, CodeBlockFont, FgCode, FgCodeBg);
                AppendNewline(rtb);
                continue;
            }

            // Horizontal rule (---, ***, ___)
            var t = line.Trim();
            if (Regex.IsMatch(t, @"^([-*_])\1{2,}$"))
            {
                AppendStyled(rtb, new string('─', 60), BodyFont, FgRule, null);
                AppendNewline(rtb);
                continue;
            }

            // Headings
            if (t.StartsWith("### "))
            { AppendStyled(rtb, t.Substring(4), H3Font, FgHeading, null); AppendNewline(rtb); continue; }
            if (t.StartsWith("## "))
            { AppendStyled(rtb, t.Substring(3), H2Font, FgHeading, null); AppendNewline(rtb); continue; }
            if (t.StartsWith("# "))
            { AppendStyled(rtb, t.Substring(2), H1Font, FgHeading, null); AppendNewline(rtb); continue; }

            // Blockquote
            if (t.StartsWith("> "))
            {
                AppendStyled(rtb, "  │  ", BodyFont, FgQuote, null);
                RenderInline(rtb, t.Substring(2), FgQuote);
                AppendNewline(rtb);
                continue;
            }

            // Bulleted list — supports nested indentation by
            // counting leading spaces in multiples of 2 or 4.
            var bulletMatch = Regex.Match(line, @"^(\s*)([-*+])\s+(.*)$");
            if (bulletMatch.Success)
            {
                var indent = bulletMatch.Groups[1].Value.Length;
                var depth  = Math.Min(indent / 2, 4);
                AppendStyled(rtb, new string(' ', depth * 4) + "• ", BodyFont, FgDefault, null);
                RenderInline(rtb, bulletMatch.Groups[3].Value, FgDefault);
                AppendNewline(rtb);
                continue;
            }

            // Numbered list
            var numMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.*)$");
            if (numMatch.Success)
            {
                var indent = numMatch.Groups[1].Value.Length;
                var depth  = Math.Min(indent / 2, 4);
                AppendStyled(rtb,
                    new string(' ', depth * 4) + numMatch.Groups[2].Value + ". ",
                    BodyFont, FgDefault, null);
                RenderInline(rtb, numMatch.Groups[3].Value, FgDefault);
                AppendNewline(rtb);
                continue;
            }

            // Default paragraph line — render inline formatting
            RenderInline(rtb, line, FgDefault);
            AppendNewline(rtb);
        }
        // Reset caret to the top so the user starts at the
        // newest entry (CHANGELOG convention is newest-first).
        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;
        rtb.ScrollToCaret();
        rtb.ResumeLayout();
    }

    /// <summary>Inline-formatting pass for a single "paragraph"
    /// line. Handles `code`, **bold**, *italic* / _italic_, and
    /// bare-URL auto-linking. Tokenises left-to-right and emits
    /// runs of the appropriate style. Nested formatting is
    /// shallow — bold-inside-italic etc. work, but pathological
    /// combinations may not match Pandoc's parser. CHANGELOGs
    /// don't usually contain such cases.</summary>
    private static void RenderInline(RichTextBox rtb, string text, Color baseColor)
    {
        // Single composite regex matching either ``code``, **bold**,
        // *italic*, _italic_, or a bare http(s) URL. The plain
        // text between matches is emitted as default-styled runs.
        var rx = new Regex(
            @"(`{1,2})(.+?)\1"           // group 1+2 = code span
          + @"|\*\*(.+?)\*\*"             // group 3 = bold
          + @"|\*(.+?)\*"                 // group 4 = italic *
          + @"|_(.+?)_"                   // group 5 = italic _
          + @"|(https?://\S+)",          // group 6 = bare URL
            RegexOptions.Compiled);

        int pos = 0;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Index > pos)
                AppendStyled(rtb, text.Substring(pos, m.Index - pos),
                    BodyFont, baseColor, null);
            if (m.Groups[1].Success)
                AppendStyled(rtb, m.Groups[2].Value, CodeFont, FgCode, FgCodeBg);
            else if (m.Groups[3].Success)
                AppendStyled(rtb, m.Groups[3].Value, BodyBold, baseColor, null);
            else if (m.Groups[4].Success)
                AppendStyled(rtb, m.Groups[4].Value, BodyItalic, baseColor, null);
            else if (m.Groups[5].Success)
                AppendStyled(rtb, m.Groups[5].Value, BodyItalic, baseColor, null);
            else if (m.Groups[6].Success)
                AppendStyled(rtb, m.Groups[6].Value, BodyFont, FgLink, null);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            AppendStyled(rtb, text.Substring(pos), BodyFont, baseColor, null);
    }

    private static void AppendStyled(RichTextBox rtb, string text,
        Font font, Color fg, Color? bg)
    {
        if (string.IsNullOrEmpty(text)) return;
        rtb.SelectionStart  = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionFont   = font;
        rtb.SelectionColor  = fg;
        rtb.SelectionBackColor = bg ?? rtb.BackColor;
        rtb.AppendText(text);
    }

    private static void AppendNewline(RichTextBox rtb)
    {
        rtb.SelectionStart  = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionBackColor = rtb.BackColor;
        rtb.AppendText("\n");
    }
}
