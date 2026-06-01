// Dark theme for ContextMenuStrip / ToolStripDropDownMenu — the
// WinForms popup menu defaults are stuck in the system Light scheme
// and look jarringly white next to our soviet-slate UI. Setting
// `ToolStripManager.Renderer` once at startup themes every menu
// in the app (title-row profile selector, mods grid right-click,
// profile detail grid right-click, anywhere else) with no per-menu
// wiring.
//
// Colours mirror the rest of the manager — same dark slate, same
// red accent for checked items, same muted separators.

namespace VostokModManager.Ui;

public static class DarkMenuTheme
{
    /// <summary>Installs the renderer globally. Call once during
    /// form construction (idempotent — last assignment wins, and
    /// re-installing the same renderer is a no-op).</summary>
    public static void Install()
    {
        ToolStripManager.Renderer = new DarkRenderer();
    }

    // ── Color table ──────────────────────────────────────────────

    private class DarkColors : ProfessionalColorTable
    {
        // Menu surface
        public override Color ToolStripDropDownBackground       => Color.FromArgb(36, 42, 54);
        public override Color MenuStripGradientBegin            => Color.FromArgb(36, 42, 54);
        public override Color MenuStripGradientEnd              => Color.FromArgb(36, 42, 54);
        public override Color MenuBorder                        => Color.FromArgb(70, 82, 100);
        public override Color MenuItemBorder                    => Color.FromArgb(85, 100, 120);

        // Left "image margin" strip (where checkmarks render)
        public override Color ImageMarginGradientBegin          => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginGradientMiddle         => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginGradientEnd            => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginRevealedGradientBegin  => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginRevealedGradientMiddle => Color.FromArgb(36, 42, 54);
        public override Color ImageMarginRevealedGradientEnd    => Color.FromArgb(36, 42, 54);

        // Hover / pressed item highlight
        public override Color MenuItemSelected                  => Color.FromArgb(60, 80, 110);
        public override Color MenuItemSelectedGradientBegin     => Color.FromArgb(60, 80, 110);
        public override Color MenuItemSelectedGradientEnd       => Color.FromArgb(60, 80, 110);
        public override Color MenuItemPressedGradientBegin      => Color.FromArgb(50, 65, 90);
        public override Color MenuItemPressedGradientMiddle     => Color.FromArgb(50, 65, 90);
        public override Color MenuItemPressedGradientEnd        => Color.FromArgb(50, 65, 90);
        public override Color ButtonSelectedHighlight           => Color.FromArgb(60, 80, 110);
        public override Color ButtonSelectedHighlightBorder     => Color.FromArgb(85, 100, 120);

        // Checked-item indicator (the box behind the ✓)
        public override Color CheckBackground                   => Color.FromArgb(45, 90, 55);
        public override Color CheckSelectedBackground           => Color.FromArgb(55, 110, 65);
        public override Color CheckPressedBackground            => Color.FromArgb(35, 70, 45);

        // Separators
        public override Color SeparatorDark                     => Color.FromArgb(60, 70, 90);
        public override Color SeparatorLight                    => Color.FromArgb(40, 46, 58);
    }

    // ── Renderer ────────────────────────────────────────────────

    private class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // The base ProfessionalRenderer uses the item's
            // ForeColor when it's been explicitly set, otherwise
            // falls back to SystemColors.MenuText (black) — which
            // is what produced the unreadable dark-on-dark text.
            // Forcing the text colour here keeps every menu item
            // light by default; per-item ForeColor overrides
            // (e.g. red for destructive items) still win because
            // they get set on the item itself.
            if (e.Item.Selected || e.Item.Pressed)
                e.TextColor = Color.FromArgb(255, 255, 255);
            else if (e.Item.Enabled)
                e.TextColor = e.Item.ForeColor.IsKnownColor
                    ? Color.FromArgb(220, 225, 235)
                    : e.Item.ForeColor; // honour explicit per-item ForeColor
            else
                e.TextColor = Color.FromArgb(120, 130, 150);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The default check glyph is a black-on-system check
            // mark and gets lost on our dark menu. Re-render as a
            // bright accent-red ✓ centered in the image margin.
            using var brush = new SolidBrush(Color.FromArgb(220, 80, 90));
            using var font  = new Font("Segoe UI Symbol", 11f, FontStyle.Bold);
            var bounds = e.ImageRectangle;
            TextRenderer.DrawText(
                e.Graphics, "✓", font, bounds,
                Color.FromArgb(220, 80, 90),
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix);
        }
    }
}
