// Markdown in the manager's theme: mod descriptions (VostokMods) and
// CHANGELOGs. A port of the WinForms MarkdownRenderer subset, grown to
// what VostokMods pages actually carry:
//
//   • # … ###### headings (and === / --- setext underlines)
//   • paragraphs (a single line break is a space; two trailing spaces,
//     a trailing backslash or <br> is a hard break)
//   • **bold**, __bold__, *italic*, _italic_, ***both***, ~~strike~~
//   • `inline code` and ``` / ~~~ fenced code blocks
//   • - * + bullet lists and 1. / 1) numbered lists, nested by indent,
//     with [ ] / [x] task boxes
//   • > block quotes (nested Markdown inside)
//   • --- *** ___ rules
//   • [links](https://…), <https://…> and bare URLs, opened in the
//     browser (http, https and mailto only)
//   • | pipe | tables |
//   • <details>/<summary> (the summary as a bold line, the body shown),
//     <br>; other HTML tags are dropped, images are dropped.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VostokModManager.Wpf.Controls;

/// <summary>A read-only FlowDocument viewer that renders its
/// <see cref="Markdown"/> text in the theme. Body text takes the
/// viewer's FontFamily / FontSize / Foreground. With
/// VerticalScrollBarVisibility="Disabled" it grows to its content and
/// hands the mouse wheel to the surrounding scroller.</summary>
public class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Rebuild()));

    /// <summary>The Markdown source to show.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>On a TextBlock: sets its inlines from one line of
    /// Markdown (`code`, **bold**, *italic*, links). For short texts
    /// such as titles that quote ids in backticks.</summary>
    public static readonly DependencyProperty InlineTextProperty = DependencyProperty.RegisterAttached(
        "InlineText", typeof(string), typeof(MarkdownView),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is not TextBlock tb) return;
            tb.Inlines.Clear();
            var text = e.NewValue as string;
            if (string.IsNullOrEmpty(text)) return;
            foreach (var inline in MarkdownDocument.ParseInlines(text, tb)) tb.Inlines.Add(inline);
        }));

    public static string? GetInlineText(DependencyObject d) => (string?)d.GetValue(InlineTextProperty);
    public static void SetInlineText(DependencyObject d, string? value) => d.SetValue(InlineTextProperty, value);

    public MarkdownView()
    {
        SetResourceReference(FontFamilyProperty, "Font.Lora");
        SetResourceReference(FontSizeProperty, "Size.Body");
        SetResourceReference(ForegroundProperty, "B.Text");
        SetResourceReference(SelectionBrushProperty, "B.Blue");
        Background = Brushes.Transparent;
        IsToolBarVisible = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // No Ctrl+wheel zoom: the text size belongs to the theme.
        MinZoom = 100;
        MaxZoom = 100;
        Zoom = 100;
        Focusable = false;
        Rebuild();
    }

    private void Rebuild()
    {
        FlowDocument doc;
        try { doc = MarkdownDocument.Build(Markdown ?? "", this); }
        catch
        {
            // Markdown this renderer can't place must never take the page
            // down: show the text as it is.
            doc = new FlowDocument(new Paragraph(new Run(Markdown ?? ""))) { PagePadding = new Thickness(0) };
        }
        doc.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        doc.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        doc.SetBinding(FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Document = doc;
    }

    /// <summary>When this viewer doesn't scroll itself, the wheel
    /// belongs to whatever scroller holds it.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) return;
        if (VisualTreeHelper.GetParent(this) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        });
    }
}

