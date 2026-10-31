using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VostokModManager.Services;

namespace VostokModManager.Wpf.Views;

/// <summary>The manager's one modal message box: a kind label, a title,
/// the message, optional extra content (a checklist, a text box) and a
/// row of buttons. Replaces WinForms' ThemedMessageBox.</summary>
public partial class MessageWindow : Window
{
    /// <summary>Index of the button that closed the window; -1 for Esc / close.</summary>
    public int Result { get; private set; } = -1;

    private const double MaxWidthForButtons = 900;

    public MessageWindow() => InitializeComponent();

    public static MessageWindow Create(Window? owner, string title, string message, PromptKind kind,
        IReadOnlyList<string> buttons, int primary = 0, int cancel = -1, UIElement? extra = null)
    {
        var w = new MessageWindow { Title = title };
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Kind.Text = kind switch
        {
            PromptKind.Error => "SOMETHING WENT WRONG",
            PromptKind.Warning => "CHECK THIS FIRST",
            PromptKind.Question => "YOUR CALL",
            _ => "NOTE",
        };
        if (kind == PromptKind.Error) w.Kind.Foreground = (System.Windows.Media.Brush)w.FindResource("B.SalmonText");
        if (kind == PromptKind.Warning) w.Kind.Foreground = (System.Windows.Media.Brush)w.FindResource("B.AmberText");
        w.TitleText.Text = title;
        w.Message.Text = message;
        w.Message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (extra != null) w.Extra.Content = extra;

        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 96,
                Style = (Style)w.FindResource(i == primary ? "Btn.Primary" : "Btn.Outline"),
                IsDefault = i == primary,
                IsCancel = i == cancel,
            };
            b.Click += (_, _) => { w.Result = index; w.DialogResult = true; };
            w.Buttons.Children.Add(b);
        }
        // Widen the window so the buttons sit on one row (the message wraps
        // to the new width); past the cap they wrap.
        w.Buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var needed = w.Buttons.DesiredSize.Width + 28 + 28 + 2;
        if (needed > w.Width) w.Width = Math.Min(Math.Ceiling(needed), MaxWidthForButtons);
        // A window without buttons is a progress view, shown modeless: Esc
        // must not close it (and DialogResult would throw there).
        if (buttons.Count > 0)
            w.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { w.Result = cancel; w.DialogResult = false; }
            };
        return w;
    }
}
