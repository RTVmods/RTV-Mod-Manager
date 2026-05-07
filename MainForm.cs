// Top-level window. v0.3 skeleton — just enough to launch + show that
// the build pipeline works. Mod listing, conflict detection, ModWorkshop
// updates, AI resolver, etc. are ported in subsequent commits.

using VostokModManager.Domain;

namespace VostokModManager;

public class MainForm : Form
{
    private readonly Settings _settings;

    public MainForm()
    {
        _settings = Settings.Load();
        InitializeWindow();
        BuildLayout();
    }

    private void InitializeWindow()
    {
        Text = "Vostok Mod Manager";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(26, 30, 40);
        ForeColor = Color.FromArgb(220, 225, 235);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Vostok Mod Manager",
            Font = new Font(Font.FontFamily, 18f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        var status = new Label
        {
            Text = "Skeleton build — porting from GDScript. "
                + "Settings file: " + Settings.Path,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            ForeColor = Color.FromArgb(180, 190, 210),
        };
        root.Controls.Add(status, 0, 1);
    }
}