/// <summary>Builds a themed FlowDocument from Markdown.</summary>
public static class MarkdownDocument
{
    /// <summary>The document for `markdown`. Brushes and fonts are looked
    /// up from `scope` (else the application).</summary>
    public static FlowDocument Build(string markdown, FrameworkElement? scope = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
            IsOptimalParagraphEnabled = false,
        };
        var ctx = new Ctx(scope);
        foreach (var block in ParseBlocks(Lines(markdown), ctx)) doc.Blocks.Add(block);
        // The last block's bottom margin would only pad the scroller.
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return doc;
    }

    /// <summary>One line of Markdown as inlines (for a TextBlock).</summary>
    public static IEnumerable<Inline> ParseInlines(string text, FrameworkElement? scope = null)
        => Inlines(text, new Ctx(scope, codeFontSize: null));

    // ── context ───────────────────────────────────────────────────────

    private sealed class Ctx
    {
        private readonly FrameworkElement? _scope;
        public Ctx(FrameworkElement? scope, double? codeFontSize = 12.5)
        {
            _scope = scope;
            CodeFontSize = codeFontSize;
        }

        public double? CodeFontSize { get; }

        public object? Res(string key)
            => _scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);

        public Brush Brush(string key) => Res(key) as Brush ?? Brushes.Transparent;
        public FontFamily Mono => Res("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
    }

    private static List<string> Lines(string md)
        => (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n').ToList();

    // ── blocks ────────────────────────────────────────────────────────

    private static readonly Regex HeadingRx = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceRx = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^\s{0,3}>\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^(\s*)([-*+])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex SummaryRx = new(@"^\s*<summary>(.*?)</summary>\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlOnlyRx = new(@"^\s*(</?[a-zA-Z][^>]*>\s*)+$", RegexOptions.Compiled);
    private static readonly Regex TableSepRx = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex ImageOnlyRx = new(@"^\s*(!\[[^\]]*\]\([^)]*\)\s*)+$", RegexOptions.Compiled);

    private static List<Block> ParseBlocks(List<string> lines, Ctx ctx)
    {
        var blocks = new List<Block>();
        var para = new List<string>();
        ListBuilder? list = null;

        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(MakeParagraph(para, ctx));
            para.Clear();
        }
        void FlushList()
        {
            if (list == null) return;
            blocks.Add(list.Root);
            list = null;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var t = line.Trim();

            // Fenced code: everything up to the closing fence, verbatim.
            var fence = FenceRx.Match(line);
            if (fence.Success)
            {
                FlushPara(); FlushList();
                var code = new List<string>();
                var marker = fence.Groups[1].Value;
                for (i++; i < lines.Count && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(MakeCodeBlock(code, ctx));
                continue;
            }

            if (t.Length == 0)
            {
                FlushPara();
                // A blank line inside a list keeps the list going when the
                // next line is another item or an indented continuation.
                if (list != null)
                {
                    var next = i + 1 < lines.Count ? lines[i + 1] : "";
                    if (!(BulletRx.IsMatch(next) || NumberRx.IsMatch(next) || (next.StartsWith("  ") && next.Trim().Length > 0)))
                        FlushList();
                }
                continue;
            }

            // Setext headings: a paragraph line underlined with === or ---.
            if (para.Count > 0 && Regex.IsMatch(t, @"^(=+|-+)$"))
            {
                var text = string.Join(" ", para.Select(p => p.Trim()));
                para.Clear();
                blocks.Add(MakeHeading(t[0] == '=' ? 1 : 2, text, ctx));
                continue;
            }

            var h = HeadingRx.Match(line);
            if (h.Success)
            {
                FlushPara(); FlushList();
                blocks.Add(MakeHeading(h.Groups[1].Value.Length, h.Groups[2].Value, ctx));
                continue;
            }

            if (RuleRx.IsMatch(line))
            {
                FlushPara(); FlushList();
                blocks.Add(MakeRule(ctx));
                continue;
            }

            var sum = SummaryRx.Match(line);
            if (sum.Success)
            {
                FlushPara(); FlushList();
                blocks.Add(MakeHeading(5, sum.Groups[1].Value, ctx));
                continue;
            }
            // Layout-only HTML (<details>, <p align=…>, </div>) and lines that
            // are only images: nothing to show.
            if (HtmlOnlyRx.IsMatch(line) || ImageOnlyRx.IsMatch(line))
            {
                if (Regex.IsMatch(t, @"^<br\s*/?>$", RegexOptions.IgnoreCase)) { para.Add("<br>"); continue; }
                FlushPara();
                continue;
            }

            var q = QuoteRx.Match(line);
            if (q.Success && list == null)
            {
                FlushPara();
                var inner = new List<string>();
                for (; i < lines.Count; i++)
                {
                    var qm = QuoteRx.Match(lines[i]);
                    if (qm.Success) inner.Add(qm.Groups[1].Value);
                    else if (lines[i].Trim().Length > 0 && inner.Count > 0 && inner[^1].Trim().Length > 0) inner.Add(lines[i]); // lazy continuation
                    else { i--; break; }
                }
                blocks.Add(MakeQuote(inner, ctx));
                continue;
            }

            // Tables: a header row, a --- separator, then rows.
            if (t.Contains('|') && i + 1 < lines.Count && TableSepRx.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
            {
                FlushPara(); FlushList();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Count && lines[i].Trim().Length > 0 && lines[i].Contains('|'); i++)
                    rows.Add(lines[i]);
                i--;
                blocks.Add(MakeTable(rows, ctx));
                continue;
            }

            var b = BulletRx.Match(line);
            var n = b.Success ? Match.Empty : NumberRx.Match(line);
            // "---" was taken as a rule above; "* * *" too.
            if (b.Success || n.Success)
            {
                FlushPara();
                var m = b.Success ? b : n;
                // A numbered list right after a bullet one (or the other
                // way round) is a new list, not more of the old.
                if (list != null && list.SwitchesType(m.Groups[1].Value.Length, n.Success)) FlushList();
                list ??= new ListBuilder(ctx);
                list.Add(m.Groups[1].Value.Length, ordered: n.Success,
                    start: n.Success && int.TryParse(n.Groups[2].Value, out var s) ? s : 1,
                    text: m.Groups[3].Value);
                continue;
            }

            if (list != null)
            {
                // A continuation of the last item.
                list.Continue(t);
                continue;
            }

            para.Add(line);
        }
        FlushPara();
        FlushList();
        return blocks;
    }

    private static Paragraph MakeParagraph(List<string> lines, Ctx ctx)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var hard = raw.EndsWith("  ", StringComparison.Ordinal) || raw.TrimEnd().EndsWith('\\');
            var text = raw.Trim();
            if (text.EndsWith('\\')) text = text[..^1];
            if (text == "<br>") { p.Inlines.Add(new LineBreak()); continue; }
            foreach (var inline in Inlines(text, ctx)) p.Inlines.Add(inline);
            if (i < lines.Count - 1)
                p.Inlines.Add(hard ? new LineBreak() : new Run(" "));
        }
        return p;
    }

    private static Paragraph MakeHeading(int level, string text, Ctx ctx)
    {
        var (size, top) = level switch
        {
            1 => (24.0, 18.0),
            2 => (20.0, 16.0),
            3 => (17.0, 14.0),
            _ => (15.0, 10.0),
        };
        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = level <= 3 ? FontWeights.Medium : FontWeights.SemiBold,
            Margin = new Thickness(0, top, 0, 6),
            Foreground = ctx.Brush("B.Text"),
        };
        if (level <= 2)
        {
            // A hairline under the big headings, as on the mod pages.
            p.BorderBrush = ctx.Brush("B.HairlineSoft");
            p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.Padding = new Thickness(0, 0, 0, 4);
        }
        foreach (var inline in Inlines(text, ctx)) p.Inlines.Add(inline);
        return p;
    }

    private static Block MakeCodeBlock(List<string> code, Ctx ctx)
    {
        var p = new Paragraph
        {
            FontFamily = ctx.Mono,
            FontSize = 12.5,
            Foreground = ctx.Brush("B.Text2"),
            Background = ctx.Brush("B.Fill"),
            BorderBrush = ctx.Brush("B.Hairline"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        for (int i = 0; i < code.Count; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run(code[i]));
        }
        return p;
    }

    private static Block MakeRule(Ctx ctx)
        => new BlockUIContainer(new Border
        {
            Height = 1,
            Background = ctx.Brush("B.Hairline"),
            Margin = new Thickness(0, 6, 0, 6),
            SnapsToDevicePixels = true,
        })
        { Margin = new Thickness(0, 0, 0, 12) };

    private static Block MakeQuote(List<string> inner, Ctx ctx)
    {
        var s = new Section
        {
            BorderBrush = ctx.Brush("B.Edge"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = ctx.Brush("B.Text2"),
        };
        foreach (var block in ParseBlocks(inner, ctx)) s.Blocks.Add(block);
        if (s.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return s;
    }

    private static Block MakeTable(List<string> rows, Ctx ctx)
    {
        static List<string> Cells(string row)
        {
            var r = row.Trim();
            if (r.StartsWith('|')) r = r[1..];
            if (r.EndsWith('|')) r = r[..^1];
            return Regex.Split(r, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList();
        }
        var parsed = rows.Select(Cells).ToList();
        var cols = parsed.Max(r => r.Count);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = ctx.Brush("B.Hairline"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < parsed.Count; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var text = c < parsed[r].Count ? parsed[r][c] : "";
                var p = new Paragraph { Margin = new Thickness(0) };
                foreach (var inline in Inlines(text, ctx)) p.Inlines.Add(inline);
                if (r == 0) { p.FontWeight = FontWeights.SemiBold; p.FontSize = 13; }
                row.Cells.Add(new TableCell(p)
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderBrush = ctx.Brush(r == 0 ? "B.Hairline" : "B.HairlineSoft"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = r == 0 ? ctx.Brush("B.Fill") : Brushes.Transparent,
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>Nested lists by indentation: a deeper item opens a list
    /// inside the previous item; a shallower one closes back out.</summary>
    private sealed class ListBuilder
    {
        private readonly Ctx _ctx;
        private readonly Stack<(List List, int Indent, bool Ordered)> _stack = new();
        private Paragraph? _lastPara;
        public List Root { get; private set; } = null!;

        public ListBuilder(Ctx ctx) => _ctx = ctx;

        /// <summary>An item at the outer level of the other kind.</summary>
        public bool SwitchesType(int indent, bool ordered)
        {
            if (_stack.Count == 0) return false;
            var root = _stack.Last();   // Stack enumerates top first: Last() is the outermost
            return indent <= root.Indent && root.Ordered != ordered;
        }

        public void Add(int indent, bool ordered, int start, string text)
        {
            // The outermost list stays: an item less indented than the
            // first one still belongs to it.
            while (_stack.Count > 1 && _stack.Peek().Indent > indent)
                _stack.Pop();

            if (_stack.Count == 0 || indent > _stack.Peek().Indent + 1)
            {
                var list = NewList(ordered, start, nested: _stack.Count > 0);
                if (_stack.Count == 0) Root = list;
                else (_stack.Peek().List.ListItems.LastListItem ?? AddItem(_stack.Peek().List)).Blocks.Add(list);
                _stack.Push((list, indent, ordered));
            }

            var item = AddItem(_stack.Peek().List);
            _lastPara = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            if (task.Success)
            {
                _lastPara.Inlines.Add(new Run(task.Groups[1].Value == " " ? "☐  " : "☑  "));
                text = task.Groups[2].Value;
            }
            foreach (var inline in Inlines(text, _ctx)) _lastPara.Inlines.Add(inline);
            item.Blocks.Add(_lastPara);
        }

        public void Continue(string text)
        {
            if (_lastPara == null) return;
            _lastPara.Inlines.Add(new Run(" "));
            foreach (var inline in Inlines(text, _ctx)) _lastPara.Inlines.Add(inline);
        }

        private static ListItem AddItem(List list)
        {
            var item = new ListItem();
            list.ListItems.Add(item);
            return item;
        }

        private List NewList(bool ordered, int start, bool nested) => new()
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : nested ? TextMarkerStyle.Circle : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, start),
            Padding = new Thickness(26, 0, 0, 0),
            Margin = nested ? new Thickness(0, 3, 0, 0) : new Thickness(0, 0, 0, 12),
        };
    }

    // ── inlines ───────────────────────────────────────────────────────

    private static readonly Regex InlineRx = new(
          @"(?<code>(?<tick>`+)(?<codetext>.+?)\k<tick>)"
        + @"|\\(?<esc>[\\`*_{}\[\]()#+\-.!|>~<])"
        + @"|(?<img>!\[[^\]]*\]\([^)]*\))"
        + @"|\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)(?:\s+""[^""]*"")?\)"
        + @"|<(?<auto>(?:https?://|mailto:)[^>\s]+)>"
        + @"|(?<br><br\s*/?>)"
        + @"|(?<tag></?[a-zA-Z][^>]*>)"
        + @"|\*\*\*(?<bi>(?!\s).+?(?<!\s))\*\*\*"
        + @"|\*\*(?<b>(?!\s).+?(?<!\s))\*\*"
        + @"|(?<![\w_])__(?<b2>(?!\s).+?(?<!\s))__(?![\w_])"
        + @"|(?<![\w*])\*(?<i>(?![\s*]).+?(?<![\s*]))\*(?![\w*])"
        + @"|(?<![\w_])_(?<i2>(?![\s_]).+?(?<![\s_]))_(?![\w_])"
        + @"|~~(?<s>(?!\s).+?(?<!\s))~~"
        + @"|(?<url>https?://[^\s<>()\[\]]+[^\s<>()\[\].,;:!?'""])",
        RegexOptions.Compiled);

    private static IEnumerable<Inline> Inlines(string text, Ctx ctx)
    {
        var result = new List<Inline>();
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new Run(WebUtility.HtmlDecode(plain.ToString())));
            plain.Clear();
        }

        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) plain.Append(text, pos, m.Index - pos);
            pos = m.Index + m.Length;

            if (m.Groups["esc"].Success) { plain.Append(m.Groups["esc"].Value); continue; }
            if (m.Groups["img"].Success || m.Groups["tag"].Success) continue;
            FlushPlain();

            if (m.Groups["code"].Success)
            {
                var run = new Run(m.Groups["codetext"].Value.Trim())
                {
                    FontFamily = ctx.Mono,
                    Background = ctx.Brush("B.Selected"),
                    Foreground = ctx.Brush("B.Text"),
                };
                // In a document, a touch under the body size; on a
                // TextBlock (a title), the TextBlock's own size.
                if (ctx.CodeFontSize is { } size) run.FontSize = size;
                result.Add(run);
            }
            else if (m.Groups["ltext"].Success)
                result.Add(Link(m.Groups["lurl"].Value, Inlines(m.Groups["ltext"].Value, ctx), ctx));
            else if (m.Groups["auto"].Success)
                result.Add(Link(m.Groups["auto"].Value, new[] { new Run(m.Groups["auto"].Value) }, ctx));
            else if (m.Groups["url"].Success)
                result.Add(Link(m.Groups["url"].Value, new[] { new Run(m.Groups["url"].Value) }, ctx));
            else if (m.Groups["br"].Success)
                result.Add(new LineBreak());
            else if (m.Groups["bi"].Success)
                result.Add(Wrap(new Bold(), Inlines(m.Groups["bi"].Value, ctx), italic: true));
            else if (m.Groups["b"].Success || m.Groups["b2"].Success)
                result.Add(Wrap(new Bold(), Inlines(m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value, ctx)));
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
                result.Add(Wrap(new Italic(), Inlines(m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value, ctx)));
            else if (m.Groups["s"].Success)
            {
                var span = Wrap(new Span(), Inlines(m.Groups["s"].Value, ctx));
                span.TextDecorations = TextDecorations.Strikethrough;
                result.Add(span);
            }
        }
        if (pos < text.Length) plain.Append(text, pos, text.Length - pos);
        FlushPlain();
        return result;
    }

    private static Span Wrap(Span span, IEnumerable<Inline> inner, bool italic = false)
    {
        foreach (var i in inner) span.Inlines.Add(i);
        if (span is Bold) span.FontWeight = FontWeights.SemiBold;
        if (italic) span.FontStyle = FontStyles.Italic;
        return span;
    }

    private static Inline Link(string url, IEnumerable<Inline> content, Ctx ctx)
    {
        if (!IsSafeUrl(url))
        {
            // A relative or odd link: keep the text, drop the target.
            var span = new Span();
            foreach (var i in content) span.Inlines.Add(i);
            return span;
        }
        var link = new Hyperlink
        {
            Foreground = ctx.Brush("B.Blue"),
            Cursor = Cursors.Hand,
            ToolTip = url,
        };
        // Link text that is itself a URL ("[https://…](https://…)") was
        // auto-linked too; WPF can't nest a Hyperlink, so keep only its text.
        foreach (var i in content) link.Inlines.Add(Unlinked(i));
        link.Click += (_, _) => OpenUrl(url);
        return link;
    }

    /// <summary>`inline` with every Hyperlink in it (itself included)
    /// turned into a plain Span holding the same text.</summary>
    private static Inline Unlinked(Inline inline)
    {
        if (inline is not Span span) return inline;
        foreach (var child in span.Inlines.ToList())
        {
            var plain = Unlinked(child);
            if (ReferenceEquals(plain, child)) continue;
            span.Inlines.InsertBefore(child, plain);
            span.Inlines.Remove(child);
        }
        if (span is not Hyperlink) return span;
        var flat = new Span();
        foreach (var child in span.Inlines.ToList())
        {
            span.Inlines.Remove(child);
            flat.Inlines.Add(child);
        }
        return flat;
    }

    private static bool IsSafeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
